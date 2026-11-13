using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace ResourcePreview.Resources
{
    /// <summary>A {Binding Path} in a template, with the element and property it sets.</summary>
    internal sealed class SampleBinding
    {
        public SampleBinding(string path, Type elementType, string property)
        {
            Path = path;
            ElementType = elementType;
            Property = property;
        }

        /// <summary>"Value", "Author.Name", or empty for {Binding} without a path.</summary>
        public string Path { get; }

        /// <summary>The WPF type of the element, such as TextBox. Null when unknown.</summary>
        public Type ElementType { get; }

        /// <summary>"Text", "IsReadOnly", or "Grid.Row" for an attached property.</summary>
        public string Property { get; }
    }

    /// <summary>A template made ready to render: what changed, and which data it binds to.</summary>
    internal sealed class TemplateSample
    {
        public TemplateSample(ResourceEntry entry, IReadOnlyList<SampleBinding> bindings, IReadOnlyList<string> placeholders)
        {
            Entry = entry;
            Bindings = bindings;
            Placeholders = placeholders;
        }

        /// <summary>The template with placeholders, for <see cref="PreviewXamlBuilder.Build"/>.</summary>
        public ResourceEntry Entry { get; }

        public IReadOnlyList<SampleBinding> Bindings { get; }

        /// <summary>Types shown as a box, such as "local:Rating". Distinct, in file order.</summary>
        public IReadOnlyList<string> Placeholders { get; }
    }

    /// <summary>
    /// Prepares a DataTemplate or ItemsPanelTemplate for a rendered preview. Works on a copy of the XML:
    /// - an element Visual Studio cannot load (local:Rating) becomes a dashed box with the type name, with the
    ///   same Margin, size, alignment and Grid or DockPanel position;
    /// - a converter that cannot load is left out of the binding, so the binding shows the raw value;
    /// - other attributes with own types ({x:Static local:Icons.Save}, local:Help.Text) and event handlers are left out;
    /// - a design-time value such as d:Text="120" replaces the real attribute, as in the XAML designer.
    /// Then it lists the bindings, so the renderer can make sample data for them.
    /// Uses XML and reflection only, so it runs on a background thread.
    /// </summary>
    internal static class TemplateSampleBuilder
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace Designer = "http://schemas.microsoft.com/expression/blend/2008";

        /// <summary>The templates that get a rendered sample. ControlTemplate has its own (LiveSampleRenderer).</summary>
        public static bool CanRender(ResourceEntry entry) =>
            entry.ElementTypeName == "DataTemplate" || entry.ElementTypeName == "ItemsPanelTemplate";

        // Kept on a placeholder so it takes the same place in the layout.
        private static readonly HashSet<string> LayoutAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Margin", "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
            "HorizontalAlignment", "VerticalAlignment",
            "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan", "DockPanel.Dock", "Canvas.Left", "Canvas.Top",
        };

        // d: attributes that describe the designer, not a value of the element.
        private static readonly HashSet<string> DesignerOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "DataContext", "DesignWidth", "DesignHeight", "DesignInstance", "DesignData", "DesignSource", "IsDesignTimeCreatable",
        };

        private static readonly Regex BindingPattern = new Regex(@"^\{\s*Binding\b", RegexOptions.Compiled);

        private static readonly Regex PrefixedNamePattern =
            new Regex(@"(?<![\w.:/])(?<prefix>[A-Za-z_][\w-]*):(?<name>[A-Za-z_][\w.]*)", RegexOptions.Compiled);

        public static TemplateSample Prepare(ResourceIndex index, ResourceEntry entry, CancellationToken cancellationToken)
        {
            var copy = new XElement(entry.Element);
            var placeholders = new List<string>();
            var removedNames = new HashSet<string>(StringComparer.Ordinal);

            string NamespaceOf(XElement node, string prefix)
            {
                XNamespace declared = node.GetNamespaceOfPrefix(prefix);
                if (declared != null)
                {
                    return declared.NamespaceName;
                }

                return entry.Namespaces.TryGetValue(prefix, out string outer) ? outer : null;
            }

            bool HasOwnType(XElement node, string value) =>
                PrefixedNamePattern.Matches(value).Cast<Match>().Any(m =>
                {
                    string ns = NamespaceOf(node, m.Groups["prefix"].Value);
                    return ns != null && !PreviewXamlBuilder.IsLoadableNamespace(ns);
                });

            ApplyDesignValues(copy);

            // Children first would change the list while we walk it, so take a snapshot. Elements inside a
            // replaced element are skipped: they went with it.
            foreach (XElement node in copy.DescendantsAndSelf().ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node != copy && node.Ancestors().LastOrDefault() != copy)
                {
                    continue; // inside an element that was replaced or removed
                }

                if (IsOwnType(node))
                {
                    if (node == copy)
                    {
                        break; // the template itself; PreviewXamlBuilder reports it
                    }

                    ReplaceOrRemove(node, entry, placeholders, removedNames);
                    continue;
                }

                foreach (XAttribute attribute in node.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList())
                {
                    CleanAttribute(index, entry, node, attribute, HasOwnType, cancellationToken);
                }
            }

            RemoveTargetsOf(copy, removedNames);

            var prepared = new ResourceEntry(entry.Key, copy, entry.FilePath, entry.LineNumber, entry.Column, entry.Namespaces, entry.IsRootScope, entry.KeyText);
            return new TemplateSample(prepared, FindBindings(copy), placeholders.Distinct().ToList());
        }

        /// <summary>d:Text="120" becomes Text="120", as in the XAML designer. Markup such as {d:SampleData} is skipped.</summary>
        private static void ApplyDesignValues(XElement template)
        {
            foreach (XElement node in template.DescendantsAndSelf())
            {
                foreach (XAttribute design in node.Attributes().Where(a => a.Name.Namespace == Designer).ToList())
                {
                    string name = design.Name.LocalName;
                    if (DesignerOnly.Contains(name) || design.Value.TrimStart().StartsWith("{d:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    node.SetAttributeValue(XNamespace.None + name, design.Value);
                    design.Remove();
                }
            }
        }

        private static bool IsOwnType(XElement node) =>
            !PreviewXamlBuilder.IsLoadableNamespace(node.Name.NamespaceName)
            || PreviewXamlBuilder.IsBlockedType(node.Name.LocalName);

        /// <summary>
        /// A visual element becomes a placeholder. A resource or property element (local:Rating.Value,
        /// a converter in DataTemplate.Resources) is removed: a box makes no sense there.
        /// </summary>
        private static void ReplaceOrRemove(XElement node, ResourceEntry entry, List<string> placeholders, HashSet<string> removedNames)
        {
            foreach (XElement named in node.DescendantsAndSelf())
            {
                string name = (string)named.Attribute(ResourceIndex.XamlNamespace + "Name") ?? (string)named.Attribute("Name");
                if (name != null)
                {
                    removedNames.Add(name);
                }
            }

            XElement parent = node.Parent;
            bool isProperty = node.Name.LocalName.Contains(".");
            bool inResources = parent != null && parent.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal);
            if (isProperty || inResources || node.Attribute(ResourceIndex.XamlNamespace + "Key") != null)
            {
                node.Remove();
                return;
            }

            string typeName = DisplayName(node, entry);
            placeholders.Add(typeName);
            node.ReplaceWith(Placeholder(node, typeName));
        }

        private static string DisplayName(XElement node, ResourceEntry entry)
        {
            string prefix = node.GetPrefixOfNamespace(node.Name.Namespace)
                ?? entry.Namespaces.FirstOrDefault(p => p.Key.Length > 0 && p.Value == node.Name.NamespaceName).Key;
            return string.IsNullOrEmpty(prefix) ? node.Name.LocalName : prefix + ":" + node.Name.LocalName;
        }

        /// <summary>A dashed box with the type name, in the place of the element.</summary>
        internal static XElement Placeholder(XElement original, string typeName)
        {
            var box = new XElement(Presentation + "Grid", new XAttribute("MinWidth", "40"), new XAttribute("MinHeight", "20"));
            foreach (XAttribute attribute in original.Attributes().Where(a => a.Name.Namespace == XNamespace.None && LayoutAttributes.Contains(a.Name.LocalName)))
            {
                box.SetAttributeValue(attribute.Name, attribute.Value);
            }

            box.Add(new XElement(Presentation + "Rectangle",
                new XAttribute("Stroke", "#FF808080"),
                new XAttribute("StrokeThickness", "1"),
                new XAttribute("StrokeDashArray", "3 2")));
            // The name as content, not as Text="local:Rating": the type check reads attributes and would see a type there.
            box.Add(new XElement(Presentation + "TextBlock",
                typeName,
                new XAttribute("Margin", "6,2"),
                new XAttribute("FontSize", "11"),
                new XAttribute("Opacity", "0.7"),
                new XAttribute("HorizontalAlignment", "Center"),
                new XAttribute("VerticalAlignment", "Center")));
            return box;
        }

        private static void CleanAttribute(
            ResourceIndex index,
            ResourceEntry entry,
            XElement node,
            XAttribute attribute,
            Func<XElement, string, bool> hasOwnType,
            CancellationToken cancellationToken)
        {
            if (attribute.Parent == null || attribute.Name == ResourceIndex.XamlNamespace + "Key" || attribute.Name == ResourceIndex.XamlNamespace + "Name")
            {
                return; // already removed with its converter, or the key and name, which PreviewXamlBuilder handles
            }

            // local:Help.Text="..." on a WPF element.
            if (attribute.Name.Namespace != XNamespace.None && !PreviewXamlBuilder.IsLoadableNamespace(attribute.Name.NamespaceName))
            {
                attribute.Remove();
                return;
            }

            if (attribute.Name.Namespace == XNamespace.None && IsEvent(node, attribute.Name.LocalName))
            {
                attribute.Remove(); // Click="OnClick": XamlReader cannot connect it without code-behind
                return;
            }

            string value = attribute.Value;

            // <Binding Converter="{StaticResource PriceConverter}" /> in element syntax.
            if (node.Name.LocalName == "Binding" && attribute.Name.LocalName == "Converter")
            {
                if (!IsLoadableConverter(index, entry, node, value, hasOwnType, cancellationToken))
                {
                    node.Attribute("ConverterParameter")?.Remove();
                    attribute.Remove();
                }

                return;
            }

            if (BindingPattern.IsMatch(value))
            {
                value = WithoutUnloadableConverter(index, entry, node, value, hasOwnType, cancellationToken);
                attribute.Value = value;
            }

            if (hasOwnType(node, value))
            {
                attribute.Remove();
            }
        }

        /// <summary>True when the element has an event with this name: Click, or Button.Click as an attached event.</summary>
        private static bool IsEvent(XElement node, string name)
        {
            if (node.Name.Namespace != Presentation)
            {
                return false;
            }

            int dot = name.LastIndexOf('.');
            Type owner = ResourceMatcher.FindWpfType(dot > 0 ? name.Substring(0, dot) : node.Name.LocalName);
            string eventName = dot > 0 ? name.Substring(dot + 1) : name;
            return owner != null && owner.GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) != null;
        }

        /// <summary>"{Binding Price, Converter={StaticResource PriceConverter}}" becomes "{Binding Price}" when the converter cannot load.</summary>
        private static string WithoutUnloadableConverter(
            ResourceIndex index,
            ResourceEntry entry,
            XElement node,
            string markup,
            Func<XElement, string, bool> hasOwnType,
            CancellationToken cancellationToken)
        {
            List<string> parts = SplitMarkup(markup);
            if (parts == null)
            {
                return markup;
            }

            string converter = parts.Skip(1).Select(p => NamedValue(p, "Converter")).FirstOrDefault(v => v != null);
            if (converter == null || IsLoadableConverter(index, entry, node, converter, hasOwnType, cancellationToken))
            {
                return markup;
            }

            IEnumerable<string> kept = parts.Take(1).Concat(parts.Skip(1).Where(p => NamedValue(p, "Converter") == null && NamedValue(p, "ConverterParameter") == null));
            return "{" + string.Join(", ", kept) + "}";
        }

        private static bool IsLoadableConverter(
            ResourceIndex index,
            ResourceEntry entry,
            XElement node,
            string converter,
            Func<XElement, string, bool> hasOwnType,
            CancellationToken cancellationToken)
        {
            Match reference = Regex.Match(converter.Trim(), @"^\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?" + ResourceKeys.KeyPattern + @"\s*\}$");
            if (reference.Success)
            {
                string key = ResourceKeys.Normalize(reference.Groups["key"].Value);
                ResourceEntry definition = key == null ? null : index.Find(key, entry.FilePath, cancellationToken).FirstOrDefault();
                return definition != null && PreviewXamlBuilder.FindUnloadableType(definition) == null;
            }

            return !hasOwnType(node, converter);
        }

        /// <summary>The value of "Name=value", or null when the part has another name.</summary>
        private static string NamedValue(string part, string name)
        {
            int equals = part.IndexOf('=');
            return equals > 0 && part.Substring(0, equals).Trim() == name ? part.Substring(equals + 1).Trim() : null;
        }

        /// <summary>
        /// "{Binding Price, Mode=OneWay, Converter={StaticResource C}}" becomes
        /// ["Binding Price", "Mode=OneWay", "Converter={StaticResource C}"]. Commas inside braces or quotes
        /// do not split. Null when the braces do not match. Internal for testing.
        /// </summary>
        internal static List<string> SplitMarkup(string markup)
        {
            string text = markup.Trim();
            if (text.Length < 2 || text[0] != '{' || text[text.Length - 1] != '}')
            {
                return null;
            }

            var parts = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            char quote = '\0';
            foreach (char c in text.Substring(1, text.Length - 2))
            {
                if (quote != '\0')
                {
                    quote = c == quote ? '\0' : quote;
                }
                else if (c == '\'' || c == '"')
                {
                    quote = c;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            if (depth != 0 || quote != '\0')
            {
                return null;
            }

            parts.Add(current.ToString().Trim());
            return parts;
        }

        /// <summary>Setters and animations aimed at an element that became a placeholder would fail to load.</summary>
        private static void RemoveTargetsOf(XElement template, HashSet<string> removedNames)
        {
            if (removedNames.Count == 0)
            {
                return;
            }

            foreach (XElement node in template.Descendants().ToList())
            {
                string target = (string)node.Attribute("TargetName") ?? (string)node.Attribute("Storyboard.TargetName");
                if (target != null && removedNames.Contains(target) && node.Parent != null)
                {
                    node.Remove();
                }
            }
        }

        /// <summary>
        /// The bindings to make sample data for. Skipped: bindings to something other than the data
        /// (RelativeSource, ElementName, Source), and paths with an index or an attached property.
        /// </summary>
        internal static IReadOnlyList<SampleBinding> FindBindings(XElement template)
        {
            var result = new List<SampleBinding>();
            foreach (XElement node in template.Descendants().Where(n => n.Name.Namespace == Presentation && !n.Name.LocalName.Contains(".")))
            {
                Type type = ResourceMatcher.FindWpfType(node.Name.LocalName);
                foreach (XAttribute attribute in node.Attributes().Where(a => a.Name.Namespace == XNamespace.None))
                {
                    string value = attribute.Value;
                    if (!BindingPattern.IsMatch(value) || Regex.IsMatch(value, @"\b(RelativeSource|ElementName|Source)\s*="))
                    {
                        continue;
                    }

                    string path = StyleInfoBuilder.BindingPath(value);
                    path = path == "Binding" ? string.Empty : path;
                    if (path.StartsWith("(", StringComparison.Ordinal) || path.Contains("["))
                    {
                        continue;
                    }

                    result.Add(new SampleBinding(path, type, attribute.Name.LocalName));
                }
            }

            return result;
        }
    }
}
