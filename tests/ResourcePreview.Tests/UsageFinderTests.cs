using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>Find Resource References: every use of a key, and which definition each use gets.</summary>
    [TestClass]
    public class UsageFinderTests
    {
        private static string App(params string[] sources)
        {
            string merged = string.Concat(sources.Select(s => $"<ResourceDictionary Source=\"{s}\" />"));
            return $"<Application x:Class=\"T.App\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Application.Resources><ResourceDictionary>" +
                   $"<ResourceDictionary.MergedDictionaries>{merged}</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>";
        }

        private static string Window(string body) =>
            $"<Window x:Class=\"T.W\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\">\n{body}\n</Window>";

        [TestMethod]
        public void FindsAllUses_AndWhichDefinitionEachGets()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("A.xaml", "B.xaml"));
                string a = solution.Write("A.xaml", Xaml.Dictionary(
                    "<SolidColorBrush x:Key=\"K\" Color=\"Red\" />\n" +
                    "<Style x:Key=\"S\" TargetType=\"Border\"><Setter Property=\"Background\" Value=\"{StaticResource K}\" /></Style>"));
                solution.Write("B.xaml", Xaml.Dictionary("<SolidColorBrush x:Key=\"K\" Color=\"Blue\" />"));
                string main = solution.Write("MainWindow.xaml", Window(
                    "  <Border Background=\"{StaticResource K}\" />\n  <Border BorderBrush=\"{DynamicResource K}\" />\n  <Border Background=\"{StaticResource Other}\" />"));

                ResourceIndex index = ResourceIndex.ForFile(main);
                ResourceEntry inA = index.Find("K", a, CancellationToken.None).First(e => e.FilePath == a);
                UsageReport report = UsageFinder.Find(index, "K", inA, CancellationToken.None);

                Assert.AreEqual(2, report.Definitions.Count);
                CollectionAssert.AreEqual(
                    new[] { "A.xaml 3", "MainWindow.xaml 2", "MainWindow.xaml 3" },
                    report.Usages.Select(u => Path.GetFileName(u.FilePath) + " " + u.Use.Line).ToArray());

                // Inside A.xaml its own K comes first; in the window, B.xaml (merged last) wins.
                Assert.IsFalse(report.UsesOtherDefinition(report.Usages[0]));
                Assert.IsTrue(report.UsesOtherDefinition(report.Usages[1]));
                Assert.AreEqual("B.xaml", Path.GetFileName(report.Usages[1].ResolvesTo.FilePath));
                Assert.AreEqual("<Border Background=\"{StaticResource K}\" />", report.Usages[1].Use.LineText);
            }
        }

        [TestMethod]
        public void KeyWithoutDefinitions_StillListsItsUses()
        {
            using (var solution = new TempSolution())
            {
                string main = solution.Write("MainWindow.xaml", Window("<Border Background=\"{StaticResource Typo}\" />"));
                UsageReport report = UsageFinder.Find(ResourceIndex.ForFile(main), "Typo", null, CancellationToken.None);

                Assert.AreEqual(0, report.Definitions.Count);
                Assert.IsNull(report.Usages.Single().ResolvesTo);
                Assert.IsFalse(report.UsesOtherDefinition(report.Usages.Single()));
            }
        }

        [TestMethod]
        public void Sample_InfoBrush_FromTheOverriddenDefinition()
        {
            string main = Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            ResourceIndex index = ResourceIndex.ForFile(main);
            ResourceEntry inBrushes = index.Find("InfoBrush", main, CancellationToken.None).Single(e => Path.GetFileName(e.FilePath) == "Brushes.xaml");

            UsageReport report = UsageFinder.Find(index, "InfoBrush", inBrushes, CancellationToken.None);

            ResourceUsage usage = report.Usages.Single();
            Assert.AreEqual("MainWindow.xaml", Path.GetFileName(usage.FilePath));
            Assert.IsTrue(report.UsesOtherDefinition(usage), "Values.xaml is merged later, so the window gets that one");
        }
    }
}
