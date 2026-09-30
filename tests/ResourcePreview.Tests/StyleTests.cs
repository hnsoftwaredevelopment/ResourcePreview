using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Localization;
using ResourcePreview.Preview;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>Reads a Style from the XML, using the styles in samples\SampleApp\Resources\Styles.xaml.</summary>
    [TestClass]
    public class StyleInfoBuilderTests
    {
        private static string MainWindow => Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");

        private static StyleInfo SampleStyle(string key)
        {
            var index = ResourceIndex.ForFile(MainWindow);
            ResourceEntry entry = index.Find(key, MainWindow, CancellationToken.None).Single();
            Assert.IsTrue(StyleInfoBuilder.IsStyle(entry), $"{key} is not a Style");
            return StyleInfoBuilder.Build(index, entry, CancellationToken.None);
        }

        private static StyleInfo StyleFrom(string key, string body)
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("Styles.xaml", Xaml.Dictionary(body, "xmlns:local=\"clr-namespace:MyApp\""));
                var index = ResourceIndex.ForFile(file);
                return StyleInfoBuilder.Build(index, index.Find(key, file, CancellationToken.None).Single(), CancellationToken.None);
            }
        }

        private static SetterInfo Setter(StyleInfo style, string property) => style.Setters.Single(s => s.Property == property);

        // ---------- TargetType and BasedOn ----------

        [TestMethod]
        [DataRow("{x:Type Button}", "Button")]
        [DataRow("{x:Type TypeName=Button}", "Button")]
        [DataRow("Button", "Button")]
        [DataRow("{x:Type local:FancyControl}", "local:FancyControl")]
        [DataRow("  Button  ", "Button")]
        public void TargetType_AllNotations(string attribute, string expected)
        {
            var style = XElement.Parse($"<Style TargetType=\"{attribute}\" />");
            Assert.AreEqual(expected, StyleInfoBuilder.ReadTargetType(style));
        }

        [TestMethod]
        public void TargetType_Missing_IsNull()
        {
            Assert.IsNull(StyleInfoBuilder.ReadTargetType(XElement.Parse("<Style />")));
        }

        [TestMethod]
        public void BasedOnImplicitStyle_ShowsTypeButDoesNotInherit()
        {
            var style = StyleFrom("S", "<Style x:Key=\"S\" TargetType=\"Button\" BasedOn=\"{x:Type Button}\"><Setter Property=\"Width\" Value=\"10\" /></Style>");
            Assert.AreEqual("Button", style.BasedOn);
            Assert.AreEqual(1, style.Setters.Count);
        }

        [TestMethod]
        public void BasedOnUnknownKey_OwnSettersOnly()
        {
            var style = StyleFrom("S", "<Style x:Key=\"S\" BasedOn=\"{StaticResource Missing}\"><Setter Property=\"Width\" Value=\"10\" /></Style>");
            Assert.AreEqual("Missing", style.BasedOn);
            Assert.AreEqual(1, style.Setters.Count);
        }

        [TestMethod]
        public void BasedOnCycle_DoesNotHang()
        {
            var style = StyleFrom("A",
                "<Style x:Key=\"A\" BasedOn=\"{StaticResource B}\"><Setter Property=\"Width\" Value=\"1\" /></Style>" +
                "<Style x:Key=\"B\" BasedOn=\"{StaticResource A}\"><Setter Property=\"Height\" Value=\"2\" /></Style>");

            CollectionAssert.AreEqual(new[] { "Width", "Height" }, style.Setters.Select(s => s.Property).ToArray());
        }

        // ---------- Setters: order, inheritance, value kinds ----------

        [TestMethod]
        public void PrimaryButton_OwnSettersFirst_ThenInheritedWithoutOverridden()
        {
            StyleInfo style = SampleStyle("PrimaryButton");

            Assert.AreEqual("Button", style.TargetType);
            Assert.AreEqual("BaseButton", style.BasedOn);
            CollectionAssert.AreEqual(
                new[] { "Foreground", "FontSize", "Tag", "Template", "Padding", "Background" },
                style.Setters.Select(s => s.Property).ToArray(),
                "FontSize from BaseButton is overridden and must appear only once");
            Assert.AreEqual("15", Setter(style, "FontSize").ValueText, "the value that actually applies");
            Assert.IsNull(Setter(style, "Foreground").InheritedFrom);
            Assert.AreEqual("BaseButton", Setter(style, "Padding").InheritedFrom);
        }

        [TestMethod]
        public void PrimaryButton_ValueKinds()
        {
            StyleInfo style = SampleStyle("PrimaryButton");

            SetterInfo foreground = Setter(style, "Foreground");
            Assert.AreEqual(SetterValueKind.Color, foreground.Kind);
            Assert.AreEqual("White", foreground.ColorText);

            SetterInfo tag = Setter(style, "Tag");
            Assert.AreEqual(SetterValueKind.ResourceReference, tag.Kind);
            Assert.AreEqual("Check", tag.ValueText);
            Assert.AreEqual("GeometryGroup", tag.ResourceTypeName);
            Assert.IsNotNull(tag.ResourceXaml, "XAML needed for the mini icon");

            SetterInfo template = Setter(style, "Template");
            Assert.AreEqual(SetterValueKind.Element, template.Kind);
            Assert.AreEqual("ControlTemplate", template.ValueText);

            SetterInfo background = Setter(style, "Background");
            Assert.AreEqual(SetterValueKind.ResourceReference, background.Kind);
            Assert.AreEqual("#FF002BBD", background.ColorText, "color of AccentBrush from Brushes.xaml");
        }

        [TestMethod]
        public void DangerButton_ThreeLevelChain()
        {
            StyleInfo style = SampleStyle("DangerButton");

            var rows = style.Setters.Select(s => $"{s.Property}@{s.InheritedFrom ?? "self"}").ToArray();
            CollectionAssert.AreEqual(new[]
            {
                "Background@self",
                "Foreground@PrimaryButton", "FontSize@PrimaryButton", "Tag@PrimaryButton", "Template@PrimaryButton",
                "Padding@BaseButton",
            }, rows);

            SetterInfo background = Setter(style, "Background");
            Assert.AreEqual(SetterValueKind.Element, background.Kind);
            Assert.AreEqual("SolidColorBrush", background.ValueText);
            Assert.AreEqual("#FFC62828", background.ColorText);
        }

        [TestMethod]
        public void StyleForCustomControl_ReadsFromXml()
        {
            StyleInfo style = SampleStyle("FancyStyle");
            Assert.AreEqual("local:FancyControl", style.TargetType);

            SetterInfo border = Setter(style, "BorderBrush");
            Assert.AreEqual(SetterValueKind.ResourceReference, border.Kind, "DynamicResource also counts as a reference");
            Assert.AreEqual("#FFE8A317", border.ColorText);
        }

        [TestMethod]
        public void SettersInsideTriggers_AreNotStyleSetters()
        {
            Assert.IsFalse(SampleStyle("PrimaryButton").Setters.Any(s => s.Property == "Opacity"));
        }

        [TestMethod]
        public void ColorWordInTextProperty_StaysText()
        {
            var style = StyleFrom("S", "<Style x:Key=\"S\"><Setter Property=\"Content\" Value=\"Red\" /><Setter Property=\"Background\" Value=\"Red\" /></Style>");
            Assert.AreEqual(SetterValueKind.Text, Setter(style, "Content").Kind);
            Assert.AreEqual(SetterValueKind.Color, Setter(style, "Background").Kind);
        }

        [TestMethod]
        public void OtherMarkupExtensions_AreText()
        {
            var style = StyleFrom("S", "<Style x:Key=\"S\"><Setter Property=\"Width\" Value=\"{Binding ActualWidth}\" /></Style>");
            SetterInfo width = Setter(style, "Width");
            Assert.AreEqual(SetterValueKind.Text, width.Kind);
            Assert.AreEqual("{Binding ActualWidth}", width.ValueText);
        }

        [TestMethod]
        public void SetterWithTargetName_IsSkipped()
        {
            // TargetName belongs in a template trigger; in a style setter it is invalid.
            var style = StyleFrom("S", "<Style x:Key=\"S\"><Setter TargetName=\"Border\" Property=\"Width\" Value=\"1\" /></Style>");
            Assert.AreEqual(0, style.Setters.Count);
        }

        [TestMethod]
        [DataRow("#FFF", true)]
        [DataRow("#80FF0000", true)]
        [DataRow("Red", true)]
        [DataRow("SteelBlue", true)]
        [DataRow("Reddish", false)]
        [DataRow("#12345", false)]
        [DataRow("{StaticResource X}", false)]
        [DataRow("", false)]
        public void TryReadLiteralColor(string value, bool expected)
        {
            Assert.AreEqual(expected, StyleInfoBuilder.TryReadLiteralColor(value, out _));
        }

        // ---------- Triggers ----------

        [TestMethod]
        public void PrimaryButton_TriggerNames()
        {
            CollectionAssert.AreEqual(
                new[] { "IsMouseOver", "IsPressed", "IsBusy", "IsMouseOver + IsFocused" },
                SampleStyle("PrimaryButton").Triggers.ToArray());
        }

        [TestMethod]
        public void Triggers_OnlyOwn_NotFromBaseStyle()
        {
            Assert.AreEqual(0, SampleStyle("DangerButton").Triggers.Count);
        }

        [TestMethod]
        public void EventAndMultiDataTriggers()
        {
            var style = StyleFrom("S",
                "<Style x:Key=\"S\"><Style.Triggers>" +
                "<EventTrigger RoutedEvent=\"MouseEnter\" />" +
                "<MultiDataTrigger><MultiDataTrigger.Conditions>" +
                "<Condition Binding=\"{Binding IsBusy}\" Value=\"True\" /><Condition Binding=\"{Binding Path=HasError}\" Value=\"True\" />" +
                "</MultiDataTrigger.Conditions></MultiDataTrigger>" +
                "</Style.Triggers></Style>");

            CollectionAssert.AreEqual(new[] { "MouseEnter", "IsBusy + HasError" }, style.Triggers.ToArray());
        }

        [TestMethod]
        [DataRow("{Binding IsBusy}", "IsBusy")]
        [DataRow("{Binding Path=IsBusy, Mode=OneWay}", "IsBusy")]
        [DataRow("{Binding RelativeSource={RelativeSource Self}, Path=IsBusy}", "IsBusy")]
        [DataRow("{Binding Order.Total}", "Order.Total")]
        [DataRow("{Binding Converter={StaticResource C}}", "Binding")]
        [DataRow("{Binding}", "Binding")]
        public void BindingPath(string binding, string expected)
        {
            Assert.AreEqual(expected, StyleInfoBuilder.BindingPath(binding));
        }
    }

    /// <summary>How a Style is shown in the tooltip.</summary>
    [TestClass]
    public class StylePreviewRendererTests
    {
        [TestInitialize]
        public void Setup() => Localizer.SetCulture(CultureInfo.GetCultureInfo("en"));

        [TestCleanup]
        public void Cleanup() => Localizer.SetCulture(CultureInfo.GetCultureInfo("en"));

        private static StyleInfo Sample(string key)
        {
            string mainWindow = Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            var index = ResourceIndex.ForFile(mainWindow);
            return StyleInfoBuilder.Build(index, index.Find(key, mainWindow, CancellationToken.None).Single(), CancellationToken.None);
        }

        /// <summary>All texts and the number of color swatches in the rendered tree.</summary>
        private static (List<string> Texts, int Swatches) Inspect(UIElement root)
        {
            var texts = new List<string>();
            int swatches = 0;

            void Walk(DependencyObject node)
            {
                switch (node)
                {
                    case TextBlock text:
                        texts.Add(text.Text);
                        break;
                    case Border border when border.Child == null && border.Width == 12:
                        swatches++;
                        break;
                    case Border border:
                        Walk(border.Child);
                        break;
                    case Panel panel:
                        foreach (UIElement child in panel.Children)
                        {
                            Walk(child);
                        }

                        break;
                }
            }

            Walk(root);
            return (texts, swatches);
        }

        [TestMethod]
        public void PrimaryButton_HeaderRowsSeparatorAndTriggers()
        {
            StyleInfo style = Sample("PrimaryButton");
            var (texts, swatches) = Sta.Run(() => Inspect(StylePreviewRenderer.Render(style)));

            CollectionAssert.Contains(texts, "Button · BasedOn BaseButton");
            CollectionAssert.Contains(texts, "inherited from BaseButton");
            CollectionAssert.Contains(texts, "Background");
            CollectionAssert.Contains(texts, "AccentBrush (#FF002BBD)");
            CollectionAssert.Contains(texts, "Check (GeometryGroup)");
            CollectionAssert.Contains(texts, "ControlTemplate");
            CollectionAssert.Contains(texts, "Triggers (4): IsMouseOver, IsPressed, IsBusy, IsMouseOver + IsFocused");
            Assert.AreEqual(2, swatches, "White (Foreground) and AccentBrush (Background)");
        }

        [TestMethod]
        public void SeparatorOrder_OwnThenEachBaseStyle()
        {
            StyleInfo style = Sample("DangerButton");
            var (texts, _) = Sta.Run(() => Inspect(StylePreviewRenderer.Render(style)));

            int primary = texts.IndexOf("inherited from PrimaryButton");
            int baseButton = texts.IndexOf("inherited from BaseButton");
            Assert.IsTrue(primary > 0 && baseButton > primary, string.Join(" | ", texts));
            Assert.IsTrue(texts.IndexOf("SolidColorBrush (#FFC62828)") < primary, "own setter is above the separator");
        }

        [TestMethod]
        public void NoSetters_ShowsMessage()
        {
            var style = new StyleInfo("Button", null, Array.Empty<SetterInfo>(), Array.Empty<string>());
            var (texts, _) = Sta.Run(() => Inspect(StylePreviewRenderer.Render(style)));
            CollectionAssert.Contains(texts, "No setters");
        }

        [TestMethod]
        public void DuplicateTriggerNames_CountAllNameOnce()
        {
            var style = new StyleInfo("Button", null, Array.Empty<SetterInfo>(), new[] { "IsMouseOver", "IsMouseOver", "IsPressed" });
            var (texts, _) = Sta.Run(() => Inspect(StylePreviewRenderer.Render(style)));
            CollectionAssert.Contains(texts, "Triggers (3): IsMouseOver, IsPressed");
        }

        [TestMethod]
        public void Translated_InDutch()
        {
            Localizer.SetCulture(CultureInfo.GetCultureInfo("nl"));
            StyleInfo style = Sample("PrimaryButton");
            var (texts, _) = Sta.Run(() => Inspect(StylePreviewRenderer.Render(style)));
            CollectionAssert.Contains(texts, "geërfd van BaseButton");
        }

        [TestMethod]
        public void PreviewRenderer_RoutesStyleToStyleRenderer()
        {
            StyleInfo style = Sample("BaseButton");
            var data = new ResourcePreviewData("BaseButton", "Style", null, Array.Empty<string>(), navigate: null, style: style);
            var (texts, _) = Sta.Run(() => Inspect(PreviewRenderer.Render(data)));
            CollectionAssert.Contains(texts, "Padding");
        }

        [TestMethod]
        public void StyleTable_WithNavigate_IsClickable()
        {
            StyleInfo style = Sample("BaseButton");
            int clicks = 0;
            var data = new ResourcePreviewData("BaseButton", "Style", null, Array.Empty<string>(), navigate: () => clicks++, style: style);

            Sta.Run(() =>
            {
                var panel = (Panel)PreviewRenderer.Render(data);
                Assert.AreEqual(System.Windows.Input.Cursors.Hand, panel.Cursor);
                Assert.IsNotNull(panel.Background, "without a background, empty space does not catch the click");

                panel.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                });
            });

            Assert.AreEqual(1, clicks);
        }

        [TestMethod]
        [DataRow("Text", "8,4", null, null, "8,4")]
        [DataRow("Color", "White", "White", null, "White")]
        [DataRow("ResourceReference", "AccentBrush", "#FF002BBD", "SolidColorBrush", "AccentBrush (#FF002BBD)")]
        [DataRow("ResourceReference", "Renew", null, "GeometryGroup", "Renew (GeometryGroup)")]
        [DataRow("ResourceReference", "Missing", null, null, "Missing")]
        [DataRow("Element", "ControlTemplate", null, null, "ControlTemplate")]
        public void DescribeValue(string kind, string value, string color, string type, string expected)
        {
            // A string instead of the enum: a public test method cannot take an internal type as a parameter.
            var setter = new SetterInfo("P", value, (SetterValueKind)Enum.Parse(typeof(SetterValueKind), kind)) { ColorText = color, ResourceTypeName = type };
            Assert.AreEqual(expected, StylePreviewRenderer.DescribeValue(setter));
        }
    }
}
