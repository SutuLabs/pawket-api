namespace NodeDBSyncer;

using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Npgsql;

public class PgsqlTargetConnection : ITargetConnection
{
    public const string CoinRecordTableName = "sync_coin_record";
    public const string HintRecordTableName = "sync_hint_record";

    private readonly NpgsqlConnection connection;
    private bool disposedValue;

    public PgsqlTargetConnection(string connString)
    {
        connection = new NpgsqlConnection(connString);
    }

    public void Open()
    {
        connection.Open();
    }

    public async Task<long> GetTotalCoinRecords()
    {
        using var cmd = new NpgsqlCommand(@$"select max(id) from {CoinRecordTableName};", connection);
        var o = await cmd.ExecuteScalarAsync();
        return o is DBNull ? 0
            : o is long lo ? lo
            : 0;
    }

    public async Task<long> GetTotalHintRecords()
    {
        using var cmd = new NpgsqlCommand(@$"select max(id) from {HintRecordTableName};", connection);
        var o = await cmd.ExecuteScalarAsync();
        return o is DBNull ? 0
            : o is long lo ? lo
            : 0;
    }

    public async Task WriteCoinRecords(DataTable dataTable)
    {
        var fields = string.Join(",", dataTable.Columns.OfType<DataColumn>().Select(_ => _.ColumnName));
        using var writer = connection.BeginBinaryImport($"COPY {CoinRecordTableName} ({fields}) FROM STDIN (FORMAT BINARY)");

        foreach (DataRow row in dataTable.Rows)
        {
            writer.WriteRow(row.ItemArray);
        }

        writer.Complete();
    }

    public async Task WriteHintRecords(DataTable dataTable)
    {
        var fields = string.Join(",", dataTable.Columns.OfType<DataColumn>().Select(_ => _.ColumnName));
        using var writer = connection.BeginBinaryImport($"COPY {HintRecordTableName} ({fields}) FROM STDIN (FORMAT BINARY)");

        foreach (DataRow row in dataTable.Rows)
        {
            writer.WriteRow(row.ItemArray);
        }

        writer.Complete();
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