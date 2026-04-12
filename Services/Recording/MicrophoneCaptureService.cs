using Zink.Interop;

namespace Zink.Services.Recording
{
    public sealed class MicrophoneCaptureService : WasapiCaptureBase
    {
        protected override bool UseLoopback => false;
        protected override EDataFlow DataFlow => EDataFlow.eCapture;
    }
}