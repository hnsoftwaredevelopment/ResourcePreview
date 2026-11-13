using System;
using System.Collections.Generic;
using System.Threading;
using ResourcePreview.Resources;

namespace ResourcePreview.Preview
{
    /// <summary>
    /// Quick Info content model. Holds no WPF objects so it can be created on a background thread;
    /// <see cref="ResourcePreviewElementFactory"/> turns it into a UIElement on the UI thread.
    /// </summary>
    internal sealed class ResourcePreviewData
    {
        public ResourcePreviewData(string key, string elementTypeName, string xaml, IReadOnlyList<string> missingKeys, Action navigate, StyleInfo style = null, TemplateTree templateTree = null, string unloadableType = null, TemplateSample sample = null)
        {
            Sample = sample;
            Key = key;
            ElementTypeName = elementTypeName;
            Xaml = xaml;
            MissingKeys = missingKeys;
            Navigate = navigate;
            Style = style;
            TemplateTree = templateTree;
            UnloadableType = unloadableType;
        }

        /// <summary>
        /// Collects everything the preview needs for one definition: the XAML with its dependencies,
        /// and for a style or template the table or tree from the XML. Runs on any thread.
        /// </summary>
        public static ResourcePreviewData Create(ResourceIndex index, ResourceEntry target, Action navigate, CancellationToken cancellationToken)
        {
            StyleInfo style = StyleInfoBuilder.IsStyle(target) ? StyleInfoBuilder.Build(index, target, cancellationToken) : null;
            TemplateTree templateTree = TemplateTreeBuilder.IsTemplate(target) ? TemplateTreeBuilder.Build(target.Element) : null;

            // A DataTemplate or ItemsPanelTemplate is rendered from a copy with own types as boxes. The tree
            // keeps showing the original.
            TemplateSample sample = TemplateSampleBuilder.CanRender(target) ? TemplateSampleBuilder.Prepare(index, target, cancellationToken) : null;
            PreviewXaml previewXaml = PreviewXamlBuilder.Build(index, sample?.Entry ?? target, cancellationToken);
            return new ResourcePreviewData(
                target.Key, target.ElementTypeName, previewXaml.Xaml, previewXaml.MissingKeys,
                navigate, style, templateTree, previewXaml.UnloadableType, sample);
        }

        public string Key { get; }

        /// <summary>XAML element name, for example "GeometryGroup". Used in error messages only.</summary>
        public string ElementTypeName { get; }

        /// <summary>Complete ResourceDictionary as XAML text.</summary>
        public string Xaml { get; }

        public IReadOnlyList<string> MissingKeys { get; }

        /// <summary>Navigates to the definition. Call on the UI thread only.</summary>
        public Action Navigate { get; }

        /// <summary>Set for a Style. The renderer then shows a setter table instead of tiles.</summary>
        public StyleInfo Style { get; }

        /// <summary>Set for a DataTemplate or ControlTemplate: its element tree as text.</summary>
        public TemplateTree TemplateTree { get; }

        /// <summary>For a DataTemplate or ItemsPanelTemplate: the bindings and the boxes of the rendered sample. Null otherwise.</summary>
        public TemplateSample Sample { get; }

        /// <summary>A type in the XAML that Visual Studio cannot load, such as "local:Book". Parsing is skipped when set.</summary>
        public string UnloadableType { get; }
    }
}
