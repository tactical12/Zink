using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Zink.Pages
{
    public sealed partial class ZinkConnectPage : Page
    {
        public ZinkConnectPage()
        {
            InitializeComponent();
        }

        private void OpenBrowserButton_Click(object sender, RoutedEventArgs e)
        {
            ZinkConnectBrowserWindow.ShowOrActivate();
        }
    }
}
