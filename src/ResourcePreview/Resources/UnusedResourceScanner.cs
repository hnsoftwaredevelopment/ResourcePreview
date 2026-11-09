using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ResourcePreview.Resources
{
    /// <summary>
    /// Finds resource definitions that nothing uses: no {StaticResource}, no {DynamicResource} in any .xaml
    /// file, and no string with that key in any .cs or .vb file (FindResource("X"), Resources["X"]).
    ///
    /// To avoid false alarms, only definitions in files the app loads are checked: App.xaml, windows and
    /// controls, and dictionaries that are merged somewhere. Not checked:
    /// - solutions without App.xaml (a class library: the app that uses it is elsewhere);
    /// - dictionaries that nothing merges (they may be loaded by another solution or from code);
    /// - theme files such as Themes\Generic.xaml;
    /// - the dictionaries inside ResourceDictionary.ThemeDictionaries, whose keys ("Light", "Dark") WPF uses itself.
    /// </summary>
    internal static class UnusedResourceScanner
    {
        public static IReadOnlyList<ResourceEntry> Scan(ResourceIndex index, CancellationToken cancellationToken)
        {
            MergeGraph graph = index.GetMergeGraph(cancellationToken);
            if (graph.Applications.Count == 0)
            {
                return Array.Empty<ResourceEntry>();
            }

            return Scan(graph, CodeStrings.Collect(index.RootDirectory, cancellationToken), cancellationToken);
        }

        internal static IReadOnlyList<ResourceEntry> Scan(MergeGraph graph, ISet<string> codeStrings, CancellationToken cancellationToken)
        {
            var used = new HashSet<string>(graph.Files.Values.SelectMany(f => f.Uses).Select(u => u.Key), StringComparer.Ordinal);

            var result = new List<ResourceEntry>();
            foreach (KeyValuePair<string, XamlFileModel> file in graph.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool isMergedDictionary = file.Value.RootKind == XamlRootKind.ResourceDictionary
                    && graph.IsLoaded(file.Key)
                    && !MergeGraph.IsThemeFile(file.Key);
                if (file.Value.RootKind == XamlRootKind.ResourceDictionary && !isMergedDictionary)
                {
                    continue;
                }

                result.AddRange(file.Value.Entries.Where(e =>
                    e.ElementTypeName != "ResourceDictionary"
                    && !used.Contains(e.Key)
                    && !codeStrings.Contains(e.Key)));
            }

            return result;
        }
    }
}
