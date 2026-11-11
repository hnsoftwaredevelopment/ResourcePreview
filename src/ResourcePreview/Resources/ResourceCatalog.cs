using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ResourcePreview.Resources
{
    /// <summary>The groups of the "By type" view, in the order they are shown.</summary>
    internal enum ResourceCategory
    {
        Brushes,
        Colors,
        Images,
        Styles,
        Templates,
        Values,
    }

    /// <summary>What the Error List would say about a definition, shown as a note in the window.</summary>
    [Flags]
    internal enum CatalogFlags
    {
        None = 0,

        /// <summary>A dictionary merged later defines the same key; WPF never uses this one.</summary>
        Overridden = 1,

        /// <summary>No XAML or code uses the key.</summary>
        Unused = 2,

        /// <summary>The file is in no MergedDictionaries, so WPF does not load it.</summary>
        NotLoaded = 4,
    }

    /// <summary>One resource in the window.</summary>
    internal sealed class CatalogItem
    {
        public CatalogItem(ResourceEntry entry, ResourceCategory category, CatalogFlags flags, ResourceEntry overriddenBy)
        {
            Entry = entry;
            Category = category;
            Flags = flags;
            OverriddenBy = overriddenBy;
        }

        public ResourceEntry Entry { get; }

        public string Key => Entry.Key;

        public ResourceCategory Category { get; }

        public CatalogFlags Flags { get; }

        /// <summary>For <see cref="CatalogFlags.Overridden"/>: the definition WPF uses instead.</summary>
        public ResourceEntry OverriddenBy { get; }
    }

    /// <summary>A node in the window: a file or a category, with its resources.</summary>
    internal sealed class CatalogGroup
    {
        public CatalogGroup(string filePath, ResourceCategory? category, IReadOnlyList<CatalogItem> items)
        {
            FilePath = filePath;
            Category = category;
            Items = items;
        }

        /// <summary>Set for "By file".</summary>
        public string FilePath { get; }

        /// <summary>Set for "By type".</summary>
        public ResourceCategory? Category { get; }

        public IReadOnlyList<CatalogItem> Items { get; }
    }

    /// <summary>
    /// The content of the XAML Resources window: which resources, grouped how, with which notes.
    /// Uses no VS APIs, so it is unit tested; the window only draws the result.
    /// </summary>
    internal static class ResourceCatalog
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        /// <summary>
        /// All resources in the main dictionary of each file (not those in Grid.Resources or Style.Resources),
        /// or, with <paramref name="activeFile"/>, only what that file can use: one definition per key,
        /// the one WPF picks, in lookup order.
        /// </summary>
        public static IReadOnlyList<CatalogItem> Build(
            MergeGraph graph,
            string activeFile,
            IReadOnlyList<OverriddenKey> overridden,
            IEnumerable<ResourceEntry> unused)
        {
            var overriddenBy = new Dictionary<ResourceEntry, ResourceEntry>();
            foreach (OverriddenKey key in overridden)
            {
                overriddenBy[key.Definition] = key.UsedInstead;
            }

            var unusedSet = new HashSet<ResourceEntry>(unused);

            IEnumerable<ResourceEntry> entries = activeFile == null
                ? graph.Files.Values.SelectMany(f => f.Entries).Where(e => e.IsRootScope)
                : VisibleFrom(graph, activeFile);

            return entries
                .Select(e =>
                {
                    CatalogFlags flags = CatalogFlags.None;
                    overriddenBy.TryGetValue(e, out ResourceEntry winner);
                    if (winner != null)
                    {
                        flags |= CatalogFlags.Overridden;
                    }

                    if (unusedSet.Contains(e))
                    {
                        flags |= CatalogFlags.Unused;
                    }

                    if (!graph.IsLoaded(e.FilePath))
                    {
                        flags |= CatalogFlags.NotLoaded;
                    }

                    return new CatalogItem(e, CategoryOf(e.ElementTypeName), flags, winner);
                })
                .ToList();
        }

        /// <summary>What <paramref name="file"/> can use: its own keys, then its merges and those of App.xaml. First key wins.</summary>
        private static IEnumerable<ResourceEntry> VisibleFrom(MergeGraph graph, string file)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string chainFile in graph.GetLookupChain(file))
            {
                if (!graph.Files.TryGetValue(chainFile, out XamlFileModel model))
                {
                    continue;
                }

                bool isFile = PathComparer.Equals(chainFile, file);
                foreach (ResourceEntry entry in model.Entries.Where(e => isFile || e.IsRootScope).OrderBy(e => e.LineNumber))
                {
                    if (keys.Add(entry.Key))
                    {
                        yield return entry;
                    }
                }
            }
        }

        /// <summary>Which group of the "By type" view a resource belongs to, by its element name.</summary>
        internal static ResourceCategory CategoryOf(string elementType)
        {
            if (elementType == "Style")
            {
                return ResourceCategory.Styles;
            }

            if (elementType.EndsWith("Template", StringComparison.Ordinal))
            {
                return ResourceCategory.Templates;
            }

            if (elementType == "Color")
            {
                return ResourceCategory.Colors;
            }

            if (elementType.EndsWith("Brush", StringComparison.Ordinal))
            {
                return ResourceCategory.Brushes;
            }

            if (elementType.EndsWith("Geometry", StringComparison.Ordinal)
                || elementType.EndsWith("Drawing", StringComparison.Ordinal)
                || elementType.StartsWith("Bitmap", StringComparison.Ordinal)
                || elementType == "GeometryGroup"
                || elementType == "DrawingGroup"
                || elementType == "DrawingImage"
                || elementType == "Path"
                || elementType == "Canvas"
                || elementType == "Viewbox")
            {
                return ResourceCategory.Images;
            }

            return ResourceCategory.Values;
        }

        /// <summary>Keeps the items whose key contains <paramref name="search"/>, ignoring case. Empty search keeps all.</summary>
        public static IEnumerable<CatalogItem> Filter(IEnumerable<CatalogItem> items, string search)
        {
            string text = search?.Trim();
            return string.IsNullOrEmpty(text)
                ? items
                : items.Where(i => i.Key.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// "By file": the files in the order WPF searches them from App.xaml (the last merged dictionary
        /// first, as in the lookup), then the other files alphabetically. Resources in file order.
        /// </summary>
        public static IReadOnlyList<CatalogGroup> GroupByFile(IEnumerable<CatalogItem> items, MergeGraph graph, string activeFile)
        {
            var order = new List<string>();
            IEnumerable<string> chains = activeFile != null
                ? graph.GetLookupChain(activeFile)
                : graph.Applications.SelectMany(graph.GetLookupChain);
            foreach (string file in chains)
            {
                if (!order.Contains(file, PathComparer))
                {
                    order.Add(file);
                }
            }

            return items
                .GroupBy(i => i.Entry.FilePath, PathComparer)
                .OrderBy(g =>
                {
                    int position = order.FindIndex(f => PathComparer.Equals(f, g.Key));
                    return position < 0 ? int.MaxValue : position;
                })
                .ThenBy(g => Path.GetFileName(g.Key), PathComparer)
                .Select(g => new CatalogGroup(g.Key, null, g.OrderBy(i => i.Entry.LineNumber).ToList()))
                .ToList();
        }

        /// <summary>"By type": the categories in a fixed order, resources by key.</summary>
        public static IReadOnlyList<CatalogGroup> GroupByType(IEnumerable<CatalogItem> items) =>
            items
                .GroupBy(i => i.Category)
                .OrderBy(g => g.Key)
                .Select(g => new CatalogGroup(null, g.Key, g.OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Entry.FilePath, PathComparer).ToList()))
                .ToList();
    }
}
