namespace Zink.Pages.Social
{
    public sealed class MessagesPageArgs
    {
        public long TargetUserId { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }
}
