using System;
using System.Collections.Generic;
using ResourcePreview.Resources;

namespace ResourcePreview.Preview
{
    /// <summary>
    /// Quick Info content model. Holds no WPF objects so it can be created on a background thread;
    /// <see cref="ResourcePreviewElementFactory"/> turns it into a UIElement on the UI thread.
    /// </summary>
    internal sealed class ResourcePreviewData
    {
        public ResourcePreviewData(string key, string elementTypeName, string xaml, IReadOnlyList<string> missingKeys, Action navigate, StyleInfo style = null, TemplateTree templateTree = null, string unloadableType = null)
        {
            Key = key;
            ElementTypeName = elementTypeName;
            Xaml = xaml;
            MissingKeys = missingKeys;
            Navigate = navigate;
            Style = style;
            TemplateTree = templateTree;
            UnloadableType = unloadableType;
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

        /// <summary>A type in the XAML that Visual Studio cannot load, such as "local:Book". Parsing is skipped when set.</summary>
        public string UnloadableType { get; }
    }
}
