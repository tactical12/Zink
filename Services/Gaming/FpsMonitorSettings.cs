namespace Zink.Services.Gaming
{
    public sealed class FpsMonitorSettings
    {
        public int TargetFps { get; set; } = 60;
        public bool IncludeCursor { get; set; } = false;
        public bool HideCaptureBorder { get; set; } = true;
        public bool ShowWidget { get; set; } = true;
        public int WidgetOpacity { get; set; } = 92;
        public int SampleWindowSeconds { get; set; } = 3;
        public bool RecordCsv { get; set; } = true;
    }
}
