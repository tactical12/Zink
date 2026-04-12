namespace Zink.Models
{
    public sealed class RecordingOptions
    {
        public bool IncludeSystemAudio { get; set; } = true;
        public bool IncludeMicrophone { get; set; } = false;
        public string? SelectedRenderDeviceId { get; set; }
        public string? SelectedMicDeviceId { get; set; }
        public int RetrospectiveSeconds { get; set; } = 45;

        public RecordingOptions Clone()
        {
            return new RecordingOptions
            {
                IncludeSystemAudio = IncludeSystemAudio,
                IncludeMicrophone = IncludeMicrophone,
                SelectedRenderDeviceId = SelectedRenderDeviceId,
                SelectedMicDeviceId = SelectedMicDeviceId,
                RetrospectiveSeconds = RetrospectiveSeconds
            };
        }
    }
}