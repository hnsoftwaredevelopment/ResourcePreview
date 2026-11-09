using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        private readonly Dictionary<string, XamlFileModel> _entriesByFile = new Dictionary<string, XamlFileModel>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, XamlFileModel> _liveOverrides = new Dictionary<string, XamlFileModel>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _projectDirectories = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirtyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<string> _projectFiles = new List<string>();
        private FileSystemWatcher _watcher;
        private volatile bool _initialized;
        private int _version;
        private MergeGraph _graph;
        private int _graphVersion = -1;

        private ResourceIndex(string rootDirectory)
        {
            RootDirectory = rootDirectory;
        }

        public string RootDirectory { get; }

        /// <summary>
        /// True after the first scan. Code on the UI thread checks this first, because the first scan
        /// reads every .xaml file in the solution and must run in the background.
        /// </summary>
        public bool IsReady => _initialized;

        /// <summary>
        /// Raised on a background thread when a .xaml file under the root changes on disk.
        /// The index itself rereads the file on the next lookup.
        /// </summary>
        public event EventHandler FilesChanged;

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
            XamlFileModel model = ParseModel(filePath, text);
            if (model == null)
            {
                return;
            }

            lock (_gate)
            {
                _liveOverrides[filePath] = model;
                _version++;
            }
        }

        public void ClearLiveText(string filePath)
        {
            lock (_gate)
            {
                if (_liveOverrides.Remove(filePath))
                {
                    _version++;
                }
            }
        }

        /// <summary>
        /// All definitions of a key, in the order WPF would look them up from <paramref name="preferredFile"/>:
        /// see <see cref="Resolve"/>. The first one is the definition WPF uses there.
        /// </summary>
        public IReadOnlyList<ResourceEntry> Find(string key, string preferredFile, CancellationToken cancellationToken)
        {
            return Resolve(key, preferredFile, cancellationToken).Select(d => d.Entry).ToList();
        }

        /// <summary>
        /// All definitions of a key with their status as seen from <paramref name="fromFile"/>. Order:
        /// <list type="number">
        /// <item>the file itself (its own keys win over what it merges)</item>
        /// <item>its MergedDictionaries, the last one first, each with its own merges</item>
        /// <item>App.xaml and its MergedDictionaries, in the same way</item>
        /// <item>definitions this lookup does not reach, alphabetically; files that nothing merges last</item>
        /// </list>
        /// </summary>
        public IReadOnlyList<ResourceDefinition> Resolve(string key, string fromFile, CancellationToken cancellationToken)
        {
            EnsureUpToDate(cancellationToken);

            var matches = new List<ResourceEntry>();
            lock (_gate)
            {
                foreach (KeyValuePair<string, XamlFileModel> file in EnumerateFiles())
                {
                    foreach (ResourceEntry entry in file.Value.Entries)
                    {
                        if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                        {
                            matches.Add(entry);
                        }
                    }
                }
            }

            if (matches.Count == 0)
            {
                return Array.Empty<ResourceDefinition>();
            }

            return CurrentGraph().Resolve(matches, fromFile);
        }

        /// <summary>Which files merge which, for the whole solution. Rebuilt only after a change.</summary>
        internal MergeGraph GetMergeGraph(CancellationToken cancellationToken)
        {
            EnsureUpToDate(cancellationToken);
            return CurrentGraph();
        }

        private MergeGraph CurrentGraph()
        {
            Dictionary<string, XamlFileModel> files;
            int version;
            lock (_gate)
            {
                if (_graph != null && _graphVersion == _version)
                {
                    return _graph;
                }

                files = EnumerateFiles().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                version = _version;
            }

            // Resolving Source paths touches the disk, so it happens outside the lock.
            var graph = new MergeGraph(files, ResolveSource, GetProjectDirectory);
            lock (_gate)
            {
                _graph = graph;
                _graphVersion = version;
            }

            return graph;
        }

        /// <summary>
        /// Turns a MergedDictionaries Source into a file path, the way WPF resolves it in compiled XAML:
        /// <list type="bullet">
        /// <item>"Brushes.xaml" or "../Themes/Dark.xaml": relative to the folder of the file that merges it</item>
        /// <item>"/Themes/Dark.xaml": relative to the project folder</item>
        /// <item>"/MyApp.Styles;component/Dark.xaml", with or without "pack://application:,,,": in that project</item>
        /// </list>
        /// Returns null for sources outside the solution, such as pack://siteoforigin: or an http address.
        /// </summary>
        internal string ResolveSource(string source, string fromFile)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            string location = source.Trim();
            const string PackPrefix = "pack://application:,,,";
            if (location.StartsWith(PackPrefix, StringComparison.OrdinalIgnoreCase))
            {
                location = location.Substring(PackPrefix.Length);
            }

            string directory;
            string path;
            int component = location.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
            if (location.StartsWith("/", StringComparison.Ordinal) && component > 1)
            {
                // A dictionary from a NuGet package or another solution: we cannot read it.
                string assembly = location.Substring(1, component - 1);
                directory = FindProjectDirectoryByAssemblyName(assembly);
                if (directory == null)
                {
                    return null;
                }

                path = location.Substring(component + ";component/".Length);
            }
            else if (location.Contains("://"))
            {
                return null;
            }
            else if (location.StartsWith("/", StringComparison.Ordinal))
            {
                directory = GetProjectDirectory(fromFile);
                path = location.TrimStart('/');
            }
            else
            {
                directory = Path.GetDirectoryName(fromFile);
                path = location;
            }

            try
            {
                return Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(path).Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception)
            {
                return null; // invalid characters in the path
            }
        }

        /// <summary><see cref="FindProjectDirectory"/>, remembered per folder because it reads the disk.</summary>
        internal string GetProjectDirectory(string filePath)
        {
            string folder = Path.GetDirectoryName(filePath) ?? string.Empty;
            return _projectDirectories.GetOrAdd(folder, _ => FindProjectDirectory(filePath));
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
        private IEnumerable<KeyValuePair<string, XamlFileModel>> EnumerateFiles()
        {
            foreach (KeyValuePair<string, XamlFileModel> live in _liveOverrides)
            {
                yield return live;
            }

            foreach (KeyValuePair<string, XamlFileModel> file in _entriesByFile)
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
            var entries = new Dictionary<string, XamlFileModel>(StringComparer.OrdinalIgnoreCase);
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

                // Files without keys still count when they merge something, such as App.xaml.
                XamlFileModel parsed = ParseFile(file);
                if (parsed != null && (parsed.Entries.Count > 0 || parsed.MergedSources.Count > 0 || parsed.Uses.Count > 0 || parsed.RootKind == XamlRootKind.Application))
                {
                    entries[file] = parsed;
                }
            }

            lock (_gate)
            {
                _entriesByFile.Clear();
                foreach (KeyValuePair<string, XamlFileModel> pair in entries)
                {
                    _entriesByFile[pair.Key] = pair.Value;
                }

                _projectFiles = projects;
                _dirtyFiles.Clear();
                _version++;
            }
        }

        private void ReparseFromDisk(string file)
        {
            if (!File.Exists(file))
            {
                lock (_gate)
                {
                    _entriesByFile.Remove(file);
                    _version++;
                }

                return;
            }

            XamlFileModel parsed = ParseFile(file);
            if (parsed == null)
            {
                return; // invalid XML or file locked: keep the previous version
            }

            lock (_gate)
            {
                _entriesByFile[file] = parsed;
                _version++;
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

            FilesChanged?.Invoke(this, EventArgs.Empty);
        }

        private static XamlFileModel ParseFile(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileSize)
                {
                    return new XamlFileModel(new List<ResourceEntry>(), new List<string>(), XamlRootKind.Other);
                }

                return ParseModel(file, File.ReadAllText(file));
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
            return ParseModel(filePath, text)?.Entries.ToList();
        }

        /// <summary>Keys, merged dictionaries and root kind of XAML text. Returns null for invalid XML.</summary>
        internal static XamlFileModel ParseModel(string filePath, string text)
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

            XElement root = document.Root;
            if (root == null)
            {
                return new XamlFileModel(new List<ResourceEntry>(), new List<string>(), XamlRootKind.Other);
            }

            var namespaces = new Dictionary<string, string>();
            foreach (XAttribute attribute in root.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                string prefix = attribute.Name.Namespace == XNamespace.Xmlns ? attribute.Name.LocalName : string.Empty;
                namespaces[prefix] = attribute.Value;
            }

            XamlRootKind kind = root.Name.LocalName == "ResourceDictionary" ? XamlRootKind.ResourceDictionary
                : root.Name.LocalName == "Application" ? XamlRootKind.Application
                : XamlRootKind.Other;
            XElement rootDictionary = FindRootDictionary(root, kind);

            var result = new List<ResourceEntry>();
            foreach (XElement element in root.Descendants())
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
                bool isRootScope = rootDictionary != null && element.Parent == rootDictionary;
                result.Add(new ResourceEntry(keyAttribute.Value, new XElement(element), filePath, line, column, namespaces, isRootScope));
            }

            return new XamlFileModel(result, FindMergedSources(rootDictionary), kind, root.Name.LocalName, FindUses(text));
        }

        private static readonly Regex UsePattern = new Regex(
            @"\{\s*(?<kind>StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?(?<key>[^\s,}{]+)" +
            @"|<\s*(?<kind>StaticResource|DynamicResource)\s+ResourceKey\s*=\s*""(?<key>[^""{]+)""",
            RegexOptions.Compiled);

        private static readonly Regex CommentPattern = new Regex("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>
        /// All {StaticResource X} and {DynamicResource X} references, line by line. Comments are blanked
        /// first, keeping their line breaks so line numbers stay right. A reference split over two lines is missed.
        /// Internal for testing.
        /// </summary>
        internal static List<ResourceUse> FindUses(string text)
        {
            string withoutComments = CommentPattern.Replace(text, m => new string(m.Value.Select(c => c == '\n' || c == '\r' ? c : ' ').ToArray()));
            string[] lines = withoutComments.Replace("\r\n", "\n").Split('\n');

            var uses = new List<ResourceUse>();
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in UsePattern.Matches(lines[i]))
                {
                    Group key = match.Groups["key"];
                    uses.Add(new ResourceUse(key.Value, i + 1, key.Index + 1, match.Groups["kind"].Value == "StaticResource"));
                }
            }

            return uses;
        }

        /// <summary>
        /// The element whose children are the file's main resources: the root ResourceDictionary, or
        /// the ResourceDictionary in Application.Resources / Window.Resources, or that property element itself.
        /// </summary>
        private static XElement FindRootDictionary(XElement root, XamlRootKind kind)
        {
            if (kind == XamlRootKind.ResourceDictionary)
            {
                return root;
            }

            XElement resources = root.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal));
            if (resources == null)
            {
                return null;
            }

            XElement dictionary = resources.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "ResourceDictionary" && e.Attribute(XamlNamespace + "Key") == null);
            return dictionary ?? resources;
        }

        /// <summary>
        /// Source values in ResourceDictionary.MergedDictionaries, in file order. A Source on the root
        /// dictionary itself (&lt;ResourceDictionary Source="..."/&gt; in Window.Resources) counts as the first one.
        /// </summary>
        private static List<string> FindMergedSources(XElement rootDictionary)
        {
            var sources = new List<string>();
            if (rootDictionary == null)
            {
                return sources;
            }

            AddSource(sources, rootDictionary.Attribute("Source"));
            foreach (XElement merged in rootDictionary.Elements().Where(e => e.Name.LocalName == "ResourceDictionary.MergedDictionaries"))
            {
                foreach (XElement dictionary in merged.Elements().Where(e => e.Name.LocalName == "ResourceDictionary"))
                {
                    AddSource(sources, dictionary.Attribute("Source"));
                }
            }

            return sources;
        }

        private static void AddSource(List<string> sources, XAttribute source)
        {
            if (source != null && source.Value.Length > 0 && !source.Value.StartsWith("{", StringComparison.Ordinal))
            {
                sources.Add(source.Value);
            }
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
