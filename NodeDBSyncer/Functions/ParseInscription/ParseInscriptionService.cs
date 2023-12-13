namespace NodeDBSyncer.Functions.ParseInscription;

using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using chia.dotnet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NodeDBSyncer.Services;

internal class ParseInscriptionService : BaseRefreshService
{
    public const long MAX_SAFE_INTEGER = 9007199254740991;
    public const int Timeout = 1000;
    private readonly AppSettings appSettings;

    public ParseInscriptionService(
        ILogger<ParseInscriptionService> logger,
        IOptions<AppSettings> appSettings)
        : base(logger, nameof(ParseInscriptionService), 5, 15, Timeout)
    {
        this.appSettings = appSettings.Value;

    }

    protected override async Task DoWorkAsync(CancellationToken token)
    {
        if (this.appSettings.ParsingInscriptionBatchSize == 0) return;
        if (string.IsNullOrEmpty(this.appSettings.OnlineDbConnString)) return;

        using var target = new ParseInscriptionDbConnection(this.appSettings.OnlineDbConnString);
        await target.Open();

        var sw = new Stopwatch();
        sw.Start();
        var threshold = Timeout * 1000;
        while (sw.ElapsedMilliseconds < threshold)
        {
            var processed = await ParseInscriptionRecords(target);
            if (!processed) break;
        }
    }

    private async Task<bool> ParseInscriptionRecords(ParseInscriptionDbConnection db)
    {
        var batch = this.appSettings.ParsingInscriptionBatchSize;

        var sw = new Stopwatch();
        sw.Start();
        var available = await db.GetLatestBlockIndex();
        var start = await db.GetInscriptionRecordProcessedBlockIndex();

        if (start >= available) return false;

        var records = await db.GetRawInscriptionRecords(start, batch);
        this.logger.LogInformation(
            $"Analyzing inscription record from block index from [{start}] to [{start + batch}]" +
            $" [Total: {records.Length}].");
        if (records.Length == 0)
        {
            await db.WriteInscriptionRecordProcessedBlockIndex(start + batch);
            return true;
        }

        var rs = new List<InscriptionRecordRaw>();
        var blkidx = 0L;
        var serial = 0;
        foreach (var record in records)
        {
            if (record.spent_index < blkidx)
                throw new NotSupportedException("The retrieval inscription records should in order");

            if (record.spent_index > blkidx)
            {
                blkidx = record.spent_index;
                serial = 0;
            }

            rs.Add(record with { serial = serial });
            serial++;
        }

        var irecords = ConvertToRecord(rs).ToArray();
        var trecords = ConvertToTickRecord(irecords).ToArray();

        var tget = sw.ElapsedMilliseconds;

        await db.UpdateInscriptionRecords(irecords);
        await db.UpdateInscriptionTicks(trecords);
        await db.WriteInscriptionRecordProcessedBlockIndex(start + batch);

        sw.Stop();
        this.logger.LogInformation($"Inscription Record Processed," +
            $" retrieval: {tget} ms, persistent: {sw.ElapsedMilliseconds - tget} ms," +
            $" total {irecords.Length} inscription record(s)" +
            $" and {trecords.Length} inscription tick(s).");

        return true;
    }

    private IEnumerable<InscriptionRecordInfo> ConvertToRecord(IEnumerable<InscriptionRecordRaw> raw)
    {
        foreach (var item in raw)
        {
            JsonP2InscriptionCoinAnalysisResult ana;
            try
            {
                var obj = JsonConvert.DeserializeObject<JsonP2InscriptionCoinAnalysisResult>(item.analysis ?? "");
                if (obj == null)
                {
                    logger.LogWarning($"Not able to parse raw inscription record: {item.analysis}");
                    continue;
                }
                ana = obj;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, $"failed to parse raw inscription record: {item.analysis}");
                continue;
            }

            foreach (var r in ConvertToRecord(item, ana))
            {
                yield return r;
            }
        }
    }

    private IEnumerable<InscriptionRecordInfo> ConvertToRecord(
        InscriptionRecordRaw raw, JsonP2InscriptionCoinAnalysisResult ana)
    {
        for (int i = 0; i < ana.coins.Length; i++)
        {
            var coin = ana.coins[i];
            if (coin.meta == null) continue;

            var meta = "{}";
            if (coin.meta.p != "xchs") continue;
            if (coin.meta.tick.Length != 4) continue;
            var op = coin.meta.op;
            if (op != "mint" && op != "transfer" && op != "deploy") continue;
            var tick = coin.meta.tick.ToLower();

            if ((op == "mint" || op == "transfer") && coin.meta.amt == null) continue;

            if (op == "deploy")
            {
                if (coin.meta.max == null) continue;
                if (coin.meta.lim == null) continue;
                if (coin.meta.max > MAX_SAFE_INTEGER) continue;
                if (coin.meta.lim > MAX_SAFE_INTEGER) continue;
                var lim = coin.meta.lim > coin.meta.max ? coin.meta.max : coin.meta.lim;
                meta = JsonConvert.SerializeObject(new JsonDeployMeta(lim ?? 0, coin.meta.max ?? 0));
            }

            yield return new InscriptionRecordInfo(
                raw.coin_class_id,
                raw.spent_index,
                raw.serial,
                i,
                coin.coinId,
                coin.parent,
                coin.from,
                coin.to,
                coin.meta.p,
                op,
                tick,
                coin.meta.amt ?? 0,
                null,
                null,
                meta);
        }
    }

    private IEnumerable<TickInfo> ConvertToTickRecord(IEnumerable<InscriptionRecordInfo> records)
    {
        foreach (var r in records)
        {
            if (r.op != "deploy") continue;
            long max;
            long lim;
            try
            {
                var meta = JsonConvert.DeserializeObject<JsonDeployMeta>(r.meta ?? "{}");
                if (meta == null) continue;
                max = meta.max;
                lim = meta.lim;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, $"failed to parse inscription record meta: {r.meta}");
                continue;
            }

            yield return new TickInfo(
                r.coin_name, r.tick, max, lim, "{}");
        }
    }
}
