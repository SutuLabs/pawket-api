namespace NodeDBSyncer;

using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;

public class MssqlTargetConnection : ITargetConnection
{
    public const string CoinRecordTableName = "[dbo].[sync_coin_record]";

    private readonly SqlConnection connection;
    private bool disposedValue;

    public MssqlTargetConnection(string connString)
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