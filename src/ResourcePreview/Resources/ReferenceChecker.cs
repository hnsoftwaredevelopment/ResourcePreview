using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ResourcePreview.Resources
{
    internal enum ReferenceProblemKind
    {
        /// <summary>No definition anywhere in the solution.</summary>
        Missing,

        /// <summary>Only defined further down in the same file. StaticResource does not look ahead.</summary>
        DefinedLater,

        /// <summary>Defined, but in a file that WPF does not search from here (not merged, or another window).</summary>
        NotLoaded,
    }

    /// <summary>A resource reference that WPF will not resolve at runtime.</summary>
    internal sealed class ReferenceProblem
    {
        public ReferenceProblem(string filePath, ResourceUse use, ReferenceProblemKind kind, ResourceEntry definition, string suggestion)
        {
            FilePath = filePath;
            Use = use;
            Kind = kind;
            Definition = definition;
            Suggestion = suggestion;
        }

        public string FilePath { get; }

        public ResourceUse Use { get; }

        public ReferenceProblemKind Kind { get; }

        /// <summary>For DefinedLater and NotLoaded: the definition that WPF does not find.</summary>
        public ResourceEntry Definition { get; }

        /// <summary>For Missing: a key that differs only in upper and lower case, or null. Keys are case-sensitive.</summary>
        public string Suggestion { get; }
    }

    /// <summary>
    /// Finds StaticResource and DynamicResource references that WPF will not resolve. A missing StaticResource
    /// throws a XamlParseException while loading, which otherwise only shows up when the app runs.
    ///
    /// We only report what we can be sure of, using the same lookup order as the tooltip (<see cref="MergeGraph"/>):
    /// - Missing: no file in the solution defines the key. Skipped when the lookup passes a dictionary from
    ///   outside the solution (a NuGet theme), or when there is no App.xaml: those may define it. For a static
    ///   key ({x:Static res:TemplateKeys.A}) only when the solution defines other keys of that class.
    /// - DefinedLater (StaticResource only): the only reachable definition is lower in the same file.
    /// - NotLoaded (StaticResource only): defined, but not in any file WPF searches from here. Only for Windows,
    ///   App.xaml and dictionaries. A UserControl also sees the resources of the window it is placed in, which we do not know.
    /// A DynamicResource is looked up again at runtime and may be added in code, so only Missing applies to it.
    /// </summary>
    internal static class ReferenceChecker
    {
        public static IReadOnlyList<ReferenceProblem> CheckAll(ResourceIndex index, CancellationToken cancellationToken)
        {
            MergeGraph graph = index.GetMergeGraph(cancellationToken);
            var keys = new KeyTable(graph);

            var result = new List<ReferenceProblem>();
            foreach (KeyValuePair<string, XamlFileModel> file in graph.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (ResourceUse use in file.Value.Uses)
                {
                    ReferenceProblem problem = Check(graph, keys, file.Key, use);
                    if (problem != null)
                    {
                        result.Add(problem);
                    }
                }
            }

            return result;
        }

        /// <summary>Checks one reference, for the tooltip. Null when there is no problem.</summary>
        public static ReferenceProblem CheckOne(ResourceIndex index, string filePath, ResourceUse use, CancellationToken cancellationToken)
        {
            MergeGraph graph = index.GetMergeGraph(cancellationToken);
            return Check(graph, new KeyTable(graph), filePath, use);
        }

        private static ReferenceProblem Check(MergeGraph graph, KeyTable keys, string filePath, ResourceUse use)
        {
            IReadOnlyList<ResourceEntry> definitions = keys.Get(use.Key);
            if (definitions.Count == 0)
            {
                // {x:Static SystemColors.ControlBrushKey} comes from WPF, and a key class may be used from code only.
                // Only report a static key when other keys of the same class are defined in XAML.
                if (ResourceKeys.IsStatic(use.Key) && !keys.HasStaticType(ResourceKeys.StaticTypeName(use.Key)))
                {
                    return null;
                }

                return graph.IsLookupComplete(filePath)
                    ? new ReferenceProblem(filePath, use, ReferenceProblemKind.Missing, null, keys.FindOtherCase(use.Key))
                    : null;
            }

            if (!use.IsStatic)
            {
                return null;
            }

            IReadOnlyList<ResourceDefinition> resolved = graph.Resolve(definitions, filePath);
            List<ResourceEntry> reachable = resolved.Where(d => d.Status != DefinitionStatus.Unknown).Select(d => d.Entry).ToList();

            if (reachable.Count == 0)
            {
                return graph.IsLookupComplete(filePath) && IsWholeLookupKnown(graph, filePath)
                    ? new ReferenceProblem(filePath, use, ReferenceProblemKind.NotLoaded, resolved[0].Entry, null)
                    : null;
            }

            bool definedHereAboveOrElsewhere = reachable.Any(e =>
                !string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase) || e.LineNumber <= use.Line);
            if (!definedHereAboveOrElsewhere)
            {
                return new ReferenceProblem(filePath, use, ReferenceProblemKind.DefinedLater, reachable.OrderBy(e => e.LineNumber).First(), null);
            }

            return null;
        }

        /// <summary>
        /// A Window, App.xaml or ResourceDictionary only sees what we follow. A UserControl, Page or custom root
        /// also sees the resources of whatever hosts it at runtime.
        /// </summary>
        private static bool IsWholeLookupKnown(MergeGraph graph, string filePath)
        {
            if (!graph.Files.TryGetValue(filePath, out XamlFileModel model))
            {
                return false;
            }

            return model.RootKind != XamlRootKind.Other || model.RootName.EndsWith("Window", StringComparison.Ordinal);
        }

        /// <summary>All definitions by key, built once per check.</summary>
        private sealed class KeyTable
        {
            private static readonly IReadOnlyList<ResourceEntry> None = new ResourceEntry[0];
            private readonly Dictionary<string, List<ResourceEntry>> _byKey = new Dictionary<string, List<ResourceEntry>>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _byLowerCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _staticTypes = new HashSet<string>(StringComparer.Ordinal);

            public KeyTable(MergeGraph graph)
            {
                foreach (ResourceEntry entry in graph.Files.Values.SelectMany(f => f.Entries))
                {
                    if (!_byKey.TryGetValue(entry.Key, out List<ResourceEntry> list))
                    {
                        _byKey[entry.Key] = list = new List<ResourceEntry>();
                        _byLowerCase[entry.Key] = entry.Key;
                        if (ResourceKeys.IsStatic(entry.Key))
                        {
                            _staticTypes.Add(ResourceKeys.StaticTypeName(entry.Key));
                        }
                    }

                    list.Add(entry);
                }
            }

            public IReadOnlyList<ResourceEntry> Get(string key) => _byKey.TryGetValue(key, out List<ResourceEntry> list) ? list : None;

            public bool HasStaticType(string typeName) => typeName != null && _staticTypes.Contains(typeName);

            public string FindOtherCase(string key) => _byLowerCase.TryGetValue(key, out string match) ? match : null;
        }

        /// <summary>File name with the folder relative to the solution, for messages.</summary>
        public static string Relative(ResourceIndex index, string path)
        {
            string root = index.RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
        }
    }
}
