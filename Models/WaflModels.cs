using Microsoft.AspNetCore.Mvc;

namespace BareProx.Models
{
    public class SnapMirrorRelationGraphDto
    {
        public string SourceController { get; set; }
        public string DestinationController { get; set; }
        public string SourceVolume { get; set; }
        public string DestinationVolume { get; set; }
        public int HourlySnapshotsPrimary { get; set; }
        public int DailySnapshotsPrimary { get; set; }
        public int WeeklySnapshotsPrimary { get; set; }
        public int HourlySnapshotsSecondary { get; set; }
        public int DailySnapshotsSecondary { get; set; }
        public int WeeklySnapshotsSecondary { get; set; }
        public string Health { get; set; }
        public string LagTime { get; set; }
        public string RelationUuid { get; set; }
        public int SourceControllerId { get; set; }  // add this so you can match Primary
        public int DestinationControllerId { get; set; } // for Secondary
        // For secondary policy
        public string PolicyName { get; set; }
        public string PolicyType { get; set; }
        public int HourlyRetention { get; set; }
        public int DailyRetention { get; set; }
        public int WeeklyRetention { get; set; }
        public string LockedPeriod { get; set; } // already ISO8601, decode as needed
    }

}
