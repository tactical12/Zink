namespace Zink.Services.NativeCalling
{
    public enum NativeCallState
    {
        Idle,
        Initializing,
        Ready,
        Calling,
        Incoming,
        Accepted,
        Negotiating,
        Connected,
        Rejected,
        Ended,
        Failed
    }
}