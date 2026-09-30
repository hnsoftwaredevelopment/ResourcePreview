using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Localization;
using ResourcePreview.Options;
using ResourcePreview.Preview;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>The full VS pipeline without VS: find the key in the sample app, build the XAML, load it with XamlReader.</summary>
    [TestClass]
    public class SampleAppEndToEndTests
    {
        private static string MainWindow => Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");

        private static object Load(string key)
        {
            var index = ResourceIndex.ForFile(MainWindow);
            ResourceEntry entry = index.Find(key, MainWindow, CancellationToken.None).First();
            PreviewXaml preview = PreviewXamlBuilder.Build(index, entry, CancellationToken.None);
            Assert.AreEqual(0, preview.MissingKeys.Count, $"missing keys for {key}: {string.Join(", ", preview.MissingKeys)}");

            // Return only the type: WPF objects belong to the STA thread that created them.
            return Sta.Run(() =>
            {
                object resource = ((ResourceDictionary)XamlReader.Parse(preview.Xaml))[key];
                return (object)resource.GetType();
            });
        }

        [TestMethod]
        [DataRow("Renew", typeof(GeometryGroup))]
        [DataRow("Search", typeof(GeometryGroup))]
        [DataRow("Folder", typeof(GeometryGroup))]
        [DataRow("SwapTitleAuthorIconGeometry", typeof(StreamGeometry))]
        [DataRow("Report", typeof(DrawingImage))]
        [DataRow("Warning", typeof(DrawingImage))]
        [DataRow("AccentBrush", typeof(SolidColorBrush))]
        [DataRow("HeaderFont", typeof(FontFamily))]
        [DataRow("CardPadding", typeof(Thickness))]
        [DataRow("TabCorners", typeof(CornerRadius))]
        [DataRow("HeaderFontSize", typeof(double))]
        [DataRow("AppTitle", typeof(string))]
        [DataRow("ShowDebugInfo", typeof(bool))]
        [DataRow("MaxItems", typeof(int))]
        [DataRow("HeaderWeight", typeof(FontWeight))]
        [DataRow("DebugVisibility", typeof(Visibility))]
        [DataRow("CardShadow", typeof(System.Windows.Media.Effects.DropShadowEffect))]
        [DataRow("SoftBlur", typeof(System.Windows.Media.Effects.BlurEffect))]
        [DataRow("SearchBox", typeof(Style))]
        [DataRow("RoundButtonTemplate", typeof(ControlTemplate))]
        [DataRow("WrapPanelTemplate", typeof(ItemsPanelTemplate))]
        public void SampleResource_LoadsAsExpectedType(string key, Type expected)
        {
            Assert.AreEqual(expected, Load(key));
        }

        [TestMethod]
        public void RootDirectory_IsSampleAppSolutionFolder()
        {
            Assert.AreEqual(RepoPaths.SampleApp, ResourceIndex.ForFile(MainWindow).RootDirectory);
        }

        [TestMethod]
        public void Check_HasTwoDefinitions()
        {
            var entries = ResourceIndex.ForFile(MainWindow).Find("Check", MainWindow, CancellationToken.None);
            CollectionAssert.AreEquivalent(new[] { "Icons.xaml", "IconsLegacy.xaml" }, entries.Select(e => Path.GetFileName(e.FilePath)).ToArray());
        }

        [TestMethod]
        public void DoesNotExist_IsNotFound()
        {
            Assert.AreEqual(0, ResourceIndex.ForFile(MainWindow).Find("DoesNotExist", MainWindow, CancellationToken.None).Count);
        }

        [TestMethod]
        public void EveryReferenceInMainWindow_ExceptDoesNotExist_IsFound()
        {
            // Checks MainWindow.xaml line by line, the same way the hover in VS does.
            var index = ResourceIndex.ForFile(MainWindow);
            foreach (string line in File.ReadAllLines(MainWindow))
            {
                int position = line.IndexOf("Resource ", StringComparison.Ordinal);
                if (position < 0 || !QuickInfo.ResourceReferenceFinder.TryFind(line, position + 10, out var reference) || reference.Key == "DoesNotExist")
                {
                    continue;
                }

                Assert.IsTrue(index.Find(reference.Key, MainWindow, CancellationToken.None).Count > 0, $"'{reference.Key}' not found");
            }
        }
    }

    /// <summary>The renderer: tiles, size and colors from the settings.</summary>
    [TestClass]
    public class PreviewRendererTests
    {
        private const string GeometryXaml =
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
            "<Geometry x:Key=\"Icon\">M0,0 L20,0 20,10 0,10 Z</Geometry></ResourceDictionary>";

        [TestInitialize]
        public void Setup()
        {
            Localizer.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("en"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            SettingsService.SetCurrentForTests(ExtensionSettings.Default);
        }

        private static ResourcePreviewData Data(string xaml, string key = "Icon") =>
            new ResourcePreviewData(key, "Geometry", xaml, Array.Empty<string>(), navigate: null);

        private static ExtensionSettings With(PreviewTiles tiles, int size = 64) => new ExtensionSettings(
            ExtensionLanguage.English, 5, size, tiles, Colors.Red, Colors.Yellow, Colors.Lime, Colors.Navy);

        private static Border[] Tiles(UIElement rendered)
        {
            var root = (StackPanel)rendered;
            return ((StackPanel)root.Children[0]).Children.OfType<Border>().ToArray();
        }

        [TestMethod]
        public void Default_TwoTiles_LightThenDark()
        {
            Sta.Run(() =>
            {
                Border[] tiles = Tiles(PreviewRenderer.Render(Data(GeometryXaml)));
                Assert.AreEqual(2, tiles.Length);
                Assert.AreEqual(Colors.White, ((SolidColorBrush)tiles[0].Background).Color);
                Assert.AreEqual(Color.FromRgb(0x1E, 0x1E, 0x1E), ((SolidColorBrush)tiles[1].Background).Color);
            });
        }

        [TestMethod]
        [DataRow(PreviewTiles.Both, 2)]
        [DataRow(PreviewTiles.Light, 1)]
        [DataRow(PreviewTiles.Dark, 1)]
        public void TilesSetting_ControlsNumberOfTiles(PreviewTiles mode, int expected)
        {
            SettingsService.SetCurrentForTests(With(mode));
            Sta.Run(() => Assert.AreEqual(expected, Tiles(PreviewRenderer.Render(Data(GeometryXaml))).Length));
        }

        [TestMethod]
        public void Colors_ComeFromSettings()
        {
            SettingsService.SetCurrentForTests(With(PreviewTiles.Both));
            Sta.Run(() =>
            {
                Border[] tiles = Tiles(PreviewRenderer.Render(Data(GeometryXaml)));
                var lightPath = (ShapePath)tiles[0].Child;
                var darkPath = (ShapePath)tiles[1].Child;

                Assert.AreEqual(Colors.Red, ((SolidColorBrush)lightPath.Fill).Color, "light tile foreground");
                Assert.AreEqual(Colors.Yellow, ((SolidColorBrush)tiles[0].Background).Color, "light tile background");
                Assert.AreEqual(Colors.Lime, ((SolidColorBrush)darkPath.Fill).Color, "dark tile foreground");
                Assert.AreEqual(Colors.Navy, ((SolidColorBrush)tiles[1].Background).Color, "dark tile background");
            });
        }

        [TestMethod]
        public void TileSize_ComesFromSettings()
        {
            SettingsService.SetCurrentForTests(With(PreviewTiles.Light, size: 32));
            Sta.Run(() =>
            {
                var visual = (FrameworkElement)Tiles(PreviewRenderer.Render(Data(GeometryXaml)))[0].Child;
                Assert.AreEqual(32.0, visual.Width);
                Assert.AreEqual(32.0, visual.Height);
            });
        }

        [TestMethod]
        public void Geometry_ShowsTypeAndSize()
        {
            Sta.Run(() =>
            {
                var root = (StackPanel)PreviewRenderer.Render(Data(GeometryXaml));
                string details = ((TextBlock)root.Children[1]).Text;
                StringAssert.Contains(details, "20");
                StringAssert.Contains(details, "10");
            });
        }

        [TestMethod]
        public void DrawingImage_IsShownAsImage()
        {
            const string xaml =
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
                "<DrawingImage x:Key=\"Icon\"><DrawingImage.Drawing><GeometryDrawing Brush=\"Red\" Geometry=\"M0,0 L1,1 1,0 Z\" /></DrawingImage.Drawing></DrawingImage></ResourceDictionary>";

            Sta.Run(() => Assert.IsInstanceOfType(Tiles(PreviewRenderer.Render(Data(xaml)))[0].Child, typeof(Image)));
        }

        [TestMethod]
        public void Brush_IsShownAsRectangle()
        {
            const string xaml =
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
                "<SolidColorBrush x:Key=\"Icon\" Color=\"Orange\" /></ResourceDictionary>";

            Sta.Run(() => Assert.IsInstanceOfType(Tiles(PreviewRenderer.Render(Data(xaml)))[0].Child, typeof(Rectangle)));
        }

        [TestMethod]
        public void UnsupportedType_ShowsMessage()
        {
            const string xaml =
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
                "<ScaleTransform x:Key=\"Icon\" ScaleX=\"2\" /></ResourceDictionary>";

            Sta.Run(() =>
            {
                var message = (TextBlock)PreviewRenderer.Render(Data(xaml));
                StringAssert.Contains(message.Text, "ScaleTransform");
            });
        }

        [TestMethod]
        public void BrokenXaml_ShowsErrorWithKey()
        {
            Sta.Run(() =>
            {
                var message = (TextBlock)PreviewRenderer.Render(Data("<ResourceDictionary><Oops", key: "Renew"));
                StringAssert.Contains(message.Text, "Renew");
            });
        }

        [TestMethod]
        public void BrokenXaml_ListsMissingKeys()
        {
            var data = new ResourcePreviewData("Icon", "GeometryDrawing", "<broken", new[] { "ThemeBrush" }, navigate: null);
            Sta.Run(() => StringAssert.Contains(((TextBlock)PreviewRenderer.Render(data)).Text, "ThemeBrush"));
        }
    }
}
