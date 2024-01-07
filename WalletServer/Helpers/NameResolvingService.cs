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

    public async Task<NameEntity[]?> GetAllNamesAsync()
    {
        const string key = nameof(GetAllNamesAsync);
        return await memoryCache.GetByCacheAsync(
            key,
            GetAllNamesDbTaskLock,
            () => GetAllNamesDbTask,
            task => GetAllNamesDbTask = task,
            () => this.dataAccess.GetAllNameEntities(this.appSettings.CnsCreatorPuzzleHash),
            TimeSpan.FromMinutes(2));
    }

    private static object GetRecentNamesDbTaskLock = new object();
    private static Task<RecentNameEntity[]>? GetRecentNamesDbTask;

    public async Task<RecentNameEntity[]?> GetRecentNamesAsync()
    {
        const string key = nameof(GetRecentNamesAsync);
        return await memoryCache.GetByCacheAsync(
            key,
            GetRecentNamesDbTaskLock,
            () => GetRecentNamesDbTask,
            task => GetRecentNamesDbTask = task,
            () => this.dataAccess.GetRecentNames(this.appSettings.CnsCreatorPuzzleHash),
            TimeSpan.FromMinutes(10));
    }

    private static object GetWealthiestNamesDbTaskLock = new object();
    private static Task<WealthiestNameEntity[]>? GetWealthiestNamesDbTask;

    public async Task<WealthiestNameEntity[]?> GetWealthiestNamesAsync(bool isOwnerOnly)
    {
        string key = $"{nameof(GetWealthiestNamesAsync)}-{(isOwnerOnly ? "Owner" : "All")}";
        return await memoryCache.GetByCacheAsync(
            key,
            GetWealthiestNamesDbTaskLock,
            () => GetWealthiestNamesDbTask,
            task => GetWealthiestNamesDbTask = task,
            () => this.dataAccess.GetWealthiestNames(this.appSettings.CnsCreatorPuzzleHash, isOwnerOnly),
            TimeSpan.FromMinutes(10));
    }
}
