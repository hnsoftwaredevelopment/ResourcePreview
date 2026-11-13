using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using ResourcePreview.Localization;
using ResourcePreview.Options;
using ResourcePreview.Resources;

namespace ResourcePreview.Preview
{
    /// <summary>
    /// Renders a DataTemplate with sample data, and an ItemsPanelTemplate with a few sample items.
    /// The template comes from <see cref="TemplateSampleBuilder"/>, so own types are already boxes.
    ///
    /// The sample is shown in the current theme. A template often sets its own colors (Foreground="White" for a dark
    /// app); when those do not read on the theme background, the sample gets a background they do read on.
    /// Call on the UI thread only.
    /// </summary>
    internal static class TemplateSampleRenderer
    {
        private const double MaxWidth = 320;
        private const double MaxHeight = 180;
        private const double PanelWidth = 260;
        private const int PanelItems = 6;

        public static LiveSampleRenderer.Result ForDataTemplate(DataTemplate template, IReadOnlyList<SampleBinding> bindings)
        {
            return Render(() => new ContentPresenter
            {
                ContentTemplate = template,
                Content = CreateData(bindings),
            }, MaxWidth);
        }

        public static LiveSampleRenderer.Result ForItemsPanel(ItemsPanelTemplate template)
        {
            string text = Localizer.Get(Strings.SampleText);
            return Render(() =>
            {
                var items = new ItemsControl { ItemsPanel = template, Width = PanelWidth };
                for (int i = 1; i <= PanelItems; i++)
                {
                    items.Items.Add(new Border
                    {
                        BorderBrush = PreviewRenderer.SampleItemBorder,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(2),
                        Child = new TextBlock { Text = text + " " + i },
                    });
                }

                return items;
            }, PanelWidth);
        }

        /// <summary>
        /// One sample in the current theme: the tooltip background, and the theme text color for everything that
        /// sets no color itself. When the colors the template sets itself (Foreground="White" for a dark app) are hard
        /// to read on that background, the sample gets a background they do read on. See <see cref="ChooseBackground"/>.
        /// </summary>
        private static LiveSampleRenderer.Result Render(Func<FrameworkElement> create, double width)
        {
            FrameworkElement sample = create();
            sample.HorizontalAlignment = HorizontalAlignment.Left;
            sample.VerticalAlignment = VerticalAlignment.Top;

            // Keep the sample static and let clicks fall through to the panel, which navigates to the definition.
            sample.IsHitTestVisible = false;
            sample.Focusable = false;

            var frame = new Border
            {
                Child = sample,
                BorderBrush = PreviewRenderer.SampleItemBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                MaxWidth = width + 14,
                MaxHeight = MaxHeight,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 2),
            };

            // Our own TextBlocks get the theme color explicitly, so it does not inherit into the sample. Set it on the
            // frame: TextElement.Foreground inherits to every TextBlock and Control that sets no color itself.
            frame.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolTipTextBrushKey);

            try
            {
                // Measure applies the template, so errors in it are thrown here and not later in the tooltip.
                frame.Measure(new Size(width + 14, MaxHeight));
            }
            catch (Exception ex)
            {
                return new LiveSampleRenderer.Result(null, Localizer.Format(Strings.NoLiveSampleFormat, LiveSampleRenderer.FirstLine(ex)));
            }

            Color theme = ThemeColor(EnvironmentColors.ToolTipBrushKey) ?? Colors.White;
            Color? other = ChooseBackground(OwnColors(sample), theme);
            if (other.HasValue)
            {
                frame.Background = PreviewRenderer.ToBrush(other.Value);
                TextElement.SetForeground(frame, PreviewRenderer.ToBrush(Luminance(other.Value) < 0.5 ? Colors.White : Colors.Black));
            }

            return new LiveSampleRenderer.Result(frame, null);
        }

        private static Color? ThemeColor(object key) =>
            (Application.Current?.TryFindResource(key) as SolidColorBrush)?.Color;

        /// <summary>The dark and light backgrounds a sample can switch to: the tile colors from the settings.</summary>
        private static IEnumerable<Color> OtherBackgrounds()
        {
            ExtensionSettings settings = SettingsService.Current;
            yield return settings.DarkBackground;
            yield return settings.LightBackground;
        }

        /// <summary>
        /// Null to keep the theme background. The theme stays when every color of the template itself has a contrast
        /// of at least 3 with it (the WCAG minimum for large text and icons). Otherwise the other background on which
        /// the worst color reads best, but only when it reaches that minimum: with white and black text together no
        /// background helps, and a slightly better one is not worth leaving the theme for. Internal for testing.
        /// </summary>
        internal static Color? ChooseBackground(IReadOnlyCollection<Color> ownColors, Color theme, IEnumerable<Color> others = null)
        {
            const double Readable = 3.0;
            if (ownColors.Count == 0)
            {
                return null;
            }

            double Worst(Color background) => ownColors.Min(c => Contrast(c, background));

            double themeScore = Worst(theme);
            if (themeScore >= Readable)
            {
                return null;
            }

            Color? best = null;
            double bestScore = Readable;
            foreach (Color candidate in others ?? OtherBackgrounds())
            {
                double score = Worst(candidate);
                if (score >= bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best;
        }

        /// <summary>
        /// The solid colors the template sets itself on text and shapes: Foreground, Fill and Stroke set in the template
        /// or a style, not inherited and not from the default look of a control. Colors on top of a background the
        /// template sets itself (a dark card) are skipped: they do not depend on the background behind the sample.
        /// </summary>
        internal static IReadOnlyCollection<Color> OwnColors(DependencyObject root)
        {
            var colors = new List<Color>();

            void Visit(DependencyObject node)
            {
                if (OwnSolidColor(node, BackgroundProperty(node)).HasValue)
                {
                    return; // everything inside sits on the template's own background
                }

                foreach (DependencyProperty property in ColorProperties(node))
                {
                    Color? color = OwnSolidColor(node, property);
                    if (color.HasValue && color.Value.A > 0x40)
                    {
                        colors.Add(color.Value);
                    }
                }

                if (node is Control && node != root)
                {
                    return; // a TextBox or Button draws its inside on its own background
                }

                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    Visit(VisualTreeHelper.GetChild(node, i));
                }
            }

            Visit(root);
            return colors;
        }

        private static DependencyProperty BackgroundProperty(DependencyObject node)
        {
            switch (node)
            {
                case Border _: return Border.BackgroundProperty;
                case Panel _: return Panel.BackgroundProperty;
                case Control _: return Control.BackgroundProperty;
                default: return null;
            }
        }

        private static IEnumerable<DependencyProperty> ColorProperties(DependencyObject node)
        {
            switch (node)
            {
                case TextBlock _:
                    yield return TextBlock.ForegroundProperty;
                    break;
                case Control control when IsTransparent(control.Background):
                    // A Label draws its text on whatever is behind it; a TextBox on its own white field.
                    yield return Control.ForegroundProperty;
                    break;
                case System.Windows.Shapes.Shape _:
                    yield return System.Windows.Shapes.Shape.FillProperty;
                    yield return System.Windows.Shapes.Shape.StrokeProperty;
                    break;
            }
        }

        private static bool IsTransparent(Brush brush) => brush == null || (brush is SolidColorBrush solid && solid.Color.A <= 0x40);

        /// <summary>The color when the template or a style sets the property to a solid brush; null when inherited or default.</summary>
        private static Color? OwnSolidColor(DependencyObject node, DependencyProperty property)
        {
            if (property == null)
            {
                return null;
            }

            switch (DependencyPropertyHelper.GetValueSource(node, property).BaseValueSource)
            {
                case BaseValueSource.Local:
                case BaseValueSource.ParentTemplate:
                case BaseValueSource.ParentTemplateTrigger:
                case BaseValueSource.Style:
                case BaseValueSource.StyleTrigger:
                case BaseValueSource.TemplateTrigger:
                case BaseValueSource.ImplicitStyleReference:
                    return (node.GetValue(property) as SolidColorBrush)?.Color;
                default:
                    return null;
            }
        }

        /// <summary>WCAG contrast ratio: 1 for the same color, 21 for black on white.</summary>
        internal static double Contrast(Color a, Color b)
        {
            double la = Luminance(a);
            double lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>WCAG relative luminance, 0 for black and 1 for white.</summary>
        internal static double Luminance(Color color)
        {
            double Channel(byte value)
            {
                double c = value / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
        }

        /// <summary>
        /// Sample data for the bindings: text shows the name of the path ({Binding Title} shows "Title"), a list
        /// gets three items, and everything else the default value of the property, so it looks as if unbound.
        /// "Author.Name" makes a nested object. Internal for testing.
        /// </summary>
        internal static SampleObject CreateData(IReadOnlyList<SampleBinding> bindings)
        {
            var root = new SampleObject(Localizer.Get(Strings.SampleText));

            // Text first: when one path feeds a TextBlock and a ToolTip, the text wins.
            foreach (SampleBinding binding in bindings.OrderBy(b => IsText(PropertyType(b)) ? 0 : 1))
            {
                if (binding.Path.Length == 0)
                {
                    continue; // {Binding}: the object itself, shown with its ToString
                }

                string[] segments = binding.Path.Split('.');
                SampleObject owner = root;
                for (int i = 0; i < segments.Length - 1; i++)
                {
                    if (!(owner.Values.TryGetValue(segments[i], out object child) && child is SampleObject nested))
                    {
                        nested = new SampleObject(segments[i]);
                        owner.Values[segments[i]] = nested;
                    }

                    owner = nested;
                }

                string name = segments[segments.Length - 1];
                if (!owner.Values.ContainsKey(name))
                {
                    owner.Values[name] = SampleValue(binding, name);
                }
            }

            return root;
        }

        private static object SampleValue(SampleBinding binding, string name)
        {
            Type type = PropertyType(binding);
            if (IsText(type))
            {
                return name;
            }

            if (typeof(IEnumerable).IsAssignableFrom(type))
            {
                return Enumerable.Range(1, 3).Select(i => name + " " + i).ToArray();
            }

            return DefaultValue(binding, type);
        }

        private static bool IsText(Type type) => type == null || type == typeof(object) || type == typeof(string);

        /// <summary>The type of the bound property: TextBox.Text is string, Grid.Row (attached) is int. Null when unknown.</summary>
        internal static Type PropertyType(SampleBinding binding)
        {
            int dot = binding.Property.LastIndexOf('.');
            if (dot > 0)
            {
                Type owner = ResourceMatcher.FindWpfType(binding.Property.Substring(0, dot));
                return owner?.GetMethod("Get" + binding.Property.Substring(dot + 1), BindingFlags.Public | BindingFlags.Static)?.ReturnType;
            }

            // GetProperty(name) throws when a subclass hides a property with "new", so search the list.
            return binding.ElementType?.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name == binding.Property)?.PropertyType;
        }

        private static object DefaultValue(SampleBinding binding, Type type)
        {
            int dot = binding.Property.LastIndexOf('.');
            Type owner = dot > 0 ? ResourceMatcher.FindWpfType(binding.Property.Substring(0, dot)) : binding.ElementType;
            string name = dot > 0 ? binding.Property.Substring(dot + 1) : binding.Property;

            var property = owner?.GetField(name + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as DependencyProperty;
            if (property != null)
            {
                return property.GetMetadata(owner).DefaultValue;
            }

            return type != null && type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    /// <summary>
    /// A data object with properties that only exist at runtime. WPF reads it through TypeDescriptor, the same way
    /// it binds to a DataRowView. Unlike ExpandoObject this does not go through the DLR, which throws and catches
    /// an exception for every member it looks up.
    /// </summary>
    internal sealed class SampleObject : ICustomTypeDescriptor
    {
        private readonly string _text;

        public SampleObject(string text)
        {
            _text = text;
        }

        public Dictionary<string, object> Values { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

        public override string ToString() => _text;

        public PropertyDescriptorCollection GetProperties() =>
            new PropertyDescriptorCollection(Values.Select(v => (PropertyDescriptor)new ValueProperty(v.Key, v.Value?.GetType() ?? typeof(object))).ToArray());

        public PropertyDescriptorCollection GetProperties(Attribute[] attributes) => GetProperties();

        public AttributeCollection GetAttributes() => AttributeCollection.Empty;

        public string GetClassName() => nameof(SampleObject);

        public string GetComponentName() => null;

        public TypeConverter GetConverter() => null;

        public EventDescriptor GetDefaultEvent() => null;

        public PropertyDescriptor GetDefaultProperty() => null;

        public object GetEditor(Type editorBaseType) => null;

        public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;

        public EventDescriptorCollection GetEvents(Attribute[] attributes) => EventDescriptorCollection.Empty;

        public object GetPropertyOwner(PropertyDescriptor pd) => this;

        private sealed class ValueProperty : PropertyDescriptor
        {
            private readonly Type _type;

            public ValueProperty(string name, Type type)
                : base(name, null)
            {
                _type = type;
            }

            public override Type ComponentType => typeof(SampleObject);

            public override bool IsReadOnly => false;

            public override Type PropertyType => _type;

            public override bool CanResetValue(object component) => false;

            public override object GetValue(object component) => ((SampleObject)component).Values[Name];

            public override void ResetValue(object component)
            {
            }

            // A TextBox with Mode=TwoWay writes back; keep the value so the binding stays quiet.
            public override void SetValue(object component, object value) => ((SampleObject)component).Values[Name] = value;

            public override bool ShouldSerializeValue(object component) => false;
        }
    }
}
