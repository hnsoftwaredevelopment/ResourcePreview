using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Localization;
using ResourcePreview.Preview;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>Detects types VS cannot load up front, so XamlReader never throws on them.</summary>
    [TestClass]
    public class UnloadableTypeTests
    {
        private const string Namespaces =
            "xmlns:local=\"clr-namespace:MyApp\" " +
            "xmlns:lib=\"clr-namespace:MyLib.Controls;assembly=MyLib\" " +
            "xmlns:mah=\"http://metro.mahapps.com/winfx/xaml/controls\" " +
            "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\" " +
            "xmlns:core=\"clr-namespace:System;assembly=System.Runtime\" " +
            "xmlns:po=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation/options\" " +
            "xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"";

        private static XElement Element(string body)
        {
            XDocument doc = XDocument.Parse(Xaml.Dictionary(body, Namespaces));
            return doc.Root.Elements().Single();
        }

        [TestMethod]
        [DataRow("<local:Book x:Key=\"A\" />", "local:Book")]
        [DataRow("<DataTemplate x:Key=\"A\" DataType=\"{x:Type local:Book}\" />", "local:Book")]
        [DataRow("<Style x:Key=\"A\" TargetType=\"local:Fancy\" />", "local:Fancy")]
        [DataRow("<Style x:Key=\"A\" TargetType=\"Button\"><Setter Property=\"local:Help.Text\" Value=\"x\" /></Style>", "local:Help.Text")]
        [DataRow("<RotateTransform x:Key=\"A\" Angle=\"{x:Static local:Angles.Right}\" />", "local:Angles.Right")]
        [DataRow("<ControlTemplate x:Key=\"A\" TargetType=\"Button\"><Grid><local:Rating /></Grid></ControlTemplate>", "local:Rating")]
        [DataRow("<Border x:Key=\"A\" local:Help.Text=\"x\" />", "local:Help.Text")]
        [DataRow("<lib:Gauge x:Key=\"A\" />", "lib:Gauge")]
        [DataRow("<mah:MetroWindow x:Key=\"A\" />", "mah:MetroWindow")]
        [DataRow("<SolidColorBrush x:Key=\"A\" Color=\"Red\" />", null)]
        [DataRow("<sys:Double x:Key=\"A\">1</sys:Double>", null)]
        [DataRow("<core:Double x:Key=\"A\">1</core:Double>", null)]
        [DataRow("<RotateTransform x:Key=\"A\" Angle=\"{x:Static sys:Math.PI}\" />", null)]
        [DataRow("<SolidColorBrush x:Key=\"A\" po:Freeze=\"True\" Color=\"Red\" />", null)]
        [DataRow("<Border x:Key=\"A\" d:DesignWidth=\"100\" />", null)]
        [DataRow("<TextBlock x:Key=\"A\" Text=\"Tip:Click here\" />", null)]
        [DataRow("<TextBlock x:Key=\"A\" Text=\"Ready at 10:30\" />", null)]
        [DataRow("<Image x:Key=\"A\" Source=\"pack://application:,,,/Images/x.png\" />", null)]
        [DataRow("<Image x:Key=\"A\" Source=\"file:///C:/x.png\" />", null)]
        [DataRow("<Storyboard x:Key=\"A\"><DoubleAnimation Duration=\"0:0:1\" /></Storyboard>", null)]
        public void FindUnloadableType(string body, string expected)
        {
            Assert.AreEqual(expected, PreviewXamlBuilder.FindUnloadableType(Element(body)));
        }

        [TestMethod]
        [DataRow("", true)]
        [DataRow("http://schemas.microsoft.com/winfx/2006/xaml/presentation", true)]
        [DataRow("http://schemas.microsoft.com/winfx/2006/xaml", true)]
        [DataRow("clr-namespace:System;assembly=mscorlib", true)]
        [DataRow("clr-namespace:System;assembly=System.Runtime", true)]
        [DataRow("clr-namespace:System.Windows.Controls;assembly=PresentationFramework", true)]
        [DataRow("clr-namespace:Microsoft.VisualStudio.TestTools.UnitTesting;assembly=MSTest.TestFramework", true)] // already loaded
        [DataRow("clr-namespace:MyApp", false)]
        [DataRow("clr-namespace:MyLib;assembly=MyLib", false)]
        [DataRow("http://metro.mahapps.com/winfx/xaml/controls", false)]
        public void IsLoadableNamespace(string ns, bool expected)
        {
            Assert.AreEqual(expected, PreviewXamlBuilder.IsLoadableNamespace(ns));
        }

        [TestMethod]
        public void Dependency_WithOwnType_MakesWholePreviewUnloadable()
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("Icons.xaml", Xaml.Dictionary(
                    "<local:IconSize x:Key=\"Size\" />" +
                    "<Border x:Key=\"Box\" Tag=\"{StaticResource Size}\" />",
                    "xmlns:local=\"clr-namespace:MyApp\""));

                var index = ResourceIndex.ForFile(file);
                PreviewXaml preview = PreviewXamlBuilder.Build(index, index.Find("Box", file, CancellationToken.None).Single(), CancellationToken.None);
                Assert.AreEqual("local:IconSize", preview.UnloadableType);
            }
        }

        [TestMethod]
        [DataRow("BookTemplate", "local:Book")]
        [DataRow("FancyTemplate", "local:FancyControl")]
        [DataRow("FancyStyle", "local:FancyControl")]
        [DataRow("SearchBox", null)]
        [DataRow("DangerButton", null)]
        [DataRow("HeaderFontSize", null)] // sys: with assembly=System.Runtime
        [DataRow("Report", null)]
        public void SampleApp_UnloadableType(string key, string expected)
        {
            string main = Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            var index = ResourceIndex.ForFile(main);
            PreviewXaml preview = PreviewXamlBuilder.Build(index, index.Find(key, main, CancellationToken.None).First(), CancellationToken.None);
            Assert.AreEqual(expected, preview.UnloadableType);
        }
    }

    /// <summary>Renders every SampleApp resource and checks that no exception is thrown, not even a caught one.</summary>
    [TestClass]
    public class NoFirstChanceExceptionTests
    {
        private static string MainWindow => Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");

        public static IEnumerable<object[]> AllSampleKeys()
        {
            XNamespace x = Xaml.X;
            return Directory.GetFiles(Path.Combine(RepoPaths.SampleApp, "Resources"), "*.xaml")
                .SelectMany(file => XDocument.Load(file).Root.Elements())
                .Select(e => (string)e.Attribute(x + "Key"))
                .Where(k => k != null)
                .Distinct()
                .Select(k => new object[] { k });
        }

        [TestMethod]
        [DynamicData(nameof(AllSampleKeys))]
        public void Render_ThrowsNothing(string key)
        {
            Localizer.SetCulture(CultureInfo.GetCultureInfo("en"));

            // Same steps as ResourceQuickInfoSource, without VS.
            var index = ResourceIndex.ForFile(MainWindow);
            ResourceEntry entry = index.Find(key, MainWindow, CancellationToken.None).First();
            StyleInfo style = StyleInfoBuilder.IsStyle(entry) ? StyleInfoBuilder.Build(index, entry, CancellationToken.None) : null;
            TemplateTree tree = TemplateTreeBuilder.IsTemplate(entry) ? TemplateTreeBuilder.Build(entry.Element) : null;
            PreviewXaml xaml = PreviewXamlBuilder.Build(index, entry, CancellationToken.None);
            var data = new ResourcePreviewData(entry.Key, entry.ElementTypeName, xaml.Xaml, xaml.MissingKeys, null, style, tree, xaml.UnloadableType);

            var thrown = new List<string>();
            Sta.Run(() =>
            {
                int thread = Thread.CurrentThread.ManagedThreadId;
                EventHandler<FirstChanceExceptionEventArgs> handler = (sender, e) =>
                {
                    if (Thread.CurrentThread.ManagedThreadId == thread)
                    {
                        thrown.Add(e.Exception.GetType().Name + ": " + e.Exception.Message);
                    }
                };

                AppDomain.CurrentDomain.FirstChanceException += handler;
                try
                {
                    PreviewRenderer.Render(data);
                }
                finally
                {
                    AppDomain.CurrentDomain.FirstChanceException -= handler;
                }
            });

            Assert.AreEqual(0, thrown.Count, string.Join(Environment.NewLine, thrown));
        }
    }

    [TestClass]
    public class ColorParserTests
    {
        [TestMethod]
        [DataRow("#F00", 0xFF, 0xFF, 0x00, 0x00)]
        [DataRow("#8F00", 0x88, 0xFF, 0x00, 0x00)]
        [DataRow("#1E1E1E", 0xFF, 0x1E, 0x1E, 0x1E)]
        [DataRow("#801E1E1E", 0x80, 0x1E, 0x1E, 0x1E)]
        [DataRow("red", 0xFF, 0xFF, 0x00, 0x00)]
        [DataRow(" CornflowerBlue ", 0xFF, 0x64, 0x95, 0xED)]
        [DataRow("Transparent", 0x00, 0xFF, 0xFF, 0xFF)]
        public void Valid(string text, int a, int r, int g, int b)
        {
            Assert.IsTrue(ColorParser.TryParse(text, out Color color));
            Assert.AreEqual(Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b), color);
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("#")]
        [DataRow("#12")]
        [DataRow("#12345")]
        [DataRow("#GGGGGG")]
        [DataRow("#+1234")]
        [DataRow("NoSuchColor")]
        [DataRow("{StaticResource AccentBrush}")]
        public void Invalid(string text)
        {
            Assert.IsFalse(ColorParser.TryParse(text, out _));
        }

        [TestMethod]
        public void SameAsWpfColorConverter_ForAllNamedColors()
        {
            foreach (var property in typeof(Colors).GetProperties())
            {
                Assert.IsTrue(ColorParser.TryParse(property.Name, out Color color), property.Name);
                Assert.AreEqual((Color)ColorConverter.ConvertFromString(property.Name), color, property.Name);
            }
        }
    }
}
