using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using ResourcePreview.Localization;
using ResourcePreview.Resources;
using Task = System.Threading.Tasks.Task;

namespace ResourcePreview.Navigation
{
    /// <summary>
    /// "Rename Resource Key" in the XAML editor context menu: renames a key in all its definitions and
    /// references in the solution. Works on a reference and on an x:Key.
    ///
    /// The changes go through the editor buffers, not straight to disk (<see cref="BufferEditor"/>).
    /// </summary>
    internal sealed class RenameResourceKeyCommand
    {
        public const int CommandId = 0x0102;

        private readonly AsyncPackage _package;
        private readonly IVsEditorAdaptersFactoryService _editorAdapters;
        private readonly ITextDocumentFactoryService _documents;

        private RenameResourceKeyCommand(AsyncPackage package, IVsEditorAdaptersFactoryService editorAdapters, ITextDocumentFactoryService documents)
        {
            _package = package;
            _editorAdapters = editorAdapters;
            _documents = documents;
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

            var instance = new RenameResourceKeyCommand(
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
            command.Text = Localizer.Get(Strings.RenameResourceKey);
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
                .RunAsync(() => RenameAsync(caret.FilePath, caret.Reference.Key, text))
                .FileAndForget("ResourcePreview/RenameResourceKey");
        }

        private async Task RenameAsync(string filePath, string key, string text)
        {
            // The first lookup in a solution reads every .xaml file. Keep that off the UI thread.
            await TaskScheduler.Default;

            ResourceIndex index = ResourceIndex.ForFile(filePath);
            index.SetLiveText(filePath, text);
            IReadOnlyList<RenameFile> plan = KeyRenamer.Plan(index, key, _package.DisposalToken);

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);

            string summary = Localizer.Format(
                Strings.RenameSummaryFormat,
                plan.Sum(f => f.Definitions),
                plan.Sum(f => f.References),
                plan.Count);

            var dialog = new RenameDialog(key, summary, name => Describe(KeyRenamer.Check(index, key, name, CancellationToken.None), name));
            if (dialog.ShowModal() != true)
            {
                return;
            }

            string newKey = dialog.NewKey;
            int changes = 0;
            int files = 0;
            foreach (RenameFile file in plan)
            {
                int changed = RenameInFile(file.FilePath, key, newKey);
                if (changed > 0)
                {
                    changes += changed;
                    files++;
                }
            }

            ShowStatus(Localizer.Format(Strings.RenameDoneFormat, key, newKey, changes, files));
        }

        /// <summary>Error text for the dialog, empty for "same name" (no text, but nothing to do), or null when fine.</summary>
        private static string Describe(RenameRejection? rejection, string newKey)
        {
            switch (rejection)
            {
                case RenameRejection.Invalid:
                    return Localizer.Get(Strings.RenameInvalid);
                case RenameRejection.Exists:
                    return Localizer.Format(Strings.RenameExistsFormat, newKey);
                case RenameRejection.Unchanged:
                    return string.Empty;
                default:
                    return null;
            }
        }

        /// <summary>Renames in one file and returns the number of changes.</summary>
        private int RenameInFile(string filePath, string key, string newKey)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new BufferEditor(_package, _editorAdapters).Apply(
                filePath,
                text => KeyRenamer.FindSpans(text, key).Select(span => new TextChange(span.Start, span.Length, newKey)).ToList());
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
