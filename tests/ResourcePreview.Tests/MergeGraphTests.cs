using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>MergedDictionaries: reading them, resolving Source, the WPF lookup order and overridden keys.</summary>
    [TestClass]
    public class MergeGraphTests
    {
        private static string App(params string[] sources) => AppWithOwn(string.Empty, sources);

        private static string AppWithOwn(string ownResources, params string[] sources)
        {
            string merged = string.Concat(sources.Select(s => $"<ResourceDictionary Source=\"{s}\" />"));
            return $"<Application x:Class=\"T.App\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Application.Resources><ResourceDictionary>" +
                   $"<ResourceDictionary.MergedDictionaries>{merged}</ResourceDictionary.MergedDictionaries>{ownResources}" +
                   "</ResourceDictionary></Application.Resources></Application>";
        }

        private static string Brush(string key, string color = "Red") => $"<SolidColorBrush x:Key=\"{key}\" Color=\"{color}\" />";

        private static string Window(string resources = "") =>
            $"<Window x:Class=\"T.MainWindow\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Window.Resources>{resources}</Window.Resources><Grid /></Window>";

        private static string[] Files(IReadOnlyList<ResourceDefinition> list) => list.Select(d => Path.GetFileName(d.Entry.FilePath)).ToArray();

        private static IReadOnlyList<ResourceDefinition> Resolve(string file, string key) =>
            ResourceIndex.ForFile(file).Resolve(key, file, CancellationToken.None);

        // --- Parsing ---

        [TestMethod]
        public void ParseModel_App_ReadsKindAndMergedSourcesInOrder()
        {
            XamlFileModel model = ResourceIndex.ParseModel("App.xaml", AppWithOwn(Brush("Own"), "A.xaml", "/Themes/B.xaml"));

            Assert.AreEqual(XamlRootKind.Application, model.RootKind);
            CollectionAssert.AreEqual(new[] { "A.xaml", "/Themes/B.xaml" }, model.MergedSources.ToArray());
            Assert.IsTrue(model.Entries.Single().IsRootScope);
        }

        [TestMethod]
        public void ParseModel_NestedScopes_AreNotRootScope()
        {
            string text = Xaml.Dictionary(
                Brush("Top") +
                "<Style x:Key=\"S\" TargetType=\"Button\"><Style.Resources>" + Brush("InStyle") + "</Style.Resources></Style>");
            var entries = ResourceIndex.ParseModel("D.xaml", text).Entries;

            Assert.IsTrue(entries.Single(e => e.Key == "Top").IsRootScope);
            Assert.IsTrue(entries.Single(e => e.Key == "S").IsRootScope);
            Assert.IsFalse(entries.Single(e => e.Key == "InStyle").IsRootScope);
        }

        [TestMethod]
        public void ParseModel_WindowResources_DirectChildrenAreRootScope_GridResourcesAreNot()
        {
            string text = $"<Window xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Window.Resources>{Brush("W")}</Window.Resources>" +
                          $"<Grid><Grid.Resources>{Brush("G")}</Grid.Resources></Grid></Window>";
            XamlFileModel model = ResourceIndex.ParseModel("MainWindow.xaml", text);

            Assert.AreEqual(XamlRootKind.Other, model.RootKind);
            Assert.IsTrue(model.Entries.Single(e => e.Key == "W").IsRootScope);
            Assert.IsFalse(model.Entries.Single(e => e.Key == "G").IsRootScope);
        }

        [TestMethod]
        public void ParseModel_SourceOnWindowDictionary_CountsAsMerged()
        {
            string text = $"<Window xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Window.Resources><ResourceDictionary Source=\"Local.xaml\" /></Window.Resources></Window>";
            CollectionAssert.AreEqual(new[] { "Local.xaml" }, ResourceIndex.ParseModel("W.xaml", text).MergedSources.ToArray());
        }

        // --- Source paths ---

        [TestMethod]
        public void ResolveSource_AllForms()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App/App.csproj", "<Project />");
                solution.Write("Styles/MyApp.Styles.csproj", "<Project />");
                string from = solution.Write("App/Views/Main.xaml", Window());
                ResourceIndex index = ResourceIndex.ForFile(from);
                index.Find("x", from, CancellationToken.None); // makes the index find the projects

                string app = Path.Combine(solution.Root, "App");
                string styles = Path.Combine(solution.Root, "Styles");
                Assert.AreEqual(Path.Combine(app, "Views", "Local.xaml"), index.ResolveSource("Local.xaml", from));
                Assert.AreEqual(Path.Combine(app, "Themes", "Dark.xaml"), index.ResolveSource("../Themes/Dark.xaml", from));
                Assert.AreEqual(Path.Combine(app, "Themes", "Dark.xaml"), index.ResolveSource("/Themes/Dark.xaml", from));
                Assert.AreEqual(Path.Combine(app, "Themes", "Dark.xaml"), index.ResolveSource("pack://application:,,,/Themes/Dark.xaml", from));
                Assert.AreEqual(Path.Combine(styles, "Themes", "Dark.xaml"), index.ResolveSource("/MyApp.Styles;component/Themes/Dark.xaml", from));
                Assert.AreEqual(Path.Combine(styles, "Themes", "Dark.xaml"), index.ResolveSource("pack://application:,,,/MyApp.Styles;component/Themes/Dark.xaml", from));
                Assert.AreEqual(Path.Combine(app, "Themes", "My Dark.xaml"), index.ResolveSource("/Themes/My%20Dark.xaml", from));
                Assert.IsNull(index.ResolveSource("pack://siteoforigin:,,,/Dark.xaml", from));
                Assert.IsNull(index.ResolveSource("", from));
            }
        }

        // --- Lookup order ---

        [TestMethod]
        public void LastMergedDictionary_Wins()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("A.xaml", "B.xaml"));
                solution.Write("A.xaml", Xaml.Dictionary(Brush("K")));
                solution.Write("B.xaml", Xaml.Dictionary(Brush("K")));
                string main = solution.Write("MainWindow.xaml", Window());

                var result = Resolve(main, "K");

                CollectionAssert.AreEqual(new[] { "B.xaml", "A.xaml" }, Files(result));
                Assert.AreEqual(DefinitionStatus.Used, result[0].Status);
                Assert.AreEqual(DefinitionStatus.Overridden, result[1].Status);
                Assert.AreEqual("B.xaml", Path.GetFileName(result[1].OverriddenBy.FilePath));
                Assert.IsFalse(result.Any(d => d.IsInUnmergedFile));
            }
        }

        [TestMethod]
        public void OwnKeys_WinOverMergedDictionaries()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", AppWithOwn(Brush("K"), "A.xaml"));
                solution.Write("A.xaml", Xaml.Dictionary(Brush("K")));
                string main = solution.Write("MainWindow.xaml", Window());

                CollectionAssert.AreEqual(new[] { "App.xaml", "A.xaml" }, Files(Resolve(main, "K")));
            }
        }

        [TestMethod]
        public void NestedMerges_FollowTheLastMergedFirst()
        {
            // App merges A, then B. B merges C. Lookup: B, C, A.
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("A.xaml", "B.xaml"));
                solution.Write("A.xaml", Xaml.Dictionary(Brush("K")));
                solution.Write("B.xaml", Xaml.Dictionary("<ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"C.xaml\" /></ResourceDictionary.MergedDictionaries>"));
                solution.Write("C.xaml", Xaml.Dictionary(Brush("K")));
                string main = solution.Write("MainWindow.xaml", Window());

                CollectionAssert.AreEqual(new[] { "C.xaml", "A.xaml" }, Files(Resolve(main, "K")));
            }
        }

        [TestMethod]
        public void CurrentFile_WinsOverApp()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("A.xaml"));
                solution.Write("A.xaml", Xaml.Dictionary(Brush("K")));
                string main = solution.Write("MainWindow.xaml", Window(Brush("K")));

                var result = Resolve(main, "K");
                CollectionAssert.AreEqual(new[] { "MainWindow.xaml", "A.xaml" }, Files(result));
                Assert.AreEqual(DefinitionStatus.Overridden, result[1].Status);
            }
        }

        [TestMethod]
        public void UnmergedDictionary_IsMarkedAndSortedLast()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("A.xaml"));
                solution.Write("A.xaml", Xaml.Dictionary(Brush("K")));
                solution.Write("Aaa.Legacy.xaml", Xaml.Dictionary(Brush("K")));
                string main = solution.Write("MainWindow.xaml", Window());

                var result = Resolve(main, "K");
                CollectionAssert.AreEqual(new[] { "A.xaml", "Aaa.Legacy.xaml" }, Files(result));
                Assert.AreEqual(DefinitionStatus.Unknown, result[1].Status);
                Assert.IsTrue(result[1].IsInUnmergedFile);
                Assert.IsFalse(result[0].IsInUnmergedFile);
            }
        }

        [TestMethod]
        public void OtherWindowResources_AreUnknown_NotOverridden()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("A.xaml"));
                solution.Write("A.xaml", Xaml.Dictionary(Brush("K")));
                solution.Write("OtherWindow.xaml", Window(Brush("K")));
                string main = solution.Write("MainWindow.xaml", Window());

                var result = Resolve(main, "K");
                CollectionAssert.AreEqual(new[] { "A.xaml", "OtherWindow.xaml" }, Files(result));
                Assert.AreEqual(DefinitionStatus.Unknown, result[1].Status);
                Assert.IsFalse(result[1].IsInUnmergedFile, "a window is loaded by itself");
            }
        }

        [TestMethod]
        public void NoApplication_KeepsCurrentFileFirstThenAlphabetical()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("B.xaml", Xaml.Dictionary(Brush("K")));
                solution.Write("A.xaml", Xaml.Dictionary(Brush("K")));
                string current = solution.Write("C.xaml", Xaml.Dictionary(Brush("K")));

                CollectionAssert.AreEqual(new[] { "C.xaml", "A.xaml", "B.xaml" }, Files(Resolve(current, "K")));
            }
        }

        [TestMethod]
        public void MergeCycle_DoesNotLoop()
        {
            using (var solution = new TempSolution())
            {
                const string MergeOther = "<ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"{0}\" /></ResourceDictionary.MergedDictionaries>";
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("A.xaml"));
                solution.Write("A.xaml", Xaml.Dictionary(string.Format(MergeOther, "B.xaml") + Brush("K")));
                solution.Write("B.xaml", Xaml.Dictionary(string.Format(MergeOther, "A.xaml") + Brush("K")));
                string main = solution.Write("MainWindow.xaml", Window());

                CollectionAssert.AreEqual(new[] { "A.xaml", "B.xaml" }, Files(Resolve(main, "K")));
                Assert.AreEqual(1, OverriddenKeyScanner.Scan(ResourceIndex.ForFile(main), CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void ThemeFiles_AreNotReportedAsUnmerged()
        {
            Assert.IsTrue(MergeGraph.IsThemeFile(Path.Combine("C:\\", "Lib", "Themes", "Generic.xaml")));
            Assert.IsTrue(MergeGraph.IsThemeFile(Path.Combine("C:\\", "Lib", "Themes", "Aero2.NormalColor.xaml")));
            Assert.IsFalse(MergeGraph.IsThemeFile(Path.Combine("C:\\", "Lib", "Themes", "Dark.xaml")));
            Assert.IsFalse(MergeGraph.IsThemeFile(Path.Combine("C:\\", "Lib", "Generic.xaml")));
        }

        // --- Error List scan ---

        [TestMethod]
        public void Scan_ReportsOverriddenDefinition()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("A.xaml", "B.xaml"));
                solution.Write("A.xaml", Xaml.Dictionary(Brush("K") + Brush("OnlyInA")));
                solution.Write("B.xaml", Xaml.Dictionary(Brush("K")));
                string main = solution.Write("MainWindow.xaml", Window());

                OverriddenKey found = OverriddenKeyScanner.Scan(ResourceIndex.ForFile(main), CancellationToken.None).Single();
                Assert.AreEqual("K", found.Definition.Key);
                Assert.AreEqual("A.xaml", Path.GetFileName(found.Definition.FilePath));
                Assert.AreEqual("B.xaml", Path.GetFileName(found.UsedInstead.FilePath));
                Assert.AreEqual("App.xaml", Path.GetFileName(found.LoadedThrough));
            }
        }

        [TestMethod]
        public void Scan_SameDictionaryMergedTwice_IsNotADuplicate()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("Colors.xaml", "Styles.xaml"));
                solution.Write("Colors.xaml", Xaml.Dictionary(Brush("K")));
                solution.Write("Styles.xaml", Xaml.Dictionary("<ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"Colors.xaml\" /></ResourceDictionary.MergedDictionaries>"));
                string main = solution.Write("MainWindow.xaml", Window());

                Assert.AreEqual(0, OverriddenKeyScanner.Scan(ResourceIndex.ForFile(main), CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void Scan_IgnoresWindowOverridingApp_AndNestedScopes()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("A.xaml"));
                solution.Write("A.xaml", Xaml.Dictionary(Brush("K") + "<Style x:Key=\"S\" TargetType=\"Button\"><Style.Resources>" + Brush("K") + "</Style.Resources></Style>"));
                string main = solution.Write("MainWindow.xaml", Window(Brush("K")));

                Assert.AreEqual(0, OverriddenKeyScanner.Scan(ResourceIndex.ForFile(main), CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void Scan_WindowMergingTwoDictionaries_IsReported()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("A.xaml", Xaml.Dictionary(Brush("K")));
                solution.Write("B.xaml", Xaml.Dictionary(Brush("K")));
                string main = solution.Write("MainWindow.xaml",
                    $"<Window xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Window.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                    "<ResourceDictionary Source=\"A.xaml\" /><ResourceDictionary Source=\"B.xaml\" /></ResourceDictionary.MergedDictionaries></ResourceDictionary></Window.Resources></Window>");

                OverriddenKey found = OverriddenKeyScanner.Scan(ResourceIndex.ForFile(main), CancellationToken.None).Single();
                Assert.AreEqual("A.xaml", Path.GetFileName(found.Definition.FilePath));
                Assert.AreEqual("MainWindow.xaml", Path.GetFileName(found.LoadedThrough));
            }
        }

        [TestMethod]
        public void Scan_SeesUnsavedChanges()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("A.xaml", "B.xaml"));
                solution.Write("A.xaml", Xaml.Dictionary(Brush("K")));
                string b = solution.Write("B.xaml", Xaml.Dictionary(Brush("Other")));
                ResourceIndex index = ResourceIndex.ForFile(b);
                Assert.AreEqual(0, OverriddenKeyScanner.Scan(index, CancellationToken.None).Count);

                index.SetLiveText(b, Xaml.Dictionary(Brush("K")));
                Assert.AreEqual(1, OverriddenKeyScanner.Scan(index, CancellationToken.None).Count);

                index.ClearLiveText(b);
                Assert.AreEqual(0, OverriddenKeyScanner.Scan(index, CancellationToken.None).Count);
            }
        }

        // --- The sample app ---

        private static string MainWindow => Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");

        [TestMethod]
        public void Sample_InfoBrush_ValuesWinsOverBrushes()
        {
            var result = Resolve(MainWindow, "InfoBrush");
            CollectionAssert.AreEqual(new[] { "Values.xaml", "Brushes.xaml" }, Files(result));
            Assert.AreEqual(DefinitionStatus.Overridden, result[1].Status);
        }

        [TestMethod]
        public void Sample_Check_LegacyFileIsNotMerged()
        {
            var result = Resolve(MainWindow, "Check");
            CollectionAssert.AreEqual(new[] { "Icons.xaml", "IconsLegacy.xaml" }, Files(result));
            Assert.IsTrue(result[1].IsInUnmergedFile);
        }

        [TestMethod]
        public void Sample_Scan_FindsOnlyInfoBrush()
        {
            var found = OverriddenKeyScanner.Scan(ResourceIndex.ForFile(MainWindow), CancellationToken.None);
            Assert.AreEqual("InfoBrush", found.Single().Definition.Key);
            Assert.AreEqual("Brushes.xaml", Path.GetFileName(found.Single().Definition.FilePath));
        }
    }
}
