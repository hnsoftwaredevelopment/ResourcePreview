using System.Windows;
using System.Windows.Controls;
using ResourcePreview.Localization;
using ResourcePreview.Resources;

namespace ResourcePreview.Preview
{
    /// <summary>
    /// Renders a <see cref="TemplateTree"/> as an indented two-column list:
    ///
    ///   DataType local:Book
    ///   Border
    ///     StackPanel
    ///       Image       Source = {Binding Cover}
    ///       TextBlock   Text = {Binding Title}
    ///
    /// Call on the UI thread only.
    /// </summary>
    internal static class TemplateTreeRenderer
    {
        private const double IndentPerLevel = 14;

        public static StackPanel Render(TemplateTree tree)
        {
            var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            string type = tree.DataType != null ? "DataType " + tree.DataType
                : tree.TargetType != null ? "TargetType " + tree.TargetType
                : null;
            if (type != null)
            {
                root.Children.Add(PreviewRenderer.CreateText(type, opacity: 0.8));
            }

            if (tree.Lines.Count == 0)
            {
                root.Children.Add(PreviewRenderer.CreateText(Localizer.Get(Strings.TemplateEmpty), opacity: 0.7));
                return root;
            }

            var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            foreach (TemplateTreeLine line in tree.Lines)
            {
                int row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                TextBlock name = PreviewRenderer.CreateText(line.Element, opacity: 0.9);
                name.Margin = new Thickness(line.Depth * IndentPerLevel, 1, 12, 0);
                Grid.SetRow(name, row);
                grid.Children.Add(name);

                if (line.Details != null)
                {
                    TextBlock details = PreviewRenderer.CreateText(line.Details, opacity: 0.7);
                    details.Margin = new Thickness(0, 1, 0, 0);
                    Grid.SetRow(details, row);
                    Grid.SetColumn(details, 1);
                    grid.Children.Add(details);
                }
            }

            root.Children.Add(grid);

            if (tree.HiddenCount > 0)
            {
                root.Children.Add(PreviewRenderer.CreateText(Localizer.Format(Strings.TemplateMoreElementsFormat, tree.HiddenCount), opacity: 0.6));
            }

            return root;
        }
    }
}
