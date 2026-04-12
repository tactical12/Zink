using System;

namespace Zink.Services.NativeCalling
{
    public sealed class NativeCallSession
    {
        public string CallId { get; set; } = "";
        public long TargetUserId { get; set; }
        public long RemoteUserId { get; set; }
        public bool IsScreenShare { get; set; }
        public NativeCallState State { get; set; } = NativeCallState.Idle;
        public string StatusText { get; set; } = "Waiting...";
        public string PeerText { get; set; } = "Preparing call...";
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}