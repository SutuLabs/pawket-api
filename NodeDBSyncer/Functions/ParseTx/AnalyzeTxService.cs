namespace NodeDBSyncer.Functions.ParseTx;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodeDBSyncer.Services;

internal class AnalyzeTxService : BaseRefreshService
{
    public const int Timeout = 3000;
    private readonly AppSettings appSettings;

    public AnalyzeTxService(
        ILogger<AnalyzeTxService> logger,
        IOptions<AppSettings> appSettings)
        : base(logger, nameof(AnalyzeTxService), 5, 15, Timeout)
    {
        this.appSettings = appSettings.Value;

    }

    protected override async Task DoWorkAsync(CancellationToken token)
    {
        if (this.appSettings.AnalyzingTxBatchSize == 0) return;

        using var target = new ParseTxDbConnection(this.appSettings.OnlineDbConnString);
        await target.Open();
        var nodeProcessor = new LocalNodeProcessor(this.appSettings.LocalNodeProcessor);

        var sw = new Stopwatch();
        sw.Start();
        var threshold = Timeout * 1000 / 2;
        while (sw.ElapsedMilliseconds < threshold)
        {
            var processed = await ParseTx(target, nodeProcessor);
            if (!processed) break;
        }
    }

    private async Task<bool> ParseTx(ParseTxDbConnection db, LocalNodeProcessor nodeProcessor)
    {
        var batch = this.appSettings.AnalyzingTxBatchSize;

        var sw = new Stopwatch();
        sw.Start();
        var txs = await db.GetUnanalyzedTxs(batch);
        if (txs.Length == 0) return false;

        // sync_coin_record may have duplicate coin_name, should be handled.
        txs = txs.GroupBy(_ => _.id).Select(_ => _.First()).ToArray();

        var begin = txs.Min(_ => _.id);
        var end = txs.Max(_ => _.id);
        this.logger.LogInformation($"Parsing tx from id from [{begin}] to [{end}].");


        var lstUpdates = new ConcurrentBag<AnalysisUpdateEntity>();

        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        await Parallel.ForEachAsync(txs, options, async (tx, ct) =>
        {
            try
            {
                var result = await nodeProcessor.AnalyzeTx(tx);
                if (result != null && !string.IsNullOrEmpty(result.analysis))
                {
                    lstUpdates.Add(new AnalysisUpdateEntity(tx.id, result.analysis));
                }
                else
                {
                    lstUpdates.Add(new AnalysisUpdateEntity(tx.id, "{ \"success\": false }"));
                    this.logger.LogWarning($"Tx analyze failed: {tx.coin_name}");
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, $"Tx cannot analyze: {tx.coin_name}");
            }
        });

        var tget = sw.ElapsedMilliseconds;

        if (!lstUpdates.IsEmpty)
        {
            await db.UpdateTxAnalysis(lstUpdates.ToArray());
        }

        sw.Stop();
        this.logger.LogInformation($"Tx Analyzed," +
            $" parse: {tget} ms, persistent: {sw.ElapsedMilliseconds - tget} ms," +
            $" total {lstUpdates.Count} update(s).");

        return true;
    }
}
