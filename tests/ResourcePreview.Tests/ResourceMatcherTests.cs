using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>Find Matching Resource: which styles and values the selected attributes could use.</summary>
    [TestClass]
    public class ResourceMatcherTests
    {
        private const string IconLine = "<Path Data=\"{StaticResource Search}\" Fill=\"White\" Stretch=\"Uniform\" Width=\"16\" Height=\"16\" Margin=\"0,0,8,0\" />";

        private static IReadOnlyList<ResourceEntry> Entries(string body, string extraNamespaces = "") =>
            ResourceIndex.ParseModel("R.xaml", Xaml.Dictionary(body, extraNamespaces)).Entries;

        private static AttributeSelection Select(string text, string from, string to)
        {
            int start = text.IndexOf(from);
            return StylePlanner.TryFind(text, start, text.IndexOf(to, start) + to.Length);
        }

        private static IReadOnlyList<ResourceMatch> Find(AttributeSelection selection, string body, string extraNamespaces = "") =>
            ResourceMatcher.Find(selection, Entries(body, extraNamespaces), CancellationToken.None);

        private static string[] Keys(IEnumerable<ResourceMatch> matches) => matches.Select(m => m.Key).ToArray();

        // --- Styles ---

        [TestMethod]
        public void Style_SameSetters_OtherSpelling_Matches_AndReplacesTheAttributes()
        {
            string styles =
                "<Style x:Key=\"IconStyle\" TargetType=\"{x:Type Path}\">" +
                "<Setter Property=\"Fill\" Value=\"#FFFFFFFF\" /><Setter Property=\"Stretch\" Value=\"Uniform\" />" +
                "<Setter Property=\"Width\" Value=\"16.0\" /><Setter Property=\"Height\" Value=\"16\" /><Setter Property=\"Margin\" Value=\"0 0 8 0\" />" +
                "</Style>";
            AttributeSelection selection = Select(IconLine, "Fill=", "\"0,0,8,0\"");

            ResourceMatch match = Find(selection, styles).Single();

            Assert.AreEqual(MatchKind.Style, match.Kind);
            Assert.AreEqual(5, match.Covered.Count);
            Assert.AreEqual(0, match.Remaining.Count);
            Assert.AreEqual(
                "<Path Data=\"{StaticResource Search}\" Style=\"{StaticResource IconStyle}\" />",
                TextChange.Apply(IconLine, ResourceMatcher.Apply(selection, match)));
        }

        [TestMethod]
        public void Style_SetsPartOfTheSelection_RestStaysOnTheElement()
        {
            string styles = "<Style x:Key=\"Icon16\" TargetType=\"Path\"><Setter Property=\"Width\" Value=\"16\" /><Setter Property=\"Height\" Value=\"16\" /></Style>";
            AttributeSelection selection = Select(IconLine, "Fill=", "\"0,0,8,0\"");

            ResourceMatch match = Find(selection, styles).Single();

            CollectionAssert.AreEqual(new[] { "Fill", "Stretch", "Margin" }, match.Remaining.Select(a => a.Name).ToArray());
            Assert.AreEqual(
                "<Path Data=\"{StaticResource Search}\" Fill=\"White\" Stretch=\"Uniform\" Style=\"{StaticResource Icon16}\" Margin=\"0,0,8,0\" />",
                TextChange.Apply(IconLine, ResourceMatcher.Apply(selection, match)));
        }

        [DataTestMethod]
        [DataRow("<Setter Property=\"Width\" Value=\"16\" /><Setter Property=\"Opacity\" Value=\"0.5\" />", DisplayName = "sets more")]
        [DataRow("<Setter Property=\"Width\" Value=\"24\" />", DisplayName = "other value")]
        [DataRow("<Setter Property=\"Width\"><Setter.Value>16</Setter.Value></Setter>", DisplayName = "element syntax")]
        [DataRow("<EventSetter Event=\"Loaded\" Handler=\"OnLoaded\" /><Setter Property=\"Width\" Value=\"16\" />", DisplayName = "event setter")]
        [DataRow("", DisplayName = "no setters")]
        public void Style_ThatChangesMore_OrLess_IsLeftOut(string setters)
        {
            AttributeSelection selection = Select(IconLine, "Fill=", "\"0,0,8,0\"");
            Assert.AreEqual(0, Find(selection, $"<Style x:Key=\"S\" TargetType=\"Path\">{setters}</Style>").Count);
        }

        [DataTestMethod]
        [DataRow("Path", true)]
        [DataRow("Shape", true)]
        [DataRow("{x:Type FrameworkElement}", true)]
        [DataRow("Button", false)]
        [DataRow("local:IconPath", false)]
        public void Style_TargetType_MustFitTheElement(string targetType, bool fits)
        {
            AttributeSelection selection = Select(IconLine, "Width=", "\"16\"");
            string style = $"<Style x:Key=\"S\" TargetType=\"{targetType}\"><Setter Property=\"Width\" Value=\"16\" /></Style>";

            Assert.AreEqual(fits ? 1 : 0, Find(selection, style, "xmlns:local=\"clr-namespace:App\"").Count);
        }

        [TestMethod]
        public void Style_BasedOn_CountsTheBaseSetters_AndTheTriggers()
        {
            string styles =
                "<Style x:Key=\"Base\" TargetType=\"Button\"><Setter Property=\"Padding\" Value=\"8\" />" +
                "<Style.Triggers><Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter Property=\"Opacity\" Value=\"0.8\" /></Trigger></Style.Triggers></Style>" +
                "<Style x:Key=\"Wide\" TargetType=\"Button\" BasedOn=\"{StaticResource Base}\"><Setter Property=\"Button.MinWidth\" Value=\"80\" /></Style>";
            string text = "<Button Padding=\"8,8,8,8\" MinWidth=\"80\" Content=\"OK\" />";
            AttributeSelection selection = Select(text, "Padding", "\"OK\"");

            IReadOnlyList<ResourceMatch> matches = Find(selection, styles);

            CollectionAssert.AreEqual(new[] { "Wide", "Base" }, Keys(matches));
            Assert.AreEqual(1, matches[0].TriggerCount);
            CollectionAssert.AreEqual(new[] { "Content" }, matches[0].Remaining.Select(a => a.Name).ToArray());
        }

        [TestMethod]
        public void Style_SetterWithAReference_MatchesTheLiteralValue()
        {
            string body =
                "<SolidColorBrush x:Key=\"AccentBrush\" Color=\"#FF002BBD\" />" +
                "<Style x:Key=\"BaseButton\" TargetType=\"Button\"><Setter Property=\"Padding\" Value=\"8,4\" />" +
                "<Setter Property=\"Background\" Value=\"{StaticResource AccentBrush}\" /></Style>";
            string text = "<Button Padding=\"8 4\" Background=\"#002BBD\" Content=\"Plain\" />";

            IReadOnlyList<ResourceMatch> matches = Find(Select(text, "Padding", "\"Plain\""), body);

            CollectionAssert.AreEqual(new[] { "BaseButton", "AccentBrush" }, Keys(matches));
            Assert.AreEqual(
                "<Button Style=\"{StaticResource BaseButton}\" Content=\"Plain\" />",
                TextChange.Apply(text, ResourceMatcher.Apply(Select(text, "Padding", "\"Plain\""), matches[0])));
        }

        [TestMethod]
        public void Style_UnknownBase_IsLeftOut()
        {
            string style = "<Style x:Key=\"S\" TargetType=\"Button\" BasedOn=\"{StaticResource Elsewhere}\"><Setter Property=\"Padding\" Value=\"8\" /></Style>";
            AttributeSelection selection = Select("<Button Padding=\"8\" />", "Padding", "\"8\"");
            Assert.AreEqual(0, Find(selection, style).Count);
        }

        [TestMethod]
        public void Style_NotOffered_WhenTheElementHasAStyle_ValuesStillAre()
        {
            string body = "<Style x:Key=\"S\" TargetType=\"Button\"><Setter Property=\"Padding\" Value=\"8\" /></Style><Thickness x:Key=\"Pad\">8</Thickness>";
            AttributeSelection selection = Select("<Button Style=\"{StaticResource Other}\" Padding=\"8\" />", "Padding", "\"8\"");

            CollectionAssert.AreEqual(new[] { "Pad" }, Keys(Find(selection, body)));
        }

        [TestMethod]
        public void AttachedProperty_KeepsItsOwner()
        {
            string style = "<Style x:Key=\"S\" TargetType=\"Button\"><Setter Property=\"Grid.Row\" Value=\"1\" /></Style>";
            Assert.AreEqual(1, Find(Select("<Button Grid.Row=\"1\" />", "Grid", "\"1\""), style).Count);
        }

        // --- Single values ---

        [DataTestMethod]
        [DataRow("<SolidColorBrush x:Key=\"V\" Color=\"#FFF\" />", "<Path Fill=\"White\" />", "Fill")]
        [DataRow("<SolidColorBrush x:Key=\"V\" Color=\"White\" />", "<Path Fill=\"#FFFFFFFF\" />", "Fill")]
        [DataRow("<Color x:Key=\"V\">#FF2D7D9A</Color>", "<GradientStop Color=\"#2D7D9A\" />", "Color")]
        [DataRow("<Thickness x:Key=\"V\">8</Thickness>", "<Border Margin=\"8,8,8,8\" />", "Margin")]
        [DataRow("<Thickness x:Key=\"V\">4,8</Thickness>", "<Border Padding=\"4 8 4 8\" />", "Padding")]
        [DataRow("<CornerRadius x:Key=\"V\">4</CornerRadius>", "<Border CornerRadius=\"4,4,4,4\" />", "CornerRadius")]
        [DataRow("<sys:Double x:Key=\"V\">16</sys:Double>", "<Path Width=\"16.0\" />", "Width")]
        [DataRow("<FontWeight x:Key=\"V\">SemiBold</FontWeight>", "<TextBlock FontWeight=\"semibold\" />", "FontWeight")]
        public void Value_SameValue_OtherSpelling_Matches(string resource, string element, string property)
        {
            AttributeSelection selection = Select(element, property, "\" ");
            ResourceMatch match = Find(selection, resource, "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\"").Single();

            Assert.AreEqual(MatchKind.Value, match.Kind);
            StringAssert.Contains(TextChange.Apply(element, ResourceMatcher.Apply(selection, match)), property + "=\"{StaticResource V}\"");
        }

        [DataTestMethod]
        [DataRow("<SolidColorBrush x:Key=\"V\" Color=\"White\" Opacity=\"0.5\" />", "<Path Fill=\"White\" />", "Fill", DisplayName = "brush with opacity")]
        [DataRow("<SolidColorBrush x:Key=\"V\" Color=\"Black\" />", "<Path Fill=\"White\" />", "Fill", DisplayName = "other color")]
        [DataRow("<Color x:Key=\"V\">White</Color>", "<Path Fill=\"White\" />", "Fill", DisplayName = "Color is no Brush")]
        [DataRow("<Thickness x:Key=\"V\">8</Thickness>", "<Border Width=\"8\" />", "Width", DisplayName = "Thickness is no Double")]
        public void Value_OtherTypeOrValue_IsLeftOut(string resource, string element, string property)
        {
            Assert.AreEqual(0, Find(Select(element, property, "\" "), resource).Count);
        }

        [TestMethod]
        public void Order_FullStylesFirst_ThenPartial_ThenValues()
        {
            string body =
                "<SolidColorBrush x:Key=\"WhiteBrush\" Color=\"White\" />" +
                "<Style x:Key=\"Small\" TargetType=\"Path\"><Setter Property=\"Width\" Value=\"16\" /></Style>" +
                "<Style x:Key=\"Full\" TargetType=\"Path\"><Setter Property=\"Fill\" Value=\"White\" /><Setter Property=\"Width\" Value=\"16\" /></Style>";
            string text = "<Path Fill=\"White\" Width=\"16\" />";

            CollectionAssert.AreEqual(new[] { "Full", "Small", "WhiteBrush" }, Keys(Find(Select(text, "Fill", "\"16\""), body)));
        }

        // --- Caret and what the element can see ---

        [DataTestMethod]
        [DataRow(14, "Fill", DisplayName = "in the value")]
        [DataRow(18, "Fill", DisplayName = "after the quote")]
        [DataRow(6, "Fill", DisplayName = "on the name")]
        public void TryFindAt_TheAttributeAtTheCaret(int caret, string expected)
        {
            const string text = "<Path Fill=\"White\" Width=\"16\" />";
            Assert.AreEqual(expected, StylePlanner.TryFindAt(text, caret).Selected.Single().Name);
        }

        [TestMethod]
        public void TryFindAt_OnTheElementName_Nothing()
        {
            Assert.IsNull(StylePlanner.TryFindAt("<Path Fill=\"White\" />", 2));
        }

        [TestMethod]
        public void VisibleEntries_LookupOrder_FirstKeyWins_OnlyAboveTheElement()
        {
            using (var solution = new TempSolution())
            {
                solution.Write("App.csproj", "<Project />");
                solution.Write("App.xaml",
                    $"<Application x:Class=\"T.App\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\"><Application.Resources><ResourceDictionary>" +
                    "<ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"Brushes.xaml\" /></ResourceDictionary.MergedDictionaries>" +
                    "</ResourceDictionary></Application.Resources></Application>");
                solution.Write("Brushes.xaml", Xaml.Dictionary("<SolidColorBrush x:Key=\"A\" Color=\"Red\" />\n<SolidColorBrush x:Key=\"B\" Color=\"Red\" />"));
                solution.Write("Unmerged.xaml", Xaml.Dictionary("<SolidColorBrush x:Key=\"Lost\" Color=\"Red\" />"));
                string window = solution.Write("MainWindow.xaml",
                    $"<Window x:Class=\"T.MainWindow\" xmlns=\"{Xaml.Presentation}\" xmlns:x=\"{Xaml.X}\">\n" +
                    "<Window.Resources>\n<SolidColorBrush x:Key=\"A\" Color=\"Blue\" />\n</Window.Resources>\n" +
                    "<Grid>\n<Grid.Resources>\n<SolidColorBrush x:Key=\"Later\" Color=\"Red\" />\n</Grid.Resources>\n</Grid>\n</Window>");

                MergeGraph graph = ResourceIndex.ForFile(window).GetMergeGraph(CancellationToken.None);
                IReadOnlyList<ResourceEntry> visible = ResourceMatcher.VisibleEntries(graph, window, line: 5);

                CollectionAssert.AreEqual(new[] { "A", "B" }, visible.Select(e => e.Key).ToArray());
                Assert.AreEqual("Blue", (string)visible[0].Element.Attribute("Color"));
            }
        }
    }
}
