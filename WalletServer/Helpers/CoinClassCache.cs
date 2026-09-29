using System.Collections.Concurrent;
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
    private readonly string processorUrl;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> keyLocks = new();

    public CoinClassCache(IOptions<AppSettings> options)
    {
        var settings = options.Value;
        processorUrl = settings.CoinProcessorUrl.TrimEnd('/');
        var path = Path.GetFullPath(settings.PriceCachePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS coin_class_cache (
            coin_id TEXT PRIMARY KEY,
            spent_height INTEGER NOT NULL,
            block_hash TEXT NOT NULL,
            mods TEXT NOT NULL,
            analysis TEXT NOT NULL
        );";
        cmd.ExecuteNonQuery();
    }

    public async Task<(string Mods, string Analysis)> GetOrAnalyzeAsync(
        FullNodeProxy node, CoinRecord parent, string blockHash)
    {
        var id = parent.Coin.Name;
        var keyLock = keyLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
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
                if (row.Read()) return (row.GetString(0), row.GetString(1));
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
            var mods = parsed.RootElement.GetProperty("mods").GetString() ?? "";
            var analysis = parsed.RootElement.GetProperty("analysis").GetString() ?? "";
            using var write = db.CreateCommand();
            write.CommandText = @"INSERT INTO coin_class_cache (coin_id,spent_height,block_hash,mods,analysis)
                VALUES ($id,$height,$hash,$mods,$analysis)
                ON CONFLICT(coin_id) DO UPDATE SET spent_height=excluded.spent_height,
                    block_hash=excluded.block_hash,mods=excluded.mods,analysis=excluded.analysis";
            write.Parameters.AddWithValue("$id", id);
            write.Parameters.AddWithValue("$height", (long)parent.SpentBlockIndex);
            write.Parameters.AddWithValue("$hash", blockHash);
            write.Parameters.AddWithValue("$mods", mods);
            write.Parameters.AddWithValue("$analysis", analysis);
            write.ExecuteNonQuery();
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
}
