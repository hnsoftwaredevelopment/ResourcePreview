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

    /// <summary>The parts of one .xaml file that the index needs: its keys and what it merges.</summary>
    internal sealed class XamlFileModel
    {
        public XamlFileModel(IReadOnlyList<ResourceEntry> entries, IReadOnlyList<string> mergedSources, XamlRootKind rootKind)
        {
            Entries = entries;
            MergedSources = mergedSources;
            RootKind = rootKind;
        }

        public IReadOnlyList<ResourceEntry> Entries { get; }

        /// <summary>
        /// The Source values in ResourceDictionary.MergedDictionaries of the root dictionary, in file order,
        /// unresolved ("Resources/Brushes.xaml", "/MyApp;component/Themes/Dark.xaml").
        /// </summary>
        public IReadOnlyList<string> MergedSources { get; }

        public XamlRootKind RootKind { get; }
    }
}
