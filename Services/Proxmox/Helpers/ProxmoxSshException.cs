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


using System;

namespace BareProx.Services.Helpers
{
    /// <summary>
    /// Thrown when an SSH operation against a Proxmox host fails
    /// (connection/auth/exec/exit-code/etc).
    /// </summary>
    public sealed class ProxmoxSshException : Exception, IProxmoxSshException
    {
        public int? ExitCode { get; }
        public string? Host { get; }
        public string? Command { get; }

        public ProxmoxSshException(
            string message,
            int? exitCode = null,
            string? host = null,
            string? command = null)
            : base(message)
        {
            ExitCode = exitCode;
            Host = host;
            Command = command;
        }

    }
}
