namespace NodeDBSyncer.Functions.SyncCoin;

using System.Buffers.Binary;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodeDBSyncer.Services;

internal class SyncDbService : BaseRefreshService
{
    private readonly AppSettings appSettings;

    public SyncDbService(
        ILogger<SyncDbService> logger,
        IOptions<AppSettings> appSettings)
        : base(logger, nameof(SyncDbService), 2, 15, 3000)
    {
        this.appSettings = appSettings.Value;
    }

    protected override async Task DoWorkAsync(CancellationToken token)
    {
        if (this.appSettings.SyncInsertBatchSize == 0 && this.appSettings.SyncUpdateBatchSize == 0) return;

        using var source = new SouceConnection(this.appSettings.LocalSqliteConnString);
        source.Open();
        using var target = new PgsqlTargetConnection(this.appSettings.OnlineDbConnString);
        await target.Open();

        var complete = await SyncTables(source, target);
        if (!complete) return;

        // for first time finish initialize, initialize the index
        if (!await target.CheckIndexExistence())
        {
            this.logger.LogInformation($"First finish initialization, starting index initialization");
            await target.InitializeIndex();
        }

        await UpdateSpentIndex(source, target);
    }

    private async Task<bool> SyncTables(SouceConnection source, PgsqlTargetConnection target)
    {
        var batch = this.appSettings.SyncInsertBatchSize;
        if (batch == 0) return true;

        var exist = await target.GetLastSyncSpentHeight();
        if (exist == 0)
        {
            var peak = await source.GetPeakSpentHeight() - 1;
            await target.WriteLastSyncSpentHeight(peak);
        }

        var complete = true;

        {
            var sourceCount = await source.GetTotalCoinRecords();
            var targetCount = await target.GetTotalCoinRecords();
            var totalBatch = Math.Ceiling((double)(sourceCount - targetCount) / batch);
            var max = Math.Min(totalBatch, this.appSettings.SyncBatchCount);
            if (totalBatch != max) complete = false;
            if (max > 0)
                this.logger.LogInformation($"sync coin records [{targetCount}]~[{sourceCount}](+{sourceCount - targetCount}) with {max} batches.");

            for (var i = 0; i < max; i++)
            {
                var sw = new Stopwatch();
                sw.Start();
                var records = source.GetCoinRecords(targetCount + i * batch, batch);
                var dt = ConvertRecordsToTable(records);
                var tget = sw.ElapsedMilliseconds;
                sw.Restart();
                await target.WriteCoinRecords(dt);
                sw.Stop();
                this.logger.LogInformation($"batch processed coin records [{targetCount + i * batch}]~[{targetCount + i * batch + batch}], {tget} ms, {sw.ElapsedMilliseconds} ms");
            }
        }

        {
            var sourceCount = await source.GetTotalHintRecords();
            var targetCount = await target.GetTotalHintRecords();

            var totalBatch = Math.Ceiling((double)(sourceCount - targetCount) / batch);
            var max = Math.Min(totalBatch, this.appSettings.SyncBatchCount);
            if (totalBatch != max) complete = false;
            if (max > 0)
                this.logger.LogInformation($"sync hint records [{targetCount}]~[{sourceCount}](+{sourceCount - targetCount}) with {max} batches.");

            for (var i = 0; i < max; i++)
            {
                var sw = new Stopwatch();
                sw.Start();
                var records = source.GetHintRecords(targetCount + i * batch, batch);
                var dt = ConvertHintRecordsToTable(records);
                var tget = sw.ElapsedMilliseconds;
                sw.Restart();
                await target.WriteHintRecords(dt);
                sw.Stop();
                this.logger.LogInformation($"batch processed hint records [{targetCount + i * batch}]~[{targetCount + i * batch + batch}], {tget} ms, {sw.ElapsedMilliseconds} ms");
            }
        }

        return complete;
    }

    private async Task UpdateSpentIndex(SouceConnection source, PgsqlTargetConnection target)
    {
        var batch = this.appSettings.SyncUpdateBatchSize;
        var sourcePeak = await source.GetPeakSpentHeight() - 1; // ignore last block as it may be during writing phase
        var targetPeak = await target.GetLastSyncSpentHeight() + 1; // saved state is processed peak, we start from next one

        var number = sourcePeak - targetPeak;
        if (number > 0)
            this.logger.LogInformation($"sync spent records [{targetPeak}]~[{sourcePeak}](+{number}).");

        var current = targetPeak;
        while (current < sourcePeak)
        {
            var sw = new Stopwatch();
            sw.Start();
            var records = source.GetSpentHeightChange(current, batch).ToArray();

            var tc = current;
            current = records.Max(_ => _.spent_index) - 1;// ignore records from last block in this batch, as it may not be complete
            if (current > sourcePeak) current = sourcePeak;
            records = records.Where(_ => _.spent_index <= current).ToArray();

            var tget = sw.ElapsedMilliseconds;
            sw.Restart();
            var affectedRow = await target.WriteSpentHeight(records);
            sw.Stop();
            await target.WriteLastSyncSpentHeight(current);
            this.logger.LogInformation($"batch processed spent records [{tc}]~[{current}], {tget} ms, {sw.ElapsedMilliseconds} ms, affected {affectedRow} row(s).");

            current++;// saved state is processed block, next loop process from next block
        }
    }

    private DataTable ConvertRecordsToTable(IEnumerable<CoinRecord> records)
    {
        var dt = new DataTable();
        dt.Columns.Add(nameof(CoinRecord.id), typeof(long));
        dt.Columns.Add(nameof(CoinRecord.coin_name), typeof(byte[]));
        dt.Columns.Add(nameof(CoinRecord.confirmed_index), typeof(long));
        dt.Columns.Add(nameof(CoinRecord.spent_index), typeof(long));
        dt.Columns.Add(nameof(CoinRecord.coinbase), typeof(bool));
        dt.Columns.Add(nameof(CoinRecord.puzzle_hash), typeof(byte[]));
        dt.Columns.Add(nameof(CoinRecord.coin_parent), typeof(byte[]));
        dt.Columns.Add(nameof(CoinRecord.amount), typeof(long));
        dt.Columns.Add(nameof(CoinRecord.timestamp), typeof(long));

        foreach (var r in records)
        {
            var amount = (long)r.amount;
            if (r.amount > long.MaxValue)
            {
                var buff = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(buff, r.amount);
                amount = BinaryPrimitives.ReadInt64BigEndian(buff);
            }

            dt.Rows.Add(r.id, r.coin_name, r.confirmed_index, r.spent_index, r.coinbase, r.puzzle_hash, r.coin_parent, amount, r.timestamp);
        }
        return dt;
    }

    private DataTable ConvertHintRecordsToTable(IEnumerable<HintRecord> records)
    {
        var dt = new DataTable();
        dt.Columns.Add(nameof(HintRecord.id), typeof(long));
        dt.Columns.Add(nameof(HintRecord.coin_name), typeof(byte[]));
        dt.Columns.Add(nameof(HintRecord.hint), typeof(byte[]));

        foreach (var r in records)
        {
            dt.Rows.Add(r.id, r.coin_name, r.hint);
        }
        return dt;
    }

    private DataTable ConvertSpentRecordsToTable(IEnumerable<CoinSpentRecord> records)
    {
        var dt = new DataTable();
        dt.Columns.Add(nameof(CoinSpentRecord.coin_name), typeof(byte[]));
        dt.Columns.Add(nameof(CoinSpentRecord.spent_index), typeof(long));

        foreach (var r in records)
        {
            dt.Rows.Add(r.coin_name, r.spent_index);
        }
        return dt;
    }
}