using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using ResourcePreview.Resources;

namespace ResourcePreview.Editor
{
    /// <summary>
    /// Keeps the index up to date with every open .xaml editor, also when nothing is saved: after an edit
    /// (a short pause, not per key stroke) the text goes to <see cref="ResourceIndex.SetLiveText"/>.
    /// When the editor closes, the file on disk counts again. The XAML Resources window listens to
    /// <see cref="ResourceIndex.LiveTextChanged"/>, so a resource added by "Extract to resource" shows at once.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("XAML")]
    [ContentType("XML")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class LiveTextTracker : IWpfTextViewCreationListener
    {
        private const int PauseMilliseconds = 500;

        [Import]
        internal ITextDocumentFactoryService TextDocumentFactoryService { get; set; }

        [Import]
        internal JoinableTaskContext JoinableTaskContext { get; set; }

        public void TextViewCreated(IWpfTextView textView)
        {
            ITextBuffer buffer = textView.TextBuffer;
            if (!TextDocumentFactoryService.TryGetTextDocument(buffer, out ITextDocument document)
                || !document.FilePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            JoinableTaskFactory factory = JoinableTaskContext.Factory;
            CancellationTokenSource pause = null;

            void OnChanged(object sender, TextContentChangedEventArgs e)
            {
                pause?.Cancel();
                pause = new CancellationTokenSource();
                CancellationToken token = pause.Token;
                ITextSnapshot snapshot = e.After;
                string path = document.FilePath;
                factory.RunAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(PauseMilliseconds, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return; // another edit came in
                    }

                    // Parsing a large file takes a moment; not on the UI thread.
                    await TaskScheduler.Default;
                    ResourceIndex.ForFile(path).SetLiveText(path, snapshot.GetText());
                }).FileAndForget("ResourcePreview/LiveTextTracker");
            }

            buffer.Changed += OnChanged;
            textView.Closed += (s, e) =>
            {
                buffer.Changed -= OnChanged;
                pause?.Cancel();

                // Another view of the same file may still be open; then it keeps the live text.
                if (--ViewCount(buffer).Value == 0)
                {
                    ResourceIndex.ForFile(document.FilePath).ClearLiveText(document.FilePath);
                }
            };
            ViewCount(buffer).Value++;
        }

        private sealed class Counter
        {
            public int Value;
        }

        private static Counter ViewCount(ITextBuffer buffer) =>
            buffer.Properties.GetOrCreateSingletonProperty(typeof(LiveTextTracker), () => new Counter());
    }
}
