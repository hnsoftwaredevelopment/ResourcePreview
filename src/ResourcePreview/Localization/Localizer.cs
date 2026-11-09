using System;
using System.Globalization;
using System.Resources;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using ResourcePreview.Options;

namespace ResourcePreview.Localization
{
    /// <summary>
    /// Resource names in Strings.resx. Written by hand instead of a generated Designer class,
    /// because that class always uses the thread's CurrentUICulture.
    /// </summary>
    internal static class Strings
    {
        public const string LocationFormat = nameof(LocationFormat);
        public const string AlsoDefinedIn = nameof(AlsoDefinedIn);
        public const string AndMoreFormat = nameof(AndMoreFormat);
        public const string NotFoundUnderFormat = nameof(NotFoundUnderFormat);
        public const string CannotLoadFormat = nameof(CannotLoadFormat);
        public const string MissingInSolutionFormat = nameof(MissingInSolutionFormat);
        public const string NoPreviewForFormat = nameof(NoPreviewForFormat);
        public const string CannotOpenFileFormat = nameof(CannotOpenFileFormat);
        public const string OptionsCategoryGeneral = nameof(OptionsCategoryGeneral);
        public const string OptionsLanguageName = nameof(OptionsLanguageName);
        public const string OptionsLanguageDescription = nameof(OptionsLanguageDescription);
        public const string LanguageAutomatic = nameof(LanguageAutomatic);
        public const string OptionsRootDescription = nameof(OptionsRootDescription);
        public const string OptionsCategoryAppearance = nameof(OptionsCategoryAppearance);
        public const string OptionsMaxDefinitionsName = nameof(OptionsMaxDefinitionsName);
        public const string OptionsMaxDefinitionsDescription = nameof(OptionsMaxDefinitionsDescription);
        public const string OptionsTileSizeName = nameof(OptionsTileSizeName);
        public const string OptionsTileSizeDescription = nameof(OptionsTileSizeDescription);
        public const string OptionsTilesName = nameof(OptionsTilesName);
        public const string OptionsTilesDescription = nameof(OptionsTilesDescription);
        public const string TilesBoth = nameof(TilesBoth);
        public const string TilesLight = nameof(TilesLight);
        public const string TilesDark = nameof(TilesDark);
        public const string OptionsLightForegroundName = nameof(OptionsLightForegroundName);
        public const string OptionsLightBackgroundName = nameof(OptionsLightBackgroundName);
        public const string OptionsDarkForegroundName = nameof(OptionsDarkForegroundName);
        public const string OptionsDarkBackgroundName = nameof(OptionsDarkBackgroundName);
        public const string OptionsForegroundDescription = nameof(OptionsForegroundDescription);
        public const string OptionsBackgroundDescription = nameof(OptionsBackgroundDescription);
        public const string StyleInheritedFromFormat = nameof(StyleInheritedFromFormat);
        public const string StyleTriggersFormat = nameof(StyleTriggersFormat);
        public const string StyleNoSetters = nameof(StyleNoSetters);
        public const string ThicknessFormat = nameof(ThicknessFormat);
        public const string CornerRadiusFormat = nameof(CornerRadiusFormat);
        public const string FontNotInstalledFormat = nameof(FontNotInstalledFormat);
        public const string FontFileNotFoundFormat = nameof(FontFileNotFoundFormat);
        public const string SampleText = nameof(SampleText);
        public const string SampleNormal = nameof(SampleNormal);
        public const string SampleDisabled = nameof(SampleDisabled);
        public const string SampleMouseOver = nameof(SampleMouseOver);
        public const string SamplePressed = nameof(SamplePressed);
        public const string NoLiveSampleFormat = nameof(NoLiveSampleFormat);
        public const string TemplateMoreElementsFormat = nameof(TemplateMoreElementsFormat);
        public const string TemplateEmpty = nameof(TemplateEmpty);
        public const string NoLiveSampleOwnTypeFormat = nameof(NoLiveSampleOwnTypeFormat);
        public const string NoPreviewOwnTypeFormat = nameof(NoPreviewOwnTypeFormat);
        public const string OptionsLiveSampleName = nameof(OptionsLiveSampleName);
        public const string OptionsLiveSampleDescription = nameof(OptionsLiveSampleDescription);
        public const string GoToResourceDefinition = nameof(GoToResourceDefinition);
        public const string GoToMoreDefinitionsFormat = nameof(GoToMoreDefinitionsFormat);
        public const string UsedByWpf = nameof(UsedByWpf);
        public const string OverriddenByFormat = nameof(OverriddenByFormat);
        public const string NotMerged = nameof(NotMerged);
        public const string NotMergedFileFormat = nameof(NotMergedFileFormat);
        public const string OverriddenKeyWarningFormat = nameof(OverriddenKeyWarningFormat);
        public const string OptionsWarnOverriddenName = nameof(OptionsWarnOverriddenName);
        public const string OptionsWarnOverriddenDescription = nameof(OptionsWarnOverriddenDescription);
    }

    /// <summary>
    /// Returns strings in the language chosen in the extension settings.
    /// The resource is embedded as "VSPackage" (see LogicalName in the .csproj) so VS can use the
    /// same strings for registration.json and [ProvideOptionPage], in the VS UI language.
    /// </summary>
    internal static class Localizer
    {
        private static readonly ResourceManager ResourceManager =
            new ResourceManager("VSPackage", typeof(Localizer).Assembly);

        private static volatile CultureInfo _culture = CultureInfo.GetCultureInfo("en");

        /// <summary>The current language. Safe to read from any thread.</summary>
        public static CultureInfo Culture => _culture;

        public static string Get(string name)
        {
            return ResourceManager.GetString(name, _culture) ?? name;
        }

        public static string Format(string name, params object[] args)
        {
            // Use the same culture for number formatting.
            return string.Format(_culture, Get(name), args);
        }

        /// <summary>
        /// Resolves the language from the setting. Called on package load and after each settings change.
        /// </summary>
        public static void Refresh(IServiceProvider serviceProvider, ExtensionLanguage setting)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            SetCulture(setting == ExtensionLanguage.Automatic
                ? GetVisualStudioCulture(serviceProvider)
                : LanguageInfo.ToCulture(setting));
        }

        /// <summary>Sets the language directly. Used by Refresh and by unit tests that run without VS.</summary>
        internal static void SetCulture(CultureInfo culture)
        {
            _culture = culture ?? CultureInfo.GetCultureInfo("en");
        }

        /// <summary>
        /// Returns the language the VS UI actually shows. CurrentUICulture is unreliable here: with VS set
        /// to "Same as Windows" it can be a language VS has no language pack for, while VS shows English.
        /// </summary>
        private static CultureInfo GetVisualStudioCulture(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (serviceProvider.GetService(typeof(SUIHostLocale)) is IUIHostLocale hostLocale
                && ErrorHandler.Succeeded(hostLocale.GetUILocale(out uint lcid)))
            {
                try
                {
                    return LanguageInfo.MatchVisualStudioLanguage(CultureInfo.GetCultureInfo((int)lcid));
                }
                catch (CultureNotFoundException)
                {
                    // Unknown LCID, fall back below.
                }
            }

            return LanguageInfo.MatchVisualStudioLanguage(CultureInfo.CurrentUICulture);
        }
    }
}
