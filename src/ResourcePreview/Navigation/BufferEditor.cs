using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using ResourcePreview.Resources;

namespace ResourcePreview.Navigation
{
    /// <summary>
    /// Changes a file through its VS text buffer instead of writing to disk, so unsaved changes in an open
    /// editor are kept and the change can be undone. Used by Rename Resource Key and Extract to Resource.
    ///
    /// - A file that is open in an editor gets one undo step and stays unsaved.
    /// - A file that is not open is opened invisibly (IVsInvisibleEditorManager), changed and saved.
    /// Call on the UI thread only.
    /// </summary>
    internal sealed class BufferEditor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IVsEditorAdaptersFactoryService _editorAdapters;

        public BufferEditor(IServiceProvider serviceProvider, IVsEditorAdaptersFactoryService editorAdapters)
        {
            _serviceProvider = serviceProvider;
            _editorAdapters = editorAdapters;
        }

        /// <summary>
        /// Computes the changes from the current buffer text and applies them in one edit.
        /// Returns the number of changes, 0 when the file could not be opened or nothing changed.
        /// </summary>
        public int Apply(string filePath, Func<string, IReadOnlyList<TextChange>> computeChanges)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool wasOpen = VsShellUtilities.IsDocumentOpen(_serviceProvider, filePath, Guid.Empty, out _, out _, out _);

            if (!(_serviceProvider.GetService(typeof(SVsInvisibleEditorManager)) is IVsInvisibleEditorManager manager)
                || manager.RegisterInvisibleEditor(filePath, null, (uint)_EDITORREGFLAGS.RIEF_ENABLECACHING, null, out IVsInvisibleEditor invisibleEditor) != VSConstants.S_OK)
            {
                return 0;
            }

            try
            {
                // The document data of the file: the same buffer an open editor shows.
                Guid textLinesGuid = typeof(IVsTextLines).GUID;
                if (invisibleEditor.GetDocData(0, ref textLinesGuid, out IntPtr docData) != VSConstants.S_OK || docData == IntPtr.Zero)
                {
                    return 0;
                }

                IVsTextLines textLines;
                try
                {
                    textLines = (IVsTextLines)Marshal.GetObjectForIUnknown(docData);
                }
                finally
                {
                    Marshal.Release(docData);
                }

                ITextBuffer buffer = _editorAdapters.GetDocumentBuffer(textLines);
                if (buffer == null)
                {
                    return 0;
                }

                IReadOnlyList<TextChange> changes = computeChanges(buffer.CurrentSnapshot.GetText());
                if (changes.Count == 0)
                {
                    return 0;
                }

                // One ITextEdit is one undo step in that file. All positions refer to the text before the edit.
                using (ITextEdit edit = buffer.CreateEdit())
                {
                    foreach (TextChange change in changes)
                    {
                        edit.Replace(new Span(change.Start, change.Length), change.NewText);
                    }

                    edit.Apply();
                }

                if (wasOpen)
                {
                    // Unsaved, so the next lookup must use the buffer and not the file on disk.
                    ResourceIndex.ForFile(filePath).SetLiveText(filePath, buffer.CurrentSnapshot.GetText());
                }
                else if (textLines is IVsPersistDocData persist)
                {
                    persist.SaveDocData(VSSAVEFLAGS.VSSAVE_SilentSave, out string _, out int _);
                }

                return changes.Count;
            }
            finally
            {
                if (Marshal.IsComObject(invisibleEditor))
                {
                    Marshal.ReleaseComObject(invisibleEditor);
                }
            }
        }
    }
}
