using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace ResourcePreview.Resources
{
    /// <summary>One line in the tree: "    TextBlock   Text = {Binding Title}".</summary>
    internal sealed class TemplateTreeLine
    {
        public TemplateTreeLine(int depth, string element, string details)
        {
            Depth = depth;
            Element = element;
            Details = details;
        }

        /// <summary>0 for the top-level element in the template.</summary>
        public int Depth { get; }

        /// <summary>"TextBlock", or "local:Rating" with prefix.</summary>
        public string Element { get; }

        /// <summary>"Text = {Binding Title} · x:Name = TitleText", or null.</summary>
        public string Details { get; }
    }

    /// <summary>The elements of a DataTemplate or ControlTemplate as text.</summary>
    internal sealed class TemplateTree
    {
        public TemplateTree(string templateKind, string dataType, string targetType, IReadOnlyList<TemplateTreeLine> lines, int hiddenCount)
        {
            TemplateKind = templateKind;
            DataType = dataType;
            TargetType = targetType;
            Lines = lines;
            HiddenCount = hiddenCount;
        }

        /// <summary>"DataTemplate", "ControlTemplate", "HierarchicalDataTemplate", "ItemsPanelTemplate".</summary>
        public string TemplateKind { get; }

        /// <summary>DataType of a DataTemplate, without {x:Type}. Null if missing.</summary>
        public string DataType { get; }

        /// <summary>TargetType of a ControlTemplate, without {x:Type}. Null if missing.</summary>
        public string TargetType { get; }

        public IReadOnlyList<TemplateTreeLine> Lines { get; }

        /// <summary>Number of elements left out because the tree is too large.</summary>
        public int HiddenCount { get; }
    }

    /// <summary>
    /// Turns a template into an indented element list. A rendered DataTemplate without data shows
    /// empty bindings, so a text tree is more useful. Uses XML only, no WPF, so it runs on a
    /// background thread and handles custom types such as local:Rating.
    /// </summary>
    internal static class TemplateTreeBuilder
    {
        internal const int MaxLines = 20;
        internal const int MaxDepth = 10;
        private const int MaxValueLength = 60;

        private static readonly XNamespace XamlNamespace = ResourceIndex.XamlNamespace;
        private static readonly XNamespace DesignerNamespace = "http://schemas.microsoft.com/expression/blend/2008";
        private static readonly XNamespace CompatibilityNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";

        /// <summary>Literal-text attributes worth showing because they describe visible content.</summary>
        private static readonly HashSet<string> LiteralAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Text", "Content", "Header",
        };

        public static bool IsTemplate(ResourceEntry entry) => IsTemplate(entry.Element);

        internal static bool IsTemplate(XElement element)
        {
            switch (element.Name.LocalName)
            {
                case "DataTemplate":
                case "HierarchicalDataTemplate":
                case "ControlTemplate":
                case "ItemsPanelTemplate":
                    return true;
                default:
                    return false;
            }
        }

        public static TemplateTree Build(XElement template)
        {
            var lines = new List<TemplateTreeLine>();
            int hidden = 0;

            foreach (XElement child in VisualChildren(template))
            {
                Add(child, 0);
            }

            void Add(XElement element, int depth)
            {
                if (lines.Count >= MaxLines || depth > MaxDepth)
                {
                    hidden += CountElements(element);
                    return;
                }

                lines.Add(new TemplateTreeLine(depth, ElementName(element), Details(element)));
                foreach (XElement child in VisualChildren(element))
                {
                    Add(child, depth + 1);
                }
            }

            return new TemplateTree(
                template.Name.LocalName,
                StyleInfoBuilder.ReadTypeAttribute(template, "DataType"),
                StyleInfoBuilder.ReadTypeAttribute(template, "TargetType"),
                lines,
                hidden);
        }

        /// <summary>
        /// Child elements that belong in the tree. Skips property elements such as
        /// &lt;Grid.ColumnDefinitions&gt; and &lt;Border.Background&gt;, which are settings, not visuals.
        /// </summary>
        private static IEnumerable<XElement> VisualChildren(XElement element) =>
            element.Elements().Where(e => !IsPropertyElement(e) && e.Name.Namespace != XamlNamespace);

        private static int CountElements(XElement element) => 1 + VisualChildren(element).Sum(CountElements);

        private static bool IsPropertyElement(XElement element) => element.Name.LocalName.Contains(".");

        private static string ElementName(XElement element)
        {
            string prefix = element.GetPrefixOfNamespace(element.Name.Namespace);
            return string.IsNullOrEmpty(prefix) ? element.Name.LocalName : prefix + ":" + element.Name.LocalName;
        }

        /// <summary>
        /// Bindings, TemplateBindings, x:Name and literal text. Layout and color attributes are left out
        /// to keep lines short.
        /// </summary>
        private static string Details(XElement element)
        {
            var parts = new List<string>();

            foreach (XAttribute attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration
                    || attribute.Name.Namespace == DesignerNamespace
                    || attribute.Name.Namespace == CompatibilityNamespace)
                {
                    continue;
                }

                string name = attribute.Name.LocalName;
                string value = attribute.Value.Trim();

                if (attribute.Name.Namespace == XamlNamespace)
                {
                    if (name == "Name")
                    {
                        parts.Add("x:Name = " + value);
                    }

                    continue;
                }

                if (name == "Name")
                {
                    parts.Add("Name = " + value);
                }
                else if (IsBinding(value))
                {
                    parts.Add(name + " = " + Shorten(value));
                }
                else if (LiteralAttributes.Contains(name) && !value.StartsWith("{", StringComparison.Ordinal))
                {
                    parts.Add(name + " = \"" + Shorten(value) + "\"");
                }
            }

            // Text content, such as <TextBlock>Title</TextBlock>.
            if (!element.HasElements && !string.IsNullOrWhiteSpace(element.Value))
            {
                parts.Add("\"" + Shorten(element.Value.Trim()) + "\"");
            }

            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }

        internal static bool IsBinding(string value) =>
            value.StartsWith("{Binding", StringComparison.Ordinal)
            || value.StartsWith("{TemplateBinding", StringComparison.Ordinal)
            || value.StartsWith("{MultiBinding", StringComparison.Ordinal);

        private static string Shorten(string text) =>
            text.Length <= MaxValueLength ? text : text.Substring(0, MaxValueLength - 1) + "…";
    }
}
