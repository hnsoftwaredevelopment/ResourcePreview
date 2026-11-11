using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Microsoft.VisualStudio.PlatformUI;
using ResourcePreview.Localization;
using ResourcePreview.Options;

namespace ResourcePreview.Preview
{
    /// <summary>
    /// Parses the resource XAML into WPF objects and builds the preview.
    /// Call on the UI thread only.
    /// </summary>
    internal static class PreviewRenderer
    {
        private static readonly Brush TileBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));

        public static UIElement Render(ResourcePreviewData data)
        {
            if (data.Style != null)
            {
                var table = (Panel)StylePreviewRenderer.Render(data.Style, CreateStyleSample(data));
                MakeClickable(table, data.Navigate);
                return table;
            }

            // Visual Studio cannot load types from the user's project or NuGet packages, so XamlReader
            // would always throw. Skip parsing.
            if (data.UnloadableType != null)
            {
                if (data.TemplateTree != null)
                {
                    // The tree comes from the XML and still works. DataTemplates are never rendered live, so no note.
                    string note = data.TemplateTree.TemplateKind == "ControlTemplate" ? LiveSampleRenderer.OwnTypeNote(data.UnloadableType) : null;
                    return CreateTemplatePreview(data, null, note);
                }

                return CreateText(Localizer.Format(
                    global::ResourcePreview.Resources.PreviewXamlBuilder.IsBlockedType(data.UnloadableType) ? Strings.BlockedTypeFormat : Strings.NoPreviewOwnTypeFormat,
                    data.UnloadableType));
            }

            // A missing key would make XamlReader throw. Caught or not, a debugger with "break when thrown"
            // stops there, so say it without parsing.
            if (data.MissingKeys.Count > 0)
            {
                string missing = Localizer.Format(Strings.MissingInSolutionFormat, string.Join(", ", data.MissingKeys));
                return data.TemplateTree != null
                    ? CreateTemplatePreview(data, null, data.TemplateTree.TemplateKind == "ControlTemplate" ? Localizer.Format(Strings.NoLiveSampleFormat, missing) : null)
                    : CreateText(missing);
            }

            object resource;
            try
            {
                var dictionary = (ResourceDictionary)XamlReader.Parse(data.Xaml);
                resource = dictionary[data.Key];
            }
            catch (Exception ex)
            {
                // A template with custom types fails to load, but the tree from the XML still works.
                if (data.TemplateTree != null)
                {
                    string note = data.TemplateTree.TemplateKind == "ControlTemplate"
                        ? Localizer.Format(Strings.NoLiveSampleFormat, LiveSampleRenderer.FirstLine(ex))
                        : null;
                    return CreateTemplatePreview(data, null, note);
                }

                return CreateError(data, ex);
            }

            if (resource is ControlTemplate controlTemplate && SettingsService.Current.ShowLiveSample)
            {
                LiveSampleRenderer.Result sample = LiveSampleRenderer.ForTemplate(controlTemplate);
                return CreateTemplatePreview(data, sample.Sample, sample.Problem);
            }

            // Data and panel templates have nothing to render without data, so show the tree.
            if (resource is FrameworkTemplate && data.TemplateTree != null)
            {
                return CreateTemplatePreview(data, null, null);
            }

            Func<Brush, UIElement> createVisual = GetVisualFactory(resource, out string details);
            if (createVisual == null)
            {
                UIElement value = ValuePreviewRenderer.TryRender(resource);
                if (value is Panel valuePanel)
                {
                    MakeClickable(valuePanel, data.Navigate);
                    return valuePanel;
                }

                return CreateText(Localizer.Format(Strings.NoPreviewForFormat, resource?.GetType().Name ?? "null"));
            }

            // A Geometry has no color of its own, so show it on both a light and a dark tile.
            // Tile size and colors come from the options page.
            ExtensionSettings settings = SettingsService.Current;
            var tiles = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };

            if (settings.Tiles != PreviewTiles.Dark)
            {
                tiles.Children.Add(CreateTile(createVisual(ToBrush(settings.LightForeground)), ToBrush(settings.LightBackground), settings.TileSize));
            }

            if (settings.Tiles != PreviewTiles.Light)
            {
                tiles.Children.Add(CreateTile(createVisual(ToBrush(settings.DarkForeground)), ToBrush(settings.DarkBackground), settings.TileSize));
            }

            MakeClickable(tiles, data.Navigate);

            var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            root.Children.Add(tiles);

            if (!string.IsNullOrEmpty(details))
            {
                root.Children.Add(CreateText(details, opacity: 0.7));
            }

            return root;
        }

        /// <summary>
        /// Loads the Style for the live sample. The setter table comes from the XML, so a load
        /// failure only adds a note and the table still shows.
        /// </summary>
        private static LiveSampleRenderer.Result CreateStyleSample(ResourcePreviewData data)
        {
            if (string.IsNullOrEmpty(data.Xaml) || !SettingsService.Current.ShowLiveSample)
            {
                return null;
            }

            if (data.UnloadableType != null)
            {
                return new LiveSampleRenderer.Result(null, LiveSampleRenderer.OwnTypeNote(data.UnloadableType));
            }

            // A Style for a Setter, GradientStop or other element that cannot have a style: WPF refuses the
            // TargetType. There is nothing to show live, and loading would only throw.
            string targetType = data.Style?.TargetType;
            if (targetType != null && !targetType.Contains(":") && global::ResourcePreview.Resources.ResourceMatcher.FindWpfType(targetType) != null && !global::ResourcePreview.Resources.ResourceMatcher.CanHaveStyle(targetType))
            {
                return null;
            }

            if (data.MissingKeys.Count > 0)
            {
                // Loading would throw on the missing key; the setter table shows anyway.
                string missing = Localizer.Format(Strings.MissingInSolutionFormat, string.Join(", ", data.MissingKeys));
                return new LiveSampleRenderer.Result(null, Localizer.Format(Strings.NoLiveSampleFormat, missing));
            }

            try
            {
                var dictionary = (ResourceDictionary)XamlReader.Parse(data.Xaml);
                return dictionary[data.Key] is Style style ? LiveSampleRenderer.ForStyle(style) : null;
            }
            catch (Exception ex)
            {
                return new LiveSampleRenderer.Result(null, Localizer.Format(Strings.NoLiveSampleFormat, LiveSampleRenderer.FirstLine(ex)));
            }
        }

        /// <summary>
        /// Shows the live sample if there is one, otherwise the template tree, plus an optional note.
        /// </summary>
        private static UIElement CreateTemplatePreview(ResourcePreviewData data, UIElement sample, string note)
        {
            var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            if (sample != null)
            {
                if (data.TemplateTree?.TargetType != null)
                {
                    root.Children.Add(CreateText("TargetType " + data.TemplateTree.TargetType, opacity: 0.8));
                }

                root.Children.Add(sample);
            }
            else if (data.TemplateTree != null)
            {
                root.Children.Add(TemplateTreeRenderer.Render(data.TemplateTree));
            }

            if (note != null)
            {
                TextBlock noteText = CreateText(note, opacity: 0.6);
                noteText.FontStyle = FontStyles.Italic;
                root.Children.Add(noteText);
            }

            MakeClickable(root, data.Navigate);
            return root;
        }

        /// <summary>
        /// Returns a factory that draws the resource. The factory argument is the foreground brush,
        /// used for Geometry and Effect samples.
        /// </summary>
        internal static Func<Brush, UIElement> GetVisualFactory(object resource, out string details)
        {
            details = null;

            switch (resource)
            {
                case Geometry geometry:
                    Rect bounds = geometry.Bounds;
                    details = bounds.IsEmpty ? null : $"{geometry.GetType().Name}, {bounds.Width:0.##} × {bounds.Height:0.##}";
                    return foreground => new Path { Data = geometry, Fill = foreground, Stretch = Stretch.Uniform };

                case Drawing drawing:
                    var drawingImage = new DrawingImage(drawing);
                    details = $"{drawing.GetType().Name}, {drawing.Bounds.Width:0.##} × {drawing.Bounds.Height:0.##}";
                    return _ => new Image { Source = drawingImage, Stretch = Stretch.Uniform };

                case ImageSource imageSource:
                    details = $"{imageSource.GetType().Name}, {imageSource.Width:0.##} × {imageSource.Height:0.##}";
                    return _ => new Image { Source = imageSource, Stretch = Stretch.Uniform };

                case Brush brush:
                    details = brush is SolidColorBrush solid ? $"SolidColorBrush {solid.Color}" : brush.GetType().Name;
                    return _ => new Rectangle { Fill = brush };

                case Color color:
                    details = $"Color {color}";
                    return _ => new Rectangle { Fill = new SolidColorBrush(color) };

                case Effect effect:
                    details = DescribeEffect(effect);
                    return foreground => CreateEffectSample(effect, foreground);

                default:
                    return null;
            }
        }

        /// <summary>
        /// A square with the effect applied. The Viewbox scales with the tile size so small tiles
        /// still leave room for the shadow or blur.
        /// </summary>
        private static UIElement CreateEffectSample(Effect effect, Brush foreground)
        {
            var canvas = new Grid { Width = 48, Height = 48 };
            canvas.Children.Add(new Rectangle { Margin = new Thickness(10), Fill = foreground, RadiusX = 3, RadiusY = 3, Effect = effect });
            return new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        }

        /// <summary>"DropShadowEffect · Color #FF000000 · BlurRadius 8 · ShadowDepth 3 · Direction 315 · Opacity 0.5"</summary>
        internal static string DescribeEffect(Effect effect)
        {
            string N(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

            switch (effect)
            {
                case DropShadowEffect shadow:
                    return $"DropShadowEffect · Color {shadow.Color} · BlurRadius {N(shadow.BlurRadius)} · ShadowDepth {N(shadow.ShadowDepth)} · Direction {N(shadow.Direction)} · Opacity {N(shadow.Opacity)}";

                case BlurEffect blur:
                    return $"BlurEffect · Radius {N(blur.Radius)} · {blur.KernelType}";

                default:
                    return effect.GetType().Name;
            }
        }

        private static UIElement CreateTile(UIElement visual, Brush background, double size)
        {
            if (visual is FrameworkElement element)
            {
                element.Width = size;
                element.Height = size;
            }

            return new Border
            {
                Background = background,
                BorderBrush = TileBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                Margin = new Thickness(0, 0, 6, 0),
                Child = visual,
            };
        }

        private static UIElement CreateError(ResourcePreviewData data, Exception ex)
        {
            string message = ex.Message.Length > 300 ? ex.Message.Substring(0, 300) + "…" : ex.Message;
            string text = Localizer.Format(Strings.CannotLoadFormat, data.ElementTypeName, data.Key, message);

            if (data.MissingKeys.Count > 0)
            {
                text += "\n" + Localizer.Format(Strings.MissingInSolutionFormat, string.Join(", ", data.MissingKeys));
            }

            return CreateText(text);
        }

        internal static TextBlock CreateText(string text, double opacity = 1.0)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 500,
                Margin = new Thickness(0, 4, 0, 0),
                Opacity = opacity,
            };

            // Use a resource reference so the color follows VS theme changes.
            block.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolTipTextBrushKey);
            return block;
        }

        /// <summary>
        /// Clicking navigates to the definition. A Panel needs a non-null Background to receive
        /// clicks on the empty space between its children.
        /// </summary>
        private static void MakeClickable(Panel panel, Action navigate)
        {
            if (navigate == null)
            {
                return;
            }

            panel.Background = Brushes.Transparent;
            panel.Cursor = Cursors.Hand;
            panel.MouseLeftButtonUp += (sender, e) =>
            {
                e.Handled = true;
                navigate();
            };
        }

        private static Brush ToBrush(Color color) => Freeze(new SolidColorBrush(color));

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
