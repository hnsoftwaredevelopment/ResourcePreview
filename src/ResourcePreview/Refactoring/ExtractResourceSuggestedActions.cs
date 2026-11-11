using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using ResourcePreview.Localization;
using ResourcePreview.Resources;

namespace ResourcePreview.Refactoring
{
    /// <summary>
    /// The light bulb (Ctrl+.) in a .xaml file:
    /// - on a value such as Background="#FF2D7D9A": "Extract to resource...";
    /// - on a selection of attributes: "Extract to style...", and "Add to style 'X'" when the element has a Style;
    /// - on both: "Find matching resource...".
    /// VS asks every ISuggestedActionsSourceProvider for the content type when the caret or selection changes.
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

        /// <summary>Imported instead of ThreadHelper.JoinableTaskFactory, which the VSSDK007 analyzer flags here.</summary>
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

            var refactorings = new ResourceRefactorings(ServiceProvider, EditorAdapters, JoinableTaskContext.Factory);
            return new ExtractResourceSuggestedActionsSource(textView, textBuffer, document, refactorings);
        }
    }

    internal sealed class ExtractResourceSuggestedActionsSource : ISuggestedActionsSource
    {
        private readonly ITextView _textView;
        private readonly ITextBuffer _textBuffer;
        private readonly ITextDocument _document;
        private readonly ResourceRefactorings _refactorings;

        public ExtractResourceSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer, ITextDocument document, ResourceRefactorings refactorings)
        {
            _textView = textView;
            _textBuffer = textBuffer;
            _document = document;
            _refactorings = refactorings;
        }

        // Never raised: VS asks again when the caret moves, which is all we need.
        public event EventHandler<EventArgs> SuggestedActionsChanged
        {
            add { }
            remove { }
        }

        public Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateActions(range).Any());
        }

        public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
        {
            List<ISuggestedAction> actions = CreateActions(range).ToList();
            return actions.Count == 0
                ? Enumerable.Empty<SuggestedActionSet>()
                : new[] { new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.Refactoring, actions) };
        }

        /// <summary>Only reads the text: VS calls this often, so no index work here.</summary>
        private IEnumerable<ISuggestedAction> CreateActions(SnapshotSpan range)
        {
            if (range.Snapshot.TextBuffer != _textBuffer)
            {
                yield break;
            }

            ITextSnapshot snapshot = range.Snapshot;
            string filePath = _document.FilePath;
            SnapshotSpan selection = _textView.Selection.IsEmpty || _textView.Selection.SelectedSpans.Count != 1
                ? new SnapshotSpan(snapshot, 0, 0)
                : _textView.Selection.SelectedSpans[0];

            if (selection.Length > 0 && selection.Snapshot == snapshot)
            {
                AttributeSelection attributes = StylePlanner.TryFind(snapshot.GetText(), selection.Start, selection.End);
                if (attributes != null && attributes.CanHaveStyle)
                {
                    yield return new RefactoringAction(Localizer.Get(Strings.ExtractToStyle), () => _refactorings.ExtractStyle(filePath, snapshot, attributes));
                    if (attributes.StyleKey != null)
                    {
                        yield return new RefactoringAction(
                            Localizer.Format(Strings.AddToStyleFormat, attributes.StyleKey),
                            () => _refactorings.AddToStyle(filePath, snapshot, attributes));
                    }

                    yield return new RefactoringAction(Localizer.Get(Strings.FindMatchingResource), () => _refactorings.FindMatching(filePath, snapshot, attributes));

                    yield break;
                }
            }

            SnapshotPoint caret = _textView.Caret.Position.BufferPosition;
            if (caret.Snapshot == snapshot)
            {
                ExtractCandidate candidate = ExtractPlanner.TryFind(snapshot.GetText(), caret.Position);
                if (candidate != null)
                {
                    yield return new RefactoringAction(Localizer.Get(Strings.ExtractToResource), () => _refactorings.ExtractValue(filePath, snapshot, candidate));

                    // Only where the light bulb shows anyway: a value that could also be a resource already.
                    AttributeSelection single = StylePlanner.TryFindAt(snapshot.GetText(), caret.Position);
                    if (single != null)
                    {
                        yield return new RefactoringAction(Localizer.Get(Strings.FindMatchingResource), () => _refactorings.FindMatching(filePath, snapshot, single));
                    }
                }
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

    /// <summary>One line in the light bulb menu.</summary>
    internal sealed class RefactoringAction : ISuggestedAction
    {
        private readonly Action _invoke;

        public RefactoringAction(string displayText, Action invoke)
        {
            DisplayText = displayText;
            _invoke = invoke;
        }

        public string DisplayText { get; }

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
            _invoke();
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
