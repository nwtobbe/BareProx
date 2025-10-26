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
