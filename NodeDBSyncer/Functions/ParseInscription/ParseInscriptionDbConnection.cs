namespace NodeDBSyncer.Functions.ParseInscription;

using System.Data;
using System.Threading.Tasks;
using NodeDBSyncer.Helpers;
using Npgsql;
using WalletServer.Helpers;
using static NodeDBSyncer.Helpers.DbReference;

public class ParseInscriptionDbConnection : PgsqlConnection
{
    private const string ProcessedKey = "inscription_latest_block_synced";
    public ParseInscriptionDbConnection(string connString)
        : base(connString)
    {
    }

    public override async Task Open()
    {
        await base.Open();
        if (!await this.CheckTableExistence())
        {
            await this.UpgradeDatabase();
        }
    }

    public async Task<InscriptionRecordRaw[]> GetRawInscriptionRecords(long block_index, int number)
    {
        await this.connection.EnsureOpen();
        var sql = $@"
SELECT cc.id, cr.spent_index, cc.analysis

FROM sync_coin_class cc
JOIN sync_coin_record cr on cr.coin_name = cc.coin_name
WHERE cc.mods = 'p2_delegated_puzzle_or_hidden_puzzle()'
AND cc.analysis is not null
AND cr.spent_index > @start
AND cr.spent_index <= @end

ORDER BY cr.spent_index, cc.id";
        await using var cmd = new NpgsqlCommand(sql, this.connection)
        {
            CommandTimeout = 600,
            Parameters =
            {
                new("start", block_index),
                new("end", block_index+ number),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var list = await ReadInscriptionRecords(reader);
        return list;

        static async Task<InscriptionRecordRaw[]> ReadInscriptionRecords(NpgsqlDataReader reader)
        {
            var list = new List<InscriptionRecordRaw>();
            while (await reader.ReadAsync())
            {
                var lccid = reader.GetFieldValue<long>(0);
                var index = reader.GetFieldValue<long>(1);
                var analysis = reader.GetNullableFieldValue<string>(2);
                list.Add(new InscriptionRecordRaw(
                    lccid,
                    index,
                    0,
                    analysis));
            }

            return list.ToArray();
        }
    }

    public async Task<int> UpdateInscriptionRecords(InscriptionRecordInfo[] changes)
    {
        await this.connection.EnsureOpen();
        var tmpTable = "_tmp_import_inscription_record_update_table";

        // TODO: deduplicate this table creation script
        using var cmd = new NpgsqlCommand(
            $@"CREATE TEMPORARY TABLE {tmpTable}(
    id serial NOT NULL,
    ""{nameof(InscriptionRecordInfo.coin_class_id)}"" bigint NOT NULL,
    ""{nameof(InscriptionRecordInfo.spent_index)}"" bigint NOT NULL,
    ""{nameof(InscriptionRecordInfo.serial)}"" int NOT NULL,
    ""{nameof(InscriptionRecordInfo.coin_index)}"" int NOT NULL,
    ""{nameof(InscriptionRecordInfo.coin_name)}"" bytea NOT NULL,
    ""{nameof(InscriptionRecordInfo.parent)}"" bytea NOT NULL,
    ""{nameof(InscriptionRecordInfo.from)}"" bytea NOT NULL,
    ""{nameof(InscriptionRecordInfo.to)}"" bytea NOT NULL,
    ""{nameof(InscriptionRecordInfo.p)}"" text NOT NULL,
    ""{nameof(InscriptionRecordInfo.op)}"" text NOT NULL,
    ""{nameof(InscriptionRecordInfo.tick)}"" text NOT NULL,
    ""{nameof(InscriptionRecordInfo.amt)}"" bigint,
    ""{nameof(InscriptionRecordInfo.from_balance)}"" bigint,
    ""{nameof(InscriptionRecordInfo.to_balance)}"" bigint,
    ""{nameof(InscriptionRecordInfo.meta)}"" json,
    ""{nameof(InscriptionRecordInfo.valid)}"" boolean,
    PRIMARY KEY (id)
);", connection);
        await cmd.ExecuteNonQueryAsync();

        var dataTable = ConvertRecordsToTable(changes);
        await this.connection.Import(dataTable, tmpTable);

        var fields = typeof(InscriptionRecordInfo).GetFieldsNameFromType();
        using var cmd2 = new NpgsqlCommand($@"
INSERT INTO {InscriptionRecordTableName}({fields})
SELECT {fields} FROM {tmpTable}
ON CONFLICT DO NOTHING;
" +

            //ON CONFLICT({nameof(InscriptionRecordInfo.coin_name)})
            //DO UPDATE SET
            //    {nameof(InscriptionRecordInfo.coin_class_id)} = EXCLUDED.{nameof(InscriptionRecordInfo.coin_class_id)};
            $"DROP TABLE {tmpTable};",
            connection);
        cmd2.CommandTimeout = 300;
        return await cmd2.ExecuteNonQueryAsync();
    }

    public async Task<int> UpdateInscriptionTicks(TickInfo[] changes)
    {
        await this.connection.EnsureOpen();
        var tmpTable = "_tmp_import_inscription_tick_update_table";

        // TODO: deduplicate this table creation script
        using var cmd = new NpgsqlCommand(
            $@"CREATE TEMPORARY TABLE {tmpTable}(
    id serial NOT NULL,
    ""{nameof(TickInfo.coin_name)}"" bytea NOT NULL,
    ""{nameof(TickInfo.index)}"" bigint NOT NULL,
    ""{nameof(TickInfo.tick)}"" text NOT NULL,
    ""{nameof(TickInfo.lim)}"" bigint NOT NULL,
    ""{nameof(TickInfo.max)}"" bigint NOT NULL,
    ""{nameof(TickInfo.info)}"" json,
    PRIMARY KEY (id)
);", connection);
        await cmd.ExecuteNonQueryAsync();

        var dataTable = ConvertRecordsToTable(changes);
        await this.connection.Import(dataTable, tmpTable);

        var fields = typeof(TickInfo).GetFieldsNameFromType();
        using var cmd2 = new NpgsqlCommand($@"
INSERT INTO {InscriptionTickTableName}({fields})
SELECT {fields} FROM {tmpTable}
ON CONFLICT DO NOTHING;
" +
            $"DROP TABLE {tmpTable};",
            connection);
        cmd2.CommandTimeout = 300;
        return await cmd2.ExecuteNonQueryAsync();
    }

    public async Task<long> GetLatestBlockIndex()
        => await GetMaxId(FullBlockTableName, "index");

    public async Task<long> GetInscriptionRecordProcessedBlockIndex()
        => await GetSyncState(ProcessedKey);

    public async Task<long> GetSyncDbProcessedBlockIndex()
        => await GetSyncState("spent_index");

    public async Task<long> GetLatestProcessedBlockIndex()
    {
        await this.connection.EnsureOpen();
        using var cmd = new NpgsqlCommand(@$"SELECT min(index)
	FROM {FullBlockTableName}
	WHERE tx_parsed=false AND is_tx_block=true;", connection);
        var o = await cmd.ExecuteScalarAsync();

        if (o is DBNull)
        {
            using var cmd2 = new NpgsqlCommand(@$"SELECT max(index)
	FROM {FullBlockTableName}
	WHERE tx_parsed=true AND is_tx_block=true;", connection);
            var o2 = await cmd2.ExecuteScalarAsync();

            return o2 is DBNull ? 0
                : o2 is long lo ? lo
                : 0;
        }
        else
        {
            return o is long lo ? lo - 1 : 0;
        }


    }

    public async Task WriteInscriptionRecordProcessedBlockIndex(long block_index)
        => await WriteSyncState(ProcessedKey, block_index);

    internal async Task<bool> CheckIndexExistence()
        => await this.connection.CheckExistence($"idx_{InscriptionRecordTableName}_{nameof(InscriptionRecordInfo.to)}");

    private DataTable ConvertRecordsToTable(IEnumerable<InscriptionRecordInfo> records)
    {
        var dt = new DataTable();
        dt.Columns.Add(nameof(InscriptionRecordInfo.coin_class_id), typeof(long));
        dt.Columns.Add(nameof(InscriptionRecordInfo.spent_index), typeof(long));
        dt.Columns.Add(nameof(InscriptionRecordInfo.serial), typeof(int));
        dt.Columns.Add(nameof(InscriptionRecordInfo.coin_index), typeof(int));
        dt.Columns.Add(nameof(InscriptionRecordInfo.coin_name), typeof(byte[]));
        dt.Columns.Add(nameof(InscriptionRecordInfo.parent), typeof(byte[]));
        dt.Columns.Add(nameof(InscriptionRecordInfo.from), typeof(byte[]));
        dt.Columns.Add(nameof(InscriptionRecordInfo.to), typeof(byte[]));
        dt.Columns.Add(nameof(InscriptionRecordInfo.p), typeof(string));
        dt.Columns.Add(nameof(InscriptionRecordInfo.op), typeof(string));
        dt.Columns.Add(nameof(InscriptionRecordInfo.tick), typeof(string));
        dt.Columns.Add(nameof(InscriptionRecordInfo.amt), typeof(long));
        dt.Columns.Add(nameof(InscriptionRecordInfo.from_balance), typeof(long));
        dt.Columns.Add(nameof(InscriptionRecordInfo.to_balance), typeof(long));
        dt.Columns.Add(nameof(InscriptionRecordInfo.meta), typeof(string));
        dt.Columns.Add(nameof(InscriptionRecordInfo.valid), typeof(bool));

        foreach (var r in records)
        {
            dt.Rows.Add(
                r.coin_class_id,
                r.spent_index,
                r.serial,
                r.coin_index,
                r.coin_name.ToHexBytes(),
                r.parent.ToHexBytes(),
                r.from.ToHexBytes(),
                r.to.ToHexBytes(),
                r.p,
                r.op,
                r.tick,
                r.amt,
                r.from_balance,
                r.to_balance,
                r.meta,
                r.valid);
        }

        return dt;
    }

    private DataTable ConvertRecordsToTable(IEnumerable<TickInfo> records)
    {
        var dt = new DataTable();
        dt.Columns.Add(nameof(TickInfo.coin_name), typeof(byte[]));
        dt.Columns.Add(nameof(TickInfo.index), typeof(long));
        dt.Columns.Add(nameof(TickInfo.tick), typeof(string));
        dt.Columns.Add(nameof(TickInfo.max), typeof(long));
        dt.Columns.Add(nameof(TickInfo.lim), typeof(long));
        dt.Columns.Add(nameof(TickInfo.info), typeof(string));

        foreach (var r in records)
        {
            dt.Rows.Add(
                r.coin_name.ToHexBytes(),
                r.index,
                r.tick,
                r.max,
                r.lim,
                r.info);
        }

        return dt;
    }

    private async Task<bool> CheckTableExistence() => await this.connection.CheckExistence(InscriptionRecordTableName);

    private async Task UpgradeDatabase()
    {
        using var cmd = new NpgsqlCommand(@$"
ALTER TABLE public.{SyncStateTableName} ADD COLUMN IF NOT EXISTS {ProcessedKey} bigint;

CREATE TABLE public.{InscriptionRecordTableName}
(
    id serial NOT NULL,
    ""{nameof(InscriptionRecordInfo.coin_class_id)}"" bigint NOT NULL,
    ""{nameof(InscriptionRecordInfo.spent_index)}"" bigint NOT NULL,
    ""{nameof(InscriptionRecordInfo.serial)}"" int NOT NULL,
    ""{nameof(InscriptionRecordInfo.coin_index)}"" int NOT NULL,
    ""{nameof(InscriptionRecordInfo.coin_name)}"" bytea NOT NULL UNIQUE,
    ""{nameof(InscriptionRecordInfo.parent)}"" bytea NOT NULL,
    ""{nameof(InscriptionRecordInfo.from)}"" bytea NOT NULL,
    ""{nameof(InscriptionRecordInfo.to)}"" bytea NOT NULL,
    ""{nameof(InscriptionRecordInfo.p)}"" text NOT NULL,
    ""{nameof(InscriptionRecordInfo.op)}"" text NOT NULL,
    ""{nameof(InscriptionRecordInfo.tick)}"" text NOT NULL,
    ""{nameof(InscriptionRecordInfo.amt)}"" bigint,
    ""{nameof(InscriptionRecordInfo.from_balance)}"" bigint,
    ""{nameof(InscriptionRecordInfo.to_balance)}"" bigint,
    ""{nameof(InscriptionRecordInfo.meta)}"" json,
    ""{nameof(InscriptionRecordInfo.valid)}"" boolean,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{InscriptionRecordTableName}
    OWNER to postgres;

CREATE TABLE public.{InscriptionTickTableName}
(
    id serial NOT NULL,
    ""{nameof(TickInfo.coin_name)}"" bytea NOT NULL UNIQUE,
    ""{nameof(TickInfo.index)}"" bigint NOT NULL,
    ""{nameof(TickInfo.tick)}"" text NOT NULL UNIQUE,
    ""{nameof(TickInfo.lim)}"" bigint NOT NULL,
    ""{nameof(TickInfo.max)}"" bigint NOT NULL,
    ""{nameof(TickInfo.info)}"" json,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{InscriptionTickTableName}
    OWNER to postgres;
", connection);

        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException pex)
        {
            Console.WriteLine($"Failed to create db for Inscription tables due to [{pex.Message}], you may want to execute it yourself, here it is:");
            Console.WriteLine(cmd.CommandText);
        }
    }

    internal async Task InitializeIndex()
    {
        await this.connection.EnsureOpen();
        using var cmd = new NpgsqlCommand(@$"
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_{InscriptionRecordTableName}_{nameof(InscriptionRecordInfo.coin_name)}
    ON public.{InscriptionRecordTableName} USING btree
    ({nameof(InscriptionRecordInfo.coin_name)} DESC NULLS LAST);

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_{InscriptionRecordTableName}_{nameof(InscriptionRecordInfo.to)}
    ON public.{InscriptionRecordTableName} USING btree
    (""{nameof(InscriptionRecordInfo.to)}"" DESC NULLS LAST);

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_{InscriptionRecordTableName}_{nameof(InscriptionRecordInfo.from)}
    ON public.{InscriptionRecordTableName} USING btree
    (""{nameof(InscriptionRecordInfo.from)}"" DESC NULLS LAST);
", connection);
        try
        {
            cmd.CommandTimeout = 3600;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException pex)
        {
            Console.WriteLine($"Failed to execute index creation script due to [{pex.Message}], you may want to execute it yourself, here it is:");
            Console.WriteLine(cmd.CommandText);
        }
    }
}