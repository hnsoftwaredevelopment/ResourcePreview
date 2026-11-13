using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Options;
using ResourcePreview.Preview;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>The rendered preview of a DataTemplate or ItemsPanelTemplate: own types as boxes, sample data for bindings.</summary>
    [TestClass]
    public class TemplateSampleTests
    {
        private const string Namespaces =
            "xmlns:local=\"clr-namespace:MyApp\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"";

        /// <summary>Writes the dictionary to a temp solution and prepares the template with this key.</summary>
        private static TemplateSample Prepare(string key, string body)
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("Templates.xaml", Xaml.Dictionary(body, Namespaces));
                var index = ResourceIndex.ForFile(file);
                return TemplateSampleBuilder.Prepare(index, index.Find(key, file, CancellationToken.None).Single(), CancellationToken.None);
            }
        }

        private static XElement Single(TemplateSample sample, string name) =>
            sample.Entry.Element.Descendants().Single(e => e.Name.LocalName == name);

        // --- Preparing the XML ---

        [TestMethod]
        public void OwnControl_BecomesABox_InTheSamePlace()
        {
            TemplateSample sample = Prepare("Row",
                "<DataTemplate x:Key=\"Row\"><Grid>" +
                "<local:Rating Grid.Column=\"1\" Margin=\"4\" Width=\"80\" Value=\"{Binding Stars}\" Foreground=\"Gold\" />" +
                "</Grid></DataTemplate>");

            XElement box = sample.Entry.Element.Element(XName.Get("Grid", Xaml.Presentation)).Elements().Single();
            Assert.AreEqual("Grid", box.Name.LocalName);
            Assert.AreEqual("1", (string)box.Attribute("Grid.Column"));
            Assert.AreEqual("4", (string)box.Attribute("Margin"));
            Assert.AreEqual("80", (string)box.Attribute("Width"));
            Assert.IsNull(box.Attribute("Foreground"));
            Assert.AreEqual("local:Rating", box.Descendants().Single(e => e.Name.LocalName == "TextBlock").Value);
            CollectionAssert.AreEqual(new[] { "local:Rating" }, sample.Placeholders.ToArray());
        }

        [TestMethod]
        public void NestedOwnControls_OnlyTheOuterBecomesABox()
        {
            TemplateSample sample = Prepare("Card",
                "<DataTemplate x:Key=\"Card\"><local:Card><local:Rating /></local:Card></DataTemplate>");

            CollectionAssert.AreEqual(new[] { "local:Card" }, sample.Placeholders.ToArray());
        }

        [TestMethod]
        public void OwnConverter_IsLeftOut_WpfConverterStays()
        {
            TemplateSample sample = Prepare("Price",
                "<local:PriceConverter x:Key=\"PriceConverter\" />\n" +
                "<BooleanToVisibilityConverter x:Key=\"Visible\" />\n" +
                "<DataTemplate x:Key=\"Price\"><StackPanel>" +
                "<TextBlock Text=\"{Binding Price, Converter={StaticResource PriceConverter}, ConverterParameter=2, StringFormat='{}{0}, net'}\" />" +
                "<TextBlock Text=\"VAT\" Visibility=\"{Binding HasVat, Converter={StaticResource Visible}}\" />" +
                "</StackPanel></DataTemplate>");

            XElement[] texts = sample.Entry.Element.Descendants().Where(e => e.Name.LocalName == "TextBlock").ToArray();
            Assert.AreEqual("{Binding Price, StringFormat='{}{0}, net'}", (string)texts[0].Attribute("Text"));
            Assert.AreEqual("{Binding HasVat, Converter={StaticResource Visible}}", (string)texts[1].Attribute("Visibility"));
        }

        [TestMethod]
        public void OwnConverter_InElementSyntax_IsLeftOut()
        {
            TemplateSample sample = Prepare("Price",
                "<local:PriceConverter x:Key=\"PriceConverter\" />\n" +
                "<DataTemplate x:Key=\"Price\"><TextBlock><TextBlock.Text>" +
                "<Binding Path=\"Price\" Converter=\"{StaticResource PriceConverter}\" ConverterParameter=\"2\" />" +
                "</TextBlock.Text></TextBlock></DataTemplate>");

            XElement binding = Single(sample, "Binding");
            Assert.IsNull(binding.Attribute("Converter"));
            Assert.IsNull(binding.Attribute("ConverterParameter"));
            Assert.AreEqual("Price", (string)binding.Attribute("Path"));
        }

        [TestMethod]
        public void OwnTypesInValues_EventsAndDataType_AreLeftOut()
        {
            TemplateSample sample = Prepare("Book",
                "<DataTemplate x:Key=\"Book\" DataType=\"{x:Type local:Book}\">" +
                "<Button Content=\"Open\" Click=\"OnOpen\" Command=\"{x:Static local:Commands.Open}\" local:Help.Text=\"Opens it\" Loaded=\"OnLoaded\" />" +
                "</DataTemplate>");

            Assert.IsNull(sample.Entry.Element.Attribute("DataType"));
            XElement button = Single(sample, "Button");
            CollectionAssert.AreEqual(new[] { "Content" }, button.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.LocalName).ToArray());
        }

        [TestMethod]
        public void TriggerOnABox_IsLeftOut()
        {
            TemplateSample sample = Prepare("Row",
                "<DataTemplate x:Key=\"Row\"><StackPanel><local:Rating x:Name=\"Stars\" /><TextBlock x:Name=\"Label\" /></StackPanel>" +
                "<DataTemplate.Triggers><DataTrigger Binding=\"{Binding IsNew}\" Value=\"True\">" +
                "<Setter TargetName=\"Stars\" Property=\"Opacity\" Value=\"0.5\" /><Setter TargetName=\"Label\" Property=\"Opacity\" Value=\"0.5\" />" +
                "</DataTrigger></DataTemplate.Triggers></DataTemplate>");

            CollectionAssert.AreEqual(new[] { "Label" }, sample.Entry.Element.Descendants().Where(e => e.Name.LocalName == "Setter").Select(e => (string)e.Attribute("TargetName")).ToArray());
        }

        [TestMethod]
        public void DesignValue_ReplacesTheBinding()
        {
            TemplateSample sample = Prepare("Editor",
                "<DataTemplate x:Key=\"Editor\" d:DataContext=\"{d:DesignInstance local:Item}\">" +
                "<TextBox Text=\"{Binding Value}\" d:Text=\"120\" IsReadOnly=\"{Binding IsReadOnly}\" d:ItemsSource=\"{d:SampleData}\" />" +
                "</DataTemplate>");

            XElement textBox = Single(sample, "TextBox");
            Assert.AreEqual("120", (string)textBox.Attribute("Text"));
            CollectionAssert.AreEqual(new[] { "IsReadOnly" }, sample.Bindings.Select(b => b.Path).ToArray());
        }

        [TestMethod]
        public void Bindings_WithTargetAndPath()
        {
            TemplateSample sample = Prepare("Book",
                "<DataTemplate x:Key=\"Book\"><StackPanel>" +
                "<TextBlock Text=\"{Binding Path=Title}\" />" +
                "<TextBlock Text=\"{Binding Author.Name, StringFormat='by {0}'}\" Grid.Row=\"{Binding Row}\" />" +
                "<TextBlock Text=\"{Binding}\" />" +
                "<TextBlock Text=\"{Binding Tag, RelativeSource={RelativeSource Self}}\" />" +
                "<TextBlock Text=\"{Binding Items[0]}\" />" +
                "</StackPanel></DataTemplate>");

            CollectionAssert.AreEqual(
                new[] { "Title TextBlock.Text", "Author.Name TextBlock.Text", "Row TextBlock.Grid.Row", " TextBlock.Text" },
                sample.Bindings.Select(b => b.Path + " " + b.ElementType.Name + "." + b.Property).ToArray());
        }

        [TestMethod]
        public void SplitMarkup_RespectsBracesAndQuotes()
        {
            CollectionAssert.AreEqual(
                new[] { "Binding Price", "Converter={StaticResource C}", "StringFormat='{}{0:N2}, total'" },
                TemplateSampleBuilder.SplitMarkup("{Binding Price, Converter={StaticResource C}, StringFormat='{}{0:N2}, total'}"));
            Assert.IsNull(TemplateSampleBuilder.SplitMarkup("{Binding Price"));
            Assert.IsNull(TemplateSampleBuilder.SplitMarkup("Price"));
        }

        [TestMethod]
        public void Build_PreparedTemplate_HasNoOwnTypes()
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("Templates.xaml", Xaml.Dictionary(
                    "<local:PriceConverter x:Key=\"PriceConverter\" />\n" +
                    "<DataTemplate x:Key=\"Book\" DataType=\"{x:Type local:Book}\"><StackPanel>" +
                    "<TextBlock Text=\"{Binding Price, Converter={StaticResource PriceConverter}}\" /><local:Rating />" +
                    "</StackPanel></DataTemplate>", Namespaces));
                var index = ResourceIndex.ForFile(file);
                ResourceEntry entry = index.Find("Book", file, CancellationToken.None).Single();

                Assert.IsNotNull(PreviewXamlBuilder.Build(index, entry, CancellationToken.None).UnloadableType);

                PreviewXaml prepared = PreviewXamlBuilder.Build(index, TemplateSampleBuilder.Prepare(index, entry, CancellationToken.None).Entry, CancellationToken.None);
                Assert.IsNull(prepared.UnloadableType, prepared.Xaml);
                Assert.AreEqual(0, prepared.MissingKeys.Count);
            }
        }

        // --- Rendering ---

        private static T FindVisual<T>(DependencyObject root, System.Func<T, bool> match = null) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T found && (match == null || match(found)))
                {
                    return found;
                }

                T deeper = FindVisual(child, match);
                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
        }

        private static IReadOnlyList<SampleBinding> Bindings(string templateXaml) =>
            TemplateSampleBuilder.FindBindings(XElement.Parse(templateXaml));

        private static string Template(string body) =>
            $"<DataTemplate xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\">{body}</DataTemplate>";

        [TestMethod]
        public void Render_ValueEditor_ShowsThePathAsText()
        {
            string xaml = Template("<TextBox Text=\"{Binding Value, Mode=TwoWay}\" IsReadOnly=\"{Binding IsReadOnly}\" />");

            (string text, bool readOnly) = Sta.Run(() =>
            {
                var result = TemplateSampleRenderer.ForDataTemplate((DataTemplate)XamlReader.Parse(xaml), Bindings(xaml));
                Assert.IsNull(result.Problem);
                var sample = (FrameworkElement)result.Sample;
                sample.Arrange(new Rect(sample.DesiredSize));
                TextBox box = FindVisual<TextBox>(sample);
                return (box.Text, box.IsReadOnly);
            });

            Assert.AreEqual("Value", text);
            Assert.IsFalse(readOnly);
        }

        [TestMethod]
        public void Render_NestedPathAndList()
        {
            string xaml = Template(
                "<StackPanel><TextBlock x:Name=\"Author\" Text=\"{Binding Author.Name}\" />" +
                "<ItemsControl ItemsSource=\"{Binding Chapters}\" /></StackPanel>");

            (string author, int chapters) = Sta.Run(() =>
            {
                var sample = (FrameworkElement)TemplateSampleRenderer.ForDataTemplate((DataTemplate)XamlReader.Parse(xaml), Bindings(xaml)).Sample;
                sample.Arrange(new Rect(sample.DesiredSize));
                return (FindVisual<TextBlock>(sample, t => t.Name == "Author").Text, FindVisual<ItemsControl>(sample).Items.Count);
            });

            Assert.AreEqual("Name", author);
            Assert.AreEqual(3, chapters);
        }

        [TestMethod]
        public void SampleData_OtherTypesGetTheDefault()
        {
            string xaml = Template("<StackPanel><TextBlock FontSize=\"{Binding Size}\" Text=\"{Binding Size}\" /><CheckBox IsChecked=\"{Binding Done}\" /></StackPanel>");
            SampleObject data = Sta.Run(() => TemplateSampleRenderer.CreateData(Bindings(xaml)));

            Assert.AreEqual("Size", data.Values["Size"], "text wins over the number");
            Assert.AreEqual(false, data.Values["Done"]);
        }

        [TestMethod]
        public void Render_ItemsPanel_ArrangesSixItems()
        {
            string xaml = $"<ItemsPanelTemplate xmlns=\"{Xaml.Presentation}\"><WrapPanel ItemWidth=\"80\" /></ItemsPanelTemplate>";

            int count = Sta.Run(() =>
            {
                var result = TemplateSampleRenderer.ForItemsPanel((ItemsPanelTemplate)XamlReader.Parse(xaml));
                var sample = (FrameworkElement)result.Sample;
                sample.Arrange(new Rect(sample.DesiredSize));
                return FindVisual<WrapPanel>(sample).Children.Count;
            });

            Assert.AreEqual(6, count);
        }

        [TestMethod]
        public void Render_FromTheSampleApp_ValueEditorAndPropertyRow()
        {
            SettingsService.SetCurrentForTests(ExtensionSettings.Default);
            string main = System.IO.Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            var index = ResourceIndex.ForFile(main);

            foreach (string key in new[] { "{x:Static TemplateKeys.ValueEditor}", "PropertyRowTemplate", "BookTemplate", "WrapPanelTemplate" })
            {
                ResourcePreviewData data = ResourcePreviewData.Create(index, index.Find(key, main, CancellationToken.None).First(), null, CancellationToken.None);
                Assert.IsNotNull(data.Sample, key);
                Assert.IsNull(data.UnloadableType, key);
                Assert.AreEqual(0, data.MissingKeys.Count, key);

                bool rendered = Sta.Run(() =>
                {
                    var preview = (Panel)PreviewRenderer.Render(data);
                    return preview.Children.OfType<Border>().Any(b => b.ClipToBounds);
                });
                Assert.IsTrue(rendered, key);
            }

            ResourcePreviewData row = ResourcePreviewData.Create(index, index.Find("PropertyRowTemplate", main, CancellationToken.None).First(), null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "local:FancyControl" }, row.Sample.Placeholders.ToArray());
        }
    }
}
