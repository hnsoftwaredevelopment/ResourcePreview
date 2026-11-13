using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using System.Xml.Linq;

namespace ResourcePreview.Resources
{
    /// <summary>A place in a line that gets a color swatch: a literal color, or a key that may be a color.</summary>
    internal sealed class SwatchSpot
    {
        public SwatchSpot(int position, Color? color, string key)
        {
            Position = position;
            Color = color;
            Key = key;
        }

        /// <summary>Position in the line where the value starts. The swatch goes just before it.</summary>
        public int Position { get; }

        /// <summary>A literal color such as White or #FF2D7D9A. Null for a reference.</summary>
        public Color? Color { get; }

        /// <summary>The key in {StaticResource Key}; its color comes from the index. Null for a literal.</summary>
        public string Key { get; }
    }

    /// <summary>
    /// Finds colors in a line of XAML for the swatches in the editor:
    /// - Background="White", Color="#FF2D7D9A", and Value="White" in a Setter for a brush property;
    /// - Fill="{StaticResource AccentBrush}": the key, resolved later to a SolidColorBrush or Color;
    /// - &lt;Color x:Key="Accent"&gt;#FF2D7D9A&lt;/Color&gt;.
    /// Works per line, like the other finders, so it is cheap enough to run while the editor draws.
    /// </summary>
    internal static class ColorSwatchFinder
    {
        private static readonly Regex Attribute =
            new Regex(@"(?<name>[\w.:]+)\s*=\s*(?<q>[""'])(?<value>[^""']*)\k<q>", RegexOptions.Compiled);

        private static readonly Regex Reference =
            new Regex(@"^\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?" + ResourceKeys.KeyPattern + @"\s*\}$", RegexOptions.Compiled);

        private static readonly Regex SetterProperty =
            new Regex(@"<Setter\b[^>]*?\bProperty\s*=\s*[""'](?<property>[\w.:]+)[""']", RegexOptions.Compiled);

        private static readonly Regex ColorContent =
            new Regex(@"<(?:\w+:)?Color\b[^<>]*>\s*(?<value>[^<\s][^<]*?)\s*</", RegexOptions.Compiled);

        private static readonly Regex Comment = new Regex(@"<!--.*?(-->|$)", RegexOptions.Compiled);

        public static IReadOnlyList<SwatchSpot> Find(string line)
        {
            if (line.IndexOf('=') < 0 && line.IndexOf("Color", StringComparison.Ordinal) < 0)
            {
                return Array.Empty<SwatchSpot>();
            }

            // Blank out comments, keeping the positions.
            string text = Comment.Replace(line, m => new string(' ', m.Length));
            var spots = new List<SwatchSpot>();

            foreach (Match match in Attribute.Matches(text))
            {
                string name = match.Groups["name"].Value;
                if (name == "Value")
                {
                    // <Setter Property="Background" Value="White" />: the property is in another attribute.
                    int tagStart = text.LastIndexOf('<', match.Index);
                    Match setter = tagStart < 0 ? Match.Empty : SetterProperty.Match(text.Substring(tagStart));
                    if (!setter.Success || setter.Index != 0)
                    {
                        continue;
                    }

                    name = setter.Groups["property"].Value;
                }

                if (!IsColorProperty(name))
                {
                    continue;
                }

                Group value = match.Groups["value"];
                int start = value.Index + (value.Value.Length - value.Value.TrimStart().Length);
                string trimmed = value.Value.Trim();

                Match reference = Reference.Match(trimmed);
                if (reference.Success)
                {
                    string key = ResourceKeys.Normalize(reference.Groups["key"].Value);
                    if (key != null)
                    {
                        spots.Add(new SwatchSpot(start, null, key));
                    }
                }
                else if (ColorParser.TryParse(trimmed, out Color color))
                {
                    spots.Add(new SwatchSpot(start, color, null));
                }
            }

            foreach (Match match in ColorContent.Matches(text))
            {
                if (ColorParser.TryParse(match.Groups["value"].Value, out Color color))
                {
                    spots.Add(new SwatchSpot(match.Groups["value"].Index, color, null));
                }
            }

            return spots.OrderBy(s => s.Position).ToList();
        }

        /// <summary>Background, Fill, Stroke, Color, ...: the properties ExtractPlanner turns into a brush or color.</summary>
        internal static bool IsColorProperty(string name)
        {
            string type = ExtractPlanner.ResourceTypeFor(name, "White");
            return type == "SolidColorBrush" || type == "Color";
        }

        /// <summary>
        /// The color of a resource WPF uses for <paramref name="key"/> in <paramref name="fromFile"/>:
        /// a Color, or a SolidColorBrush with a color (also one that refers to a Color resource).
        /// Null for anything else, such as a gradient or a brush with Opacity.
        /// </summary>
        public static Color? Resolve(ResourceIndex index, string key, string fromFile, CancellationToken cancellationToken)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add(key))
            {
                ResourceEntry entry = index.Resolve(key, fromFile, cancellationToken)
                    .FirstOrDefault(d => d.Status == DefinitionStatus.Used)?.Entry;
                if (entry == null)
                {
                    return null;
                }

                string text = ColorText(entry.Element);
                if (text == null)
                {
                    return null;
                }

                Match reference = Reference.Match(text.Trim());
                if (!reference.Success)
                {
                    return ColorParser.TryParse(text, out Color color) ? color : (Color?)null;
                }

                key = ResourceKeys.Normalize(reference.Groups["key"].Value);
                if (key == null)
                {
                    return null;
                }

                fromFile = entry.FilePath;
            }

            return null; // a loop of references
        }

        private static string ColorText(XElement element)
        {
            switch (element.Name.LocalName)
            {
                case "Color":
                    return element.HasElements ? null : element.Value;
                case "SolidColorBrush":
                    string opacity = (string)element.Attribute("Opacity");
                    if (opacity != null && opacity.Trim() != "1")
                    {
                        return null; // the swatch would show the wrong color
                    }

                    return (string)element.Attribute("Color");
                default:
                    return null;
            }
        }
    }
}
