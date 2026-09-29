using System.Buffers.Binary;
using chia.dotnet;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace WalletServer.Helpers;

/// <summary>Read-only access to the existing Chia full-node v2 coin and hint indexes.</summary>
public sealed class ChiaCoinStore
{
    public record PageRow(CoinRecord Record, long SortHeight, byte[] CoinId);

    private readonly string connectionString;
    private readonly object schemaGate = new();
    private bool schemaChecked;

    public ChiaCoinStore(IOptions<AppSettings> options)
    {
        var path = Path.GetFullPath(options.Value.ChiaDbPath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        using var queryOnly = db.CreateCommand();
        queryOnly.CommandText = "PRAGMA query_only=ON";
        queryOnly.ExecuteNonQuery();
        lock (schemaGate)
        {
            if (!schemaChecked)
            {
                using var schema = db.CreateCommand();
                schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('coin_record','hints')";
                if ((long)schema.ExecuteScalar()! != 2)
                    throw new InvalidDataException("Unsupported Chia database: coin_record/hints tables are missing");
                schemaChecked = true;
            }
        }
        return db;
    }

    public PageRow[] GetPage(SqliteConnection db, string hash, bool hint, bool includeSpent,
        uint? startHeight, uint? endHeight, uint snapshotHeight, long offset, int limit,
        long? afterHeight = null, byte[]? afterCoinId = null)
    {
        var from = hint
            ? "hints h JOIN coin_record c ON c.coin_name=h.coin_id"
            : "coin_record c";
        var key = hint ? "h.hint" : "c.puzzle_hash";
        using var cmd = db.CreateCommand();
        cmd.CommandText = $@"
            WITH eligible AS (
                SELECT c.coin_name,c.coin_parent,c.puzzle_hash,c.amount,c.confirmed_index,
                    c.coinbase,c.timestamp,
                    CASE WHEN c.spent_index=0 OR c.spent_index>$snapshot THEN 0
                         ELSE c.spent_index END AS effective_spent
                FROM {from}
                WHERE {key}=$hash AND c.confirmed_index<=$snapshot
                    AND c.confirmed_index>=$start AND c.confirmed_index<$end
                    AND ($includeSpent=1 OR c.spent_index=0 OR c.spent_index>$snapshot)
            ), ordered AS (
                SELECT *,max(confirmed_index,effective_spent) AS sort_height FROM eligible
            )
            SELECT coin_name,coin_parent,puzzle_hash,amount,confirmed_index,effective_spent,
                coinbase,timestamp,sort_height FROM ordered
            WHERE $afterHeight IS NULL OR sort_height<$afterHeight
                OR (sort_height=$afterHeight AND coin_name<$afterCoinId)
            ORDER BY sort_height DESC,coin_name DESC LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$hash", Hex(hash));
        cmd.Parameters.AddWithValue("$snapshot", (long)snapshotHeight);
        cmd.Parameters.AddWithValue("$start", (long)(startHeight ?? 0));
        cmd.Parameters.AddWithValue("$end", endHeight.HasValue ? (long)endHeight.Value : (long)snapshotHeight + 1);
        cmd.Parameters.AddWithValue("$includeSpent", includeSpent ? 1 : 0);
        cmd.Parameters.AddWithValue("$afterHeight", afterHeight is null ? DBNull.Value : afterHeight.Value);
        cmd.Parameters.AddWithValue("$afterCoinId", afterCoinId is null ? DBNull.Value : afterCoinId);
        cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        using var reader = cmd.ExecuteReader();
        var result = new List<PageRow>();
        while (reader.Read())
        {
            var id = reader.GetFieldValue<byte[]>(0);
            var record = new CoinRecord
            {
                Coin = new Coin
                {
                    ParentCoinInfo = "0x" + Convert.ToHexString(reader.GetFieldValue<byte[]>(1)).ToLowerInvariant(),
                    PuzzleHash = "0x" + Convert.ToHexString(reader.GetFieldValue<byte[]>(2)).ToLowerInvariant(),
                    Amount = ReadAmount(reader.GetFieldValue<byte[]>(3)),
                },
                ConfirmedBlockIndex = checked((uint)reader.GetInt64(4)),
                SpentBlockIndex = checked((uint)reader.GetInt64(5)),
                Spent = reader.GetInt64(5) != 0,
                Coinbase = reader.GetInt64(6) != 0,
                Timestamp = checked((ulong)reader.GetInt64(7)),
            };
            result.Add(new PageRow(record, reader.GetInt64(8), id));
        }
        return result.ToArray();
    }

    public FullBalanceInfo GetBalance(SqliteConnection db, string puzzleHash, uint snapshotHeight)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT amount,spent_index FROM coin_record WHERE puzzle_hash=$hash AND confirmed_index<=$snapshot";
        cmd.Parameters.AddWithValue("$hash", Hex(puzzleHash));
        cmd.Parameters.AddWithValue("$snapshot", (long)snapshotHeight);
        using var reader = cmd.ExecuteReader();
        long unspent = 0, spent = 0;
        int unspentCount = 0, spentCount = 0;
        while (reader.Read())
        {
            var amount = ReadAmount(reader.GetFieldValue<byte[]>(0));
            if (amount == 0) continue;
            var spentHeight = reader.GetInt64(1);
            if (spentHeight != 0 && spentHeight <= snapshotHeight)
            {
                spent = checked(spent + checked((long)amount));
                spentCount++;
            }
            else
            {
                unspent = checked(unspent + checked((long)amount));
                unspentCount++;
            }
        }
        return new FullBalanceInfo(unspent, unspentCount, spent, spentCount);
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value.Unprefix0x());

    private static ulong ReadAmount(byte[] amount)
    {
        if (amount.Length != 8) throw new InvalidDataException("Unsupported Chia coin amount encoding");
        return BinaryPrimitives.ReadUInt64BigEndian(amount);
    }
}
