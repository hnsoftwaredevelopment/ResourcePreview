using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using ResourcePreview.Localization;
using ResourcePreview.Preview;
using ResourcePreview.Resources;
using Task = System.Threading.Tasks.Task;

namespace ResourcePreview.Navigation
{
    /// <summary>
    /// View > Other Windows > XAML Resources: every resource in the solution, or only what the active
    /// .xaml file can use, grouped by file or by type. Search by key, double-click to open, right-click
    /// for references, rename and copy, and drag a resource into the editor to insert {StaticResource Key}.
    /// The content comes from <see cref="ResourceCatalog"/>; this class only draws it and reacts.
    /// </summary>
    [Guid("2d5c7a1e-9b44-4f0e-8c3a-6e1f0b7d5a92")]
    internal sealed class ResourcesWindow : ToolWindowPane, IVsSelectionEvents
    {
        public const int OpenCommandId = 0x0107;

        private readonly DockPanel _root = new DockPanel { LastChildFill = true };
        private readonly TextBox _search = new TextBox { MinWidth = 140, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ComboBox _grouping = new ComboBox { Margin = new Thickness(0, 0, 6, 0), MinWidth = 90 };
        private readonly CheckBox _activeOnly = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly Button _refresh = new Button { Content = "⟳", Padding = new Thickness(6, 0, 6, 0) };
        private readonly TextBlock _summary = new TextBlock { Margin = new Thickness(6, 2, 6, 4), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        private readonly TreeView _tree = new TreeView { BorderThickness = new Thickness(0) };
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private AsyncPackage _package;
        private DefinitionNavigator _navigator;
        private IVsMonitorSelection _selection;
        private uint _selectionCookie;
        private ResourceIndex _index;
        private MergeGraph _graph;
        private IReadOnlyList<CatalogItem> _items = Array.Empty<CatalogItem>();
        private string _activeFile;
        private CancellationTokenSource _loading;
        private Point _dragStart;
        private CatalogItem _dragItem;

        public ResourcesWindow()
            : base(null)
        {
            Caption = Localizer.Get(Strings.ResourcesWindowTitle);

            _root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _summary.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _tree.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ThemedDialogTreeViewStyleKey);
            _search.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.TextBoxStyleKey);
            _grouping.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ComboBoxStyleKey);
            _refresh.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ButtonStyleKey);
            _activeOnly.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            _grouping.Items.Add(Localizer.Get(Strings.ResourcesGroupByFile));
            _grouping.Items.Add(Localizer.Get(Strings.ResourcesGroupByType));
            _grouping.SelectedIndex = 0;
            _activeOnly.Content = Localizer.Get(Strings.ResourcesActiveFileOnly);
            _refresh.ToolTip = Localizer.Get(Strings.ResourcesRefresh);

            _search.TextChanged += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Draw();
            };
            _grouping.SelectionChanged += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Draw();
            };
            _activeOnly.Click += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Reload();
            };
            _refresh.Click += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Reload();
            };

            var toolbar = new WrapPanel { Margin = new Thickness(6, 6, 6, 2) };
            toolbar.Children.Add(WithPlaceholder(_search, Localizer.Get(Strings.ResourcesSearchPlaceholder)));
            toolbar.Children.Add(_grouping);
            toolbar.Children.Add(_activeOnly);
            toolbar.Children.Add(_refresh);

            DockPanel.SetDock(toolbar, Dock.Top);
            DockPanel.SetDock(_summary, Dock.Top);
            _root.Children.Add(toolbar);
            _root.Children.Add(_summary);
            _root.Children.Add(_tree);
            Content = _root;
        }

        /// <summary>Called by VS after the window is created, also when VS restores it at startup.</summary>
        protected override void Initialize()
        {
            base.Initialize();
            ThreadHelper.ThrowIfNotOnUIThread();

            _package = (AsyncPackage)Package;
            var componentModel = (IComponentModel)GetService(typeof(SComponentModel));
            _navigator = new DefinitionNavigator(_package, componentModel.GetService<IVsEditorAdaptersFactoryService>(), _package.JoinableTaskFactory.Context);

            // Follow the active document, for "only what the active file can use".
            _selection = GetService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (_selection != null)
            {
                _selection.AdviseSelectionEvents(this, out _selectionCookie);
                if (_selection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out object frame) == VSConstants.S_OK)
                {
                    _activeFile = XamlPath(frame);
                }
            }

            Reload();
        }

        // --- Loading ---

        /// <summary>Reads the resources on a background thread and draws them. Cancels a load that is still running.</summary>
        private void Reload()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_package == null)
            {
                return;
            }

            _loading?.Cancel();
            _loading = new CancellationTokenSource();
            CancellationToken token = _loading.Token;

            string anchor = _activeFile ?? SolutionFile();
            bool activeOnly = _activeOnly.IsChecked == true;
            if (anchor == null || (activeOnly && _activeFile == null))
            {
                _items = Array.Empty<CatalogItem>();
                _tree.Items.Clear();
                _summary.Text = Localizer.Get(activeOnly ? Strings.ResourcesNoActiveFile : Strings.ResourcesNoFile);
                return;
            }

            _summary.Text = Localizer.Get(Strings.ResourcesLoading);
            string activeFile = activeOnly ? _activeFile : null;
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                ResourceIndex index;
                MergeGraph graph;
                IReadOnlyList<CatalogItem> items;
                try
                {
                    // The first load in a solution reads every .xaml file and scans for unused keys.
                    await TaskScheduler.Default;
                    index = ResourceIndex.ForFile(anchor);
                    graph = index.GetMergeGraph(token);
                    IReadOnlyList<OverriddenKey> overridden = OverriddenKeyScanner.Scan(index, token);
                    IReadOnlyList<ResourceEntry> unused = UnusedResourceScanner.Scan(index, token);
                    items = ResourceCatalog.Build(graph, activeFile, overridden, unused);
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return; // a newer load replaces this one
                }

                if (_index != index)
                {
                    if (_index != null)
                    {
                        _index.FilesChanged -= OnFilesChanged;
                    }

                    index.FilesChanged += OnFilesChanged;
                }

                _index = index;
                _graph = graph;
                _items = items;
                Draw();
            }).FileAndForget("ResourcePreview/ResourcesWindow");
        }

        /// <summary>A .xaml file changed on disk: load again after a short pause, so a save of several files is one load.</summary>
        private void OnFilesChanged(object sender, EventArgs e)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Delay(1000);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Reload();
            }).FileAndForget("ResourcePreview/ResourcesWindow");
        }

        private string SolutionFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return GetService(typeof(SVsSolution)) is IVsSolution solution
                && solution.GetSolutionInfo(out _, out string file, out _) == VSConstants.S_OK
                && !string.IsNullOrEmpty(file)
                ? file
                : null;
        }

        private static string XamlPath(object frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return frame is IVsWindowFrame windowFrame
                && windowFrame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object document) == VSConstants.S_OK
                && document is string path
                && path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }

        // --- Drawing ---

        /// <summary>Filters and groups the loaded items. Cheap, so it runs for every key stroke in the search box.</summary>
        private void Draw()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_graph == null)
            {
                return;
            }

            List<CatalogItem> visible = ResourceCatalog.Filter(_items, _search.Text).ToList();
            bool byType = _grouping.SelectedIndex == 1;
            IReadOnlyList<CatalogGroup> groups = byType
                ? ResourceCatalog.GroupByType(visible)
                : ResourceCatalog.GroupByFile(visible, _graph, _activeOnly.IsChecked == true ? _activeFile : null);

            int fileCount = _items.Select(i => i.Entry.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _summary.Text = visible.Count == _items.Count
                ? Localizer.Format(Strings.ResourcesSummaryFormat, _items.Count, fileCount)
                : Localizer.Format(Strings.ResourcesFilteredFormat, visible.Count, _items.Count);

            _tree.Items.Clear();
            foreach (CatalogGroup group in groups)
            {
                string title = group.FilePath != null ? ReferenceChecker.Relative(_index, group.FilePath) : CategoryName(group.Category.Value);
                var node = new TreeViewItem
                {
                    Header = Text(title + " (" + group.Items.Count + ")", 1.0),
                    IsExpanded = !_collapsed.Contains(title) || visible.Count != _items.Count,
                };
                node.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ThemedDialogTreeViewItemStyleKey);
                node.Expanded += (s, e) => _collapsed.Remove(title);
                node.Collapsed += (s, e) => _collapsed.Add(title);

                foreach (CatalogItem item in group.Items)
                {
                    node.Items.Add(CreateItem(item, showFile: byType));
                }

                _tree.Items.Add(node);
            }
        }

        private TreeViewItem CreateItem(CatalogItem item, bool showFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            try
            {
                icon.Source = CompletionIconFactory.Get(_index, item.Entry, (Brush)_tree.FindResource(EnvironmentColors.ToolWindowTextBrushKey));
            }
            catch (Exception)
            {
                // No icon is fine; the tooltip shows what went wrong.
            }

            header.Children.Add(icon);
            header.Children.Add(Text(item.Key, 1.0));
            header.Children.Add(Text("  " + item.Entry.ElementTypeName, 0.6));
            if (showFile)
            {
                header.Children.Add(Text("  " + Path.GetFileName(item.Entry.FilePath), 0.6));
            }

            string note = Note(item);
            if (note != null)
            {
                TextBlock noteText = Text("  " + note, 0.7);
                noteText.FontStyle = FontStyles.Italic;
                header.Children.Add(noteText);
            }

            var node = new TreeViewItem { Header = header, ToolTip = new ToolTip(), ContextMenu = CreateMenu(item) };
            node.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ThemedDialogTreeViewItemStyleKey);

            // The preview is built when the tooltip opens, not for every item in the list.
            node.ToolTipOpening += (s, e) =>
            {
                if (node.ToolTip is ToolTip tip && tip.Content == null)
                {
                    tip.Content = CreatePreview(item.Entry);
                }
            };

            node.MouseDoubleClick += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (node.IsSelected)
                {
                    e.Handled = true;
                    _navigator.NavigateTo(item.Entry);
                }
            };
            node.KeyDown += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    _navigator.NavigateTo(item.Entry);
                }
            };

            // Drag into the editor: the editor inserts the text where you drop it.
            node.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _dragStart = e.GetPosition(null);
                _dragItem = item;
            };
            node.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragItem != item)
                {
                    return;
                }

                Vector moved = e.GetPosition(null) - _dragStart;
                if (Math.Abs(moved.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(moved.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _dragItem = null;
                    var data = new DataObject();
                    data.SetText(Reference(item), TextDataFormat.UnicodeText);
                    DragDrop.DoDragDrop(node, data, DragDropEffects.Copy);
                }
            };
            return node;
        }

        private static string Reference(CatalogItem item) => "{StaticResource " + item.Key + "}";

        private static string Note(CatalogItem item)
        {
            var notes = new List<string>();
            if (item.Flags.HasFlag(CatalogFlags.Overridden) && item.OverriddenBy != null)
            {
                notes.Add(Localizer.Format(Strings.OverriddenByFormat, Path.GetFileName(item.OverriddenBy.FilePath)));
            }

            if (item.Flags.HasFlag(CatalogFlags.NotLoaded))
            {
                notes.Add(Localizer.Get(Strings.NotMerged));
            }

            if (item.Flags.HasFlag(CatalogFlags.Unused))
            {
                notes.Add(Localizer.Get(Strings.ResourcesUnused));
            }

            return notes.Count == 0 ? null : string.Join(", ", notes);
        }

        /// <summary>The same preview as in the editor tooltip.</summary>
        private UIElement CreatePreview(ResourceEntry entry)
        {
            try
            {
                ResourcePreviewData data = ResourcePreviewData.Create(_index, entry, null, CancellationToken.None);
                TextBlock header = PreviewRenderer.CreateText(entry.ElementTypeName + " " + entry.Key);
                header.FontWeight = FontWeights.SemiBold;

                var panel = new StackPanel { Margin = new Thickness(2) };
                panel.Children.Add(header);
                panel.Children.Add(PreviewRenderer.Render(data));
                return panel;
            }
            catch (Exception ex)
            {
                return new TextBlock { Text = ex.Message };
            }
        }

        private ContextMenu CreateMenu(CatalogItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem(Localizer.Get(Strings.GoToResourceDefinition), () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _navigator.NavigateTo(item.Entry);
            }));
            menu.Items.Add(MenuItem(Localizer.Get(Strings.FindResourceReferences), () => FindResourceReferencesCommand.Instance?.FindFor(item.Entry)));
            menu.Items.Add(MenuItem(Localizer.Get(Strings.RenameResourceKey), () => RenameResourceKeyCommand.Instance?.Rename(item.Entry.FilePath, item.Key)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(Localizer.Format(Strings.ResourcesCopyFormat, Reference(item)), () => Clipboard.SetText(Reference(item))));
            return menu;
        }

        private static MenuItem MenuItem(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                action();
            };
            return item;
        }

        private static string CategoryName(ResourceCategory category)
        {
            switch (category)
            {
                case ResourceCategory.Brushes: return Localizer.Get(Strings.CategoryBrushes);
                case ResourceCategory.Colors: return Localizer.Get(Strings.CategoryColors);
                case ResourceCategory.Images: return Localizer.Get(Strings.CategoryImages);
                case ResourceCategory.Styles: return Localizer.Get(Strings.CategoryStyles);
                case ResourceCategory.Templates: return Localizer.Get(Strings.CategoryTemplates);
                default: return Localizer.Get(Strings.CategoryValues);
            }
        }

        private static TextBlock Text(string text, double opacity)
        {
            var block = new TextBlock { Text = text, Opacity = opacity, VerticalAlignment = VerticalAlignment.Center };
            block.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return block;
        }

        /// <summary>A grey hint in an empty text box.</summary>
        private static UIElement WithPlaceholder(TextBox box, string hint)
        {
            var placeholder = new TextBlock
            {
                Text = hint,
                Opacity = 0.5,
                IsHitTestVisible = false,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            placeholder.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            box.TextChanged += (s, e) => placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            var grid = new Grid();
            grid.Children.Add(box);
            grid.Children.Add(placeholder);
            return grid;
        }

        // --- Active document (IVsSelectionEvents) ---

        public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (elementid == (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame)
            {
                string path = XamlPath(varValueNew);
                if (path != null && !string.Equals(path, _activeFile, StringComparison.OrdinalIgnoreCase))
                {
                    // Keep the last .xaml file when another kind of document becomes active.
                    _activeFile = path;
                    if (_activeOnly.IsChecked == true || _graph == null)
                    {
                        Reload();
                    }
                }
            }

            return VSConstants.S_OK;
        }

        public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld, IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew) => VSConstants.S_OK;

        public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;

        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing)
            {
                if (_selection != null && _selectionCookie != 0)
                {
                    _selection.UnadviseSelectionEvents(_selectionCookie);
                    _selectionCookie = 0;
                }

                if (_index != null)
                {
                    _index.FilesChanged -= OnFilesChanged;
                }

                _loading?.Cancel();
            }

            base.Dispose(disposing);
        }

        // --- View > Other Windows > XAML Resources ---

        public static async Task InitializeCommandAsync(AsyncPackage package)
        {
            if (!(await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService))
            {
                return;
            }

            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var command = new OleMenuCommand(
                (s, e) => package.JoinableTaskFactory
                    .RunAsync(() => package.ShowToolWindowAsync(typeof(ResourcesWindow), 0, create: true, package.DisposalToken))
                    .FileAndForget("ResourcePreview/OpenResourcesWindow"),
                new CommandID(GoToResourceDefinitionCommand.CommandSet, OpenCommandId));
            command.BeforeQueryStatus += (s, e) => ((OleMenuCommand)s).Text = Localizer.Get(Strings.ResourcesWindowTitle);
            commandService.AddCommand(command);
        }
    }
}
