using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that periodically verifies tenant storage usage matches filesystem reality
/// Runs every 6 hours to detect and fix discrepancies between database and actual file storage
/// </summary>
public class TenantStorageVerificationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TenantStorageVerificationService> _logger;
    private readonly string _gridStorage;
    private readonly TimeSpan _verificationInterval;

    public TenantStorageVerificationService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<TenantStorageVerificationService> _logger)
    {
        _serviceProvider = serviceProvider;
        this._logger = _logger;
        _gridStorage = configuration["GridStorage"] ?? "map";

        // Default: 6 hours (configurable)
        var intervalHours = configuration.GetValue<int>("StorageVerification:IntervalHours", 6);
        _verificationInterval = TimeSpan.FromHours(intervalHours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Randomized startup delay to prevent all services starting simultaneously
        var startupDelay = TimeSpan.FromSeconds(Random.Shared.Next(0, 60));
        _logger.LogInformation("TenantStorageVerificationService starting in {Delay:F1}s", startupDelay.TotalSeconds);
        await Task.Delay(startupDelay, stoppingToken);

        _logger.LogInformation("TenantStorageVerificationService started. Interval: {Interval}", _verificationInterval);

        // Wait 1 hour before first run (let system stabilize after startup)
        await Task.Delay(TimeSpan.FromHours(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await VerifyAllTenantsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during storage verification");
            }

            // Wait for next verification cycle
            await Task.Delay(_verificationInterval, stoppingToken);
        }

        _logger.LogInformation("TenantStorageVerificationService stopped");
    }

    private async Task VerifyAllTenantsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var quotaService = scope.ServiceProvider.GetRequiredService<IStorageQuotaService>();
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var tenants = await db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.IsActive)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Starting storage verification for {Count} active tenants", tenants.Count);

        foreach (var tenant in tenants)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                await VerifyTenantAsync(tenant.Id, db, quotaService, auditService);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify storage for tenant {TenantId}", tenant.Id);
            }
        }

        _logger.LogInformation("Storage verification complete");
    }

    private async Task VerifyTenantAsync(string tenantId, ApplicationDbContext db, IStorageQuotaService quotaService, IAuditService auditService)
    {
        // Calculate from database: SUM(FileSizeBytes)
        var dbUsageBytes = await db.Tiles
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .SumAsync(t => (long)t.FileSizeBytes);
        var dbUsageMB = dbUsageBytes / 1024.0 / 1024.0;

        // Calculate from filesystem
        var tenantDir = Path.Combine(_gridStorage, "tenants", tenantId);
        var fsUsageBytes = CalculateDirectorySize(tenantDir);
        var fsUsageMB = fsUsageBytes / 1024.0 / 1024.0;

        // Compare
        var diffMB = Math.Abs(dbUsageMB - fsUsageMB);

        if (diffMB > 10) // More than 10 MB difference
        {
            _logger.LogWarning(
                "Storage discrepancy detected for tenant {TenantId}: DB={DbMB:F2}MB, FS={FsMB:F2}MB, Diff={DiffMB:F2}MB",
                tenantId, dbUsageMB, fsUsageMB, diffMB);

            // Update to filesystem value (filesystem is source of truth)
            var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId);

            if (tenant != null)
            {
                var oldUsage = tenant.CurrentStorageMB;
                tenant.CurrentStorageMB = fsUsageMB;
                await db.SaveChangesAsync();

                _logger.LogInformation(
                    "Updated tenant {TenantId} storage usage: {OldMB:F2}MB → {NewMB:F2}MB",
                    tenantId, oldUsage, fsUsageMB);

                // Log storage discrepancy to audit table
                await auditService.LogAsync(new AuditEntry
                {
                    TenantId = tenantId,
                    UserId = null, // System action
                    Action = "StorageDiscrepancyDetected",
                    EntityType = "Tenant",
                    EntityId = tenantId,
                    OldValue = $"{oldUsage:F2} MB (DB tracked)",
                    NewValue = $"{fsUsageMB:F2} MB (filesystem actual), Discrepancy: {diffMB:F2} MB ({(diffMB / oldUsage * 100):F1}%)"
                });
            }
        }
        else
        {
            _logger.LogDebug(
                "Storage verified for tenant {TenantId}: {UsageMB:F2}MB (diff: {DiffMB:F2}MB)",
                tenantId, dbUsageMB, diffMB);
        }

        // Recalculate storage (this also writes .storage.json metadata file)
        await quotaService.RecalculateStorageUsageAsync(tenantId, _gridStorage);
    }

    private static long CalculateDirectorySize(string dirPath)
    {
        if (!Directory.Exists(dirPath))
            return 0;

        var dirInfo = new DirectoryInfo(dirPath);

        try
        {
            return dirInfo.GetFiles("*", SearchOption.AllDirectories)
                .Sum(fi => fi.Length);
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            // Directory was deleted during scan
            return 0;
        }
    }
}
