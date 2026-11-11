using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using ResourcePreview.Localization;
using ResourcePreview.Navigation;
using ResourcePreview.Resources;
using Task = System.Threading.Tasks.Task;

namespace ResourcePreview.Refactoring
{
    /// <summary>
    /// The light bulb actions also in the context menu: "Extract to Resource...", "Extract to Style...",
    /// "Add to Existing Style" and "Find Matching Resource...". Each item only shows when it applies to the caret or selection.
    /// </summary>
    internal sealed class RefactoringCommands
    {
        public const int ExtractToResourceId = 0x0103;
        public const int ExtractToStyleId = 0x0104;
        public const int AddToStyleId = 0x0105;
        public const int FindMatchingResourceId = 0x0106;

        private readonly AsyncPackage _package;
        private readonly IVsEditorAdaptersFactoryService _editorAdapters;
        private readonly ITextDocumentFactoryService _documents;
        private readonly ResourceRefactorings _refactorings;

        private RefactoringCommands(AsyncPackage package, IVsEditorAdaptersFactoryService editorAdapters, ITextDocumentFactoryService documents)
        {
            _package = package;
            _editorAdapters = editorAdapters;
            _documents = documents;
            _refactorings = new ResourceRefactorings(package, editorAdapters, package.JoinableTaskFactory);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            var componentModel = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            if (commandService == null || componentModel == null)
            {
                return;
            }

            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var instance = new RefactoringCommands(
                package,
                componentModel.GetService<IVsEditorAdaptersFactoryService>(),
                componentModel.GetService<ITextDocumentFactoryService>());

            instance.Add(commandService, ExtractToResourceId, instance.ExtractToResourceStatus, instance.ExtractToResource);
            instance.Add(commandService, ExtractToStyleId, instance.ExtractToStyleStatus, instance.ExtractToStyle);
            instance.Add(commandService, AddToStyleId, instance.AddToStyleStatus, instance.AddToStyle);
            instance.Add(commandService, FindMatchingResourceId, instance.FindMatchingStatus, instance.FindMatching);
        }

        private void Add(OleMenuCommandService commandService, int id, Action<OleMenuCommand> status, Action execute)
        {
            var command = new OleMenuCommand((s, e) => execute(), new CommandID(GoToResourceDefinitionCommand.CommandSet, id));
            command.BeforeQueryStatus += (s, e) => status((OleMenuCommand)s);
            commandService.AddCommand(command);
        }

        // --- What is under the caret or selected ---

        private ExtractCandidate FindValue(out IWpfTextView view, out string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            view = CaretResource.TryGetActiveXamlView(_package, _editorAdapters, _documents, out filePath);
            if (view == null || HasSelection(view))
            {
                return null; // with a selection the style items apply
            }

            return ExtractPlanner.TryFind(view.TextSnapshot.GetText(), view.Caret.Position.BufferPosition.Position);
        }

        private AttributeSelection FindAttributes(out IWpfTextView view, out string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            view = CaretResource.TryGetActiveXamlView(_package, _editorAdapters, _documents, out filePath);
            if (view == null || !HasSelection(view))
            {
                return null;
            }

            SnapshotSpan span = view.Selection.SelectedSpans[0];
            AttributeSelection selection = StylePlanner.TryFind(view.TextSnapshot.GetText(), span.Start.Position, span.End.Position);
            return selection != null && selection.CanHaveStyle ? selection : null; // no Style for a Setter or GradientStop
        }

        /// <summary>The selected attributes, or without a selection the attribute at the caret.</summary>
        private AttributeSelection FindSearchInput(out IWpfTextView view, out string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            view = CaretResource.TryGetActiveXamlView(_package, _editorAdapters, _documents, out filePath);
            if (view == null)
            {
                return null;
            }

            string text = view.TextSnapshot.GetText();
            if (HasSelection(view))
            {
                SnapshotSpan span = view.Selection.SelectedSpans[0];
                return StylePlanner.TryFind(text, span.Start.Position, span.End.Position);
            }

            return StylePlanner.TryFindAt(text, view.Caret.Position.BufferPosition.Position);
        }

        private static bool HasSelection(IWpfTextView view) =>
            !view.Selection.IsEmpty && view.Selection.SelectedSpans.Count == 1;

        // --- The three items ---

        private void ExtractToResourceStatus(OleMenuCommand command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Show(command, FindValue(out _, out _) != null, Localizer.Get(Strings.ExtractToResource));
        }

        private void ExtractToResource()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ExtractCandidate candidate = FindValue(out IWpfTextView view, out string filePath);
            if (candidate != null)
            {
                _refactorings.ExtractValue(filePath, view.TextSnapshot, candidate);
            }
        }

        private void ExtractToStyleStatus(OleMenuCommand command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Show(command, FindAttributes(out _, out _) != null, Localizer.Get(Strings.ExtractToStyle));
        }

        private void ExtractToStyle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            AttributeSelection selection = FindAttributes(out IWpfTextView view, out string filePath);
            if (selection != null)
            {
                _refactorings.ExtractStyle(filePath, view.TextSnapshot, selection);
            }
        }

        private void AddToStyleStatus(OleMenuCommand command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            AttributeSelection selection = FindAttributes(out _, out _);
            bool available = selection?.StyleKey != null;
            Show(command, available, available ? Localizer.Format(Strings.AddToStyleFormat, selection.StyleKey) : string.Empty);
        }

        private void AddToStyle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            AttributeSelection selection = FindAttributes(out IWpfTextView view, out string filePath);
            if (selection?.StyleKey != null)
            {
                _refactorings.AddToStyle(filePath, view.TextSnapshot, selection);
            }
        }

        private void FindMatchingStatus(OleMenuCommand command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Show(command, FindSearchInput(out _, out _) != null, Localizer.Get(Strings.FindMatchingResource));
        }

        private void FindMatching()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            AttributeSelection selection = FindSearchInput(out IWpfTextView view, out string filePath);
            if (selection != null)
            {
                _refactorings.FindMatching(filePath, view.TextSnapshot, selection);
            }
        }

        private static void Show(OleMenuCommand command, bool available, string text)
        {
            command.Visible = available;
            command.Enabled = available;
            if (available)
            {
                command.Text = text;
            }
        }
    }
}
