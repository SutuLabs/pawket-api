using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Prometheus;

namespace WalletServer.Helpers;

public class OnlineCounter : IDisposable
{
    private readonly ILogger<OnlineCounter> logger;
    private readonly IMemoryCache memoryCache;
    private readonly AppSettings appSettings;
    private readonly Timer timer;
    private static readonly Gauge OnlineUser = Metrics.CreateGauge("online_user", "Number of online user according to latest interactive.");

    private bool disposedValue;
    private ConcurrentDictionary<string, DateTime> dictUsers = new();

    public OnlineCounter(
        ILogger<OnlineCounter> logger,
        IMemoryCache memoryCache,
        IOptions<AppSettings> appSettings)
    {
        this.logger = logger;
        this.memoryCache = memoryCache;
        this.appSettings = appSettings.Value;
        this.timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    public void Renew(string ip, string firstPuzzle, int puzzleCount)
    {
        var key = string.Join(":", new[] { ip, firstPuzzle, puzzleCount.ToString() });
        dictUsers.AddOrUpdate(key, DateTime.UtcNow, (_, __) => DateTime.UtcNow);
    }

    private void DoWork(object? state)
    {
        try
        {
            var count = 0;
            foreach (var user in dictUsers)
            {
                count++;
                if ((DateTime.UtcNow - user.Value).TotalSeconds > this.appSettings.OnlineUserStaySeconds)
                {
                    this.dictUsers.Remove(user.Key, out var _);
                    count--;
                }
            }

            OnlineUser.Set(count);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "failed when count online user");
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                this.timer.Dispose();
            }
            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
