using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using ResourcePreview.Localization;

namespace ResourcePreview.Options
{
    /// <summary>
    /// Languages offered in the options. The declaration order is the order in the drop-down list.
    /// </summary>
    [TypeConverter(typeof(ExtensionLanguageConverter))]
    public enum ExtensionLanguage
    {
        Automatic,
        English,
        ChineseSimplified,
        ChineseTraditional,
        Czech,
        Dutch,
        French,
        German,
        Italian,
        Japanese,
        Korean,
        Polish,
        PortugueseBrazil,
        Russian,
        Spanish,
        Turkish,
    }

    /// <summary>
    /// Maps an <see cref="ExtensionLanguage"/> to a .NET culture and its native display name.
    /// </summary>
    internal static class LanguageInfo
    {
        private sealed class Entry
        {
            public Entry(ExtensionLanguage language, string cultureName, string nativeName, bool visualStudioLanguage)
            {
                Language = language;
                CultureName = cultureName;
                NativeName = nativeName;
                IsVisualStudioLanguage = visualStudioLanguage;
            }

            public ExtensionLanguage Language { get; }
            public string CultureName { get; }
            public string NativeName { get; }

            /// <summary>Whether VS ships in this language. Automatic only picks these languages.</summary>
            public bool IsVisualStudioLanguage { get; }
        }

        // Native names, as Windows and VS show them in their own language lists.
        private static readonly Entry[] Entries =
        {
            new Entry(ExtensionLanguage.English, "en", "English", true),
            new Entry(ExtensionLanguage.ChineseSimplified, "zh-Hans", "中文(简体)", true),
            new Entry(ExtensionLanguage.ChineseTraditional, "zh-Hant", "中文(繁體)", true),
            new Entry(ExtensionLanguage.Czech, "cs", "Čeština", true),
            new Entry(ExtensionLanguage.Dutch, "nl", "Nederlands", false),
            new Entry(ExtensionLanguage.French, "fr", "Français", true),
            new Entry(ExtensionLanguage.German, "de", "Deutsch", true),
            new Entry(ExtensionLanguage.Italian, "it", "Italiano", true),
            new Entry(ExtensionLanguage.Japanese, "ja", "日本語", true),
            new Entry(ExtensionLanguage.Korean, "ko", "한국어", true),
            new Entry(ExtensionLanguage.Polish, "pl", "Polski", true),
            new Entry(ExtensionLanguage.PortugueseBrazil, "pt-BR", "Português (Brasil)", true),
            new Entry(ExtensionLanguage.Russian, "ru", "Русский", true),
            new Entry(ExtensionLanguage.Spanish, "es", "Español", true),
            new Entry(ExtensionLanguage.Turkish, "tr", "Türkçe", true),
        };

        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

        public static CultureInfo ToCulture(ExtensionLanguage language)
        {
            Entry entry = Entries.FirstOrDefault(e => e.Language == language);
            return entry == null ? English : CultureInfo.GetCultureInfo(entry.CultureName);
        }

        public static string GetDisplayName(ExtensionLanguage language)
        {
            if (language == ExtensionLanguage.Automatic)
            {
                return Localizer.Get(Strings.LanguageAutomatic);
            }

            return Entries.FirstOrDefault(e => e.Language == language)?.NativeName ?? language.ToString();
        }

        /// <summary>
        /// Finds the best matching VS language by walking the culture's parents, so "zh-TW" (as
        /// returned by VS) matches "zh-Hant". Falls back to English.
        /// </summary>
        public static CultureInfo MatchVisualStudioLanguage(CultureInfo culture)
        {
            for (CultureInfo current = culture; current != null && !current.Equals(CultureInfo.InvariantCulture); current = current.Parent)
            {
                Entry entry = Entries.FirstOrDefault(e =>
                    e.IsVisualStudioLanguage && string.Equals(e.CultureName, current.Name, StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    return CultureInfo.GetCultureInfo(entry.CultureName);
                }
            }

            return English;
        }

        /// <summary>The value stored in registration.json: "automatic" or a culture name such as "de" or "zh-Hans".</summary>
        public static string ToSettingValue(ExtensionLanguage language)
        {
            if (language == ExtensionLanguage.Automatic)
            {
                return "automatic";
            }

            return Entries.FirstOrDefault(e => e.Language == language)?.CultureName ?? "automatic";
        }

        public static ExtensionLanguage FromSettingValue(string value)
        {
            Entry entry = Entries.FirstOrDefault(e => string.Equals(e.CultureName, value, StringComparison.OrdinalIgnoreCase));
            return entry?.Language ?? ExtensionLanguage.Automatic;
        }

        public static IEnumerable<ExtensionLanguage> All => Enum.GetValues(typeof(ExtensionLanguage)).Cast<ExtensionLanguage>();
    }

    /// <summary>
    /// Shows native language names in the options PropertyGrid.
    /// DialogPage also persists values through this converter, so ConvertFrom accepts both the
    /// display name and the enum name.
    /// </summary>
    internal sealed class ExtensionLanguageConverter : EnumConverter
    {
        public ExtensionLanguageConverter()
            : base(typeof(ExtensionLanguage))
        {
        }

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is ExtensionLanguage language)
            {
                return LanguageInfo.GetDisplayName(language);
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }

        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
        {
            if (value is string text)
            {
                foreach (ExtensionLanguage language in LanguageInfo.All)
                {
                    if (text == LanguageInfo.GetDisplayName(language) || string.Equals(text, language.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        return language;
                    }
                }

                // Includes the "Automatic" display name from a language that is no longer active.
                return ExtensionLanguage.Automatic;
            }

            return base.ConvertFrom(context, culture, value);
        }
    }
}
