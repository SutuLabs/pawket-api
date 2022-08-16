using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Prometheus;

namespace WalletServer.Helpers;

public class OnlineCounter : IDisposable
{
    private const string CacheFileName = "counter_data.json";
    private readonly ILogger<OnlineCounter> logger;
    private readonly IMemoryCache memoryCache;
    private readonly AppSettings appSettings;
    private readonly Timer timer;

    [Obsolete]
    private static readonly Gauge LegacyDailyActiveUser = Metrics.CreateGauge("active_user", "Number of active user account in recent 24h.");
    [Obsolete]
    private static readonly Gauge LegacyDailyActiveIp = Metrics.CreateGauge("active_ip", "Number of active ip in recent 24h.");

    private static readonly Gauge OnlineUser = Metrics.CreateGauge("online_user", "Number of online user account according to latest interactive.");
    private static readonly Gauge DailyActiveUser = Metrics.CreateGauge("daily_active_user", "Number of active user account in recent 24h.");
    private static readonly Gauge DailyActiveIp = Metrics.CreateGauge("daily_active_ip", "Number of active ip in recent 24h.");
    private static readonly Gauge MonthlyActiveUser = Metrics.CreateGauge("monthly_active_user", "Number of active user in recent 31d.");
    private static readonly Gauge MonthlyActiveIp = Metrics.CreateGauge("monthly_active_ip", "Number of active ip in recent 31d.");

    private bool disposedValue;
    private ConcurrentDictionary<string, DateTime> dictUsers = new();
    private ConcurrentDictionary<string, DateTime> dictDailyUsers = new();
    private ConcurrentDictionary<string, DateTime> dictDailyIps = new();
    private ConcurrentDictionary<string, DateTime> dictMonthlyUsers = new();
    private ConcurrentDictionary<string, DateTime> dictMonthlyIps = new();
    private bool isSaving = false;

    public OnlineCounter(
        ILogger<OnlineCounter> logger,
        IMemoryCache memoryCache,
        IOptions<AppSettings> appSettings)
    {
        this.logger = logger;
        this.memoryCache = memoryCache;
        this.appSettings = appSettings.Value;
        this.Load();

        this.timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    private string CacheFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CacheFileName);

    public void Renew(string ip, string firstPuzzle, int puzzleCount)
    {
        var key = string.Join(":", new[] { ip, firstPuzzle, puzzleCount.ToString() });
        dictUsers.AddOrUpdate(key, DateTime.UtcNow, (_, __) => DateTime.UtcNow);

        var userKey = firstPuzzle;
        dictDailyUsers.AddOrUpdate(userKey, DateTime.UtcNow, (_, __) => DateTime.UtcNow);
        dictMonthlyUsers.AddOrUpdate(userKey, DateTime.UtcNow, (_, __) => DateTime.UtcNow);

        var ipKey = ip;
        dictDailyIps.AddOrUpdate(ipKey, DateTime.UtcNow, (_, __) => DateTime.UtcNow);
        dictMonthlyIps.AddOrUpdate(ipKey, DateTime.UtcNow, (_, __) => DateTime.UtcNow);
    }

    private void DoWork(object? state)
    {
        try
        {
            Count(this.dictUsers, TimeSpan.FromSeconds(this.appSettings.OnlineUserStaySeconds), OnlineUser);
            Count(this.dictDailyUsers, TimeSpan.FromHours(24), DailyActiveUser);
            Count(this.dictDailyUsers, TimeSpan.FromHours(24), LegacyDailyActiveUser);
            Count(this.dictDailyIps, TimeSpan.FromHours(24), DailyActiveIp);
            Count(this.dictDailyIps, TimeSpan.FromHours(24), LegacyDailyActiveIp);
            Count(this.dictMonthlyUsers, TimeSpan.FromDays(31), MonthlyActiveUser);
            Count(this.dictMonthlyIps, TimeSpan.FromDays(31), MonthlyActiveIp);
            this.Save();
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

    private void Load()
    {
        if (!File.Exists(CacheFilePath)) return;

        try
        {
            var data = JsonConvert.DeserializeObject<PersistentData>(File.ReadAllText(CacheFilePath));
            if (data != null)
            {
                this.dictUsers = data.OnlineUsers ?? this.dictUsers;
                this.dictDailyUsers = data.DailyUsers ?? this.dictDailyUsers;
                this.dictDailyIps = data.DailyIps ?? this.dictDailyIps;
                this.dictMonthlyUsers = data.MonthlyUsers ?? this.dictMonthlyUsers;
                this.dictMonthlyIps = data.MonthlyIps ?? this.dictMonthlyIps;
            }
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "failed to get cache from disk");
        }
    }

    private void Save()
    {
        if (this.isSaving) return;
        this.isSaving = true;
        try
        {
            var data = new PersistentData(
                this.dictUsers, this.dictDailyUsers, this.dictDailyIps, this.dictMonthlyUsers, this.dictMonthlyIps);
            File.WriteAllText(CacheFilePath, JsonConvert.SerializeObject(data));
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "failed to persistent counter cache data");
        }
        finally
        {
            this.isSaving = false;
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

    private record PersistentData
    (
         ConcurrentDictionary<string, DateTime>? OnlineUsers,
         ConcurrentDictionary<string, DateTime>? DailyUsers,
         ConcurrentDictionary<string, DateTime>? DailyIps,
         ConcurrentDictionary<string, DateTime>? MonthlyUsers,
         ConcurrentDictionary<string, DateTime>? MonthlyIps
    );
}
