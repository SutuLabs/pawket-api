namespace NodeDBSyncer.Functions.ParseSingleton;

using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodeDBSyncer.Services;

internal class ParseSingletonService : BaseRefreshService
{
    public const int Timeout = 3000;
    private readonly AppSettings appSettings;

    public ParseSingletonService(
        ILogger<ParseSingletonService> logger,
        IOptions<AppSettings> appSettings)
        : base(logger, nameof(ParseSingletonService), 5, 15, Timeout)
    {
        this.appSettings = appSettings.Value;

    }

    protected override async Task DoWorkAsync(CancellationToken token)
    {
        if (this.appSettings.ParsingSingletonBatchSize == 0) return;
        if (string.IsNullOrEmpty(this.appSettings.OnlineDbConnString)) return;

        using var target = new ParseSingletonDbConnection(this.appSettings.OnlineDbConnString);
        await target.Open();

        await EnsureCoinSpentIndex(target);

        var sw = new Stopwatch();
        sw.Start();
        var threshold = Timeout * 1000 / 2;
        while (sw.ElapsedMilliseconds < threshold)
        {
            var processed = await ParseSingletonRecords(target);
            if (!processed) break;
        }

        while (sw.ElapsedMilliseconds < threshold)
        {
            var processed = await ParseSingletonHistory(target);
            if (!processed) break;
        }
    }

    private async Task EnsureCoinSpentIndex(ParseSingletonDbConnection db)
    {
        var number = await db.EnsureCoinSpentIndex();
        this.logger.LogInformation($"Ensured coin spent index, number = {number}");
    }

    private async Task<bool> ParseSingletonRecords(ParseSingletonDbConnection db)
    {
        var batch = this.appSettings.ParsingSingletonBatchSize;

        var sw = new Stopwatch();
        sw.Start();
        var start = await db.GetSingletonRecordLatestCoinClassIdSynced();
        var records = await db.GetSingletonRecords(start, batch);
        if (records.Length == 0) return false;

        var begin = records.Min(_ => _.last_coin_class_id);
        var end = records.Max(_ => _.last_coin_class_id);
        this.logger.LogInformation($"Analyzing singleton record from coin class id from [{begin}] to [{end}] [Total: {records.Length}].");

        records = records
            .GroupBy(_ => _.bootstrap_coin_name)
            .Select(_ => _.OrderByDescending(_ => _.last_coin_class_id).First())
            .ToArray();

        var tget = sw.ElapsedMilliseconds;

        await db.UpdateSingletonRecords(records);

        sw.Stop();
        this.logger.LogInformation($"Singleton Record Processed," +
            $" analyze: {tget} ms, persistent: {sw.ElapsedMilliseconds - tget} ms," +
            $" total {records.Length} record(s).");

        return true;
    }

    private async Task<bool> ParseSingletonHistory(ParseSingletonDbConnection db)
    {
        var batch = this.appSettings.ParsingSingletonBatchSize;

        var sw = new Stopwatch();
        sw.Start();
        var start = await db.GetSingletonHistoryLatestCoinClassIdSynced();
        var records = await db.GetSingletonHistories(start, batch);
        if (records.Length == 0) return false;

        var begin = records.Min(_ => _.coin_class_id);
        var end = records.Max(_ => _.coin_class_id);
        this.logger.LogInformation($"Analyzing singleton history from coin class id from [{begin}] to [{end}] [Total: {records.Length}].");

        records = records
            .GroupBy(_ => _.coin_class_id)
            .Select(_ => _.First())
            .ToArray();

        var tget = sw.ElapsedMilliseconds;

        await db.UpdateSingletonHistories(records);

        sw.Stop();
        this.logger.LogInformation($"Singleton History Processed," +
            $" analyze: {tget} ms, persistent: {sw.ElapsedMilliseconds - tget} ms," +
            $" total {records.Length} record(s).");

        return true;
    }
}
