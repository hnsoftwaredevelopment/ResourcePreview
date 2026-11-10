using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using ResourcePreview.Localization;

namespace ResourcePreview.Refactoring
{
    /// <summary>Asks for the key and the file the new resource goes to, and shows the XAML it will add.</summary>
    internal sealed class ExtractResourceDialog : DialogWindow
    {
        private readonly TextBox _key;
        private readonly ComboBox _target;
        private readonly TextBlock _preview;
        private readonly TextBlock _error;
        private readonly Button _ok;
        private readonly Func<string, string> _validate;
        private readonly Func<string, string> _createPreview;

        /// <param name="targets">Shown as relative paths; the full paths come back from <see cref="Target"/>.</param>
        /// <param name="validate">Error text for a key, or null when it can be used.</param>
        /// <param name="createPreview">The XAML that will be added for a key.</param>
        public ExtractResourceDialog(string suggestedKey, IReadOnlyList<KeyValuePair<string, string>> targets, string selectedTarget, Func<string, string> validate, Func<string, string> createPreview)
        {
            _validate = validate;
            _createPreview = createPreview;

            Title = Localizer.Get(Strings.ExtractDialogTitle);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);

            var panel = new StackPanel { Margin = new Thickness(12), Width = 460 };

            panel.Children.Add(Text(Localizer.Get(Strings.ExtractKeyLabel)));
            _key = new TextBox { Text = suggestedKey, Margin = new Thickness(0, 2, 0, 8) };
            _key.SetResourceReference(StyleProperty, VsResourceKeys.TextBoxStyleKey);
            _key.TextChanged += (s, e) => Update();
            panel.Children.Add(_key);

            _error = Text(string.Empty);
            _error.Foreground = System.Windows.Media.Brushes.IndianRed; // readable in light and dark themes
            _error.Visibility = Visibility.Collapsed;
            panel.Children.Add(_error);

            panel.Children.Add(Text(Localizer.Get(Strings.ExtractTargetLabel)));
            _target = new ComboBox
            {
                ItemsSource = targets,
                DisplayMemberPath = "Value",
                SelectedValuePath = "Key",
                Margin = new Thickness(0, 2, 0, 8),
            };
            _target.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
            _target.SelectedValue = targets.Any(t => t.Key == selectedTarget) ? selectedTarget : targets[0].Key;
            panel.Children.Add(_target);

            _preview = Text(string.Empty);
            _preview.FontFamily = new System.Windows.Media.FontFamily("Consolas");
            _preview.Opacity = 0.8;
            panel.Children.Add(_preview);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            _ok = CreateButton(Localizer.Get(Strings.ExtractButton), isDefault: true);
            _ok.Click += (s, e) => DialogResult = true;
            Button cancel = CreateButton(Localizer.Get(Strings.CancelButton), isDefault: false);
            cancel.IsCancel = true;
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = panel;
            Loaded += (s, e) =>
            {
                _key.SelectAll();
                Keyboard.Focus(_key);
            };
            Update();
        }

        public string Key => _key.Text.Trim();

        public string Target => _target.SelectedValue as string;

        private void Update()
        {
            string error = _validate(Key);
            _error.Text = error ?? string.Empty;
            _error.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            _ok.IsEnabled = error == null;
            _preview.Text = error == null ? _createPreview(Key) : string.Empty;
        }

        private static TextBlock Text(string text)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            block.SetResourceReference(TextBlock.ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            return block;
        }

        private static Button CreateButton(string text, bool isDefault)
        {
            var button = new Button { Content = text, IsDefault = isDefault, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            button.SetResourceReference(StyleProperty, VsResourceKeys.ButtonStyleKey);
            return button;
        }
    }
}
