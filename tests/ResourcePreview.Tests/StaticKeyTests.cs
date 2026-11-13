using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.QuickInfo;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>Keys that are a static member: x:Key="{x:Static res:TemplateKeys.ValueEditor}".</summary>
    [TestClass]
    public class StaticKeyTests
    {
        private const string Res = "xmlns:res=\"clr-namespace:MyApp.Resources\"";
        private const string Keys = "xmlns:keys=\"clr-namespace:MyApp.Resources;assembly=MyApp\"";

        private static string App(params string[] sources)
        {
            string merged = string.Concat(sources.Select(s => $"<ResourceDictionary Source=\"{s}\" />"));
            return $"<Application x:Class=\"T.App\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Application.Resources><ResourceDictionary>" +
                   $"<ResourceDictionary.MergedDictionaries>{merged}</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>";
        }

        private static string Window(string body, string namespaces = Keys) =>
            $"<Window x:Class=\"T.W\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\" {namespaces}>\n{body}\n</Window>";

        private const string Template =
            "<DataTemplate x:Key=\"{x:Static res:TemplateKeys.ValueEditor}\">\n" +
            "  <TextBox Text=\"{Binding Value, Mode=TwoWay}\" IsReadOnly=\"{Binding IsReadOnly}\" />\n" +
            "</DataTemplate>";

        private const string Brush = "<SolidColorBrush x:Key=\"{x:Static res:BrushKeys.Accent}\" Color=\"#FF2050A0\" />";

        /// <summary>App.xaml merges Templates.xaml; MainWindow.xaml uses the keys with another prefix.</summary>
        private static TempSolution AppSolution(string windowBody, out string window)
        {
            var solution = new TempSolution();
            solution.Write("App.csproj", "<Project />");
            solution.Write("App.xaml", App("Templates.xaml"));
            solution.Write("Templates.xaml", Xaml.Dictionary(Template + "\n" + Brush, Res));
            window = solution.Write("MainWindow.xaml", Window(windowBody));
            return solution;
        }

        // --- ResourceKeys ---

        [TestMethod]
        public void Normalize_PlainKeyStaysTheSame()
        {
            Assert.AreEqual("AccentBrush", ResourceKeys.Normalize("AccentBrush"));
            Assert.AreEqual("My Brush", ResourceKeys.Normalize("My Brush"));
        }

        [TestMethod]
        public void Normalize_StaticMember_LeavesOutThePrefix()
        {
            const string expected = "{x:Static TemplateKeys.ValueEditor}";
            Assert.AreEqual(expected, ResourceKeys.Normalize("{x:Static res:TemplateKeys.ValueEditor}"));
            Assert.AreEqual(expected, ResourceKeys.Normalize("{x:Static keys:TemplateKeys.ValueEditor}"));
            Assert.AreEqual(expected, ResourceKeys.Normalize("{ x:Static  Member=res:TemplateKeys.ValueEditor }"));
            Assert.AreEqual(expected, ResourceKeys.Normalize("{x:Static TemplateKeys.ValueEditor}"));
            Assert.AreEqual("{x:Static SystemColors.ControlBrushKey}", ResourceKeys.Normalize("{x:Static SystemColors.ControlBrushKey}"));
        }

        [TestMethod]
        public void Normalize_OtherMarkup_IsNotSupported()
        {
            Assert.IsNull(ResourceKeys.Normalize("{x:Type Button}"));
            Assert.IsNull(ResourceKeys.Normalize("{x:Static NoMember}"));
            Assert.IsNull(ResourceKeys.Normalize("{ComponentResourceKey TypeInTargetAssembly={x:Type local:A}, ResourceId=B}"));
            Assert.IsNull(ResourceKeys.Normalize(""));
            Assert.IsNull(ResourceKeys.Normalize(null));
        }

        [TestMethod]
        public void StaticTypeNameAndPreviewKey()
        {
            Assert.AreEqual("TemplateKeys", ResourceKeys.StaticTypeName("{x:Static TemplateKeys.ValueEditor}"));
            Assert.AreEqual("Ns.Keys", ResourceKeys.StaticTypeName("{x:Static Ns.Keys.A}"));
            Assert.IsNull(ResourceKeys.StaticTypeName("TemplateKeys.ValueEditor"));
            Assert.AreEqual("xStatic.TemplateKeys.ValueEditor", ResourceKeys.PreviewKey("{x:Static TemplateKeys.ValueEditor}"));
            Assert.AreEqual("AccentBrush", ResourceKeys.PreviewKey("AccentBrush"));
        }

        // --- Index and references ---

        [TestMethod]
        public void Index_FindsTheDefinition_WithTheTextAsWritten()
        {
            using (AppSolution(string.Empty, out string window))
            {
                ResourceEntry entry = ResourceIndex.ForFile(window)
                    .Find("{x:Static TemplateKeys.ValueEditor}", window, CancellationToken.None)
                    .Single();

                Assert.AreEqual("DataTemplate", entry.ElementTypeName);
                Assert.AreEqual("{x:Static res:TemplateKeys.ValueEditor}", entry.KeyText);
                Assert.AreEqual("{StaticResource {x:Static res:TemplateKeys.ValueEditor}}", entry.StaticReference);
            }
        }

        [TestMethod]
        public void PlainKey_KeyTextIsTheKey()
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("Brushes.xaml", Xaml.Dictionary("<SolidColorBrush x:Key=\"Accent\" Color=\"Red\" />"));
                ResourceEntry entry = ResourceIndex.ForFile(file).Find("Accent", file, CancellationToken.None).Single();
                Assert.AreEqual("Accent", entry.KeyText);
                Assert.AreEqual("{StaticResource Accent}", entry.StaticReference);
            }
        }

        [TestMethod]
        public void FindUses_StaticKeysInAllForms()
        {
            string text =
                "<ContentControl ContentTemplate=\"{StaticResource {x:Static keys:TemplateKeys.ValueEditor}}\" />\n" +
                "<Border Background=\"{DynamicResource ResourceKey={x:Static keys:BrushKeys.Accent}}\" />\n" +
                "<StaticResource ResourceKey=\"{x:Static keys:BrushKeys.Accent}\" />\n" +
                "<Style BasedOn=\"{StaticResource {x:Type Button}}\" />";

            List<ResourceUse> uses = ResourceIndex.FindUses(text);

            CollectionAssert.AreEqual(
                new[] { "{x:Static TemplateKeys.ValueEditor}", "{x:Static BrushKeys.Accent}", "{x:Static BrushKeys.Accent}" },
                uses.Select(u => u.Key).ToArray());
            Assert.AreEqual(text.IndexOf("{x:Static") + 1, uses[0].Column);
        }

        [TestMethod]
        public void ReferenceFinder_OnAReferenceAndOnTheDefinition()
        {
            string reference = "<ContentControl ContentTemplate=\"{StaticResource {x:Static keys:TemplateKeys.ValueEditor}}\" />";
            Assert.IsTrue(ResourceReferenceFinder.TryFindReference(reference, reference.IndexOf("ValueEditor"), out ResourceReference found));
            Assert.AreEqual("{x:Static TemplateKeys.ValueEditor}", found.Key);
            Assert.AreEqual(reference.IndexOf("{StaticResource"), found.Start);
            Assert.AreEqual(reference.LastIndexOf('}') + 1 - found.Start, found.Length);

            string definition = "<DataTemplate x:Key=\"{x:Static res:TemplateKeys.ValueEditor}\">";
            Assert.IsFalse(ResourceReferenceFinder.TryFindReference(definition, definition.IndexOf("Value"), out _));
            Assert.IsTrue(ResourceReferenceFinder.TryFind(definition, definition.IndexOf("Value"), out found));
            Assert.AreEqual("{x:Static TemplateKeys.ValueEditor}", found.Key);
        }

        [TestMethod]
        public void ReferenceFinder_TypeKey_IsNoReference()
        {
            string line = "<Style BasedOn=\"{StaticResource {x:Type Button}}\" />";
            Assert.IsFalse(ResourceReferenceFinder.TryFind(line, line.IndexOf("Button"), out _));
        }

        [TestMethod]
        public void UsageFinder_FindsUsesWithAnotherPrefix()
        {
            using (AppSolution("<ContentControl ContentTemplate=\"{StaticResource {x:Static keys:TemplateKeys.ValueEditor}}\" />", out string window))
            {
                UsageReport report = UsageFinder.Find(ResourceIndex.ForFile(window), "{x:Static TemplateKeys.ValueEditor}", null, CancellationToken.None);

                Assert.AreEqual(1, report.Definitions.Count);
                Assert.AreEqual(window, report.Usages.Single().FilePath);
                Assert.AreEqual(report.Definitions[0], report.Usages.Single().ResolvesTo);
            }
        }

        // --- Error List ---

        [TestMethod]
        public void Check_DefinedStaticKey_IsFine()
        {
            using (AppSolution("<ContentControl ContentTemplate=\"{StaticResource {x:Static keys:TemplateKeys.ValueEditor}}\" />", out string window))
            {
                Assert.AreEqual(0, ReferenceChecker.CheckAll(ResourceIndex.ForFile(window), CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void Check_WpfStaticKey_IsNotMissing()
        {
            using (AppSolution("<Border Background=\"{DynamicResource {x:Static SystemColors.ControlBrushKey}}\" BorderBrush=\"{StaticResource {x:Static keys:Other.Key}}\" />", out string window))
            {
                Assert.AreEqual(0, ReferenceChecker.CheckAll(ResourceIndex.ForFile(window), CancellationToken.None).Count);
            }
        }

        [TestMethod]
        public void Check_MissingMemberOfAKnownKeyClass_IsReported()
        {
            using (AppSolution("<ContentControl ContentTemplate=\"{StaticResource {x:Static keys:TemplateKeys.NumberEditor}}\" />", out string window))
            {
                ReferenceProblem problem = ReferenceChecker.CheckAll(ResourceIndex.ForFile(window), CancellationToken.None).Single();
                Assert.AreEqual(ReferenceProblemKind.Missing, problem.Kind);
                Assert.AreEqual("{x:Static TemplateKeys.NumberEditor}", problem.Use.Key);
            }
        }

        [TestMethod]
        public void Unused_StaticKeys_AreNotReported()
        {
            using (AppSolution(string.Empty, out string window))
            {
                Assert.AreEqual(0, UnusedResourceScanner.Scan(ResourceIndex.ForFile(window), CancellationToken.None).Count);
            }
        }

        // --- Preview ---

        [TestMethod]
        public void Preview_StaticKeysBecomePlainText_AndTheDictionaryLoads()
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("Brushes.xaml", Xaml.Dictionary(
                    Brush + "\n" +
                    "<Style x:Key=\"{x:Static res:StyleKeys.Card}\" TargetType=\"Border\">\n" +
                    "  <Setter Property=\"Background\" Value=\"{StaticResource {x:Static res:BrushKeys.Accent}}\" />\n" +
                    "  <Setter Property=\"BorderBrush\" Value=\"{DynamicResource {x:Static SystemColors.ControlBrushKey}}\" />\n" +
                    "</Style>", Res));

                var index = ResourceIndex.ForFile(file);
                ResourceEntry style = index.Find("{x:Static StyleKeys.Card}", file, CancellationToken.None).Single();
                PreviewXaml preview = PreviewXamlBuilder.Build(index, style, CancellationToken.None);

                Assert.IsNull(preview.UnloadableType, preview.Xaml);
                Assert.AreEqual(0, preview.MissingKeys.Count);
                StringAssert.Contains(preview.Xaml, "{StaticResource xStatic.BrushKeys.Accent}");
                StringAssert.Contains(preview.Xaml, "{x:Static SystemColors.ControlBrushKey}");

                Color background = Sta.Run(() =>
                {
                    var dictionary = (ResourceDictionary)XamlReader.Parse(preview.Xaml);
                    var loaded = (Style)dictionary[ResourceKeys.PreviewKey(style.Key)];
                    var setter = (Setter)loaded.Setters[0];
                    return ((SolidColorBrush)setter.Value).Color;
                });
                Assert.AreEqual(Color.FromRgb(0x20, 0x50, 0xA0), background);
            }
        }

        [TestMethod]
        public void Preview_StaticKeyOfTheProjectWithoutDefinition_IsAnOwnType()
        {
            using (var solution = new TempSolution())
            {
                string file = solution.Write("Brushes.xaml", Xaml.Dictionary(
                    "<Style x:Key=\"Card\" TargetType=\"Border\"><Setter Property=\"Background\" Value=\"{StaticResource {x:Static res:BrushKeys.Nope}}\" /></Style>", Res));

                var index = ResourceIndex.ForFile(file);
                PreviewXaml preview = PreviewXamlBuilder.Build(index, index.Find("Card", file, CancellationToken.None).Single(), CancellationToken.None);

                Assert.AreEqual("res:BrushKeys.Nope", preview.UnloadableType);
            }
        }

        [TestMethod]
        public void SampleApp_ValueEditor_FromMainWindowWithAnotherPrefix()
        {
            string main = System.IO.Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            var index = ResourceIndex.ForFile(main);

            ResourceEntry entry = index.Find("{x:Static TemplateKeys.ValueEditor}", main, CancellationToken.None).Single();
            Assert.AreEqual("DataTemplate", entry.ElementTypeName);
            Assert.AreEqual("{x:Static res:TemplateKeys.ValueEditor}", entry.KeyText);

            UsageReport report = UsageFinder.Find(index, entry.Key, entry, CancellationToken.None);
            Assert.AreEqual(main, report.Usages.Single().FilePath);
            Assert.AreEqual(entry, report.Usages.Single().ResolvesTo);
        }

        // --- Styles ---

        [TestMethod]
        public void StylePlanner_StaticStyleKey_KeepsTheTextForBasedOn()
        {
            string text = "<Border Style=\"{StaticResource {x:Static res:StyleKeys.Card}}\" Padding=\"4\" />";
            AttributeSelection selection = StylePlanner.TryFindAt(text, text.IndexOf("Padding"));

            Assert.AreEqual("{x:Static StyleKeys.Card}", selection.StyleKey);
            Assert.AreEqual("{x:Static res:StyleKeys.Card}", selection.StyleKeyText);
            StringAssert.Contains(StylePlanner.StyleLines(selection, "BorderStyle")[0], "BasedOn=\"{StaticResource {x:Static res:StyleKeys.Card}}\"");
        }
    }
}
