namespace BareProx.Services
{
    using BareProx.Models;
    using System.Collections.Generic;
    using System.Threading.Tasks;
  
        public interface INetappService
        {
            Task<List<VserverDto>> GetVserversAndVolumesAsync(int netappControllerId);
            Task<List<NetappMountInfo>> GetVolumesWithMountInfoAsync(int netappControllerId);
            Task<SnapshotResult> CreateSnapshotAsync(int clusterId, string StorageName, string snapmirrorLabel);
            Task<FlexCloneResult> CloneVolumeFromSnapshotAsync(string volumeName, string snapshotName, string cloneName, int controllerId);
        Task<List<string>> GetNfsEnabledIpsAsync(string vserver);

        Task<List<VolumeSnapshotTreeDto>> GetSnapshotsForVolumesAsync(HashSet<string> volumeNames);
        Task<List<string>> GetSnapshotsAsync(string vserver, string volumeName);
        Task<bool> DeleteVolumeAsync(string volumeName, int controllerId);
        Task<List<string>> ListFlexClonesAsync(int controllerId);

        Task<List<string>> ListVolumesByPrefixAsync(string prefix, int controllerId);
        Task<bool> CopyExportPolicyAsync(string sourceVolume, string targetVolume, int controllerId);
        Task<DeleteSnapshotResult> DeleteSnapshotAsync(int controllerId, string volumeName, string snapshotName);

        /// <summary>
        /// Patches the ONTAP volume’s NAS export path so it’s visible over NFS.
        /// </summary>
        Task<VolumeInfo?> LookupVolumeAsync(string volumeName, int controllerId);
        Task<bool> SetVolumeExportPathAsync(string volumeUuid, string exportPath, int controllerId);
    }
}

