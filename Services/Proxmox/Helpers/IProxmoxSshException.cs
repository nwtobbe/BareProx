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


namespace BareProx.Services.Helpers
{
    /// <summary>
    /// Marker/contract for SSH-related Proxmox exceptions.
    /// Note: you cannot catch an interface; catch ProxmoxSshException (the class) instead.
    /// </summary>
    public interface IProxmoxSshException
    {
        /// <summary>The SSH exit code if known.</summary>
        int? ExitCode { get; }

        /// <summary>The target host the SSH command was executed against, if known.</summary>
        string? Host { get; }

        /// <summary>The command attempted, if known.</summary>
        string? Command { get; }
    }
}
