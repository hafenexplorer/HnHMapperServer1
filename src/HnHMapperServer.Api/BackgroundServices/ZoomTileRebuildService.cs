using HnHMapperServer.Services.Interfaces;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that rebuilds incomplete zoom tiles periodically
/// Scans for zoom tiles that were created before their sub-tiles and regenerates them
/// This fixes the issue where tiles don't display at certain zoom levels
/// </summary>
public class ZoomTileRebuildService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ZoomTileRebuildService> _logger;
    private readonly IConfiguration _configuration;

    public ZoomTileRebuildService(
        IServiceScopeFactory scopeFactory,
        ILogger<ZoomTileRebuildService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check if the service is enabled
        var enabled = _configuration.GetValue<bool>("ZoomRebuild:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("Zoom Tile Rebuild Service is disabled");
            return;
        }

        var intervalMinutes = _configuration.GetValue<int>("ZoomRebuild:IntervalMinutes", 5);
        var maxTilesPerRun = _configuration.GetValue<int>("ZoomRebuild:MaxTilesPerRun", 100);
        var gridStorage = _configuration.GetValue<string>("GridStorage") ?? "map";

        _logger.LogInformation(
            "Zoom Tile Rebuild Service started (Interval: {IntervalMinutes}min, MaxTiles: {MaxTiles})",
            intervalMinutes,
            maxTilesPerRun);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var tileService = scope.ServiceProvider.GetRequiredService<ITileService>();

                var rebuiltCount = await tileService.RebuildIncompleteZoomTilesAsync(gridStorage, maxTilesPerRun);

                if (rebuiltCount > 0)
                {
                    _logger.LogInformation("Zoom rebuild cycle completed: {Count} tiles rebuilt", rebuiltCount);
                }

                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in zoom tile rebuild service");
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
        }

        _logger.LogInformation("Zoom Tile Rebuild Service stopped");
    }
}
