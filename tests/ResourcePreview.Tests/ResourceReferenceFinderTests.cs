using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.QuickInfo;

namespace ResourcePreview.Tests
{
    /// <summary>Finds the key under the mouse. The position is an index in the line (0 = first character).</summary>
    [TestClass]
    public class ResourceReferenceFinderTests
    {
        private static ResourceReference FindAt(string line, string hoverText, int offsetInHoverText = 1)
        {
            int position = line.IndexOf(hoverText) + offsetInHoverText;
            Assert.IsTrue(ResourceReferenceFinder.TryFind(line, position, out ResourceReference reference), $"Nothing found at position {position} in: {line}");
            return reference;
        }

        [TestMethod]
        public void StaticResource_KeyIsFound()
        {
            var reference = FindAt("<Path Data=\"{StaticResource BuildInstaller}\" />", "BuildInstaller");
            Assert.AreEqual("BuildInstaller", reference.Key);
        }

        [TestMethod]
        public void DynamicResource_KeyIsFound()
        {
            var reference = FindAt("<Path Fill=\"{DynamicResource AccentBrush}\" />", "AccentBrush");
            Assert.AreEqual("AccentBrush", reference.Key);
        }

        [TestMethod]
        public void ResourceKeySyntax_KeyIsFound()
        {
            var reference = FindAt("<Image Source=\"{StaticResource ResourceKey=Report}\" />", "Report");
            Assert.AreEqual("Report", reference.Key);
        }

        [TestMethod]
        public void ExtraSpaces_KeyIsFound()
        {
            var reference = FindAt("<Path Data=\"{ StaticResource   Renew }\" />", "Renew");
            Assert.AreEqual("Renew", reference.Key);
        }

        [TestMethod]
        public void HoverOnMarkupExtensionName_AlsoCounts()
        {
            // The whole {StaticResource X} is the hover area, not just the key.
            var reference = FindAt("<Path Data=\"{StaticResource Renew}\" />", "StaticResource");
            Assert.AreEqual("Renew", reference.Key);
        }

        [TestMethod]
        public void ApplicableSpan_CoversWholeMarkupExtension()
        {
            const string line = "<Path Data=\"{StaticResource Renew}\" />";
            var reference = FindAt(line, "Renew");
            Assert.AreEqual("{StaticResource Renew}", line.Substring(reference.Start, reference.Length));
        }

        [TestMethod]
        public void ElementSyntax_KeyIsFound()
        {
            var reference = FindAt("<StaticResource ResourceKey=\"Folder\" />", "Folder");
            Assert.AreEqual("Folder", reference.Key);
        }

        [TestMethod]
        public void KeyDefinition_KeyIsFound()
        {
            var reference = FindAt("<GeometryGroup x:Key=\"Check\">", "Check");
            Assert.AreEqual("Check", reference.Key);
        }

        [TestMethod]
        public void NestedInBinding_InnerReferenceIsFound()
        {
            var reference = FindAt("<TextBlock Text=\"{Binding Source={StaticResource Model}, Path=Name}\" />", "Model");
            Assert.AreEqual("Model", reference.Key);
        }

        [TestMethod]
        public void TwoReferencesOnOneLine_TheOneUnderTheMouseWins()
        {
            const string line = "<Path Data=\"{StaticResource Renew}\" Fill=\"{StaticResource AccentBrush}\" />";
            Assert.AreEqual("Renew", FindAt(line, "Renew").Key);
            Assert.AreEqual("AccentBrush", FindAt(line, "AccentBrush").Key);
        }

        [TestMethod]
        public void LastCharacterOfReference_StillCounts()
        {
            const string line = "<Path Data=\"{StaticResource Renew}\" />";
            int closingBrace = line.IndexOf('}');
            Assert.IsTrue(ResourceReferenceFinder.TryFind(line, closingBrace + 1, out _));
        }

        [TestMethod]
        public void OutsideReference_NothingFound()
        {
            const string line = "<Path Data=\"{StaticResource Renew}\" Width=\"16\" />";
            Assert.IsFalse(ResourceReferenceFinder.TryFind(line, line.IndexOf("Width"), out _));
        }

        [TestMethod]
        public void OtherMarkupExtension_NothingFound()
        {
            const string line = "<TextBlock Text=\"{Binding Title}\" />";
            Assert.IsFalse(ResourceReferenceFinder.TryFind(line, line.IndexOf("Title"), out _));
        }

        [TestMethod]
        public void TypedKey_IsIgnored()
        {
            // x:Key="{x:Type Button}" is not a name that can be looked up.
            const string line = "<Style x:Key=\"{x:Type Button}\">";
            Assert.IsFalse(ResourceReferenceFinder.TryFind(line, line.IndexOf("Button"), out _));
        }

        [TestMethod]
        public void EmptyLine_NothingFound()
        {
            Assert.IsFalse(ResourceReferenceFinder.TryFind(string.Empty, 0, out _));
        }

        // ---------- TryFindReference (Go To Resource Definition) ----------

        [TestMethod]
        [DataRow("<Path Data=\"{StaticResource Renew}\" />", "Renew")]
        [DataRow("<Path Fill=\"{DynamicResource AccentBrush}\" />", "AccentBrush")]
        [DataRow("<Image Source=\"{StaticResource ResourceKey=Report}\" />", "Report")]
        [DataRow("<StaticResource ResourceKey=\"Check\" />", "Check")]
        public void TryFindReference_FindsReferences(string line, string key)
        {
            Assert.IsTrue(ResourceReferenceFinder.TryFindReference(line, line.IndexOf(key) + 1, out ResourceReference reference));
            Assert.AreEqual(key, reference.Key);
        }

        [TestMethod]
        public void TryFindReference_IgnoresDefinitions()
        {
            // On x:Key the caret is already at the definition, so the command has nothing to do.
            const string line = "<Geometry x:Key=\"Renew\">M0,0 L1,1</Geometry>";
            Assert.IsTrue(ResourceReferenceFinder.TryFind(line, line.IndexOf("Renew") + 1, out _), "the preview does show the definition");
            Assert.IsFalse(ResourceReferenceFinder.TryFindReference(line, line.IndexOf("Renew") + 1, out _));
        }

        [TestMethod]
        public void TryFindReference_DefinitionAndReferenceOnOneLine()
        {
            const string line = "<Style x:Key=\"Danger\" BasedOn=\"{StaticResource Primary}\">";
            Assert.IsFalse(ResourceReferenceFinder.TryFindReference(line, line.IndexOf("Danger") + 1, out _));
            Assert.IsTrue(ResourceReferenceFinder.TryFindReference(line, line.IndexOf("Primary") + 1, out ResourceReference reference));
            Assert.AreEqual("Primary", reference.Key);
        }
    }
}
