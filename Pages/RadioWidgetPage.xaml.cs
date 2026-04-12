using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class RadioWidgetPage : Page
    {
        public ViewModels.RadioWidgetViewModel ViewModel { get; } = new();

        public RadioWidgetPage()
        {
            InitializeComponent();
        }

        private void OpenWidget_Click(object sender, RoutedEventArgs e) => MiniRadioWidgetWindow.ShowSingleton();

        private void Play_Click(object sender, RoutedEventArgs e) => AppPlaybackService.Instance.RequestPlay();
        private void Pause_Click(object sender, RoutedEventArgs e) => AppPlaybackService.Instance.RequestPause();
        private void Stop_Click(object sender, RoutedEventArgs e) => AppPlaybackService.Instance.RequestStop();
    }
}
