using System;
using System.Threading.Tasks;
using Zink.Services.NativeCalling.Models;

namespace Zink.Services.NativeCalling
{
    public sealed class NativePeerConnectionService
    {
        public event EventHandler<IceCandidateModel>? LocalIceCandidateReady;

        public Task AttachMicrophoneAsync(MicCaptureService mic)
        {
            // TODO:
            // Create/send audio track into peer connection.
            return Task.CompletedTask;
        }

        public Task AttachScreenShareAsync(ScreenShareCaptureService screen)
        {
            // TODO:
            // Create/send video track into peer connection.
            return Task.CompletedTask;
        }

        public Task<SessionDescriptionModel> CreateOfferAsync()
        {
            return Task.FromResult(new SessionDescriptionModel
            {
                Type = "offer",
                Sdp = ""
            });
        }

        public Task<SessionDescriptionModel> CreateAnswerAsync()
        {
            return Task.FromResult(new SessionDescriptionModel
            {
                Type = "answer",
                Sdp = ""
            });
        }

        public Task SetRemoteOfferAsync(SessionDescriptionModel offer) => Task.CompletedTask;
        public Task SetRemoteAnswerAsync(SessionDescriptionModel answer) => Task.CompletedTask;
        public Task AddIceCandidateAsync(IceCandidateModel ice) => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;
    }
}