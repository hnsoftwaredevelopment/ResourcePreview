using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ResourcePreview.Localization;
using ResourcePreview.Resources;

namespace ResourcePreview.Preview
{
    /// <summary>
    /// Renders a DataTemplate with sample data, and an ItemsPanelTemplate with a few sample items.
    /// The template comes from <see cref="TemplateSampleBuilder"/>, so own types are already boxes.
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
            var presenter = new ContentPresenter
            {
                ContentTemplate = template,
                Content = CreateData(bindings),
            };

            return Render(presenter, MaxWidth);
        }

        public static LiveSampleRenderer.Result ForItemsPanel(ItemsPanelTemplate template)
        {
            var items = new ItemsControl { ItemsPanel = template, Width = PanelWidth };
            string text = Localizer.Get(Strings.SampleText);
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

            return Render(items, PanelWidth);
        }

        private static LiveSampleRenderer.Result Render(FrameworkElement sample, double width)
        {
            sample.HorizontalAlignment = HorizontalAlignment.Left;
            sample.VerticalAlignment = VerticalAlignment.Top;

            // Keep the sample static and let clicks fall through to the panel, which navigates to the definition.
            sample.IsHitTestVisible = false;
            sample.Focusable = false;

            var frame = new Border
            {
                Child = sample,
                MaxWidth = width,
                MaxHeight = MaxHeight,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 2),
            };

            try
            {
                // Measure applies the template, so errors in it are thrown here and not later in the tooltip.
                frame.Measure(new Size(width, MaxHeight));
            }
            catch (Exception ex)
            {
                return new LiveSampleRenderer.Result(null, Localizer.Format(Strings.NoLiveSampleFormat, LiveSampleRenderer.FirstLine(ex)));
            }

            return new LiveSampleRenderer.Result(frame, null);
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
