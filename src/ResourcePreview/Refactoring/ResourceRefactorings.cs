using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;
using ResourcePreview.Localization;
using ResourcePreview.Navigation;
using ResourcePreview.Resources;
using Task = System.Threading.Tasks.Task;

namespace ResourcePreview.Refactoring
{
    /// <summary>
    /// The three refactorings, shared by the light bulb (Ctrl+.) and the context menu:
    /// - a value to a resource:      Background="#FF2D7D9A"          → {StaticResource Key}
    /// - attributes to a new Style:  Fill="White" Width="16" ...      → Style="{StaticResource Key}"
    /// - attributes to the existing Style the element already has.
    /// The text work is in <see cref="ExtractPlanner"/> and <see cref="StylePlanner"/>; this class asks,
    /// and applies the changes through <see cref="BufferEditor"/>.
    /// </summary>
    internal sealed class ResourceRefactorings
    {
        // The last file a resource went to, so the dialog starts there next time.
        private static string _lastTarget;

        private readonly IServiceProvider _serviceProvider;
        private readonly IVsEditorAdaptersFactoryService _editorAdapters;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        public ResourceRefactorings(IServiceProvider serviceProvider, IVsEditorAdaptersFactoryService editorAdapters, JoinableTaskFactory joinableTaskFactory)
        {
            _serviceProvider = serviceProvider;
            _editorAdapters = editorAdapters;
            _joinableTaskFactory = joinableTaskFactory;
        }

        public void ExtractValue(string filePath, ITextSnapshot snapshot, ExtractCandidate candidate) =>
            Run(() => ExtractAsync(
                filePath,
                snapshot,
                Localizer.Get(Strings.ExtractDialogTitle),
                ExtractPlanner.SuggestKey(candidate),
                key => ExtractPlanner.ResourceText(candidate, key),
                (text, key, beforeOffset) => ExtractPlanner.AddResource(text, candidate, key, beforeOffset),
                key => new[] { ExtractPlanner.ReplaceValue(candidate, key) },
                candidate.ValueStart,
                (key, file) => Localizer.Format(Strings.ExtractDoneFormat, key, file)));

        public void ExtractStyle(string filePath, ITextSnapshot snapshot, AttributeSelection selection) =>
            Run(() => ExtractAsync(
                filePath,
                snapshot,
                Localizer.Get(Strings.ExtractStyleDialogTitle),
                StylePlanner.SuggestKey(selection),
                key => string.Join(Environment.NewLine, StylePlanner.StyleLines(selection, key)),
                (text, key, beforeOffset) => ExtractPlanner.AddResource(text, _ => StylePlanner.StyleLines(selection, key), false, beforeOffset),
                key => StylePlanner.PointElementAtStyle(selection, key),
                selection.TagStart,
                (key, file) => Localizer.Format(Strings.ExtractStyleDoneFormat, selection.Selected.Count, key, file)));

        public void AddToStyle(string filePath, ITextSnapshot snapshot, AttributeSelection selection) =>
            Run(() => AddToStyleAsync(filePath, snapshot, selection));

        public void FindMatching(string filePath, ITextSnapshot snapshot, AttributeSelection selection) =>
            Run(() => FindMatchingAsync(filePath, snapshot, selection));

        private void Run(Func<Task> action)
        {
            _joinableTaskFactory.RunAsync(action).FileAndForget("ResourcePreview/Refactoring");
        }

        /// <summary>Shared by the value and the style: ask for key and file, add the resource, point the element at it.</summary>
        private async Task ExtractAsync(
            string filePath,
            ITextSnapshot snapshot,
            string title,
            string suggestedKey,
            Func<string, string> preview,
            Func<string, string, int?, IReadOnlyList<TextChange>> addResource,
            Func<string, IReadOnlyList<TextChange>> changeElement,
            int usePosition,
            Func<string, string, string> doneMessage)
        {
            // The first lookup in a solution reads every .xaml file. Keep that off the UI thread.
            await TaskScheduler.Default;
            ResourceIndex index = ResourceIndex.ForFile(filePath);
            index.SetLiveText(filePath, snapshot.GetText());
            IReadOnlyList<string> targets = ExtractPlanner.Targets(index, filePath, CancellationToken.None);

            await _joinableTaskFactory.SwitchToMainThreadAsync();

            if (targets.Count == 0)
            {
                ShowStatus(Localizer.Get(Strings.ExtractNoTarget));
                return;
            }

            var items = targets.Select(t => new KeyValuePair<string, string>(t, ReferenceChecker.Relative(index, t))).ToList();
            var dialog = new ExtractResourceDialog(title, suggestedKey, items, _lastTarget, key => Validate(index, key), preview);
            if (dialog.ShowModal() != true || dialog.Target == null || !IsUnchanged(snapshot))
            {
                return;
            }

            string target = dialog.Target;
            string newKey = dialog.Key;
            _lastTarget = target;
            bool sameFile = string.Equals(target, filePath, StringComparison.OrdinalIgnoreCase);
            var editor = new BufferEditor(_serviceProvider, _editorAdapters);

            // In the same file the resource goes above its use, and both changes are one edit.
            int added = editor.Apply(target, targetText =>
            {
                List<TextChange> changes = addResource(targetText, newKey, sameFile ? usePosition : (int?)null).ToList();
                if (sameFile && changes.Count > 0)
                {
                    changes.AddRange(changeElement(newKey));
                }

                return changes;
            });

            if (added == 0)
            {
                ShowStatus(Localizer.Get(Strings.ExtractNoTarget));
                return;
            }

            if (!sameFile)
            {
                editor.Apply(filePath, _ => changeElement(newKey));
            }

            ShowStatus(doneMessage(newKey, Path.GetFileName(target)));
        }

        private async Task AddToStyleAsync(string filePath, ITextSnapshot snapshot, AttributeSelection selection)
        {
            await TaskScheduler.Default;
            ResourceIndex index = ResourceIndex.ForFile(filePath);
            index.SetLiveText(filePath, snapshot.GetText());

            // The style WPF would use here, the same one the tooltip shows.
            ResourceEntry style = index.Resolve(selection.StyleKey, filePath, CancellationToken.None)
                .Where(d => d.Status == DefinitionStatus.Used)
                .Select(d => d.Entry)
                .FirstOrDefault(e => e.ElementTypeName == "Style");

            await _joinableTaskFactory.SwitchToMainThreadAsync();

            if (style == null)
            {
                ShowStatus(Localizer.Format(Strings.AddToStyleNotFoundFormat, selection.StyleKey));
                return;
            }

            if (!IsUnchanged(snapshot))
            {
                return;
            }

            bool sameFile = string.Equals(style.FilePath, filePath, StringComparison.OrdinalIgnoreCase);
            var editor = new BufferEditor(_serviceProvider, _editorAdapters);
            int replaced = 0;
            int changed = editor.Apply(style.FilePath, text =>
            {
                List<TextChange> changes = StylePlanner.AddSetters(text, style.Key, style.LineNumber, selection.Selected, out replaced).ToList();
                if (sameFile && changes.Count > 0)
                {
                    changes.AddRange(StylePlanner.RemoveSelected(selection));
                }

                return changes;
            });

            if (changed == 0)
            {
                ShowStatus(Localizer.Format(Strings.AddToStyleNotFoundFormat, selection.StyleKey));
                return;
            }

            if (!sameFile)
            {
                editor.Apply(filePath, _ => StylePlanner.RemoveSelected(selection));
            }

            ShowStatus(Localizer.Format(Strings.AddToStyleDoneFormat, selection.Selected.Count, style.Key, Path.GetFileName(style.FilePath), replaced));
        }

        private async Task FindMatchingAsync(string filePath, ITextSnapshot snapshot, AttributeSelection selection)
        {
            await TaskScheduler.Default;
            string text = snapshot.GetText();
            ResourceIndex index = ResourceIndex.ForFile(filePath);
            index.SetLiveText(filePath, text);
            MergeGraph graph = index.GetMergeGraph(CancellationToken.None);
            int line = snapshot.GetLineNumberFromPosition(selection.TagStart) + 1;
            IReadOnlyList<ResourceEntry> visible = ResourceMatcher.VisibleEntries(graph, filePath, line);
            IReadOnlyList<ResourceMatch> matches = ResourceMatcher.Find(selection, visible, CancellationToken.None);
            List<MatchRow> rows = matches.Select(m => CreateRow(index, selection, m)).ToList();

            await _joinableTaskFactory.SwitchToMainThreadAsync();

            if (rows.Count == 0)
            {
                ShowStatus(Localizer.Get(Strings.MatchNoneFound));
                return;
            }

            var dialog = new MatchResourceDialog(rows);
            if (dialog.ShowModal() != true || !(dialog.Selected is ResourceMatch match) || !IsUnchanged(snapshot))
            {
                return;
            }

            new BufferEditor(_serviceProvider, _editorAdapters).Apply(filePath, _ => ResourceMatcher.Apply(selection, match));
            ShowStatus(Localizer.Format(Strings.MatchAppliedFormat, match.Key, Path.GetFileName(match.Entry.FilePath)));
        }

        /// <summary>The text of one row: "Style for Path · takes over: Fill, Width" and the new attribute.</summary>
        private static MatchRow CreateRow(ResourceIndex index, AttributeSelection selection, ResourceMatch match)
        {
            string covered = string.Join(", ", match.Covered.Select(a => a.Name));
            string description;
            string change;
            if (match.Kind == MatchKind.Style)
            {
                var parts = new List<string> { Localizer.Format(Strings.MatchStyleFormat, StyleInfoBuilder.ReadTargetType(match.Entry.Element), covered) };
                if (match.Remaining.Count > 0)
                {
                    parts.Add(Localizer.Format(Strings.MatchKeepsFormat, string.Join(", ", match.Remaining.Select(a => a.Name))));
                }

                if (match.TriggerCount > 0)
                {
                    parts.Add(Localizer.Format(Strings.MatchTriggersFormat, match.TriggerCount));
                }

                description = string.Join(" · ", parts);
                change = "Style=\"{StaticResource " + match.Key + "}\"";
            }
            else
            {
                description = Localizer.Format(Strings.MatchValueFormat, match.Entry.ElementTypeName, covered);
                change = covered + "=\"{StaticResource " + match.Key + "}\"";
            }

            return new MatchRow(match, match.Key, description, change, ReferenceChecker.Relative(index, match.Entry.FilePath) + ":" + match.Entry.LineNumber);
        }

        /// <summary>The positions are from the snapshot the action saw. Stop if the text changed since.</summary>
        private static bool IsUnchanged(ITextSnapshot snapshot) =>
            snapshot.TextBuffer.CurrentSnapshot.Version.VersionNumber == snapshot.Version.VersionNumber;

        private static string Validate(ResourceIndex index, string key)
        {
            switch (KeyRenamer.Check(index, string.Empty, key, CancellationToken.None))
            {
                case RenameRejection.Invalid:
                    return Localizer.Get(Strings.RenameInvalid);
                case RenameRejection.Exists:
                    return Localizer.Format(Strings.RenameExistsFormat, key);
                default:
                    return null;
            }
        }

        private void ShowStatus(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_serviceProvider.GetService(typeof(SVsStatusbar)) is IVsStatusbar statusbar)
            {
                statusbar.SetText("ResourcePreview: " + message);
            }
        }
    }
}
