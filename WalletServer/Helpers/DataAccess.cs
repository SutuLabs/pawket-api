using System.Data;
using chia.dotnet;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NodeDBSyncer.Helpers;
using Npgsql;
using Prometheus;

namespace WalletServer.Helpers;

public class DataAccess : IDisposable
{
    private const string SqlLastIndexLateral = ", LATERAL (SELECT spent_index AS last_index FROM sync_state WHERE id=1) AS t";
    private const string SqlIndexConstraint = " AND (c.confirmed_index <= last_index) AND (c.spent_index <= last_index)";
    private const string PriceTableName = "series_price";

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
        this.connection = new NpgsqlConnection(this.appSettings.OnlineDbConnString);
        this.connection.Open();
    }

    public async Task<CoinRecordWithAnalysis[]> GetCoins(
        string[] puzzleHashes,
        bool includeSpent = true,
        GetCoinOrder order = GetCoinOrder.ConfirmedIndexAsc,
        GetCoinMethod method = GetCoinMethod.PuzzleHash,
        long? startIndex = 0,
        long? pageStart = 0,
        int? pageLength = 100,
        CoinClassType? coinClassType = null)
    {
        if (method == GetCoinMethod.PuzzleHash) GetCoinByPuzzleHashCount.Inc();
        if (method == GetCoinMethod.Hint) GetCoinByHintCount.Inc();

        startIndex ??= 0;
        pageStart ??= 0;
        pageLength ??= 100;

        var sql =
            (method switch
            {
                GetCoinMethod.PuzzleHash => $"SELECT c.* FROM sync_coin_record c{SqlLastIndexLateral} WHERE puzzle_hash=ANY(@puzzle_hash)",
                GetCoinMethod.Hint => $"SELECT c.* FROM sync_hint_record h JOIN sync_coin_record c ON c.coin_name=h.coin_name{SqlLastIndexLateral} WHERE hint=ANY(@puzzle_hash)",
                GetCoinMethod.Class => $"SELECT c.*, cc.analysis as analysis FROM sync_hint_record h JOIN sync_coin_record c ON c.coin_name=h.coin_name" +
                $" JOIN sync_coin_record pc ON pc.coin_name = c.coin_parent" +
                $" FULL JOIN sync_coin_class cc ON cc.coin_name = pc.coin_name{SqlLastIndexLateral} WHERE hint=ANY(@puzzle_hash)",
                _ => throw new NotImplementedException(),
            })
            + SqlIndexConstraint
            + (includeSpent ? "" : " AND c.spent_index=0")
            + (coinClassType == null ? "" : " AND mods = ANY(@mods)")
            + (order switch
            {
                GetCoinOrder.AmountDesc => " ORDER BY c.amount DESC",
                GetCoinOrder.ConfirmedIndexAsc => " ORDER BY c.confirmed_index",
                GetCoinOrder.AnyIndexDesc => " ORDER BY GREATEST(c.spent_index, c.confirmed_index) DESC",
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
                new("puzzle_hash", puzzleHashes.Select(_=> HexMate.Convert.FromHexString(_.Unprefix0x().AsSpan())).ToArray()),
                new("limit", pageLength),
                new("offset", pageStart),
                new("start", startIndex),
            }
        };
        if (coinClassType is CoinClassType cct) cmd.Parameters.AddWithValue("mods", GetTypeStringArrayByType(cct));
        var reader = await cmd.ExecuteReaderAsync();
        var dt = new DataTable();
        dt.Load(reader);
        var records = dt.Rows
            .OfType<DataRow>()
            .Select(_ => new CoinRecordWithAnalysis
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
                },
                Analysis = _.ItemArray.Length <= 9 ? null : _["analysis"] as string,
            })
            .ToArray();

        return records;
    }

    public async Task<FullBalanceInfo> GetBalance(string puzzleHash)
    {
        GetBalanceCount.Inc();
        var sql = "SELECT sum(amount)::bigint, count(*), CASE WHEN spent_index = 0 THEN 0 ELSE 1 END AS spent FROM sync_coin_record c"
            + SqlLastIndexLateral
            + " WHERE puzzle_hash=(@puzzle_hash)"
            + "AND amount > 0"
            + SqlIndexConstraint
            + " GROUP BY puzzle_hash, spent";
        /*
        "sum"	"count"	"spent"
        8000000000000	8	0
        538045426319383	177120	1
         */
        var hex = HexMate.Convert.FromHexString(puzzleHash.Unprefix0x().AsSpan());
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

    public async Task<CoinDetail[]> GetCoinDetails(string[] coinIds, long? pageStart = 0, int? pageLength = 100)
    {
        using var cmd = new NpgsqlCommand(
            $"SELECT c.amount, c.coin_parent, c.puzzle_hash, cc.puzzle, cc.solution FROM sync_coin_record c"
            + $" LEFT JOIN sync_coin_class cc ON c.coin_name = cc.coin_name"
            + SqlLastIndexLateral
            + $" WHERE c.coin_name = ANY(@coin_name)"
            + SqlIndexConstraint
            + $" ORDER BY GREATEST(c.spent_index, c.confirmed_index) DESC"
            + $" LIMIT (@limit) OFFSET (@offset)"
            , connection)
        {
            Parameters =
            {
                new("coin_name", coinIds.Select(_=> HexMate.Convert.FromHexString(_.Unprefix0x().AsSpan())).ToArray()),
                new("limit", pageLength),
                new("offset", pageStart),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var dt = new DataTable();
        dt.Load(reader);
        var rows = dt.Rows
            .OfType<DataRow>()
            .Select(_ => new
            {
                amount = _["amount"] as long?,
                parent = _["coin_parent"] as byte[],
                hash = _["puzzle_hash"] as byte[],
                puzzle = _["puzzle"] as byte[],
                solution = _["solution"] as byte[],
            })
            .Select(_ => (_.amount == null || _.parent == null || _.hash == null) ? null : new CoinDetail(
                Convert.ToUInt64(_.amount),
                _.parent.ToHexWithPrefix0x(),
                _.hash.ToHexWithPrefix0x(),
                _.puzzle?.Decompress().ToHexWithPrefix0x(),
                _.solution?.Decompress().ToHexWithPrefix0x()))
            .WhereNotNull()
            .ToArray();

        return rows;
    }

    public async Task<CoinPuzzleInfo[]> GetParentPuzzle(string[] coinIds)
    {
        using var cmd = new NpgsqlCommand(
            $"SELECT cc.coin_name, cc.puzzle, c.amount, c.coin_parent FROM sync_coin_class cc"
            + $" JOIN sync_coin_record c ON c.coin_name = cc.coin_name"
            + SqlLastIndexLateral
            + $" WHERE cc.coin_name = ANY(@coin_name)"
            + SqlIndexConstraint
            , connection)
        {
            Parameters =
            {
                new("coin_name", coinIds.Select(_=> HexMate.Convert.FromHexString(_.Unprefix0x().AsSpan())).ToArray()),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var dt = new DataTable();
        dt.Load(reader);
        var rows = dt.Rows
            .OfType<DataRow>()
            .Select(_ => new
            {
                amount = _["amount"] as long?,
                parent = _["coin_parent"] as byte[],
                name = _["coin_name"] as byte[],
                puzzle = _["puzzle"] as byte[],
            })
            .Select(_ => (_.amount == null || _.parent == null || _.puzzle == null) ? null : new CoinPuzzleInfo(
                _.name.ToHexWithPrefix0x(),
                Convert.ToUInt64(_.amount),
                _.parent.ToHexWithPrefix0x(),
                _.puzzle.Decompress().ToHexWithPrefix0x()))
            .WhereNotNull()
            .ToArray();

        return rows;
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

    public async Task<PriceEntity[]> GetLatestPrices(string from)
    {
        // example: from is XCH, while to is USDT/CNY
        using var cmd = new NpgsqlCommand(
            $"SELECT DISTINCT on (\"{nameof(PriceEntity.to)}\")" +
            $" \"{nameof(PriceEntity.source)}\",\"{nameof(PriceEntity.to)}\",\"{nameof(PriceEntity.price)}\",\"{nameof(PriceEntity.time)}\"" +
            $" FROM {PriceTableName}" +
            $" WHERE \"{nameof(PriceEntity.from)}\"=@{nameof(PriceEntity.from)}" +
            $" ORDER BY 2, 4 DESC;", connection)
        {
            Parameters =
            {
                new (nameof(PriceEntity.from),from),
                //new (nameof(PriceEntity.to),to),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var dt = new DataTable();
        dt.Load(reader);
        var rows = dt.Rows
            .OfType<DataRow>()
            .Select(_ => new PriceEntity(
                _[nameof(PriceEntity.source)] as string ?? "UNKNOWN",
                from,
                _[nameof(PriceEntity.to)] as string ?? "UNKNOWN",
                (decimal)_[nameof(PriceEntity.price)],
                (DateTime)_[nameof(PriceEntity.time)]))
            .ToArray();

        return rows;
    }

    public async Task<NameEntity[]> GetAllNameEntities(string creator_puzzle_hash)
    {
        using var cmd = new NpgsqlCommand(
            $@"
SELECT
    sr.singleton_coin_name AS nft_coin_name,
    sh.this_coin_name AS last_change_coin_name,
    sh.this_coin_spent_index AS last_change_spent_index,
    (CASE WHEN (cc.analysis->>'cnsName' != '') THEN cc.analysis->>'cnsName' ELSE cc.analysis->'metadata'->>'name' END) AS name,
    (CASE WHEN (cc.analysis->>'cnsAddress' != '') THEN cc.analysis->>'cnsAddress' ELSE cc.analysis->'metadata'->>'address' END) AS address,
    cc.analysis->>'cnsBindings' AS bindings
FROM ext_singleton_record sr
LEFT JOIN ext_singleton_history sh ON sr.singleton_coin_name = sh.singleton_coin_name
LEFT JOIN sync_coin_record c ON sh.next_coin_name=c.coin_name
LEFT JOIN sync_coin_class cc ON sh.this_coin_name=cc.coin_name
WHERE sr.creator_puzzle_hash=@ph
AND c.spent_index=0
AND sr.type='nft_v1'
ORDER BY last_change_spent_index ASC;", connection)
        {
            Parameters =
            {
                new ("ph", creator_puzzle_hash.ToHexBytes()),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var dt = new DataTable();
        dt.Load(reader);
        var rows = dt.Rows
            .OfType<DataRow>()
            .Select(_ => new NameEntity(
                (_[nameof(NameEntity.nft_coin_name)] as byte[]).ToHexWithPrefix0x(),
                (_[nameof(NameEntity.last_change_coin_name)] as byte[]).ToHexWithPrefix0x(),
                (int)_[nameof(NameEntity.last_change_spent_index)],
                (_[nameof(NameEntity.name)] as string ?? "").ToLower(),
                _[nameof(NameEntity.address)] as string ?? "",
                ParseBindings(_[nameof(NameEntity.bindings)] as string ?? "")))
            .ToArray();

        return rows;
    }

    public async Task<RecentNameEntity[]> GetRecentNames(string creator_puzzle_hash, int? limit = null)
    {
        limit = limit ?? 10;
        limit = limit > 100 ? 100 : limit;
        using var cmd = new NpgsqlCommand(
            $@"
SELECT
    sr.singleton_coin_name AS nft_coin_name,
	sr.singleton_create_index,
    (CASE WHEN (cc.analysis->>'cnsName' != '') THEN cc.analysis->>'cnsName' ELSE cc.analysis->'metadata'->>'name' END) AS name
FROM ext_singleton_record sr
LEFT JOIN ext_singleton_history sh ON sr.singleton_coin_name = sh.singleton_coin_name
LEFT JOIN sync_coin_record c ON sh.next_coin_name=c.coin_name
LEFT JOIN sync_coin_class cc ON sh.this_coin_name=cc.coin_name
WHERE sr.creator_puzzle_hash=@ph
AND c.spent_index=0
AND sr.type='nft_v1'
ORDER BY singleton_create_index DESC
LIMIT @limit;", connection)
        {
            Parameters =
            {
                new ("ph", creator_puzzle_hash.ToHexBytes()),
                new ("limit", limit),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var dt = new DataTable();
        dt.Load(reader);
        var rows = dt.Rows
            .OfType<DataRow>()
            .Select(_ => new RecentNameEntity(
                (_[nameof(RecentNameEntity.nft_coin_name)] as byte[]).ToHexWithPrefix0x(),
                (int)_[nameof(RecentNameEntity.singleton_create_index)],
                (_[nameof(RecentNameEntity.name)] as string ?? "").ToLower()))
            .ToArray();

        return rows;
    }

    public async Task<WealthiestNameEntity[]> GetWealthiestNames(string creator_puzzle_hash, int? limit = null)
    {
        limit = limit ?? 10;
        limit = limit > 100 ? 100 : limit;
        using var cmd = new NpgsqlCommand(
            $@"
WITH names_table AS (
SELECT
    (CASE WHEN (cc.analysis->>'cnsName' != '') THEN cc.analysis->>'cnsName' ELSE cc.analysis->'metadata'->>'name' END) AS name,
    decode((CASE WHEN (cc.analysis->>'cnsAddress' != '') THEN cc.analysis->>'cnsAddress' ELSE cc.analysis->'metadata'->>'address' END),'hex') AS address
FROM ext_singleton_record sr
    LEFT JOIN ext_singleton_history sh ON sr.singleton_coin_name = sh.singleton_coin_name
    LEFT JOIN sync_coin_record c ON sh.next_coin_name=c.coin_name
    LEFT JOIN sync_coin_class cc ON sh.this_coin_name=cc.coin_name
WHERE sr.creator_puzzle_hash=@ph
    AND c.spent_index=0
    AND sr.type='nft_v1'
)
SELECT
	sum(amount)::bigint as balance,
	c.puzzle_hash,
	count(distinct(n.name)) as count,
	array_to_string((array_agg(distinct n.name))[1:10],',') as names
FROM sync_coin_record c
JOIN names_table n on n.address = c.puzzle_hash
WHERE amount > 0
AND spent_index = 0
GROUP BY c.puzzle_hash, n.address
ORDER BY balance DESC
LIMIT @limit;", connection)
        {
            Parameters =
            {
                new ("ph", creator_puzzle_hash.ToHexBytes()),
                new ("limit", limit),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var dt = new DataTable();
        dt.Load(reader);
        var rows = dt.Rows
            .OfType<DataRow>()
            .Select(_ => new WealthiestNameEntity(
                _[nameof(WealthiestNameEntity.balance)] as long? ?? -1,
                (_[nameof(WealthiestNameEntity.puzzle_hash)] as byte[]).ToHexWithPrefix0x(),
                _[nameof(WealthiestNameEntity.count)] as long? ?? -1,
                (_[nameof(WealthiestNameEntity.names)] as string ?? "")))
            .ToArray();

        return rows;
    }

    public async Task<CoinAnalysis[]> GetCoinAnalysis(string[] coinNames, long? pageStart = 0, int? pageLength = 100)
    {
        using var cmd = new NpgsqlCommand(
            $"SELECT coin_name,analysis FROM sync_coin_class"
            + $" WHERE coin_name = ANY(@coin_name)"
            + $" ORDER BY id DESC"
            + $" LIMIT (@limit) OFFSET (@offset)"
            , connection)
        {
            Parameters =
            {
                new("coin_name", coinNames.Select(_=> HexMate.Convert.FromHexString(_.Unprefix0x().AsSpan())).ToArray()),
                new("limit", pageLength),
                new("offset", pageStart),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var dt = new DataTable();
        dt.Load(reader);
        var rows = dt.Rows
            .OfType<DataRow>()
            .Select(_ => new
            {
                coinname = _["coin_name"] as byte[],
                analysis = _["analysis"] as string,
            })
            .Select(_ => (_.analysis == null) ? null : new CoinAnalysis(_.coinname.ToHexWithPrefix0x(), _.analysis))
            .WhereNotNull()
            .ToArray();

        return rows;
    }

    private Dictionary<string, string> ParseBindings(string bindingString)
    {
        if (string.IsNullOrEmpty(bindingString)) return new Dictionary<string, string>();
        try
        {
            var jobj = JsonConvert.DeserializeObject<Dictionary<string, string>>(bindingString);
            if (jobj == null) return new Dictionary<string, string>();
            return jobj;
        }
        catch (Exception ex)
        {
            logger.LogWarning($"cannot parse bindings [{ex.Message}]: {bindingString}");
            return new Dictionary<string, string>();
        }
    }

    private string[] GetTypeStringArrayByType(CoinClassType type)
    {
        switch (type)
        {
            case CoinClassType.CatV2:
                return new[] {
                    "cat_v2()",
                    "cat_v2(p2_delegated_puzzle_or_hidden_puzzle())",
                    "cat_v2(settlement_payments())",
                    "cat_v2(settlement_payments_v1())",
                };
            case CoinClassType.DidV1:
                return new[] {
                    "singleton_top_layer_v1_1(did_innerpuz(p2_delegated_puzzle_or_hidden_puzzle()))",
                };
            case CoinClassType.NftV1:
                return new[] {
                    "singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),p2_delegated_puzzle_or_hidden_puzzle())))",
                    "singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),settlement_payments())))",
                    "singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),settlement_payments_v1())))",
                };
            default:
                throw new NotImplementedException($"Unrecognize class type: {type}");
        }
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
    Class,
}

public enum CoinClassType
{
    CatV2,
    DidV1,
    NftV1,
}

public record FullBalanceInfo(long Amount, int CoinCount, long SpentAmount, int SpentCount);
public record PriceEntity(string source, string from, string to, decimal price, DateTime time);
public record NameEntity(
    string nft_coin_name,
    string last_change_coin_name,
    int last_change_spent_index,
    string name,
    string address,
    Dictionary<string, string> bindings);

public record RecentNameEntity(
    string nft_coin_name,
    int singleton_create_index,
    string name);

public record WealthiestNameEntity(
    long balance,
    string puzzle_hash,
    long count,
    string names);

public record CoinDetail
(
     ulong Amount,
     string ParentCoinInfo,
     string PuzzleHash,
     string? PuzzleReveal,
     string? Solution
);

public record CoinPuzzleInfo(string CoinName, ulong Amount, string ParentCoinName, string PuzzleReveal);

public record CoinAnalysis(string CoinName, string Analysis);

public record CoinRecordWithAnalysis : CoinRecord
{
    public string? Analysis { get; init; }
}
