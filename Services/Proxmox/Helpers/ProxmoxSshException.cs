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
