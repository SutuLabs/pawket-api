using System.Data;
using chia.dotnet;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;
using Prometheus;

namespace WalletServer.Helpers;

public class DataAccess : IDisposable
{
    private readonly ILogger<DataAccess> logger;
    private readonly IMemoryCache memoryCache;
    private readonly AppSettings appSettings;
    private readonly NpgsqlConnection connection;

    private static readonly Counter GetCoinByPuzzleHashCount = Metrics.CreateCounter("get_coin_by_puzzle_hash", "");
    private static readonly Counter GetCoinByHintCount = Metrics.CreateCounter("get_coin_by_hint", "");
    private static readonly Counter GetBalanceCount = Metrics.CreateCounter("get_balance", "");
    private static readonly Counter GetPeakHeightCount = Metrics.CreateCounter("get_peak_height", "");

    private bool disposedValue;

    public DataAccess(
        ILogger<DataAccess> logger,
        IMemoryCache memoryCache,
        IOptions<AppSettings> appSettings)
    {
        this.logger = logger;
        this.memoryCache = memoryCache;
        this.appSettings = appSettings.Value;
        this.connection = new NpgsqlConnection(this.appSettings.ConnString);
        this.connection.Open();
    }

    public async Task<CoinRecord[]> GetCoins(
        string puzzleHash,
        bool includeSpent = true,
        GetCoinOrder order = GetCoinOrder.ConfirmedIndexAsc,
        GetCoinMethod method = GetCoinMethod.PuzzleHash,
        long? startIndex = 0,
        long? pageStart = 0,
        int? pageLength = 100)
    {
        if (method == GetCoinMethod.PuzzleHash) GetCoinByPuzzleHashCount.Inc();
        if (method == GetCoinMethod.Hint) GetCoinByHintCount.Inc();

        startIndex ??= 0;
        pageStart ??= 0;
        pageLength ??= 100;

        var sql =
            (method switch
            {
                GetCoinMethod.PuzzleHash => "SELECT * FROM sync_coin_record WHERE puzzle_hash=(@puzzle_hash)",
                GetCoinMethod.Hint => "SELECT c.* FROM sync_hint_record h JOIN sync_coin_record c ON c.coin_name=h.coin_id WHERE hint=(@puzzle_hash)",
                _ => throw new NotImplementedException(),
            })
            + " AND amount>0 AND (confirmed_index >= (@start) or spent_index >= (@start))"
            + (includeSpent ? "" : " AND spent_index=0")
            + (order switch
            {
                GetCoinOrder.AmountDesc => " ORDER BY amount DESC",
                GetCoinOrder.ConfirmedIndexAsc => " ORDER BY confirmed_index",
                GetCoinOrder.AnyIndexDesc => " ORDER BY GREATEST(spent_index, confirmed_index) DESC",
                _ => throw new NotImplementedException(),
            })
            + " LIMIT (@limit) OFFSET (@offset)";
        /*
        "id"	"coin_name"	"confirmed_index"	"spent_index"	"coinbase"	"puzzle_hash"	"coin_parent"	"amount"	"timestamp"
        114349505	"binary data"	1745850	0	false	"binary data"	"binary data"	31	1648248020
         */
        using var cmd = new NpgsqlCommand(sql, this.connection)
        {
            Parameters = {
                new("puzzle_hash", HexMate.Convert.FromHexString(puzzleHash.AsSpan())),
                new("limit", pageLength),
                new("offset", pageStart),
                new("start", startIndex),
            }
        };
        var reader = await cmd.ExecuteReaderAsync();
        var dt = new DataTable();
        dt.Load(reader);
        var records = dt.Rows
            .OfType<DataRow>()
            .Select(_ => new CoinRecord
            {
                Coinbase = _["coinbase"] as bool? ?? false,
                ConfirmedBlockIndex = Convert.ToUInt32(_["confirmed_index"] as long? ?? 0L),
                SpentBlockIndex = Convert.ToUInt32(_["spent_index"] as long? ?? 0L),
                Timestamp = Convert.ToUInt32(_["timestamp"] as long? ?? 0L),
                Spent = (_["spent_index"] as long? ?? 0L) > 0,
                Coin = new Coin
                {
                    Amount = Convert.ToUInt64(_["amount"] as long? ?? 0L),
                    ParentCoinInfo = (_["coin_parent"] as byte[]).ToHexWithPrefix0x(),
                    PuzzleHash = (_["puzzle_hash"] as byte[]).ToHexWithPrefix0x(),
                }
            })
            .ToArray();

        return records;
    }

    public async Task<FullBalanceInfo> GetBalance(string puzzleHash)
    {
        GetBalanceCount.Inc();
        var sql = @"SELECT sum(amount)::bigint, count(*), CASE WHEN spent_index = 0 THEN 0 ELSE 1 END AS spent FROM sync_coin_record
WHERE puzzle_hash=(@puzzle_hash)
AND amount > 0
GROUP BY puzzle_hash, spent";
        /*
        "sum"	"count"	"spent"
        8000000000000	8	0
        538045426319383	177120	1
         */
        var hex = HexMate.Convert.FromHexString(puzzleHash.AsSpan());
        using var cmd = new NpgsqlCommand(sql, this.connection)
        {
            Parameters = { new("puzzle_hash", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = hex }, }
        };
        var reader = await cmd.ExecuteReaderAsync();
        var dt = new DataTable();
        dt.Load(reader);
        var rows = dt.Rows
            .OfType<DataRow>()
            .Select(_ => new { sum = _["sum"] as long?, count = _["count"] as long?, spent = Convert.ToBoolean(_["spent"]) })
            .ToDictionary(_ => _.spent, _ => _);

        var spentAmount = 0L;
        var spentCount = 0;

        if (rows.TryGetValue(true, out var spent))
        {
            spentAmount = spent.sum ?? 0;
            spentCount = (int?)spent.count ?? 0;
        }

        var unspentAmount = 0L;
        var unspentCount = 0;

        if (rows.TryGetValue(false, out var unspent))
        {
            unspentAmount = unspent.sum ?? 0;
            unspentCount = (int?)unspent.count ?? 0;
        }

        return new FullBalanceInfo(unspentAmount, unspentCount, spentAmount, spentCount);
    }


    public async Task<long> GetPeakHeight()
    {
        GetPeakHeightCount.Inc();
        var sql = "select max(spent_index) from sync_coin_record";
        using var cmd = new NpgsqlCommand(sql, this.connection);
        var o = await cmd.ExecuteScalarAsync();
        return o is DBNull ? 0
            : o is long lo ? lo
            : 0;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                this.connection.Close();
                this.connection.Dispose();
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

public enum GetCoinOrder
{
    AmountDesc,
    ConfirmedIndexAsc,
    AnyIndexDesc,
}

public enum GetCoinMethod
{
    PuzzleHash,
    Hint,
}

public record FullBalanceInfo(long Amount, int CoinCount, long SpentAmount, int SpentCount);
