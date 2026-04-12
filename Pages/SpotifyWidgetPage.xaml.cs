using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class SpotifyWidgetPage : Page
    {
        private Zink.SpotifyWidgetWindow _widget;

        public SpotifyWidgetPage()
        {
            this.InitializeComponent();
        }

        private void OpenWidgetBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_widget == null)
                _widget = new Zink.SpotifyWidgetWindow();

            _widget.ShowWindow();   // AppWindow.Show + Activate + TopMost
        }

        private void HideWidgetBtn_Click(object sender, RoutedEventArgs e)
        {
            _widget?.HideWindow();  // AppWindow.Hide
        }

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SpotifyControllerService.Instance.IsAttached)
                await SpotifyControllerService.Instance.RefreshStateAsync();
        }
    }
}
