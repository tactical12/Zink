using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Zink.Pages
{
    public sealed partial class WidgetHubPage : Page
    {
        public WidgetHubPage()
        {
            InitializeComponent();
        }

        private void OpenRadioWidget_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(RadioWidgetPage));
        private void OpenSpotifyWidget_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(SpotifyWidgetPage));
        private void OpenFpsWidget_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(FpsRecorderPage));
        private void OpenRecorder_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(RecorderPage));
        private void OpenHome_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(HomeDashboardPage));
    }
}
