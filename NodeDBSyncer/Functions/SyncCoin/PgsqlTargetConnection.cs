namespace NodeDBSyncer.Functions.SyncCoin;

using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using NodeDBSyncer.Helpers;
using Npgsql;
using static NodeDBSyncer.Helpers.DbReference;

public class PgsqlTargetConnection : PgsqlConnection
{
    public PgsqlTargetConnection(string connString)
        :base(connString)
    {
    }

    public override async Task Open()
    {
        await base.Open();
        if (!await this.CheckTableExistence())
        {
            await this.InitializeDatabase();
        }
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

    public async Task<long> GetLastSyncSpentHeight() => await GetSyncState("spent_index");

    public async Task<long> GetLastCoinSyncHeight() => await GetSyncState("coin_index");

    public async Task WriteLastSyncSpentHeight(long height) => await WriteSyncState("spent_index", height);

    public async Task WriteLastCoinSyncHeight(long height) => await WriteSyncState("coin_index", height);

    public async Task WriteCoinRecords(DataTable dataTable)
    {
        await this.connection.Import(dataTable, CoinRecordTableName);
    }

    public async Task WriteHintRecords(DataTable dataTable)
    {
        await this.connection.Import(dataTable, HintRecordTableName);
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
}