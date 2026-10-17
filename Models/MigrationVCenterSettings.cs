using System.ComponentModel.DataAnnotations;

namespace BareProx.Models;

// Global singleton for the optional migration vCenter connection.
public sealed class MigrationVCenterSettings
{
    [Key]
    public int Id { get; set; } = 1;
    [MaxLength(512)]
    public string Server { get; set; } = string.Empty;
    [MaxLength(512)]
    public string Username { get; set; } = string.Empty;
    public string EncryptedPassword { get; set; } = string.Empty;
    public bool AllowUntrustedCertificate { get; set; }
}
