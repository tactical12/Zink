using System;
using Zink.Services.Social;

namespace Zink.Services.NativeCalling
{
    public sealed class NativeSignalingBridge
    {
        public static NativeSignalingBridge Instance { get; } = new NativeSignalingBridge();

        public event EventHandler<IncomingCallEventArgs>? IncomingCallReceived;
        public event EventHandler<(string CallId, long FromUserId)>? CallAnsweredReceived;
        public event EventHandler<(string CallId, long FromUserId)>? CallRejectedReceived;
        public event EventHandler<(string CallId, long FromUserId)>? CallEndedReceived;

        private bool _isHooked;

        private NativeSignalingBridge()
        {
        }

        public void EnsureHooked()
        {
            if (_isHooked)
                return;

            _isHooked = true;

            SocialManager.Instance.Realtime.IncomingCall += (_, e) => IncomingCallReceived?.Invoke(this, e);
            SocialManager.Instance.Realtime.CallAnswered += (_, e) => CallAnsweredReceived?.Invoke(this, e);
            SocialManager.Instance.Realtime.CallRejected += (_, e) => CallRejectedReceived?.Invoke(this, e);
            SocialManager.Instance.Realtime.CallEnded += (_, e) => CallEndedReceived?.Invoke(this, e);
        }
    }
}