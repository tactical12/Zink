namespace Zink.Models
{
    public sealed class SignalEnvelope
    {
        public string Type { get; set; } = "";
        public string RoomId { get; set; } = "";
        public string FromUser { get; set; } = "";
        public string? Message { get; set; }

        public string? Sdp { get; set; }
        public string? SdpType { get; set; }
        public string? Candidate { get; set; }
        public string? Mid { get; set; }
        public int? MLineIndex { get; set; }
    }
}