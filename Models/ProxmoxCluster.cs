/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */


using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace BareProx.Models
{
    public class SelectStorageViewModel
    {
        public int ClusterId { get; set; }
        public List<ProxmoxStorageDto> StorageList { get; set; } = new();
        public List<string> SelectedStorageIds { get; set; } = new();
    }

    public class ProxmoxStorageDto
    {
        public string Id { get; set; } = "";
        public string Storage { get; set; } = "";
        public string Type { get; set; } = "";
        public string Path { get; set; } = "";
        public string Node { get; set; } = "";
        public bool IsSelected { get; set; } = false;
    }

    public class StorageWithVMsDto
    {
        public string StorageName { get; set; } = default!;
        public List<ProxmoxVM> VMs { get; set; } = new();
        public int ClusterId { get; set; }

        public int? SelectedNetappVolumeId { get; set; }
        public int NetappControllerId { get; set; }
        public bool IsReplicable { get; set; }
        public bool SnapshotLockingEnabled { get; set; }
        public string? VolumeUuid { get; set; }
    }

    public class StorageConfig
    {
        [JsonPropertyName("storage")]
        public string Storage { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [JsonPropertyName("online")]
        public int Online { get; set; }  // 1 = mounted/online, 0 = not
    }

    public sealed class PveNetworkIf { public string? Type { get; set; } public string? Iface { get; set; } public int? BridgeVlanAware { get; set; } }
    public sealed class PveSdnVnet { public string? Vnet { get; set; } public string? Zone { get; set; } public int? Tag { get; set; } public string? Type { get; set; } public string? Description { get; set; } }
    public sealed class PveStorageContentItem 
    { 
        public string? VolId { get; set; } 
        public string? Volid { get; set; } 
        public string? Volume { get; set; } 
        public string? Name { get; set; } 
        public string? Content { get; set; }
        public long? Ctime { get; set; }
    }
    public class PveStorageListItem
    {
        public string? Storage { get; set; }
        public string? Content { get; set; }  // e.g. "images,iso,backup,..."
        public string? Type { get; set; }     // e.g. "dir","nfs","iscs","rbd",...
    }

    public sealed class ProxmoxClusterDiscoveryResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? ClusterName { get; set; }
        public List<string> Logs { get; set; } = new();
        public List<DiscoveredProxmoxNode> Nodes { get; set; } = new();
    }

    public sealed class DiscoveredProxmoxNode
    {
        public string NodeName { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string? ReverseName { get; set; }
        public bool SshOk { get; set; }
    }

    // ---- ViewModel for auth options (place wherever you keep VMs) ----
    public class ProxmoxClusterAuthOptionsVm : IValidatableObject
    {
        public int Id { get; set; }
        public bool UseApiToken { get; set; } = true;
        public string? ApiTokenId { get; set; } // user@realm!tokenid or just tokenid
        public int? ApiTokenLifetimeDays { get; set; } = 180;
        public int ApiTokenRenewBeforeMinutes { get; set; } = 1440;

        // Info only
        public DateTime? ApiTokenExpiresUtc { get; set; }
        public string? ApiTokenSecretPreview { get; set; }
        public bool HasSecret => !string.IsNullOrWhiteSpace(ApiTokenSecretPreview);

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (!UseApiToken) yield break;
            var days = ApiTokenLifetimeDays ?? 180;
            var lifetimeMinutes = days * 24 * 60;
            if (ApiTokenRenewBeforeMinutes >= lifetimeMinutes)
            {
                yield return new ValidationResult(
                    $"Renew-before must be lower than the lifetime ({lifetimeMinutes} minutes).",
                    new[] { nameof(ApiTokenRenewBeforeMinutes) });
            }
        }
    }

}