using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;
using ResourcePreview.Localization;
using ResourcePreview.Resources;
using Task = System.Threading.Tasks.Task;

namespace ResourcePreview.Navigation
{
    /// <summary>
    /// "Find Resource References" in the XAML editor context menu (Shift+Alt+F12). Works on a reference
    /// and on an x:Key. Shows the result in <see cref="ResourceReferencesWindow"/>.
    /// The menu item and key binding are declared in ResourcePreviewPackage.vsct.
    /// </summary>
    internal sealed class FindResourceReferencesCommand
    {
        public const int CommandId = 0x0101;

        private readonly AsyncPackage _package;
        private readonly IVsEditorAdaptersFactoryService _editorAdapters;
        private readonly ITextDocumentFactoryService _documents;
        private readonly DefinitionNavigator _navigator;

        private FindResourceReferencesCommand(AsyncPackage package, IVsEditorAdaptersFactoryService editorAdapters, ITextDocumentFactoryService documents)
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

            var instance = new FindResourceReferencesCommand(
                package,
                componentModel.GetService<IVsEditorAdaptersFactoryService>(),
                componentModel.GetService<ITextDocumentFactoryService>());

            var command = new OleMenuCommand(instance.Execute, new CommandID(GoToResourceDefinitionCommand.CommandSet, CommandId));
            command.BeforeQueryStatus += instance.OnBeforeQueryStatus;
            commandService.AddCommand(command);
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var command = (OleMenuCommand)sender;
            bool available = CaretResource.TryGet(_package, _editorAdapters, _documents, includeDefinitions: true) != null;
            command.Visible = available;
            command.Enabled = available;
            command.Text = Localizer.Get(Strings.FindResourceReferences);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            CaretResource caret = CaretResource.TryGet(_package, _editorAdapters, _documents, includeDefinitions: true);
            if (caret == null)
            {
                return;
            }

            string text = caret.View.TextSnapshot.GetText();
            _package.JoinableTaskFactory
                .RunAsync(() => FindAsync(caret, text))
                .FileAndForget("ResourcePreview/FindResourceReferences");
        }

        private async Task FindAsync(CaretResource caret, string text)
        {
            // The first search in a solution reads every .xaml file. Keep that off the UI thread.
            await TaskScheduler.Default;

            ResourceIndex index = ResourceIndex.ForFile(caret.FilePath);
            index.SetLiveText(caret.FilePath, text);

            string key = caret.Reference.Key;

            // On an x:Key that definition is the starting point; on a reference, the one WPF uses there.
            ResourceEntry selected = caret.IsDefinition
                ? index.Find(key, caret.FilePath, _package.DisposalToken)
                    .FirstOrDefault(d => string.Equals(d.FilePath, caret.FilePath, StringComparison.OrdinalIgnoreCase) && d.LineNumber == caret.LineNumber)
                : index.Resolve(key, caret.FilePath, _package.DisposalToken).FirstOrDefault(d => d.Status == DefinitionStatus.Used)?.Entry;

            UsageReport report = UsageFinder.Find(index, key, selected, _package.DisposalToken);

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);

            var window = await _package.ShowToolWindowAsync(typeof(ResourceReferencesWindow), 0, create: true, _package.DisposalToken) as ResourceReferencesWindow;
            window?.Show(report, index, _navigator);
        }
    }
}
