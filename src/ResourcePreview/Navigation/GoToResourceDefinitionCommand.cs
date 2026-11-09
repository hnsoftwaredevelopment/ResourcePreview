using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using ResourcePreview.Localization;
using ResourcePreview.QuickInfo;
using ResourcePreview.Resources;
using Task = System.Threading.Tasks.Task;

namespace ResourcePreview.Navigation
{
    /// <summary>
    /// "Go To Resource Definition" in the XAML editor context menu (Ctrl+Alt+F12).
    /// The menu item and key binding are declared in ResourcePreviewPackage.vsct.
    /// </summary>
    internal sealed class GoToResourceDefinitionCommand
    {
        public static readonly Guid CommandSet = new Guid("49b12957-db89-4dfc-b79d-a9d1c46f0a5f");
        public const int CommandId = 0x0100;

        private readonly AsyncPackage _package;
        private readonly IVsEditorAdaptersFactoryService _editorAdapters;
        private readonly ITextDocumentFactoryService _documents;
        private readonly DefinitionNavigator _navigator;

        private GoToResourceDefinitionCommand(AsyncPackage package, IVsEditorAdaptersFactoryService editorAdapters, ITextDocumentFactoryService documents)
        {
            _package = package;
            _editorAdapters = editorAdapters;
            _documents = documents;
            _navigator = new DefinitionNavigator(package, editorAdapters, package.JoinableTaskFactory.Context);
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

            var instance = new GoToResourceDefinitionCommand(
                package,
                componentModel.GetService<IVsEditorAdaptersFactoryService>(),
                componentModel.GetService<ITextDocumentFactoryService>());

            var command = new OleMenuCommand(instance.Execute, new CommandID(CommandSet, CommandId));
            command.BeforeQueryStatus += instance.OnBeforeQueryStatus;
            commandService.AddCommand(command);
        }

        /// <summary>
        /// Runs every time the context menu opens or the shortcut is pressed, so it only reads the
        /// current line. The menu item is hidden unless the caret is on a resource reference.
        /// </summary>
        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var command = (OleMenuCommand)sender;
            bool available = CaretResource.TryGet(_package, _editorAdapters, _documents, includeDefinitions: false) != null;
            command.Visible = available;
            command.Enabled = available;

            // The text is set here instead of in the .vsct, so it follows the language setting.
            command.Text = Localizer.Get(Strings.GoToResourceDefinition);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            CaretResource caret = CaretResource.TryGet(_package, _editorAdapters, _documents, includeDefinitions: false);
            if (caret == null)
            {
                return;
            }

            // Unsaved changes in the current file count, like in the Quick Info preview.
            string text = caret.View.TextSnapshot.GetText();
            _package.JoinableTaskFactory
                .RunAsync(() => GoToAsync(caret.View, caret.FilePath, text, caret.Reference.Key))
                .FileAndForget("ResourcePreview/GoToResourceDefinition");
        }

        private async Task GoToAsync(IWpfTextView view, string filePath, string text, string key)
        {
            // The first search in a solution reads every .xaml file. Keep that off the UI thread.
            await TaskScheduler.Default;

            ResourceIndex index = ResourceIndex.ForFile(filePath);
            index.SetLiveText(filePath, text);
            IReadOnlyList<ResourceEntry> entries = index.Find(key, filePath, _package.DisposalToken);

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);

            // Remove the live text when this editor closes, otherwise the index keeps using it instead of the file on disk.
            view.Closed -= OnViewClosed;
            view.Closed += OnViewClosed;

            if (entries.Count == 0)
            {
                ShowStatus(key + ": " + Localizer.Format(Strings.NotFoundUnderFormat, index.RootDirectory));
                return;
            }

            _navigator.NavigateTo(entries[0]);

            if (entries.Count > 1)
            {
                ShowStatus(Localizer.Format(Strings.GoToMoreDefinitionsFormat, key, entries.Count - 1));
            }
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            var view = (IWpfTextView)sender;
            view.Closed -= OnViewClosed;
            if (_documents.TryGetTextDocument(view.TextBuffer, out ITextDocument document))
            {
                ResourceIndex.ForFile(document.FilePath).ClearLiveText(document.FilePath);
            }
        }

        private void ShowStatus(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (((IServiceProvider)_package).GetService(typeof(SVsStatusbar)) is IVsStatusbar statusbar)
            {
                statusbar.SetText("ResourcePreview: " + message);
            }
        }
    }
}
