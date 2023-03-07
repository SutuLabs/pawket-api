using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace WalletServer.Helpers;

public class NameResolvingService
{
    private readonly IMemoryCache memoryCache;
    private readonly DataAccess dataAccess;
    private readonly ILogger<NameResolvingService> logger;
    private readonly AppSettings appSettings;

    public NameResolvingService(
        IMemoryCache memoryCache,
        DataAccess dataAccess,
        ILogger<NameResolvingService> logger,
        IOptions<AppSettings> appSettings)
    {
        this.memoryCache = memoryCache;
        this.dataAccess = dataAccess;
        this.logger = logger;
        this.appSettings = appSettings.Value;
    }

    public async Task<NameEntity[]> GetAllNamesAsync()
    {
        const string key = nameof(GetAllNamesAsync);
        if (!memoryCache.TryGetValue(key, out NameEntity[] names))
        {
            // TODO: throttling to avoid concurrent database retrieval
            names = await this.dataAccess.GetAllNameEntities(this.appSettings.CnsCreatorPuzzleHash);
            memoryCache.Set(key, names, TimeSpan.FromMinutes(1));
        }

        return names;
    }

    public async Task<RecentNameEntity[]> GetRecentNamesAsync(int? limit = null)
    {
        const string key = nameof(GetRecentNamesAsync);
        if (!memoryCache.TryGetValue(key, out RecentNameEntity[] names))
        {
            // TODO: throttling to avoid concurrent database retrieval
            names = await this.dataAccess.GetRecentNames(this.appSettings.CnsCreatorPuzzleHash, limit);
            memoryCache.Set(key, names, TimeSpan.FromMinutes(10));
        }

        return names;
    }

    public async Task<WealthiestNameEntity[]> GetWealthiestNamesAsync(int? limit = null)
    {
        const string key = nameof(GetWealthiestNamesAsync);
        if (!memoryCache.TryGetValue(key, out WealthiestNameEntity[] names))
        {
            // TODO: throttling to avoid concurrent database retrieval
            names = await this.dataAccess.GetWealthiestNames(this.appSettings.CnsCreatorPuzzleHash, limit);
            memoryCache.Set(key, names, TimeSpan.FromMinutes(10));
        }

        return names;
    }
}
