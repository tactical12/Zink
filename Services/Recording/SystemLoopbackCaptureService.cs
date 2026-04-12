using Zink.Interop;

namespace Zink.Services.Recording
{
    public sealed class SystemLoopbackCaptureService : WasapiCaptureBase
    {
        protected override bool UseLoopback => true;
        protected override EDataFlow DataFlow => EDataFlow.eRender;
    }
}