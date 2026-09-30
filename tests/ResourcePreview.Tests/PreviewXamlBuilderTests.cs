using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    [TestClass]
    public class PreviewXamlBuilderTests
    {
        private static readonly XNamespace P = Xaml.Presentation;
        private static readonly XNamespace X = Xaml.X;

        /// <summary>Writes the XAML to a temp solution and builds the preview for one key.</summary>
        private static PreviewXaml BuildFor(string key, params (string Path, string Body)[] files)
        {
            using (var solution = new TempSolution())
            {
                string first = null;
                foreach (var (path, body) in files)
                {
                    string written = solution.Write(path, Xaml.Dictionary(body, "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\" xmlns:local=\"clr-namespace:MyApp\""));
                    first = first ?? written;
                }

                var index = ResourceIndex.ForFile(first);
                ResourceEntry target = index.Find(key, first, CancellationToken.None).Single();
                return PreviewXamlBuilder.Build(index, target, CancellationToken.None);
            }
        }

        private static string[] KeysInOrder(PreviewXaml preview)
        {
            return XDocument.Parse(preview.Xaml).Root.Elements().Select(e => (string)e.Attribute(X + "Key")).ToArray();
        }

        // ---------- FindReferencedKeys ----------

        [TestMethod]
        public void FindReferencedKeys_AttributesElementSyntaxAndNesting()
        {
            var element = XElement.Parse(
                $"<DrawingGroup xmlns=\"{Xaml.Presentation}\">" +
                "  <GeometryDrawing Brush=\"{StaticResource A}\" Geometry=\"{DynamicResource ResourceKey=B}\" />" +
                "  <GeometryDrawing Brush=\"{StaticResource A}\"><GeometryDrawing.Pen><StaticResource ResourceKey=\"C\" /></GeometryDrawing.Pen></GeometryDrawing>" +
                "</DrawingGroup>");

            CollectionAssert.AreEquivalent(new[] { "A", "B", "C" }, PreviewXamlBuilder.FindReferencedKeys(element).ToArray());
        }

        [TestMethod]
        public void FindReferencedKeys_NoReferences_Empty()
        {
            var element = XElement.Parse($"<Geometry xmlns=\"{Xaml.Presentation}\">M0,0 L1,1</Geometry>");
            Assert.AreEqual(0, PreviewXamlBuilder.FindReferencedKeys(element).Count());
        }

        // ---------- Build: order and dependencies ----------

        [TestMethod]
        public void Build_NoDependencies_OnlyTarget()
        {
            var preview = BuildFor("Folder", ("Icons.xaml", "<Geometry x:Key=\"Folder\">M3,5 L21,19 Z</Geometry>"));
            CollectionAssert.AreEqual(new[] { "Folder" }, KeysInOrder(preview));
            Assert.AreEqual(0, preview.MissingKeys.Count);
        }

        [TestMethod]
        public void Build_ChainOfDependencies_DeepestFirst()
        {
            // Mirrors the sample app: Report -> ReportDrawingGroup -> ReportGeometry.
            var preview = BuildFor("Report", ("Icons.xaml",
                "<Geometry x:Key=\"ReportGeometry\">M0,0 L10,10 Z</Geometry>" +
                "<DrawingGroup x:Key=\"ReportDrawingGroup\"><GeometryDrawing Brush=\"Blue\" Geometry=\"{StaticResource ReportGeometry}\" /></DrawingGroup>" +
                "<DrawingImage x:Key=\"Report\" Drawing=\"{StaticResource ReportDrawingGroup}\" />"));

            CollectionAssert.AreEqual(new[] { "ReportGeometry", "ReportDrawingGroup", "Report" }, KeysInOrder(preview));
        }

        [TestMethod]
        public void Build_DependencyInOtherFile_IsIncluded()
        {
            var preview = BuildFor("Warning",
                ("Icons.xaml", "<DrawingImage x:Key=\"Warning\"><DrawingImage.Drawing><GeometryDrawing Brush=\"{StaticResource WarningBrush}\" Geometry=\"M0,0 L1,1 Z\" /></DrawingImage.Drawing></DrawingImage>"),
                ("Brushes.xaml", "<SolidColorBrush x:Key=\"WarningBrush\" Color=\"Orange\" />"));

            CollectionAssert.AreEqual(new[] { "WarningBrush", "Warning" }, KeysInOrder(preview));
        }

        [TestMethod]
        public void Build_SharedDependency_IncludedOnce()
        {
            var preview = BuildFor("Group", ("Icons.xaml",
                "<SolidColorBrush x:Key=\"Brush\" Color=\"Red\" />" +
                "<DrawingGroup x:Key=\"Group\">" +
                "  <GeometryDrawing Brush=\"{StaticResource Brush}\" Geometry=\"M0,0 L1,1 Z\" />" +
                "  <GeometryDrawing Brush=\"{StaticResource Brush}\" Geometry=\"M1,1 L2,2 Z\" />" +
                "</DrawingGroup>"));

            CollectionAssert.AreEqual(new[] { "Brush", "Group" }, KeysInOrder(preview));
        }

        [TestMethod]
        public void Build_Cycle_DoesNotHang()
        {
            var preview = BuildFor("A", ("Icons.xaml",
                "<SolidColorBrush x:Key=\"A\" Color=\"{StaticResource B}\" />" +
                "<Color x:Key=\"B\" Tag=\"{StaticResource A}\">Red</Color>"));

            CollectionAssert.AreEquivalent(new[] { "A", "B" }, KeysInOrder(preview));
        }

        [TestMethod]
        public void Build_SelfReference_IsIgnored()
        {
            var preview = BuildFor("A", ("Icons.xaml", "<SolidColorBrush x:Key=\"A\" Color=\"Red\" Opacity=\"{StaticResource A}\" />"));
            CollectionAssert.AreEqual(new[] { "A" }, KeysInOrder(preview));
            Assert.AreEqual(0, preview.MissingKeys.Count);
        }

        [TestMethod]
        public void Build_MissingDependency_IsReported()
        {
            var preview = BuildFor("Icon", ("Icons.xaml", "<GeometryDrawing x:Key=\"Icon\" Brush=\"{StaticResource ThemeBrush}\" Geometry=\"M0,0 L1,1 Z\" />"));
            CollectionAssert.AreEqual(new[] { "ThemeBrush" }, preview.MissingKeys.ToArray());
        }

        // ---------- Build: changes for the preview ----------

        [TestMethod]
        public void Build_DynamicResource_BecomesStaticResource()
        {
            var preview = BuildFor("Icon",
                ("Icons.xaml", "<GeometryDrawing x:Key=\"Icon\" Brush=\"{DynamicResource Accent}\" Geometry=\"M0,0 L1,1 Z\" />"),
                ("Brushes.xaml", "<SolidColorBrush x:Key=\"Accent\" Color=\"Blue\" />"));

            StringAssert.Contains(preview.Xaml, "{StaticResource Accent}");
            Assert.IsFalse(preview.Xaml.Contains("DynamicResource"));
        }

        [TestMethod]
        public void Build_DynamicResourceElementSyntax_BecomesStaticResource()
        {
            var preview = BuildFor("Icon",
                ("Icons.xaml", "<GeometryDrawing x:Key=\"Icon\" Geometry=\"M0,0 L1,1 Z\"><GeometryDrawing.Brush><DynamicResource ResourceKey=\"Accent\" /></GeometryDrawing.Brush></GeometryDrawing>"),
                ("Brushes.xaml", "<SolidColorBrush x:Key=\"Accent\" Color=\"Blue\" />"));

            Assert.IsFalse(preview.Xaml.Contains("DynamicResource"));
        }

        [TestMethod]
        public void Build_DesignerAttributes_AreRemoved()
        {
            var preview = BuildFor("A", ("Icons.xaml",
                "<SolidColorBrush x:Key=\"A\" Color=\"Red\" d:IsLocked=\"True\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" />"));

            Assert.IsFalse(preview.Xaml.Contains("IsLocked"));
        }

        [TestMethod]
        public void Build_PrefixUsedInAttributeValue_IsDeclared()
        {
            // {x:Static sys:Math.PI} uses "sys:" only inside a value, which LINQ to XML does not see.
            var preview = BuildFor("A", ("Icons.xaml", "<RotateTransform x:Key=\"A\" Angle=\"{x:Static sys:Math.PI}\" />"));
            StringAssert.Contains(preview.Xaml, "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\"");
        }

        [TestMethod]
        public void Build_UnusedPrefix_IsNotDeclared()
        {
            var preview = BuildFor("A", ("Icons.xaml", "<SolidColorBrush x:Key=\"A\" Color=\"Red\" />"));
            Assert.IsFalse(preview.Xaml.Contains("xmlns:local"));
        }

        [TestMethod]
        public void Build_PackUri_BecomesFileUriInProjectFolder()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App/App.csproj", "<Project />");
                string file = solution.Write("App/Resources/Images.xaml", Xaml.Dictionary(
                    "<BitmapImage x:Key=\"Logo\" UriSource=\"pack://application:,,,/Images/logo.png\" />"));

                var index = ResourceIndex.ForFile(file);
                var preview = PreviewXamlBuilder.Build(index, index.Find("Logo", file, CancellationToken.None).Single(), CancellationToken.None);

                string expected = new Uri(Path.Combine(solution.Root, "App", "Images", "logo.png")).AbsoluteUri;
                StringAssert.Contains(preview.Xaml, expected);
            }
        }

        [TestMethod]
        public void Build_PackUriWithAssembly_UsesThatProject()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App/App.csproj", "<Project />");
                solution.Write("Shared/MyCompany.Icons/MyCompany.Icons.csproj", "<Project />");
                string file = solution.Write("App/Images.xaml", Xaml.Dictionary(
                    "<BitmapImage x:Key=\"Logo\" UriSource=\"pack://application:,,,/MyCompany.Icons;component/Images/logo.png\" />"));

                var index = ResourceIndex.ForFile(file);
                var preview = PreviewXamlBuilder.Build(index, index.Find("Logo", file, CancellationToken.None).Single(), CancellationToken.None);

                string expected = new Uri(Path.Combine(solution.Root, "Shared", "MyCompany.Icons", "Images", "logo.png")).AbsoluteUri;
                StringAssert.Contains(preview.Xaml, expected);
            }
        }

        [TestMethod]
        public void Build_RelativeImagePaths_AreResolved()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App/App.csproj", "<Project />");
                string file = solution.Write("App/Resources/Images.xaml", Xaml.Dictionary(
                    "<BitmapImage x:Key=\"FromFile\" UriSource=\"icons/a.png\" />" +
                    "<BitmapImage x:Key=\"FromProject\" UriSource=\"/Images/b.png\" />"));
                var index = ResourceIndex.ForFile(file);

                string Build(string key) => PreviewXamlBuilder.Build(index, index.Find(key, file, CancellationToken.None).Single(), CancellationToken.None).Xaml;

                StringAssert.Contains(Build("FromFile"), new Uri(Path.Combine(solution.Root, "App", "Resources", "icons", "a.png")).AbsoluteUri, "relative to the XAML file");
                StringAssert.Contains(Build("FromProject"), new Uri(Path.Combine(solution.Root, "App", "Images", "b.png")).AbsoluteUri, "/ = relative to the project folder");
            }
        }

        // ---------- The result must load in WPF ----------

        [TestMethod]
        public void Build_Result_LoadsInXamlReader()
        {
            var preview = BuildFor("Report", ("Icons.xaml",
                "<Geometry x:Key=\"ReportGeometry\">M0,0 L10,10 Z</Geometry>" +
                "<DrawingGroup x:Key=\"ReportDrawingGroup\"><GeometryDrawing Brush=\"{DynamicResource Accent}\" Geometry=\"{StaticResource ReportGeometry}\" /></DrawingGroup>" +
                "<DrawingImage x:Key=\"Report\" Drawing=\"{StaticResource ReportDrawingGroup}\" />" +
                "<SolidColorBrush x:Key=\"Accent\" Color=\"#FF002BBD\" />"));

            Type loaded = Sta.Run(() => ((ResourceDictionary)XamlReader.Parse(preview.Xaml))["Report"].GetType());

            Assert.AreEqual(typeof(DrawingImage), loaded);
        }
    }
}
