using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ResourcePreview.Resources
{
    /// <summary>A literal attribute value under the caret that can become a resource, such as Background="#FF2D7D9A".</summary>
    internal sealed class ExtractCandidate
    {
        public ExtractCandidate(string elementName, string property, string value, int valueStart, int valueLength, string resourceType)
        {
            ElementName = elementName;
            Property = property;
            Value = value;
            ValueStart = valueStart;
            ValueLength = valueLength;
            ResourceType = resourceType;
        }

        /// <summary>Local name of the element: "Border" for &lt;Border ...&gt;.</summary>
        public string ElementName { get; }

        /// <summary>The attribute name as written: "Background", or "Border.Background".</summary>
        public string Property { get; }

        public string Value { get; }

        /// <summary>Position of the value in the whole file text, without the quotes.</summary>
        public int ValueStart { get; }

        public int ValueLength { get; }

        /// <summary>The resource element: "SolidColorBrush", "Thickness", "sys:Double" ...</summary>
        public string ResourceType { get; }

        public bool NeedsSystemNamespace => ResourceType.StartsWith("sys:", StringComparison.Ordinal);
    }

    /// <summary>
    /// The text side of "Extract to resource": which values qualify, what resource they become, where it goes
    /// in the target file, and the changes to both files. Uses no VS APIs, so all of it is unit tested.
    /// </summary>
    internal static class ExtractPlanner
    {
        private const string SystemNamespace = "clr-namespace:System;assembly=mscorlib";

        // Name="value" where the value is plain text, not a markup extension such as {StaticResource X}.
        private static readonly Regex SetterProperty =
            new Regex(@"\bProperty\s*=\s*[""'](?<property>[\w.:]+)[""']", RegexOptions.Compiled);

        private static readonly Regex StyleStart = new Regex(@"<(?:\w+:)?Style[\s/>]", RegexOptions.Compiled);

        private static readonly Regex StyleTargetType =
            new Regex(@"\bTargetType\s*=\s*[""'](?:\{\s*x:Type\s+(?:TypeName\s*=\s*)?)?(?<type>[\w.:]+)", RegexOptions.Compiled);

        private static readonly Regex Attribute = new Regex(@"(?<name>[\w.:]+)\s*=\s*""(?<value>[^""{][^""]*)""", RegexOptions.Compiled);

        private static readonly string[] BrushProperties = { "Background", "Foreground", "BorderBrush", "Fill", "Stroke", "CaretBrush", "SelectionBrush", "OpacityMask" };
        private static readonly string[] ThicknessProperties = { "Margin", "Padding", "BorderThickness" };
        private static readonly string[] DoubleProperties = { "FontSize", "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Opacity", "StrokeThickness" };
        private static readonly string[] StringProperties = { "Text", "Content", "Header", "ToolTip", "Title" };

        /// <summary>The value the caret is in, or null when it is not a value we can turn into a resource.</summary>
        public static ExtractCandidate TryFind(string text, int caret)
        {
            if (caret < 0 || caret > text.Length)
            {
                return null;
            }

            int lineStart = text.LastIndexOf('\n', Math.Max(caret - 1, 0)) + 1;
            if (caret == 0)
            {
                lineStart = 0;
            }

            int lineEnd = text.IndexOf('\n', caret);
            string line = text.Substring(lineStart, (lineEnd < 0 ? text.Length : lineEnd) - lineStart);
            int position = caret - lineStart;

            foreach (Match match in Attribute.Matches(line))
            {
                if (position < match.Index || position > match.Index + match.Length)
                {
                    continue;
                }

                string name = match.Groups["name"].Value;
                if (name.StartsWith("x:", StringComparison.Ordinal) || name.StartsWith("xmlns", StringComparison.Ordinal) || name.StartsWith("d:", StringComparison.Ordinal))
                {
                    return null;
                }

                string value = match.Groups["value"].Value;
                string elementName = FindElementName(text, lineStart + match.Index);
                string property = name;
                if (name == "Value" && elementName == "Setter")
                {
                    // <Setter Property="Background" Value="White" />: the type comes from the property in the setter,
                    // and the key from the TargetType of the style around it: "ButtonBackground".
                    if (!TryReadSetter(text, lineStart + match.Index, out property, out string targetType))
                    {
                        return null;
                    }

                    elementName = targetType ?? string.Empty;
                }

                string type = ResourceTypeFor(property, value);
                if (type == null)
                {
                    return null;
                }

                Group valueGroup = match.Groups["value"];
                return new ExtractCandidate(elementName, property, value, lineStart + valueGroup.Index, valueGroup.Length, type);
            }

            return null;
        }

        /// <summary>
        /// Which resource a property value becomes. XAML text has no types, so this goes by the property name,
        /// and checks that the value fits. Null for properties we do not know.
        /// </summary>
        internal static string ResourceTypeFor(string property, string value)
        {
            string name = property.Substring(property.LastIndexOf('.') + 1);
            string trimmed = value.Trim();

            if (BrushProperties.Contains(name))
            {
                return ColorParser.TryParse(trimmed, out _) ? "SolidColorBrush" : null;
            }

            if (name == "Color" || name.EndsWith("Color", StringComparison.Ordinal))
            {
                return ColorParser.TryParse(trimmed, out _) ? "Color" : null;
            }

            if (ThicknessProperties.Contains(name))
            {
                return IsNumberList(trimmed, 1, 2, 4) ? "Thickness" : null;
            }

            if (name == "CornerRadius")
            {
                return IsNumberList(trimmed, 1, 4) ? "CornerRadius" : null;
            }

            if (DoubleProperties.Contains(name))
            {
                return IsNumberList(trimmed, 1) ? "sys:Double" : null;
            }

            if (name == "FontFamily")
            {
                return "FontFamily";
            }

            if (name == "FontWeight")
            {
                return Regex.IsMatch(trimmed, @"^[A-Za-z]+$") ? "FontWeight" : null;
            }

            if (StringProperties.Contains(name))
            {
                return "sys:String";
            }

            return null;
        }

        private static bool IsNumberList(string value, params int[] counts)
        {
            string[] parts = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return counts.Contains(parts.Length)
                && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        }

        /// <summary>The Property of the Setter that holds the attribute, and the TargetType of the Style around it, if any.</summary>
        private static bool TryReadSetter(string text, int attributeStart, out string property, out string targetType)
        {
            property = null;
            targetType = null;
            int open = text.LastIndexOf('<', Math.Max(attributeStart - 1, 0));
            int close = open < 0 ? -1 : text.IndexOf('>', open);
            if (open < 0 || close < 0)
            {
                return false;
            }

            Match setter = SetterProperty.Match(text.Substring(open, close - open));
            if (!setter.Success)
            {
                return false;
            }

            property = setter.Groups["property"].Value;
            // The last <Style before the setter; not <Style.Triggers.
            Match lastStyle = StyleStart.Matches(text.Substring(0, open)).Cast<Match>().LastOrDefault();
            int style = lastStyle?.Index ?? -1;
            int styleEnd = style < 0 ? -1 : text.IndexOf('>', style);
            if (styleEnd > style)
            {
                Match target = StyleTargetType.Match(text.Substring(style, styleEnd - style));
                if (target.Success)
                {
                    string type = target.Groups["type"].Value;
                    targetType = type.Substring(type.LastIndexOf(':') + 1);
                }
            }

            return true;
        }

        /// <summary>The local name of the element the attribute belongs to: the last "&lt;Name" before it.</summary>
        private static string FindElementName(string text, int attributeStart)
        {
            int open = text.LastIndexOf('<', Math.Max(attributeStart - 1, 0));
            if (open < 0)
            {
                return string.Empty;
            }

            Match match = Regex.Match(text.Substring(open + 1), @"^[\w.:]+");
            string name = match.Value;
            return name.Substring(name.LastIndexOf(':') + 1);
        }

        /// <summary>A key to start with: element plus property, "BorderBackground".</summary>
        public static string SuggestKey(ExtractCandidate candidate)
        {
            string property = candidate.Property.Substring(candidate.Property.LastIndexOf('.') + 1);
            return candidate.ElementName + property;
        }

        /// <summary>The resource element as XAML: &lt;SolidColorBrush x:Key="K" Color="#FF2D7D9A" /&gt;.</summary>
        public static string ResourceText(ExtractCandidate candidate, string key, string systemPrefix = "sys")
        {
            // The value comes from an attribute, so it is already escaped for XML (&amp;). The key is
            // checked first and contains no characters that need escaping.
            string value = candidate.Value;
            string escapedKey = key;
            switch (candidate.ResourceType)
            {
                case "SolidColorBrush":
                    return $"<SolidColorBrush x:Key=\"{escapedKey}\" Color=\"{value.Trim()}\" />";
                default:
                    string type = candidate.NeedsSystemNamespace
                        ? systemPrefix + candidate.ResourceType.Substring(3)
                        : candidate.ResourceType;
                    return $"<{type} x:Key=\"{escapedKey}\">{value}</{type}>";
            }
        }

        /// <summary>
        /// The files the new resource can go to: dictionaries this file can see (its own merges and those
        /// of App.xaml), then App.xaml. The file itself first when it is a dictionary.
        /// </summary>
        public static IReadOnlyList<string> Targets(ResourceIndex index, string filePath, CancellationToken cancellationToken)
        {
            MergeGraph graph = index.GetMergeGraph(cancellationToken);
            List<string> chain = graph.GetLookupChain(filePath);

            var dictionaries = chain.Where(f => graph.Files.TryGetValue(f, out XamlFileModel m) && m.RootKind == XamlRootKind.ResourceDictionary);
            var applications = chain.Where(f => graph.Files.TryGetValue(f, out XamlFileModel m) && m.RootKind == XamlRootKind.Application);
            return dictionaries.Concat(applications).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// The changes that add the resource to the target file: the element itself and, for sys:Double or
        /// sys:String, an xmlns:sys on the root when it is missing. Empty when no place to insert is found.
        /// <paramref name="beforeOffset"/> is set when the value is in the same file: the resource then goes
        /// above the element that uses it, because a StaticResource does not look ahead.
        /// </summary>
        public static IReadOnlyList<TextChange> AddResource(string targetText, ExtractCandidate candidate, string key, int? beforeOffset) =>
            AddResource(targetText, prefix => new[] { ResourceText(candidate, key, prefix) }, candidate.NeedsSystemNamespace, beforeOffset);

        /// <summary>
        /// Adds a resource of one or more lines, such as a Style with setters. <paramref name="createLines"/> gets
        /// the prefix for the System namespace. Each line is indented to its place in the target file.
        /// </summary>
        public static IReadOnlyList<TextChange> AddResource(string targetText, Func<string, IReadOnlyList<string>> createLines, bool needsSystemNamespace, int? beforeOffset)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(targetText, LoadOptions.SetLineInfo);
            }
            catch (XmlException)
            {
                return Array.Empty<TextChange>();
            }

            var changes = new List<TextChange>();
            string prefix = "sys";
            if (needsSystemNamespace)
            {
                prefix = FindSystemPrefix(document.Root);
                if (prefix == null)
                {
                    prefix = document.Root.Attribute(XNamespace.Xmlns + "sys") == null ? "sys" : "system";
                    int insertAt = FindRootNameEnd(targetText, document.Root);
                    if (insertAt < 0)
                    {
                        return Array.Empty<TextChange>();
                    }

                    changes.Add(new TextChange(insertAt, 0, $" xmlns:{prefix}=\"{SystemNamespace}\""));
                }
            }

            string newline = targetText.Contains("\r\n") ? "\r\n" : "\n";
            IReadOnlyList<string> lines = createLines(prefix);
            string Indent(string indentation) => string.Join(newline, lines.Select(line => indentation + line));

            int? position = beforeOffset.HasValue
                ? FindPositionAbove(targetText, beforeOffset.Value)
                : FindDictionaryEnd(targetText, document.Root);
            if (!position.HasValue)
            {
                return Array.Empty<TextChange>();
            }

            int lineStart = targetText.LastIndexOf('\n', Math.Max(position.Value - 1, 0)) + 1;
            string indentation = LeadingWhitespace(targetText, lineStart);
            if (beforeOffset.HasValue)
            {
                changes.Add(new TextChange(lineStart, 0, Indent(indentation) + newline));
            }
            else if (string.IsNullOrWhiteSpace(targetText.Substring(lineStart, position.Value - lineStart)))
            {
                // The closing tag is on its own line: a new line above it, one level deeper.
                changes.Add(new TextChange(lineStart, 0, Indent(indentation + "    ") + newline));
            }
            else
            {
                // Something else on the same line, such as <ResourceDictionary ...></ResourceDictionary>.
                changes.Add(new TextChange(position.Value, 0, newline + Indent(indentation + "    ") + newline + indentation));
            }

            return changes;
        }

        /// <summary>The prefix the root already uses for the System namespace (mscorlib or System.Runtime), or null.</summary>
        private static string FindSystemPrefix(XElement root)
        {
            foreach (XAttribute attribute in root.Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.Xmlns))
            {
                if (attribute.Value.StartsWith("clr-namespace:System;", StringComparison.Ordinal))
                {
                    return attribute.Name.LocalName;
                }
            }

            return null;
        }

        /// <summary>Just after "&lt;ResourceDictionary" (or "&lt;Application"): where a new xmlns attribute can go.</summary>
        private static int FindRootNameEnd(string text, XElement root)
        {
            string prefix = root.GetPrefixOfNamespace(root.Name.Namespace);
            string tag = "<" + (string.IsNullOrEmpty(prefix) ? string.Empty : prefix + ":") + root.Name.LocalName;
            int index = text.IndexOf(tag, StringComparison.Ordinal);
            return index < 0 ? -1 : index + tag.Length;
        }

        /// <summary>
        /// Start of the line with the closing tag of the main dictionary. A root ResourceDictionary closes with
        /// the last &lt;/ResourceDictionary&gt;. In App.xaml it is the last one before &lt;/Application.Resources&gt;,
        /// or &lt;/Application.Resources&gt; itself when there is no ResourceDictionary inside.
        /// </summary>
        private static int? FindDictionaryEnd(string text, XElement root)
        {
            const string DictionaryEnd = "</ResourceDictionary>";
            if (root.Name.LocalName == "ResourceDictionary")
            {
                int end = text.LastIndexOf(DictionaryEnd, StringComparison.Ordinal);
                return end < 0 ? (int?)null : end;
            }

            int resourcesEnd = text.IndexOf("</" + root.Name.LocalName + ".Resources>", StringComparison.Ordinal);
            if (resourcesEnd < 0)
            {
                return null;
            }

            bool hasDictionary = root.Elements().Any(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)
                && e.Elements().Any(c => c.Name.LocalName == "ResourceDictionary"));
            if (!hasDictionary)
            {
                return resourcesEnd;
            }

            int dictionaryEnd = text.LastIndexOf(DictionaryEnd, resourcesEnd, StringComparison.Ordinal);
            return dictionaryEnd < 0 ? (int?)null : dictionaryEnd;
        }

        /// <summary>
        /// Above the top-level resource that contains the offset, so the new resource is defined before its use.
        /// </summary>
        private static int? FindPositionAbove(string text, int offset)
        {
            XDocument document = XDocument.Parse(text, LoadOptions.SetLineInfo);
            int line = text.Substring(0, offset).Count(c => c == '\n') + 1;

            XElement container = document.Root.Elements()
                .Where(e => !e.Name.LocalName.Contains("."))
                .LastOrDefault(e => ((IXmlLineInfo)e).LineNumber <= line);
            if (container == null)
            {
                return null;
            }

            return LineStart(text, ((IXmlLineInfo)container).LineNumber);
        }

        internal static int LineStart(string text, int lineNumber)
        {
            int position = 0;
            for (int i = 1; i < lineNumber; i++)
            {
                position = text.IndexOf('\n', position) + 1;
            }

            return position;
        }

        internal static string LeadingWhitespace(string text, int lineStart)
        {
            int end = lineStart;
            while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
            {
                end++;
            }

            return text.Substring(lineStart, end - lineStart);
        }

        /// <summary>Replaces the literal value with {StaticResource Key}.</summary>
        public static TextChange ReplaceValue(ExtractCandidate candidate, string key) =>
            new TextChange(candidate.ValueStart, candidate.ValueLength, "{StaticResource " + key + "}");
    }
}
