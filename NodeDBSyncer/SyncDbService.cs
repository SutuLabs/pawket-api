namespace NodeDBSyncer;

using System.Buffers.Binary;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal class SyncDbService : BaseRefreshService
{
    private readonly AppSettings appSettings;

    public SyncDbService(
        ILogger<SyncDbService> logger,
        IOptions<AppSettings> appSettings)
        : base(logger, nameof(SyncDbService), 2, 15, 30000)
    {
        this.appSettings = appSettings.Value;
    }

    protected override async Task DoWorkAsync(CancellationToken token)
    {
        using var source = new SouceConnection(this.appSettings.LocalSqliteConnString);
        source.Open();
        using var target = new TargetConnection(this.appSettings.OnlineDbConnString);
        target.Open();

        var sourceCount = await source.GetTotalCoinRecords();
        var targetCount = await target.GetTotalCoinRecords();

        var batch = 10000;
        var max = Math.Min(sourceCount - targetCount, 1000000) / batch;
        for (int i = 0; i < max; i++)
        {
            var sw = new Stopwatch();
            sw.Start();
            var records = source.GetCoinRecords(targetCount + i * batch, batch);
            var dt = ConvertRecordsToTable(records);
            var tget = sw.ElapsedMilliseconds;
            sw.Restart();
            await target.WriteCoinRecords(dt);
            sw.Stop();
            this.logger.LogInformation($"batch processed [{targetCount + i * batch}]~[{targetCount + i * batch + batch}], {tget} ms, {sw.ElapsedMilliseconds} ms");
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
}