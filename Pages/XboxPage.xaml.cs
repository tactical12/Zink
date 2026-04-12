using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;

namespace Zink.Pages
{
    public sealed partial class XboxPage : Page
    {
        public XboxPage()
        {
            this.InitializeComponent();
            InitializeXboxWebView();
        }

        private async void InitializeXboxWebView()
        {
            // Choose a folder in LocalAppData for persistence of cookies, local storage, etc.
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZinkXboxWebView2Data");

            // Create the CoreWebView2Environment with your chosen userDataFolder
            // (leave browserExecutableFolder null to auto-find the runtime, and options null for default)
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null);

            // Initialize the WebView2 itself with that environment
            await XboxWebView.EnsureCoreWebView2Async(env);

            // Now point it at the Xbox web app
            XboxWebView.Source = new Uri("https://www.xbox.com/play");
        }
    }
}
