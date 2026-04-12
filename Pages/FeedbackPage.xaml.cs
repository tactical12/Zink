// FeedbackPage.xaml.cs
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using Windows.System;

namespace Zink.Pages
{
    public sealed partial class FeedbackPage : Page
    {
        public FeedbackPage()
        {
            InitializeComponent();
        }

        private async void SubmitFeedback_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            string email = EmailBox.Text.Trim();
            string message = MessageBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(message))
            {
                FeedbackStatus.Text = "Please enter your feedback.";
                return;
            }

            SubmitButton.IsEnabled = false;
            FeedbackStatus.Text = "Opening your mail client...";

            try
            {
                string mailto = $"mailto:francehayle@outlook.com?subject=Zink%20Feedback&body={Uri.EscapeDataString($"From: {name} ({email})\n\n{message}")}";
                var uri = new Uri(mailto);
                await Launcher.LaunchUriAsync(uri);
                FeedbackStatus.Text = "Mail client opened. You can send your feedback from there.";

                NameBox.Text = string.Empty;
                EmailBox.Text = string.Empty;
                MessageBox.Text = string.Empty;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                FeedbackStatus.Text = "Couldn't open mail client. Please contact us manually at francehayle@outlook.com.";
            }

            SubmitButton.IsEnabled = true;
        }
    }
}
