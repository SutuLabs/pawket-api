namespace NodeDBSyncer;

using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using NodeDBSyncer.Helpers;
using Npgsql;

public class PgsqlTargetConnection : ITargetConnection
{
    public const string CoinRecordTableName = "sync_coin_record";
    public const string HintRecordTableName = "sync_hint_record";
    public const string SpentRecordTableName = "sync_coin_spent";
    public const string SyncStateTableName = "sync_state";
    public const string FullBlockTableName = "sync_block";
    public const string CoinClassTableName = "sync_coin_class";
    public const string FullBlockSyncStateField = "block_index";

    private readonly NpgsqlConnection connection;
    private readonly string connString;
    private bool disposedValue;

    public PgsqlTargetConnection(string connString)
    {
        connection = new NpgsqlConnection(connString);
        this.connString = connString;
    }

    public async Task Open()
    {
        connection.Open();
        if (!await this.CheckTableExistence())
        {
            await this.InitializeDatabase();
        }
        //if (!await this.CheckTableExistenceV2())
        //{
        //    await this.UpgradeDatabaseV2();
        //}
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

    public async Task<CoinRemovalIndex[]> GetCoinRemovalIndex(long spent_index)
    {
        // for parallel
        using var tconn = new NpgsqlConnection(connString);
        tconn.Open();
        await using var cmd = new NpgsqlCommand($"SELECT coin_name FROM {CoinRecordTableName} WHERE spent_index=@spent_index", tconn)
        {
            Parameters = { new("spent_index", spent_index), }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var list = new List<CoinRemovalIndex>();
        while (await reader.ReadAsync())
        {
            var coin_name = reader.GetFieldValue<byte[]>(0);
            list.Add(new CoinRemovalIndex(coin_name, spent_index));
        }

        return list.ToArray();
    }

    public async Task<long> GetLastSyncSpentHeight() => await GetSyncState("spent_index");

    public async Task<long> GetLastCoinSyncHeight() => await GetSyncState("coin_index");

    public async Task<long> GetLastBlockSyncHeight() => await GetSyncState(FullBlockSyncStateField);

    public async Task WriteLastSyncSpentHeight(long height) => await WriteSyncState("spent_index", height);

    public async Task WriteLastCoinSyncHeight(long height) => await WriteSyncState("coin_index", height);

    public async Task WriteLastBlockSyncHeight(long height) => await WriteSyncState(FullBlockSyncStateField, height);

    private async Task<long> GetSyncState(string stateName)
    {
        using var cmd = new NpgsqlCommand(@$"select {stateName} from {SyncStateTableName} where id=1;", connection);
        var o = await cmd.ExecuteScalarAsync();
        return o is DBNull ? 0
            : o is long lo ? lo
            : 0;
    }

    private async Task WriteSyncState(string stateName, object value)
    {
        using var cmd = new NpgsqlCommand(@$"UPDATE {SyncStateTableName} SET {stateName}=(@{stateName}) WHERE id=1;", connection)
        {
            Parameters = { new(stateName, value), }
        };
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task WriteCoinRecords(DataTable dataTable)
    {
        await this.connection.Import(dataTable, CoinRecordTableName);
    }

    public async Task WriteHintRecords(DataTable dataTable)
    {
        await this.connection.Import(dataTable, HintRecordTableName);
    }

    public async Task WriteCoinClassRecords(DataTable dataTable)
    {
        await this.connection.Import(dataTable, CoinClassTableName);
    }

    public async Task WriteBlockRecords(DataTable dataTable)
    {
        await this.connection.Import(dataTable, FullBlockTableName);
    }

    [Obsolete]
    public async Task WriteSpentHeight(DataTable dataTable)
    {
        await this.connection.Import(dataTable, SpentRecordTableName);
    }

    public async Task<int> WriteSpentHeight(CoinSpentRecord[] changes)
    {
        var tmpTable = "_tmp_import_spent_height_table";

        using var cmd = new NpgsqlCommand(@$"CREATE TEMPORARY TABLE {tmpTable}(coin_name bytea NOT NULL, spent_index bigint NOT NULL, PRIMARY KEY (coin_name));"
        //using var cmd = new NpgsqlCommand(@$"CREATE TABLE IF NOT EXISTS {tmpTable}(coin_name bytea NOT NULL, spent_index bigint NOT NULL, PRIMARY KEY (coin_name));"
            + $"CREATE INDEX IF NOT EXISTS idx_tmp_coin_name ON {tmpTable} USING btree (coin_name ASC NULLS LAST);"
            + $"CREATE INDEX IF NOT EXISTS idx_tmp_spent_height ON {tmpTable} USING btree (spent_index ASC NULLS LAST);", connection);
        await cmd.ExecuteNonQueryAsync();

        var dataTable = changes.ConvertToDataTable();
        await this.connection.Import(dataTable, tmpTable);

        using var cmd2 = new NpgsqlCommand($"UPDATE {CoinRecordTableName} SET spent_index = t.spent_index" +
            $" FROM {tmpTable} as t WHERE t.coin_name = {CoinRecordTableName}.coin_name AND t.spent_index <> {CoinRecordTableName}.spent_index;" +
            //$"TRUNCATE TABLE {tmpTable};",
            $"DROP TABLE {tmpTable};",
            connection);
        return await cmd2.ExecuteNonQueryAsync();
    }

    public async Task<long> GetPeakSpentHeight()
    {
        using var cmd = new NpgsqlCommand(@$"select max(spent_index) from {CoinRecordTableName};", connection);
        var o = await cmd.ExecuteScalarAsync();
        return o is DBNull ? 0
            : o is long lo ? lo
            : 0;
    }

    private async Task<bool> CheckTableExistence() => await this.connection.CheckExistence(CoinRecordTableName);
    private async Task<bool> CheckTableExistenceV2() => await this.connection.CheckExistence(FullBlockTableName);
    internal async Task<bool> CheckIndexExistence() => await this.connection.CheckExistence($"idx_{HintRecordTableName}_coin");

    private async Task InitializeDatabase()
    {
        using var cmd = new NpgsqlCommand(@$"
CREATE TABLE public.{CoinRecordTableName}
(
    id bigint NOT NULL,
    coin_name bytea NOT NULL,
    confirmed_index bigint NOT NULL,
    spent_index bigint NOT NULL,
    coinbase boolean NOT NULL,
    puzzle_hash bytea NOT NULL,
    coin_parent bytea NOT NULL,
    amount bigint NOT NULL,
    timestamp bigint NOT NULL,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{CoinRecordTableName}
    OWNER to postgres;

CREATE TABLE public.{HintRecordTableName}
(
    id bigint NOT NULL,
    coin_name bytea NOT NULL,
    hint bytea NOT NULL,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{HintRecordTableName}
    OWNER to postgres;

CREATE TABLE public.{SyncStateTableName}
(
    id bigint NOT NULL,
    --coin_index bigint NOT NULL,
    spent_index bigint,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{SyncStateTableName}
    OWNER to postgres;

INSERT INTO public.{SyncStateTableName} (id, spent_index) VALUES (1, 0);
--INSERT INTO public.{SyncStateTableName} (id, coin_index, spent_index) VALUES (1, 0, NULL);
", connection);

        //CREATE TABLE public.{SpentRecordTableName}
        //(
        //    id serial PRIMARY KEY,
        //    coin_name bytea NOT NULL,
        //    spent_index bigint NOT NULL
        //);

        //ALTER TABLE IF EXISTS public.{SpentRecordTableName}
        //    OWNER to postgres;
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException pex)
        {
            Console.WriteLine($"Failed to execute table creation script due to [{pex.Message}], you may want to execute it yourself, here it is:");
            Console.WriteLine(cmd.CommandText);
        }
    }

    private async Task UpgradeDatabaseV2()
    {
        using var cmd = new NpgsqlCommand(@$"
CREATE TABLE public.{FullBlockTableName}
(
    id serial NOT NULL,
    is_tx_block boolean NOT NULL,
    index bigint NOT NULL UNIQUE,
    weight bigint NOT NULL,
    iterations bigint NOT NULL,
    cost bigint NOT NULL,
    fee bigint NOT NULL,
    generator bytea NOT NULL,
    generator_ref_list bytea NOT NULL,
    block_info json NOT NULL,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{FullBlockTableName}
    OWNER to postgres;

CREATE TABLE public.{CoinClassTableName}
(
    id serial NOT NULL,
    coin_name bytea NOT NULL UNIQUE,
    puzzle json NOT NULL,
    solution bytea NOT NULL,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{CoinClassTableName}
    OWNER to postgres;

ALTER TABLE public.{SyncStateTableName}
    ADD COLUMN IF NOT EXISTS {FullBlockSyncStateField} bigint;

UPDATE public.{SyncStateTableName} SET {FullBlockSyncStateField}=0;
", connection);

        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException pex)
        {
            Console.WriteLine($"Failed to upgrade v2 script due to [{pex.Message}], you may want to execute it yourself, here it is:");
            Console.WriteLine(cmd.CommandText);
        }
    }

    internal async Task InitializeIndex()
    {
        using var cmd = new NpgsqlCommand(@$"
CREATE INDEX IF NOT EXISTS idx_{CoinRecordTableName}_confirmed_index
    ON public.{CoinRecordTableName} USING btree
    (confirmed_index DESC NULLS LAST);

CREATE INDEX IF NOT EXISTS idx_{CoinRecordTableName}_spent_index
    ON public.{CoinRecordTableName} USING btree
    (spent_index DESC NULLS LAST);

CREATE INDEX IF NOT EXISTS idx_{CoinRecordTableName}_coin_parent
    ON public.{CoinRecordTableName} USING btree
    (coin_parent ASC NULLS LAST);

CREATE INDEX IF NOT EXISTS idx_{CoinRecordTableName}_puzzle_hash
    ON public.{CoinRecordTableName} USING btree
    (puzzle_hash ASC NULLS LAST)
	INCLUDE(amount, spent_index);

CREATE INDEX IF NOT EXISTS idx_{CoinRecordTableName}_coin_name
    ON public.{CoinRecordTableName} USING btree
    (coin_name ASC NULLS LAST);

CREATE INDEX IF NOT EXISTS idx_{HintRecordTableName}_coin
    ON public.{HintRecordTableName} USING btree
    (hint ASC NULLS LAST);
", connection);
        try
        {
            cmd.CommandTimeout = 3600;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException pex)
        {
            Console.WriteLine($"Failed to execute table creation script due to [{pex.Message}], you may want to execute it yourself, here it is:");
            Console.WriteLine(cmd.CommandText);
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