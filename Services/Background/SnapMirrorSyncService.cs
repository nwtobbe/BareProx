using BareProx.Data;
using BareProx.Services;
using Microsoft.EntityFrameworkCore;


public class SnapMirrorSyncService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SnapMirrorSyncService> _logger;

    public SnapMirrorSyncService(IServiceProvider services, ILogger<SnapMirrorSyncService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var netappService = scope.ServiceProvider.GetRequiredService<INetappService>();
                await netappService.SyncSnapMirrorRelationsAsync();
                await EnsureSnapMirrorPoliciesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing SnapMirror relations");
            }

            // --- Set Time!
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        } 
    }
         private async Task EnsureSnapMirrorPoliciesAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var netapp = scope.ServiceProvider.GetRequiredService<INetappService>();

        // Step 1: Find all PolicyUuids in use
        var refs = await db.SnapMirrorRelations
            .Where(r => r.PolicyUuid != null && r.DestinationControllerId != 0)
            .Select(r => new { r.DestinationControllerId, r.PolicyUuid })
            .Distinct()
            .ToListAsync(ct);

        foreach (var pair in refs)
        {
            if (await db.SnapMirrorPolicies.AnyAsync(p => p.Uuid == pair.PolicyUuid, ct))
                continue; // Already present

            try
            {
                // This method should fetch and map policy+retentions for a single controller+uuid
                var policy = await netapp.SnapMirrorPolicyGet(pair.DestinationControllerId, pair.PolicyUuid!);
                if (policy != null)
                {
                    db.SnapMirrorPolicies.Add(policy);
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync SnapMirror policy for controller {ControllerId} and policy {PolicyUuid}", pair.DestinationControllerId, pair.PolicyUuid);
            }
        }
    }
}

