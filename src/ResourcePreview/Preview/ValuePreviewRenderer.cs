using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using ResourcePreview.Localization;

namespace ResourcePreview.Preview
{
    /// <summary>
    /// Preview for non-visual resources:
    ///
    ///   FontFamily    sample text in that font, regular and bold
    ///   Thickness     a sketch of the four edges to scale, plus the values
    ///   CornerRadius  a rectangle with those corners, plus the values
    ///   sys:Double, sys:String, sys:Boolean, enums, ...   the value only
    ///
    /// Uses the VS theme text color. Call on the UI thread only.
    /// </summary>
    internal static class ValuePreviewRenderer
    {
        internal const string FontSample = "AaBbCc 0123";

        /// <summary>Upper bounds for the Thickness and CornerRadius sketches.</summary>
        private const double MaxDrawnEdge = 16;
        private const double MaxDrawnRadius = 20;

        private static readonly Brush EdgeFill = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x33, 0x99, 0xFF)));
        private static readonly Brush ContentFill = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)));

        /// <summary>
        /// WPF composite font names that resolve without a file in C:\Windows\Fonts.
        /// </summary>
        private static readonly HashSet<string> BuiltInFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Global User Interface", "Global Monospace", "Global Sans Serif", "Global Serif",
        };

        /// <summary>Returns null if the type cannot be shown as a value.</summary>
        public static UIElement TryRender(object resource)
        {
            switch (resource)
            {
                case FontFamily family:
                    return RenderFontFamily(family);

                case Thickness thickness:
                    return RenderThickness(thickness);

                case CornerRadius radius:
                    return RenderCornerRadius(radius);

                default:
                    string text = DescribeValue(resource);
                    return text == null ? null : Stack(CreateValueText(text));
            }
        }

        /// <summary>
        /// "18", "True", "Bold", "\"Save\"". Null for reference types such as a Transform or
        /// ControlTemplate, whose text form is not useful.
        /// </summary>
        internal static string DescribeValue(object value)
        {
            switch (value)
            {
                case null:
                    return null;

                case string text:
                    return "\"" + text + "\"";

                case bool flag:
                    return flag ? "True" : "False";

                case IFormattable formattable when value.GetType().IsPrimitive || value is decimal:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
            }

            // Structs and enums: the TypeConverter returns the same text you would write in XAML.
            Type type = value.GetType();
            if (!type.IsValueType)
            {
                return null;
            }

            try
            {
                TypeConverter converter = TypeDescriptor.GetConverter(type);
                return converter.CanConvertTo(typeof(string))
                    ? converter.ConvertToInvariantString(value)
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private static UIElement RenderFontFamily(FontFamily family)
        {
            var sample = CreateValueText(FontSample);
            sample.FontFamily = family;
            sample.FontSize = 20;
            sample.FontWeight = FontWeights.Normal;

            var bold = CreateValueText(FontSample);
            bold.FontFamily = family;
            bold.FontSize = 20;
            bold.FontWeight = FontWeights.Bold;

            var panel = Stack(sample, bold, PreviewRenderer.CreateText(family.Source, opacity: 0.7));

            string problem = FindFontProblem(family);
            if (problem != null)
            {
                panel.Children.Add(PreviewRenderer.CreateText(problem, opacity: 0.7));
            }

            return panel;
        }

        /// <summary>
        /// Returns a message if WPF cannot find the font, because WPF silently falls back to another font.
        /// Only the first family counts, since "Inter, Segoe UI" falls back to Segoe UI by design.
        /// </summary>
        internal static string FindFontProblem(FontFamily family)
        {
            string first = family.Source.Split(',')[0].Trim();
            if (first.Length == 0)
            {
                return null;
            }

            int hash = first.LastIndexOf('#');
            if (hash < 0)
            {
                return BuiltInFamilies.Contains(first) || CanResolve(first) ? null : Localizer.Format(Strings.FontNotInstalledFormat, first);
            }

            // "file:///C:/App/Fonts/#Inter": the font must exist in that folder or file.
            // PreviewXamlBuilder has already converted pack URIs and relative paths to file:///.
            string location = first.Substring(0, hash);
            string name = first.Substring(hash + 1);
            if (!Uri.TryCreate(location, UriKind.Absolute, out Uri uri) || !uri.IsFile)
            {
                return null; // cannot verify; no message beats a wrong one
            }

            string path = uri.LocalPath;
            bool found = (Directory.Exists(path) || File.Exists(path)) && CanResolve(first);
            return found ? null : Localizer.Format(Strings.FontFileNotFoundFormat, name, path);
        }

        /// <summary>
        /// Asks WPF to resolve the font instead of searching Fonts.SystemFontFamilies, which misses
        /// faces like "Segoe UI Semibold" and fonts installed per app (Cascadia Code via Windows Terminal).
        /// </summary>
        private static bool CanResolve(string source)
        {
            try
            {
                var typeface = new Typeface(new FontFamily(source), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                return typeface.TryGetGlyphTypeface(out _);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// A gray content box with blue edges as thick as the Thickness, scaled down
        /// proportionally when the largest edge exceeds MaxDrawnEdge.
        /// </summary>
        private static UIElement RenderThickness(Thickness thickness)
        {
            double max = new[] { thickness.Left, thickness.Top, thickness.Right, thickness.Bottom }.Select(Math.Abs).Max();
            double scale = max > MaxDrawnEdge ? MaxDrawnEdge / max : 1;

            var content = new Border { Width = 48, Height = 24, Background = ContentFill };
            var edges = new Border
            {
                // Negative margins are valid, draw them as 0.
                BorderThickness = new Thickness(
                    Math.Max(0, thickness.Left * scale),
                    Math.Max(0, thickness.Top * scale),
                    Math.Max(0, thickness.Right * scale),
                    Math.Max(0, thickness.Bottom * scale)),
                BorderBrush = EdgeFill,
                Child = content,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0),
            };

            string text = Localizer.Format(Strings.ThicknessFormat,
                Number(thickness.Left), Number(thickness.Top), Number(thickness.Right), Number(thickness.Bottom));

            return Stack(edges, PreviewRenderer.CreateText(text, opacity: 0.7));
        }

        private static UIElement RenderCornerRadius(CornerRadius radius)
        {
            double max = new[] { radius.TopLeft, radius.TopRight, radius.BottomRight, radius.BottomLeft }.Max();
            double scale = max > MaxDrawnRadius ? MaxDrawnRadius / max : 1;

            var shape = new Border
            {
                Width = 64,
                Height = 40,
                CornerRadius = new CornerRadius(radius.TopLeft * scale, radius.TopRight * scale, radius.BottomRight * scale, radius.BottomLeft * scale),
                Background = EdgeFill,
                BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0),
            };
            shape.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolTipTextBrushKey);

            string text = Localizer.Format(Strings.CornerRadiusFormat,
                Number(radius.TopLeft), Number(radius.TopRight), Number(radius.BottomRight), Number(radius.BottomLeft));

            return Stack(shape, PreviewRenderer.CreateText(text, opacity: 0.7));
        }

        private static TextBlock CreateValueText(string text)
        {
            TextBlock block = PreviewRenderer.CreateText(text);
            block.FontSize = 14;
            block.FontWeight = FontWeights.SemiBold;
            return block;
        }

        private static StackPanel Stack(params UIElement[] children)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (UIElement child in children)
            {
                panel.Children.Add(child);
            }

            return panel;
        }

        private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
