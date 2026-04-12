using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Zink.Pages.Social;

namespace Zink.Services.NativeCalling
{
    public static class NativeIncomingCallOverlayService
    {
        public static async Task AcceptAndNavigateAsync(Frame frame, long fromUserId, string callId, bool isScreenShare)
        {
            await NativeCallCoordinator.Instance.AcceptIncomingAsync(fromUserId, callId);

            frame.Navigate(typeof(CallPage), new CallPageArgs
            {
                TargetUserId = fromUserId,
                IsScreenShare = isScreenShare
            });
        }

        public static async Task RejectAsync(long fromUserId, string callId)
        {
            await NativeCallCoordinator.Instance.RejectIncomingAsync(fromUserId, callId);
        }
    }
}