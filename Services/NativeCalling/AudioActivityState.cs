namespace Zink.Services.NativeCalling
{
    public sealed class AudioActivityState
    {
        public bool LocalSpeaking { get; set; }
        public bool RemoteSpeaking { get; set; }

        public double LocalLevel { get; set; }
        public double RemoteLevel { get; set; }
    }
}