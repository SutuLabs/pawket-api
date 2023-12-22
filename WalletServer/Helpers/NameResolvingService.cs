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

    private static object GetAllNamesDbTaskLock = new object();
    private static Task<NameEntity[]>? GetAllNamesDbTask;

    public async Task<NameEntity[]> GetAllNamesAsync()
    {
        const string key = nameof(GetAllNamesAsync);
        if (!memoryCache.TryGetValue(key, out NameEntity[] names))
        {
            Task<NameEntity[]> task;
            lock (GetAllNamesDbTaskLock)
            {
                task = GetAllNamesDbTask ?? this.dataAccess.GetAllNameEntities(this.appSettings.CnsCreatorPuzzleHash);
                GetAllNamesDbTask = task;
            }

            names = await task;
            memoryCache.Set(key, names, TimeSpan.FromMinutes(2));
        }

        return names;
    }

    private static object GetRecentNamesDbTaskLock = new object();
    private static Task<RecentNameEntity[]>? GetRecentNamesDbTask;

    public async Task<RecentNameEntity[]> GetRecentNamesAsync()
    {
        const string key = nameof(GetRecentNamesAsync);
        if (!memoryCache.TryGetValue(key, out RecentNameEntity[] names))
        {
            Task<RecentNameEntity[]> task;
            lock (GetRecentNamesDbTaskLock)
            {
                task = GetRecentNamesDbTask ?? this.dataAccess.GetRecentNames(this.appSettings.CnsCreatorPuzzleHash);
                GetRecentNamesDbTask = task;
            }

            names = await task;
            memoryCache.Set(key, names, TimeSpan.FromMinutes(10));
        }

        return names;
    }

    private static object GetWealthiestNamesDbTaskLock = new object();
    private static Task<WealthiestNameEntity[]>? GetWealthiestNamesDbTask;

    public async Task<WealthiestNameEntity[]> GetWealthiestNamesAsync(bool isOwnerOnly)
    {
        string key = $"{nameof(GetWealthiestNamesAsync)}-{(isOwnerOnly ? "Owner" : "All")}";
        if (!memoryCache.TryGetValue(key, out WealthiestNameEntity[] names))
        {
            Task<WealthiestNameEntity[]> task;
            lock (GetWealthiestNamesDbTaskLock)
            {
                task = GetWealthiestNamesDbTask ?? this.dataAccess.GetWealthiestNames(this.appSettings.CnsCreatorPuzzleHash, isOwnerOnly);
                GetWealthiestNamesDbTask = task;
            }

            names = await task;
            memoryCache.Set(key, names, TimeSpan.FromMinutes(10));
        }

        return names;
    }
}
