using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>References WPF will not resolve: missing keys, keys defined further down, keys in a file not loaded there.</summary>
    [TestClass]
    public class ReferenceCheckerTests
    {
        private static string App(params string[] sources)
        {
            string merged = string.Concat(sources.Select(s => $"<ResourceDictionary Source=\"{s}\" />"));
            return $"<Application x:Class=\"T.App\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Application.Resources><ResourceDictionary>" +
                   $"<ResourceDictionary.MergedDictionaries>{merged}</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>";
        }

        private static string Window(string body, string root = "Window") =>
            $"<{root} x:Class=\"T.W\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\">\n{body}\n</{root}>";

        private static string Brush(string key) => $"<SolidColorBrush x:Key=\"{key}\" Color=\"Red\" />";

        private static string UsesBrush(string key, string kind = "StaticResource") => $"<Border Background=\"{{{kind} {key}}}\" />";

        private static IReadOnlyList<ReferenceProblem> Check(string anyFile) =>
            ReferenceChecker.CheckAll(ResourceIndex.ForFile(anyFile), CancellationToken.None);

        /// <summary>A solution with an App.xaml that merges Brushes.xaml, which defines AccentBrush.</summary>
        private static TempSolution AppSolution()
        {
            var solution = new TempSolution();
            solution.Write("App.csproj", "<Project />");
            solution.Write("App.xaml", App("Brushes.xaml"));
            solution.Write("Brushes.xaml", Xaml.Dictionary(Brush("AccentBrush")));
            return solution;
        }

        // --- Finding references ---

        [TestMethod]
        public void FindUses_AllFormsWithLineAndColumn()
        {
            string text =
                "<Grid>\n" +
                "  <Border Background=\"{StaticResource A}\" BorderBrush=\"{DynamicResource ResourceKey=B}\" />\n" +
                "  <!-- {StaticResource InComment} -->\n" +
                "  <StaticResource ResourceKey=\"C\" />\n" +
                "  <Border Background=\"{StaticResource {x:Static SystemColors.ControlBrushKey}}\" />\n" +
                "</Grid>";

            List<ResourceUse> uses = ResourceIndex.FindUses(text);

            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, uses.Select(u => u.Key).ToArray());
            CollectionAssert.AreEqual(new[] { true, false, true }, uses.Select(u => u.IsStatic).ToArray());
            Assert.AreEqual(2, uses[0].Line);
            Assert.AreEqual(text.Split('\n')[1].IndexOf("A}") + 1, uses[0].Column);
            Assert.AreEqual(4, uses[2].Line);
        }

        [TestMethod]
        public void FindUses_MultiLineComment_KeepsLineNumbers()
        {
            List<ResourceUse> uses = ResourceIndex.FindUses("<!--\n{StaticResource X}\n-->\n<Border Background=\"{StaticResource Y}\" />");
            Assert.AreEqual("Y", uses.Single().Key);
            Assert.AreEqual(4, uses.Single().Line);
        }

        // --- Missing ---

        [TestMethod]
        public void Missing_StaticAndDynamic()
        {
            using (TempSolution solution = AppSolution())
            {
                string main = solution.Write("MainWindow.xaml", Window(UsesBrush("AccentBrush") + UsesBrush("Nope") + UsesBrush("AlsoNope", "DynamicResource")));

                var problems = Check(main);

                CollectionAssert.AreEqual(new[] { "Nope", "AlsoNope" }, problems.Select(p => p.Use.Key).ToArray());
                Assert.IsTrue(problems.All(p => p.Kind == ReferenceProblemKind.Missing));
                Assert.IsTrue(problems[0].Use.IsStatic);
                Assert.IsFalse(problems[1].Use.IsStatic);
            }
        }

        [TestMethod]
        public void Missing_OtherCase_SuggestsTheRealKey()
        {
            using (TempSolution solution = AppSolution())
            {
                string main = solution.Write("MainWindow.xaml", Window(UsesBrush("accentbrush")));
                Assert.AreEqual("AccentBrush", Check(main).Single().Suggestion);
            }
        }

        [TestMethod]
        public void Missing_NotReported_WhenADictionaryComesFromOutsideTheSolution()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml", App("pack://application:,,,/MahApps.Metro;component/Styles/Controls.xaml"));
                string main = solution.Write("MainWindow.xaml", Window(UsesBrush("MahApps.Brushes.Accent")));

                Assert.AreEqual(0, Check(main).Count);
            }
        }

        [TestMethod]
        public void Missing_NotReported_WithoutAnApp()
        {
            // A class library: the app that uses it may define the key.
            using (var solution = new TempSolution())
            {
                solution.Write("Lib.csproj", "<Project />");
                string styles = solution.Write("Styles.xaml", Xaml.Dictionary("<Style x:Key=\"S\" TargetType=\"Border\"><Setter Property=\"Background\" Value=\"{StaticResource FromTheApp}\" /></Style>"));

                Assert.AreEqual(0, Check(styles).Count);
            }
        }

        // --- Defined later ---

        [TestMethod]
        public void DefinedLater_InTheSameDictionary()
        {
            using (TempSolution solution = AppSolution())
            {
                string styles = solution.Write("Styles.xaml", Xaml.Dictionary(
                    "<Style x:Key=\"S\" TargetType=\"Border\">\n<Setter Property=\"Background\" Value=\"{StaticResource Late}\" />\n</Style>\n" +
                    Brush("Late") + "\n" +
                    "<Style x:Key=\"T\" TargetType=\"Border\"><Setter Property=\"Background\" Value=\"{StaticResource Late}\" /></Style>"));

                ReferenceProblem problem = Check(styles).Single();
                Assert.AreEqual(ReferenceProblemKind.DefinedLater, problem.Kind);
                Assert.AreEqual(3, problem.Use.Line);
                Assert.AreEqual(5, problem.Definition.LineNumber);
            }
        }

        [TestMethod]
        public void DefinedLater_NotReported_WhenAnotherLoadedFileHasIt()
        {
            using (TempSolution solution = AppSolution())
            {
                // AccentBrush is also in Brushes.xaml, which App.xaml merges: WPF finds that one.
                string main = solution.Write("MainWindow.xaml", Window(UsesBrush("AccentBrush") + "\n<Window.Resources>" + Brush("AccentBrush") + "</Window.Resources>"));
                Assert.AreEqual(0, Check(main).Count);
            }
        }

        [TestMethod]
        public void DefinedLater_NotReported_ForDynamicResource()
        {
            using (TempSolution solution = AppSolution())
            {
                string main = solution.Write("MainWindow.xaml", Window(UsesBrush("Late", "DynamicResource") + "\n<Window.Resources>" + Brush("Late") + "</Window.Resources>"));
                Assert.AreEqual(0, Check(main).Count);
            }
        }

        // --- Not loaded ---

        [TestMethod]
        public void NotLoaded_KeyOnlyInAnUnmergedDictionary()
        {
            using (TempSolution solution = AppSolution())
            {
                solution.Write("Legacy.xaml", Xaml.Dictionary(Brush("Old")));
                string main = solution.Write("MainWindow.xaml", Window(UsesBrush("Old")));

                ReferenceProblem problem = Check(main).Single();
                Assert.AreEqual(ReferenceProblemKind.NotLoaded, problem.Kind);
                Assert.AreEqual("Legacy.xaml", Path.GetFileName(problem.Definition.FilePath));
            }
        }

        [TestMethod]
        public void NotLoaded_KeyInAnotherWindow()
        {
            using (TempSolution solution = AppSolution())
            {
                solution.Write("OtherWindow.xaml", Window("<Window.Resources>" + Brush("Local") + "</Window.Resources>"));
                string main = solution.Write("MainWindow.xaml", Window(UsesBrush("Local")));

                Assert.AreEqual(ReferenceProblemKind.NotLoaded, Check(main).Single(p => p.FilePath == main).Kind);
            }
        }

        [TestMethod]
        public void NotLoaded_NotReported_ForUserControl()
        {
            // At runtime a UserControl also sees the resources of the window it is placed in.
            using (TempSolution solution = AppSolution())
            {
                solution.Write("MainWindow.xaml", Window("<Window.Resources>" + Brush("FromWindow") + "</Window.Resources>"));
                string control = solution.Write("Card.xaml", Window(UsesBrush("FromWindow"), "UserControl"));

                Assert.AreEqual(0, Check(control).Count(p => p.FilePath == control));
            }
        }

        [TestMethod]
        public void UnsavedChanges_AreChecked()
        {
            using (TempSolution solution = AppSolution())
            {
                string main = solution.Write("MainWindow.xaml", Window(UsesBrush("AccentBrush")));
                ResourceIndex index = ResourceIndex.ForFile(main);
                Assert.AreEqual(0, ReferenceChecker.CheckAll(index, CancellationToken.None).Count);

                index.SetLiveText(main, Window(UsesBrush("Typo")));
                Assert.AreEqual("Typo", ReferenceChecker.CheckAll(index, CancellationToken.None).Single().Use.Key);
            }
        }

        // --- The sample app ---

        [TestMethod]
        public void Sample_FindsExactlyTheKnownProblems()
        {
            string main = Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            var found = Check(main)
                .Select(p => $"{Path.GetFileName(p.FilePath)} {p.Use.Key} {p.Kind}")
                .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase)
                .ToArray();

            CollectionAssert.AreEqual(
                new[]
                {
                    "MainWindow.xaml DoesNotExist Missing",
                    "ReferenceProblems.xaml accentBrush Missing",
                    "ReferenceProblems.xaml LateBrush DefinedLater",
                    "ReferenceProblems.xaml LegacyArrow NotLoaded",
                    "ReferenceProblems.xaml ThemeBackgroundFromCode Missing",
                },
                found,
                string.Join("\n", found));
        }
    }
}
