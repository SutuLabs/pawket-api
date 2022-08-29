namespace NodeDBSyncer.Helpers;

using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Npgsql;
using static NodeDBSyncer.Helpers.DbReference;

public abstract class PgsqlConnection : IDisposable
{
    protected readonly NpgsqlConnection connection;
    protected readonly string connString;
    private bool disposedValue;

    public PgsqlConnection(string connString)
    {
        connection = new NpgsqlConnection(connString);
        this.connString = connString;
    }

    public virtual Task Open()
    {
        connection.Open();
        return Task.CompletedTask;
    }

    protected async Task<long> GetMaxId(string tableName, string columnName = "id")
    {
        await this.connection.EnsureOpen();
        using var cmd = new NpgsqlCommand(@$"select max({columnName}) from {tableName};", connection);
        var o = await cmd.ExecuteScalarAsync();
        return o is DBNull ? 0
            : o is long lo ? lo
            : 0;
    }

    protected async Task<long> GetSyncState(string stateName)
    {
        await this.connection.EnsureOpen();
        using var cmd = new NpgsqlCommand(@$"select {stateName} from {SyncStateTableName} where id=1;", connection);
        var o = await cmd.ExecuteScalarAsync();
        return o is DBNull ? 0
            : o is long lo ? lo
            : 0;
    }

    protected async Task WriteSyncState(string stateName, object value)
    {
        await this.connection.EnsureOpen();
        using var cmd = new NpgsqlCommand(@$"UPDATE {SyncStateTableName} SET {stateName}=(@{stateName}) WHERE id=1;", connection)
        {
            Parameters = { new(stateName, value), }
        };
        await cmd.ExecuteNonQueryAsync();
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