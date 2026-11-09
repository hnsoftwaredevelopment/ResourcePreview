using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using ResourcePreview.Localization;
using ResourcePreview.Resources;

namespace ResourcePreview.Navigation
{
    /// <summary>
    /// Opens the file of a resource and selects the name in its x:Key attribute.
    /// Must run on the UI thread, like most IVs* shell interfaces.
    /// </summary>
    internal sealed class DefinitionNavigator
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IVsEditorAdaptersFactoryService _editorAdapters;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        public DefinitionNavigator(IServiceProvider serviceProvider, IVsEditorAdaptersFactoryService editorAdapters, JoinableTaskContext joinableTaskContext)
        {
            _serviceProvider = serviceProvider;
            _editorAdapters = editorAdapters;
            _joinableTaskFactory = joinableTaskContext.Factory;
        }

        /// <summary>
        /// Handles a click in the tooltip. Dismisses the tooltip first, otherwise it stays open
        /// on top of the newly opened file.
        /// </summary>
        public void NavigateFromQuickInfo(IAsyncQuickInfoSession session, ResourceEntry entry)
        {
            // Fire and forget from a sync click handler. FileAndForget logs failures instead of
            // swallowing them (required by analyzer VSSDK007).
            _joinableTaskFactory
                .RunAsync(() => NavigateFromQuickInfoAsync(session, entry))
                .FileAndForget("ResourcePreview/NavigateFromQuickInfo");
        }

        private async Task NavigateFromQuickInfoAsync(IAsyncQuickInfoSession session, ResourceEntry entry)
        {
            // Switching first lets callers use any thread (recognized by analyzer VSTHRD010).
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            await session.DismissAsync();
            NavigateTo(entry);
        }

        public void NavigateTo(ResourceEntry entry)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Open(entry.FilePath, textView => SelectKey(textView, entry));
        }

        /// <summary>Opens the file of a {StaticResource X} and selects X. Used by the list of references.</summary>
        public void NavigateToUse(string filePath, ResourceUse use)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Open(filePath, textView => SelectUse(textView, use));
        }

        private void Open(string filePath, Action<IWpfTextView> select)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                // TextView_guid requests the text editor instead of a designer.
                VsShellUtilities.OpenDocument(
                    _serviceProvider,
                    filePath,
                    VSConstants.LOGVIEWID.TextView_guid,
                    out IVsUIHierarchy _,
                    out uint _,
                    out IVsWindowFrame frame);

                if (frame == null)
                {
                    ShowStatus("ResourcePreview: " + Localizer.Format(Strings.CannotOpenFileFormat, filePath));
                    return;
                }

                frame.Show();

                IVsTextView vsTextView = VsShellUtilities.GetTextView(frame);
                IWpfTextView textView = vsTextView == null ? null : _editorAdapters.GetWpfTextView(vsTextView);
                if (textView == null)
                {
                    return; // File is open, but not in a text editor (for example designer only).
                }

                select(textView);
            }
            catch (Exception ex)
            {
                ShowStatus($"ResourcePreview: {ex.Message}");
            }
        }

        /// <summary>
        /// Selects the key name and centers it. Searches the line for the key instead of trusting the
        /// indexed column, because the file may have changed since it was indexed.
        /// </summary>
        private static void SelectKey(IWpfTextView textView, ResourceEntry entry)
        {
            ITextSnapshotLine line = GetLine(textView.TextSnapshot, entry.LineNumber);
            string text = line.GetText();

            int searchFrom = Math.Min(Math.Max(entry.Column - 1, 0), text.Length);
            int keyStart = FindQuotedKey(text, entry.Key, searchFrom);
            if (keyStart < 0)
            {
                keyStart = FindQuotedKey(text, entry.Key, 0);
            }

            Select(textView, line, keyStart, entry.Key.Length);
        }

        private static void SelectUse(IWpfTextView textView, ResourceUse use)
        {
            ITextSnapshotLine line = GetLine(textView.TextSnapshot, use.Line);
            string text = line.GetText();

            int searchFrom = Math.Min(Math.Max(use.Column - 1, 0), text.Length);
            int keyStart = text.IndexOf(use.Key, searchFrom, StringComparison.Ordinal);
            if (keyStart < 0)
            {
                keyStart = text.IndexOf(use.Key, StringComparison.Ordinal);
            }

            Select(textView, line, keyStart, use.Key.Length);
        }

        private static ITextSnapshotLine GetLine(ITextSnapshot snapshot, int lineNumber)
        {
            int lineIndex = Math.Min(Math.Max(lineNumber - 1, 0), snapshot.LineCount - 1);
            return snapshot.GetLineFromLineNumber(lineIndex);
        }

        private static void Select(IWpfTextView textView, ITextSnapshotLine line, int start, int length)
        {
            SnapshotSpan span = start >= 0
                ? new SnapshotSpan(line.Start + start, length)
                : new SnapshotSpan(line.Start, 0);

            textView.Selection.Select(span, isReversed: false);
            textView.Caret.MoveTo(span.End);
            textView.ViewScroller.EnsureSpanVisible(span, EnsureSpanVisibleOptions.AlwaysCenter);
            textView.VisualElement.Focus();
        }

        /// <summary>Returns the position of the key inside single or double quotes, or -1.</summary>
        private static int FindQuotedKey(string text, string key, int startIndex)
        {
            foreach (char quote in new[] { '"', '\'' })
            {
                int index = text.IndexOf(quote + key + quote, startIndex, StringComparison.Ordinal);
                if (index >= 0)
                {
                    return index + 1;
                }
            }

            return -1;
        }

        private void ShowStatus(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_serviceProvider.GetService(typeof(SVsStatusbar)) is IVsStatusbar statusbar)
            {
                statusbar.SetText(message);
            }
        }
    }
}
