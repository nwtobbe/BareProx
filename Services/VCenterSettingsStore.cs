using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Services.Migration;

// Scoped database-backed store. Only the encrypted password is persisted.
public sealed class VCenterSettingsStore
{
    private readonly ApplicationDbContext _db;
    private readonly IEncryptionService _encryption;

    public VCenterSettingsStore(ApplicationDbContext db, IEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public sealed class StoredSettings
    {
        public string Server { get; init; } = "";
        public string Username { get; init; } = "";
        public string EncryptedPassword { get; init; } = "";
        public bool AllowUntrustedCertificate { get; init; }
    }

    public async Task<StoredSettings> ReadAsync(CancellationToken ct = default)
    {
        var s = await _db.MigrationVCenterSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == 1, ct);
        return s == null ? new StoredSettings() : new StoredSettings
        {
            Server = s.Server,
            Username = s.Username,
            EncryptedPassword = s.EncryptedPassword,
            AllowUntrustedCertificate = s.AllowUntrustedCertificate
        };
    }

    // Save operates on the same DbContext as MigrationSelection; caller commits once.
    // Neither Verify nor Save ever returns the decrypted password to the browser.
    public async Task SetAsync(string? server, string? username, string? newPassword,
        bool allowUntrusted, CancellationToken ct = default)
    {
        var current = await _db.MigrationVCenterSettings
            .FirstOrDefaultAsync(x => x.Id == 1, ct);
        string targetServer = (server ?? "").Trim();
        string targetUser = (username ?? "").Trim();
        bool cleared = targetServer.Length == 0 && targetUser.Length == 0;
        if (!cleared && (targetServer.Length == 0 || targetUser.Length == 0))
            throw new InvalidOperationException("Provide both the vCenter address and username.");
        bool sameIdentity = current != null &&
            string.Equals(current.Server, targetServer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.Username, targetUser, StringComparison.Ordinal);
        if (!cleared && string.IsNullOrEmpty(newPassword) &&
            (!sameIdentity || string.IsNullOrEmpty(current?.EncryptedPassword)))
            throw new InvalidOperationException("Enter the vCenter password for this server and username.");

        if (current == null)
        {
            current = new MigrationVCenterSettings { Id = 1 };
            _db.MigrationVCenterSettings.Add(current);
        }
        current.Server = targetServer;
        current.Username = targetUser;
        current.AllowUntrustedCertificate = !cleared && allowUntrusted;
        current.EncryptedPassword = cleared ? "" :
            string.IsNullOrEmpty(newPassword) ? current.EncryptedPassword : _encryption.Encrypt(newPassword);
    }
}