using Microsoft.AspNetCore.Mvc;

namespace BareProx.Models
{
    public class TrackedSnapshotDto
    {
        public int JobId { get; set; }

        // Primary
        public string PrimaryVolume { get; set; } = "";
        public int PrimaryControllerId { get; set; }
        public bool IfExistsPrimary { get; set; }

        // Secondary (optional)
        public string? SecondaryVolume { get; set; }
        public int? SecondaryControllerId { get; set; }
        public bool IfExistsSecondary { get; set; }
    }
}
