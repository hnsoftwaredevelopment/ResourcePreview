using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Threading;
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

            IReadOnlyList<ResourceEntry> entries = index.Find(reference.Key, filePath, cancellationToken);
            if (entries.Count == 0)
            {
                return new QuickInfoItem(applicableSpan, new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, reference.Key),
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, "  " + Localizer.Format(Strings.NotFoundUnderFormat, index.RootDirectory))));
            }

            ResourceEntry target = entries[0];
            // Style and template info come from the XML. The XAML is always built for the image or live sample.
            StyleInfo style = StyleInfoBuilder.IsStyle(target) ? StyleInfoBuilder.Build(index, target, cancellationToken) : null;
            TemplateTree templateTree = TemplateTreeBuilder.IsTemplate(target) ? TemplateTreeBuilder.Build(target.Element) : null;
            PreviewXaml previewXaml = PreviewXamlBuilder.Build(index, target, cancellationToken);

            var header = new ClassifiedTextElement(
                new ClassifiedTextRun(PredefinedClassificationTypeNames.Keyword, target.ElementTypeName + " "),
                new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, target.Key));

            // The navigate action is created here on the background thread and runs on the UI thread when clicked.
            var preview = new ResourcePreviewData(
                target.Key, target.ElementTypeName, previewXaml.Xaml, previewXaml.MissingKeys,
#pragma warning disable VSTHRD010 // See CreateNavigateAction.
                CreateNavigateAction(session, target),
                style,
                templateTree,
                previewXaml.UnloadableType);
#pragma warning restore VSTHRD010

            var parts = new List<object> { header, preview, CreateLocationLink(session, index, target) };

            // Number of other definitions listed as links, set in the options page (0 = none).
            int maxListed = Options.SettingsService.Current.MaxListedDefinitions;
            if (entries.Count > 1 && maxListed > 0)
            {
                parts.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, Localizer.Get(Strings.AlsoDefinedIn))));

                for (int i = 1; i < entries.Count && i <= maxListed; i++)
                {
                    parts.Add(CreateLocationLink(session, index, entries[i]));
                }

                if (entries.Count - 1 > maxListed)
                {
                    parts.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, Localizer.Format(Strings.AndMoreFormat, entries.Count - 1 - maxListed))));
                }
            }

            return new QuickInfoItem(applicableSpan, new ContainerElement(ContainerElementStyle.Stacked, parts));
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
        /// </summary>
        private ClassifiedTextElement CreateLocationLink(IAsyncQuickInfoSession session, ResourceIndex index, ResourceEntry entry)
        {
            string text = Localizer.Format(Strings.LocationFormat, MakeRelative(index.RootDirectory, entry.FilePath), entry.LineNumber);
            return new ClassifiedTextElement(
                new ClassifiedTextRun(
                    PredefinedClassificationTypeNames.Comment,
                    text,
#pragma warning disable VSTHRD010 // See CreateNavigateAction.
                    CreateNavigateAction(session, entry),
#pragma warning restore VSTHRD010
                    tooltip: entry.FilePath,
                    style: ClassifiedTextRunStyle.Underline));
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
