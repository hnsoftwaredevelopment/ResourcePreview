using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using ResourcePreview.Localization;

namespace ResourcePreview.Refactoring
{
    /// <summary>One row in the list: what the resource is, and what changes on the element.</summary>
    internal sealed class MatchRow
    {
        public MatchRow(object match, string key, string description, string change, string file)
        {
            Match = match;
            Key = key;
            Description = description;
            Change = change;
            File = file;
        }

        public object Match { get; }

        public string Key { get; }

        public string Description { get; }

        public string Change { get; }

        public string File { get; }
    }

    /// <summary>Shows the resources that fit the selection. Nothing changes until the user picks one.</summary>
    internal sealed class MatchResourceDialog : DialogWindow
    {
        private readonly ListBox _list;
        private readonly Button _ok;

        public MatchResourceDialog(IReadOnlyList<MatchRow> rows)
        {
            Title = Localizer.Get(Strings.MatchDialogTitle);
            SizeToContent = SizeToContent.Height;
            Width = 560;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(Text(Localizer.Get(Strings.MatchListLabel), 1.0));

            _list = new ListBox
            {
                ItemsSource = rows,
                MaxHeight = 360,
                Margin = new Thickness(0, 6, 0, 0),
                ItemTemplate = CreateTemplate(),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
            _list.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            _list.SelectionChanged += (s, e) => _ok.IsEnabled = _list.SelectedItem != null;
            _list.MouseDoubleClick += (s, e) =>
            {
                if (_list.SelectedItem != null)
                {
                    DialogResult = true;
                }
            };
            panel.Children.Add(_list);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            _ok = CreateButton(Localizer.Get(Strings.MatchApplyButton), isDefault: true);
            _ok.Click += (s, e) => DialogResult = true;
            Button cancel = CreateButton(Localizer.Get(Strings.CancelButton), isDefault: false);
            cancel.IsCancel = true;
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = panel;
            _list.SelectedIndex = 0;
            Loaded += (s, e) =>
            {
                if (_list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
                {
                    Keyboard.Focus(first);
                }
            };
        }

        /// <summary>The chosen match, or null.</summary>
        public object Selected => (_list.SelectedItem as MatchRow)?.Match;

        /// <summary>Key in bold, then the description, the change in code font, and the file.</summary>
        private static DataTemplate CreateTemplate()
        {
            var root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(MarginProperty, new Thickness(2, 3, 2, 5));
            root.AppendChild(Line(nameof(MatchRow.Key), 1.0, bold: true, code: false));
            root.AppendChild(Line(nameof(MatchRow.Description), 0.85, bold: false, code: false));
            root.AppendChild(Line(nameof(MatchRow.Change), 0.85, bold: false, code: true));
            root.AppendChild(Line(nameof(MatchRow.File), 0.6, bold: false, code: false));
            return new DataTemplate { VisualTree = root };
        }

        private static FrameworkElementFactory Line(string path, double opacity, bool bold, bool code)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(path));
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            text.SetValue(OpacityProperty, opacity);
            if (bold)
            {
                text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            }

            if (code)
            {
                text.SetValue(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Consolas"));
            }

            return text;
        }

        private static TextBlock Text(string text, double opacity)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity };
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
