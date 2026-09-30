using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Localization;
using ResourcePreview.Options;
using ResourcePreview.Preview;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>FontFamily, Thickness, CornerRadius, sys: values and effects.</summary>
    [TestClass]
    public class ValuePreviewRendererTests
    {
        private const string Sys = "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\"";

        [TestInitialize]
        public void Setup()
        {
            Localizer.SetCulture(CultureInfo.GetCultureInfo("en"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            SettingsService.SetCurrentForTests(ExtensionSettings.Default);
        }

        private static ResourcePreviewData Data(string body, Action navigate = null) =>
            new ResourcePreviewData("Value", "Value", Xaml.Dictionary(body, Sys), Array.Empty<string>(), navigate);

        private static TextBlock[] Texts(UIElement rendered) =>
            ((Panel)rendered).Children.OfType<TextBlock>().ToArray();

        private static T Parse<T>(string body) =>
            (T)((ResourceDictionary)XamlReader.Parse(Xaml.Dictionary(body, Sys)))["Value"];

        // ---------- FontFamily ----------

        [TestMethod]
        public void FontFamily_ShowsSampleInNormalAndBold()
        {
            Sta.Run(() =>
            {
                TextBlock[] texts = Texts(PreviewRenderer.Render(Data("<FontFamily x:Key=\"Value\">Arial</FontFamily>")));

                Assert.AreEqual(ValuePreviewRenderer.FontSample, texts[0].Text);
                Assert.AreEqual("Arial", texts[0].FontFamily.Source);
                Assert.AreEqual(FontWeights.Normal, texts[0].FontWeight);
                Assert.AreEqual(FontWeights.Bold, texts[1].FontWeight);
                Assert.AreEqual("Arial", texts[2].Text, "font name below the sample");
            });
        }

        [TestMethod]
        public void FontFamily_Installed_NoWarning()
        {
            Sta.Run(() => Assert.AreEqual(3, Texts(PreviewRenderer.Render(Data("<FontFamily x:Key=\"Value\">Arial</FontFamily>"))).Length));
        }

        [TestMethod]
        public void FontFamily_NotInstalled_ShowsWarning()
        {
            Sta.Run(() =>
            {
                TextBlock[] texts = Texts(PreviewRenderer.Render(Data("<FontFamily x:Key=\"Value\">Missing Font Sans</FontFamily>")));
                Assert.AreEqual(4, texts.Length);
                StringAssert.Contains(texts[3].Text, "Missing Font Sans");
                StringAssert.Contains(texts[3].Text, "not installed");
            });
        }

        [TestMethod]
        [DataRow("Missing Font Sans, Arial", true)]
        [DataRow("Arial, Missing Font Sans", false)]
        [DataRow("Global User Interface", false)]
        [DataRow("segoe ui", false)]
        [DataRow("Segoe UI Semibold", false)] // a face name, not a family in Fonts.SystemFontFamilies
        public void FindFontProblem_OnlyFirstNameCounts(string source, bool expectProblem)
        {
            string problem = Sta.Run(() => ValuePreviewRenderer.FindFontProblem(new FontFamily(source)));
            Assert.AreEqual(expectProblem, problem != null, problem);
        }

        [TestMethod]
        public void FindFontProblem_FontInFolder()
        {
            string arial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
            if (!File.Exists(arial))
            {
                Assert.Inconclusive("arial.ttf not found");
            }

            using (var solution = new TempSolution())
            {
                string fonts = Path.Combine(solution.Root, "Fonts");
                Directory.CreateDirectory(fonts);
                File.Copy(arial, Path.Combine(fonts, "arial.ttf"));
                string folderUri = new Uri(fonts + Path.DirectorySeparatorChar).AbsoluteUri;

                Sta.Run(() =>
                {
                    Assert.IsNull(ValuePreviewRenderer.FindFontProblem(new FontFamily(folderUri + "#Arial")), "font is in the folder");

                    string wrongName = ValuePreviewRenderer.FindFontProblem(new FontFamily(folderUri + "#Inter"));
                    StringAssert.Contains(wrongName, "Inter");
                    StringAssert.Contains(wrongName, fonts);

                    string missingFolder = ValuePreviewRenderer.FindFontProblem(new FontFamily(folderUri + "Missing/#Arial"));
                    Assert.IsNotNull(missingFolder, "folder does not exist");
                });
            }
        }

        [TestMethod]
        public void FindFontProblem_NotCheckable_NoMessage()
        {
            // A relative location without a base cannot be checked, so no message is shown.
            Assert.IsNull(Sta.Run(() => ValuePreviewRenderer.FindFontProblem(new FontFamily("./Fonts/#Inter"))));
        }

        // ---------- Thickness ----------

        [TestMethod]
        public void Thickness_ShowsFourValuesInOrder()
        {
            Sta.Run(() =>
            {
                var root = (StackPanel)PreviewRenderer.Render(Data("<Thickness x:Key=\"Value\">12,8,10,6</Thickness>"));
                var edges = (Border)root.Children[0];

                Assert.AreEqual(new Thickness(12, 8, 10, 6), edges.BorderThickness);
                Assert.AreEqual("Left 12 · Top 8 · Right 10 · Bottom 6", ((TextBlock)root.Children[1]).Text);
            });
        }

        [TestMethod]
        public void Thickness_Large_IsScaledSoThickestEdgeIs16()
        {
            Sta.Run(() =>
            {
                var root = (StackPanel)PreviewRenderer.Render(Data("<Thickness x:Key=\"Value\">40,0,40,16</Thickness>"));
                Thickness drawn = ((Border)root.Children[0]).BorderThickness;

                Assert.AreEqual(16, drawn.Left, 0.001);
                Assert.AreEqual(6.4, drawn.Bottom, 0.001);
                StringAssert.Contains(((TextBlock)root.Children[1]).Text, "Left 40", "the text shows the real value");
            });
        }

        [TestMethod]
        public void Thickness_Negative_IsDrawnAsZero()
        {
            Sta.Run(() =>
            {
                var root = (StackPanel)PreviewRenderer.Render(Data("<Thickness x:Key=\"Value\">-4,2,0,0</Thickness>"));
                Assert.AreEqual(0, ((Border)root.Children[0]).BorderThickness.Left);
                StringAssert.Contains(((TextBlock)root.Children[1]).Text, "Left -4");
            });
        }

        [TestMethod]
        public void Thickness_Text_IsTranslated_NumbersStayInvariant()
        {
            // Dutch labels, but 1.5 stays 1.5 as in XAML; 1,5 would look like two values.
            Localizer.SetCulture(CultureInfo.GetCultureInfo("nl"));
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                Sta.Run(() =>
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
                    var root = (StackPanel)PreviewRenderer.Render(Data("<Thickness x:Key=\"Value\">1.5</Thickness>"));
                    Assert.AreEqual("Links 1.5 · Boven 1.5 · Rechts 1.5 · Onder 1.5", ((TextBlock)root.Children[1]).Text);
                });
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        // ---------- CornerRadius ----------

        [TestMethod]
        public void CornerRadius_ShowsFourValuesAndShape()
        {
            Sta.Run(() =>
            {
                var root = (StackPanel)PreviewRenderer.Render(Data("<CornerRadius x:Key=\"Value\">8,8,0,0</CornerRadius>"));
                var shape = (Border)root.Children[0];

                Assert.AreEqual(new CornerRadius(8, 8, 0, 0), shape.CornerRadius);
                Assert.AreEqual("Top left 8 · Top right 8 · Bottom right 0 · Bottom left 0", ((TextBlock)root.Children[1]).Text);
            });
        }

        [TestMethod]
        public void CornerRadius_Large_IsScaledTo20()
        {
            Sta.Run(() =>
            {
                var root = (StackPanel)PreviewRenderer.Render(Data("<CornerRadius x:Key=\"Value\">100,50,0,0</CornerRadius>"));
                CornerRadius drawn = ((Border)root.Children[0]).CornerRadius;
                Assert.AreEqual(20, drawn.TopLeft, 0.001);
                Assert.AreEqual(10, drawn.TopRight, 0.001);
            });
        }

        // ---------- Simple values ----------

        [TestMethod]
        [DataRow("<sys:Double x:Key=\"Value\">18</sys:Double>", "18")]
        [DataRow("<sys:Double x:Key=\"Value\">1.5</sys:Double>", "1.5")]
        [DataRow("<sys:String x:Key=\"Value\">Save</sys:String>", "\"Save\"")]
        [DataRow("<sys:String x:Key=\"Value\"></sys:String>", "\"\"")]
        [DataRow("<sys:Boolean x:Key=\"Value\">True</sys:Boolean>", "True")]
        [DataRow("<sys:Int32 x:Key=\"Value\">25</sys:Int32>", "25")]
        [DataRow("<FontWeight x:Key=\"Value\">SemiBold</FontWeight>", "SemiBold")]
        [DataRow("<Visibility x:Key=\"Value\">Collapsed</Visibility>", "Collapsed")]
        [DataRow("<GridLength x:Key=\"Value\">2*</GridLength>", "2*")]
        [DataRow("<Point x:Key=\"Value\">1,2</Point>", "1,2")]
        public void Value_IsShownAsText(string body, string expected)
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                Sta.Run(() =>
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL"); // must not turn 1.5 into 1,5
                    TextBlock[] texts = Texts(PreviewRenderer.Render(Data(body)));
                    Assert.AreEqual(1, texts.Length);
                    Assert.AreEqual(expected, texts[0].Text);
                });
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [TestMethod]
        public void DescribeValue_ReferenceType_IsNull()
        {
            Assert.IsNull(Sta.Run(() => ValuePreviewRenderer.DescribeValue(new ScaleTransform(2, 2))));
            Assert.IsNull(ValuePreviewRenderer.DescribeValue(null));
        }

        [TestMethod]
        public void ValuePreview_WithNavigate_IsClickable()
        {
            int clicks = 0;
            Sta.Run(() =>
            {
                var panel = (Panel)PreviewRenderer.Render(Data("<sys:Double x:Key=\"Value\">18</sys:Double>", () => clicks++));
                Assert.AreEqual(Cursors.Hand, panel.Cursor);
                panel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            });

            Assert.AreEqual(1, clicks);
        }

        // ---------- Effects ----------

        [TestMethod]
        public void DropShadowEffect_IsShownOnTilesWithDetails()
        {
            Sta.Run(() =>
            {
                var root = (StackPanel)PreviewRenderer.Render(Data(
                    "<DropShadowEffect x:Key=\"Value\" Color=\"Black\" BlurRadius=\"8\" ShadowDepth=\"3\" Direction=\"315\" Opacity=\"0.5\" />"));

                Border[] tiles = ((StackPanel)root.Children[0]).Children.OfType<Border>().ToArray();
                Assert.AreEqual(2, tiles.Length);

                var viewbox = (Viewbox)tiles[0].Child;
                var sample = (System.Windows.Shapes.Rectangle)((Grid)viewbox.Child).Children[0];
                Assert.IsInstanceOfType(sample.Effect, typeof(DropShadowEffect));

                Assert.AreEqual(
                    "DropShadowEffect · Color #FF000000 · BlurRadius 8 · ShadowDepth 3 · Direction 315 · Opacity 0.5",
                    ((TextBlock)root.Children[1]).Text);
            });
        }

        [TestMethod]
        public void EffectSample_UsesTileForeground()
        {
            SettingsService.SetCurrentForTests(new ExtensionSettings(
                ExtensionLanguage.English, 5, 32, PreviewTiles.Light, Colors.Red, Colors.Yellow, Colors.Lime, Colors.Navy));

            Sta.Run(() =>
            {
                var root = (StackPanel)PreviewRenderer.Render(Data("<BlurEffect x:Key=\"Value\" Radius=\"4\" />"));
                var tile = ((StackPanel)root.Children[0]).Children.OfType<Border>().Single();
                var viewbox = (Viewbox)tile.Child;
                var sample = (System.Windows.Shapes.Rectangle)((Grid)viewbox.Child).Children[0];

                Assert.AreEqual(32.0, viewbox.Width, "tile size from the settings");
                Assert.AreEqual(Colors.Red, ((SolidColorBrush)sample.Fill).Color);
            });
        }

        [TestMethod]
        public void DescribeEffect_Blur()
        {
            Assert.AreEqual("BlurEffect · Radius 4 · Gaussian", Sta.Run(() => PreviewRenderer.DescribeEffect(new BlurEffect { Radius = 4 })));
        }
    }

    /// <summary>What PreviewXamlBuilder does for fonts and for sys: from .NET 8 projects.</summary>
    [TestClass]
    public class ValuePreviewXamlBuilderTests
    {
        [TestMethod]
        [DataRow("clr-namespace:System;assembly=System.Runtime", "clr-namespace:System;assembly=mscorlib")]
        [DataRow("clr-namespace:System;assembly=System.Private.CoreLib", "clr-namespace:System;assembly=mscorlib")]
        [DataRow("clr-namespace:System;assembly=netstandard", "clr-namespace:System;assembly=mscorlib")]
        [DataRow("clr-namespace:System;assembly=mscorlib", "clr-namespace:System;assembly=mscorlib")]
        [DataRow("clr-namespace:MyApp", "clr-namespace:MyApp")]
        [DataRow("clr-namespace:MyApp;assembly=System.RuntimeHelpers", "clr-namespace:MyApp;assembly=System.RuntimeHelpers")]
        public void NormalizeClrNamespace(string input, string expected)
        {
            Assert.AreEqual(expected, PreviewXamlBuilder.NormalizeClrNamespace(input));
        }

        [TestMethod]
        public void SysFromSystemRuntime_LoadsInNetFramework()
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("Values.xaml", Xaml.Dictionary(
                    // "Size" uses sys: as element name. "Turn" uses sys: only in a value, declared on the root.
                    "<sys:Double x:Key=\"Size\">18</sys:Double><RotateTransform x:Key=\"Turn\" Angle=\"{x:Static sys:Math.PI}\" />",
                    "xmlns:sys=\"clr-namespace:System;assembly=System.Runtime\""));

                var index = ResourceIndex.ForFile(file);
                foreach (var (key, type) in new[] { ("Size", typeof(double)), ("Turn", typeof(RotateTransform)) })
                {
                    PreviewXaml preview = PreviewXamlBuilder.Build(index, index.Find(key, file, CancellationToken.None).Single(), CancellationToken.None);
                    StringAssert.DoesNotMatch(preview.Xaml, new System.Text.RegularExpressions.Regex("System\\.Runtime"));

                    Type actual = Sta.Run(() => ((ResourceDictionary)XamlReader.Parse(preview.Xaml))[key].GetType());
                    Assert.AreEqual(type, actual, key);
                }
            }
        }

        private sealed class FontProject : IDisposable
        {
            private readonly TempSolution _solution = new TempSolution();

            public FontProject()
            {
                _solution.Write("App/App.csproj", "<Project />");
                _solution.Write("Other/Other.csproj", "<Project />");
                File = _solution.Write("App/Themes/Fonts.xaml", Xaml.Dictionary("<FontFamily x:Key=\"A\">Arial</FontFamily>"));
                Index = ResourceIndex.ForFile(File);
                Index.Find("A", File, CancellationToken.None); // makes the index find the projects
            }

            public string File { get; }

            public ResourceIndex Index { get; }

            public string Uri(string relative) => new Uri(Path.Combine(_solution.Root, relative)).AbsoluteUri;

            public string Resolve(string value) => PreviewXamlBuilder.ResolveFontFamily(
                Index, value, ResourceIndex.FindProjectDirectory(File), Path.GetDirectoryName(File));

            public void Dispose() => _solution.Dispose();
        }

        [TestMethod]
        public void ResolveFontFamily_AllLocationForms()
        {
            using (var project = new FontProject())
            {
                Assert.AreEqual(project.Uri(@"App\Themes\Fonts\") + "#Inter", project.Resolve("./Fonts/#Inter"), "relative to the XAML file");
                Assert.AreEqual(project.Uri(@"App\Themes\Fonts\") + "#Inter", project.Resolve("Fonts/#Inter"));
                Assert.AreEqual(project.Uri(@"App\Themes\") + "#Inter", project.Resolve("#Inter"), "same folder as the XAML file");
                Assert.AreEqual(project.Uri(@"App\Fonts\") + "#Inter", project.Resolve("/Fonts/#Inter"), "relative to the project folder");
                Assert.AreEqual(project.Uri(@"App\Fonts\") + "#Inter", project.Resolve("pack://application:,,,/Fonts/#Inter"));
                Assert.AreEqual(project.Uri(@"App\") + "#Inter", project.Resolve("pack://application:,,,/#Inter"));
                Assert.AreEqual(project.Uri(@"Other\Fonts\") + "#Inter", project.Resolve("pack://application:,,,/Other;component/Fonts/#Inter"));
                Assert.AreEqual(project.Uri(@"Other\Fonts\") + "#Inter", project.Resolve("/Other;component/Fonts/#Inter"));
                Assert.AreEqual(project.Uri(@"App\Fonts\Inter.ttf") + "#Inter", project.Resolve("/Fonts/Inter.ttf#Inter"), "a file, not a folder");
            }
        }

        [TestMethod]
        [DataRow("Segoe UI")]
        [DataRow("Segoe UI, Arial")]
        [DataRow("{StaticResource BrandFont}")]
        [DataRow("file:///C:/Fonts/#Inter")]
        [DataRow("")]
        public void ResolveFontFamily_LeavesOthersAlone(string value)
        {
            using (var project = new FontProject())
            {
                Assert.AreEqual(value, project.Resolve(value));
            }
        }

        [TestMethod]
        public void ResolveFontFamily_FallbackListKeepsSystemNames()
        {
            using (var project = new FontProject())
            {
                Assert.AreEqual(project.Uri(@"App\Fonts\") + "#Inter, Segoe UI", project.Resolve("/Fonts/#Inter, Segoe UI"));
            }
        }

        [TestMethod]
        public void Build_ResolvesFontInElementTextAndAttributes()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App/App.csproj", "<Project />");
                string file = solution.Write("App/Fonts.xaml", Xaml.Dictionary(
                    "<FontFamily x:Key=\"Brand\">./Fonts/#Inter</FontFamily>" +
                    "<Style x:Key=\"Title\" TargetType=\"TextBlock\"><Setter Property=\"Tag\" Value=\"x\" /></Style>" +
                    "<TextBlock x:Key=\"Sample\" FontFamily=\"pack://application:,,,/Fonts/#Inter\" />"));

                var index = ResourceIndex.ForFile(file);
                string expected = new Uri(Path.Combine(solution.Root, @"App\Fonts\")).AbsoluteUri + "#Inter";

                foreach (string key in new[] { "Brand", "Sample" })
                {
                    PreviewXaml preview = PreviewXamlBuilder.Build(index, index.Find(key, file, CancellationToken.None).Single(), CancellationToken.None);
                    StringAssert.Contains(preview.Xaml, expected, key);
                }

                PreviewXaml brand = PreviewXamlBuilder.Build(index, index.Find("Brand", file, CancellationToken.None).Single(), CancellationToken.None);
                string source = Sta.Run(() => ((FontFamily)((ResourceDictionary)XamlReader.Parse(brand.Xaml))["Brand"]).Source);
                Assert.AreEqual(expected, source);
            }
        }
    }
}
