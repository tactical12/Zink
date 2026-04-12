using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;

namespace Zink.Pages
{
    public sealed partial class XPage : Page
    {
        private static readonly Uri XHome = new("https://x.com/?lang=en");
        private bool _initialized;

        public XPage()
        {
            InitializeComponent();
            Loaded += XPage_Loaded;
            Unloaded += XPage_Unloaded;
        }

        private async void XPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                // Keep X signed in across app restarts
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ZinkXWebView2Data");

                // Older WebView2: use parameterless ctor and set properties
                var options = new CoreWebView2EnvironmentOptions();
                options.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";

                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
                await XWebView.EnsureCoreWebView2Async(env);

                // Open popups in the same view
                XWebView.CoreWebView2.NewWindowRequested += (s, args) =>
                {
                    args.Handled = true;
                    try { XWebView.CoreWebView2.Navigate(args.Uri); } catch { }
                };

                // Allow autoplay explicitly (if your SDK exposes this kind)
                XWebView.CoreWebView2.PermissionRequested += (s, args) =>
                {
                    try
                    {
                        if (args.PermissionKind == CoreWebView2PermissionKind.Autoplay)
                        {
                            args.State = CoreWebView2PermissionState.Allow;
                            args.Handled = true;
                        }
                    }
                    catch { /* safe no-op for older runtimes */ }
                };

                // Unmute; tidy status bar if available
                try
                {
                    XWebView.CoreWebView2.IsMuted = false; // correct API
                    XWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                }
                catch { }

                XWebView.Source = XHome;
            }
            catch
            {
                // Optional: render a friendly error UI here
            }
        }

        private void XPage_Unloaded(object sender, RoutedEventArgs e)
        {
            TryStopPlayback();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            TryStopPlayback();
            base.OnNavigatedFrom(e);
        }

        private void TryStopPlayback()
        {
            try
            {
                XWebView.CoreWebView2?.Navigate("about:blank");
            }
            catch { }
        }
    }
}
