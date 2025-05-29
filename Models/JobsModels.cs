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
}
