namespace SampleApp.Resources;

/// <summary>
/// Resource keys shared by code and XAML, as in a property grid that picks an editor template per property.
/// XAML: x:Key="{x:Static res:TemplateKeys.ValueEditor}". Code: FindResource(TemplateKeys.ValueEditor).
/// </summary>
public static class TemplateKeys
{
    public static readonly string ValueEditor = "ValueEditor";
}
