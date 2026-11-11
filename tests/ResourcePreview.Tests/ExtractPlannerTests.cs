using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>Extract to resource: which values qualify, the resource XAML, and where it is inserted.</summary>
    [TestClass]
    public class ExtractPlannerTests
    {
        private static ExtractCandidate At(string text, string marker)
        {
            int caret = text.IndexOf(marker) + 1;
            return ExtractPlanner.TryFind(text, caret);
        }

        [TestMethod]
        [DataRow("<Border Background=\"#FF2D7D9A\" />", "#", "SolidColorBrush")]
        [DataRow("<Border Background=\"SteelBlue\" />", "S", "SolidColorBrush")]
        [DataRow("<GradientStop Color=\"Red\" />", "R", "Color")]
        [DataRow("<Border Margin=\"4,2\" />", "4", "Thickness")]
        [DataRow("<Border Padding=\"1 2 3 4\" />", "1", "Thickness")]
        [DataRow("<Border CornerRadius=\"6\" />", "6", "CornerRadius")]
        [DataRow("<TextBlock FontSize=\"14\" />", "1", "sys:Double")]
        [DataRow("<TextBlock FontFamily=\"Segoe UI\" />", "S", "FontFamily")]
        [DataRow("<TextBlock FontWeight=\"SemiBold\" />", "S", "FontWeight")]
        [DataRow("<TextBlock Text=\"Hello\" />", "H", "sys:String")]
        public void TryFind_KnownProperties(string text, string marker, string expectedType)
        {
            Assert.AreEqual(expectedType, At(text, marker)?.ResourceType);
        }

        [TestMethod]
        [DataRow("<Border Background=\"{StaticResource Accent}\" />", "A")]
        [DataRow("<Border x:Key=\"Accent\" />", "A")]
        [DataRow("<Border Tag=\"Something\" />", "S")]
        [DataRow("<Border Background=\"NotAColor\" />", "N")]
        [DataRow("<Border Width=\"Auto\" />", "A")]
        [DataRow("<Border Margin=\"1,2,3\" />", "1")]
        public void TryFind_Rejects(string text, string marker)
        {
            Assert.IsNull(At(text, marker));
        }

        [TestMethod]
        [DataRow("<Style TargetType=\"Button\">\n  <Setter Property=\"Background\" Value=\"White\" />\n</Style>", "SolidColorBrush", "ButtonBackground")]
        [DataRow("<Style x:Key=\"S\" TargetType=\"{x:Type local:Fancy}\">\n  <Setter Property=\"Margin\" Value=\"4\" />", "Thickness", "FancyMargin")]
        [DataRow("<Style TargetType=\"Button\">\n  <Style.Triggers>\n    <Trigger Property=\"IsMouseOver\" Value=\"True\">\n      <Setter Property=\"Foreground\" Value=\"Red\" />", "SolidColorBrush", "ButtonForeground")]
        [DataRow("<Setter Property=\"FontSize\" Value=\"13\" />", "sys:Double", "FontSize")]
        public void TryFind_SetterValue_UsesTheSetterProperty(string text, string expectedType, string expectedKey)
        {
            int caret = text.LastIndexOf("Value=\"", System.StringComparison.Ordinal) + 8;
            ExtractCandidate candidate = ExtractPlanner.TryFind(text, caret);

            Assert.AreEqual(expectedType, candidate.ResourceType);
            Assert.AreEqual(expectedKey, ExtractPlanner.SuggestKey(candidate));
        }

        [TestMethod]
        public void TryFind_SetterValue_UnknownProperty_Nothing()
        {
            const string text = "<Setter Property=\"Tag\" Value=\"White\" />";
            Assert.IsNull(ExtractPlanner.TryFind(text, text.IndexOf("White", System.StringComparison.Ordinal) + 1));
        }

        [TestMethod]
        public void TryFind_PositionsAndNames()
        {
            string text = "<Grid>\n  <Border\n      Background=\"#FF2D7D9A\" />\n</Grid>";
            ExtractCandidate candidate = At(text, "#FF");

            Assert.AreEqual("#FF2D7D9A", text.Substring(candidate.ValueStart, candidate.ValueLength));
            Assert.AreEqual("Border", candidate.ElementName);
            Assert.AreEqual("BorderBackground", ExtractPlanner.SuggestKey(candidate));
        }

        [TestMethod]
        public void ResourceText_PerType()
        {
            Assert.AreEqual("<SolidColorBrush x:Key=\"K\" Color=\"#FF2D7D9A\" />",
                ExtractPlanner.ResourceText(At("<Border Background=\"#FF2D7D9A\" />", "#"), "K"));
            Assert.AreEqual("<Thickness x:Key=\"K\">4,2</Thickness>",
                ExtractPlanner.ResourceText(At("<Border Margin=\"4,2\" />", "4"), "K"));
            Assert.AreEqual("<sys:String x:Key=\"K\">Save &amp; close</sys:String>",
                ExtractPlanner.ResourceText(At("<Button Content=\"Save &amp; close\" />", "S"), "K"));
        }

        [TestMethod]
        public void AddResource_AtTheEndOfADictionary()
        {
            string dictionary = Xaml.Dictionary("    <SolidColorBrush x:Key=\"A\" Color=\"Red\" />");
            ExtractCandidate candidate = At("<Border Background=\"#FF2D7D9A\" />", "#");

            string result = TextChange.Apply(dictionary, ExtractPlanner.AddResource(dictionary, candidate, "Accent", null));

            StringAssert.Contains(result, "    <SolidColorBrush x:Key=\"Accent\" Color=\"#FF2D7D9A\" />\n</ResourceDictionary>");
            System.Xml.Linq.XDocument.Parse(result); // still valid XML
        }

        [TestMethod]
        public void AddResource_SystemNamespace_AddedOrReused()
        {
            ExtractCandidate size = At("<TextBlock FontSize=\"14\" />", "1");

            string plain = Xaml.Dictionary(string.Empty);
            string added = TextChange.Apply(plain, ExtractPlanner.AddResource(plain, size, "Big", null));
            StringAssert.Contains(added, "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\"");
            StringAssert.Contains(added, "<sys:Double x:Key=\"Big\">14</sys:Double>");
            System.Xml.Linq.XDocument.Parse(added);

            string withCore = Xaml.Dictionary(string.Empty, "xmlns:s=\"clr-namespace:System;assembly=System.Runtime\"");
            string reused = TextChange.Apply(withCore, ExtractPlanner.AddResource(withCore, size, "Big", null));
            StringAssert.Contains(reused, "<s:Double x:Key=\"Big\">14</s:Double>");
            Assert.IsFalse(reused.Contains("xmlns:sys"));
        }

        [TestMethod]
        public void AddResource_AppXaml_WithAndWithoutDictionary()
        {
            ExtractCandidate candidate = At("<Border Background=\"Red\" />", "R");

            string withDictionary =
                $"<Application xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\">\n" +
                "    <Application.Resources>\n        <ResourceDictionary>\n" +
                "            <ResourceDictionary.MergedDictionaries>\n                <ResourceDictionary Source=\"A.xaml\" />\n            </ResourceDictionary.MergedDictionaries>\n" +
                "        </ResourceDictionary>\n    </Application.Resources>\n</Application>";
            string result = TextChange.Apply(withDictionary, ExtractPlanner.AddResource(withDictionary, candidate, "K", null));
            StringAssert.Contains(result, "            <SolidColorBrush x:Key=\"K\" Color=\"Red\" />\n        </ResourceDictionary>\n    </Application.Resources>");

            string direct = $"<Application xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\">\n    <Application.Resources>\n    </Application.Resources>\n</Application>";
            string result2 = TextChange.Apply(direct, ExtractPlanner.AddResource(direct, candidate, "K", null));
            StringAssert.Contains(result2, "        <SolidColorBrush x:Key=\"K\" Color=\"Red\" />\n    </Application.Resources>");
        }

        [TestMethod]
        public void SameDictionary_ResourceGoesAboveItsUse()
        {
            string text = Xaml.Dictionary(
                "    <SolidColorBrush x:Key=\"A\" Color=\"Red\" />\n" +
                "    <Style x:Key=\"S\" TargetType=\"Border\">\n" +
                "        <Setter Property=\"Background\" Value=\"Red\" />\n" +
                "        <Setter Property=\"Margin\" Value=\"4\" />\n" +
                "    </Style>");

            // The setter value: Value="4" is not a known property, so take a direct attribute instead.
            string withAttribute = text.Replace("<Style x:Key=\"S\" TargetType=\"Border\">", "<Style x:Key=\"S\" TargetType=\"Border\">\n        <Border Margin=\"4,2\" />");
            ExtractCandidate candidate = At(withAttribute, "4,2");

            var changes = ExtractPlanner.AddResource(withAttribute, candidate, "Gap", candidate.ValueStart).ToList();
            changes.Add(ExtractPlanner.ReplaceValue(candidate, "Gap"));
            string result = TextChange.Apply(withAttribute, changes);

            StringAssert.Contains(result, "    <Thickness x:Key=\"Gap\">4,2</Thickness>\n    <Style x:Key=\"S\"");
            StringAssert.Contains(result, "<Border Margin=\"{StaticResource Gap}\" />");
            System.Xml.Linq.XDocument.Parse(result);
        }

        [TestMethod]
        public void Targets_SampleApp_DictionariesThenApp()
        {
            string main = Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            var targets = ExtractPlanner.Targets(ResourceIndex.ForFile(main), main, CancellationToken.None)
                .Select(Path.GetFileName)
                .ToArray();

            CollectionAssert.AreEqual(new[] { "Templates.xaml", "Values.xaml", "Styles.xaml", "Brushes.xaml", "Icons.xaml", "App.xaml" }, targets);
        }
    }
}
