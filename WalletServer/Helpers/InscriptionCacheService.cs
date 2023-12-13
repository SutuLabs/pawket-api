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

    public async Task<TickEntity[]> GetAllTicksAsync()
    {
        const string key = nameof(GetAllTicksAsync);
        if (!memoryCache.TryGetValue(key, out TickEntity[] ticks))
        {
            Task<TickEntity[]> task;
            lock (GetAllTickEntitiesDbTaskLock)
            {
                task = GetAllTickEntitiesDbTask ?? this.dataAccess.GetAllTickEntities();
            }

            ticks = await task;
            memoryCache.Set(key, ticks, TimeSpan.FromMinutes(1));
        }

        return ticks;
    }

}
