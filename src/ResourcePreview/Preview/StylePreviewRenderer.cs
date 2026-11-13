using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using ResourcePreview.Localization;
using ResourcePreview.Resources;

namespace ResourcePreview.Preview
{
    /// <summary>
    /// Renders a Style as a table:
    ///
    ///   Button · BasedOn BaseButton
    ///   Foreground   ■ White
    ///   FontSize     15
    ///   Template     ControlTemplate
    ///   from BaseButton
    ///   Padding      8,4
    ///   Background   ■ AccentBrush (#FF002BBD)
    ///   Triggers (2): IsMouseOver, IsPressed
    ///
    /// Call on the UI thread only.
    /// </summary>
    internal static class StylePreviewRenderer
    {
        private const double SwatchSize = 12;
        private const double IconSize = 16;
        private const int MaxValueLength = 60;

        private static readonly Brush SwatchBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));

        public static UIElement Render(StyleInfo style, LiveSampleRenderer.Result sample = null)
        {
            var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            string header = string.Join(" · ", new[]
            {
                style.TargetType,
                style.BasedOn == null ? null : "BasedOn " + style.BasedOn,
            }.Where(p => p != null));

            if (header.Length > 0)
            {
                root.Children.Add(PreviewRenderer.CreateText(header, opacity: 0.8));
            }

            if (sample?.Sample != null)
            {
                root.Children.Add(sample.Sample);
            }
            else if (sample?.Problem != null)
            {
                TextBlock note = PreviewRenderer.CreateText(sample.Problem, opacity: 0.6);
                note.FontStyle = FontStyles.Italic;
                root.Children.Add(note);
            }

            if (style.Setters.Count == 0)
            {
                root.Children.Add(PreviewRenderer.CreateText(Localizer.Get(Strings.StyleNoSetters), opacity: 0.7));
            }
            else
            {
                root.Children.Add(CreateSetterTable(style));
            }

            if (style.Triggers.Count > 0)
            {
                // The count includes every trigger, but each property name is listed once.
                string names = string.Join(", ", style.Triggers.Distinct());
                root.Children.Add(PreviewRenderer.CreateText(Localizer.Format(Strings.StyleTriggersFormat, style.Triggers.Count, names), opacity: 0.8));
            }

            return root;
        }

        private static Grid CreateSetterTable(StyleInfo style)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string currentSource = null;
            foreach (SetterInfo setter in style.Setters)
            {
                // Setters from a base style get a subheading with the style name.
                if (setter.InheritedFrom != currentSource)
                {
                    currentSource = setter.InheritedFrom;
                    TextBlock separator = PreviewRenderer.CreateText(Localizer.Format(Strings.StyleInheritedFromFormat, currentSource), opacity: 0.6);
                    separator.FontStyle = FontStyles.Italic;
                    AddRow(grid, separator, null);
                }

                TextBlock name = PreviewRenderer.CreateText(setter.Property, opacity: 0.8);
                name.Margin = new Thickness(0, 2, 12, 0);
                AddRow(grid, name, CreateValue(setter));
            }

            return grid;
        }

        private static void AddRow(Grid grid, UIElement first, UIElement second)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(first, row);
            Grid.SetColumn(first, 0);
            if (second == null)
            {
                Grid.SetColumnSpan(first, 2);
            }

            grid.Children.Add(first);

            if (second != null)
            {
                Grid.SetRow(second, row);
                Grid.SetColumn(second, 1);
                grid.Children.Add(second);
            }
        }

        private static UIElement CreateValue(SetterInfo setter)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

            UIElement visual = CreateValueVisual(setter);
            if (visual != null)
            {
                panel.Children.Add(visual);
            }

            TextBlock text = PreviewRenderer.CreateText(Shorten(DescribeValue(setter)));
            text.Margin = new Thickness(0);
            text.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(text);
            return panel;
        }

        /// <summary>"AccentBrush (#FF002BBD)", "Renew (GeometryGroup)", "15", "ControlTemplate".</summary>
        internal static string DescribeValue(SetterInfo setter)
        {
            switch (setter.Kind)
            {
                case SetterValueKind.ResourceReference:
                    string extra = setter.ColorText ?? setter.ResourceTypeName;
                    return extra == null ? setter.ValueText : $"{setter.ValueText} ({extra})";

                case SetterValueKind.Element:
                    return setter.ColorText == null ? setter.ValueText : $"{setter.ValueText} ({setter.ColorText})";

                default:
                    return setter.ValueText;
            }
        }

        /// <summary>A color swatch, a small icon, or null.</summary>
        private static UIElement CreateValueVisual(SetterInfo setter)
        {
            if (setter.ColorText != null)
            {
                return TryParseColor(setter.ColorText, out Color color) ? CreateSwatch(new SolidColorBrush(color)) : null;
            }

            if (setter.Kind != SetterValueKind.ResourceReference || setter.ResourceXaml == null)
            {
                return null;
            }

            object resource;
            try
            {
                resource = ((ResourceDictionary)XamlReader.Parse(setter.ResourceXaml))[ResourceKeys.PreviewKey(setter.ValueText)];
            }
            catch (Exception)
            {
                return null; // the text is shown anyway, the icon is optional
            }

            if (resource is Brush brush)
            {
                return CreateSwatch(brush);
            }

            Func<Brush, UIElement> factory = PreviewRenderer.GetVisualFactory(resource, out _);
            if (factory == null)
            {
                return null;
            }

            UIElement icon = factory(TextBrush());
            if (icon is FrameworkElement element)
            {
                element.Width = IconSize;
                element.Height = IconSize;
                element.Margin = new Thickness(0, 0, 4, 0);
                element.VerticalAlignment = VerticalAlignment.Center;
            }

            return icon;
        }

        private static Border CreateSwatch(Brush fill)
        {
            return new Border
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Background = fill,
                BorderBrush = SwatchBorder,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        /// <summary>VS tooltip text brush so geometry icons match the theme. Falls back to gray outside VS.</summary>
        private static Brush TextBrush()
        {
            return Application.Current?.TryFindResource(EnvironmentColors.ToolTipTextBrushKey) as Brush ?? Brushes.Gray;
        }

        private static bool TryParseColor(string text, out Color color)
        {
            return ColorParser.TryParse(text, out color);
        }

        private static string Shorten(string text) =>
            text.Length <= MaxValueLength ? text : text.Substring(0, MaxValueLength - 1) + "…";

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
