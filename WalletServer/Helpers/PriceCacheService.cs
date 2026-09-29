using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using WalletServer.Controllers;

namespace WalletServer.Helpers;

/// <summary>Persistent last-known prices with one background refresh per API instance.</summary>
public sealed class PriceCacheService : IDisposable
{
    private readonly ILogger<PriceCacheService> logger;
    private readonly AppSettings settings;
    private readonly HttpClient http;
    private readonly string connectionString;
    private readonly object gate = new();
    private DateTime nextAttemptUtc = DateTime.MinValue;
    private int consecutiveFailures;
    private bool running;

    public PriceCacheService(ILogger<PriceCacheService> logger, IOptions<AppSettings> options)
    {
        this.logger = logger;
        this.settings = options.Value;
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(settings.PriceProxy))
            handler.Proxy = new WebProxy(settings.PriceProxy);
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var path = Path.GetFullPath(settings.PriceCachePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        using var schema = db.CreateCommand();
        schema.CommandText = @"
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS latest_price (
                from_currency TEXT NOT NULL,
                to_currency TEXT NOT NULL,
                source TEXT NOT NULL,
                price TEXT NOT NULL,
                price_time TEXT NOT NULL,
                PRIMARY KEY (from_currency, to_currency)
            );
            CREATE TABLE IF NOT EXISTS price_refresh_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                next_attempt_utc TEXT NOT NULL,
                consecutive_failures INTEGER NOT NULL
            );";
        schema.ExecuteNonQuery();
        SeedIfEmpty(db);
        using var state = db.CreateCommand();
        state.CommandText = "SELECT next_attempt_utc, consecutive_failures FROM price_refresh_state WHERE id=1";
        using var row = state.ExecuteReader();
        if (row.Read())
        {
            nextAttemptUtc = DateTime.Parse(row.GetString(0), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            consecutiveFailures = row.GetInt32(1);
        }
    }

    public MiscController.PriceResponse[] GetLatestPrices()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT source, from_currency, to_currency, price, price_time FROM latest_price WHERE from_currency='XCH' ORDER BY to_currency";
        using var reader = cmd.ExecuteReader();
        var result = new List<MiscController.PriceResponse>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.None)));
        return result.ToArray();
    }

    public void RefreshInBackgroundIfDue()
    {
        lock (gate)
        {
            if (running || DateTime.UtcNow < nextAttemptUtc) return;
            running = true;
            nextAttemptUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, settings.PriceRefreshMinutes));
            try { SaveRefreshState(); }
            catch (Exception ex) { logger.LogWarning(ex, "Unable to persist price refresh cooldown"); }
        }
        _ = Task.Run(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var prices = string.IsNullOrWhiteSpace(settings.PriceSourceUrl)
                ? await FetchCoinbaseAsync()
                : await FetchJsonFeedAsync(settings.PriceSourceUrl);
            if (prices.Length == 0) throw new InvalidOperationException("Price source returned no rows");
            using var db = Open();
            using var tx = db.BeginTransaction();
            foreach (var price in prices)
            {
                if (!IsValid(price)) throw new InvalidOperationException("Price source returned an invalid row");
                Upsert(db, tx, price);
            }
            tx.Commit();
            lock (gate)
            {
                consecutiveFailures = 0;
                nextAttemptUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, settings.PriceRefreshMinutes));
                SaveRefreshState();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to refresh prices; serving last-known values");
            lock (gate)
            {
                consecutiveFailures = Math.Min(consecutiveFailures + 1, 6);
                nextAttemptUtc = DateTime.UtcNow.AddMinutes(Math.Min(60, 1 << consecutiveFailures));
                try { SaveRefreshState(); }
                catch (Exception saveEx) { logger.LogWarning(saveEx, "Unable to persist price refresh backoff"); }
            }
        }
        finally
        {
            lock (gate) running = false;
        }
    }

    private async Task<MiscController.PriceResponse[]> FetchJsonFeedAsync(string url)
    {
        var json = await http.GetStringAsync(url);
        return JsonSerializer.Deserialize<MiscController.PriceResponse[]>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? Array.Empty<MiscController.PriceResponse>();
    }

    private async Task<MiscController.PriceResponse[]> FetchCoinbaseAsync()
    {
        var result = new List<MiscController.PriceResponse>();
        foreach (var currency in new[] { "CNY", "USD" })
        {
            var json = await http.GetStringAsync($"https://www.coinbase.com/api/v2/assets/prices/chia-network?base={currency}");
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            var price = data.GetProperty("prices").GetProperty("latest").GetDecimal();
            var time = data.GetProperty("prices").GetProperty("latest_price").GetProperty("timestamp").GetDateTime();
            result.Add(new("coinbase", data.GetProperty("base").GetString()!, data.GetProperty("currency").GetString()!, price, time));
        }
        return result.ToArray();
    }

    private static bool IsValid(MiscController.PriceResponse p) =>
        p.From == "XCH" && !string.IsNullOrWhiteSpace(p.To) && !string.IsNullOrWhiteSpace(p.Source)
        && p.Price > 0 && p.Time != default;

    private static void Upsert(SqliteConnection db, SqliteTransaction tx, MiscController.PriceResponse p)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO latest_price (from_currency, to_currency, source, price, price_time)
            VALUES ($from, $to, $source, $price, $time)
            ON CONFLICT(from_currency, to_currency) DO UPDATE SET
                source=excluded.source, price=excluded.price, price_time=excluded.price_time
            WHERE excluded.price_time > latest_price.price_time
            ";
        cmd.Parameters.AddWithValue("$from", p.From);
        cmd.Parameters.AddWithValue("$to", p.To);
        cmd.Parameters.AddWithValue("$source", p.Source);
        cmd.Parameters.AddWithValue("$price", p.Price.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$time", p.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private void SeedIfEmpty(SqliteConnection db)
    {
        using var count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM latest_price";
        if ((long)count.ExecuteScalar()! != 0) return;
        var file = Path.Combine(AppContext.BaseDirectory, "price-seed.json");
        if (!File.Exists(file)) return;
        var prices = JsonSerializer.Deserialize<MiscController.PriceResponse[]>(File.ReadAllText(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? Array.Empty<MiscController.PriceResponse>();
        using var tx = db.BeginTransaction();
        foreach (var price in prices)
        {
            if (!IsValid(price)) throw new InvalidOperationException("Invalid initial price fixture");
            Upsert(db, tx, price);
        }
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private void SaveRefreshState()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"INSERT INTO price_refresh_state (id,next_attempt_utc,consecutive_failures)
            VALUES (1,$next,$failures)
            ON CONFLICT(id) DO UPDATE SET next_attempt_utc=excluded.next_attempt_utc,
                consecutive_failures=excluded.consecutive_failures";
        cmd.Parameters.AddWithValue("$next", nextAttemptUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$failures", consecutiveFailures);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => http.Dispose();
}
