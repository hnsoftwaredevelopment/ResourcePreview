using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>The XAML Resources window: which resources, their notes, grouping and search.</summary>
    [TestClass]
    public class ResourceCatalogTests
    {
        private static string App(params string[] sources) =>
            $"<Application x:Class=\"T.App\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Application.Resources><ResourceDictionary>" +
            "<ResourceDictionary.MergedDictionaries>" + string.Concat(sources.Select(s => $"<ResourceDictionary Source=\"{s}\" />")) +
            "</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>";

        private static string Brush(string key, string color = "Red") => $"<SolidColorBrush x:Key=\"{key}\" Color=\"{color}\" />\n";

        /// <summary>A.xaml and B.xaml merged by App.xaml (B later, so B wins), Lost.xaml merged nowhere, and a window.</summary>
        private static string Setup(TempSolution solution)
        {
            solution.Write("App.csproj", "<Project />");
            solution.Write("App.xaml", App("A.xaml", "B.xaml"));
            solution.Write("A.xaml", Xaml.Dictionary(Brush("Shared") + Brush("OnlyA") + "<Style x:Key=\"S\" TargetType=\"Button\"><Style.Resources>" + Brush("Nested") + "</Style.Resources></Style>"));
            solution.Write("B.xaml", Xaml.Dictionary(Brush("Shared", "Blue") + "<Color x:Key=\"C\">Red</Color>"));
            solution.Write("Lost.xaml", Xaml.Dictionary(Brush("Lost")));
            return solution.Write("MainWindow.xaml",
                $"<Window x:Class=\"T.MainWindow\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Window.Resources>{Brush("Own")}</Window.Resources>" +
                "<Grid Background=\"{StaticResource Shared}\" Tag=\"{StaticResource S}\" /></Window>");
        }

        private static IReadOnlyList<CatalogItem> Build(string file, bool activeOnly)
        {
            ResourceIndex index = ResourceIndex.ForFile(file);
            return ResourceCatalog.Build(
                index.GetMergeGraph(CancellationToken.None),
                activeOnly ? file : null,
                OverriddenKeyScanner.Scan(index, CancellationToken.None),
                UnusedResourceScanner.Scan(index, CancellationToken.None));
        }

        private static string Describe(CatalogItem item) => Path.GetFileNameWithoutExtension(item.Entry.FilePath) + "." + item.Key;

        [TestMethod]
        public void Solution_AllMainDictionaryKeys_WithNotes()
        {
            using (var solution = new TempSolution())
            {
                IReadOnlyList<CatalogItem> items = Build(Setup(solution), activeOnly: false);

                CollectionAssert.AreEquivalent(
                    new[] { "A.Shared", "A.OnlyA", "A.S", "B.Shared", "B.C", "Lost.Lost", "MainWindow.Own" },
                    items.Select(Describe).ToArray());

                CatalogItem overridden = items.Single(i => Describe(i) == "A.Shared");
                Assert.IsTrue(overridden.Flags.HasFlag(CatalogFlags.Overridden));
                Assert.AreEqual("B.xaml", Path.GetFileName(overridden.OverriddenBy.FilePath));

                Assert.IsTrue(items.Single(i => i.Key == "Lost").Flags.HasFlag(CatalogFlags.NotLoaded));
                Assert.IsTrue(items.Single(i => i.Key == "OnlyA").Flags.HasFlag(CatalogFlags.Unused));
                Assert.AreEqual(CatalogFlags.None, items.Single(i => Describe(i) == "B.Shared").Flags);
                Assert.AreEqual(CatalogFlags.None, items.Single(i => i.Key == "S").Flags);
            }
        }

        [TestMethod]
        public void ActiveFile_OneDefinitionPerKey_TheOneWpfUses()
        {
            using (var solution = new TempSolution())
            {
                IReadOnlyList<CatalogItem> items = Build(Setup(solution), activeOnly: true);

                CollectionAssert.AreEquivalent(
                    new[] { "MainWindow.Own", "B.Shared", "B.C", "A.OnlyA", "A.S" },
                    items.Select(Describe).ToArray());
            }
        }

        [TestMethod]
        public void GroupByFile_LookupOrderFromApp_ThenTheRest()
        {
            using (var solution = new TempSolution())
            {
                string main = Setup(solution);
                IReadOnlyList<CatalogItem> items = Build(main, activeOnly: false);
                MergeGraph graph = ResourceIndex.ForFile(main).GetMergeGraph(CancellationToken.None);

                IReadOnlyList<CatalogGroup> groups = ResourceCatalog.GroupByFile(items, graph, null);

                CollectionAssert.AreEqual(
                    new[] { "B.xaml", "A.xaml", "Lost.xaml", "MainWindow.xaml" },
                    groups.Select(g => Path.GetFileName(g.FilePath)).ToArray());
                CollectionAssert.AreEqual(new[] { "Shared", "OnlyA", "S" }, groups[1].Items.Select(i => i.Key).ToArray());
            }
        }

        [TestMethod]
        public void GroupByType_FixedOrder_KeysSorted()
        {
            using (var solution = new TempSolution())
            {
                IReadOnlyList<CatalogGroup> groups = ResourceCatalog.GroupByType(Build(Setup(solution), activeOnly: false));

                CollectionAssert.AreEqual(
                    new ResourceCategory?[] { ResourceCategory.Brushes, ResourceCategory.Colors, ResourceCategory.Styles },
                    groups.Select(g => g.Category).ToArray());
                CollectionAssert.AreEqual(new[] { "Lost", "OnlyA", "Own", "Shared", "Shared" }, groups[0].Items.Select(i => i.Key).ToArray());
            }
        }

        [TestMethod]
        public void Filter_KeyContains_IgnoresCase()
        {
            using (var solution = new TempSolution())
            {
                IReadOnlyList<CatalogItem> items = Build(Setup(solution), activeOnly: false);

                CollectionAssert.AreEquivalent(new[] { "Shared", "Shared" }, ResourceCatalog.Filter(items, " SHAR ").Select(i => i.Key).ToArray());
                Assert.AreEqual(items.Count, ResourceCatalog.Filter(items, "").Count());
            }
        }

        [TestMethod]
        [DataRow("SolidColorBrush", "Brushes")]
        [DataRow("LinearGradientBrush", "Brushes")]
        [DataRow("Color", "Colors")]
        [DataRow("StreamGeometry", "Images")]
        [DataRow("GeometryGroup", "Images")]
        [DataRow("DrawingImage", "Images")]
        [DataRow("BitmapImage", "Images")]
        [DataRow("Style", "Styles")]
        [DataRow("ControlTemplate", "Templates")]
        [DataRow("ItemsPanelTemplate", "Templates")]
        [DataRow("Thickness", "Values")]
        [DataRow("Double", "Values")]
        public void CategoryOf_ByElementName(string elementType, string expected)
        {
            // A string, because a public test method cannot take the internal enum.
            Assert.AreEqual(expected, ResourceCatalog.CategoryOf(elementType).ToString());
        }

        [TestMethod]
        public void Sample_KnownNotes()
        {
            string main = Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            IReadOnlyList<CatalogItem> items = Build(main, activeOnly: false);

            Assert.IsTrue(items.Single(i => i.Key == "InfoBrush" && i.Entry.FilePath.EndsWith("Brushes.xaml")).Flags.HasFlag(CatalogFlags.Overridden));
            Assert.IsTrue(items.Single(i => i.Key == "UnusedBrush").Flags.HasFlag(CatalogFlags.Unused));
            Assert.IsTrue(items.Single(i => i.Key == "LegacyArrow").Flags.HasFlag(CatalogFlags.NotLoaded));
            Assert.AreEqual(CatalogFlags.None, items.Single(i => i.Key == "AccentBrush").Flags);
        }
    }
}
