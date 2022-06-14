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
    private static readonly Gauge OnlineUser = Metrics.CreateGauge("online_user", "Number of online user account according to latest interactive.");
    private static readonly Gauge DailyActiveUser = Metrics.CreateGauge("active_user", "Number of active user account in recent 24h.");

    private bool disposedValue;
    private ConcurrentDictionary<string, DateTime> dictUsers = new();
    private ConcurrentDictionary<string, DateTime> dictDailyUsers = new();

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
        var dailyKey = firstPuzzle;
        dictDailyUsers.AddOrUpdate(dailyKey, DateTime.UtcNow, (_, __) => DateTime.UtcNow);
    }

    private void DoWork(object? state)
    {
        try
        {
            Count(this.dictUsers, TimeSpan.FromSeconds(this.appSettings.OnlineUserStaySeconds), OnlineUser);
            Count(this.dictDailyUsers, TimeSpan.FromHours(24), DailyActiveUser);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "failed when count online user");
        }
    }

    private static void Count(ConcurrentDictionary<string, DateTime> dict, TimeSpan duration, Gauge metric)
    {
        var count = 0;
        foreach (var user in dict)
        {
            count++;
            if ((DateTime.UtcNow - user.Value) > duration)
            {
                dict.Remove(user.Key, out var _);
                count--;
            }
        }

        metric.Set(count);
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
