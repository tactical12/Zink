using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Zink.Pages
{
    public sealed partial class FeatureTourPage : Page
    {
        public FeatureTourPage()
        {
            InitializeComponent();
        }

        private void OpenRecorder_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(RecorderPage));
        private void OpenRecordings_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(RecordingsLibraryPage));
        private void OpenWidgets_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(WidgetHubPage));
        private void OpenRadio_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(RadioDiscoveryPage));
        private void OpenStreaming_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(StreamingPage));
        private void OpenConnect_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(ZinkConnectPage));
    }
}
