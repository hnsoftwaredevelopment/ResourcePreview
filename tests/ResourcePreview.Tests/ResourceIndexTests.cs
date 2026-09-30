using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    [TestClass]
    public class ResourceIndexParseTests
    {
        [TestMethod]
        public void ParseXaml_FindsAllKeyedElements()
        {
            string text = Xaml.Dictionary(
                "  <SolidColorBrush x:Key=\"A\" Color=\"Red\" />\n" +
                "  <Geometry x:Key=\"B\">M0,0 L1,1</Geometry>\n" +
                "  <Style TargetType=\"Button\" />");

            var entries = ResourceIndex.ParseXaml(@"C:\x\Icons.xaml", text);

            CollectionAssert.AreEqual(new[] { "A", "B" }, entries.Select(e => e.Key).ToArray());
        }

        [TestMethod]
        public void ParseXaml_LineAndColumnPointToXKey()
        {
            string text = Xaml.Dictionary("    <GeometryGroup x:Key=\"Renew\">\n      <Geometry>M0,0</Geometry>\n    </GeometryGroup>");

            ResourceEntry entry = ResourceIndex.ParseXaml("Icons.xaml", text).Single();

            // Line 1 is <ResourceDictionary ...>, so the GeometryGroup is on line 2.
            Assert.AreEqual(2, entry.LineNumber);
            // Four spaces + "<GeometryGroup " = 19 characters, so x:Key starts at column 20.
            Assert.AreEqual(20, entry.Column);
        }

        [TestMethod]
        public void ParseXaml_InvalidXml_ReturnsNull()
        {
            // null means "keep the previous version", which happens while the user is still typing.
            Assert.IsNull(ResourceIndex.ParseXaml("Icons.xaml", "<ResourceDictionary><Geometry x:Key=\"A\">"));
        }

        [TestMethod]
        public void ParseXaml_TypedKeys_AreSkipped()
        {
            string text = Xaml.Dictionary("<Style x:Key=\"{x:Type Button}\" TargetType=\"Button\" />");
            Assert.AreEqual(0, ResourceIndex.ParseXaml("Styles.xaml", text).Count);
        }

        [TestMethod]
        public void ParseXaml_RememberRootNamespaces()
        {
            string text = Xaml.Dictionary("<SolidColorBrush x:Key=\"A\" />", "xmlns:local=\"clr-namespace:MyApp\"");

            ResourceEntry entry = ResourceIndex.ParseXaml("Icons.xaml", text).Single();

            Assert.AreEqual("clr-namespace:MyApp", entry.Namespaces["local"]);
            Assert.AreEqual(Xaml.X, entry.Namespaces["x"]);
            Assert.AreEqual(Xaml.Presentation, entry.Namespaces[string.Empty]);
        }

        [TestMethod]
        public void ParseXaml_KeysInsideWindowResources_AreFound()
        {
            string text =
                $"<Window xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\">\n" +
                "  <Window.Resources><SolidColorBrush x:Key=\"LocalBrush\" Color=\"Red\" /></Window.Resources>\n" +
                "</Window>";

            Assert.AreEqual("LocalBrush", ResourceIndex.ParseXaml("MainWindow.xaml", text).Single().Key);
        }

        [TestMethod]
        public void ParseXaml_ElementTypeName_IsLocalName()
        {
            string text = Xaml.Dictionary("<DrawingImage x:Key=\"Report\" />");
            Assert.AreEqual("DrawingImage", ResourceIndex.ParseXaml("Icons.xaml", text).Single().ElementTypeName);
        }
    }

    [TestClass]
    public class ResourceIndexRootTests
    {
        [TestMethod]
        public void FindRootDirectory_SolutionInParent()
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("App/Resources/Icons.xaml", "<x />");
                Assert.AreEqual(solution.Root, ResourceIndex.FindRootDirectory(file));
            }
        }

        [TestMethod]
        public void FindRootDirectory_Slnx_AlsoCounts()
        {
            using (var solution = new TempSolution("Test.slnx"))
            {
                string file = solution.Write("App/Icons.xaml", "<x />");
                Assert.AreEqual(solution.Root, ResourceIndex.FindRootDirectory(file));
            }
        }

        [TestMethod]
        public void FindRootDirectory_NoSolution_GitRoot()
        {
            using (var solution = new TempSolution(solutionName: null))
            {
                Directory.CreateDirectory(Path.Combine(solution.Root, ".git"));
                string file = solution.Write("src/App/Icons.xaml", "<x />");
                Assert.AreEqual(solution.Root, ResourceIndex.FindRootDirectory(file));
            }
        }

        [TestMethod]
        public void FindRootDirectory_StopsAtGitRoot_EvenIfSolutionHigherUp()
        {
            using (var solution = new TempSolution())
            {
                string repo = Path.Combine(solution.Root, "repo");
                Directory.CreateDirectory(Path.Combine(repo, ".git"));
                string file = solution.Write("repo/App/Icons.xaml", "<x />");
                Assert.AreEqual(repo, ResourceIndex.FindRootDirectory(file));
            }
        }

        [TestMethod]
        public void FindRootDirectory_OnlyProject_ProjectFolder()
        {
            using (var solution = new TempSolution(solutionName: null))
            {
                solution.Write("App/App.csproj", "<Project />");
                string file = solution.Write("App/Resources/Icons.xaml", "<x />");
                Assert.AreEqual(Path.Combine(solution.Root, "App"), ResourceIndex.FindRootDirectory(file));
            }
        }

        [TestMethod]
        public void FindProjectDirectory_NearestProjectAboveFile()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App/App.csproj", "<Project />");
                string file = solution.Write("App/Resources/Icons.xaml", "<x />");
                Assert.AreEqual(Path.Combine(solution.Root, "App"), ResourceIndex.FindProjectDirectory(file));
            }
        }
    }

    [TestClass]
    public class ResourceIndexSearchTests
    {
        private const string Dict = "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">{0}</ResourceDictionary>";

        private static string D(string body) => string.Format(Dict, body);

        [TestMethod]
        public void Find_AcrossFiles()
        {
            using (var solution = new TempSolution())
            {
                string main = solution.Write("App/MainWindow.xaml", D(string.Empty));
                solution.Write("App/Resources/Brushes.xaml", D("<SolidColorBrush x:Key=\"Accent\" Color=\"Blue\" />"));

                var entries = ResourceIndex.ForFile(main).Find("Accent", main, CancellationToken.None);

                Assert.AreEqual(1, entries.Count);
                StringAssert.EndsWith(entries[0].FilePath, "Brushes.xaml");
            }
        }

        [TestMethod]
        public void Find_UnknownKey_Empty()
        {
            using (var solution = new TempSolution())
            {
                string main = solution.Write("App/MainWindow.xaml", D(string.Empty));
                Assert.AreEqual(0, ResourceIndex.ForFile(main).Find("DoesNotExist", main, CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void Find_IsCaseSensitive()
        {
            // XAML keys are case-sensitive: "accent" is a different resource than "Accent".
            using (var solution = new TempSolution())
            {
                string main = solution.Write("App/Brushes.xaml", D("<SolidColorBrush x:Key=\"Accent\" />"));
                Assert.AreEqual(0, ResourceIndex.ForFile(main).Find("accent", main, CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void Find_PreferredFileFirst_ThenAlphabetical()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App/A.xaml", D("<Geometry x:Key=\"Check\">M0,0</Geometry>"));
                string preferred = solution.Write("App/Z.xaml", D("<Geometry x:Key=\"Check\">M0,0</Geometry>"));
                solution.Write("App/M.xaml", D("<Geometry x:Key=\"Check\">M0,0</Geometry>"));

                var files = ResourceIndex.ForFile(preferred).Find("Check", preferred, CancellationToken.None)
                    .Select(e => Path.GetFileName(e.FilePath)).ToArray();

                CollectionAssert.AreEqual(new[] { "Z.xaml", "A.xaml", "M.xaml" }, files);
            }
        }

        [TestMethod]
        public void Find_SkipsBinAndObj()
        {
            using (var solution = new TempSolution())
            {
                string main = solution.Write("App/Icons.xaml", D("<Geometry x:Key=\"Check\">M0,0</Geometry>"));
                solution.Write("App/bin/Debug/Icons.xaml", D("<Geometry x:Key=\"Check\">M0,0</Geometry>"));
                solution.Write("App/obj/Icons.xaml", D("<Geometry x:Key=\"Check\">M0,0</Geometry>"));

                Assert.AreEqual(1, ResourceIndex.ForFile(main).Find("Check", main, CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void Find_InvalidFile_DoesNotBreakOthers()
        {
            using (var solution = new TempSolution())
            {
                string main = solution.Write("App/Icons.xaml", D("<Geometry x:Key=\"Check\">M0,0</Geometry>"));
                solution.Write("App/Broken.xaml", "<ResourceDictionary><Geometry x:Key=\"Oops\">");

                Assert.AreEqual(1, ResourceIndex.ForFile(main).Find("Check", main, CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void LiveText_OverridesFileOnDisk()
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("App/Icons.xaml", D("<Geometry x:Key=\"Old\">M0,0</Geometry>"));
                var index = ResourceIndex.ForFile(file);

                index.SetLiveText(file, D("<Geometry x:Key=\"New\">M0,0</Geometry>"));

                Assert.AreEqual(1, index.Find("New", file, CancellationToken.None).Count, "unsaved key must be found");
                Assert.AreEqual(0, index.Find("Old", file, CancellationToken.None).Count, "version on disk must be ignored");
            }
        }

        [TestMethod]
        public void LiveText_Invalid_KeepsPreviousVersion()
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("App/Icons.xaml", D(string.Empty));
                var index = ResourceIndex.ForFile(file);

                index.SetLiveText(file, D("<Geometry x:Key=\"Typed\">M0,0</Geometry>"));
                index.SetLiveText(file, D("<Geometry x:Key=\"Typed\">M0,0</Geometry><Geo"));

                Assert.AreEqual(1, index.Find("Typed", file, CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void ClearLiveText_FallsBackToDisk()
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("App/Icons.xaml", D("<Geometry x:Key=\"OnDisk\">M0,0</Geometry>"));
                var index = ResourceIndex.ForFile(file);
                index.SetLiveText(file, D(string.Empty));

                index.ClearLiveText(file);

                Assert.AreEqual(1, index.Find("OnDisk", file, CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void FileChangedOnDisk_IsPickedUp()
        {
            using (var solution = new TempSolution())
            {
                string main = solution.Write("App/MainWindow.xaml", D(string.Empty));
                string icons = solution.Write("App/Icons.xaml", D("<Geometry x:Key=\"First\">M0,0</Geometry>"));
                var index = ResourceIndex.ForFile(main);
                Assert.AreEqual(1, index.Find("First", main, CancellationToken.None).Count);

                File.WriteAllText(icons, D("<Geometry x:Key=\"Second\">M0,0</Geometry>"));

                // FileSystemWatcher reports the change asynchronously, so wait up to 5 seconds.
                var timer = Stopwatch.StartNew();
                while (index.Find("Second", main, CancellationToken.None).Count == 0 && timer.Elapsed.TotalSeconds < 5)
                {
                    Thread.Sleep(100);
                }

                Assert.AreEqual(1, index.Find("Second", main, CancellationToken.None).Count);
                Assert.AreEqual(0, index.Find("First", main, CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void FindProjectDirectoryByAssemblyName_UsesProjectFileName()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("Shared/MyCompany.Icons/MyCompany.Icons.csproj", "<Project />");
                string main = solution.Write("App/MainWindow.xaml", D(string.Empty));
                var index = ResourceIndex.ForFile(main);
                index.Find("anything", main, CancellationToken.None); // first scan

                Assert.AreEqual(Path.Combine(solution.Root, "Shared", "MyCompany.Icons"), index.FindProjectDirectoryByAssemblyName("MyCompany.Icons"));
                Assert.IsNull(index.FindProjectDirectoryByAssemblyName("Unknown"));
            }
        }
    }
}
