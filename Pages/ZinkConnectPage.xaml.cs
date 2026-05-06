using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Zink.Pages
{
    public sealed partial class ZinkConnectPage : Page
    {
        private static ZinkConnectWindow? _browserWindow;

        public ZinkConnectPage()
        {
            InitializeComponent();
            Loaded += (_, _) => OpenBrowser();
        }

        private void OpenBrowserButton_Click(object sender, RoutedEventArgs e)
        {
            OpenBrowser();
        }

        private static void OpenBrowser()
        {
            if (_browserWindow == null)
            {
                _browserWindow = new ZinkConnectWindow();
                _browserWindow.Closed += (_, _) => _browserWindow = null;
            }

            _browserWindow.ShowBrowser();
        }
    }
}
