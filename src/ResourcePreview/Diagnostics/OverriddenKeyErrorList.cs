using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using ResourcePreview.Localization;
using ResourcePreview.Navigation;
using ResourcePreview.Options;
using ResourcePreview.Resources;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;
using Task = System.Threading.Tasks.Task;

namespace ResourcePreview.Diagnostics
{
    /// <summary>
    /// Shows a warning in the Error List for each resource definition WPF never uses, because a
    /// dictionary merged later (or App.xaml itself) defines the same key. See <see cref="OverriddenKeyScanner"/>.
    ///
    /// Scans when the package loads, when a solution opens, a short moment after a .xaml file is saved,
    /// and when the setting changes. The scan runs on a background thread; the Error List is updated
    /// on the UI thread.
    /// </summary>
    internal sealed class OverriddenKeyErrorList
    {
        // Saving several files at once (Save All, git checkout) should give one scan, not ten.
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(1500);

        private readonly AsyncPackage _package;
        private readonly ErrorListProvider _errorList;
        private readonly DefinitionNavigator _navigator;
        private CancellationTokenSource _pending;
        private ResourceIndex _watchedIndex;

        private OverriddenKeyErrorList(AsyncPackage package, DefinitionNavigator navigator)
        {
            _package = package;
            _navigator = navigator;
            _errorList = new ErrorListProvider(package)
            {
                ProviderName = "XAML Resource Preview",
                ProviderGuid = new Guid("0d0a3b2c-5f53-4a8e-9d55-6f3c1e0b7a41"),
            };
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            var componentModel = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            if (componentModel == null)
            {
                return;
            }

            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var navigator = new DefinitionNavigator(package, componentModel.GetService<IVsEditorAdaptersFactoryService>(), package.JoinableTaskFactory.Context);
            var instance = new OverriddenKeyErrorList(package, navigator);

            // All three are raised on the UI thread.
            SolutionEvents.OnAfterOpenSolution += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                instance.ScheduleScan();
            };
            SolutionEvents.OnAfterCloseSolution += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                instance._pending?.Cancel();
                instance.Clear();
            };
            SettingsService.Changed += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                instance.ScheduleScan();
            };

            // The package loads when a .xaml file becomes active, so a solution is usually open already.
            instance.ScheduleScan();
        }

        private void ScheduleScan()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _pending?.Cancel();
            if (!SettingsService.Current.WarnOverriddenKeys)
            {
                Clear();
                return;
            }

            string solutionFile = GetSolutionFile();
            if (solutionFile == null)
            {
                Clear();
                return;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_package.DisposalToken);
            _pending = cancellation;
            _package.JoinableTaskFactory
                .RunAsync(() => ScanAsync(solutionFile, cancellation.Token))
                .FileAndForget("ResourcePreview/OverriddenKeyScan");
        }

        private async Task ScanAsync(string solutionFile, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Delay, cancellationToken);
                await TaskScheduler.Default;

                ResourceIndex index = ResourceIndex.ForFile(solutionFile);
                Watch(index);
                IReadOnlyList<OverriddenKey> found = OverriddenKeyScanner.Scan(index, cancellationToken);

                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                Show(index, found);
            }
            catch (OperationCanceledException)
            {
                // A newer scan replaced this one, or VS is closing.
            }
        }

        /// <summary>Rescans when a .xaml file under the solution changes on disk, for example after a save.</summary>
        private void Watch(ResourceIndex index)
        {
            ResourceIndex previous = Interlocked.Exchange(ref _watchedIndex, index);
            if (previous == index)
            {
                return;
            }

            if (previous != null)
            {
                previous.FilesChanged -= OnFilesChanged;
            }

            index.FilesChanged += OnFilesChanged;
        }

        private void OnFilesChanged(object sender, EventArgs e)
        {
            // Raised on a FileSystemWatcher thread.
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
                ScheduleScan();
            }).FileAndForget("ResourcePreview/OverriddenKeyFilesChanged");
        }

        private void Show(ResourceIndex index, IReadOnlyList<OverriddenKey> found)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _errorList.SuspendRefresh();
            try
            {
                _errorList.Tasks.Clear();
                foreach (OverriddenKey item in found)
                {
                    ResourceEntry definition = item.Definition;
                    var task = new ErrorTask
                    {
                        Category = TaskCategory.BuildCompile,
                        ErrorCategory = TaskErrorCategory.Warning,
                        Text = Localizer.Format(
                            Strings.OverriddenKeyWarningFormat,
                            definition.Key,
                            Relative(index, item.UsedInstead.FilePath),
                            item.UsedInstead.LineNumber,
                            Path.GetFileName(item.LoadedThrough)),
                        Document = definition.FilePath,

                        // The Error List counts from 0, the editor shows lines from 1.
                        Line = Math.Max(0, definition.LineNumber - 1),
                        Column = Math.Max(0, definition.Column - 1),
                    };
                    task.Navigate += (s, e) =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        _navigator.NavigateTo(definition);
                    };
                    _errorList.Tasks.Add(task);
                }
            }
            finally
            {
                _errorList.ResumeRefresh();
            }
        }

        private void Clear()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _errorList.Tasks.Clear();
        }

        private string GetSolutionFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(((IServiceProvider)_package).GetService(typeof(SVsSolution)) is IVsSolution solution)
                || solution.GetSolutionInfo(out string directory, out string file, out string _) != 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(file))
            {
                return file;
            }

            // Open Folder has no .sln. Any path in the folder lets the index find its root.
            return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, "_");
        }

        private static string Relative(ResourceIndex index, string path)
        {
            string root = index.RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
        }
    }
}
