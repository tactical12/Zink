using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Zink.Pages
{
    public sealed partial class AppCustomizationPage : Page
    {
        public AppCustomizationPage()
        {
            InitializeComponent();
        }

        private void AppTheme_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                App.MainWindow.MainFrame.Navigate(typeof(AppThemePage));
            }
            catch { }
        }

        private void DashboardCustomise_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                App.MainWindow.MainFrame.Navigate(typeof(CustomisePage));
            }
            catch { }
        }
    }
}
