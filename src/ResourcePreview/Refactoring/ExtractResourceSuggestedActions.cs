using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using ResourcePreview.Localization;
using ResourcePreview.Navigation;
using ResourcePreview.Resources;
using Task = System.Threading.Tasks.Task;

namespace ResourcePreview.Refactoring
{
    /// <summary>
    /// The light bulb (Ctrl+.) on a literal value such as Background="#FF2D7D9A": "Extract to resource...".
    /// VS asks every ISuggestedActionsSourceProvider for the content type when the caret moves.
    /// </summary>
    [Export(typeof(ISuggestedActionsSourceProvider))]
    [Name("XAML Resource Preview Extract to Resource")]
    [ContentType("XAML")]
    internal sealed class ExtractResourceSuggestedActionsSourceProvider : ISuggestedActionsSourceProvider
    {
        [Import]
        internal ITextDocumentFactoryService TextDocumentFactoryService { get; set; }

        [Import]
        internal SVsServiceProvider ServiceProvider { get; set; }

        [Import]
        internal IVsEditorAdaptersFactoryService EditorAdapters { get; set; }

        [Import]
        internal JoinableTaskContext JoinableTaskContext { get; set; }

        public ISuggestedActionsSource CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer)
        {
            if (textView == null || textBuffer == null
                || !TextDocumentFactoryService.TryGetTextDocument(textBuffer, out ITextDocument document)
                || !document.FilePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new ExtractResourceSuggestedActionsSource(textView, textBuffer, document, ServiceProvider, EditorAdapters, JoinableTaskContext.Factory);
        }
    }

    internal sealed class ExtractResourceSuggestedActionsSource : ISuggestedActionsSource
    {
        private readonly ITextView _textView;
        private readonly ITextBuffer _textBuffer;
        private readonly ITextDocument _document;
        private readonly IServiceProvider _serviceProvider;
        private readonly IVsEditorAdaptersFactoryService _editorAdapters;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        public ExtractResourceSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer, ITextDocument document, IServiceProvider serviceProvider, IVsEditorAdaptersFactoryService editorAdapters, JoinableTaskFactory joinableTaskFactory)
        {
            _textView = textView;
            _textBuffer = textBuffer;
            _document = document;
            _serviceProvider = serviceProvider;
            _editorAdapters = editorAdapters;
            _joinableTaskFactory = joinableTaskFactory;
        }

        // Never raised: VS asks again when the caret moves, which is all we need.
        public event EventHandler<EventArgs> SuggestedActionsChanged
        {
            add { }
            remove { }
        }

        public Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
        {
            return Task.FromResult(FindCandidate(range) != null);
        }

        public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
        {
            ExtractCandidate candidate = FindCandidate(range);
            if (candidate == null)
            {
                return Enumerable.Empty<SuggestedActionSet>();
            }

            var action = new ExtractResourceAction(candidate, range.Snapshot, _document.FilePath, _serviceProvider, _editorAdapters, _joinableTaskFactory);
            return new[] { new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.Refactoring, new[] { action }, applicableToSpan: new Span(candidate.ValueStart, candidate.ValueLength)) };
        }

        /// <summary>The value at the caret, only reading the text. Runs often, so no index work here.</summary>
        private ExtractCandidate FindCandidate(SnapshotSpan range)
        {
            if (range.Snapshot.TextBuffer != _textBuffer)
            {
                return null;
            }

            SnapshotPoint caret = _textView.Caret.Position.BufferPosition;
            if (caret.Snapshot != range.Snapshot)
            {
                return null;
            }

            return ExtractPlanner.TryFind(range.Snapshot.GetText(), caret.Position);
        }

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Dispose()
        {
        }
    }

    internal sealed class ExtractResourceAction : ISuggestedAction
    {
        // The last file a resource went to, so the dialog starts there next time.
        private static string _lastTarget;

        private readonly ExtractCandidate _candidate;
        private readonly ITextSnapshot _snapshot;
        private readonly string _filePath;
        private readonly IServiceProvider _serviceProvider;
        private readonly IVsEditorAdaptersFactoryService _editorAdapters;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        public ExtractResourceAction(ExtractCandidate candidate, ITextSnapshot snapshot, string filePath, IServiceProvider serviceProvider, IVsEditorAdaptersFactoryService editorAdapters, JoinableTaskFactory joinableTaskFactory)
        {
            _joinableTaskFactory = joinableTaskFactory;
            _candidate = candidate;
            _snapshot = snapshot;
            _filePath = filePath;
            _serviceProvider = serviceProvider;
            _editorAdapters = editorAdapters;
        }

        public string DisplayText => Localizer.Get(Strings.ExtractToResource);

        public ImageMoniker IconMoniker => default;

        public string IconAutomationText => null;

        public string InputGestureText => null;

        public bool HasActionSets => false;

        public bool HasPreview => false;

        public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<SuggestedActionSet>());

        public Task<object> GetPreviewAsync(CancellationToken cancellationToken) => Task.FromResult<object>(null);

        public void Invoke(CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _joinableTaskFactory.RunAsync(ExtractAsync).FileAndForget("ResourcePreview/ExtractToResource");
        }

        private async Task ExtractAsync()
        {
            // The first lookup in a solution reads every .xaml file. Keep that off the UI thread.
            await TaskScheduler.Default;
            string text = _snapshot.GetText();
            ResourceIndex index = ResourceIndex.ForFile(_filePath);
            index.SetLiveText(_filePath, text);
            IReadOnlyList<string> targets = ExtractPlanner.Targets(index, _filePath, CancellationToken.None);

            await _joinableTaskFactory.SwitchToMainThreadAsync();

            if (targets.Count == 0)
            {
                ShowStatus(Localizer.Get(Strings.ExtractNoTarget));
                return;
            }

            var items = targets.Select(t => new KeyValuePair<string, string>(t, ReferenceChecker.Relative(index, t))).ToList();
            var dialog = new ExtractResourceDialog(
                ExtractPlanner.SuggestKey(_candidate),
                items,
                _lastTarget,
                key => Validate(index, key),
                key => ExtractPlanner.ResourceText(_candidate, key));
            if (dialog.ShowModal() != true || dialog.Target == null)
            {
                return;
            }

            string target = dialog.Target;
            string newKey = dialog.Key;
            _lastTarget = target;
            bool sameFile = string.Equals(target, _filePath, StringComparison.OrdinalIgnoreCase);
            var editor = new BufferEditor(_serviceProvider, _editorAdapters);

            // The value positions are from the snapshot the light bulb saw. Stop if the text changed since.
            if (_snapshot.TextBuffer.CurrentSnapshot.Version.VersionNumber != _snapshot.Version.VersionNumber)
            {
                return;
            }

            int added = editor.Apply(target, targetText =>
            {
                List<TextChange> changes = ExtractPlanner.AddResource(targetText, _candidate, newKey, sameFile ? _candidate.ValueStart : (int?)null).ToList();
                if (sameFile && changes.Count > 0)
                {
                    changes.Add(ExtractPlanner.ReplaceValue(_candidate, newKey));
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
                editor.Apply(_filePath, _ => new[] { ExtractPlanner.ReplaceValue(_candidate, newKey) });
            }

            ShowStatus(Localizer.Format(Strings.ExtractDoneFormat, newKey, Path.GetFileName(target)));
        }

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

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Dispose()
        {
        }
    }
}
