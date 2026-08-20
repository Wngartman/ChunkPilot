using System.Windows;
using ChunkPilot.Core;

namespace ChunkPilot.App;

public partial class TroubleshootingWindow : Window
{
    public TroubleshootingWindow(TroubleshootingReport report)
    {
        InitializeComponent();
        DataContext = report;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
