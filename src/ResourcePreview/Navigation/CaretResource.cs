using System;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using ResourcePreview.QuickInfo;

namespace ResourcePreview.Navigation
{
    /// <summary>
    /// The resource key at the caret of the active .xaml editor: a reference ({StaticResource X}) or,
    /// when asked for, a definition (x:Key="X"). Shared by the context menu commands.
    /// </summary>
    internal sealed class CaretResource
    {
        private CaretResource(IWpfTextView view, string filePath, ResourceReference reference, int lineNumber, bool isDefinition)
        {
            View = view;
            FilePath = filePath;
            Reference = reference;
            LineNumber = lineNumber;
            IsDefinition = isDefinition;
        }

        public IWpfTextView View { get; }

        public string FilePath { get; }

        public ResourceReference Reference { get; }

        /// <summary>1-based, like the line numbers in the index.</summary>
        public int LineNumber { get; }

        /// <summary>True for x:Key="X", false for {StaticResource X} and {DynamicResource X}.</summary>
        public bool IsDefinition { get; }

        /// <summary>
        /// Only reads the current line, because menu commands call this every time the context menu opens.
        /// Returns null when the caret is not on a resource key.
        /// </summary>
        public static CaretResource TryGet(
            IServiceProvider serviceProvider,
            IVsEditorAdaptersFactoryService editorAdapters,
            ITextDocumentFactoryService documents,
            bool includeDefinitions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(serviceProvider.GetService(typeof(SVsTextManager)) is IVsTextManager textManager)
                || textManager.GetActiveView(1, null, out IVsTextView vsView) != 0
                || vsView == null)
            {
                return null;
            }

            IWpfTextView view = editorAdapters.GetWpfTextView(vsView);
            if (view == null
                || !documents.TryGetTextDocument(view.TextBuffer, out ITextDocument document)
                || !document.FilePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            SnapshotPoint caret = view.Caret.Position.BufferPosition;
            ITextSnapshotLine line = caret.GetContainingLine();
            string text = line.GetText();
            int position = caret.Position - line.Start.Position;

            if (ResourceReferenceFinder.TryFindReference(text, position, out ResourceReference reference))
            {
                return new CaretResource(view, document.FilePath, reference, line.LineNumber + 1, isDefinition: false);
            }

            if (includeDefinitions && ResourceReferenceFinder.TryFind(text, position, out reference))
            {
                return new CaretResource(view, document.FilePath, reference, line.LineNumber + 1, isDefinition: true);
            }

            return null;
        }
    }
}
