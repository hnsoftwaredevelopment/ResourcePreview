using System.ComponentModel;

namespace ResourcePreview.Localization
{
    // The standard attributes only take constant strings. These subclasses look up the text
    // when the options PropertyGrid asks for it, so it follows the current UI language.

    internal sealed class LocalizedDisplayNameAttribute : DisplayNameAttribute
    {
        private readonly string _resourceName;

        public LocalizedDisplayNameAttribute(string resourceName)
        {
            _resourceName = resourceName;
        }

        public override string DisplayName => Localizer.Get(_resourceName);
    }

    internal sealed class LocalizedDescriptionAttribute : DescriptionAttribute
    {
        private readonly string _resourceName;

        public LocalizedDescriptionAttribute(string resourceName)
        {
            _resourceName = resourceName;
        }

        public override string Description => Localizer.Get(_resourceName);
    }

    /// <summary>Uses the GetLocalizedString hook that CategoryAttribute provides for this.</summary>
    internal sealed class LocalizedCategoryAttribute : CategoryAttribute
    {
        public LocalizedCategoryAttribute(string resourceName)
            : base(resourceName)
        {
        }

        protected override string GetLocalizedString(string value) => Localizer.Get(value);
    }
}
