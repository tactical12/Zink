using System;

namespace Zink.Pages
{
    public class Notification
    {
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public DateTime Timestamp { get; set; }

        // Pre‑formatted so we don’t need StringFormat in XAML
        public string FormattedTimestamp => Timestamp.ToString("g");
    }
}
