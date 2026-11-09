using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ResourcePreview.Resources
{
    /// <summary>A definition that WPF never uses, because another one comes first in the lookup order.</summary>
    internal sealed class OverriddenKey
    {
        public OverriddenKey(ResourceEntry definition, ResourceEntry usedInstead, string loadedThrough)
        {
            Definition = definition;
            UsedInstead = usedInstead;
            LoadedThrough = loadedThrough;
        }

        /// <summary>The definition that is not used.</summary>
        public ResourceEntry Definition { get; }

        /// <summary>The definition WPF finds first.</summary>
        public ResourceEntry UsedInstead { get; }

        /// <summary>The App.xaml or Window file whose MergedDictionaries cause this.</summary>
        public string LoadedThrough { get; }
    }

    /// <summary>
    /// Finds keys that are defined more than once in what App.xaml (or a Window, UserControl ...) loads.
    /// Only the first definition in the WPF lookup order counts; the others are reported.
    ///
    /// Not reported:
    /// - a Window key that hides an App.xaml key: that is usually on purpose;
    /// - two definitions in the same file: the XAML compiler already reports those;
    /// - keys in nested scopes such as Style.Resources.
    /// </summary>
    internal static class OverriddenKeyScanner
    {
        public static IReadOnlyList<OverriddenKey> Scan(ResourceIndex index, CancellationToken cancellationToken)
        {
            return Scan(index.GetMergeGraph(cancellationToken), cancellationToken);
        }

        internal static IReadOnlyList<OverriddenKey> Scan(MergeGraph graph, CancellationToken cancellationToken)
        {
            IEnumerable<string> roots = graph.Files
                .Where(f => f.Value.RootKind != XamlRootKind.ResourceDictionary)
                .OrderBy(f => f.Value.RootKind == XamlRootKind.Application ? 0 : 1)
                .ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.Key);

            var result = new List<OverriddenKey>();
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<string> chain = graph.GetChain(root);
                if (chain.Count < 2)
                {
                    continue; // merges nothing, so nothing can be overridden
                }

                var first = new Dictionary<string, ResourceEntry>(StringComparer.Ordinal);
                foreach (string file in chain)
                {
                    if (!graph.Files.TryGetValue(file, out XamlFileModel model))
                    {
                        continue;
                    }

                    foreach (ResourceEntry entry in model.Entries.Where(e => e.IsRootScope).OrderBy(e => e.LineNumber))
                    {
                        if (!first.TryGetValue(entry.Key, out ResourceEntry winner))
                        {
                            first[entry.Key] = entry;
                        }
                        else if (!string.Equals(winner.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase)
                            && reported.Add(entry.FilePath + "|" + entry.LineNumber + "|" + entry.Column))
                        {
                            result.Add(new OverriddenKey(entry, winner, root));
                        }
                    }
                }
            }

            return result;
        }
    }
}
