namespace NodeDBSyncer.Functions.ParseSingleton;

using System.Data;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NodeDBSyncer.Helpers;
using Npgsql;
using WalletServer.Helpers;
using static NodeDBSyncer.Helpers.DbReference;

public class ParseSingletonDbConnection : PgsqlConnection
{
    private const string ProcessedKey = "singleton_coin_class_id";
    public ParseSingletonDbConnection(string connString)
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

    public async Task<SingletonRecordInfo[]> GetSingletonRecords(long last_coin_class_id, int number)
    {
        await this.connection.EnsureOpen();
        //-- cc: selected nft coin class(maybe ccb)
        //-- c: singleton launcher coin
        //-- cp: genesis coin
        //-- cb: bootstrap coin
        //-- ccb: bootstrap coin class
        var sql = $@"
SELECT
    cc.id AS {nameof(SingletonRecordInfo.last_coin_class_id)},
    c.coin_name AS {nameof(SingletonRecordInfo.singleton_coin_name)},
    c.spent_index AS {nameof(SingletonRecordInfo.singleton_create_index)},
    ccb.coin_name AS {nameof(SingletonRecordInfo.bootstrap_coin_name)},
    cp.puzzle_hash AS {nameof(SingletonRecordInfo.creator_puzzle_hash)},
    CASE
       WHEN translate(ccb.analysis->>'didOwner', '0123456789abcdefABCDEF', '') = ''
       THEN decode(ccb.analysis->>'didOwner', 'hex')
       ELSE NULL
    END AS {nameof(SingletonRecordInfo.creator_did)},
    CASE ccb.mods
        WHEN 'singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),p2_delegated_puzzle_or_hidden_puzzle())))'
        THEN 'nft_v1'
        WHEN 'singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),settlement_payments())))'
        THEN 'nft_v1'
        WHEN 'singleton_top_layer_v1_1(did_innerpuz(p2_delegated_puzzle_or_hidden_puzzle()))'
        THEN 'did_v1'
        ELSE 'unknown'
    END AS {nameof(SingletonRecordInfo.type)}
FROM sync_coin_class cc
JOIN sync_coin_record c ON c.coin_name=decode(substring(cc.analysis->>'launcherId' from 3),'hex')
LEFT JOIN sync_coin_record cp ON c.coin_parent=cp.coin_name
LEFT JOIN sync_coin_record cb ON cb.coin_parent=decode(substring(cc.analysis->>'launcherId' from 3),'hex')
LEFT JOIN sync_coin_class ccb ON ccb.coin_name=cb.coin_name

WHERE cc.mods IN (
    'singleton_top_layer_v1_1(did_innerpuz(p2_delegated_puzzle_or_hidden_puzzle()))',
    'singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),p2_delegated_puzzle_or_hidden_puzzle())))',
    'singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),settlement_payments())))'
)
AND cc.analysis is not null
AND cc.id>@start

ORDER BY cc.id
LIMIT @limit
";
        await using var cmd = new NpgsqlCommand(sql, this.connection)
        {
            Parameters =
            {
                new("start",last_coin_class_id),
                new("limit", number),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var list = await ReadSingletonRecords(reader);
        return list;

        static async Task<SingletonRecordInfo[]> ReadSingletonRecords(NpgsqlDataReader reader)
        {
            var list = new List<SingletonRecordInfo>();
            while (await reader.ReadAsync())
            {
                var lccid = reader.GetFieldValue<long>(0);
                var singleton_cn = reader.GetFieldValue<byte[]>(1);
                var singleton_idx = reader.GetFieldValue<int>(2);
                var bootstrap_cn = reader.GetNullableFieldValue<byte[]>(3); // due to incompleteness of sync_coin_class table, this field may missing data
                var creator_ph = reader.GetNullableFieldValue<byte[]>(4);
                var creator_did = reader.GetNullableFieldValue<byte[]>(5);
                var type = reader.GetNullableFieldValue<string>(6);
                list.Add(new SingletonRecordInfo(
                    lccid,
                    singleton_cn.ToHexWithPrefix0x(),
                    singleton_idx,
                    bootstrap_cn.ToHexWithPrefix0x(),
                    creator_ph.ToHexWithPrefix0x(),
                    creator_did.ToHexWithPrefix0x(),
                    type));
            }

            return list.ToArray();
        }
    }

    public async Task<SingletonHistoryInfo[]> GetSingletonHistories(long last_coin_class_id, int number)
    {
        await this.connection.EnsureOpen();
        //-- cc: selected nft coin class
        //-- ct: selected nft coin record
        var sql = $@"
SELECT
    cc.id as {nameof(SingletonHistoryInfo.coin_class_id)},
    decode(substring(cc.analysis->>'launcherId' from 3),'hex') as {nameof(SingletonHistoryInfo.singleton_coin_name)},
    cc.coin_name as {nameof(SingletonHistoryInfo.this_coin_name)},
    c.spent_index as {nameof(SingletonHistoryInfo.this_coin_spent_index)},
    decode(substring(cc.analysis->>'nextCoinName' from 3),'hex') as {nameof(SingletonHistoryInfo.next_coin_name)},
    CASE
       WHEN translate(cc.analysis->>'p2Owner', '0123456789abcdefABCDEF', '') = ''
       THEN decode(cc.analysis->>'p2Owner', 'hex')
       ELSE NULL
    END AS {nameof(SingletonHistoryInfo.p2_owner)},
    CASE
       WHEN translate(cc.analysis->>'didOwner', '0123456789abcdefABCDEF', '') = ''
       THEN decode(cc.analysis->>'didOwner', 'hex')
       ELSE NULL
    END AS {nameof(SingletonHistoryInfo.did_owner)},
    CASE cc.mods
        WHEN 'singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),p2_delegated_puzzle_or_hidden_puzzle())))'
        THEN 'nft_v1'
        WHEN 'singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),settlement_payments())))'
        THEN 'nft_v1'
        WHEN 'singleton_top_layer_v1_1(did_innerpuz(p2_delegated_puzzle_or_hidden_puzzle()))'
        THEN 'did_v1'
        ELSE 'unknown'
    END AS type

FROM {CoinClassTableName} cc
JOIN {CoinRecordTableName} c ON c.coin_name=cc.coin_name
WHERE cc.mods IN (
    'singleton_top_layer_v1_1(did_innerpuz(p2_delegated_puzzle_or_hidden_puzzle()))',
    'singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),p2_delegated_puzzle_or_hidden_puzzle())))',
    'singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),settlement_payments())))'
)

AND cc.analysis IS NOT NULL
AND cc.id>@start
AND cc.id<@start + 1000000

ORDER BY cc.id
LIMIT @limit
";
        await using var cmd = new NpgsqlCommand(sql, this.connection)
        {
            Parameters =
            {
                new("start",last_coin_class_id),
                new("limit", number),
            },
            CommandTimeout = 600,
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var list = await ReadSingletonRecords(reader);
        return list;

        static async Task<SingletonHistoryInfo[]> ReadSingletonRecords(NpgsqlDataReader reader)
        {
            var list = new List<SingletonHistoryInfo>();
            while (await reader.ReadAsync())
            {
                var ccid = reader.GetFieldValue<long>(0);
                var singleton_cn = reader.GetFieldValue<byte[]>(1);
                var this_cn = reader.GetFieldValue<byte[]>(2);
                var this_idx = reader.GetFieldValue<int>(3);
                var next_cn = reader.GetNullableFieldValue<byte[]>(4);
                var p2_owner = reader.GetNullableFieldValue<byte[]>(5);
                var did_owner = reader.GetNullableFieldValue<byte[]>(6);
                var type = reader.GetNullableFieldValue<string>(7);
                list.Add(new SingletonHistoryInfo(
                    ccid,
                    singleton_cn.ToHexWithPrefix0x(),
                    this_cn.ToHexWithPrefix0x(),
                    this_idx,
                    next_cn.ToHexWithPrefix0x(),
                    p2_owner.ToHexWithPrefix0x(),
                    did_owner.ToHexWithPrefix0x(),
                    type));
            }

            return list.ToArray();
        }
    }

    public async Task<int> UpdateSingletonRecords(SingletonRecordInfo[] changes)
    {
        await this.connection.EnsureOpen();
        var tmpTable = "_tmp_import_singleton_record_update_table";

        // TODO: deduplicate this table creation script
        using var cmd = new NpgsqlCommand(
            $@"CREATE TEMPORARY TABLE {tmpTable}(
    id serial NOT NULL,
    {nameof(SingletonRecordInfo.last_coin_class_id)} bigint NOT NULL UNIQUE,
    {nameof(SingletonRecordInfo.singleton_coin_name)} bytea NOT NULL UNIQUE,
    {nameof(SingletonRecordInfo.singleton_create_index)} int NOT NULL,
    {nameof(SingletonRecordInfo.bootstrap_coin_name)} bytea NOT NULL,
    {nameof(SingletonRecordInfo.creator_puzzle_hash)} bytea,
    {nameof(SingletonRecordInfo.creator_did)} bytea,
    {nameof(SingletonRecordInfo.type)} text,
    PRIMARY KEY (id)
);", connection);
        await cmd.ExecuteNonQueryAsync();

        var dataTable = ConvertRecordsToTable(changes);
        await this.connection.Import(dataTable, tmpTable);

        var fields = string.Join(",", new[] {
            nameof(SingletonRecordInfo.last_coin_class_id),
            nameof(SingletonRecordInfo.singleton_coin_name),
            nameof(SingletonRecordInfo.singleton_create_index),
            nameof(SingletonRecordInfo.bootstrap_coin_name),
            nameof(SingletonRecordInfo.creator_puzzle_hash),
            nameof(SingletonRecordInfo.creator_did),
            nameof(SingletonRecordInfo.type),
        });
        using var cmd2 = new NpgsqlCommand($@"
INSERT INTO {SingletonRecordTableName}({fields})
SELECT {fields} FROM {tmpTable}
ON CONFLICT({nameof(SingletonRecordInfo.singleton_coin_name)})
DO UPDATE SET {nameof(SingletonRecordInfo.last_coin_class_id)} = EXCLUDED.{nameof(SingletonRecordInfo.last_coin_class_id)};
" +
            $"DROP TABLE {tmpTable};",
            connection);
        return await cmd2.ExecuteNonQueryAsync();
    }

    public async Task<int> UpdateSingletonHistories(SingletonHistoryInfo[] changes)
    {
        await this.connection.EnsureOpen();
        var tmpTable = "_tmp_import_singleton_history_update_table";

        // TODO: deduplicate this table creation script
        using var cmd = new NpgsqlCommand(
            $@"CREATE TEMPORARY TABLE {tmpTable}(
    id serial NOT NULL,
    {nameof(SingletonHistoryInfo.coin_class_id)} bigint NOT NULL UNIQUE,
    {nameof(SingletonHistoryInfo.singleton_coin_name)} bytea NOT NULL,
    {nameof(SingletonHistoryInfo.this_coin_name)} bytea NOT NULL,
    {nameof(SingletonHistoryInfo.this_coin_spent_index)} int NOT NULL,
    {nameof(SingletonHistoryInfo.next_coin_name)} bytea NOT NULL,
    {nameof(SingletonHistoryInfo.p2_owner)} bytea,
    {nameof(SingletonHistoryInfo.did_owner)} bytea,
    {nameof(SingletonHistoryInfo.type)} text,
    PRIMARY KEY (id)
);", connection);
        await cmd.ExecuteNonQueryAsync();

        var dataTable = ConvertRecordsToTable(changes);
        await this.connection.Import(dataTable, tmpTable);

        var fields = string.Join(",", typeof(SingletonHistoryInfo).GetPropNames());
        using var cmd2 = new NpgsqlCommand($@"
INSERT INTO {SingletonHistoryTableName}({fields})
SELECT {fields} FROM {tmpTable}
ON CONFLICT({nameof(SingletonHistoryInfo.coin_class_id)})
DO NOTHING;
" +
            $"DROP TABLE {tmpTable};",
            connection);
        return await cmd2.ExecuteNonQueryAsync();
    }

    public async Task<int> EnsureCoinSpentIndex()
    {
        using var cmd = new NpgsqlCommand(@$"
UPDATE {SingletonHistoryTableName} sh
SET {nameof(SingletonHistoryInfo.this_coin_spent_index)}=c.spent_index
FROM {CoinRecordTableName} c
WHERE c.coin_name=sh.{nameof(SingletonHistoryInfo.this_coin_name)}
    AND sh.{nameof(SingletonHistoryInfo.this_coin_spent_index)}=0

", connection);

        try
        {
            return await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException pex)
        {
            Console.WriteLine($"Failed to ensure coin spent index due to [{pex.Message}], you may want to execute it yourself, here it is:");
            Console.WriteLine(cmd.CommandText);
            return -1;
        }
    }

    public async Task<long> GetSingletonRecordLatestCoinClassIdSynced() => await GetMaxId(SingletonRecordTableName, nameof(SingletonRecordInfo.last_coin_class_id));
    public async Task<long> GetSingletonHistoryLatestCoinClassIdSynced() => await GetMaxId(SingletonHistoryTableName, nameof(SingletonHistoryInfo.coin_class_id));

    internal async Task<bool> CheckIndexExistence() => await this.connection.CheckExistence($"idx_{SingletonRecordTableName}_{nameof(SingletonRecordInfo.creator_puzzle_hash)}");

    private DataTable ConvertRecordsToTable(IEnumerable<SingletonRecordInfo> records)
    {
        var dt = new DataTable();
        dt.Columns.Add(nameof(SingletonRecordInfo.last_coin_class_id), typeof(long));
        dt.Columns.Add(nameof(SingletonRecordInfo.singleton_coin_name), typeof(byte[]));
        dt.Columns.Add(nameof(SingletonRecordInfo.singleton_create_index), typeof(int));
        dt.Columns.Add(nameof(SingletonRecordInfo.bootstrap_coin_name), typeof(byte[]));
        dt.Columns.Add(nameof(SingletonRecordInfo.creator_puzzle_hash), typeof(byte[]));
        dt.Columns.Add(nameof(SingletonRecordInfo.creator_did), typeof(byte[]));
        dt.Columns.Add(nameof(SingletonRecordInfo.type), typeof(string));

        foreach (var r in records)
        {
            dt.Rows.Add(
                r.last_coin_class_id,
                r.singleton_coin_name.ToHexBytes(),
                r.singleton_create_index,
                r.bootstrap_coin_name.ToHexBytes(),
                r.creator_puzzle_hash?.ToHexBytes(),
                r.creator_did?.ToHexBytes(),
                r.type);
        }

        return dt;
    }

    private DataTable ConvertRecordsToTable(IEnumerable<SingletonHistoryInfo> records)
    {
        var dt = new DataTable();
        dt.Columns.Add(nameof(SingletonHistoryInfo.coin_class_id), typeof(long));
        dt.Columns.Add(nameof(SingletonHistoryInfo.singleton_coin_name), typeof(byte[]));
        dt.Columns.Add(nameof(SingletonHistoryInfo.this_coin_name), typeof(byte[]));
        dt.Columns.Add(nameof(SingletonHistoryInfo.this_coin_spent_index), typeof(int));
        dt.Columns.Add(nameof(SingletonHistoryInfo.next_coin_name), typeof(byte[]));
        dt.Columns.Add(nameof(SingletonHistoryInfo.p2_owner), typeof(byte[]));
        dt.Columns.Add(nameof(SingletonHistoryInfo.did_owner), typeof(byte[]));
        dt.Columns.Add(nameof(SingletonHistoryInfo.type), typeof(string));

        foreach (var r in records)
        {
            dt.Rows.Add(
                r.coin_class_id,
                r.singleton_coin_name.ToHexBytes(),
                r.this_coin_name.ToHexBytes(),
                r.this_coin_spent_index,
                r.next_coin_name.ToHexBytes(),
                r.p2_owner?.ToHexBytes(),
                r.did_owner?.ToHexBytes(),
                r.type);
        }

        return dt;
    }

    private async Task<bool> CheckTableExistence() => await this.connection.CheckExistence(SingletonRecordTableName);

    private async Task UpgradeDatabase()
    {
        using var cmd = new NpgsqlCommand(@$"
-- ALTER TABLE public.{SyncStateTableName} ADD COLUMN IF NOT EXISTS {ProcessedKey} bigint;

CREATE TABLE public.{SingletonRecordTableName}
(
    id serial NOT NULL,
    {nameof(SingletonRecordInfo.last_coin_class_id)} bigint NOT NULL UNIQUE,
    {nameof(SingletonRecordInfo.singleton_coin_name)} bytea NOT NULL UNIQUE,
    {nameof(SingletonRecordInfo.singleton_create_index)} int NOT NULL,
    {nameof(SingletonRecordInfo.bootstrap_coin_name)} bytea NOT NULL,
    {nameof(SingletonRecordInfo.creator_puzzle_hash)} bytea,
    {nameof(SingletonRecordInfo.creator_did)} bytea,
    {nameof(SingletonRecordInfo.type)} text,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{SingletonRecordTableName}
    OWNER to postgres;

CREATE TABLE public.{SingletonHistoryTableName}
(
    id serial NOT NULL,
    {nameof(SingletonHistoryInfo.coin_class_id)} bigint NOT NULL UNIQUE,
    {nameof(SingletonHistoryInfo.singleton_coin_name)} bytea NOT NULL,
    {nameof(SingletonHistoryInfo.this_coin_name)} bytea NOT NULL,
    {nameof(SingletonHistoryInfo.this_coin_spent_index)} int NOT NULL,
    {nameof(SingletonHistoryInfo.next_coin_name)} bytea NOT NULL,
    {nameof(SingletonHistoryInfo.p2_owner)} bytea,
    {nameof(SingletonHistoryInfo.did_owner)} bytea,
    {nameof(SingletonHistoryInfo.type)} text,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{SingletonHistoryTableName}
    OWNER to postgres;
", connection);

        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException pex)
        {
            Console.WriteLine($"Failed to create db for singleton tables due to [{pex.Message}], you may want to execute it yourself, here it is:");
            Console.WriteLine(cmd.CommandText);
        }
    }

    internal async Task InitializeIndex()
    {
        await this.connection.EnsureOpen();
        using var cmd = new NpgsqlCommand(@$"
CREATE INDEX IF NOT EXISTS idx_{SingletonRecordTableName}_{nameof(SingletonRecordInfo.creator_puzzle_hash)}
    ON public.{SingletonRecordTableName} USING btree
    ({nameof(SingletonRecordInfo.creator_puzzle_hash)} DESC NULLS LAST);
", connection);
        try
        {
            cmd.CommandTimeout = 600;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException pex)
        {
            Console.WriteLine($"Failed to execute index creation script due to [{pex.Message}], you may want to execute it yourself, here it is:");
            Console.WriteLine(cmd.CommandText);
        }
    }
}