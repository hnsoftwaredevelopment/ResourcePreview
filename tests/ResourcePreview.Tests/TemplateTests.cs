using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Localization;
using ResourcePreview.Options;
using ResourcePreview.Preview;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>TemplateTreeBuilder: the tree from the XML, without WPF.</summary>
    [TestClass]
    public class TemplateTreeBuilderTests
    {
        private static readonly XNamespace X = Xaml.X;

        private static XElement Element(string body, string key = "T")
        {
            XDocument doc = XDocument.Parse(Xaml.Dictionary(body, "xmlns:local=\"clr-namespace:MyApp\""));
            return doc.Root.Elements().Single(e => (string)e.Attribute(X + "Key") == key);
        }

        private static string[] Lines(TemplateTree tree) =>
            tree.Lines.Select(l => new string(' ', l.Depth * 2) + l.Element + (l.Details == null ? string.Empty : "  " + l.Details)).ToArray();

        [TestMethod]
        public void DataTemplate_ElementsBindingsAndDataType()
        {
            TemplateTree tree = TemplateTreeBuilder.Build(Element(
                "<DataTemplate x:Key=\"T\" DataType=\"{x:Type local:Book}\">" +
                "<Border Padding=\"6\"><StackPanel>" +
                "<Image Source=\"{Binding Cover}\" Width=\"16\" />" +
                "<TextBlock Text=\"{Binding Title}\" />" +
                "</StackPanel></Border></DataTemplate>"));

            Assert.AreEqual("DataTemplate", tree.TemplateKind);
            Assert.AreEqual("local:Book", tree.DataType);
            Assert.IsNull(tree.TargetType);
            CollectionAssert.AreEqual(new[]
            {
                "Border",
                "  StackPanel",
                "    Image  Source = {Binding Cover}",
                "    TextBlock  Text = {Binding Title}",
            }, Lines(tree));
            Assert.AreEqual(0, tree.HiddenCount);
        }

        [TestMethod]
        public void PropertyElements_AreSkipped()
        {
            TemplateTree tree = TemplateTreeBuilder.Build(Element(
                "<ControlTemplate x:Key=\"T\" TargetType=\"Button\">" +
                "<Grid><Grid.ColumnDefinitions><ColumnDefinition /></Grid.ColumnDefinitions>" +
                "<Border><Border.Background><SolidColorBrush Color=\"Red\" /></Border.Background></Border>" +
                "</Grid>" +
                "<ControlTemplate.Triggers><Trigger Property=\"IsEnabled\" Value=\"False\" /></ControlTemplate.Triggers>" +
                "</ControlTemplate>"));

            Assert.AreEqual("Button", tree.TargetType);
            CollectionAssert.AreEqual(new[] { "Grid", "  Border" }, Lines(tree));
        }

        [TestMethod]
        public void Details_TemplateBindingNameLiteralsAndTextContent()
        {
            TemplateTree tree = TemplateTreeBuilder.Build(Element(
                "<ControlTemplate x:Key=\"T\">" +
                "<Border x:Name=\"Chrome\" Background=\"{TemplateBinding Background}\" Margin=\"4\">" +
                "<StackPanel>" +
                "<TextBlock Text=\"Title:\" /><TextBlock>Book</TextBlock><Label Content=\"{StaticResource X}\" />" +
                "<local:Rating Name=\"Stars\" Value=\"{Binding Score, Mode=OneWay}\" />" +
                "</StackPanel></Border></ControlTemplate>"));

            CollectionAssert.AreEqual(new[]
            {
                "Border  x:Name = Chrome · Background = {TemplateBinding Background}",
                "  StackPanel",
                "    TextBlock  Text = \"Title:\"",
                "    TextBlock  \"Book\"",
                "    Label",
                "    local:Rating  Name = Stars · Value = {Binding Score, Mode=OneWay}",
            }, Lines(tree));
        }

        [TestMethod]
        public void LargeTemplate_StopsAt20Lines_AndCountsTheRest()
        {
            string items = string.Concat(Enumerable.Range(1, 30).Select(i => $"<TextBlock Text=\"{{Binding P{i}}}\" />"));
            TemplateTree tree = TemplateTreeBuilder.Build(Element($"<DataTemplate x:Key=\"T\"><StackPanel>{items}</StackPanel></DataTemplate>"));

            Assert.AreEqual(TemplateTreeBuilder.MaxLines, tree.Lines.Count);
            Assert.AreEqual(31 - TemplateTreeBuilder.MaxLines, tree.HiddenCount, "StackPanel + 30 TextBlocks = 31 elements");
        }

        [TestMethod]
        public void DeepTemplate_StopsAtMaxDepth()
        {
            int levels = TemplateTreeBuilder.MaxDepth + 3;
            string open = string.Concat(Enumerable.Repeat("<Border>", levels));
            string close = string.Concat(Enumerable.Repeat("</Border>", levels));
            TemplateTree tree = TemplateTreeBuilder.Build(Element($"<DataTemplate x:Key=\"T\">{open}{close}</DataTemplate>"));

            Assert.AreEqual(TemplateTreeBuilder.MaxDepth, tree.Lines.Max(l => l.Depth));
            Assert.AreEqual(levels - TemplateTreeBuilder.MaxDepth - 1, tree.HiddenCount);
        }

        [TestMethod]
        public void EmptyTemplate_HasNoLines()
        {
            Assert.AreEqual(0, TemplateTreeBuilder.Build(Element("<DataTemplate x:Key=\"T\" />")).Lines.Count);
        }

        [TestMethod]
        [DataRow("<DataTemplate x:Key=\"T\" />", true)]
        [DataRow("<HierarchicalDataTemplate x:Key=\"T\" />", true)]
        [DataRow("<ControlTemplate x:Key=\"T\" />", true)]
        [DataRow("<ItemsPanelTemplate x:Key=\"T\" />", true)]
        [DataRow("<Style x:Key=\"T\" />", false)]
        [DataRow("<Geometry x:Key=\"T\">M0,0</Geometry>", false)]
        public void IsTemplate(string body, bool expected)
        {
            Assert.AreEqual(expected, TemplateTreeBuilder.IsTemplate(Element(body)));
        }

        [TestMethod]
        public void SampleBookTemplate()
        {
            string main = Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            ResourceEntry entry = ResourceIndex.ForFile(main).Find("BookTemplate", main, CancellationToken.None).Single();
            TemplateTree tree = TemplateTreeBuilder.Build(entry.Element);

            Assert.AreEqual("local:Book", tree.DataType);
            CollectionAssert.AreEqual(new[]
            {
                "Border",
                "  Grid",
                "    Path",
                "    StackPanel",
                "      TextBlock  x:Name = TitleText · Text = {Binding Title}",
                "      TextBlock  Text = {Binding Author, StringFormat='by {0}'}",
                "      TextBlock  \"Book\"",
            }, Lines(tree));
        }
    }

    /// <summary>Live samples and template rendering. Uses WPF, so it runs on an STA thread.</summary>
    [TestClass]
    public class LiveSampleAndTemplateRendererTests
    {
        private static string MainWindow => Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");

        [TestInitialize]
        public void Setup()
        {
            Localizer.SetCulture(CultureInfo.GetCultureInfo("en"));
        }

        /// <summary>The data that ResourceQuickInfoSource creates for this key.</summary>
        private static ResourcePreviewData SampleData(string key, Action navigate = null)
        {
            var index = ResourceIndex.ForFile(MainWindow);
            ResourceEntry entry = index.Find(key, MainWindow, CancellationToken.None).First();
            StyleInfo style = StyleInfoBuilder.IsStyle(entry) ? StyleInfoBuilder.Build(index, entry, CancellationToken.None) : null;
            TemplateTree tree = TemplateTreeBuilder.IsTemplate(entry) ? TemplateTreeBuilder.Build(entry.Element) : null;
            PreviewXaml xaml = PreviewXamlBuilder.Build(index, entry, CancellationToken.None);
            return new ResourcePreviewData(entry.Key, entry.ElementTypeName, xaml.Xaml, xaml.MissingKeys, navigate, style, tree, xaml.UnloadableType);
        }

        private static ResourcePreviewData InlineData(string body, string elementType)
        {
            string xaml = Xaml.Dictionary(body, "xmlns:local=\"clr-namespace:MyApp\"");
            XElement element = XDocument.Parse(xaml).Root.Elements().Single();
            return new ResourcePreviewData("T", elementType, xaml, Array.Empty<string>(), null, null, TemplateTreeBuilder.Build(element), PreviewXamlBuilder.FindUnloadableType(element));
        }

        /// <summary>All elements under root in the logical tree, which exists without a layout pass.</summary>
        private static IEnumerable<DependencyObject> All(DependencyObject root)
        {
            yield return root;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject d)
                {
                    foreach (DependencyObject descendant in All(d))
                    {
                        yield return descendant;
                    }
                }
            }
        }

        private static string[] Texts(DependencyObject root) => All(root).OfType<TextBlock>().Select(t => t.Text).ToArray();

        // ---------- Style ----------

        [TestMethod]
        public void Style_LiveSample_NormalAndDisabled()
        {
            Sta.Run(() =>
            {
                UIElement root = PreviewRenderer.Render(SampleData("SearchBox"));
                TextBox[] boxes = All(root).OfType<TextBox>().ToArray();

                Assert.AreEqual(2, boxes.Length);
                Assert.IsTrue(boxes[0].IsEnabled);
                Assert.IsFalse(boxes[1].IsEnabled);
                Assert.AreEqual("Sample", boxes[0].Text);
                Assert.AreEqual(140.0, boxes[0].MinWidth, "setter from the style is applied");
                Assert.IsFalse(boxes[0].IsHitTestVisible, "the sample ignores the mouse");

                string[] texts = Texts(root);
                CollectionAssert.Contains(texts, "Normal");
                CollectionAssert.Contains(texts, "Disabled");
                CollectionAssert.Contains(texts, "MinWidth", "the setter table is still shown");
            });
        }

        [TestMethod]
        public void Style_BasedOnChain_SampleUsesWholeChain()
        {
            // DangerButton → PrimaryButton → BaseButton. Padding comes from BaseButton.
            Sta.Run(() =>
            {
                Button button = All(PreviewRenderer.Render(SampleData("DangerButton"))).OfType<Button>().First();
                Assert.AreEqual(new Thickness(8, 4, 8, 4), button.Padding);
                Assert.AreEqual("Sample", button.Content);
            });
        }

        [TestMethod]
        public void Style_ForOwnControl_ShowsReasonAndKeepsTable()
        {
            Sta.Run(() =>
            {
                string[] texts = Texts(PreviewRenderer.Render(SampleData("FancyStyle")));
                CollectionAssert.Contains(texts, "No live sample: local:FancyControl is not a WPF type, so Visual Studio can't load it.");
                CollectionAssert.Contains(texts, "Width");
            });
        }

        [TestMethod]
        public void Style_WithoutXaml_OnlyTable()
        {
            StyleInfo style = StyleInfoBuilder.Build(ResourceIndex.ForFile(MainWindow),
                ResourceIndex.ForFile(MainWindow).Find("BaseButton", MainWindow, CancellationToken.None).First(), CancellationToken.None);
            var data = new ResourcePreviewData("BaseButton", "Style", null, Array.Empty<string>(), null, style);

            Sta.Run(() =>
            {
                UIElement root = PreviewRenderer.Render(data);
                Assert.AreEqual(0, All(root).OfType<Button>().Count());
                Assert.IsFalse(Texts(root).Any(t => t.StartsWith("No live sample", StringComparison.Ordinal)));
            });
        }

        [TestMethod]
        public void Style_ForWindow_NoSampleAndNoReason()
        {
            Sta.Run(() =>
            {
                var style = new Style(typeof(Window));
                LiveSampleRenderer.Result result = LiveSampleRenderer.ForStyle(style);
                Assert.IsNull(result.Sample);
                Assert.IsNull(result.Problem);
            });
        }

        [TestMethod]
        public void SampleLabels_AreTranslated()
        {
            Localizer.SetCulture(CultureInfo.GetCultureInfo("nl"));
            Sta.Run(() =>
            {
                string[] texts = Texts(PreviewRenderer.Render(SampleData("SearchBox")));
                CollectionAssert.Contains(texts, "Normaal");
                CollectionAssert.Contains(texts, "Uitgeschakeld");
                Assert.AreEqual("Voorbeeld", All(PreviewRenderer.Render(SampleData("SearchBox"))).OfType<TextBox>().First().Text);
            });
        }

        // ---------- ControlTemplate ----------

        [TestMethod]
        public void ControlTemplate_Standard_ShowsTwoButtonsWithTemplate()
        {
            Sta.Run(() =>
            {
                UIElement root = PreviewRenderer.Render(SampleData("RoundButtonTemplate"));
                Button[] buttons = All(root).OfType<Button>().ToArray();

                Assert.AreEqual(2, buttons.Length);
                Assert.IsNotNull(buttons[0].Template);
                Assert.IsNotNull(buttons[0].Template.FindName("Chrome", buttons[0]), "the template is actually applied");
                Assert.IsFalse(buttons[1].IsEnabled);
                CollectionAssert.Contains(Texts(root), "TargetType Button");
            });
        }

        [TestMethod]
        public void ControlTemplate_ForOwnControl_ShowsTreeAndReason()
        {
            Sta.Run(() =>
            {
                string[] texts = Texts(PreviewRenderer.Render(SampleData("FancyTemplate")));
                CollectionAssert.Contains(texts, "TargetType local:FancyControl");
                CollectionAssert.Contains(texts, "Border");
                CollectionAssert.Contains(texts, "BorderBrush = {TemplateBinding BorderBrush}");
                CollectionAssert.Contains(texts, "No live sample: local:FancyControl is not a WPF type, so Visual Studio can't load it.");
            });
        }

        [TestMethod]
        public void ControlTemplate_WithoutTargetType_UsesContentControl()
        {
            Sta.Run(() =>
            {
                UIElement root = PreviewRenderer.Render(InlineData(
                    "<ControlTemplate x:Key=\"T\"><Border><ContentPresenter /></Border></ControlTemplate>", "ControlTemplate"));
                Assert.AreEqual(1, All(root).OfType<ContentControl>().Count(c => c.GetType() == typeof(ContentControl)), "no IsEnabled trigger: one sample");
            });
        }

        [TestMethod]
        public void Style_WithMouseTriggers_ShowsMouseOverAndPressed()
        {
            // DangerButton → PrimaryButton → BaseButton: triggers on IsMouseOver, IsPressed and IsBusy, not on IsEnabled.
            Sta.Run(() =>
            {
                UIElement root = PreviewRenderer.Render(SampleData("DangerButton"));
                Button[] buttons = All(root).OfType<Button>().ToArray();
                Assert.AreEqual(3, buttons.Length);
                CollectionAssert.AreEqual(new[] { 1.0, 0.8, 0.6 }, buttons.Select(b => b.Opacity).ToArray());

                string[] texts = Texts(root);
                CollectionAssert.IsSubsetOf(new[] { "Normal", "Mouse over", "Pressed" }, texts);
                CollectionAssert.DoesNotContain(texts, "Disabled");
            });
        }

        [TestMethod]
        public void ShowLiveSampleOff_StyleHasOnlyTable_TemplateShowsTree()
        {
            SettingsService.SetCurrentForTests(new ExtensionSettings(
                ExtensionLanguage.English, 5, 64, PreviewTiles.Both, Colors.Black, Colors.White, Colors.White, Colors.Black, showLiveSample: false));
            try
            {
                Sta.Run(() =>
                {
                    UIElement style = PreviewRenderer.Render(SampleData("SearchBox"));
                    Assert.AreEqual(0, All(style).OfType<TextBox>().Count());
                    CollectionAssert.Contains(Texts(style), "MinWidth");

                    UIElement template = PreviewRenderer.Render(SampleData("RoundButtonTemplate"));
                    Assert.AreEqual(0, All(template).OfType<Button>().Count());
                    CollectionAssert.Contains(Texts(template), "x:Name = Chrome · Background = {TemplateBinding Background}");
                });
            }
            finally
            {
                SettingsService.SetCurrentForTests(ExtensionSettings.Default);
            }
        }

        [TestMethod]
        [DataRow("<Style.Triggers><Trigger Property=\"IsEnabled\" Value=\"False\"><Setter Property=\"Opacity\" Value=\"0.5\" /></Trigger></Style.Triggers>", true)]
        [DataRow("<Style.Triggers><MultiTrigger><MultiTrigger.Conditions><Condition Property=\"IsEnabled\" Value=\"False\" /><Condition Property=\"IsFocused\" Value=\"True\" /></MultiTrigger.Conditions><Setter Property=\"Opacity\" Value=\"0.5\" /></MultiTrigger></Style.Triggers>", true)]
        [DataRow("<Style.Triggers><DataTrigger Binding=\"{Binding RelativeSource={RelativeSource Self}, Path=IsEnabled}\" Value=\"False\"><Setter Property=\"Opacity\" Value=\"0.5\" /></DataTrigger></Style.Triggers>", true)]
        [DataRow("<Setter Property=\"Template\"><Setter.Value><ControlTemplate TargetType=\"Button\"><Border /><ControlTemplate.Triggers><Trigger Property=\"IsEnabled\" Value=\"False\"><Setter Property=\"Opacity\" Value=\"0.5\" /></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>", true)]
        [DataRow("<Style.Triggers><Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter Property=\"Opacity\" Value=\"0.5\" /></Trigger></Style.Triggers>", false)]
        [DataRow("<Setter Property=\"Padding\" Value=\"4\" />", false)]
        public void HasIsEnabledTrigger(string body, bool expected)
        {
            string xaml = Xaml.Dictionary($"<Style x:Key=\"S\" TargetType=\"Button\">{body}</Style>");
            bool actual = Sta.Run(() => LiveSampleRenderer.HasIsEnabledTrigger((Style)((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml))["S"]));
            Assert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void HasIsEnabledTrigger_FollowsBasedOn()
        {
            string xaml = Xaml.Dictionary(
                "<Style x:Key=\"Base\" TargetType=\"Button\"><Style.Triggers><Trigger Property=\"IsEnabled\" Value=\"False\"><Setter Property=\"Opacity\" Value=\"0.5\" /></Trigger></Style.Triggers></Style>" +
                "<Style x:Key=\"S\" TargetType=\"Button\" BasedOn=\"{StaticResource Base}\"><Setter Property=\"Padding\" Value=\"4\" /></Style>");
            Assert.IsTrue(Sta.Run(() => LiveSampleRenderer.HasIsEnabledTrigger((Style)((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml))["S"])));
        }

        // ---------- DataTemplate and ItemsPanelTemplate ----------

        [TestMethod]
        public void DataTemplate_ShowsTreeOnly()
        {
            Sta.Run(() =>
            {
                UIElement root = PreviewRenderer.Render(InlineData(
                    "<DataTemplate x:Key=\"T\"><StackPanel><TextBlock Text=\"{Binding Title}\" /></StackPanel></DataTemplate>", "DataTemplate"));
                string[] texts = Texts(root);

                CollectionAssert.AreEqual(new[] { "StackPanel", "TextBlock", "Text = {Binding Title}" }, texts);
            });
        }

        [TestMethod]
        public void DataTemplate_WithOwnType_StillShowsTree_WithoutError()
        {
            // VS does not know local:Book, so XamlReader fails. The tree comes from the XML and still works.
            Sta.Run(() =>
            {
                string[] texts = Texts(PreviewRenderer.Render(SampleData("BookTemplate")));
                CollectionAssert.Contains(texts, "DataType local:Book");
                CollectionAssert.Contains(texts, "x:Name = TitleText · Text = {Binding Title}");
                Assert.IsFalse(texts.Any(t => t.Contains("Cannot") || t.StartsWith("No live sample", StringComparison.Ordinal)), string.Join(" | ", texts));
            });
        }

        [TestMethod]
        public void ItemsPanelTemplate_ShowsTree()
        {
            Sta.Run(() => CollectionAssert.Contains(Texts(PreviewRenderer.Render(SampleData("WrapPanelTemplate"))), "WrapPanel"));
        }

        [TestMethod]
        public void LargeTemplate_ShowsMoreElementsLine()
        {
            string items = string.Concat(Enumerable.Range(1, 25).Select(i => "<TextBlock />"));
            Sta.Run(() =>
            {
                string[] texts = Texts(PreviewRenderer.Render(InlineData($"<DataTemplate x:Key=\"T\"><StackPanel>{items}</StackPanel></DataTemplate>", "DataTemplate")));
                CollectionAssert.Contains(texts, "… 6 more elements");
            });
        }

        [TestMethod]
        public void EmptyTemplate_SaysSo()
        {
            Sta.Run(() => CollectionAssert.Contains(Texts(PreviewRenderer.Render(InlineData("<DataTemplate x:Key=\"T\" />", "DataTemplate"))), "Empty template"));
        }

        [TestMethod]
        public void TemplatePreview_WithNavigate_IsClickable()
        {
            int clicks = 0;
            Sta.Run(() =>
            {
                var panel = (Panel)PreviewRenderer.Render(SampleData("WrapPanelTemplate", () => clicks++));
                Assert.AreEqual(Cursors.Hand, panel.Cursor);
                panel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            });

            Assert.AreEqual(1, clicks);
        }

        // ---------- LiveSampleRenderer ----------

        [TestMethod]
        [DataRow(typeof(Button), true)]
        [DataRow(typeof(TextBox), true)]
        [DataRow(typeof(Control), true)]
        [DataRow(typeof(Border), true)]
        [DataRow(typeof(Panel), false)]
        [DataRow(typeof(Window), false)]
        [DataRow(typeof(Page), false)]
        [DataRow(typeof(ToolTip), false)]
        [DataRow(typeof(ContextMenu), false)]
        [DataRow(typeof(Popup), false)]
        [DataRow(typeof(string), false)]
        [DataRow(null, false)]
        public void CanCreate(Type type, bool expected)
        {
            Assert.AreEqual(expected, LiveSampleRenderer.CanCreate(type));
        }

        [TestMethod]
        public void FillContent_PerControlType()
        {
            Sta.Run(() =>
            {
                T Fill<T>(T element) where T : FrameworkElement
                {
                    LiveSampleRenderer.FillContent(element);
                    return element;
                }

                Assert.AreEqual("Sample", Fill(new Button()).Content);
                Assert.AreEqual("Sample", Fill(new GroupBox()).Header);
                Assert.AreEqual("Sample", Fill(new MenuItem()).Header);
                Assert.AreEqual("Sample", Fill(new TextBlock()).Text);
                Assert.AreEqual(0, Fill(new ComboBox()).SelectedIndex);
                Assert.AreEqual(2, Fill(new ListBox()).Items.Count);
                Assert.AreEqual(60.0, Fill(new Slider()).Value);
                Assert.AreEqual(60.0, Fill(new Border()).MinWidth);
                Assert.AreEqual("sample", Fill(new PasswordBox()).Password);
            });
        }

        [TestMethod]
        public void OwnTypeNote_NamesTheType()
        {
            StringAssert.Contains(LiveSampleRenderer.OwnTypeNote("local:FancyControl"), "local:FancyControl");
            Assert.IsNull(LiveSampleRenderer.OwnTypeNote(null));
        }

        [TestMethod]
        public void FirstLine_RemovesLinePositionOfGeneratedXaml()
        {
            Assert.AreEqual("'Failed to create a 'Type' from the text 'local:X'.'",
                LiveSampleRenderer.FirstLine(new InvalidOperationException("'Failed to create a 'Type' from the text 'local:X'.' Line number '1' and line position '264'.")));
        }

        [TestMethod]
        public void FirstLine_ShortensLongMessages()
        {
            string text = LiveSampleRenderer.FirstLine(new InvalidOperationException("line 1\nline 2"));
            Assert.AreEqual("line 1", text);
            Assert.AreEqual(150, LiveSampleRenderer.FirstLine(new InvalidOperationException(new string('a', 400))).Length);
        }
    }
}
