using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Resources;

namespace ResourcePreview.Tests
{
    /// <summary>Rename Resource Key: where the text changes, which files, and which names are allowed.</summary>
    [TestClass]
    public class KeyRenamerTests
    {
        [TestMethod]
        public void Apply_RenamesDefinitionsAndAllReferenceForms()
        {
            string text =
                "<SolidColorBrush x:Key=\"Accent\" Color=\"Red\" />\n" +
                "<Style x:Key='S' BasedOn=\"{StaticResource Accent}\" />\n" +
                "<Border Background=\"{DynamicResource ResourceKey=Accent}\" BorderBrush=\"{StaticResource Accent }\" />\n" +
                "<Binding Source=\"{StaticResource Accent}\" Path=\"Color\" />\n" +
                "<StaticResource ResourceKey=\"Accent\" />";

            string expected =
                "<SolidColorBrush x:Key=\"Primary\" Color=\"Red\" />\n" +
                "<Style x:Key='S' BasedOn=\"{StaticResource Primary}\" />\n" +
                "<Border Background=\"{DynamicResource ResourceKey=Primary}\" BorderBrush=\"{StaticResource Primary }\" />\n" +
                "<Binding Source=\"{StaticResource Primary}\" Path=\"Color\" />\n" +
                "<StaticResource ResourceKey=\"Primary\" />";

            Assert.AreEqual(expected, KeyRenamer.Apply(text, "Accent", "Primary"));
        }

        [TestMethod]
        public void Apply_LeavesOtherKeysTextAndCommentsAlone()
        {
            string text =
                "<!-- {StaticResource Accent} -->\n" +
                "<Border Background=\"{StaticResource AccentBrush}\" Tag=\"Accent\" />\n" +
                "<SolidColorBrush x:Key=\"AccentDark\" />\n" +
                "<TextBlock Text=\"Accent\" />";

            Assert.AreEqual(text, KeyRenamer.Apply(text, "Accent", "Primary"));
        }

        [TestMethod]
        public void Apply_KeyWithDots()
        {
            string text = "<Border Background=\"{StaticResource Brushes.Accent}\" Tag=\"{StaticResource BrushesXAccent}\" />";
            Assert.AreEqual(
                "<Border Background=\"{StaticResource Brushes.Primary}\" Tag=\"{StaticResource BrushesXAccent}\" />",
                KeyRenamer.Apply(text, "Brushes.Accent", "Brushes.Primary"));
        }

        [TestMethod]
        public void FindSpans_PointAtTheKey()
        {
            string text = "<Border Background=\"{StaticResource Accent}\" />";
            KeySpan span = KeyRenamer.FindSpans(text, "Accent").Single();
            Assert.AreEqual("Accent", text.Substring(span.Start, span.Length));
        }

        [TestMethod]
        public void Plan_And_Check_OnTheSampleApp()
        {
            string main = Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            ResourceIndex index = ResourceIndex.ForFile(main);

            // InfoBrush: defined in Brushes.xaml and Values.xaml, used in MainWindow.xaml.
            var plan = KeyRenamer.Plan(index, "InfoBrush", CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "MainWindow.xaml", "Brushes.xaml", "Values.xaml" }, plan.Select(f => Path.GetFileName(f.FilePath)).ToArray());
            Assert.AreEqual(2, plan.Sum(f => f.Definitions));
            Assert.AreEqual(1, plan.Sum(f => f.References));

            Assert.IsNull(KeyRenamer.Check(index, "InfoBrush", "NoticeBrush", CancellationToken.None));
            Assert.AreEqual(RenameRejection.Exists, KeyRenamer.Check(index, "InfoBrush", "AccentBrush", CancellationToken.None));
            Assert.AreEqual(RenameRejection.Unchanged, KeyRenamer.Check(index, "InfoBrush", "InfoBrush", CancellationToken.None));
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("Two words")]
        [DataRow("Quote\"")]
        [DataRow("Brace}")]
        [DataRow("A,B")]
        [DataRow("A=B")]
        [DataRow("A<B")]
        public void Check_RejectsInvalidNames(string name)
        {
            string main = Path.Combine(RepoPaths.SampleApp, "MainWindow.xaml");
            Assert.AreEqual(RenameRejection.Invalid, KeyRenamer.Check(ResourceIndex.ForFile(main), "InfoBrush", name, CancellationToken.None));
        }
    }
}
