using Microsoft.AspNetCore.Mvc;

namespace BareProx.Models
{
    public class JobStatusViewModel
    {
        public DateTime Time { get; set; }
        public string Name { get; set; }
        public string Status { get; set; } // e.g., "Success", "Warning", "Failed"
    }

    public class StatusPageViewModel
    {
        public List<JobStatusViewModel> RecentJobs { get; set; }
        // Add cluster and NetApp lists here as you build out the page
    }
}
