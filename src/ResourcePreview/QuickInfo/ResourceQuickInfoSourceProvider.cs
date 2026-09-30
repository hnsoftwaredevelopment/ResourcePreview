using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using ResourcePreview.Navigation;

namespace ResourcePreview.QuickInfo
{
    /// <summary>
    /// Creates the Quick Info source for .xaml buffers.
    /// "XML" is registered too because some VS versions open loose .xaml files in the XML editor,
    /// so the file extension is checked in <see cref="TryCreateQuickInfoSource"/>.
    /// </summary>
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("XAML Resource Preview Quick Info")]
    [ContentType("XAML")]
    [ContentType("XML")]
    [Order]
    internal sealed class ResourceQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        /// <summary>Resolves the file path of a text buffer.</summary>
        [Import]
        internal ITextDocumentFactoryService TextDocumentFactoryService { get; set; }

        [Import]
        internal SVsServiceProvider ServiceProvider { get; set; }

        [Import]
        internal IVsEditorAdaptersFactoryService EditorAdaptersFactoryService { get; set; }

        /// <summary>Imported instead of using ThreadHelper.JoinableTaskFactory so tests can replace it.</summary>
        [Import]
        internal JoinableTaskContext JoinableTaskContext { get; set; }

        private DefinitionNavigator _navigator;

        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer)
        {
            if (!TextDocumentFactoryService.TryGetTextDocument(textBuffer, out ITextDocument document))
            {
                return null;
            }

            if (!document.FilePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // One source per buffer, stored in the buffer's property bag so it goes away with the buffer.
            return textBuffer.Properties.GetOrCreateSingletonProperty(
                typeof(ResourceQuickInfoSource),
                () => new ResourceQuickInfoSource(textBuffer, document, Navigator, ServiceProvider, JoinableTaskContext.Factory));
        }

        // Shared by all buffers. MEF creates one provider instance, so this is effectively a singleton.
        private DefinitionNavigator Navigator
        {
            get
            {
                return _navigator ?? (_navigator = new DefinitionNavigator(ServiceProvider, EditorAdaptersFactoryService, JoinableTaskContext));
            }
        }
    }
}
