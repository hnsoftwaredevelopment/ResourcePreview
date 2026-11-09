using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities;
using ResourcePreview.Preview;
using ResourcePreview.Resources;

namespace ResourcePreview.IntelliSense
{
    /// <summary>
    /// The tooltip next to the selected IntelliSense item: the same preview as on hover. Only for items
    /// that <see cref="ResourceCompletionSource"/> marked; for all others VS shows its own tooltip.
    /// </summary>
    [Export(typeof(IUIElementProvider<Completion, ICompletionSession>))]
    [Name("XAML Resource Preview Completion ToolTip")]
    [ContentType("XAML")]
    [Order(Before = "Default")]
    internal sealed class ResourceCompletionToolTipProvider : IUIElementProvider<Completion, ICompletionSession>
    {
        public UIElement GetUIElement(Completion itemToRender, ICompletionSession context, UIElementType elementType)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (elementType != UIElementType.Tooltip
                || !itemToRender.Properties.TryGetProperty(typeof(ResourceEntry), out ResourceEntry entry)
                || !itemToRender.Properties.TryGetProperty(typeof(ResourceIndex), out ResourceIndex index))
            {
                return null;
            }

            try
            {
                // No click action: the tooltip of an IntelliSense item cannot be clicked.
                ResourcePreviewData data = ResourcePreviewData.Create(index, entry, null, CancellationToken.None);

                var header = PreviewRenderer.CreateText(entry.ElementTypeName + " " + entry.Key);
                header.FontWeight = FontWeights.SemiBold;
                header.Margin = new Thickness(0);

                var panel = new StackPanel { Margin = new Thickness(2) };
                panel.Children.Add(header);
                panel.Children.Add(PreviewRenderer.Render(data));
                return panel;
            }
            catch (Exception)
            {
                return null; // fall back to the tooltip of the XAML editor
            }
        }
    }
}
