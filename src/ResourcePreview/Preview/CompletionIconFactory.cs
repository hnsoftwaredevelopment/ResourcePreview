using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using ResourcePreview.Resources;

namespace ResourcePreview.Preview
{
    /// <summary>
    /// A 16×16 icon for the IntelliSense list: the image, geometry or drawing itself, or a color swatch
    /// for a brush or color. Other resources (styles, templates, values) get no icon, so the list keeps
    /// the icon of the XAML editor. Call on the UI thread only.
    /// </summary>
    internal static class CompletionIconFactory
    {
        private const double SwatchSize = 16;

        // One icon per definition. A changed file gives new ResourceEntry objects, so old icons drop out by themselves.
        private static readonly ConditionalWeakTable<ResourceEntry, IconBox> Cache = new ConditionalWeakTable<ResourceEntry, IconBox>();

        private static readonly Pen SwatchBorder = CreateSwatchBorder();

        /// <summary>Remembers "no icon" too, so a style is not parsed again for every keystroke.</summary>
        private sealed class IconBox
        {
            public IconBox(ImageSource icon) => Icon = icon;

            public ImageSource Icon { get; }
        }

        /// <summary>The icon for this definition, or null. <paramref name="foreground"/> colors a geometry.</summary>
        public static ImageSource Get(ResourceIndex index, ResourceEntry entry, Brush foreground)
        {
            if (Cache.TryGetValue(entry, out IconBox cached))
            {
                return cached.Icon;
            }

            ImageSource icon = HasIcon(entry.ElementTypeName) ? Create(index, entry, foreground) : null;
            Cache.Add(entry, new IconBox(icon));
            return icon;
        }

        /// <summary>
        /// Only types that are a picture or a color. Parsing a style or template for every item in the
        /// list would make typing slow, and it has no small picture anyway.
        /// </summary>
        internal static bool HasIcon(string elementTypeName)
        {
            if (string.IsNullOrEmpty(elementTypeName) || elementTypeName == "VisualBrush")
            {
                return false; // a VisualBrush can contain whole controls
            }

            return elementTypeName == "Color"
                || elementTypeName == "DrawingGroup"
                || elementTypeName == "GeometryGroup"
                || elementTypeName == "BitmapFrame"
                || elementTypeName.EndsWith("Geometry", StringComparison.Ordinal)
                || elementTypeName.EndsWith("Drawing", StringComparison.Ordinal)
                || elementTypeName.EndsWith("Brush", StringComparison.Ordinal)
                || elementTypeName.EndsWith("Image", StringComparison.Ordinal);
        }

        private static ImageSource Create(ResourceIndex index, ResourceEntry entry, Brush foreground)
        {
            PreviewXaml xaml = PreviewXamlBuilder.Build(index, entry, CancellationToken.None);
            if (xaml.UnloadableType != null || xaml.MissingKeys.Count > 0)
            {
                return null; // would throw while parsing
            }

            object resource;
            try
            {
                resource = ((ResourceDictionary)XamlReader.Parse(xaml.Xaml))[ResourceKeys.PreviewKey(entry.Key)];
            }
            catch (Exception)
            {
                return null; // broken XAML while typing: no icon, the tooltip shows the error
            }

            return FromResource(resource, foreground);
        }

        /// <summary>Turns a loaded resource into an icon. Internal for testing.</summary>
        internal static ImageSource FromResource(object resource, Brush foreground)
        {
            ImageSource icon;
            switch (resource)
            {
                case Geometry geometry:
                    icon = new DrawingImage(new GeometryDrawing(foreground, null, geometry));
                    break;
                case Drawing drawing:
                    icon = new DrawingImage(drawing);
                    break;
                case ImageSource image:
                    icon = image;
                    break;
                case Brush brush:
                    icon = Swatch(brush);
                    break;
                case Color color:
                    icon = Swatch(new SolidColorBrush(color));
                    break;
                default:
                    return null;
            }

            // Frozen objects can be shared and drawn on any thread. A bitmap that is still downloading cannot be frozen.
            if (icon.CanFreeze)
            {
                icon.Freeze();
            }

            return icon;
        }

        private static ImageSource Swatch(Brush brush)
        {
            var square = new RectangleGeometry(new Rect(0.5, 0.5, SwatchSize - 1, SwatchSize - 1));
            return new DrawingImage(new GeometryDrawing(brush, SwatchBorder, square));
        }

        private static Pen CreateSwatchBorder()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), 1);
            pen.Freeze();
            return pen;
        }
    }
}
