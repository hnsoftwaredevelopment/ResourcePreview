using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.IntelliSense;
using ResourcePreview.Preview;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>Icons in the IntelliSense list: where they apply and what they look like.</summary>
    [TestClass]
    public class IntelliSenseTests
    {
        private static string MainWindow => Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");

        [DataTestMethod]
        [DataRow("<Button Style=\"{StaticResource ", true)]
        [DataRow("<Button Style=\"{StaticResource Prim", true)]
        [DataRow("<Path Fill=\"{DynamicResource Accent", true)]
        [DataRow("<Path Fill=\"{StaticResource ResourceKey=Acc", true)]
        [DataRow("<Path Fill=\"{StaticResource  ", true)]
        [DataRow("<Button Style=\"{StaticResource PrimaryButton}\" Content=\"", false)]
        [DataRow("<Button Style=\"{Binding ", false)]
        [DataRow("<Button Content=\"StaticResource ", false)]
        [DataRow("<Button Style=\"{StaticResource", false)]
        public void IsKeyPosition(string textBeforeCaret, bool expected)
        {
            Assert.AreEqual(expected, ResourceCompletionSource.IsKeyPosition(textBeforeCaret));
        }

        [DataTestMethod]
        [DataRow("GeometryGroup", true)]
        [DataRow("PathGeometry", true)]
        [DataRow("DrawingImage", true)]
        [DataRow("DrawingGroup", true)]
        [DataRow("BitmapImage", true)]
        [DataRow("SolidColorBrush", true)]
        [DataRow("LinearGradientBrush", true)]
        [DataRow("Color", true)]
        [DataRow("VisualBrush", false)]
        [DataRow("Style", false)]
        [DataRow("ControlTemplate", false)]
        [DataRow("Thickness", false)]
        public void HasIcon(string elementType, bool expected)
        {
            Assert.AreEqual(expected, CompletionIconFactory.HasIcon(elementType));
        }

        [TestMethod]
        public void FromResource_MakesFrozenIcons()
        {
            Sta.Run(() =>
            {
                Brush foreground = Brushes.Black;

                var geometry = (DrawingImage)CompletionIconFactory.FromResource(Geometry.Parse("M0,0 L10,10"), foreground);
                Assert.IsTrue(geometry.IsFrozen);
                Assert.AreSame(foreground, ((GeometryDrawing)geometry.Drawing).Brush);

                var swatch = (DrawingImage)CompletionIconFactory.FromResource(Colors.Red, foreground);
                Assert.AreEqual(Colors.Red, ((SolidColorBrush)((GeometryDrawing)swatch.Drawing).Brush).Color);

                Assert.IsNull(CompletionIconFactory.FromResource(new Style(), foreground));
                Assert.IsNull(CompletionIconFactory.FromResource(new Thickness(4), foreground));
            });
        }

        [TestMethod]
        public void Get_SampleResources()
        {
            Sta.Run(() =>
            {
                ResourceIndex index = ResourceIndex.ForFile(MainWindow);
                ResourceEntry Entry(string key) => index.Find(key, MainWindow, CancellationToken.None).First();

                ImageSource renew = CompletionIconFactory.Get(index, Entry("Renew"), Brushes.Black);
                Assert.IsInstanceOfType(renew, typeof(DrawingImage));
                Assert.AreSame(renew, CompletionIconFactory.Get(index, Entry("Renew"), Brushes.Black), "cached");

                Assert.IsNotNull(CompletionIconFactory.Get(index, Entry("Report"), Brushes.Black), "drawing with dependencies in other files");
                Assert.IsNotNull(CompletionIconFactory.Get(index, Entry("InfoBrush"), Brushes.Black));
                Assert.IsNull(CompletionIconFactory.Get(index, Entry("PrimaryButton"), Brushes.Black));
            });
        }

        [TestMethod]
        public void PreviewData_Create_IsTheSameAsOnHover()
        {
            ResourceIndex index = ResourceIndex.ForFile(MainWindow);
            ResourceEntry entry = index.Find("DangerButton", MainWindow, CancellationToken.None).First();

            ResourcePreviewData data = ResourcePreviewData.Create(index, entry, null, CancellationToken.None);

            Assert.AreEqual("DangerButton", data.Key);
            Assert.IsNotNull(data.Style);
            StringAssert.Contains(data.Xaml, "x:Key=\"PrimaryButton\"");
        }
    }
}
