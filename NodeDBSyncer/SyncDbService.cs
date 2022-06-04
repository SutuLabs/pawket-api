namespace NodeDBSyncer;

using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
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

        //await target.SetIdentityInsert(TargetConnection.CoinRecordTableName, true);

        var batch = 10000;
        var max = Math.Min(sourceCount - targetCount, 1000000) / batch;
        for (int i = 0; i < max; i++)
        {
            //using var reader = await source.GetCoinRecords(targetCount + i * batch, batch);
            var sw = new Stopwatch();
            sw.Start();
            var records = source.GetCoinRecords(targetCount + i * batch, batch);
            var aa = sw.ElapsedMilliseconds;
            sw.Restart();
            await target.WriteCoinRecords(records);
            sw.Stop();
            this.logger.LogInformation($"batch processed [{targetCount + i * batch}]~[{targetCount + i * batch + batch}], {aa} ms, {sw.ElapsedMilliseconds} ms");
        }

        //SqlCommand command = new SqlCommand(queryString, connection);
        //command.ExecuteNonQuery();
        //await target.SetIdentityInsert(TargetConnection.CoinRecordTableName, false);
    }
}

public class SouceConnection : IDisposable
{
    private readonly SqliteConnection connection;
    private bool disposedValue;

    public const string CoinRecordTableName = "coin_record";

    public SouceConnection(string connString)
    {
        connection = new SqliteConnection(connString);
    }

    public void Open()
    {
        connection.Open();
    }

    public async Task<long> GetTotalCoinRecords()
    {
        var command = connection.CreateCommand();
        command.CommandText = @$"select max(rowid) from {CoinRecordTableName};";
        var num = await command.ExecuteScalarAsync() as long?;
        return num == null ? 0
            : num.Value;
    }

    public DataTable GetCoinRecords(long start, int number)
    {
        var command = connection.CreateCommand();
        command.CommandText =
        @$"
SELECT coin_name,
       confirmed_index,
       spent_index,
       coinbase,
       puzzle_hash,
       coin_parent,
       amount,
       timestamp,
       rowid
FROM {CoinRecordTableName}
WHERE rowid>$start and rowid<=$end;";
        command.Parameters.AddWithValue("$start", start);
        var end = start + number;
        command.Parameters.AddWithValue("$end", end);

        //using var reader =  await command.ExecuteReaderAsync();
        //var dt = new DataTable();
        //dt.Load(reader, LoadOption.Upsert);
        //return dt;


        using var reader = command.ExecuteReader();
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

        while (reader.Read())
        {
            var coin_name = reader.GetFieldValue<byte[]>(0);
            var confirmed_index = reader.GetFieldValue<long>(1);
            var spent_index = reader.GetFieldValue<long>(2);
            var coinbase = reader.GetFieldValue<bool>(3);
            var puzzle_hash = reader.GetFieldValue<byte[]>(4);
            var coin_parent = reader.GetFieldValue<byte[]>(5);
            var amount_raw = reader.GetFieldValue<byte[]>(6);
            var amount = BinaryPrimitives.ReadInt64BigEndian(amount_raw);
            var timestamp = reader.GetFieldValue<long>(7);
            var id = reader.GetFieldValue<long>(8);

            dt.Rows.Add(id, coin_name, confirmed_index, spent_index, coinbase, puzzle_hash, coin_parent, amount, timestamp);
            //new CoinRecord(id, coin_name, confirmed_index, spent_index, coinbase, puzzle_hash, coin_parent, amount, timestamp);
        }
        return dt;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
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

public class TargetConnection : IDisposable
{
    public const string CoinRecordTableName = "[dbo].[sync_coin_record]";

    private readonly SqlConnection connection;
    private bool disposedValue;

    public TargetConnection(string connString)
    {
        connection = new SqlConnection(connString);
    }

    public void Open()
    {
        connection.Open();
    }

    public async Task<long> GetTotalCoinRecords()
    {
        var command = connection.CreateCommand();
        command.CommandText = @$"select max(id) from {CoinRecordTableName};";
        var o = await command.ExecuteScalarAsync();
        return o is DBNull ? 0
            : o is long lo ? lo
            : 0;
    }

    public async Task SetIdentityInsert(string table, bool on)
    {
        var prop = on ? "ON" : "OFF";
        var command = connection.CreateCommand();
        command.CommandText = $"SET IDENTITY_INSERT {table} {prop}";
        await command.ExecuteNonQueryAsync();
    }

    public async Task WriteCoinRecords(DataTable reader)
    {
        using var tran = connection.BeginTransaction();
        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.KeepIdentity, tran);
        bulkCopy.SqlRowsCopied += this.BulkCopy_SqlRowsCopied;
        bulkCopy.BatchSize = 1000;
        bulkCopy.DestinationTableName = CoinRecordTableName;
        foreach (DataColumn col in reader.Columns)
        {
            bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(reader);
        await tran.CommitAsync();
    }

    private void BulkCopy_SqlRowsCopied(object sender, SqlRowsCopiedEventArgs e)
    {
        Console.WriteLine("Copied: " + e.RowsCopied);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
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

public record CoinRecord(
    long id,
    byte[] coin_name,
    long confirmed_index,
    long spent_index,
    bool coinbase,
    byte[] puzzle_hash,
    byte[] coin_parent,
    ulong amount,
    long timestamp);

/*
CREATE TABLE [dbo].[sync_coin_record](
	[id] [bigint] IDENTITY(1,1) NOT NULL,
	[coin_name] [binary](32) NOT NULL,
	[confirmed_index] [bigint] NOT NULL,
	[spent_index] [bigint] NOT NULL,
	[coinbase] [bit] NOT NULL,
	[puzzle_hash] [binary](32) NOT NULL,
	[coin_parent] [binary](32) NOT NULL,
	[amount] [bigint] NOT NULL,
	[timestamp] [bigint] NOT NULL
) ON [PRIMARY]
 */

public record HintRecord(
    long id,
    byte[] coin_id,
    byte[] hint);

/*
CREATE TABLE [dbo].[sync_hints](
	[id] [bigint] IDENTITY(1,1) NOT NULL,
	[coin_id] [binary](32) NOT NULL,
	[hint] [binary](32) NOT NULL
) ON [PRIMARY]
 */