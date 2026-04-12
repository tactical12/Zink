using System;

namespace Zink.Services.Social
{
    public sealed class SocialManager
    {
        public static SocialManager Instance { get; } = new SocialManager();

        public ApiClient Api { get; }
        public RealtimeService Realtime { get; }

        private SocialManager()
        {
            Api = new ApiClient();
            Realtime = new RealtimeService();
        }
    }
}