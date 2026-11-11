using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace ResourcePreview.Resources
{
    /// <summary>What a match replaces on the element.</summary>
    internal enum MatchKind
    {
        /// <summary>A Style whose setters equal some of the selected attributes.</summary>
        Style,

        /// <summary>A resource with the same value as one attribute: Fill="White" and a WhiteBrush.</summary>
        Value,
    }

    /// <summary>One existing resource that can replace attributes of the selected element.</summary>
    internal sealed class ResourceMatch
    {
        public ResourceMatch(ResourceEntry entry, MatchKind kind, IReadOnlyList<XamlAttribute> covered, IReadOnlyList<XamlAttribute> remaining, int triggerCount)
        {
            Entry = entry;
            Kind = kind;
            Covered = covered;
            Remaining = remaining;
            TriggerCount = triggerCount;
        }

        public ResourceEntry Entry { get; }

        public string Key => Entry.Key;

        public MatchKind Kind { get; }

        /// <summary>The attributes the resource takes over.</summary>
        public IReadOnlyList<XamlAttribute> Covered { get; }

        /// <summary>For a style: the selected attributes that stay on the element.</summary>
        public IReadOnlyList<XamlAttribute> Remaining { get; }

        /// <summary>Triggers in the style and its base styles. They do more than the setters.</summary>
        public int TriggerCount { get; }
    }

    /// <summary>
    /// "Find Matching Resource": which existing resources the selected attributes could use.
    /// - A Style matches when every setter it has (with BasedOn) is a selected attribute with the same value,
    ///   and its TargetType fits the element. A style that sets more is left out: it would change the look.
    /// - A value resource matches one attribute: Fill="White" and &lt;SolidColorBrush x:Key="WhiteBrush" Color="#FFFFFFFF" /&gt;.
    /// Values are compared after normalizing: White = #FFFFFFFF, 0,0,8,0 = 0 0 8 0, 8 = 8,8,8,8 for a Thickness.
    /// Uses no VS APIs, so all of it is unit tested.
    /// </summary>
    internal static class ResourceMatcher
    {
        private static readonly Regex KeyReference =
            new Regex(@"^\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?(?<key>[^\s,{}]+)\s*\}$", RegexOptions.Compiled);

        private static readonly string[] WpfNamespaces =
        {
            "System.Windows.Controls", "System.Windows.Shapes", "System.Windows.Controls.Primitives",
            "System.Windows.Documents", "System.Windows", "System.Windows.Media",
        };

        private static readonly ConcurrentDictionary<string, Type> WpfTypes = new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);

        // Value resources by local element name, and how to read their value.
        private static readonly HashSet<string> ValueTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "SolidColorBrush", "Color", "Thickness", "CornerRadius", "Double", "FontFamily", "FontWeight", "String",
        };

        /// <summary>
        /// The resources the element at the selection can use, best first: styles that take over all selected
        /// attributes, then styles that take over some, then single values.
        /// </summary>
        /// <param name="visible">The resources the element can see, one per key: <see cref="VisibleEntries"/>.</param>
        public static IReadOnlyList<ResourceMatch> Find(AttributeSelection selection, IReadOnlyList<ResourceEntry> visible, CancellationToken cancellationToken)
        {
            var byKey = new Dictionary<string, ResourceEntry>(StringComparer.Ordinal);
            foreach (ResourceEntry entry in visible)
            {
                if (!byKey.ContainsKey(entry.Key))
                {
                    byKey.Add(entry.Key, entry);
                }
            }

            var styles = new List<ResourceMatch>();
            var values = new List<ResourceMatch>();
            foreach (ResourceEntry entry in byKey.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string type = entry.ElementTypeName;
                if (type == "Style")
                {
                    // An element with a Style cannot get a second one: "Add to style" or "Extract to style" do that.
                    ResourceMatch match = selection.Style == null && selection.CanHaveStyle ? MatchStyle(selection, entry, byKey) : null;
                    if (match != null)
                    {
                        styles.Add(match);
                    }
                }
                else if (ValueTypes.Contains(type))
                {
                    values.AddRange(MatchValue(selection, entry));
                }
            }

            return styles
                .OrderByDescending(m => m.Covered.Count)
                .ThenBy(m => m.TriggerCount)
                .ThenBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                .Concat(values.OrderBy(m => m.Covered[0].Start).ThenBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// The resources an element in <paramref name="file"/> on <paramref name="line"/> can use with a
        /// StaticResource, in lookup order, one per key. In the file itself only resources above the element
        /// count, because a StaticResource does not look ahead.
        /// </summary>
        public static IReadOnlyList<ResourceEntry> VisibleEntries(MergeGraph graph, string file, int line)
        {
            var result = new List<ResourceEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string chainFile in graph.GetLookupChain(file))
            {
                if (!graph.Files.TryGetValue(chainFile, out XamlFileModel model))
                {
                    continue;
                }

                bool isFile = string.Equals(chainFile, file, StringComparison.OrdinalIgnoreCase);
                foreach (ResourceEntry entry in model.Entries.OrderBy(e => e.LineNumber))
                {
                    bool visible = isFile ? entry.LineNumber < line : entry.IsRootScope;
                    if (visible && keys.Add(entry.Key))
                    {
                        result.Add(entry);
                    }
                }
            }

            return result;
        }

        /// <summary>The changes that make the element use the match.</summary>
        public static IReadOnlyList<TextChange> Apply(AttributeSelection selection, ResourceMatch match)
        {
            if (match.Kind == MatchKind.Value)
            {
                XamlAttribute attribute = match.Covered[0];
                return new[] { new TextChange(attribute.ValueStart, attribute.Value.Length, "{StaticResource " + match.Key + "}") };
            }

            var covered = new AttributeSelection(selection.ElementName, selection.TagStart, match.Covered, null, null);
            return StylePlanner.PointElementAtStyle(covered, match.Key);
        }

        // --- Styles ---

        private static ResourceMatch MatchStyle(AttributeSelection selection, ResourceEntry style, IReadOnlyDictionary<string, ResourceEntry> byKey)
        {
            string targetType = StyleInfoBuilder.ReadTargetType(style.Element);
            if (targetType == null || !Fits(selection.ElementName, targetType))
            {
                return null;
            }

            // Own setters win over those of the base style, as in WPF.
            var setters = new Dictionary<string, string>(StringComparer.Ordinal);
            int triggers = 0;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (ResourceEntry current = style; current != null && visited.Add(current.Key);)
            {
                XElement element = current.Element;
                if (element.Elements().Any(e => e.Name.LocalName == "EventSetter"))
                {
                    return null; // adds event handlers: more than the attributes do
                }

                foreach (XElement setter in element.Elements().Where(e => e.Name.LocalName == "Setter"))
                {
                    string property = (string)setter.Attribute("Property");
                    if (string.IsNullOrEmpty(property) || setter.Attribute("TargetName") != null)
                    {
                        continue;
                    }

                    property = PropertyName(property);
                    if (!setters.ContainsKey(property))
                    {
                        // Element syntax (<Setter.Value>) never equals an attribute: null makes it a mismatch.
                        setters.Add(property, (string)setter.Attribute("Value"));
                    }
                }

                triggers += element.Elements()
                    .Where(e => e.Name.LocalName == "Style.Triggers")
                    .SelectMany(e => e.Elements())
                    .Count();

                current = BaseStyle(element, byKey);
                if (current == null && HasBasedOn(element))
                {
                    return null; // a base style we cannot read may set anything
                }
            }

            if (setters.Count == 0)
            {
                return null;
            }

            var covered = new List<XamlAttribute>();
            foreach (KeyValuePair<string, string> setter in setters)
            {
                XamlAttribute attribute = selection.Selected.FirstOrDefault(a => PropertyName(a.Name) == setter.Key);
                if (attribute == null || setter.Value == null
                    || !SameValue(setter.Key, Dereference(Decode(attribute.Value), byKey), Dereference(setter.Value, byKey)))
                {
                    return null; // the style sets a property the selection does not have, or another value
                }

                covered.Add(attribute);
            }

            covered = covered.OrderBy(a => a.Start).ToList();
            List<XamlAttribute> remaining = selection.Selected.Where(a => !covered.Contains(a)).ToList();
            return new ResourceMatch(style, MatchKind.Style, covered, remaining, triggers);
        }

        /// <summary>
        /// {StaticResource AccentBrush} becomes the value of AccentBrush when that is a simple value, so a setter
        /// with the reference matches an attribute with the color, and the other way around.
        /// </summary>
        private static string Dereference(string value, IReadOnlyDictionary<string, ResourceEntry> byKey)
        {
            Match reference = KeyReference.Match(value.Trim());
            if (reference.Success
                && byKey.TryGetValue(reference.Groups["key"].Value, out ResourceEntry entry)
                && ValueTypes.Contains(entry.ElementTypeName)
                && ReadValue(entry.Element) is string resolved)
            {
                return resolved;
            }

            return value;
        }

        private static ResourceEntry BaseStyle(XElement style, IReadOnlyDictionary<string, ResourceEntry> byKey)
        {
            string basedOn = ((string)style.Attribute("BasedOn"))?.Trim();
            if (string.IsNullOrEmpty(basedOn))
            {
                return null;
            }

            Match reference = KeyReference.Match(basedOn);
            return reference.Success && byKey.TryGetValue(reference.Groups["key"].Value, out ResourceEntry entry) && entry.ElementTypeName == "Style"
                ? entry
                : null;
        }

        private static bool HasBasedOn(XElement style) => !string.IsNullOrWhiteSpace((string)style.Attribute("BasedOn"));

        /// <summary>"Shape.Fill" and "Fill" are the same setter. Attached properties such as "Grid.Row" keep their owner.</summary>
        private static string PropertyName(string property)
        {
            int dot = property.LastIndexOf('.');
            if (dot < 0)
            {
                return property;
            }

            // A normal property has an instance property on the owner; an attached one only has Get/Set methods.
            string name = property.Substring(dot + 1);
            Type owner = FindWpfType(property.Substring(0, dot));
            // GetProperty(name) throws when a subclass hides a property with "new", so search the list.
            bool isNormal = owner != null && owner.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.Name == name);
            return isNormal ? name : property;
        }

        /// <summary>
        /// True when a style for <paramref name="targetType"/> can be used on <paramref name="elementName"/>:
        /// the same type, or a base type (a Style for Shape fits a Path). Types we do not know only fit themselves.
        /// </summary>
        /// <summary>
        /// True when an element can have a Style: a FrameworkElement or FrameworkContentElement.
        /// False for Setter, Trigger, GradientStop and the like, and for types we do not know.
        /// </summary>
        internal static bool CanHaveStyle(string elementName)
        {
            Type type = FindWpfType(elementName);
            return type != null && (typeof(FrameworkElement).IsAssignableFrom(type) || typeof(FrameworkContentElement).IsAssignableFrom(type));
        }

        internal static bool Fits(string elementName, string targetType)
        {
            if (string.Equals(elementName, targetType, StringComparison.Ordinal))
            {
                return true;
            }

            Type element = FindWpfType(elementName);
            Type target = targetType.Contains(":") ? null : FindWpfType(targetType);
            return element != null && target != null && target.IsAssignableFrom(element);
        }

        internal static Type FindWpfType(string name) =>
            WpfTypes.GetOrAdd(name, n =>
            {
                Assembly[] assemblies = { typeof(FrameworkElement).Assembly, typeof(UIElement).Assembly, typeof(DependencyObject).Assembly };
                foreach (string ns in WpfNamespaces)
                {
                    foreach (Assembly assembly in assemblies)
                    {
                        Type type = assembly.GetType(ns + "." + n, throwOnError: false);
                        if (type != null && type.IsPublic)
                        {
                            return type;
                        }
                    }
                }

                return null;
            });

        // --- Single values ---

        private static IEnumerable<ResourceMatch> MatchValue(AttributeSelection selection, ResourceEntry entry)
        {
            string resourceValue = ReadValue(entry.Element);
            if (resourceValue == null)
            {
                yield break;
            }

            foreach (XamlAttribute attribute in selection.Selected)
            {
                string value = Decode(attribute.Value);
                string type = ExtractPlanner.ResourceTypeFor(attribute.Name, value);
                if (type == null || LocalName(type) != entry.ElementTypeName)
                {
                    continue;
                }

                if (SameValue(attribute.Name, value, resourceValue))
                {
                    yield return new ResourceMatch(entry, MatchKind.Value, new[] { attribute }, Array.Empty<XamlAttribute>(), 0);
                }
            }
        }

        /// <summary>
        /// The value of a simple resource: Color="..." on a SolidColorBrush, or the text content.
        /// Null when the resource has more than a value, such as a brush with Opacity.
        /// </summary>
        private static string ReadValue(XElement element)
        {
            bool hasOtherAttributes = element.Attributes().Any(a =>
                !a.IsNamespaceDeclaration
                && a.Name != ResourceIndex.XamlNamespace + "Key"
                && a.Name != ResourceIndex.XamlNamespace + "Shared"
                && a.Name.LocalName != "Freeze"
                && !(element.Name.LocalName == "SolidColorBrush" && a.Name.LocalName == "Color"));
            if (hasOtherAttributes || element.HasElements)
            {
                return null;
            }

            if (element.Name.LocalName == "SolidColorBrush")
            {
                return (string)element.Attribute("Color");
            }

            return element.Value;
        }

        private static string LocalName(string type) => type.Substring(type.IndexOf(':') + 1);

        // --- Comparing values ---

        /// <summary>True when both texts give the same value for the property.</summary>
        internal static bool SameValue(string property, string a, string b) =>
            string.Equals(Normalize(property, a), Normalize(property, b), StringComparison.Ordinal);

        /// <summary>
        /// One spelling per value. Colors become #AARRGGBB, number lists are expanded to their full form,
        /// True/False ignore case, and markup extensions ignore extra white space. Other text stays as is.
        /// </summary>
        internal static string Normalize(string property, string value)
        {
            if (value == null)
            {
                return null;
            }

            string trimmed = value.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                string compact = Regex.Replace(trimmed, @"\s+", " ");
                return Regex.Replace(compact, @"\s*([=,{}])\s*", "$1");
            }

            switch (ExtractPlanner.ResourceTypeFor(property, trimmed))
            {
                case "SolidColorBrush":
                case "Color":
                    return ColorParser.TryParse(trimmed, out Color color) ? color.ToString(CultureInfo.InvariantCulture) : value;
                case "Thickness":
                    return Numbers(trimmed, 4);
                case "CornerRadius":
                    return Numbers(trimmed, 4);
                case "sys:Double":
                    return Numbers(trimmed, 1);
                case "FontWeight":
                    return trimmed.ToLowerInvariant();
            }

            if (string.Equals(trimmed, "True", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "False", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.ToLowerInvariant();
            }

            return value;
        }

        /// <summary>"8" → "8,8,8,8" and "4,8" → "4,8,4,8" for four parts; numbers written one way.</summary>
        private static string Numbers(string text, int fullCount)
        {
            double[] parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture))
                .ToArray();

            if (fullCount == 4 && parts.Length == 1)
            {
                parts = new[] { parts[0], parts[0], parts[0], parts[0] };
            }
            else if (fullCount == 4 && parts.Length == 2)
            {
                parts = new[] { parts[0], parts[1], parts[0], parts[1] };
            }

            return string.Join(",", parts.Select(p => p.ToString("R", CultureInfo.InvariantCulture)));
        }

        /// <summary>Attribute values in the file are still XML-escaped; setter values from XElement are not.</summary>
        private static string Decode(string value) => WebUtility.HtmlDecode(value);
    }
}
