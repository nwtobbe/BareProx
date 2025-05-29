using BareProx.Services;

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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing SnapMirror relations");
            }

            // --- Set Time!
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}
