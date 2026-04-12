using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Reflection;
using Windows.ApplicationModel;

namespace Zink.Pages
{
    public sealed partial class AboutPage : Page
    {
        // ? Latest version shown in About (match VersionHistoryPage keys)
        private const string LatestVersion = "2.4.1.0";

        public AboutPage()
        {
            this.InitializeComponent();

            var version = GetAppVersion();
            var buildTime = GetBuildTime();

            VersionText.Text = $"App Version: {version} (Latest: {LatestVersion})";
            BuildTimeText.Text = $"This app version was built on: {buildTime:dd MMM yyyy, HH:mm:ss}";

            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var styleObj) &&
                styleObj is Style accentStyle)
            {
                LearnMore241Button.Style = accentStyle;
                LearnMore235Button.Style = accentStyle;
                LearnMore224Button.Style = accentStyle;
                LearnMore216Button.Style = accentStyle;
                LearnMore21Button.Style = accentStyle;
                LearnMore20Button.Style = accentStyle;
                LearnMore10Button.Style = accentStyle;
            }
        }

        private string GetAppVersion()
        {
            var packageVersion = Package.Current.Id.Version;
            return $"{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}.{packageVersion.Revision}";
        }

        private DateTime GetBuildTime()
        {
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            return File.GetLastWriteTime(assemblyLocation);
        }

        // ? Learn More for 2.4.1.0
        private void LearnMore241Button_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VersionHistoryPage), "2.4.1.0");
        }

        // ? Learn More for 2.3.5.0 (match VersionHistoryPage)
        private void LearnMore235Button_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VersionHistoryPage), "2.3.5.0");
        }

        // ? Learn More for 2.2.8.0 (handler name kept the same so XAML doesn't break)
        private void LearnMore224Button_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VersionHistoryPage), "2.2.8.0");
        }

        private void LearnMore216Button_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VersionHistoryPage), "2.1.6.0");
        }

        private void LearnMore21Button_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VersionHistoryPage), "2.1.5.0");
        }

        private void LearnMore20Button_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VersionHistoryPage), "2.0.0.0");
        }

        private void LearnMore10Button_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VersionHistoryPage), "1.0.0.0");
        }
    }
}
