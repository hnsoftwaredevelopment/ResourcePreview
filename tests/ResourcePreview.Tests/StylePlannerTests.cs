using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>Extract to style and Add to style: selected attributes become setters.</summary>
    [TestClass]
    public class StylePlannerTests
    {
        private const string IconLine = "<Path Data=\"{StaticResource Search}\" Fill=\"White\" Stretch=\"Uniform\" Width=\"16\" Height=\"16\" Margin=\"0,0,8,0\" />";

        private static AttributeSelection Select(string text, string from, string to)
        {
            int start = text.IndexOf(from);
            int end = text.IndexOf(to) + to.Length;
            return StylePlanner.TryFind(text, start, end);
        }

        [TestMethod]
        public void TryFind_TheSelectedAttributes()
        {
            AttributeSelection selection = Select(IconLine, "Fill=", "\"0,0,8,0\"");

            Assert.AreEqual("Path", selection.ElementName);
            CollectionAssert.AreEqual(new[] { "Fill", "Stretch", "Width", "Height", "Margin" }, selection.Selected.Select(a => a.Name).ToArray());
            Assert.IsNull(selection.StyleKey);
        }

        [TestMethod]
        public void ExtractToStyle_NewStyleAndElement()
        {
            AttributeSelection selection = Select(IconLine, "Fill=", "\"0,0,8,0\"");

            CollectionAssert.AreEqual(
                new[]
                {
                    "<Style x:Key=\"IconStyle\" TargetType=\"Path\">",
                    "    <Setter Property=\"Fill\" Value=\"White\" />",
                    "    <Setter Property=\"Stretch\" Value=\"Uniform\" />",
                    "    <Setter Property=\"Width\" Value=\"16\" />",
                    "    <Setter Property=\"Height\" Value=\"16\" />",
                    "    <Setter Property=\"Margin\" Value=\"0,0,8,0\" />",
                    "</Style>",
                },
                StylePlanner.StyleLines(selection, "IconStyle").ToArray());

            Assert.AreEqual(
                "<Path Data=\"{StaticResource Search}\" Style=\"{StaticResource IconStyle}\" />",
                TextChange.Apply(IconLine, StylePlanner.PointElementAtStyle(selection, "IconStyle")));
            Assert.AreEqual("PathStyle", StylePlanner.SuggestKey(selection));
        }

        [TestMethod]
        public void ExtractToStyle_ExistingStyle_BecomesBasedOn()
        {
            string text = "<Button Style=\"{StaticResource BaseButton}\" Padding=\"0\" Margin=\"4\" />";
            AttributeSelection selection = Select(text, "Padding", "\"4\"");

            Assert.AreEqual("BaseButton", selection.StyleKey);
            Assert.AreEqual("<Style x:Key=\"New\" TargetType=\"Button\" BasedOn=\"{StaticResource BaseButton}\">", StylePlanner.StyleLines(selection, "New")[0]);
            Assert.AreEqual("<Button Style=\"{StaticResource New}\" />", TextChange.Apply(text, StylePlanner.PointElementAtStyle(selection, "New")));
        }

        [TestMethod]
        public void TryFind_SkipsEventsNamesAndKeys()
        {
            string text = "<Button x:Name=\"Ok\" Click=\"OnClick\" MouseEnter=\"OnEnter\" Margin=\"4\" />";
            AttributeSelection selection = Select(text, "x:Name", "\"4\"");
            CollectionAssert.AreEqual(new[] { "Margin" }, selection.Selected.Select(a => a.Name).ToArray());
        }

        [TestMethod]
        [DataRow("<local:Fancy Width=\"4\" />", "Width", "\"4\"")]
        [DataRow("<Button Style=\"{x:Null}\" Width=\"4\" />", "Width", "\"4\"")]
        [DataRow("<Button Click=\"OnClick\" />", "Click", "\"OnClick\"")]
        [DataRow("<Grid>\n  <Button Width=\"4\" />\n</Grid>", "<Grid>", "\"4\"")]
        public void TryFind_NothingToExtract(string text, string from, string to)
        {
            Assert.IsNull(Select(text, from, to));
        }

        [TestMethod]
        public void TryFind_MultiLineTag_KeepsItTidy()
        {
            string text = "<Path Data=\"{StaticResource Search}\"\n      Fill=\"White\"\n      Width=\"16\" />";
            AttributeSelection selection = Select(text, "Fill", "\"16\"");

            Assert.AreEqual(
                "<Path Data=\"{StaticResource Search}\" Style=\"{StaticResource S}\" />",
                TextChange.Apply(text, StylePlanner.PointElementAtStyle(selection, "S")));
        }

        // --- Add to an existing style ---

        private const string Styles =
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "    <Style x:Key=\"BaseButton\" TargetType=\"Button\">\n" +
            "        <Setter Property=\"Padding\" Value=\"8\" />\n" +
            "        <Setter Property=\"FontSize\" Value=\"13\" />\n" +
            "        <Style.Triggers>\n" +
            "            <Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter Property=\"Opacity\" Value=\"0.8\" /></Trigger>\n" +
            "        </Style.Triggers>\n" +
            "    </Style>\n" +
            "    <Style x:Key=\"Empty\" TargetType=\"Button\" />\n" +
            "</ResourceDictionary>";

        [TestMethod]
        public void AddSetters_AfterTheLastSetter_AndReplacesExistingOnes()
        {
            string element = "<Button Style=\"{StaticResource BaseButton}\" Padding=\"0\" Margin=\"4\" />";
            AttributeSelection selection = Select(element, "Padding", "\"4\"");

            string result = TextChange.Apply(Styles, StylePlanner.AddSetters(Styles, "BaseButton", 2, selection.Selected, out int replaced));

            Assert.AreEqual(1, replaced);
            StringAssert.Contains(result,
                "        <Setter Property=\"Padding\" Value=\"0\" />\n" +
                "        <Setter Property=\"FontSize\" Value=\"13\" />\n" +
                "        <Setter Property=\"Margin\" Value=\"4\" />\n" +
                "        <Style.Triggers>");
            System.Xml.Linq.XDocument.Parse(result);

            Assert.AreEqual("<Button Style=\"{StaticResource BaseButton}\" />", TextChange.Apply(element, StylePlanner.RemoveSelected(selection)));
        }

        [TestMethod]
        public void AddSetters_SelfClosingStyle_IsOpened()
        {
            AttributeSelection selection = Select("<Button Style=\"{StaticResource Empty}\" Margin=\"4\" />", "Margin", "\"4\"");

            string result = TextChange.Apply(Styles, StylePlanner.AddSetters(Styles, "Empty", 9, selection.Selected, out _));

            StringAssert.Contains(result, "    <Style x:Key=\"Empty\" TargetType=\"Button\">\n        <Setter Property=\"Margin\" Value=\"4\" />\n    </Style>");
            System.Xml.Linq.XDocument.Parse(result);
        }

        [TestMethod]
        public void AddSetters_UnknownStyle_NoChanges()
        {
            AttributeSelection selection = Select("<Button Style=\"{StaticResource Empty}\" Margin=\"4\" />", "Margin", "\"4\"");
            Assert.AreEqual(0, StylePlanner.AddSetters(Styles, "Missing", 2, selection.Selected, out _).Count);
        }
    }
}
