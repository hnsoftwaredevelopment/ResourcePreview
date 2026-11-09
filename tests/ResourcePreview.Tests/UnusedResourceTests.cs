using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>Resources that nothing uses, in XAML or in code.</summary>
    [TestClass]
    public class UnusedResourceTests
    {
        private static string App(params string[] sources)
        {
            string merged = string.Concat(sources.Select(s => $"<ResourceDictionary Source=\"{s}\" />"));
            return $"<Application x:Class=\"T.App\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Application.Resources><ResourceDictionary>" +
                   $"<ResourceDictionary.MergedDictionaries>{merged}</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>";
        }

        private static string Brush(string key) => $"<SolidColorBrush x:Key=\"{key}\" Color=\"Red\" />";

        private static string Window(string body) =>
            $"<Window x:Class=\"T.W\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\">\n{body}\n</Window>";

        private static string[] Unused(string anyFile) =>
            UnusedResourceScanner.Scan(ResourceIndex.ForFile(anyFile), CancellationToken.None).Select(e => e.Key).ToArray();

        [TestMethod]
        public void ReportsKeysNothingUses()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("Brushes.xaml"));
                solution.Write("Brushes.xaml", Xaml.Dictionary(Brush("Used") + Brush("Dynamic") + Brush("FromCode") + Brush("Nobody")));
                solution.Write("MainWindow.xaml.cs", "var b = FindResource(\"FromCode\"); var s = \"some other text\";");
                string main = solution.Write("MainWindow.xaml", Window(
                    "<Border Background=\"{StaticResource Used}\" BorderBrush=\"{DynamicResource Dynamic}\" />"));

                CollectionAssert.AreEqual(new[] { "Nobody" }, Unused(main));
            }
        }

        [TestMethod]
        public void UnusedKeyInWindowResources_IsReported()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App());
                string main = solution.Write("MainWindow.xaml", Window("<Window.Resources>" + Brush("Local") + "</Window.Resources>"));

                CollectionAssert.AreEqual(new[] { "Local" }, Unused(main));
            }
        }

        [TestMethod]
        public void NotReported_InUnmergedDictionaries_ThemeFiles_AndWithoutApp()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App());
                solution.Write("Unmerged.xaml", Xaml.Dictionary(Brush("A")));
                string generic = solution.Write("Themes/Generic.xaml", Xaml.Dictionary(Brush("B")));

                Assert.AreEqual(0, Unused(generic).Length);
            }

            using (var library = new TempSolution())
            {
                library.Write("Lib.csproj", "<Project />");
                library.Write("Styles.xaml", Xaml.Dictionary(Brush("C")));
                string window = library.Write("Window.xaml", Window("<Window.Resources>" + Brush("D") + "</Window.Resources>"));

                Assert.AreEqual(0, Unused(window).Length, "no App.xaml: the app that uses the library is elsewhere");
            }
        }

        [TestMethod]
        public void ThemeDictionaries_AreNotReported()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("Theme.xaml"));
                string theme = solution.Write("Theme.xaml", Xaml.Dictionary(
                    "<ResourceDictionary.ThemeDictionaries><ResourceDictionary x:Key=\"Dark\">" + Brush("Bg") + "</ResourceDictionary></ResourceDictionary.ThemeDictionaries>"));
                solution.Write("MainWindow.xaml", Window("<Border Background=\"{DynamicResource Bg}\" />"));

                Assert.AreEqual(0, Unused(theme).Length);
            }
        }

        [TestMethod]
        public void CodeStrings_ParseLiterals()
        {
            var strings = CodeStrings.Parse("Resources[\"Accent\"] = x; Say(\"with \\\"quote\\\"\"); Dim s = \"VbText\"");
            CollectionAssert.IsSubsetOf(new[] { "Accent", "VbText" }, strings.ToArray());
        }

        [TestMethod]
        public void Sample_OnlyUnusedBrush()
        {
            string main = Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            CollectionAssert.AreEqual(new[] { "UnusedBrush" }, Unused(main));
        }
    }
}
