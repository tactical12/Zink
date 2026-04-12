namespace Zink.Models
{
    public sealed class RecorderDeviceItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        public override string ToString() => Name;
    }
}