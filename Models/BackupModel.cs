using Microsoft.AspNetCore.Mvc.Rendering;
using System;
using System.Collections.Generic;

namespace BareProx.Models
{
       //public class BackupScheduleViewModel
    //{
    //    public List<SelectListItem>? StorageOptions { get; set; }
    //    public List<SelectListItem>? AllVms { get; set; }
    //    public string? StorageName { get; set; }
    //    public bool? IsApplicationAware { get; set; }
    //    public string? Schedule { get; set; }
    //    public List<ScheduleEntryViewModel> Schedules { get; set; } = new();
    //    public string? Name { get; set; }
    //    public List<string>? ExcludedVmIds { get; set; }
    //}
    //    public class ScheduleEntryViewModel
    //{
    //    public string Type { get; set; }  // Hourly, Daily, Weekly
    //    public int? Frequency { get; set; }
    //    public TimeSpan? Time { get; set; }
    //    public string Label { get; set; }
    //}

    public class VolumeMeta
    {
        public int ClusterId { get; set; }
        public int ControllerId { get; set; }
    }
    public class CreateScheduleRequest
    {
        public int Id { get; set; }
        public bool IsEnabled { get; set; } = true;
        public int ClusterId { get; set; }
        public int ControllerId { get; set; }

        // New: map volumeName -> which cluster/controller to use
        public Dictionary<string, VolumeMeta> VolumeMeta { get; set; } = new();
        public string StorageName { get; set; } = null!;
        public bool IsApplicationAware { get; set; }
        public string Name { get; set; } = null!;
        public List<string> ExcludedVmIds { get; set; } = new();

        public bool EnableIoFreeze { get; set; }
        public bool UseProxmoxSnapshot { get; set; }
        public bool WithMemory { get; set; }

        public List<ScheduleEntry> Schedules { get; set; } = new();
        public ScheduleEntry SingleSchedule { get; set; } = new();

        // For dropdowns
        public List<SelectListItem> StorageOptions { get; set; } = new();
        public List<SelectListItem> AllVms { get; set; } = new();
        public bool CanReplicateToSecondary { get; set; }  // set in controller logic
        public bool ReplicateToSecondary { get; set; }     // user input
        public HashSet<string> ReplicableVolumes { get; set; } = new();
    }

    public class ScheduleEntry
    {
        public string Type { get; set; } = null!;
        public List<string> DaysOfWeek { get; set; } = new();
        public string? Time { get; set; }
        public int? StartHour { get; set; }
        public int? EndHour { get; set; }
        public string Label { get; set; } = "";

        public int RetentionCount { get; set; } = 7;
        public string RetentionUnit { get; set; } = "Days";
    }
    public class BackupRequest
    {
        public string StorageName { get; set; } = null!;
        public bool IsApplicationAware { get; set; }
        public string Label { get; set; } = "Manual";
        public int ClusterId { get; set; }
        public int ControllerId { get; set; }

        public int RetentionCount { get; set; } = 7;
        public string RetentionUnit { get; set; } = "Days";

        // App-aware options
        public bool EnableIoFreeze { get; set; }
        public bool UseProxmoxSnapshot { get; set; }
        public bool WithMemory { get; set; }
        public bool DontTrySuspend { get; set; }
        public bool ReplicateToSecondary { get; set; }
        public int ScheduleID { get; set; }
    }
       public class RestoreViewModel
    {
        public int BackupId { get; set; }
        public string VmId { get; set; } // VM ID
        public string VmName { get; set; }
        public string SnapshotName { get; set; }
        public string VolumeName { get; set; }
        public string StorageName { get; set; }
        public string ClusterName { get; set; }
        public int ControllerId { get; set; }
        public DateTime TimeStamp { get; set; }
    }
    public enum RestoreType
    {
        ReplaceOriginal,
        CreateNew
    }
    public class RestoreFormViewModel
    {
        public int BackupId { get; set; }
        public string VmName { get; set; } = string.Empty;
        public string VmId { get; set; } = string.Empty;
        public string VolumeName { get; set; } = string.Empty;
        public string SnapshotName { get; set; } = string.Empty;
        public string OriginalConfig { get; set; } = string.Empty;
        public string? CloneVolumeName { get; set; }
        public int? JobId { get; set; }
        public string NewVmName { get; set; } = string.Empty;
        public RestoreType RestoreType { get; set; } = RestoreType.CreateNew;
        public int ControllerId { get; set; }
        public bool StartDisconnected { get; set; } = false;
        public string OriginalHostAddress { get; set; }
        public string OriginalHostName { get; set; }
        public string HostAddress { get; set; } = string.Empty;
        public List<SelectListItem> HostOptions { get; set; } = new();
        public string MountIp { get; set; } = string.Empty;
    }





}

