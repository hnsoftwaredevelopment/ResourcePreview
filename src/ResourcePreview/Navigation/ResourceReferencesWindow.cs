using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using ResourcePreview.Localization;
using ResourcePreview.Resources;

namespace ResourcePreview.Navigation
{
    /// <summary>
    /// Tool window with all definitions and uses of one key, grouped by file. Double-click or Enter opens
    /// the place. Registered with [ProvideToolWindow] on the package and shown by <see cref="FindResourceReferencesCommand"/>.
    /// </summary>
    [Guid("6b0f4a8e-2d3c-4e71-9a85-3c1f7e2b9d04")]
    internal sealed class ResourceReferencesWindow : ToolWindowPane
    {
        private readonly DockPanel _root = new DockPanel { LastChildFill = true };
        private readonly TextBlock _summary = new TextBlock { Margin = new Thickness(6, 4, 6, 4), TextWrapping = TextWrapping.Wrap };
        private readonly TreeView _tree = new TreeView { BorderThickness = new Thickness(0) };
        private DefinitionNavigator _navigator;

        public ResourceReferencesWindow()
            : base(null)
        {
            Caption = Localizer.Get(Strings.ReferencesWindowTitle);

            // Theme colors and the VS tree style, so it looks right in light and dark themes.
            _root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _summary.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _tree.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ThemedDialogTreeViewStyleKey);

            DockPanel.SetDock(_summary, Dock.Top);
            _root.Children.Add(_summary);
            _root.Children.Add(_tree);
            Content = _root;
        }

        /// <summary>Replaces the list with a new search result. Call on the UI thread.</summary>
        public void Show(UsageReport report, ResourceIndex index, DefinitionNavigator navigator)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _navigator = navigator;
            Caption = Localizer.Get(Strings.ReferencesWindowTitle) + " - " + report.Key;
            _tree.Items.Clear();

            int fileCount = report.Usages.Select(u => u.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _summary.Text = report.Usages.Count == 0
                ? Localizer.Format(Strings.ReferencesNoneFormat, report.Key)
                : Localizer.Format(Strings.ReferencesSummaryFormat, report.Key, report.Usages.Count, fileCount);

            if (report.Definitions.Count > 0)
            {
                TreeViewItem definitions = CreateGroup(Localizer.Get(Strings.ReferencesDefinitions), report.Definitions.Count);
                foreach (ResourceEntry definition in report.Definitions)
                {
                    string location = ReferenceChecker.Relative(index, definition.FilePath) + " (" + definition.LineNumber + ")";
                    bool selected = report.Selected != null && UsageReport.IsSame(definition, report.Selected);
                    TreeViewItem item = CreateLine(location, definition.ElementTypeName, null, () => _navigator.NavigateTo(definition));
                    if (selected)
                    {
                        item.FontWeight = FontWeights.Bold;
                    }

                    definitions.Items.Add(item);
                }

                _tree.Items.Add(definitions);
            }

            foreach (IGrouping<string, ResourceUsage> file in report.Usages.GroupBy(u => u.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                TreeViewItem group = CreateGroup(ReferenceChecker.Relative(index, file.Key), file.Count());
                foreach (ResourceUsage usage in file)
                {
                    string note = null;
                    if (usage.ResolvesTo == null && report.Definitions.Count > 0)
                    {
                        note = Localizer.Get(Strings.ReferencesNotFoundHere);
                    }
                    else if (report.UsesOtherDefinition(usage))
                    {
                        note = Localizer.Format(Strings.ReferencesUsesOtherFormat, Path.GetFileName(usage.ResolvesTo.FilePath));
                    }

                    ResourceUsage target = usage;
                    group.Items.Add(CreateLine(usage.Use.Line + ":", Shorten(usage.Use.LineText), note, () => _navigator.NavigateToUse(target.FilePath, target.Use)));
                }

                _tree.Items.Add(group);
            }
        }

        private static TreeViewItem CreateGroup(string title, int count)
        {
            var item = new TreeViewItem { Header = CreateText(title + " (" + count + ")", 1.0), IsExpanded = true };
            item.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ThemedDialogTreeViewItemStyleKey);
            return item;
        }

        private TreeViewItem CreateLine(string location, string text, string note, Action open)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(CreateText(location, 0.7));
            header.Children.Add(CreateText("  " + text, 1.0));
            if (note != null)
            {
                TextBlock noteText = CreateText("  " + note, 0.7);
                noteText.FontStyle = FontStyles.Italic;
                header.Children.Add(noteText);
            }

            var item = new TreeViewItem { Header = header };
            item.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ThemedDialogTreeViewItemStyleKey);
            item.MouseDoubleClick += (s, e) =>
            {
                e.Handled = true;
                open();
            };
            item.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    open();
                }
            };
            return item;
        }

        private static TextBlock CreateText(string text, double opacity)
        {
            var block = new TextBlock { Text = text, Opacity = opacity };
            block.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return block;
        }

        private static string Shorten(string text) => text.Length > 160 ? text.Substring(0, 159) + "…" : text;
    }
}
