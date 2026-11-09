using System.Windows;

namespace SampleApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // A resource used only from code. The unused-resource check looks for the key in strings like this one.
        Tag = TryFindResource("CodeOnlyBrush");
    }
}
