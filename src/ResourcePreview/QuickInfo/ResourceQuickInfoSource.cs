using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Threading;
using ResourcePreview.Diagnostics;
using ResourcePreview.Localization;
using ResourcePreview.Navigation;
using ResourcePreview.Preview;
using ResourcePreview.Resources;

namespace ResourcePreview.QuickInfo
{
    /// <summary>
    /// Builds the Quick Info item for a resource reference under the mouse in a .xaml file.
    /// Lookup and XAML building run on a background thread; <see cref="ResourcePreviewElementFactory"/>
    /// renders <see cref="ResourcePreviewData"/> later on the UI thread.
    /// </summary>
    internal sealed class ResourceQuickInfoSource : IAsyncQuickInfoSource
    {
        private readonly ITextBuffer _textBuffer;
        private readonly ITextDocument _document;
        private readonly DefinitionNavigator _navigator;
        private readonly IServiceProvider _serviceProvider;
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private int _lastIndexedVersion = -1;
        private string _lastIndexedFile;

        public ResourceQuickInfoSource(
            ITextBuffer textBuffer,
            ITextDocument document,
            DefinitionNavigator navigator,
            IServiceProvider serviceProvider,
            JoinableTaskFactory joinableTaskFactory)
        {
            _textBuffer = textBuffer;
            _document = document;
            _navigator = navigator;
            _serviceProvider = serviceProvider;
            _joinableTaskFactory = joinableTaskFactory;
        }

        public async Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            SnapshotPoint? triggerPoint = session.GetTriggerPoint(_textBuffer.CurrentSnapshot);
            if (!triggerPoint.HasValue)
            {
                return null;
            }

            // Load the package on first hover so the UI language is set. May switch to the UI thread briefly.
            await ResourcePreviewPackage.EnsureLoadedAsync(_joinableTaskFactory, _serviceProvider, cancellationToken);

            // Switch to a background thread before reading files.
            await TaskScheduler.Default;

            try
            {
                return CreateItem(session, triggerPoint.Value, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // the mouse moved away and VS cancelled the session
            }
            catch (Exception ex)
            {
                // An unhandled exception makes VS show a yellow info bar, so report it in the tooltip instead.
                return CreateTextItem(triggerPoint.Value, $"ResourcePreview: {ex.Message}");
            }
        }

        private QuickInfoItem CreateItem(IAsyncQuickInfoSession session, SnapshotPoint triggerPoint, CancellationToken cancellationToken)
        {
            ITextSnapshot snapshot = triggerPoint.Snapshot;
            ITextSnapshotLine line = triggerPoint.GetContainingLine();
            int positionInLine = triggerPoint.Position - line.Start.Position;

            if (!ResourceReferenceFinder.TryFind(line.GetText(), positionInLine, out ResourceReference reference))
            {
                return null; // no resource here, other Quick Info providers still run
            }

            // VS closes the tooltip when the mouse leaves this span.
            ITrackingSpan applicableSpan = snapshot.CreateTrackingSpan(
                line.Start.Position + reference.Start, reference.Length, SpanTrackingMode.EdgeInclusive);

            string filePath = _document.FilePath;
            ResourceIndex index = ResourceIndex.ForFile(filePath);
            UpdateLiveText(index, filePath, snapshot);

            // Will WPF find this key at runtime? Null for an x:Key definition or when all is well.
            string problem = DescribeProblem(index, filePath, line, reference, cancellationToken);

            // Sorted in WPF lookup order: the first one is the definition WPF uses here.
            IReadOnlyList<ResourceDefinition> definitions = index.Resolve(reference.Key, filePath, cancellationToken);
            if (definitions.Count == 0)
            {
                var notFound = new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, reference.Key),
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, "  " + Localizer.Format(Strings.NotFoundUnderFormat, index.RootDirectory)));
                return new QuickInfoItem(applicableSpan, problem == null
                    ? (object)notFound
                    : new ContainerElement(ContainerElementStyle.Stacked, notFound, CreateComment(problem)));
            }

            ResourceEntry target = definitions[0].Entry;
            var header = new ClassifiedTextElement(
                new ClassifiedTextRun(PredefinedClassificationTypeNames.Keyword, target.ElementTypeName + " "),
                new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, target.Key));

            // The navigate action is created here on the background thread and runs on the UI thread when clicked.
#pragma warning disable VSTHRD010 // See CreateNavigateAction.
            ResourcePreviewData preview = ResourcePreviewData.Create(index, target, CreateNavigateAction(session, target), cancellationToken);
#pragma warning restore VSTHRD010

            var parts = new List<object> { header };
            if (problem != null)
            {
                parts.Add(CreateComment(problem));
            }

            parts.Add(preview);
            parts.Add(CreateLocationLink(session, index, definitions[0]));

            // Say which definition wins when it matters: another one is overridden, or this file is never loaded.
            if (definitions[0].IsInUnmergedFile)
            {
                parts.Add(CreateComment(Localizer.Format(Strings.NotMergedFileFormat, Path.GetFileName(target.FilePath))));
            }
            else if (definitions[0].Status == DefinitionStatus.Used && definitions.Any(d => d.Status == DefinitionStatus.Overridden))
            {
                parts.Add(CreateComment(Localizer.Get(Strings.UsedByWpf)));
            }

            // Number of other definitions listed as links, set in the options page (0 = none).
            int maxListed = Options.SettingsService.Current.MaxListedDefinitions;
            if (definitions.Count > 1 && maxListed > 0)
            {
                parts.Add(CreateComment(Localizer.Get(Strings.AlsoDefinedIn)));

                for (int i = 1; i < definitions.Count && i <= maxListed; i++)
                {
                    parts.Add(CreateLocationLink(session, index, definitions[i]));
                }

                if (definitions.Count - 1 > maxListed)
                {
                    parts.Add(CreateComment(Localizer.Format(Strings.AndMoreFormat, definitions.Count - 1 - maxListed)));
                }
            }

            return new QuickInfoItem(applicableSpan, new ContainerElement(ContainerElementStyle.Stacked, parts));
        }

        /// <summary>
        /// The Error List text for this reference if WPF will not resolve it, otherwise null.
        /// Only for {StaticResource X} and {DynamicResource X}, not for an x:Key.
        /// </summary>
        private static string DescribeProblem(ResourceIndex index, string filePath, ITextSnapshotLine line, ResourceReference reference, CancellationToken cancellationToken)
        {
            string text = line.GetText().Substring(reference.Start, reference.Length);
            bool isStatic = text.Contains("StaticResource");
            if (!Options.SettingsService.Current.CheckReferences || !isStatic && !text.Contains("DynamicResource"))
            {
                return null;
            }

            var use = new ResourceUse(reference.Key, line.LineNumber + 1, reference.Start + 1, isStatic);
            ReferenceProblem problem = ReferenceChecker.CheckOne(index, filePath, use, cancellationToken);
            return problem == null ? null : ProblemText.Describe(index, problem);
        }

        /// <summary>
        /// Creates the click action. NavigateFromQuickInfo is safe from any thread because it switches
        /// to the UI thread itself. VSTHRD010 reports a false positive on callers, so it is suppressed
        /// only at the two call sites.
        /// </summary>
        private Action CreateNavigateAction(IAsyncQuickInfoSession session, ResourceEntry entry)
        {
            return () => _navigator.NavigateFromQuickInfo(session, entry);
        }

        /// <summary>
        /// A clickable location line. VS renders a ClassifiedTextRun with a navigation action as a hyperlink.
        /// For the other definitions a note follows: "overridden by Styles.xaml" or "not in any MergedDictionaries".
        /// </summary>
        private ClassifiedTextElement CreateLocationLink(IAsyncQuickInfoSession session, ResourceIndex index, ResourceDefinition definition)
        {
            ResourceEntry entry = definition.Entry;
            string text = Localizer.Format(Strings.LocationFormat, MakeRelative(index.RootDirectory, entry.FilePath), entry.LineNumber);
            var runs = new List<ClassifiedTextRun>
            {
                new ClassifiedTextRun(
                    PredefinedClassificationTypeNames.Comment,
                    text,
#pragma warning disable VSTHRD010 // See CreateNavigateAction.
                    CreateNavigateAction(session, entry),
#pragma warning restore VSTHRD010
                    tooltip: entry.FilePath,
                    style: ClassifiedTextRunStyle.Underline),
            };

            string note = definition.Status == DefinitionStatus.Overridden
                ? Localizer.Format(Strings.OverriddenByFormat, Path.GetFileName(definition.OverriddenBy.FilePath))
                : definition.Status != DefinitionStatus.Used && definition.IsInUnmergedFile
                    ? Localizer.Get(Strings.NotMerged)
                    : null;
            if (note != null)
            {
                runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, "  \u00B7 " + note));
            }

            return new ClassifiedTextElement(runs);
        }

        private static ClassifiedTextElement CreateComment(string text)
        {
            return new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, text));
        }

        /// <summary>
        /// Passes the editor text to the index so unsaved changes are included, but only when the buffer changed.
        /// </summary>
        private void UpdateLiveText(ResourceIndex index, string filePath, ITextSnapshot snapshot)
        {
            int version = snapshot.Version.VersionNumber;
            if (version == _lastIndexedVersion && string.Equals(filePath, _lastIndexedFile, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            index.SetLiveText(filePath, snapshot.GetText());
            _lastIndexedVersion = version;
            _lastIndexedFile = filePath;
        }

        private static QuickInfoItem CreateTextItem(SnapshotPoint point, string text)
        {
            ITrackingSpan span = point.Snapshot.CreateTrackingSpan(point.Position, 0, SpanTrackingMode.EdgeInclusive);
            return new QuickInfoItem(span, new ClassifiedTextElement(
                new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, text)));
        }

        private static string MakeRelative(string root, string path)
        {
            string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? root : root + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : path;
        }

        /// <summary>Called by VS when the buffer closes. Removes the live text from the index.</summary>
        public void Dispose()
        {
            if (_lastIndexedFile != null)
            {
                ResourceIndex.ForFile(_lastIndexedFile).ClearLiveText(_lastIndexedFile);
            }

            // Some VS versions reuse the source after Dispose. Resetting forces the next hover to set the live text again.
            _lastIndexedVersion = -1;
            _lastIndexedFile = null;
        }
    }
}
