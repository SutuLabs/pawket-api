namespace NodeDBSyncer.Functions.SyncCoin;

using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

public class SouceConnection : IDisposable
{
    private readonly SqliteConnection connection;
    private bool disposedValue;

    public const string CoinRecordTableName = "coin_record";
    public const string HintRecordTableName = "hints";
    public const string BlockRecordTableName = "full_blocks";

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

    public async Task<long> GetTotalHintRecords()
    {
        var command = connection.CreateCommand();
        command.CommandText = @$"select max(rowid) from {HintRecordTableName};";
        var num = await command.ExecuteScalarAsync() as long?;
        return num == null ? 0
            : num.Value;
    }

    public async Task<long> GetPeakSpentHeight()
    {
        var command = connection.CreateCommand();
        command.CommandText = @$"select max(spent_index) from {CoinRecordTableName};";
        var num = await command.ExecuteScalarAsync() as long?;
        return num == null ? 0
            : num.Value;
    }

    public IEnumerable<CoinRecord> GetCoinRecords(long start, int number)
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

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var coin_name = reader.GetFieldValue<byte[]>(0);
            var confirmed_index = reader.GetFieldValue<long>(1);
            var spent_index = reader.GetFieldValue<long>(2);
            var coinbase = reader.GetFieldValue<bool>(3);
            var puzzle_hash = reader.GetFieldValue<byte[]>(4);
            var coin_parent = reader.GetFieldValue<byte[]>(5);
            var amount_raw = reader.GetFieldValue<byte[]>(6);
            var amount = BinaryPrimitives.ReadUInt64BigEndian(amount_raw);
            var timestamp = reader.GetFieldValue<long>(7);
            var id = reader.GetFieldValue<long>(8);

            yield return new CoinRecord(id, coin_name, confirmed_index, spent_index, coinbase, puzzle_hash, coin_parent, amount, timestamp);
        }
    }

    public IEnumerable<HintRecord> GetHintRecords(long start, int number)
    {
        var command = connection.CreateCommand();
        command.CommandText =
        @$"
SELECT coin_id,
       hint,
       rowid
FROM {HintRecordTableName}
WHERE rowid>$start and rowid<=$end;";
        command.Parameters.AddWithValue("$start", start);
        var end = start + number;
        command.Parameters.AddWithValue("$end", end);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var coin_name = reader.GetFieldValue<byte[]>(0);
            var hint = reader.GetFieldValue<byte[]>(1);
            var id = reader.GetFieldValue<long>(2);

            yield return new HintRecord(id, coin_name, hint);
        }
    }

    public IEnumerable<CoinSpentRecord> GetSpentHeightChange(long start, int number)
    {
        var command = connection.CreateCommand();
        command.CommandText =
        @$"
SELECT coin_name,
       spent_index
FROM {CoinRecordTableName}
WHERE spent_index >= $start
LIMIT $number;";
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$number", number);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var coin_name = reader.GetFieldValue<byte[]>(0);
            var spent_index = reader.GetFieldValue<long>(1);

            yield return new CoinSpentRecord(coin_name, spent_index);
        }
    }

    public IEnumerable<FullBlockRecord> GetBlockRecords(long start, int number)
    {
        var command = connection.CreateCommand();
        command.CommandText =
        @$"
SELECT header_hash, prev_hash, height, sub_epoch_summary, is_fully_compactified, block, block_record
FROM {BlockRecordTableName}
WHERE in_main_chain=true and height>$start and height<=$end;";
        command.Parameters.AddWithValue("$start", start);
        var end = start + number;
        command.Parameters.AddWithValue("$end", end);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var header_hash = reader.GetFieldValue<byte[]>(0);
            var prev_hash = reader.GetFieldValue<byte[]>(1);
            var height = reader.GetFieldValue<long>(2);
            var sub_epoch_summary = reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3);
            var is_fully_compactified = reader.GetFieldValue<bool>(4);
            var block = reader.GetFieldValue<byte[]>(5);
            var block_record = reader.GetFieldValue<byte[]>(6);

            yield return new FullBlockRecord(header_hash, prev_hash, height, sub_epoch_summary, is_fully_compactified, block, block_record);
        }
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