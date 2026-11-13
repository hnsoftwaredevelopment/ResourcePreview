namespace SampleApp;

/// <summary>Sample data for the ValueEditor template in Resources/Templates.xaml.</summary>
public class PropertyItem
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool IsReadOnly { get; set; }
}
