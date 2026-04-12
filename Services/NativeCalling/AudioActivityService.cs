using System;

namespace Zink.Services.NativeCalling
{
    public sealed class AudioActivityService
    {
        public static AudioActivityService Instance { get; } = new AudioActivityService();

        public AudioActivityState Current { get; } = new AudioActivityState();

        public event EventHandler<AudioActivityState>? ActivityChanged;

        private AudioActivityService()
        {
        }

        public void UpdateLocalLevel(double level)
        {
            level = Clamp(level);

            Current.LocalLevel = level;
            Current.LocalSpeaking = level >= 0.15;

            RaiseChanged();
        }

        public void UpdateRemoteLevel(double level)
        {
            level = Clamp(level);

            Current.RemoteLevel = level;
            Current.RemoteSpeaking = level >= 0.15;

            RaiseChanged();
        }

        public void Reset()
        {
            Current.LocalSpeaking = false;
            Current.RemoteSpeaking = false;
            Current.LocalLevel = 0;
            Current.RemoteLevel = 0;

            RaiseChanged();
        }

        private static double Clamp(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private void RaiseChanged()
        {
            ActivityChanged?.Invoke(this, Current);
        }
    }
}