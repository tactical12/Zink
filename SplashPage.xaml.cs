using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Zink
{
    public sealed partial class SplashPage : Page
    {
        private CancellationTokenSource? _cts;

        public SplashPage()
        {
            InitializeComponent();

            Loaded += SplashPage_Loaded;
            Unloaded += SplashPage_Unloaded;
        }

        private async void SplashPage_Loaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            // Spinner should be active immediately, not after the delay
            if (SplashLoader != null)
                SplashLoader.IsActive = true;

            try
            {
                await Task.Delay(2000, _cts.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (SplashLoader != null)
                SplashLoader.IsActive = false;

            // Optional: navigate
            // Frame.Navigate(typeof(MainPage));
        }

        private void SplashPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
