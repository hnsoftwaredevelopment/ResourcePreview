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
        public const string OptionsShowInCompletionName = nameof(OptionsShowInCompletionName);
        public const string OptionsShowInCompletionDescription = nameof(OptionsShowInCompletionDescription);
        public const string OptionsColorSwatchesName = nameof(OptionsColorSwatchesName);
        public const string OptionsColorSwatchesDescription = nameof(OptionsColorSwatchesDescription);
        public const string MissingStaticKeyFormat = nameof(MissingStaticKeyFormat);
        public const string MissingDynamicKeyFormat = nameof(MissingDynamicKeyFormat);
        public const string DidYouMeanFormat = nameof(DidYouMeanFormat);
        public const string DefinedLaterFormat = nameof(DefinedLaterFormat);
        public const string NotLoadedFormat = nameof(NotLoadedFormat);
        public const string OptionsCheckReferencesName = nameof(OptionsCheckReferencesName);
        public const string OptionsCheckReferencesDescription = nameof(OptionsCheckReferencesDescription);
        public const string FindResourceReferences = nameof(FindResourceReferences);
        public const string ReferencesWindowTitle = nameof(ReferencesWindowTitle);
        public const string ReferencesSummaryFormat = nameof(ReferencesSummaryFormat);
        public const string ReferencesNoneFormat = nameof(ReferencesNoneFormat);
        public const string ReferencesDefinitions = nameof(ReferencesDefinitions);
        public const string ReferencesUsesOtherFormat = nameof(ReferencesUsesOtherFormat);
        public const string ReferencesNotFoundHere = nameof(ReferencesNotFoundHere);
        public const string UnusedResourceFormat = nameof(UnusedResourceFormat);
        public const string OptionsReportUnusedName = nameof(OptionsReportUnusedName);
        public const string OptionsReportUnusedDescription = nameof(OptionsReportUnusedDescription);
        public const string RenameResourceKey = nameof(RenameResourceKey);
        public const string RenameDialogTitle = nameof(RenameDialogTitle);
        public const string RenamePromptFormat = nameof(RenamePromptFormat);
        public const string RenameSummaryFormat = nameof(RenameSummaryFormat);
        public const string RenameCodeNoteFormat = nameof(RenameCodeNoteFormat);
        public const string RenameSaveNote = nameof(RenameSaveNote);
        public const string RenameInvalid = nameof(RenameInvalid);
        public const string RenameExistsFormat = nameof(RenameExistsFormat);
        public const string RenameDoneFormat = nameof(RenameDoneFormat);
        public const string RenameButton = nameof(RenameButton);
        public const string CancelButton = nameof(CancelButton);
        public const string ExtractToResource = nameof(ExtractToResource);
        public const string ExtractDialogTitle = nameof(ExtractDialogTitle);
        public const string ExtractKeyLabel = nameof(ExtractKeyLabel);
        public const string ExtractTargetLabel = nameof(ExtractTargetLabel);
        public const string ExtractButton = nameof(ExtractButton);
        public const string ExtractNoTarget = nameof(ExtractNoTarget);
        public const string ExtractDoneFormat = nameof(ExtractDoneFormat);
        public const string ExtractToStyle = nameof(ExtractToStyle);
        public const string ExtractStyleDialogTitle = nameof(ExtractStyleDialogTitle);
        public const string ExtractStyleDoneFormat = nameof(ExtractStyleDoneFormat);
        public const string AddToStyleFormat = nameof(AddToStyleFormat);
        public const string AddToStyleDoneFormat = nameof(AddToStyleDoneFormat);
        public const string AddToStyleNotFoundFormat = nameof(AddToStyleNotFoundFormat);
        public const string FindMatchingResource = nameof(FindMatchingResource);
        public const string MatchDialogTitle = nameof(MatchDialogTitle);
        public const string MatchListLabel = nameof(MatchListLabel);
        public const string MatchStyleFormat = nameof(MatchStyleFormat);
        public const string MatchKeepsFormat = nameof(MatchKeepsFormat);
        public const string MatchTriggersFormat = nameof(MatchTriggersFormat);
        public const string MatchValueFormat = nameof(MatchValueFormat);
        public const string MatchApplyButton = nameof(MatchApplyButton);
        public const string MatchNoneFound = nameof(MatchNoneFound);
        public const string MatchAppliedFormat = nameof(MatchAppliedFormat);
        public const string ResourcesWindowTitle = nameof(ResourcesWindowTitle);
        public const string ResourcesSearchPlaceholder = nameof(ResourcesSearchPlaceholder);
        public const string ResourcesGroupByFile = nameof(ResourcesGroupByFile);
        public const string ResourcesGroupByType = nameof(ResourcesGroupByType);
        public const string ResourcesActiveFileOnly = nameof(ResourcesActiveFileOnly);
        public const string ResourcesRefresh = nameof(ResourcesRefresh);
        public const string ResourcesSummaryFormat = nameof(ResourcesSummaryFormat);
        public const string ResourcesFilteredFormat = nameof(ResourcesFilteredFormat);
        public const string ResourcesNoFile = nameof(ResourcesNoFile);
        public const string ResourcesNoActiveFile = nameof(ResourcesNoActiveFile);
        public const string ResourcesLoading = nameof(ResourcesLoading);
        public const string ResourcesUnused = nameof(ResourcesUnused);
        public const string ResourcesCopyFormat = nameof(ResourcesCopyFormat);
        public const string CategoryBrushes = nameof(CategoryBrushes);
        public const string CategoryColors = nameof(CategoryColors);
        public const string CategoryImages = nameof(CategoryImages);
        public const string CategoryStyles = nameof(CategoryStyles);
        public const string CategoryTemplates = nameof(CategoryTemplates);
        public const string CategoryValues = nameof(CategoryValues);
        public const string NoLiveSampleFormat = nameof(NoLiveSampleFormat);
        public const string TemplateMoreElementsFormat = nameof(TemplateMoreElementsFormat);
        public const string TemplateEmpty = nameof(TemplateEmpty);
        public const string NoLiveSampleOwnTypeFormat = nameof(NoLiveSampleOwnTypeFormat);
        public const string NoPreviewOwnTypeFormat = nameof(NoPreviewOwnTypeFormat);
        public const string BlockedTypeFormat = nameof(BlockedTypeFormat);
        public const string BufferSaveFailedFormat = nameof(BufferSaveFailedFormat);
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
