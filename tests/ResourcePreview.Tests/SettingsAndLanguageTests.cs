using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Localization;
using ResourcePreview.Options;

namespace ResourcePreview.Tests
{
    [TestClass]
    public class ExtensionSettingsTests
    {
        private static ExtensionSettings Make(int maxListed = 5, int tileSize = 64) => new ExtensionSettings(
            ExtensionLanguage.Automatic, maxListed, tileSize, PreviewTiles.Both, Colors.Black, Colors.White, Colors.White, Colors.Black);

        [TestMethod]
        public void Defaults_MatchDocumentedValues()
        {
            var d = ExtensionSettings.Default;
            Assert.AreEqual(ExtensionLanguage.Automatic, d.Language);
            Assert.AreEqual(5, d.MaxListedDefinitions);
            Assert.AreEqual(64, d.TileSize);
            Assert.AreEqual(PreviewTiles.Both, d.Tiles);
            Assert.AreEqual(Colors.Black, d.LightForeground);
            Assert.AreEqual(Colors.White, d.LightBackground);
            Assert.AreEqual(Colors.White, d.DarkForeground);
            Assert.AreEqual(Color.FromRgb(0x1E, 0x1E, 0x1E), d.DarkBackground);
        }

        [TestMethod]
        [DataRow(0, 16)]
        [DataRow(15, 16)]
        [DataRow(16, 16)]
        [DataRow(100, 100)]
        [DataRow(256, 256)]
        [DataRow(1000, 256)]
        public void TileSize_IsClamped(int input, int expected)
        {
            Assert.AreEqual(expected, Make(tileSize: input).TileSize);
        }

        [TestMethod]
        [DataRow(-1, 0)]
        [DataRow(0, 0)]
        [DataRow(5, 5)]
        [DataRow(50, 50)]
        [DataRow(51, 50)]
        public void MaxListedDefinitions_IsClamped(int input, int expected)
        {
            Assert.AreEqual(expected, Make(maxListed: input).MaxListedDefinitions);
        }

        [TestMethod]
        [DataRow(PreviewTiles.Both, "both")]
        [DataRow(PreviewTiles.Light, "light")]
        [DataRow(PreviewTiles.Dark, "dark")]
        public void Tiles_RoundTrip(PreviewTiles tiles, string setting)
        {
            Assert.AreEqual(setting, ExtensionSettings.TilesToSetting(tiles));
            Assert.AreEqual(tiles, ExtensionSettings.TilesFromSetting(setting));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("rubbish")]
        public void Tiles_UnknownValue_IsBoth(string setting)
        {
            Assert.AreEqual(PreviewTiles.Both, ExtensionSettings.TilesFromSetting(setting));
        }

        [TestMethod]
        [DataRow("#1E1E1E", "#1E1E1E")]
        [DataRow("#1e1e1e", "#1E1E1E")]
        [DataRow("#801E1E1E", "#801E1E1E")]
        [DataRow("#FF1E1E1E", "#1E1E1E")]
        [DataRow("Red", "#FF0000")]
        [DataRow("  SteelBlue  ", "#4682B4")]
        public void Color_ParsesAndFormats(string input, string expected)
        {
            Color parsed = ExtensionSettings.ColorFromSetting(input, Colors.Magenta);
            Assert.AreEqual(expected, ExtensionSettings.ColorToSetting(parsed));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("NoSuchColor")]
        [DataRow("#12")]
        public void Color_Invalid_UsesFallback(string input)
        {
            Assert.AreEqual(Colors.Magenta, ExtensionSettings.ColorFromSetting(input, Colors.Magenta));
        }
    }

    [TestClass]
    public class LanguageInfoTests
    {
        [TestMethod]
        [DataRow(1033, "en")]
        [DataRow(2052, "zh-Hans")]
        [DataRow(1028, "zh-Hant")]
        [DataRow(1029, "cs")]
        [DataRow(1031, "de")]
        [DataRow(1036, "fr")]
        [DataRow(1040, "it")]
        [DataRow(1041, "ja")]
        [DataRow(1042, "ko")]
        [DataRow(1045, "pl")]
        [DataRow(1046, "pt-BR")]
        [DataRow(1049, "ru")]
        [DataRow(3082, "es")]
        [DataRow(1055, "tr")]
        public void VisualStudioLanguage_MatchesOwnTranslation(int lcid, string expected)
        {
            Assert.AreEqual(expected, LanguageInfo.MatchVisualStudioLanguage(CultureInfo.GetCultureInfo(lcid)).Name);
        }

        [TestMethod]
        [DataRow("nl-NL")]
        [DataRow("nl-BE")]
        [DataRow("pt-PT")]
        [DataRow("sv-SE")]
        public void NotAVisualStudioLanguage_FallsBackToEnglish(string culture)
        {
            // Dutch is a translation of the extension, but VS itself has no Dutch UI, so Automatic never picks it.
            Assert.AreEqual("en", LanguageInfo.MatchVisualStudioLanguage(CultureInfo.GetCultureInfo(culture)).Name);
        }

        [TestMethod]
        [DataRow("de-AT", "de")]
        [DataRow("fr-CA", "fr")]
        [DataRow("es-MX", "es")]
        public void RegionalVariant_MatchesParentLanguage(string culture, string expected)
        {
            Assert.AreEqual(expected, LanguageInfo.MatchVisualStudioLanguage(CultureInfo.GetCultureInfo(culture)).Name);
        }

        [TestMethod]
        public void EveryLanguage_RoundTripsThroughSettingValue()
        {
            foreach (ExtensionLanguage language in LanguageInfo.All)
            {
                Assert.AreEqual(language, LanguageInfo.FromSettingValue(LanguageInfo.ToSettingValue(language)), language.ToString());
            }
        }

        [TestMethod]
        public void UnknownSettingValue_IsAutomatic()
        {
            Assert.AreEqual(ExtensionLanguage.Automatic, LanguageInfo.FromSettingValue("xx"));
            Assert.AreEqual(ExtensionLanguage.Automatic, LanguageInfo.FromSettingValue(null));
        }

        [TestMethod]
        public void EveryFixedLanguage_HasCultureAndUniqueNativeName()
        {
            var fixedLanguages = LanguageInfo.All.Where(l => l != ExtensionLanguage.Automatic).ToList();
            foreach (ExtensionLanguage language in fixedLanguages)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(LanguageInfo.GetDisplayName(language)), $"{language} has no name");
                Assert.IsFalse(string.IsNullOrEmpty(LanguageInfo.ToCulture(language).Name), $"{language} has no culture");
            }

            // Two languages with the same display name would make the converter read back the wrong one.
            var names = fixedLanguages.Select(LanguageInfo.GetDisplayName).ToList();
            Assert.AreEqual(names.Count, names.Distinct().Count(), "duplicate names in the language list");
            Assert.AreEqual("Deutsch", LanguageInfo.GetDisplayName(ExtensionLanguage.German));
            Assert.AreEqual("日本語", LanguageInfo.GetDisplayName(ExtensionLanguage.Japanese));
        }

        [TestMethod]
        public void FifteenLanguagesPlusAutomatic()
        {
            Assert.AreEqual(16, LanguageInfo.All.Count());
        }
    }

    [TestClass]
    public class LocalizerAndConverterTests
    {
        [TestCleanup]
        public void Cleanup() => Localizer.SetCulture(CultureInfo.GetCultureInfo("en"));

        [TestMethod]
        [DataRow("en", "{0}, line {1}")]
        [DataRow("nl", "{0}, regel {1}")]
        [DataRow("de", "{0}, Zeile {1}")]
        [DataRow("ja", "{0}、{1} 行目")]
        public void Get_ReturnsTextInChosenLanguage(string culture, string expected)
        {
            Localizer.SetCulture(CultureInfo.GetCultureInfo(culture));
            Assert.AreEqual(expected, Localizer.Get(Strings.LocationFormat));
        }

        [TestMethod]
        public void Format_FillsPlaceholders()
        {
            Localizer.SetCulture(CultureInfo.GetCultureInfo("nl"));
            Assert.AreEqual(@"Resources\Icons.xaml, regel 5", Localizer.Format(Strings.LocationFormat, @"Resources\Icons.xaml", 5));
        }

        [TestMethod]
        public void Get_UnknownName_ReturnsName()
        {
            Assert.AreEqual("NoSuchText", Localizer.Get("NoSuchText"));
        }

        [TestMethod]
        public void SetCulture_Null_IsEnglish()
        {
            Localizer.SetCulture(null);
            Assert.AreEqual("en", Localizer.Culture.Name);
        }

        [TestMethod]
        public void LanguageConverter_ShowsNativeNames_AndReadsThemBack()
        {
            TypeConverter converter = TypeDescriptor.GetConverter(typeof(ExtensionLanguage));
            Assert.AreEqual("Deutsch", converter.ConvertToString(ExtensionLanguage.German));
            Assert.AreEqual(ExtensionLanguage.German, converter.ConvertFromString("Deutsch"));
            Assert.AreEqual(ExtensionLanguage.German, converter.ConvertFromString("German"), "enum name must also work (DialogPage sometimes stores it that way)");
        }

        [TestMethod]
        public void LanguageConverter_AutomaticInOtherLanguage_IsStillAutomatic()
        {
            // The options page stores the display name, which must still be recognized after a language change.
            Localizer.SetCulture(CultureInfo.GetCultureInfo("nl"));
            TypeConverter converter = TypeDescriptor.GetConverter(typeof(ExtensionLanguage));
            string dutchAutomatic = converter.ConvertToString(ExtensionLanguage.Automatic);

            Localizer.SetCulture(CultureInfo.GetCultureInfo("en"));
            Assert.AreEqual(ExtensionLanguage.Automatic, converter.ConvertFromString(dutchAutomatic));
        }

        [TestMethod]
        public void TilesConverter_IsLocalized_AndReadsBack()
        {
            Localizer.SetCulture(CultureInfo.GetCultureInfo("nl"));
            TypeConverter converter = TypeDescriptor.GetConverter(typeof(PreviewTiles));

            Assert.AreEqual("Alleen donker", converter.ConvertToString(PreviewTiles.Dark));
            Assert.AreEqual(PreviewTiles.Dark, converter.ConvertFromString("Alleen donker"));
            Assert.AreEqual(PreviewTiles.Light, converter.ConvertFromString("Light"));
        }

        [TestMethod]
        public void LocalizedAttributes_ReadCurrentLanguage()
        {
            Localizer.SetCulture(CultureInfo.GetCultureInfo("de"));
            Assert.AreEqual("Sprache", new LocalizedDisplayNameAttribute(Strings.OptionsLanguageName).DisplayName);
            StringAssert.StartsWith(new LocalizedDescriptionAttribute(Strings.OptionsLanguageDescription).Description, "Sprache");
        }
    }
}
