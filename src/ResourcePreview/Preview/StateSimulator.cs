using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ResourcePreview.Preview
{
    /// <summary>The states the live sample can show.</summary>
    internal enum SampleState
    {
        Normal,
        MouseOver,
        Pressed,
        Disabled,
    }

    /// <summary>
    /// Shows a control as if the mouse is over it or presses it. IsMouseOver and IsPressed are read-only:
    /// WPF sets them from the real mouse. So we do what the trigger would do: apply its setters to the
    /// control, or to the template part named in TargetName. Templates that use the VisualStateManager
    /// instead of triggers go to their "MouseOver" or "Pressed" state.
    ///
    /// Not simulated: EnterActions and ExitActions (storyboards), and triggers on IsMouseOver="False".
    /// Call on the UI thread only.
    /// </summary>
    internal static class StateSimulator
    {
        private const string IsMouseOver = "IsMouseOver";
        private const string IsPressed = "IsPressed";

        /// <summary>
        /// All triggers that apply to a control with this style, in the order WPF applies them. Later
        /// ones win: first the template triggers, then the style triggers from the base style down.
        /// </summary>
        public static IReadOnlyList<TriggerBase> TriggersOf(Style style)
        {
            var chain = new List<Style>();
            for (Style current = style; current != null; current = current.BasedOn)
            {
                chain.Insert(0, current);
            }

            // The template of the most derived style that sets one.
            ControlTemplate template = chain
                .AsEnumerable()
                .Reverse()
                .SelectMany(s => s.Setters.OfType<Setter>())
                .Where(s => s.Property == Control.TemplateProperty)
                .Select(s => s.Value as ControlTemplate)
                .FirstOrDefault(t => t != null);

            var result = new List<TriggerBase>();
            if (template != null)
            {
                result.AddRange(template.Triggers);
            }

            foreach (Style current in chain)
            {
                result.AddRange(current.Triggers);
            }

            return result;
        }

        public static IReadOnlyList<TriggerBase> TriggersOf(ControlTemplate template) => template.Triggers.ToList();

        /// <summary>True if a trigger with setters reacts to this state.</summary>
        public static bool HasState(IReadOnlyList<TriggerBase> triggers, SampleState state)
        {
            string property = state == SampleState.MouseOver ? IsMouseOver : state == SampleState.Pressed ? IsPressed : null;
            return property != null && triggers.Any(t => HasSetters(t) && Conditions(t).Any(c => c.Name == property && IsTrue(c.Value)));
        }

        /// <summary>True if the applied template has a VisualStateManager state for this, such as "MouseOver".</summary>
        public static bool HasVisualState(UIElement element, SampleState state)
        {
            return element is Control control && FindVisualState(control, state.ToString()) != null;
        }

        /// <summary>Puts the control in the state: applies the matching trigger setters, or goes to the visual state.</summary>
        public static void Apply(FrameworkElement element, IReadOnlyList<TriggerBase> triggers, SampleState state)
        {
            // Pressing a button means the mouse is over it too.
            var simulated = new HashSet<string> { IsMouseOver };
            if (state == SampleState.Pressed)
            {
                simulated.Add(IsPressed);
            }

            foreach (TriggerBase trigger in triggers)
            {
                if (IsActive(trigger, element, simulated))
                {
                    foreach (Setter setter in Setters(trigger))
                    {
                        DependencyObject target = setter.TargetName == null ? element : FindPart(element, setter.TargetName);
                        if (target != null)
                        {
                            SetValue(target, setter.Property, setter.Value);
                        }
                    }
                }
            }

            if (element is Control control && FindVisualState(control, state.ToString()) != null)
            {
                VisualStateManager.GoToState(control, state.ToString(), useTransitions: false);
            }
        }

        /// <summary>A condition as property name plus value, for both property and binding conditions.</summary>
        private struct ConditionInfo
        {
            public ConditionInfo(string name, DependencyProperty property, object value, string sourceName)
            {
                Name = name;
                Property = property;
                Value = value;
                SourceName = sourceName;
            }

            public string Name { get; }

            /// <summary>Null for a binding condition.</summary>
            public DependencyProperty Property { get; }

            public object Value { get; }

            public string SourceName { get; }
        }

        private static IEnumerable<ConditionInfo> Conditions(TriggerBase trigger)
        {
            switch (trigger)
            {
                case Trigger single:
                    yield return new ConditionInfo(single.Property?.Name, single.Property, single.Value, single.SourceName);
                    break;
                case MultiTrigger multi:
                    foreach (Condition c in multi.Conditions)
                    {
                        yield return new ConditionInfo(c.Property?.Name, c.Property, c.Value, c.SourceName);
                    }

                    break;
                case DataTrigger data:
                    yield return new ConditionInfo(BindingName(data.Binding), null, data.Value, null);
                    break;
                case MultiDataTrigger multiData:
                    foreach (Condition c in multiData.Conditions)
                    {
                        yield return new ConditionInfo(BindingName(c.Binding), null, c.Value, null);
                    }

                    break;
            }
        }

        /// <summary>"IsMouseOver" for {Binding IsMouseOver, RelativeSource={RelativeSource Self}} and similar.</summary>
        private static string BindingName(BindingBase binding)
        {
            if (!(binding is Binding b) || b.Path == null)
            {
                return null;
            }

            string path = b.Path.Path;
            int dot = path.LastIndexOf('.');
            return dot >= 0 ? path.Substring(dot + 1) : path;
        }

        /// <summary>
        /// Would WPF apply this trigger with the simulated properties set to True? Other conditions are
        /// checked against the control as it is now. A binding condition on anything else counts as false.
        /// </summary>
        private static bool IsActive(TriggerBase trigger, FrameworkElement element, HashSet<string> simulated)
        {
            List<ConditionInfo> conditions = Conditions(trigger).ToList();
            if (conditions.Count == 0 || !conditions.Any(c => c.Name != null && simulated.Contains(c.Name)))
            {
                return false;
            }

            foreach (ConditionInfo condition in conditions)
            {
                if (condition.Name != null && simulated.Contains(condition.Name))
                {
                    if (!IsTrue(condition.Value))
                    {
                        return false;
                    }

                    continue;
                }

                if (condition.Property == null)
                {
                    return false;
                }

                DependencyObject source = condition.SourceName == null ? element : FindPart(element, condition.SourceName) ?? element;
                if (!Equals(source.GetValue(condition.Property), condition.Value))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasSetters(TriggerBase trigger) => Setters(trigger).Any();

        private static IEnumerable<Setter> Setters(TriggerBase trigger)
        {
            switch (trigger)
            {
                case Trigger single: return single.Setters.OfType<Setter>();
                case MultiTrigger multi: return multi.Setters.OfType<Setter>();
                case DataTrigger data: return data.Setters.OfType<Setter>();
                case MultiDataTrigger multiData: return multiData.Setters.OfType<Setter>();
                default: return Enumerable.Empty<Setter>();
            }
        }

        /// <summary>True for the bool true and for the text "True" (DataTrigger values stay text).</summary>
        private static bool IsTrue(object value) =>
            value is bool b ? b : value is string s && bool.TryParse(s, out bool parsed) && parsed;

        private static DependencyObject FindPart(FrameworkElement element, string name)
        {
            return element is Control control && control.Template != null
                ? control.Template.FindName(name, control) as DependencyObject
                : null;
        }

        /// <summary>
        /// Sets the value as a local value, which wins over style and template values, like an active trigger.
        /// Values that do not fit are skipped, so nothing throws.
        /// </summary>
        private static void SetValue(DependencyObject target, DependencyProperty property, object value)
        {
            if (property == null || property.ReadOnly)
            {
                return;
            }

            switch (value)
            {
                case DynamicResourceExtension dynamic when target is FrameworkElement fe:
                    fe.SetResourceReference(property, dynamic.ResourceKey);
                    return;
                case TemplateBindingExtension templateBinding:
                    BindingOperations.SetBinding(target, property, new Binding
                    {
                        Path = new PropertyPath(templateBinding.Property),
                        RelativeSource = RelativeSource.TemplatedParent,
                    });
                    return;
                case BindingBase binding:
                    BindingOperations.SetBinding(target, property, binding);
                    return;
            }

            if (property.IsValidValue(value))
            {
                target.SetValue(property, value);
            }
        }

        /// <summary>The VisualStateManager state with this name in the template root, or null.</summary>
        private static VisualState FindVisualState(Control control, string name)
        {
            if (VisualTreeHelper.GetChildrenCount(control) == 0 || !(VisualTreeHelper.GetChild(control, 0) is FrameworkElement root))
            {
                return null;
            }

            return VisualStateManager.GetVisualStateGroups(root)?
                .OfType<VisualStateGroup>()
                .SelectMany(g => g.States.OfType<VisualState>())
                .FirstOrDefault(s => s.Name == name);
        }
    }
}
