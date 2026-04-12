namespace Zink.Services.Social
{
    public static class CallLaunchState
    {
        public static string? PendingOutgoingCallId { get; private set; }
        public static long PendingTargetUserId { get; private set; }
        public static bool PendingIsScreenShare { get; private set; }

        public static void SetOutgoing(string callId, long targetUserId, bool isScreenShare)
        {
            PendingOutgoingCallId = callId;
            PendingTargetUserId = targetUserId;
            PendingIsScreenShare = isScreenShare;
        }

        public static bool TryConsumeOutgoing(long targetUserId, bool isScreenShare, out string callId)
        {
            if (!string.IsNullOrWhiteSpace(PendingOutgoingCallId) &&
                PendingTargetUserId == targetUserId &&
                PendingIsScreenShare == isScreenShare)
            {
                callId = PendingOutgoingCallId!;
                Clear();
                return true;
            }

            callId = "";
            return false;
        }

        public static void Clear()
        {
            PendingOutgoingCallId = null;
            PendingTargetUserId = 0;
            PendingIsScreenShare = false;
        }
    }
}