using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ResourcePreview.Resources
{
    /// <summary>One {StaticResource X} or {DynamicResource X} somewhere in the solution.</summary>
    internal sealed class ResourceUsage
    {
        public ResourceUsage(string filePath, ResourceUse use, ResourceEntry resolvesTo)
        {
            FilePath = filePath;
            Use = use;
            ResolvesTo = resolvesTo;
        }

        public string FilePath { get; }

        public ResourceUse Use { get; }

        /// <summary>The definition WPF uses at this place, or null if WPF does not find one there.</summary>
        public ResourceEntry ResolvesTo { get; }
    }

    /// <summary>All definitions and uses of one key.</summary>
    internal sealed class UsageReport
    {
        public UsageReport(string key, ResourceEntry selected, IReadOnlyList<ResourceEntry> definitions, IReadOnlyList<ResourceUsage> usages)
        {
            Key = key;
            Selected = selected;
            Definitions = definitions;
            Usages = usages;
        }

        public string Key { get; }

        /// <summary>The definition the search started from, or null for a key without definitions.</summary>
        public ResourceEntry Selected { get; }

        public IReadOnlyList<ResourceEntry> Definitions { get; }

        /// <summary>Sorted by file, then line.</summary>
        public IReadOnlyList<ResourceUsage> Usages { get; }

        /// <summary>
        /// True when this usage gets another definition than <see cref="Selected"/>, for example because a
        /// dictionary merged later overrides it. Shown as a note in the list.
        /// </summary>
        public bool UsesOtherDefinition(ResourceUsage usage) =>
            Selected != null && usage.ResolvesTo != null && !IsSame(usage.ResolvesTo, Selected);

        public static bool IsSame(ResourceEntry a, ResourceEntry b) =>
            string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase) && a.LineNumber == b.LineNumber;
    }

    /// <summary>
    /// Finds every place a key is used. Uses the references the index already collects for the reference
    /// check (<see cref="XamlFileModel.Uses"/>), so it does not read files again. Runs on any thread.
    /// </summary>
    internal static class UsageFinder
    {
        public static UsageReport Find(ResourceIndex index, string key, ResourceEntry selected, CancellationToken cancellationToken)
        {
            MergeGraph graph = index.GetMergeGraph(cancellationToken);

            List<ResourceEntry> definitions = graph.Files
                .SelectMany(f => f.Value.Entries)
                .Where(e => string.Equals(e.Key, key, StringComparison.Ordinal))
                .OrderBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.LineNumber)
                .ToList();

            var usages = new List<ResourceUsage>();
            foreach (KeyValuePair<string, XamlFileModel> file in graph.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<ResourceUse> uses = file.Value.Uses.Where(u => string.Equals(u.Key, key, StringComparison.Ordinal)).ToList();
                if (uses.Count == 0)
                {
                    continue;
                }

                // Same lookup order as the tooltip: the first "Used" definition is the one WPF takes here.
                ResourceEntry resolvesTo = definitions.Count == 0
                    ? null
                    : graph.Resolve(definitions, file.Key).FirstOrDefault(d => d.Status == DefinitionStatus.Used)?.Entry;

                usages.AddRange(uses.Select(u => new ResourceUsage(file.Key, u, resolvesTo)));
            }

            // Resolve the selected entry to the instance in this snapshot, so IsSame works on fresh data.
            ResourceEntry current = selected == null ? null : definitions.FirstOrDefault(d => UsageReport.IsSame(d, selected)) ?? selected;
            return new UsageReport(key, current, definitions, usages);
        }
    }
}
