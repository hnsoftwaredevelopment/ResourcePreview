using System;
using System.ComponentModel.Composition;
using System.Windows;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ResourcePreview.Preview
{
    /// <summary>
    /// Converts <see cref="ResourcePreviewData"/> into a UIElement for the Quick Info tooltip.
    /// Visual Studio calls CreateViewElement on the UI thread.
    /// </summary>
    [Export(typeof(IViewElementFactory))]
    [Name("XAML Resource Preview element factory")]
    [TypeConversion(from: typeof(ResourcePreviewData), to: typeof(UIElement))]
    [Order(Before = "Default")]
    internal sealed class ResourcePreviewElementFactory : IViewElementFactory
    {
        public TView CreateViewElement<TView>(ITextView textView, object model) where TView : class
        {
            if (typeof(TView) != typeof(UIElement))
            {
                throw new ArgumentException($"Unsupported view type {typeof(TView)}", nameof(TView));
            }

            return PreviewRenderer.Render((ResourcePreviewData)model) as TView;
        }
    }
}
