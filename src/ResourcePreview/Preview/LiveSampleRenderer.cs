using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using ResourcePreview.Localization;

namespace ResourcePreview.Preview
{
    /// <summary>
    /// Renders a real control with the Style or ControlTemplate applied, normal and disabled:
    ///
    ///   [ Sample ]   [ Sample ]
    ///     Normal      Disabled
    ///
    /// IsMouseOver and IsPressed cannot be simulated, WPF sets them from the actual mouse.
    /// Only works for types Visual Studio can load, not for controls from the user's assembly.
    /// Call on the UI thread only.
    /// </summary>
    internal static class LiveSampleRenderer
    {
        private const double MaxSampleWidth = 220;
        private const double MaxSampleHeight = 120;

        /// <summary>The rendered sample, or the reason there is none.</summary>
        internal sealed class Result
        {
            public Result(UIElement sample, string problem)
            {
                Sample = sample;
                Problem = problem;
            }

            public UIElement Sample { get; }

            /// <summary>Message for the user, or null when no sample applies (for example a Window).</summary>
            public string Problem { get; }
        }

        public static Result ForStyle(Style style)
        {
            return Create(style.TargetType, element => element.Style = style, HasIsEnabledTrigger(style));
        }

        public static Result ForTemplate(ControlTemplate template)
        {
            // Without a TargetType, use ContentControl so a ContentPresenter has something to show.
            Type type = template.TargetType ?? typeof(ContentControl);
            if (!typeof(Control).IsAssignableFrom(type))
            {
                return new Result(null, null);
            }

            return Create(type, element => ((Control)element).Template = template, HasIsEnabledTrigger(template.Triggers));
        }

        /// <summary>
        /// True if the style reacts to IsEnabled, so a disabled sample would look different.
        /// Checks the whole BasedOn chain and any ControlTemplate set through a setter.
        /// </summary>
        internal static bool HasIsEnabledTrigger(Style style)
        {
            for (Style current = style; current != null; current = current.BasedOn)
            {
                if (HasIsEnabledTrigger(current.Triggers))
                {
                    return true;
                }

                foreach (Setter setter in current.Setters.OfType<Setter>())
                {
                    if (setter.Property == Control.TemplateProperty && setter.Value is ControlTemplate template && HasIsEnabledTrigger(template.Triggers))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static bool HasIsEnabledTrigger(TriggerCollection triggers) => triggers.Any(IsEnabledTrigger);

        /// <summary>Matches Trigger, MultiTrigger, DataTrigger and MultiDataTrigger conditions on IsEnabled.</summary>
        private static bool IsEnabledTrigger(TriggerBase trigger)
        {
            switch (trigger)
            {
                case Trigger single:
                    return single.Property == UIElement.IsEnabledProperty;
                case MultiTrigger multi:
                    return multi.Conditions.Any(c => c.Property == UIElement.IsEnabledProperty);
                case DataTrigger data:
                    return IsEnabledBinding(data.Binding);
                case MultiDataTrigger multiData:
                    return multiData.Conditions.Any(c => IsEnabledBinding(c.Binding));
                default:
                    return false;
            }
        }

        private static bool IsEnabledBinding(BindingBase binding) =>
            binding is Binding b && b.Path != null && (b.Path.Path == "IsEnabled" || b.Path.Path.EndsWith(".IsEnabled", StringComparison.Ordinal));

        private static Result Create(Type type, Action<FrameworkElement> apply, bool showDisabled)
        {
            if (!CanCreate(type))
            {
                return new Result(null, null);
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
            try
            {
                panel.Children.Add(CreateColumn(type, apply, enabled: true, showDisabled ? Localizer.Get(Strings.SampleNormal) : null));
                if (showDisabled)
                {
                    panel.Children.Add(CreateColumn(type, apply, enabled: false, Localizer.Get(Strings.SampleDisabled)));
                }
            }
            catch (Exception ex)
            {
                // Template errors only surface during Measure. Catch them here, otherwise they
                // happen later inside the VS tooltip and nothing is shown.
                return new Result(null, Localizer.Format(Strings.NoLiveSampleFormat, FirstLine(ex)));
            }

            return new Result(panel, null);
        }

        /// <summary>True if the type can be instantiated and hosted in a StackPanel.</summary>
        internal static bool CanCreate(Type type)
        {
            if (type == null
                || !typeof(FrameworkElement).IsAssignableFrom(type)
                || type.IsAbstract
                || type.GetConstructor(Type.EmptyTypes) == null)
            {
                return false;
            }

            // WPF throws when these types get a StackPanel as parent.
            return !typeof(Window).IsAssignableFrom(type)
                && !typeof(Page).IsAssignableFrom(type)
                && !typeof(Popup).IsAssignableFrom(type)
                && !typeof(ToolTip).IsAssignableFrom(type)
                && !typeof(ContextMenu).IsAssignableFrom(type);
        }

        private static UIElement CreateColumn(Type type, Action<FrameworkElement> apply, bool enabled, string label)
        {
            var element = (FrameworkElement)Activator.CreateInstance(type);
            FillContent(element);
            element.HorizontalAlignment = HorizontalAlignment.Left;
            element.VerticalAlignment = VerticalAlignment.Top;
            element.IsEnabled = enabled;

            // Keep the sample static and let clicks fall through to the panel, which navigates to the definition.
            element.IsHitTestVisible = false;
            element.Focusable = false;

            apply(element);

            var frame = new Border
            {
                Child = element,
                MaxWidth = MaxSampleWidth,
                MaxHeight = MaxSampleHeight,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            // Measure applies the template, so template errors are thrown inside the caller's try/catch.
            frame.Measure(new Size(MaxSampleWidth, MaxSampleHeight));

            var column = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            column.Children.Add(frame);
            if (label != null)
            {
                column.Children.Add(PreviewRenderer.CreateText(label, opacity: 0.6));
            }

            return column;
        }

        /// <summary>Fills the control with sample content so it does not render empty.</summary>
        internal static void FillContent(FrameworkElement element)
        {
            string sample = Localizer.Get(Strings.SampleText);

            switch (element)
            {
                case TextBox textBox:
                    textBox.Text = sample;
                    break;
                case PasswordBox passwordBox:
                    passwordBox.Password = "sample";
                    break;
                case TextBlock textBlock:
                    textBlock.Text = sample;
                    break;
                case HeaderedContentControl headered:
                    headered.Header = sample;
                    headered.Content = sample;
                    break;
                case ContentControl content:
                    content.Content = sample;
                    break;
                case HeaderedItemsControl headeredItems:
                    headeredItems.Header = sample;
                    break;
                case ComboBox comboBox:
                    comboBox.Items.Add(sample);
                    comboBox.SelectedIndex = 0;
                    break;
                case ItemsControl items:
                    items.Items.Add(sample + " 1");
                    items.Items.Add(sample + " 2");
                    break;
                case RangeBase range:
                    range.Minimum = 0;
                    range.Maximum = 100;
                    range.Value = 60;
                    range.Width = 120;
                    break;
                default:
                    // Border, Panel, Shape: zero size without content.
                    element.MinWidth = 60;
                    element.MinHeight = 24;
                    break;
            }
        }

        /// <summary>
        /// Message for a type Visual Studio cannot load, as found by PreviewXamlBuilder.FindUnloadableType.
        /// </summary>
        internal static string OwnTypeNote(string typeName) =>
            string.IsNullOrEmpty(typeName) ? null : Localizer.Format(Strings.NoLiveSampleOwnTypeFormat, typeName);

        /// <summary>
        /// First line of the exception message, at most 150 characters. Strips the line/position
        /// suffix because it refers to the generated XAML, not the user's file.
        /// </summary>
        internal static string FirstLine(Exception ex)
        {
            Exception inner = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            string message = inner.Message;
            int newline = message.IndexOfAny(new[] { '\r', '\n' });
            if (newline > 0)
            {
                message = message.Substring(0, newline);
            }

            message = System.Text.RegularExpressions.Regex.Replace(message, @"\s*Line number '\d+' and line position '\d+'\.?", string.Empty).Trim();
            return message.Length > 150 ? message.Substring(0, 149) + "…" : message;
        }
    }
}
