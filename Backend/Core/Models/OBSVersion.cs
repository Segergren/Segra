namespace Segra.Backend.Core.Models
{
    public class OBSVersion
    {
        public required string Version { get; set; }
        public bool IsBeta { get; set; }
        public string? AvailableSince { get; set; }
        public string? SupportsFrom { get; set; }
        public string? SupportsTo { get; set; }
        public required string Url { get; set; }
    }
}
