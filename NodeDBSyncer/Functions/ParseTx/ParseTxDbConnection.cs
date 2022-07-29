namespace NodeDBSyncer.Functions.ParseTx;

using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using NodeDBSyncer.Helpers;
using Npgsql;
using static NodeDBSyncer.Helpers.DbReference;

public class ParseTxDbConnection : PgsqlConnection
{

    public ParseTxDbConnection(string connString)
        :base(connString)
    {
    }

    public override async Task Open()
    {
        await base.Open();
        if (!await this.CheckTableExistenceV2())
        {
            await this.UpgradeDatabaseV2();
        }
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


    public async Task<long> GetLastBlockSyncHeight() => await GetSyncState(FullBlockSyncStateField);

    public async Task WriteLastBlockSyncHeight(long height) => await WriteSyncState(FullBlockSyncStateField, height);

    public async Task WriteCoinClassRecords(DataTable dataTable)
    {
        await this.connection.Import(dataTable, CoinClassTableName);
    }

    public async Task WriteBlockRecords(DataTable dataTable)
    {
        await this.connection.Import(dataTable, FullBlockTableName);
    }

    private async Task<bool> CheckTableExistenceV2() => await this.connection.CheckExistence(FullBlockTableName);


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
}