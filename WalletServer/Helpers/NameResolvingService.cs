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
        const string key = nameof(NameResolvingService);
        if (!memoryCache.TryGetValue(key, out NameEntity[] names))
        {
            // TODO: throttling to avoid concurrent database retrieval
            names = await this.dataAccess.GetAllNameEntities(this.appSettings.CnsCreatorPuzzleHash);
            memoryCache.Set(key, names, TimeSpan.FromMinutes(1));
        }

        return names;
    }
}
