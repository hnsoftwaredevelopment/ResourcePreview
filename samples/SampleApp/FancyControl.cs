using System.Windows.Controls;

namespace SampleApp;

/// <summary>
/// A custom control for testing the Style preview: XamlReader in VS cannot load a style with
/// TargetType="{x:Type local:FancyControl}", but the XML-based preview can.
/// </summary>
public class FancyControl : Control
{
}
