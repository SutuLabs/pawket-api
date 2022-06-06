namespace NodeDBSyncer;

using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Npgsql;

public class PgsqlTargetConnection : ITargetConnection
{
    public const string CoinRecordTableName = "sync_coin_record";
    public const string HintRecordTableName = "sync_hint_record";
    public const string SyncStateTableName = "sync_state";

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

    public async Task<long> GetLastSyncSpentHeight()
    {
        using var cmd = new NpgsqlCommand(@$"select spent_height from {SyncStateTableName} where id=1;", connection);
        var o = await cmd.ExecuteScalarAsync();
        return o is DBNull ? 0
            : o is long lo ? lo
            : 0;
    }

    public async Task WriteLastSyncSpentHeight(long height)
    {
        using var cmd = new NpgsqlCommand(@$"UPDATE {SyncStateTableName} SET spent_height=(@spent_height) WHERE id=1;", connection)
        {
            Parameters = { new("spent_height", height), }
        };
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task WriteCoinRecords(DataTable dataTable)
    {
        await Import(dataTable, CoinRecordTableName);
    }

    public async Task WriteHintRecords(DataTable dataTable)
    {
        await Import(dataTable, HintRecordTableName);
    }

    public async Task<int> WriteSpentHeight(SpentHeightChange[] changes)
    {
        var tmpTable = "_tmp_import_spent_height_table";

        using var cmd = new NpgsqlCommand(@$"CREATE TEMPORARY TABLE {tmpTable}(id bigint NOT NULL, spent_height bigint NOT NULL, PRIMARY KEY (id));"
        //using var cmd = new NpgsqlCommand(@$"CREATE TABLE IF NOT EXISTS {tmpTable}(id bigint NOT NULL, spent_height bigint NOT NULL, PRIMARY KEY (id));"
            + $"CREATE INDEX IF NOT EXISTS idx_id ON {tmpTable} USING btree (id ASC NULLS LAST);"
            + $"CREATE INDEX IF NOT EXISTS idx_spent_height ON {tmpTable} USING btree (spent_height ASC NULLS LAST);", connection);
        await cmd.ExecuteNonQueryAsync();

        var dataTable = ConvertToDataTable(changes);
        await Import(dataTable, tmpTable);

        using var cmd2 = new NpgsqlCommand($"UPDATE {CoinRecordTableName} SET spent_index = t.spent_height FROM {tmpTable} as t WHERE t.id = {CoinRecordTableName}.id AND t.spent_height <> {CoinRecordTableName}.spent_index;" +
            //$"TRUNCATE TABLE {tmpTable};",
            $"DROP TABLE {tmpTable};",
            connection);
        return await cmd2.ExecuteNonQueryAsync();
    }

    private async Task Import(DataTable dataTable, string tableName)
    {
        var fields = string.Join(",", dataTable.Columns.OfType<DataColumn>().Select(_ => _.ColumnName));
        using var writer = connection.BeginBinaryImport($"COPY {tableName} ({fields}) FROM STDIN (FORMAT BINARY)");

        foreach (DataRow row in dataTable.Rows)
        {
            writer.WriteRow(row.ItemArray);
        }

        writer.Complete();
    }

    private static DataTable ConvertToDataTable<T>(IEnumerable<T> data)
    {
        var properties = TypeDescriptor.GetProperties(typeof(T));
        DataTable table = new DataTable();
        foreach (PropertyDescriptor prop in properties)
            table.Columns.Add(prop.Name, Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType);
        foreach (T item in data)
        {
            DataRow row = table.NewRow();
            foreach (PropertyDescriptor prop in properties)
                row[prop.Name] = prop.GetValue(item) ?? DBNull.Value;
            table.Rows.Add(row);
        }
        return table;
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