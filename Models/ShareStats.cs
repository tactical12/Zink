namespace Zink.Models
{
    public sealed class ShareStats
    {
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public double FrameRate { get; set; }
        public bool Hard4kSatisfied => SourceWidth >= 3840 && SourceHeight >= 2160;

        public override string ToString()
        {
            return $"{SourceWidth} x {SourceHeight} @ {FrameRate:0.##} fps";
        }
    }
}