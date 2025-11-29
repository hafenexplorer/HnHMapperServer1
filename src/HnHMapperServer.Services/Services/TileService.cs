using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Infrastructure.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Services.Services;

public class TileService : ITileService
{
    /// <summary>
    /// Fast PNG encoder for zoom tile generation.
    /// Uses fastest compression (level 1) for ~4x faster encoding.
    /// Trade-off: ~10-15% larger files, but encoding time drops from ~40ms to ~10ms per tile.
    /// </summary>
    public static readonly PngEncoder FastPngEncoder = new()
    {
        CompressionLevel = PngCompressionLevel.BestSpeed,
        FilterMethod = PngFilterMethod.None,
        BitDepth = PngBitDepth.Bit8,
        ColorType = PngColorType.RgbWithAlpha
    };

    private readonly ITileRepository _tileRepository;
    private readonly IGridRepository _gridRepository;
    private readonly IUpdateNotificationService _updateNotificationService;
    private readonly IStorageQuotaService _quotaService;
    private readonly ILogger<TileService> _logger;
    private readonly ApplicationDbContext _dbContext;

    public TileService(
        ITileRepository tileRepository,
        IGridRepository gridRepository,
        IUpdateNotificationService updateNotificationService,
        IStorageQuotaService quotaService,
        ILogger<TileService> logger,
        ApplicationDbContext dbContext)
    {
        _tileRepository = tileRepository;
        _gridRepository = gridRepository;
        _updateNotificationService = updateNotificationService;
        _quotaService = quotaService;
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task SaveTileAsync(int mapId, Coord coord, int zoom, string file, long timestamp, string tenantId, int fileSizeBytes)
    {
        var tileData = new TileData
        {
            MapId = mapId,
            Coord = coord,
            Zoom = zoom,
            File = file,
            Cache = timestamp,
            TenantId = tenantId,
            FileSizeBytes = fileSizeBytes
        };

        await _tileRepository.SaveTileAsync(tileData);
        _updateNotificationService.NotifyTileUpdate(tileData);
    }

    public async Task<TileData?> GetTileAsync(int mapId, Coord coord, int zoom)
    {
        return await _tileRepository.GetTileAsync(mapId, coord, zoom);
    }

    public async Task UpdateZoomLevelAsync(int mapId, Coord coord, int zoom, string tenantId, string gridStorage, List<TileData>? preloadedTiles = null)
    {
        using var img = new Image<Rgba32>(100, 100);
        img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

        int loadedSubTiles = 0;

        // Sequential sub-tile loading (DbContext is not thread-safe)
        for (int x = 0; x <= 1; x++)
        {
            for (int y = 0; y <= 1; y++)
            {
                var subCoord = new Coord(coord.X * 2 + x, coord.Y * 2 + y);

                // Use preloaded tiles if available (for background services without HTTP context)
                // Otherwise fall back to repository query (for normal HTTP requests)
                TileData? td;
                if (preloadedTiles != null)
                {
                    td = preloadedTiles.FirstOrDefault(t =>
                        t.MapId == mapId &&
                        t.Zoom == zoom - 1 &&
                        t.Coord.X == subCoord.X &&
                        t.Coord.Y == subCoord.Y);
                }
                else
                {
                    td = await GetTileAsync(mapId, subCoord, zoom - 1);
                }

                if (td == null || string.IsNullOrEmpty(td.File))
                    continue;

                var filePath = Path.Combine(gridStorage, td.File);
                if (!File.Exists(filePath))
                    continue;

                try
                {
                    using var subImg = await Image.LoadAsync<Rgba32>(filePath);

                    // Resize to 50x50 and place in appropriate quadrant
                    using var resized = subImg.Clone(ctx => ctx.Resize(50, 50));
                    img.Mutate(ctx => ctx.DrawImage(resized, new Point(50 * x, 50 * y), 1f));
                    loadedSubTiles++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load sub-tile {File}", filePath);
                }
            }
        }

        if (loadedSubTiles == 0)
        {
            _logger.LogWarning("Zoom tile Map={MapId} Zoom={Zoom} Coord={Coord} has NO sub-tiles loaded - creating empty transparent tile", mapId, zoom, coord);
        }
        else if (loadedSubTiles < 4)
        {
            _logger.LogDebug("Zoom tile Map={MapId} Zoom={Zoom} Coord={Coord} has only {Count}/4 sub-tiles loaded", mapId, zoom, coord, loadedSubTiles);
        }

        // Save the combined tile to tenant-specific directory
        var outputDir = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString(), zoom.ToString());
        Directory.CreateDirectory(outputDir);

        var outputFile = Path.Combine(outputDir, $"{coord.Name()}.png");
        await img.SaveAsPngAsync(outputFile);

        // Calculate file size
        var fileInfo = new FileInfo(outputFile);
        var fileSizeBytes = (int)fileInfo.Length;

        // Update tenant storage quota
        var fileSizeMB = fileSizeBytes / 1024.0 / 1024.0;
        await _quotaService.IncrementStorageUsageAsync(tenantId, fileSizeMB);

        var relativePath = Path.Combine("tenants", tenantId, mapId.ToString(), zoom.ToString(), $"{coord.Name()}.png");
        await SaveTileAsync(mapId, coord, zoom, relativePath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), tenantId, fileSizeBytes);
    }

    public async Task RebuildZoomsAsync(string gridStorage)
    {
        _logger.LogInformation("Rebuild Zooms starting...");
        _logger.LogWarning("RebuildZoomsAsync: This method has NOT been fully updated for multi-tenancy. " +
                          "It assumes files are in old 'grids/' directory and may not work correctly after migration.");

        var allGrids = await _gridRepository.GetAllGridsAsync();
        var needProcess = new Dictionary<(Coord, int), bool>();
        var saveGrid = new Dictionary<(Coord, int), (string gridId, string tenantId)>();

        foreach (var grid in allGrids)
        {
            needProcess[(grid.Coord.Parent(), grid.Map)] = true;
            saveGrid[(grid.Coord, grid.Map)] = (grid.Id, grid.TenantId);
        }

        _logger.LogInformation("Rebuild Zooms: Saving base tiles...");
        foreach (var ((coord, mapId), (gridId, tenantId)) in saveGrid)
        {
            // NOTE: Still using old path format - needs migration update
            var filePath = Path.Combine(gridStorage, "grids", $"{gridId}.png");
            if (!File.Exists(filePath))
                continue;

            var fileInfo = new FileInfo(filePath);
            var fileSizeBytes = (int)fileInfo.Length;

            var relativePath = Path.Combine("grids", $"{gridId}.png");
            await SaveTileAsync(mapId, coord, 0, relativePath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), tenantId, fileSizeBytes);
        }

        for (int z = 1; z <= 6; z++)
        {
            _logger.LogInformation("Rebuild Zooms: Level {Zoom}", z);
            var process = needProcess.Keys.ToList();
            needProcess.Clear();

            foreach (var (coord, mapId) in process)
            {
                // Get tenantId from grid
                var grid = allGrids.FirstOrDefault(g => g.Coord == coord && g.Map == mapId);
                if (grid == null)
                {
                    throw new InvalidOperationException($"Grid at {coord} on map {mapId} not found during zoom rebuild");
                }

                await UpdateZoomLevelAsync(mapId, coord, z, grid.TenantId, gridStorage);
                needProcess[(coord.Parent(), mapId)] = true;
            }
        }

        _logger.LogInformation("Rebuild Zooms: Complete!");
    }

    /// <summary>
    /// OPTIMIZED: Rebuilds incomplete zoom tiles using database-level filtering.
    /// Instead of loading ALL tiles into memory, queries only tiles that need work.
    /// </summary>
    public async Task<int> RebuildIncompleteZoomTilesAsync(string tenantId, string gridStorage, int maxTilesToRebuild)
    {
        int rebuiltCount = 0;

        try
        {
            _logger.LogDebug("Starting optimized zoom rebuild for tenant {TenantId}", tenantId);

            // Process zoom levels 1-6 in order
            for (int zoom = 1; zoom <= 6 && rebuiltCount < maxTilesToRebuild; zoom++)
            {
                var remaining = maxTilesToRebuild - rebuiltCount;
                int zoomLevelCreatedCount = 0;
                int zoomLevelRebuiltCount = 0;

                // 1. Find and create MISSING zoom tiles (parent coords that don't have a tile yet)
                var missingTiles = await FindMissingZoomTilesAsync(tenantId, zoom, remaining);

                foreach (var (mapId, coord) in missingTiles)
                {
                    if (rebuiltCount >= maxTilesToRebuild) break;

                    // Load only the 4 sub-tiles needed for this specific parent
                    var subTiles = await LoadSubTilesForParentAsync(tenantId, mapId, coord, zoom - 1);

                    if (subTiles.Count == 0)
                    {
                        _logger.LogDebug("Skipping missing tile Map={MapId} Zoom={Zoom} Coord={Coord}: no sub-tiles found", mapId, zoom, coord);
                        continue;
                    }

                    _logger.LogDebug("Creating missing zoom tile: Map={MapId}, Zoom={Zoom}, Coord={Coord}, SubTiles={SubTileCount}",
                        mapId, zoom, coord, subTiles.Count);

                    await UpdateZoomLevelAsync(mapId, coord, zoom, tenantId, gridStorage, subTiles);
                    rebuiltCount++;
                    zoomLevelCreatedCount++;
                }

                // 2. Find and rebuild STALE zoom tiles (where sub-tiles are newer)
                remaining = maxTilesToRebuild - rebuiltCount;
                if (remaining > 0)
                {
                    var staleTiles = await FindStaleZoomTilesAsync(tenantId, zoom, remaining);

                    foreach (var staleTile in staleTiles)
                    {
                        if (rebuiltCount >= maxTilesToRebuild) break;

                        var subTiles = await LoadSubTilesForParentAsync(tenantId, staleTile.MapId, staleTile.Coord, zoom - 1);

                        // Get the old file size for quota adjustment
                        var oldFilePath = Path.Combine(gridStorage, staleTile.File);
                        long oldFileSizeBytes = 0;
                        if (File.Exists(oldFilePath))
                        {
                            oldFileSizeBytes = new FileInfo(oldFilePath).Length;
                        }

                        _logger.LogDebug("Rebuilding stale zoom tile: Map={MapId}, Zoom={Zoom}, Coord={Coord}, SubTiles={SubTileCount}",
                            staleTile.MapId, zoom, staleTile.Coord, subTiles.Count);

                        await UpdateZoomLevelAsync(staleTile.MapId, staleTile.Coord, zoom, tenantId, gridStorage, subTiles);

                        // Adjust quota: UpdateZoomLevelAsync already increments for the new file,
                        // so we need to decrement the old file size
                        if (oldFileSizeBytes > 0)
                        {
                            var oldFileSizeMB = oldFileSizeBytes / 1024.0 / 1024.0;
                            await _quotaService.IncrementStorageUsageAsync(tenantId, -oldFileSizeMB);
                        }

                        rebuiltCount++;
                        zoomLevelRebuiltCount++;
                    }
                }

                if (zoomLevelCreatedCount > 0 || zoomLevelRebuiltCount > 0)
                {
                    _logger.LogInformation("Zoom {Zoom}: created {Created}, rebuilt {Rebuilt}", zoom, zoomLevelCreatedCount, zoomLevelRebuiltCount);
                }
            }

            if (rebuiltCount > 0)
            {
                _logger.LogInformation("Total rebuilt: {Count} zoom tiles for tenant {TenantId}", rebuiltCount, tenantId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rebuilding incomplete zoom tiles for tenant {TenantId}", tenantId);
        }

        return rebuiltCount;
    }

    /// <summary>
    /// Finds parent coordinates from zoom-1 that don't have a corresponding tile at the target zoom level.
    /// Uses database-level filtering to avoid loading all tiles into memory.
    /// </summary>
    private async Task<List<(int MapId, Coord Coord)>> FindMissingZoomTilesAsync(string tenantId, int zoom, int limit)
    {
        var prevZoom = zoom - 1;

        // Use EF Core to find parent coords that don't have corresponding zoom tiles
        // This is more efficient than loading all tiles and filtering in memory
        var result = await _dbContext.Tiles
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.Zoom == prevZoom)
            .Select(t => new { t.MapId, ParentX = t.CoordX / 2, ParentY = t.CoordY / 2 })
            .Distinct()
            .Where(parent => !_dbContext.Tiles
                .IgnoreQueryFilters()
                .Any(curr => curr.TenantId == tenantId
                    && curr.MapId == parent.MapId
                    && curr.Zoom == zoom
                    && curr.CoordX == parent.ParentX
                    && curr.CoordY == parent.ParentY))
            .Take(limit)
            .ToListAsync();

        return result.Select(r => (r.MapId, new Coord(r.ParentX, r.ParentY))).ToList();
    }

    /// <summary>
    /// Finds zoom tiles where at least one sub-tile has a newer timestamp.
    /// Uses database-level filtering to avoid loading all tiles into memory.
    /// </summary>
    private async Task<List<TileData>> FindStaleZoomTilesAsync(string tenantId, int zoom, int limit)
    {
        var prevZoom = zoom - 1;

        // Find zoom tiles that have at least one sub-tile with a newer Cache timestamp
        var staleTiles = await _dbContext.Tiles
            .IgnoreQueryFilters()
            .Where(curr => curr.TenantId == tenantId && curr.Zoom == zoom)
            .Where(curr => _dbContext.Tiles
                .IgnoreQueryFilters()
                .Any(sub => sub.TenantId == tenantId
                    && sub.MapId == curr.MapId
                    && sub.Zoom == prevZoom
                    && sub.CoordX / 2 == curr.CoordX
                    && sub.CoordY / 2 == curr.CoordY
                    && sub.Cache > curr.Cache))
            .Take(limit)
            .ToListAsync();

        return staleTiles.Select(t => new TileData
        {
            MapId = t.MapId,
            Coord = new Coord(t.CoordX, t.CoordY),
            Zoom = t.Zoom,
            File = t.File,
            Cache = t.Cache,
            TenantId = t.TenantId,
            FileSizeBytes = t.FileSizeBytes
        }).ToList();
    }

    /// <summary>
    /// Loads only the 4 specific sub-tiles needed for a parent coordinate.
    /// Much more efficient than loading all tiles for a tenant.
    /// </summary>
    private async Task<List<TileData>> LoadSubTilesForParentAsync(string tenantId, int mapId, Coord parentCoord, int subZoom)
    {
        // Calculate the coordinate range for the 4 sub-tiles
        var minX = parentCoord.X * 2;
        var maxX = parentCoord.X * 2 + 1;
        var minY = parentCoord.Y * 2;
        var maxY = parentCoord.Y * 2 + 1;

        var subTiles = await _dbContext.Tiles
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId
                && t.MapId == mapId
                && t.Zoom == subZoom
                && t.CoordX >= minX && t.CoordX <= maxX
                && t.CoordY >= minY && t.CoordY <= maxY)
            .ToListAsync();

        return subTiles.Select(t => new TileData
        {
            MapId = t.MapId,
            Coord = new Coord(t.CoordX, t.CoordY),
            Zoom = t.Zoom,
            File = t.File,
            Cache = t.Cache,
            TenantId = t.TenantId,
            FileSizeBytes = t.FileSizeBytes
        }).ToList();
    }
}
