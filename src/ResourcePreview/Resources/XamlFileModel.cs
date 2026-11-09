using System.Collections.Generic;

namespace ResourcePreview.Resources
{
    /// <summary>What the root element of a .xaml file is. Decides how its resources are loaded.</summary>
    internal enum XamlRootKind
    {
        /// <summary>A &lt;ResourceDictionary&gt; file. Only loaded when something merges it.</summary>
        ResourceDictionary,

        /// <summary>App.xaml: its resources are available to the whole application.</summary>
        Application,

        /// <summary>A Window, UserControl, Page and so on: its resources apply to its own elements.</summary>
        Other,
    }

    /// <summary>A {StaticResource X} or {DynamicResource X} in a file. Line and column are 1-based and point at the key.</summary>
    internal sealed class ResourceUse
    {
        public ResourceUse(string key, int line, int column, bool isStatic)
        {
            Key = key;
            Line = line;
            Column = column;
            IsStatic = isStatic;
        }

        public string Key { get; }

        public int Line { get; }

        public int Column { get; }

        /// <summary>StaticResource (resolved once, while loading) or DynamicResource (looked up again at runtime).</summary>
        public bool IsStatic { get; }
    }

    /// <summary>The parts of one .xaml file that the index needs: its keys, what it merges and which keys it uses.</summary>
    internal sealed class XamlFileModel
    {
        private static readonly IReadOnlyList<ResourceUse> NoUses = new ResourceUse[0];

        public XamlFileModel(IReadOnlyList<ResourceEntry> entries, IReadOnlyList<string> mergedSources, XamlRootKind rootKind, string rootName = null, IReadOnlyList<ResourceUse> uses = null)
        {
            Entries = entries;
            MergedSources = mergedSources;
            RootKind = rootKind;
            RootName = rootName ?? string.Empty;
            Uses = uses ?? NoUses;
        }

        public IReadOnlyList<ResourceEntry> Entries { get; }

        /// <summary>
        /// The Source values in ResourceDictionary.MergedDictionaries of the root dictionary, in file order,
        /// unresolved ("Resources/Brushes.xaml", "/MyApp;component/Themes/Dark.xaml").
        /// </summary>
        public IReadOnlyList<string> MergedSources { get; }

        public XamlRootKind RootKind { get; }

        /// <summary>Local name of the root element: "Window", "UserControl", "ResourceDictionary" ...</summary>
        public string RootName { get; }

        /// <summary>Every resource reference in the file, in file order.</summary>
        public IReadOnlyList<ResourceUse> Uses { get; }
    }
}
