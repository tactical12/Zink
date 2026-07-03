using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Zink.Pages
{
    public sealed partial class RadioDiscoveryPage : Page
    {
        public RadioDiscoveryPage()
        {
            InitializeComponent();
        }

        private void OpenRadio_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(RadioPage));
        private void OpenLikedSongs_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(LikedRadioSongsPage));
        private void OpenWidget_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(RadioWidgetPage));
        private void SearchRadio_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(SearchResultsPage), "radio");
    }
}
