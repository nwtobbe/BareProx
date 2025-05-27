using Microsoft.AspNetCore.Mvc;

namespace BareProx.Models
{
    public class JobViewModel
    {
        public int Id { get; set; }
        public string Type { get; set; }
        public string RelatedVm { get; set; }
        public string Status { get; set; }
        public DateTime StartedAtLocal { get; set; }
        public DateTime? CompletedAtLocal { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class Job
    {
        public int Id { get; set; }
        public string Type { get; set; } // "Restore", "Backup", etc.
        public string Status { get; set; } // "Pending", "Running", "Completed", "Failed", "Cancelled"
        public string? ErrorMessage { get; set; }
        public string? RelatedVm { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        // Optional: serialized payload (config, labels, paths)
        public string? PayloadJson { get; set; }
    }

}
