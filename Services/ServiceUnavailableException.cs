using Microsoft.AspNetCore.Mvc;

namespace BareProx.Services
{
    /// <summary>
    /// Thrown when Proxmox (or NetApp) cannot be reached.
    /// </summary>
    public class ServiceUnavailableException : Exception
    {
        public ServiceUnavailableException(string message, Exception? inner = null)
            : base(message, inner)
        { }
    }
}
