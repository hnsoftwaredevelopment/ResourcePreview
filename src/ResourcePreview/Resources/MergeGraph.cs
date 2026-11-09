using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ResourcePreview.Resources
{
    /// <summary>What a definition means for a lookup from one file.</summary>
    internal enum DefinitionStatus
    {
        /// <summary>WPF finds this definition first.</summary>
        Used,

        /// <summary>WPF can reach it, but finds another definition first.</summary>
        Overridden,

        /// <summary>Not in the lookup path from this file, for example the resources of another window.</summary>
        Unknown,
    }

    /// <summary>One definition of a key, with its status for the file the lookup starts from.</summary>
    internal sealed class ResourceDefinition
    {
        public ResourceDefinition(ResourceEntry entry, DefinitionStatus status, ResourceEntry overriddenBy, bool isInUnmergedFile)
        {
            Entry = entry;
            Status = status;
            OverriddenBy = overriddenBy;
            IsInUnmergedFile = isInUnmergedFile;
        }

        public ResourceEntry Entry { get; }

        public DefinitionStatus Status { get; }

        /// <summary>The definition WPF uses instead. Set when <see cref="Status"/> is Overridden.</summary>
        public ResourceEntry OverriddenBy { get; }

        /// <summary>
        /// The file is a ResourceDictionary that no MergedDictionaries in the solution refers to.
        /// WPF only loads it when code does that, for example Application.Current.Resources.MergedDictionaries.Add.
        /// </summary>
        public bool IsInUnmergedFile { get; }
    }

    /// <summary>
    /// Which file merges which, for one solution, and the WPF lookup order that follows from it.
    /// Built from a snapshot of the index, so it never changes and any thread can use it.
    ///
    /// WPF looks up a key like this:
    ///   1. the element and its parents up to the Window (we treat the whole file as one scope);
    ///   2. App.xaml (Application.Resources);
    ///   3. within one dictionary: its own keys first, then MergedDictionaries from the last to the first,
    ///      each with its own merges.
    /// So in App.xaml with Colors.xaml and then Theme.xaml merged, a key in both comes from Theme.xaml.
    /// </summary>
    internal sealed class MergeGraph
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        // Theme dictionaries WPF loads by itself: Themes\Generic.xaml and Themes\Aero2.NormalColor.xaml and so on.
        private static readonly Regex ThemeFilePattern =
            new Regex(@"^(Generic|Classic|Aero|Aero2|AeroLite|Luna|Royale)(\.[^.]+)?\.xaml$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly Dictionary<string, List<string>> _merges = new Dictionary<string, List<string>>(PathComparer);
        private readonly HashSet<string> _mergedSomewhere = new HashSet<string>(PathComparer);
        private readonly HashSet<string> _mergesUnknownFile = new HashSet<string>(PathComparer);
        private readonly ConcurrentDictionary<string, List<string>> _lookupChains = new ConcurrentDictionary<string, List<string>>(PathComparer);
        private readonly Func<string, string> _projectDirectory;

        public MergeGraph(IReadOnlyDictionary<string, XamlFileModel> files, Func<string, string, string> resolveSource, Func<string, string> projectDirectory)
        {
            Files = files;
            _projectDirectory = projectDirectory;

            foreach (KeyValuePair<string, XamlFileModel> file in files)
            {
                if (file.Value.MergedSources.Count == 0)
                {
                    continue;
                }

                List<string> resolved = file.Value.MergedSources
                    .Select(source => resolveSource(source, file.Key))
                    .ToList();

                // A Source we cannot read (a NuGet package, a missing file) may define any key.
                if (resolved.Any(path => path == null || !files.ContainsKey(path) && !File.Exists(path)))
                {
                    _mergesUnknownFile.Add(file.Key);
                }

                resolved.RemoveAll(path => path == null);
                _merges[file.Key] = resolved;
                _mergedSomewhere.UnionWith(resolved);
            }

            Applications = files
                .Where(f => f.Value.RootKind == XamlRootKind.Application)
                .Select(f => f.Key)
                .OrderBy(f => f, PathComparer)
                .ToList();
        }

        public IReadOnlyDictionary<string, XamlFileModel> Files { get; }

        /// <summary>All App.xaml files (root element Application), sorted by path.</summary>
        public IReadOnlyList<string> Applications { get; }

        /// <summary>
        /// The file, then what it merges: the last merged dictionary first, each followed by its own merges.
        /// Each file appears once, so a dictionary that merges itself through another does not loop.
        /// </summary>
        public List<string> GetChain(string file)
        {
            var chain = new List<string>();
            Visit(file, chain, new HashSet<string>(PathComparer));
            return chain;
        }

        private void Visit(string file, List<string> chain, HashSet<string> seen)
        {
            if (!seen.Add(file))
            {
                return;
            }

            chain.Add(file);
            if (_merges.TryGetValue(file, out List<string> merged))
            {
                for (int i = merged.Count - 1; i >= 0; i--)
                {
                    Visit(merged[i], chain, seen);
                }
            }
        }

        /// <summary>
        /// The App.xaml whose resources a file sees. First one that merges the file (directly or not),
        /// then the one in the same project, then the only one in the solution. Null if none fits.
        /// </summary>
        public string FindApplication(string file)
        {
            foreach (string application in Applications)
            {
                if (GetChain(application).Contains(file, PathComparer))
                {
                    return application;
                }
            }

            string project = _projectDirectory(file);
            foreach (string application in Applications)
            {
                if (PathComparer.Equals(_projectDirectory(application), project))
                {
                    return application;
                }
            }

            return Applications.Count == 1 ? Applications[0] : null;
        }

        /// <summary>The order in which WPF searches files for a key used in <paramref name="file"/>. Do not change the list.</summary>
        public List<string> GetLookupChain(string file) => _lookupChains.GetOrAdd(file, BuildLookupChain);

        /// <summary>
        /// True when we know every file WPF searches for a key used in <paramref name="file"/>: there is an
        /// App.xaml for it, and nothing on the way merges a dictionary from outside the solution. Only then
        /// can we say a key does not exist there.
        /// </summary>
        public bool IsLookupComplete(string file)
        {
            bool isApplication = Files.TryGetValue(file, out XamlFileModel model) && model.RootKind == XamlRootKind.Application;
            return (isApplication || FindApplication(file) != null) && !GetLookupChain(file).Any(_mergesUnknownFile.Contains);
        }

        private List<string> BuildLookupChain(string file)
        {
            List<string> chain = GetChain(file);
            string application = FindApplication(file);
            if (application != null)
            {
                var seen = new HashSet<string>(chain, PathComparer);
                chain.AddRange(GetChain(application).Where(seen.Add));
            }

            return chain;
        }

        /// <summary>
        /// False for a ResourceDictionary file that nothing merges and that is not a theme file WPF loads
        /// by itself. True for files the index does not know, so they are never reported by mistake.
        /// </summary>
        public bool IsLoaded(string file)
        {
            if (!Files.TryGetValue(file, out XamlFileModel model) || model.RootKind != XamlRootKind.ResourceDictionary)
            {
                return true;
            }

            return _mergedSomewhere.Contains(file) || IsThemeFile(file);
        }

        internal static bool IsThemeFile(string file)
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
            return string.Equals(folder, "Themes", StringComparison.OrdinalIgnoreCase) && ThemeFilePattern.IsMatch(Path.GetFileName(file));
        }

        /// <summary>
        /// Orders the definitions of one key for a lookup from <paramref name="fromFile"/> and marks them.
        /// In other files only keys in the main dictionary count; in the file itself every key counts,
        /// because we do not know which element the reference is on.
        /// </summary>
        public IReadOnlyList<ResourceDefinition> Resolve(IReadOnlyList<ResourceEntry> matches, string fromFile)
        {
            var result = new List<ResourceDefinition>();
            var placed = new HashSet<ResourceEntry>();
            ResourceEntry winner = null;

            foreach (string file in GetLookupChain(fromFile))
            {
                bool isFromFile = PathComparer.Equals(file, fromFile);
                IEnumerable<ResourceEntry> inFile = matches
                    .Where(e => PathComparer.Equals(e.FilePath, file) && (isFromFile || e.IsRootScope))
                    .OrderBy(e => e.LineNumber);

                foreach (ResourceEntry entry in inFile)
                {
                    if (!placed.Add(entry))
                    {
                        continue;
                    }

                    if (winner == null)
                    {
                        winner = entry;
                        result.Add(new ResourceDefinition(entry, DefinitionStatus.Used, null, !IsLoaded(entry.FilePath)));
                    }
                    else
                    {
                        result.Add(new ResourceDefinition(entry, DefinitionStatus.Overridden, winner, !IsLoaded(entry.FilePath)));
                    }
                }
            }

            result.AddRange(matches
                .Where(e => !placed.Contains(e))
                .Select(e => new ResourceDefinition(e, DefinitionStatus.Unknown, null, !IsLoaded(e.FilePath)))
                .OrderBy(d => d.IsInUnmergedFile ? 1 : 0)
                .ThenBy(d => d.Entry.FilePath, PathComparer)
                .ThenBy(d => d.Entry.LineNumber));

            return result;
        }
    }
}
