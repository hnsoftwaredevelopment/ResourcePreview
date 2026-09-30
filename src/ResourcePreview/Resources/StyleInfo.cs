using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace ResourcePreview.Resources
{
    /// <summary>The kind of value a setter has.</summary>
    internal enum SetterValueKind
    {
        /// <summary>Plain text: "8,4", "15", "Bold".</summary>
        Text,

        /// <summary>A color literal: "White", "#FF0000". Shown with a color swatch.</summary>
        Color,

        /// <summary>{StaticResource X} or {DynamicResource X}. Shown with a small preview of X when possible.</summary>
        ResourceReference,

        /// <summary>An element in Setter.Value, such as ControlTemplate. Only the type is shown.</summary>
        Element,
    }

    /// <summary>One row in the style preview: property = value.</summary>
    internal sealed class SetterInfo
    {
        public SetterInfo(string property, string valueText, SetterValueKind kind, string inheritedFrom = null)
        {
            Property = property;
            ValueText = valueText;
            Kind = kind;
            InheritedFrom = inheritedFrom;
        }

        public string Property { get; }

        /// <summary>Display text: "8,4", "White", "AccentBrush", "ControlTemplate".</summary>
        public string ValueText { get; }

        public SetterValueKind Kind { get; }

        /// <summary>Null for the style's own setters, otherwise the key of the base style it comes from.</summary>
        public string InheritedFrom { get; }

        /// <summary>For a ResourceReference: the resource type, such as "SolidColorBrush". Null if not found.</summary>
        public string ResourceTypeName { get; internal set; }

        /// <summary>For a ResourceReference: complete ResourceDictionary XAML for a small preview (key = ValueText).</summary>
        public string ResourceXaml { get; internal set; }

        /// <summary>For a color, or a SolidColorBrush with a literal color: the color as text ("#FF002BBD").</summary>
        public string ColorText { get; internal set; }
    }

    /// <summary>Everything the tooltip shows about a Style. Text only, so safe on any thread.</summary>
    internal sealed class StyleInfo
    {
        public StyleInfo(string targetType, string basedOn, IReadOnlyList<SetterInfo> setters, IReadOnlyList<string> triggers)
        {
            TargetType = targetType;
            BasedOn = basedOn;
            Setters = setters;
            Triggers = triggers;
        }

        /// <summary>"Button", or "local:MyButton" for a custom control. Null without TargetType.</summary>
        public string TargetType { get; }

        /// <summary>The base style key, or the type for BasedOn="{x:Type Button}". Null without BasedOn.</summary>
        public string BasedOn { get; }

        /// <summary>Own setters first, then non-overridden setters from base styles in chain order.</summary>
        public IReadOnlyList<SetterInfo> Setters { get; }

        /// <summary>One name per trigger: the property, binding path, "A + B" or event.</summary>
        public IReadOnlyList<string> Triggers { get; }
    }

    /// <summary>
    /// Reads a Style from XML. Avoids XamlReader on purpose: styles for custom controls
    /// (TargetType="local:MyButton") would fail to load, and a setter table needs no live style.
    /// </summary>
    internal static class StyleInfoBuilder
    {
        private static readonly Regex ReferencePattern =
            new Regex(@"^\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?(?<key>[^\s,}{]+)\s*\}$", RegexOptions.Compiled);

        private static readonly Regex TypePattern =
            new Regex(@"^\{\s*x:Type\s+(?:TypeName\s*=\s*)?(?<type>[^\s}]+)\s*\}$", RegexOptions.Compiled);

        private static readonly Regex NamedPathPattern =
            new Regex(@"\bPath\s*=\s*(?<path>[^\s,}]+)", RegexOptions.Compiled);

        private static readonly Regex HexColorPattern =
            new Regex(@"^#([0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

        public static bool IsStyle(ResourceEntry entry) => entry.Element.Name.LocalName == "Style";

        public static StyleInfo Build(ResourceIndex index, ResourceEntry style, CancellationToken cancellationToken)
        {
            XElement element = style.Element;
            string basedOn = ReadBasedOn(element, out bool basedOnIsKey);

            var setters = new List<SetterInfo>();
            var seenProperties = new HashSet<string>(StringComparer.Ordinal);
            AddSetters(index, style, element, inheritedFrom: null, setters, seenProperties, cancellationToken);

            // Walk the BasedOn chain. A property already seen is overridden, so skip it.
            var visited = new HashSet<string>(StringComparer.Ordinal) { style.Key };
            string nextKey = basedOnIsKey ? basedOn : null;
            ResourceEntry current = style;
            while (nextKey != null && visited.Add(nextKey))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResourceEntry baseStyle = index.Find(nextKey, current.FilePath, cancellationToken).FirstOrDefault(IsStyle);
                if (baseStyle == null)
                {
                    break;
                }

                AddSetters(index, baseStyle, baseStyle.Element, baseStyle.Key, setters, seenProperties, cancellationToken);
                nextKey = ReadBasedOn(baseStyle.Element, out bool isKey) is string k && isKey ? k : null;
                current = baseStyle;
            }

            return new StyleInfo(ReadTargetType(element), basedOn, setters, ReadTriggers(element));
        }

        /// <summary>"{x:Type Button}" becomes "Button", "Button" stays "Button", "{x:Type local:MyButton}" becomes "local:MyButton".</summary>
        internal static string ReadTargetType(XElement style) => ReadTypeAttribute(style, "TargetType");

        /// <summary>Same as <see cref="ReadTargetType"/>, for any type attribute such as DataType.</summary>
        internal static string ReadTypeAttribute(XElement element, string attributeName)
        {
            string value = (string)element.Attribute(attributeName);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            Match match = TypePattern.Match(value.Trim());
            return match.Success ? match.Groups["type"].Value : value.Trim();
        }

        private static string ReadBasedOn(XElement style, out bool isKey)
        {
            isKey = false;
            string value = ((string)style.Attribute("BasedOn"))?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            Match reference = ReferencePattern.Match(value);
            if (reference.Success)
            {
                isKey = true;
                return reference.Groups["key"].Value;
            }

            // BasedOn="{x:Type Button}" refers to the implicit Button style, which has no key to look up.
            Match type = TypePattern.Match(value);
            return type.Success ? type.Groups["type"].Value : value;
        }

        private static void AddSetters(
            ResourceIndex index,
            ResourceEntry source,
            XElement style,
            string inheritedFrom,
            List<SetterInfo> result,
            HashSet<string> seenProperties,
            CancellationToken cancellationToken)
        {
            // Direct children only. Setters inside Style.Triggers belong to a trigger.
            foreach (XElement setter in style.Elements().Where(e => e.Name.LocalName == "Setter"))
            {
                string property = (string)setter.Attribute("Property");
                if (string.IsNullOrEmpty(property) || setter.Attribute("TargetName") != null || !seenProperties.Add(property))
                {
                    continue;
                }

                result.Add(ReadSetter(index, source, setter, property, inheritedFrom, cancellationToken));
            }
        }

        private static SetterInfo ReadSetter(ResourceIndex index, ResourceEntry source, XElement setter, string property, string inheritedFrom, CancellationToken cancellationToken)
        {
            string value = ((string)setter.Attribute("Value"))?.Trim();

            if (value == null)
            {
                // Element syntax: <Setter.Value><ControlTemplate ... /></Setter.Value>
                XElement valueElement = setter.Elements().FirstOrDefault(e => e.Name.LocalName == "Setter.Value")?.Elements().FirstOrDefault();
                if (valueElement == null)
                {
                    return new SetterInfo(property, string.Empty, SetterValueKind.Text, inheritedFrom);
                }

                var info = new SetterInfo(property, valueElement.Name.LocalName, SetterValueKind.Element, inheritedFrom);
                if (valueElement.Name.LocalName == "SolidColorBrush" && TryReadLiteralColor((string)valueElement.Attribute("Color"), out string color))
                {
                    info.ColorText = color;
                }

                return info;
            }

            Match reference = ReferencePattern.Match(value);
            if (reference.Success)
            {
                string key = reference.Groups["key"].Value;
                var info = new SetterInfo(property, key, SetterValueKind.ResourceReference, inheritedFrom);
                ResourceEntry target = index.Find(key, source.FilePath, cancellationToken).FirstOrDefault();
                if (target != null)
                {
                    info.ResourceTypeName = target.ElementTypeName;
                    // Resources with custom types (local:...) cannot load, so show text without a preview.
                    PreviewXaml built = PreviewXamlBuilder.Build(index, target, cancellationToken);
                    info.ResourceXaml = built.UnloadableType == null ? built.Xaml : null;
                    if (target.ElementTypeName == "SolidColorBrush" && TryReadLiteralColor((string)target.Element.Attribute("Color"), out string color))
                    {
                        info.ColorText = color;
                    }
                }

                return info;
            }

            if (TryReadLiteralColor(value, out string literal) && IsColorProperty(property))
            {
                return new SetterInfo(property, value, SetterValueKind.Color, inheritedFrom) { ColorText = literal };
            }

            return new SetterInfo(property, value, SetterValueKind.Text, inheritedFrom);
        }

        /// <summary>
        /// Background="Red" is a color, Content="Red" is text. Only properties whose name suggests
        /// a brush get a color swatch.
        /// </summary>
        private static bool IsColorProperty(string property)
        {
            string name = property.Contains(".") ? property.Substring(property.LastIndexOf('.') + 1) : property;
            return name.EndsWith("Background", StringComparison.Ordinal)
                || name.EndsWith("Foreground", StringComparison.Ordinal)
                || name.EndsWith("Brush", StringComparison.Ordinal)
                || name.EndsWith("Color", StringComparison.Ordinal)
                || name == "Fill"
                || name == "Stroke";
        }

        /// <summary>
        /// Accepts "#RGB", "#ARGB", "#RRGGBB", "#AARRGGBB" or a known color name.
        /// Using System.Windows.Media.Colors is safe off the UI thread because Color is a plain struct.
        /// </summary>
        internal static bool TryReadLiteralColor(string value, out string colorText)
        {
            colorText = null;
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("{", StringComparison.Ordinal))
            {
                return false;
            }

            value = value.Trim();
            if (HexColorPattern.IsMatch(value) || typeof(System.Windows.Media.Colors).GetProperty(value) != null)
            {
                colorText = value;
                return true;
            }

            return false;
        }

        /// <summary>A short name per trigger. See the tests for all trigger kinds.</summary>
        internal static IReadOnlyList<string> ReadTriggers(XElement style)
        {
            XElement triggers = style.Elements().FirstOrDefault(e => e.Name.LocalName == "Style.Triggers");
            if (triggers == null)
            {
                return Array.Empty<string>();
            }

            return triggers.Elements().Select(DescribeTrigger).Where(n => !string.IsNullOrEmpty(n)).ToList();
        }

        private static string DescribeTrigger(XElement trigger)
        {
            switch (trigger.Name.LocalName)
            {
                case "Trigger":
                    return (string)trigger.Attribute("Property");

                case "DataTrigger":
                    return BindingPath((string)trigger.Attribute("Binding"));

                case "EventTrigger":
                    return (string)trigger.Attribute("RoutedEvent");

                case "MultiTrigger":
                    return string.Join(" + ", Conditions(trigger).Select(c => (string)c.Attribute("Property")).Where(p => p != null));

                case "MultiDataTrigger":
                    return string.Join(" + ", Conditions(trigger).Select(c => BindingPath((string)c.Attribute("Binding"))).Where(p => p != null));

                default:
                    return trigger.Name.LocalName;
            }
        }

        private static IEnumerable<XElement> Conditions(XElement trigger) =>
            trigger.Elements().Where(e => e.Name.LocalName.EndsWith(".Conditions", StringComparison.Ordinal))
                .SelectMany(c => c.Elements().Where(e => e.Name.LocalName == "Condition"));

        /// <summary>
        /// "{Binding IsBusy}", "{Binding Path=IsBusy, Mode=OneWay}" and
        /// "{Binding RelativeSource={RelativeSource Self}, Path=IsBusy}" become "IsBusy".
        /// Without a path (for example only a Converter) the result is "Binding".
        /// </summary>
        internal static string BindingPath(string binding)
        {
            if (string.IsNullOrWhiteSpace(binding))
            {
                return null;
            }

            Match named = NamedPathPattern.Match(binding);
            if (named.Success)
            {
                return named.Groups["path"].Value;
            }

            string inner = binding.Trim().TrimStart('{').TrimEnd('}').Trim();
            if (!inner.StartsWith("Binding", StringComparison.Ordinal))
            {
                return binding.Trim();
            }

            string first = inner.Substring("Binding".Length).Split(',')[0].Trim();
            return first.Length == 0 || first.Contains("=") ? "Binding" : first;
        }
    }
}
