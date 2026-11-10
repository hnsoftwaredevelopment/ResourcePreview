using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ResourcePreview.Resources
{
    /// <summary>One attribute in a start tag, with its position in the file text.</summary>
    internal sealed class XamlAttribute
    {
        public XamlAttribute(string name, string value, char quote, int start, int end, int removeStart, int valueStart)
        {
            Name = name;
            Value = value;
            Quote = quote;
            Start = start;
            End = end;
            RemoveStart = removeStart;
            ValueStart = valueStart;
        }

        public string Name { get; }

        /// <summary>The value as written, still XML-escaped.</summary>
        public string Value { get; }

        public char Quote { get; }

        /// <summary>Where the name starts.</summary>
        public int Start { get; }

        /// <summary>Just after the closing quote.</summary>
        public int End { get; }

        /// <summary>Start of the white space before the attribute. Removing from here keeps the tag tidy.</summary>
        public int RemoveStart { get; }

        public int ValueStart { get; }

        /// <summary>The setter for this attribute: &lt;Setter Property="Fill" Value="White" /&gt;.</summary>
        public string ToSetter() => $"<Setter Property=\"{Name}\" Value={Quote}{Value}{Quote} />";
    }

    /// <summary>The attributes selected in one start tag, and what the element already has as Style.</summary>
    internal sealed class AttributeSelection
    {
        public AttributeSelection(string elementName, int tagStart, IReadOnlyList<XamlAttribute> selected, XamlAttribute style, string styleKey)
        {
            ElementName = elementName;
            TagStart = tagStart;
            Selected = selected;
            Style = style;
            StyleKey = styleKey;
        }

        /// <summary>"Path", "Button" ... Only elements from the default WPF namespace.</summary>
        public string ElementName { get; }

        public int TagStart { get; }

        /// <summary>The attributes that can become setters, in file order.</summary>
        public IReadOnlyList<XamlAttribute> Selected { get; }

        /// <summary>The Style attribute of the element, or null.</summary>
        public XamlAttribute Style { get; }

        /// <summary>The key in Style="{StaticResource Key}", or null.</summary>
        public string StyleKey { get; }
    }

    /// <summary>
    /// The text side of "Extract to style" and "Add to style": select attributes such as
    /// Fill="White" Width="16" in a start tag, and move them into the setters of a Style.
    /// Uses no VS APIs, so all of it is unit tested.
    /// </summary>
    internal static class StylePlanner
    {
        private static readonly Regex AttributePattern =
            new Regex(@"(?<name>[\w.:]+)\s*=\s*(?<q>[""'])(?<value>.*?)\k<q>", RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex StyleReference =
            new Regex(@"^\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?(?<key>[^\s,{}]+)\s*\}$", RegexOptions.Compiled);

        // Events cannot be setters (they need an EventSetter), so these stay on the element.
        private static readonly Regex EventName =
            new Regex(@"^(Click|Loaded|Unloaded|Initialized|Checked|Unchecked|Indeterminate|Closed|Closing|Opened|Drop|Drag\w+|Mouse\w+|Key(Down|Up)|Got\w*Focus|Lost\w*Focus|Touch\w+|Stylus\w+|Preview\w+|\w+Changed|\w+Click)$", RegexOptions.Compiled);

        /// <summary>
        /// The selected attributes of the start tag around the selection, or null when the selection is not
        /// inside one start tag, or holds nothing that can become a setter.
        /// </summary>
        public static AttributeSelection TryFind(string text, int selectionStart, int selectionEnd)
        {
            if (selectionEnd <= selectionStart || selectionStart < 0 || selectionEnd > text.Length)
            {
                return null;
            }

            int tagStart = text.LastIndexOf('<', selectionStart);
            if (tagStart < 0 || tagStart + 1 >= text.Length || !char.IsLetter(text[tagStart + 1]))
            {
                return null;
            }

            int tagEnd = FindTagEnd(text, tagStart);
            if (tagEnd < 0 || selectionEnd > tagEnd + 1)
            {
                return null;
            }

            string name = Regex.Match(text.Substring(tagStart + 1), @"^[\w.:]+").Value;
            if (name.Contains(":") || name.Contains("."))
            {
                return null; // own types need an xmlns in the target file; property elements have no attributes to move
            }

            int attributesStart = tagStart + 1 + name.Length;
            string tagText = text.Substring(attributesStart, tagEnd - attributesStart);
            var attributes = new List<XamlAttribute>();
            foreach (Match match in AttributePattern.Matches(tagText))
            {
                int start = attributesStart + match.Index;
                int removeStart = start;
                while (removeStart > attributesStart && char.IsWhiteSpace(text[removeStart - 1]))
                {
                    removeStart--;
                }

                attributes.Add(new XamlAttribute(
                    match.Groups["name"].Value,
                    match.Groups["value"].Value,
                    match.Groups["q"].Value[0],
                    start,
                    start + match.Length,
                    removeStart,
                    attributesStart + match.Groups["value"].Index));
            }

            XamlAttribute style = attributes.FirstOrDefault(a => a.Name == "Style");
            string styleKey = null;
            if (style != null)
            {
                Match reference = StyleReference.Match(style.Value.Trim());
                if (!reference.Success)
                {
                    return null; // Style="{x:Null}" or a binding: we cannot combine that with a new style
                }

                styleKey = reference.Groups["key"].Value;
            }

            List<XamlAttribute> selected = attributes
                .Where(a => a.Start < selectionEnd && a.End > selectionStart)
                .Where(a => CanBeSetter(a.Name))
                .ToList();

            return selected.Count == 0 ? null : new AttributeSelection(name, tagStart, selected, style, styleKey);
        }

        internal static bool CanBeSetter(string name) =>
            name != "Style" && name != "Name"
            && !name.StartsWith("x:", StringComparison.Ordinal)
            && !name.StartsWith("xmlns", StringComparison.Ordinal)
            && !name.StartsWith("d:", StringComparison.Ordinal)
            && !name.StartsWith("mc:", StringComparison.Ordinal)
            && !EventName.IsMatch(name);

        /// <summary>The position of the '&gt;' that ends the start tag, skipping '&gt;' inside quoted values.</summary>
        private static int FindTagEnd(string text, int tagStart)
        {
            char quote = '\0';
            for (int i = tagStart + 1; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    return i;
                }
                else if (c == '<')
                {
                    return -1;
                }
            }

            return -1;
        }

        /// <summary>A key to start with: "PathStyle".</summary>
        public static string SuggestKey(AttributeSelection selection) => selection.ElementName + "Style";

        /// <summary>The new Style, one line per element. BasedOn the style the element already had.</summary>
        public static IReadOnlyList<string> StyleLines(AttributeSelection selection, string key)
        {
            string basedOn = selection.StyleKey == null ? string.Empty : $" BasedOn=\"{{StaticResource {selection.StyleKey}}}\"";
            var lines = new List<string> { $"<Style x:Key=\"{key}\" TargetType=\"{selection.ElementName}\"{basedOn}>" };
            lines.AddRange(selection.Selected.Select(a => "    " + a.ToSetter()));
            lines.Add("</Style>");
            return lines;
        }

        /// <summary>
        /// Removes the selected attributes and points the element at the new style: the Style attribute
        /// gets the new key, or a new Style attribute takes the place of the first removed attribute.
        /// </summary>
        public static IReadOnlyList<TextChange> PointElementAtStyle(AttributeSelection selection, string key)
        {
            string reference = "{StaticResource " + key + "}";
            var changes = new List<TextChange>();
            for (int i = 0; i < selection.Selected.Count; i++)
            {
                XamlAttribute attribute = selection.Selected[i];
                string replacement = i == 0 && selection.Style == null ? $" Style=\"{reference}\"" : string.Empty;
                changes.Add(new TextChange(attribute.RemoveStart, attribute.End - attribute.RemoveStart, replacement));
            }

            if (selection.Style != null)
            {
                changes.Add(new TextChange(selection.Style.ValueStart, selection.Style.Value.Length, reference));
            }

            return changes;
        }

        /// <summary>Removes the selected attributes from the element (they move to the existing style).</summary>
        public static IReadOnlyList<TextChange> RemoveSelected(AttributeSelection selection) =>
            selection.Selected.Select(a => new TextChange(a.RemoveStart, a.End - a.RemoveStart, string.Empty)).ToList();

        /// <summary>
        /// Adds setters to an existing Style in a file. A setter for a property the style already sets is
        /// replaced, because a Style may set each property only once. New setters go after the last setter:
        /// they must stay together, not after &lt;Style.Triggers&gt;. Returns no changes when the style is not found.
        /// </summary>
        public static IReadOnlyList<TextChange> AddSetters(string styleFileText, string styleKey, int keyLine, IReadOnlyList<XamlAttribute> attributes, out int replaced)
        {
            replaced = 0;
            XDocument document;
            try
            {
                document = XDocument.Parse(styleFileText, LoadOptions.SetLineInfo);
            }
            catch (XmlException)
            {
                return Array.Empty<TextChange>();
            }

            XNamespace x = ResourceIndex.XamlNamespace;
            XElement style = document.Descendants()
                .Where(e => e.Name.LocalName == "Style")
                .FirstOrDefault(e =>
                {
                    XAttribute keyAttribute = e.Attribute(x + "Key");
                    return keyAttribute != null && keyAttribute.Value == styleKey && ((IXmlLineInfo)keyAttribute).LineNumber == keyLine;
                });
            if (style == null)
            {
                return Array.Empty<TextChange>();
            }

            string newline = styleFileText.Contains("\r\n") ? "\r\n" : "\n";
            int styleStart = ElementStart(styleFileText, style);
            string styleIndent = ExtractPlanner.LeadingWhitespace(styleFileText, ExtractPlanner.LineStart(styleFileText, ((IXmlLineInfo)style).LineNumber));
            int startTagEnd = FindTagEnd(styleFileText, styleStart);

            var changes = new List<TextChange>();
            var setters = style.Elements().Where(e => e.Name.LocalName == "Setter").ToList();
            var toAdd = new List<XamlAttribute>();
            foreach (XamlAttribute attribute in attributes)
            {
                XElement existing = setters.FirstOrDefault(s => (string)s.Attribute("Property") == attribute.Name);
                if (existing == null)
                {
                    toAdd.Add(attribute);
                    continue;
                }

                int start = ElementStart(styleFileText, existing);
                changes.Add(new TextChange(start, ElementEnd(styleFileText, start) - start, attribute.ToSetter()));
                replaced++;
            }

            if (toAdd.Count == 0)
            {
                return changes;
            }

            if (styleFileText[startTagEnd - 1] == '/')
            {
                // <Style ... /> has no content yet: open it up.
                string body = string.Concat(toAdd.Select(a => newline + styleIndent + "    " + a.ToSetter()));
                int from = startTagEnd - 1;
                while (from > styleStart && char.IsWhiteSpace(styleFileText[from - 1]))
                {
                    from--;
                }

                changes.Add(new TextChange(from, startTagEnd + 1 - from, ">" + body + newline + styleIndent + "</Style>"));
                return changes;
            }

            int insertAt;
            string indent;
            if (setters.Count > 0)
            {
                int lastStart = ElementStart(styleFileText, setters[setters.Count - 1]);
                insertAt = ElementEnd(styleFileText, lastStart);
                indent = ExtractPlanner.LeadingWhitespace(styleFileText, ExtractPlanner.LineStart(styleFileText, ((IXmlLineInfo)setters[setters.Count - 1]).LineNumber));
            }
            else
            {
                insertAt = startTagEnd + 1;
                indent = styleIndent + "    ";
            }

            changes.Add(new TextChange(insertAt, 0, string.Concat(toAdd.Select(a => newline + indent + a.ToSetter()))));
            return changes;
        }

        /// <summary>The position of the '&lt;' of an element, from its line info.</summary>
        private static int ElementStart(string text, XElement element)
        {
            var info = (IXmlLineInfo)element;
            return ExtractPlanner.LineStart(text, info.LineNumber) + info.LinePosition - 2;
        }

        /// <summary>Just after the end of the element that starts at <paramref name="start"/>: after "/&gt;" or its end tag.</summary>
        private static int ElementEnd(string text, int start)
        {
            int depth = 0;
            int i = start;
            while (i < text.Length)
            {
                if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
                {
                    int close = text.IndexOf("-->", i, StringComparison.Ordinal);
                    i = close < 0 ? text.Length : close + 3;
                    continue;
                }

                if (text[i] != '<')
                {
                    i++;
                    continue;
                }

                int end = FindTagEnd(text, i);
                if (end < 0)
                {
                    return text.Length;
                }

                if (text[i + 1] == '/')
                {
                    depth--;
                }
                else if (text[end - 1] != '/')
                {
                    depth++;
                }

                i = end + 1;
                if (depth == 0)
                {
                    return i;
                }
            }

            return text.Length;
        }
    }
}
