using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace WalletServer.Helpers;

public class InscriptionCacheService
{
    private readonly IMemoryCache memoryCache;
    private readonly DataAccess dataAccess;
    private readonly ILogger<InscriptionCacheService> logger;
    private readonly AppSettings appSettings;

    private static object GetAllTickEntitiesDbTaskLock = new object();
    private static Task<TickEntity[]>? GetAllTickEntitiesDbTask;

    private static object EnsureTicksDbTaskLock = new object();
    private static Task<int>? EnsureTicksTask;

    public InscriptionCacheService(
        IMemoryCache memoryCache,
        DataAccess dataAccess,
        ILogger<InscriptionCacheService> logger,
        IOptions<AppSettings> appSettings)
    {
        this.memoryCache = memoryCache;
        this.dataAccess = dataAccess;
        this.logger = logger;
        this.appSettings = appSettings.Value;
    }

    public async Task<TickEntity[]?> GetAllTicksAsync()
    {
        {
            const string key = nameof(this.dataAccess.EnsureTickInfo);
            await memoryCache.GetByCacheAsync(
                key,
                EnsureTicksDbTaskLock,
                () => EnsureTicksTask,
                task => EnsureTicksTask = task,
                async () => { await this.dataAccess.EnsureTickInfo(); return 1; },
                TimeSpan.FromMinutes(5));

        }

        {
            const string key = nameof(GetAllTicksAsync);
            return await memoryCache.GetByCacheAsync(
                key,
                GetAllTickEntitiesDbTaskLock,
                () => GetAllTickEntitiesDbTask,
                task => GetAllTickEntitiesDbTask = task,
                this.dataAccess.GetAllTickEntities,
                TimeSpan.FromMinutes(1));
        }
    }

    private static object GetAllHolderTicksDbTaskLock = new object();
    private static Task<TickHolderEntity[]>? GetAllHolderTicksTask;

    public async Task<TickHolderEntity[]?> GetAllHolderTicksAsync(string holder)
    {
        string key = $"{nameof(GetAllHolderTicksAsync)}{holder}";
        return await memoryCache.GetByCacheAsync(
            key,
            GetAllHolderTicksDbTaskLock,
            () => GetAllHolderTicksTask,
            task => GetAllHolderTicksTask = task,
            () => this.dataAccess.GetHolderTicks(holder),
            TimeSpan.FromMinutes(3));
    }
}