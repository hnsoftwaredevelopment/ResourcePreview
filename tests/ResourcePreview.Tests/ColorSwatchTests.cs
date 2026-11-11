using System.Linq;
using System.Threading;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>Color swatches in the editor: which values get one, and the color of a key.</summary>
    [TestClass]
    public class ColorSwatchTests
    {
        private static string Describe(string line) =>
            string.Join(" | ", ColorSwatchFinder.Find(line).Select(s => line.Substring(s.Position, 3) + "=" + (s.Key ?? s.Color.ToString())));

        [DataTestMethod]
        [DataRow("<Border Background=\"White\" />", "Whi=#FFFFFFFF")]
        [DataRow("<Path Fill=\"#2D7D9A\" Stroke=\"#80FF0000\" />", "#2D=#FF2D7D9A | #80=#80FF0000")]
        [DataRow("<GradientStop Color=\"Red\" Offset=\"1\" />", "Red=#FFFF0000")]
        [DataRow("<SolidColorBrush x:Key=\"AccentBrush\" Color=\"#FF002BBD\" />", "#FF=#FF002BBD")]
        [DataRow("<Color x:Key=\"Accent\">#FF2D7D9A</Color>", "#FF=#FF2D7D9A")]
        [DataRow("<Setter Property=\"Foreground\" Value=\"White\" />", "Whi=#FFFFFFFF")]
        [DataRow("<TextBlock Foreground=\"{StaticResource AccentBrush}\" />", "{St=AccentBrush")]
        [DataRow("<Border BorderBrush=\"{DynamicResource ResourceKey=Line}\" />", "{Dy=Line")]
        public void Find_ColorsAndKeys(string line, string expected)
        {
            Assert.AreEqual(expected, Describe(line));
        }

        [DataTestMethod]
        [DataRow("<TextBlock Text=\"Red\" />", DisplayName = "text, not a color")]
        [DataRow("<Button Content=\"White\" Tag=\"#FF0000\" />", DisplayName = "no color property")]
        [DataRow("<Setter Property=\"Content\" Value=\"Red\" />", DisplayName = "setter for text")]
        [DataRow("<Button Style=\"{StaticResource AccentBrush}\" />", DisplayName = "reference in Style")]
        [DataRow("<Border Background=\"{TemplateBinding Background}\" />", DisplayName = "binding")]
        [DataRow("<!-- <Border Background=\"Red\" /> -->", DisplayName = "comment")]
        [DataRow("<ColorAnimation To=\"Red\" />", DisplayName = "ColorAnimation is no Color element")]
        public void Find_Nothing(string line)
        {
            Assert.AreEqual(string.Empty, Describe(line));
        }

        [TestMethod]
        public void Resolve_BrushColorAndChains()
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("Colors.xaml", Xaml.Dictionary(
                    "<Color x:Key=\"Accent\">#FF2D7D9A</Color>\n" +
                    "<SolidColorBrush x:Key=\"AccentBrush\" Color=\"{StaticResource Accent}\" />\n" +
                    "<SolidColorBrush x:Key=\"Plain\" Color=\"Red\" />\n" +
                    "<SolidColorBrush x:Key=\"Faded\" Color=\"Red\" Opacity=\"0.5\" />\n" +
                    "<LinearGradientBrush x:Key=\"Gradient\"><GradientStop Color=\"Red\" /></LinearGradientBrush>\n" +
                    "<SolidColorBrush x:Key=\"Loop\" Color=\"{StaticResource Loop}\" />"));
                ResourceIndex index = ResourceIndex.ForFile(file);

                Color? Resolve(string key) => ColorSwatchFinder.Resolve(index, key, file, CancellationToken.None);

                Assert.AreEqual(Color.FromRgb(0x2D, 0x7D, 0x9A), Resolve("Accent"));
                Assert.AreEqual(Color.FromRgb(0x2D, 0x7D, 0x9A), Resolve("AccentBrush"));
                Assert.AreEqual(Colors.Red, Resolve("Plain"));
                Assert.IsNull(Resolve("Faded"));
                Assert.IsNull(Resolve("Gradient"));
                Assert.IsNull(Resolve("Loop"));
                Assert.IsNull(Resolve("Missing"));
            }
        }

        [TestMethod]
        public void Sample_AccentBrush()
        {
            string main = System.IO.Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            Color? color = ColorSwatchFinder.Resolve(ResourceIndex.ForFile(main), "AccentBrush", main, CancellationToken.None);
            Assert.AreEqual(Color.FromRgb(0x00, 0x2B, 0xBD), color);
        }
    }
}
