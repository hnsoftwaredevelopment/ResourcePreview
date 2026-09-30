using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ResourcePreview.Resources
{
    /// <summary>
    /// Maps each x:Key to its .xaml file per solution folder, since Visual Studio has no public API for this.
    /// Scans on first use, reparses changed files lazily on the next lookup, and prefers open editor buffers.
    /// Uses no Visual Studio or WPF APIs and is thread-safe, so it can run on a background thread.
    /// </summary>
    internal sealed class ResourceIndex
    {
        internal static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        private const long MaxFileSize = 2 * 1024 * 1024;

        private static readonly string[] SkippedDirectories = { "bin", "obj", ".vs", ".git", "node_modules", "packages", "TestResults" };

        private static readonly ConcurrentDictionary<string, ResourceIndex> Indexes =
            new ConcurrentDictionary<string, ResourceIndex>(StringComparer.OrdinalIgnoreCase);

        private readonly object _gate = new object();
        private readonly object _scanGate = new object();
        private readonly Dictionary<string, List<ResourceEntry>> _entriesByFile = new Dictionary<string, List<ResourceEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ResourceEntry>> _liveOverrides = new Dictionary<string, List<ResourceEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirtyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<string> _projectFiles = new List<string>();
        private FileSystemWatcher _watcher;
        private volatile bool _initialized;

        private ResourceIndex(string rootDirectory)
        {
            RootDirectory = rootDirectory;
        }

        public string RootDirectory { get; }

        /// <summary>Returns the index for the solution containing this file. One index per solution folder.</summary>
        public static ResourceIndex ForFile(string filePath)
        {
            string root = FindRootDirectory(filePath);
            return Indexes.GetOrAdd(root, r => new ResourceIndex(r));
        }

        /// <summary>
        /// Stores an open editor buffer, which takes precedence over the file on disk.
        /// Keeps the previous version when the text is not valid XML, for example while typing.
        /// </summary>
        public void SetLiveText(string filePath, string text)
        {
            List<ResourceEntry> entries = ParseXaml(filePath, text);
            if (entries == null)
            {
                return;
            }

            lock (_gate)
            {
                _liveOverrides[filePath] = entries;
            }
        }

        public void ClearLiveText(string filePath)
        {
            lock (_gate)
            {
                _liveOverrides.Remove(filePath);
            }
        }

        /// <summary>
        /// All definitions of a key. Definitions from <paramref name="preferredFile"/> come first,
        /// matching WPF lookup order.
        /// </summary>
        public IReadOnlyList<ResourceEntry> Find(string key, string preferredFile, CancellationToken cancellationToken)
        {
            EnsureUpToDate(cancellationToken);

            var result = new List<ResourceEntry>();
            lock (_gate)
            {
                foreach (KeyValuePair<string, List<ResourceEntry>> file in EnumerateFiles())
                {
                    foreach (ResourceEntry entry in file.Value)
                    {
                        if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                        {
                            result.Add(entry);
                        }
                    }
                }
            }

            return result
                .OrderBy(e => string.Equals(e.FilePath, preferredFile, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.LineNumber)
                .ToList();
        }

        /// <summary>
        /// Finds the project folder for an assembly name in pack URIs such as
        /// pack://application:,,,/MyApp.Icons;component/Images/save.png.
        /// Assumes MyApp.Icons.csproj builds that assembly, which holds in most solutions.
        /// </summary>
        public string FindProjectDirectoryByAssemblyName(string assemblyName)
        {
            List<string> projects;
            lock (_gate)
            {
                projects = _projectFiles;
            }

            string match = projects.FirstOrDefault(p =>
                string.Equals(Path.GetFileNameWithoutExtension(p), assemblyName, StringComparison.OrdinalIgnoreCase));
            return match == null ? null : Path.GetDirectoryName(match);
        }

        /// <summary>Folder of the nearest .csproj or .vbproj above the file.</summary>
        public static string FindProjectDirectory(string filePath)
        {
            DirectoryInfo directory = new FileInfo(filePath).Directory;
            while (directory != null)
            {
                if (directory.EnumerateFiles("*.csproj").Any() || directory.EnumerateFiles("*.vbproj").Any())
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return Path.GetDirectoryName(filePath);
        }

        // Live buffers take precedence over files on disk.
        private IEnumerable<KeyValuePair<string, List<ResourceEntry>>> EnumerateFiles()
        {
            foreach (KeyValuePair<string, List<ResourceEntry>> live in _liveOverrides)
            {
                yield return live;
            }

            foreach (KeyValuePair<string, List<ResourceEntry>> file in _entriesByFile)
            {
                if (!_liveOverrides.ContainsKey(file.Key))
                {
                    yield return file;
                }
            }
        }

        private void EnsureUpToDate(CancellationToken cancellationToken)
        {
            if (!_initialized)
            {
                // One thread scans while others wait, so concurrent lookups do not scan twice.
                lock (_scanGate)
                {
                    if (!_initialized)
                    {
                        FullScan(cancellationToken);
                        StartWatching();
                        _initialized = true;
                    }
                }

                return;
            }

            string[] dirty;
            lock (_gate)
            {
                if (_dirtyFiles.Count == 0)
                {
                    return;
                }

                dirty = _dirtyFiles.ToArray();
                _dirtyFiles.Clear();
            }

            foreach (string file in dirty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReparseFromDisk(file);
            }
        }

        private void FullScan(CancellationToken cancellationToken)
        {
            var entries = new Dictionary<string, List<ResourceEntry>>(StringComparer.OrdinalIgnoreCase);
            var projects = new List<string>();

            foreach (string file in EnumerateSourceFiles(RootDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string extension = Path.GetExtension(file);
                if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
                {
                    projects.Add(file);
                    continue;
                }

                List<ResourceEntry> parsed = ParseFile(file);
                if (parsed != null && parsed.Count > 0)
                {
                    entries[file] = parsed;
                }
            }

            lock (_gate)
            {
                _entriesByFile.Clear();
                foreach (KeyValuePair<string, List<ResourceEntry>> pair in entries)
                {
                    _entriesByFile[pair.Key] = pair.Value;
                }

                _projectFiles = projects;
                _dirtyFiles.Clear();
            }
        }

        private void ReparseFromDisk(string file)
        {
            if (!File.Exists(file))
            {
                lock (_gate)
                {
                    _entriesByFile.Remove(file);
                }

                return;
            }

            List<ResourceEntry> parsed = ParseFile(file);
            if (parsed == null)
            {
                return; // invalid XML or file locked: keep the previous version
            }

            lock (_gate)
            {
                _entriesByFile[file] = parsed;
            }
        }

        private void StartWatching()
        {
            try
            {
                _watcher = new FileSystemWatcher(RootDirectory, "*.xaml")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                _watcher.Changed += (s, e) => MarkDirty(e.FullPath);
                _watcher.Created += (s, e) => MarkDirty(e.FullPath);
                _watcher.Deleted += (s, e) => MarkDirty(e.FullPath);
                _watcher.Renamed += (s, e) => { MarkDirty(e.OldFullPath); MarkDirty(e.FullPath); };
                // Buffer overflow (for example a large git checkout): rescan everything.
                _watcher.Error += (s, e) => _initialized = false;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception)
            {
                // No watcher (network drive, permissions): the index still works, but misses
                // changes to other files until Visual Studio restarts.
                _watcher = null;
            }
        }

        private void MarkDirty(string file)
        {
            if (IsInSkippedDirectory(file))
            {
                return;
            }

            lock (_gate)
            {
                _dirtyFiles.Add(file);
            }
        }

        private static List<ResourceEntry> ParseFile(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileSize)
                {
                    return new List<ResourceEntry>();
                }

                return ParseXaml(file, File.ReadAllText(file));
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads all elements with an x:Key from XAML text. Returns null for invalid XML.
        /// Internal for testing.
        /// </summary>
        internal static List<ResourceEntry> ParseXaml(string filePath, string text)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(text, LoadOptions.SetLineInfo);
            }
            catch (XmlException)
            {
                return null;
            }

            if (document.Root == null)
            {
                return new List<ResourceEntry>();
            }

            var namespaces = new Dictionary<string, string>();
            foreach (XAttribute attribute in document.Root.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                string prefix = attribute.Name.Namespace == XNamespace.Xmlns ? attribute.Name.LocalName : string.Empty;
                namespaces[prefix] = attribute.Value;
            }

            var result = new List<ResourceEntry>();
            foreach (XElement element in document.Root.Descendants())
            {
                XAttribute keyAttribute = element.Attribute(XamlNamespace + "Key");
                if (keyAttribute == null || keyAttribute.Value.StartsWith("{", StringComparison.Ordinal))
                {
                    continue; // no key, or a markup key such as {x:Type Button} or {x:Static ...}
                }

                // Use the x:Key attribute's position as the navigation target, even for multi-line elements.
                var lineInfo = (IXmlLineInfo)keyAttribute;
                int line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;
                int column = lineInfo.HasLineInfo() ? lineInfo.LinePosition : 0;
                result.Add(new ResourceEntry(keyAttribute.Value, new XElement(element), filePath, line, column, namespaces));
            }

            return result;
        }

        private static IEnumerable<string> EnumerateSourceFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                string[] files;
                string[] subdirectories;
                try
                {
                    files = Directory.GetFiles(directory);
                    subdirectories = Directory.GetDirectories(directory);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string file in files)
                {
                    string extension = Path.GetExtension(file);
                    if (extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return file;
                    }
                }

                foreach (string subdirectory in subdirectories)
                {
                    if (!SkippedDirectories.Contains(Path.GetFileName(subdirectory), StringComparer.OrdinalIgnoreCase))
                    {
                        pending.Push(subdirectory);
                    }
                }
            }
        }

        private static bool IsInSkippedDirectory(string file)
        {
            string[] parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Any(p => SkippedDirectories.Contains(p, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Search root: the first folder above the file with a .sln or .slnx, otherwise the git root,
        /// otherwise the project folder, otherwise the file's own folder.
        /// </summary>
        internal static string FindRootDirectory(string filePath)
        {
            DirectoryInfo start = new FileInfo(filePath).Directory;
            string gitRoot = null;
            string projectRoot = null;

            for (DirectoryInfo directory = start; directory != null; directory = directory.Parent)
            {
                try
                {
                    if (directory.EnumerateFiles("*.sln").Any() || directory.EnumerateFiles("*.slnx").Any())
                    {
                        return directory.FullName;
                    }

                    if (projectRoot == null && directory.EnumerateFiles("*.csproj").Any())
                    {
                        projectRoot = directory.FullName;
                    }

                    // Do not search above the repository root.
                    string gitPath = Path.Combine(directory.FullName, ".git");
                    if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    {
                        gitRoot = directory.FullName;
                        break;
                    }
                }
                catch (Exception)
                {
                    break;
                }
            }

            return gitRoot ?? projectRoot ?? start?.FullName ?? Path.GetDirectoryName(filePath);
        }
    }
}
