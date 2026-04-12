using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Zink.Pages
{
    public sealed partial class FacebookPage : Page
    {
        private static readonly Uri HomeUri = new("https://www.facebook.com/");
        private bool _initialized;

        public FacebookPage()
        {
            InitializeComponent();
            Loaded += FacebookPage_Loaded;
            Unloaded += FacebookPage_Unloaded;
        }

        private async void FacebookPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;
            await InitWebViewAsync();
        }

        private void FacebookPage_Unloaded(object sender, RoutedEventArgs e)
        {
            TryStopPlayback();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            TryStopPlayback();
            base.OnNavigatedFrom(e);
        }

        private async Task InitWebViewAsync()
        {
            try
            {
                // Dedicated user data folder so Facebook stays signed in for this page
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Zink_Facebook_WebView2");

                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, null);
                await MyWebView.EnsureCoreWebView2Async(env);

                // Loading indicator
                MyWebView.CoreWebView2.NavigationStarting += (_, __) => LoadingRing.IsActive = true;
                MyWebView.CoreWebView2.NavigationCompleted += (_, __) => LoadingRing.IsActive = false;

                // Keep popups in this WebView
                MyWebView.CoreWebView2.NewWindowRequested += (s, e) =>
                {
                    e.NewWindow = s;   // reuse same WebView
                    e.Handled = true;
                    if (!string.IsNullOrEmpty(e.Uri))
                    {
                        try { MyWebView.CoreWebView2.Navigate(e.Uri); } catch { }
                    }
                };

                // Reasonable defaults (avoid unsupported properties)
                try
                {
                    MyWebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = true;
                    MyWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = true;
                }
                catch { /* ignore if not supported */ }

                // Go!
                MyWebView.Source = HomeUri;
            }
            catch
            {
                LoadingRing.IsActive = false;
            }
        }

        private void TryStopPlayback()
        {
            try { MyWebView.CoreWebView2?.Navigate("about:blank"); } catch { }
        }
    }
}
