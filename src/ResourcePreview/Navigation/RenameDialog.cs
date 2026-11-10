using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using ResourcePreview.Localization;

namespace ResourcePreview.Navigation
{
    /// <summary>
    /// Asks for the new key. A DialogWindow is the VS base class for modal dialogs: it gets the right owner
    /// and stays on top of VS. The controls use the VS theme styles, so the dialog is dark in a dark theme.
    /// </summary>
    internal sealed class RenameDialog : DialogWindow
    {
        private readonly TextBox _name;
        private readonly TextBlock _error;
        private readonly Button _ok;
        private readonly Func<string, string> _validate;

        /// <param name="validate">Returns an error text for a name, or null when the name is fine.</param>
        public RenameDialog(string key, string summary, Func<string, string> validate)
        {
            _validate = validate;

            Title = Localizer.Get(Strings.RenameDialogTitle);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
            SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);

            var panel = new StackPanel { Margin = new Thickness(12), Width = 420 };
            panel.Children.Add(Text(Localizer.Format(Strings.RenamePromptFormat, key)));

            _name = new TextBox { Text = key, Margin = new Thickness(0, 4, 0, 8) };
            _name.SetResourceReference(StyleProperty, VsResourceKeys.TextBoxStyleKey);
            _name.TextChanged += (s, e) => Validate();
            panel.Children.Add(_name);

            _error = Text(string.Empty);
            _error.Visibility = Visibility.Collapsed;
            _error.Foreground = System.Windows.Media.Brushes.IndianRed; // readable in light and dark themes
            panel.Children.Add(_error);

            panel.Children.Add(Text(summary));
            TextBlock note = Text(Localizer.Format(Strings.RenameCodeNoteFormat, key) + " " + Localizer.Get(Strings.RenameSaveNote));
            note.Opacity = 0.75;
            panel.Children.Add(note);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            _ok = CreateButton(Localizer.Get(Strings.RenameButton), isDefault: true);
            _ok.Click += (s, e) => DialogResult = true;
            Button cancel = CreateButton(Localizer.Get(Strings.CancelButton), isDefault: false);
            cancel.IsCancel = true;
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = panel;

            Loaded += (s, e) =>
            {
                _name.SelectAll();
                Keyboard.Focus(_name);
            };
            Validate();
        }

        public string NewKey => _name.Text.Trim();

        private void Validate()
        {
            string error = _validate(NewKey);
            _error.Text = error ?? string.Empty;
            _error.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;

            // Same name as before: no error text, but nothing to do either.
            _ok.IsEnabled = error == null;
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
