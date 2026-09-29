using System.Text;
using System.Text.Json;
using chia.dotnet;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace WalletServer.Helpers;

/// <summary>Persistent derived classification, never a raw chain-history mirror.</summary>
public sealed class CoinClassCache
{
    private readonly string connectionString;
    private readonly string directory;
    private readonly string processorUrl;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim[] keyLocks = Enumerable.Range(0, 256)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly int maxEntries;
    private readonly object schemaGate = new();
    private bool initialized;
    private int writes;

    public CoinClassCache(IOptions<AppSettings> options)
    {
        var settings = options.Value;
        processorUrl = settings.CoinProcessorUrl.TrimEnd('/');
        maxEntries = Math.Max(1000, settings.CoinCacheMaxEntries);
        var path = Path.GetFullPath(settings.PriceCachePath);
        directory = Path.GetDirectoryName(path)!;
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    private void EnsureInitialized()
    {
        lock (schemaGate)
        {
            if (initialized) return;
            Directory.CreateDirectory(directory);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS coin_class_cache (
            coin_id TEXT PRIMARY KEY,
            spent_height INTEGER NOT NULL,
            block_hash TEXT NOT NULL,
            mods TEXT NOT NULL,
            analysis TEXT NOT NULL,
            last_used_utc INTEGER NOT NULL DEFAULT 0
        );";
        cmd.ExecuteNonQuery();
        using var columns = db.CreateCommand();
        columns.CommandText = "PRAGMA table_info(coin_class_cache)";
        using var reader = columns.ExecuteReader();
        var hasLastUsed = false;
        while (reader.Read())
            hasLastUsed |= reader.GetString(1) == "last_used_utc";
        reader.Close();
        if (!hasLastUsed)
        {
            using var migrate = db.CreateCommand();
            migrate.CommandText = "ALTER TABLE coin_class_cache ADD COLUMN last_used_utc INTEGER NOT NULL DEFAULT 0";
            migrate.ExecuteNonQuery();
        }
            initialized = true;
        }
    }

    public async Task<(string Mods, string Analysis)> GetOrAnalyzeAsync(
        FullNodeProxy node, CoinRecord parent, string blockHash)
    {
        EnsureInitialized();
        var id = ChiaCoinId.FromCoin(parent.Coin);
        var keyLock = keyLocks[(int)((uint)id.GetHashCode() % (uint)keyLocks.Length)];
        await keyLock.WaitAsync();
        try
        {
            using var db = Open();
            using (var read = db.CreateCommand())
            {
                read.CommandText = "SELECT mods,analysis FROM coin_class_cache WHERE coin_id=$id AND spent_height=$height AND block_hash=$hash";
                read.Parameters.AddWithValue("$id", id);
                read.Parameters.AddWithValue("$height", (long)parent.SpentBlockIndex);
                read.Parameters.AddWithValue("$hash", blockHash);
                using var row = read.ExecuteReader();
                if (row.Read())
                {
                    var cached = (row.GetString(0), row.GetString(1));
                    row.Close();
                    using var touch = db.CreateCommand();
                    touch.CommandText = "UPDATE coin_class_cache SET last_used_utc=$now WHERE coin_id=$id";
                    touch.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    touch.Parameters.AddWithValue("$id", id);
                    touch.ExecuteNonQuery();
                    return cached;
                }
            }

            if (!parent.Spent) return ("", "");
            var spend = await node.GetPuzzleAndSolution(id, parent.SpentBlockIndex);
            var payload = JsonSerializer.Serialize(new
            {
                puzzle = spend.PuzzleReveal,
                solution = spend.Solution,
                amount = checked((long)parent.Coin.Amount),
                coin_parent = parent.Coin.ParentCoinInfo,
                puzzle_hash = parent.Coin.PuzzleHash,
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(processorUrl + "/analyze_tx", content);
            response.EnsureSuccessStatusCode();
            using var parsed = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!parsed.RootElement.TryGetProperty("mods", out var modsElement))
                throw new InvalidDataException($"Processor response missing mods for {id}: {parsed.RootElement.GetRawText()[..Math.Min(256, parsed.RootElement.GetRawText().Length)]}");
            var mods = modsElement.GetString() ?? "";
            var hasAnalysis = parsed.RootElement.TryGetProperty("analysis", out var analysisElement);
            var isTargetClass = mods.StartsWith("cat_v2(", StringComparison.Ordinal) ||
                mods.StartsWith("singleton_top_layer_v1_1(did_innerpuz(", StringComparison.Ordinal) ||
                mods.StartsWith("singleton_top_layer_v1_1(nft_state_layer(", StringComparison.Ordinal);
            var analysis = hasAnalysis ? analysisElement.GetString() ?? "" : "";
            // The old syncer kept classified coins with null analysis and retried parsing later.
            // Do not persist that incomplete result; a later request may obtain analysis.
            if (isTargetClass && string.IsNullOrWhiteSpace(analysis)) return (mods, "");
            using var write = db.CreateCommand();
            write.CommandText = @"INSERT INTO coin_class_cache (coin_id,spent_height,block_hash,mods,analysis,last_used_utc)
                VALUES ($id,$height,$hash,$mods,$analysis,$now)
                ON CONFLICT(coin_id) DO UPDATE SET spent_height=excluded.spent_height,
                    block_hash=excluded.block_hash,mods=excluded.mods,analysis=excluded.analysis,
                    last_used_utc=excluded.last_used_utc";
            write.Parameters.AddWithValue("$id", id);
            write.Parameters.AddWithValue("$height", (long)parent.SpentBlockIndex);
            write.Parameters.AddWithValue("$hash", blockHash);
            write.Parameters.AddWithValue("$mods", mods);
            write.Parameters.AddWithValue("$analysis", analysis);
            write.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            write.ExecuteNonQuery();
            if (Interlocked.Increment(ref writes) % 64 == 0) Prune(db);
            return (mods, analysis);
        }
        finally
        {
            keyLock.Release();
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private void Prune(SqliteConnection db)
    {
        using var count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM coin_class_cache";
        var excess = (long)count.ExecuteScalar()! - maxEntries;
        if (excess <= 0) return;
        using var delete = db.CreateCommand();
        delete.CommandText = @"DELETE FROM coin_class_cache WHERE coin_id IN
            (SELECT coin_id FROM coin_class_cache ORDER BY last_used_utc, rowid LIMIT $excess)";
        delete.Parameters.AddWithValue("$excess", excess);
        delete.ExecuteNonQuery();
    }
}
