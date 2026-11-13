using System.Windows;
using SampleApp.Resources;

namespace SampleApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // A resource used only from code. The unused-resource check looks for the key in strings like this one.
        Tag = TryFindResource("CodeOnlyBrush");

        // A key from a class, as a template selector would use it. Static keys are never reported as unused.
        DataTemplate valueEditor = (DataTemplate)FindResource(TemplateKeys.ValueEditor);
    }
}
