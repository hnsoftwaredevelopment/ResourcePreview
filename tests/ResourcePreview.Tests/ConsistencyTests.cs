using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Localization;
using ResourcePreview.Options;

namespace ResourcePreview.Tests
{
    /// <summary>Cross-file checks for mistakes the compiler does not catch.</summary>
    [TestClass]
    public class ConsistencyTests
    {
        private const string PackageGuid = "69e55270-ef51-4488-9e95-cfd1a6852056";

        private static readonly string LocalizationFolder = RepoPaths.InSource("Localization");

        private static Dictionary<string, string> ReadResx(string path)
        {
            return XDocument.Load(path).Root.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name"), d => (string)d.Element("value"));
        }

        private static IEnumerable<string> TranslationFiles =>
            Directory.GetFiles(LocalizationFolder, "Strings.*.resx");

        private static Dictionary<string, object> ReadRegistration()
        {
            string text = File.ReadAllText(RepoPaths.InSource("Options", "registration.json"));
            string withoutComments = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(withoutComments);
        }

        private static Dictionary<string, object> RegistrationProperty(string moniker)
        {
            var properties = (Dictionary<string, object>)ReadRegistration()["properties"];
            Assert.IsTrue(properties.ContainsKey(moniker), $"{moniker} is missing from registration.json");
            return (Dictionary<string, object>)properties[moniker];
        }

        // ---------- Translations ----------

        [TestMethod]
        public void FourteenTranslations_Exist()
        {
            Assert.AreEqual(14, TranslationFiles.Count());
        }

        [TestMethod]
        public void EveryTranslation_HasExactlyTheEnglishKeys()
        {
            var english = ReadResx(Path.Combine(LocalizationFolder, "Strings.resx")).Keys.OrderBy(k => k).ToArray();

            foreach (string file in TranslationFiles)
            {
                var keys = ReadResx(file).Keys.OrderBy(k => k).ToArray();
                CollectionAssert.AreEqual(english, keys, Path.GetFileName(file) + " differs from Strings.resx");
            }
        }

        [TestMethod]
        public void EveryTranslation_HasSamePlaceholders()
        {
            // A translation that drops a placeholder such as {1} shows a gap or crashes.
            var english = ReadResx(Path.Combine(LocalizationFolder, "Strings.resx"));

            foreach (string file in TranslationFiles)
            {
                foreach (var pair in ReadResx(file))
                {
                    var expected = Regex.Matches(english[pair.Key], @"\{\d+\}").Cast<Match>().Select(m => m.Value).OrderBy(v => v);
                    var actual = Regex.Matches(pair.Value, @"\{\d+\}").Cast<Match>().Select(m => m.Value).OrderBy(v => v);
                    CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray(), $"{Path.GetFileName(file)}: {pair.Key}");
                }
            }
        }

        [TestMethod]
        public void NoTranslation_IsEmpty()
        {
            foreach (string file in TranslationFiles.Concat(new[] { Path.Combine(LocalizationFolder, "Strings.resx") }))
            {
                foreach (var pair in ReadResx(file))
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(pair.Value), $"{Path.GetFileName(file)}: {pair.Key} is empty");
                }
            }
        }

        [TestMethod]
        public void EveryStringsConstant_ExistsInResx()
        {
            var keys = ReadResx(Path.Combine(LocalizationFolder, "Strings.resx")).Keys;
            var constants = typeof(Strings).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral)
                .Select(f => (string)f.GetRawConstantValue());

            foreach (string name in constants)
            {
                Assert.IsTrue(keys.Contains(name), $"Strings.{name} does not exist in Strings.resx");
            }
        }

        [TestMethod]
        public void ResourceIds_ForLegacyOptionPage_Exist()
        {
            // [ProvideOptionPage(..., 100, 101, ...)]: VS looks up these IDs in VSPackage (our Strings.resx).
            var keys = ReadResx(Path.Combine(LocalizationFolder, "Strings.resx")).Keys;
            Assert.IsTrue(keys.Contains("100"));
            Assert.IsTrue(keys.Contains("101"));
        }

        // ---------- registration.json ----------

        [TestMethod]
        public void Registration_EveryLocalizedReference_Exists()
        {
            // Skip the header comment: its "@Name;{guid}" is an explanation, not a real reference.
            string text = Regex.Replace(File.ReadAllText(RepoPaths.InSource("Options", "registration.json")), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            var keys = ReadResx(Path.Combine(LocalizationFolder, "Strings.resx")).Keys;

            var references = Regex.Matches(text, "\"@([^;\"]+);\\{([^}]+)\\}\"").Cast<Match>().ToList();
            Assert.IsTrue(references.Count > 0);

            foreach (Match reference in references)
            {
                Assert.AreEqual(PackageGuid, reference.Groups[2].Value, "wrong package GUID for @" + reference.Groups[1].Value);
                Assert.IsTrue(keys.Contains(reference.Groups[1].Value), $"@{reference.Groups[1].Value} does not exist in Strings.resx");
            }
        }

        [TestMethod]
        public void Registration_MonikersMatchCode()
        {
            var properties = (Dictionary<string, object>)ReadRegistration()["properties"];
            CollectionAssert.AreEquivalent(SettingMonikers.All, properties.Keys.ToArray());
        }

        [TestMethod]
        public void Registration_EveryMonikerHasItsCategory()
        {
            var categories = (Dictionary<string, object>)ReadRegistration()["categories"];
            foreach (string moniker in SettingMonikers.All)
            {
                string category = moniker.Substring(0, moniker.LastIndexOf('.'));
                Assert.IsTrue(categories.ContainsKey(category), $"category {category} is missing for {moniker}");
            }
        }

        [TestMethod]
        public void Registration_DefaultsMatchCode()
        {
            var d = ExtensionSettings.Default;
            Assert.AreEqual(LanguageInfo.ToSettingValue(d.Language), RegistrationProperty(SettingMonikers.Language)["default"]);
            Assert.AreEqual(d.MaxListedDefinitions, RegistrationProperty(SettingMonikers.MaxListedDefinitions)["default"]);
            Assert.AreEqual(d.TileSize, RegistrationProperty(SettingMonikers.TileSize)["default"]);
            Assert.AreEqual(ExtensionSettings.TilesToSetting(d.Tiles), RegistrationProperty(SettingMonikers.Tiles)["default"]);
            Assert.AreEqual(ExtensionSettings.ColorToSetting(d.LightForeground), RegistrationProperty(SettingMonikers.LightForeground)["default"]);
            Assert.AreEqual(ExtensionSettings.ColorToSetting(d.LightBackground), RegistrationProperty(SettingMonikers.LightBackground)["default"]);
            Assert.AreEqual(ExtensionSettings.ColorToSetting(d.DarkForeground), RegistrationProperty(SettingMonikers.DarkForeground)["default"]);
            Assert.AreEqual(ExtensionSettings.ColorToSetting(d.DarkBackground), RegistrationProperty(SettingMonikers.DarkBackground)["default"]);
            Assert.AreEqual(d.ShowLiveSample, RegistrationProperty(SettingMonikers.ShowLiveSample)["default"]);
            Assert.AreEqual(d.WarnOverriddenKeys, RegistrationProperty(SettingMonikers.WarnOverriddenKeys)["default"]);
            Assert.AreEqual(d.ShowInCompletion, RegistrationProperty(SettingMonikers.ShowInCompletion)["default"]);
        }

        [TestMethod]
        public void Registration_RangesMatchCode()
        {
            var tileSize = RegistrationProperty(SettingMonikers.TileSize);
            Assert.AreEqual(ExtensionSettings.MinTileSize, tileSize["minimum"]);
            Assert.AreEqual(ExtensionSettings.MaxTileSize, tileSize["maximum"]);

            var maxListed = RegistrationProperty(SettingMonikers.MaxListedDefinitions);
            Assert.AreEqual(ExtensionSettings.MinListedDefinitions, maxListed["minimum"]);
            Assert.AreEqual(ExtensionSettings.MaxListedDefinitionsLimit, maxListed["maximum"]);
        }

        [TestMethod]
        public void Registration_LanguageChoices_CoverAllLanguages()
        {
            var language = RegistrationProperty(SettingMonikers.Language);
            var values = ((object[])language["enum"]).Cast<string>().ToArray();
            var labels = ((object[])language["enumItemLabels"]).Cast<string>().ToArray();

            Assert.AreEqual(values.Length, labels.Length, "enum and enumItemLabels must have the same length");
            CollectionAssert.AreEquivalent(LanguageInfo.All.Select(LanguageInfo.ToSettingValue).ToArray(), values);

            for (int i = 0; i < values.Length; i++)
            {
                ExtensionLanguage parsed = LanguageInfo.FromSettingValue(values[i]);
                if (parsed != ExtensionLanguage.Automatic)
                {
                    Assert.AreEqual(LanguageInfo.GetDisplayName(parsed), labels[i], $"label for '{values[i]}'");
                }
            }
        }

        [TestMethod]
        public void Registration_TileChoices_MatchCode()
        {
            var values = ((object[])RegistrationProperty(SettingMonikers.Tiles)["enum"]).Cast<string>().ToArray();
            var expected = Enum.GetValues(typeof(PreviewTiles)).Cast<PreviewTiles>().Select(ExtensionSettings.TilesToSetting).ToArray();
            CollectionAssert.AreEquivalent(expected, values);
        }

        [TestMethod]
        public void Registration_ColorPattern_AcceptsWhatCodeProduces()
        {
            var pattern = new Regex((string)RegistrationProperty(SettingMonikers.LightForeground)["pattern"]);
            foreach (string value in new[] { "#000000", "#1E1E1E", "#801E1E1E", "Red", "SteelBlue" })
            {
                Assert.IsTrue(pattern.IsMatch(value), value);
            }

            Assert.IsFalse(pattern.IsMatch("#12"));
        }

        [TestMethod]
        public void Registration_LegacyPageId_IsGeneralOptionsPageGuid()
        {
            var categories = (Dictionary<string, object>)ReadRegistration()["categories"];
            var general = (Dictionary<string, object>)categories["resourcePreview.general"];
            Guid pageGuid = typeof(GeneralOptionsPage).GUID;
            Assert.AreEqual(pageGuid, Guid.Parse((string)general["legacyOptionPageId"]));
        }

        [TestMethod]
        public void PackageGuid_IsTheSameEverywhere()
        {
            Assert.AreEqual(Guid.Parse(PackageGuid), Guid.Parse(ResourcePreviewPackage.PackageGuidString));
        }

        // ---------- Project ----------

        [TestMethod]
        public void EverySourceFile_IsInTheProject()
        {
            // The classic VSIX project lists every file. A .cs file missing from the list gives no error,
            // it is just not compiled.
            XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";
            var project = XDocument.Load(RepoPaths.InSource("ResourcePreview.csproj"));
            var compiled = project.Descendants(msbuild + "Compile").Select(c => ((string)c.Attribute("Include")).Replace('\\', '/')).ToList();

            var onDisk = Directory.GetFiles(RepoPaths.Source, "*.cs", SearchOption.AllDirectories)
                .Select(f => f.Substring(RepoPaths.Source.Length + 1).Replace('\\', '/'))
                .Where(f => !f.StartsWith("obj/") && !f.StartsWith("bin/"));

            foreach (string file in onDisk)
            {
                CollectionAssert.Contains(compiled, file, $"{file} is not in ResourcePreview.csproj");
            }
        }

        [TestMethod]
        public void EveryResx_IsEmbeddedAsVSPackage()
        {
            XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";
            var project = XDocument.Load(RepoPaths.InSource("ResourcePreview.csproj"));
            var resources = project.Descendants(msbuild + "EmbeddedResource")
                .ToDictionary(e => Path.GetFileName((string)e.Attribute("Include")), e => (string)e.Element(msbuild + "LogicalName"));

            foreach (string file in Directory.GetFiles(LocalizationFolder, "*.resx").Select(Path.GetFileName))
            {
                Assert.IsTrue(resources.ContainsKey(file), $"{file} is not in the .csproj");
                string culture = file == "Strings.resx" ? string.Empty : file.Substring("Strings.".Length, file.Length - "Strings.".Length - ".resx".Length) + ".";
                Assert.AreEqual($"VSPackage.{culture}resources", resources[file], file);
            }
        }
    }
}
