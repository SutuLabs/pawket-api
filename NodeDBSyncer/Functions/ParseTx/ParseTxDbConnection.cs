namespace NodeDBSyncer.Functions.ParseTx;

using System.Data;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NodeDBSyncer.Helpers;
using Npgsql;
using static NodeDBSyncer.Helpers.DbReference;

public class ParseTxDbConnection : PgsqlConnection
{
    public ParseTxDbConnection(string connString)
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
        else if (!await this.CheckColumnExistence())
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

    public record GetUnparsedBlockResponse(BlockTransactionGeneratorRetrieval[] Blocks, BlockTransactionGeneratorRetrieval[] RefBlocks);
    public record BlockTransactionGeneratorRetrieval(ulong index, byte[] generator, uint[]? generator_ref_list);

    public async Task<GetUnparsedBlockResponse> GetUnparsedBlock(int number)
    {
        var sql = $"SELECT index,generator,generator_ref_list FROM {FullBlockTableName}" +
            $" WHERE tx_parsed=FALSE AND is_tx_block=TRUE" +
            $" ORDER BY index DESC" +
            $" LIMIT @limit";
        await using var cmd = new NpgsqlCommand(sql, this.connection)
        {
            Parameters =
            {
                new("limit", number),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var list = await ReadBlocks(reader);
        var refIndexes = list.SelectMany(_ => _.generator_ref_list ?? Array.Empty<uint>()).Distinct().ToArray();

        await reader.CloseAsync();
        await cmd.DisposeAsync();
        var refs = await GetRelativeBlocks(refIndexes);

        return new GetUnparsedBlockResponse(list, refs);
    }

    private async Task<BlockTransactionGeneratorRetrieval[]> GetRelativeBlocks(uint[] blockIndexes)
    {
        if (blockIndexes.Length == 0) return Array.Empty<BlockTransactionGeneratorRetrieval>();

        var sql = $"SELECT index,generator,generator_ref_list FROM {FullBlockTableName}" +
            $" WHERE index = ANY(@list)" +
            $" ORDER BY index";
        var idxs = blockIndexes.Select(_ => (long)_).ToArray();
        await using var cmd = new NpgsqlCommand(sql, this.connection)
        {
            Parameters =
            {
                new("list", idxs),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();
        return await ReadBlocks(reader);
    }

    private static async Task<BlockTransactionGeneratorRetrieval[]> ReadBlocks(NpgsqlDataReader reader)
    {
        var list = new List<BlockTransactionGeneratorRetrieval>();
        while (await reader.ReadAsync())
        {
            var index = reader.GetFieldValue<long>(0);
            var generator = reader.GetFieldValue<byte[]>(1);
            var generator_ref_list_buff = reader.GetFieldValue<byte[]>(2);
            var generator_ref_list = MemoryMarshal.Cast<byte, uint>(generator_ref_list_buff).ToArray();
            list.Add(new BlockTransactionGeneratorRetrieval((ulong)index, generator.Decompress(), generator_ref_list));
        }

        return list.ToArray();
    }

    public async Task UpdateParsedBlock(ulong[] blockIndexes)
    {
        var sql = $"UPDATE {FullBlockTableName}" +
            $" SET tx_parsed=TRUE" +
            $" WHERE index = ANY(@list)";
        await using var cmd = new NpgsqlCommand(sql, this.connection)
        {
            Parameters =
            {
                new("list", blockIndexes.Select(_=>(long)_).ToArray()),
            }
        };

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> GetLatestBlockSynced() => await GetMaxId(FullBlockTableName, "index");

    internal async Task<bool> CheckIndexExistence() => await this.connection.CheckExistence($"idx_{FullBlockTableName}_index");

    public async Task WriteCoinClassRecords(IEnumerable<CoinInfoForStorage> records)
    {
        await this.connection.Import(ConvertRecordsToTable(records), CoinClassTableName);
    }

    public async Task WriteBlockRecords(IEnumerable<BlockInfo> records)
    {
        await this.connection.Import(ConvertRecordsToTable(records), FullBlockTableName);
    }

    public async Task CleanCoinClassByBlock(long begin, long end)
    {
        var sql = @$"DELETE FROM {CoinClassTableName}
WHERE id in
		(SELECT cc.id FROM {CoinClassTableName} cc
			JOIN {CoinRecordTableName} c ON c.coin_name = cc.coin_name
			WHERE c.spent_index in
					(SELECT index FROM {FullBlockTableName}
						WHERE index BETWEEN @begin AND @end
							AND is_tx_block = TRUE
							AND tx_parsed = FALSE ))
";
        await using var cmd = new NpgsqlCommand(sql, this.connection)
        {
            Parameters =
            {
                new("begin", begin),
                new("end", end),
            }
        };
        cmd.CommandTimeout = 1000;

        await cmd.ExecuteNonQueryAsync();
    }

    private DataTable ConvertRecordsToTable(IEnumerable<BlockInfo> records)
    {
        var dt = new DataTable();
        dt.Columns.Add(nameof(BlockInfo.is_tx_block), typeof(bool));
        dt.Columns.Add(nameof(BlockInfo.index), typeof(long));
        dt.Columns.Add(nameof(BlockInfo.weight), typeof(long));
        dt.Columns.Add(nameof(BlockInfo.iterations), typeof(long));
        dt.Columns.Add(nameof(BlockInfo.cost), typeof(long));
        dt.Columns.Add(nameof(BlockInfo.fee), typeof(long));
        dt.Columns.Add(nameof(BlockInfo.generator), typeof(byte[]));
        dt.Columns.Add(nameof(BlockInfo.generator_ref_list), typeof(byte[]));
        //dt.Columns.Add(nameof(BlockInfo.block_info), typeof(byte[]));
        dt.Columns.Add(nameof(BlockInfo.block_info), typeof(string));

        foreach (var r in records)
        {
            // don't use JsonSerializer as they are slow and generate wrong json due to BigInteger
            var bi = Newtonsoft.Json.JsonConvert.SerializeObject(
                r.block_info with { TransactionsGenerator = null, TransactionsGeneratorRefList = Array.Empty<uint>() },
                new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });

            dt.Rows.Add(
                r.is_tx_block,
                r.index,
                (long)r.weight,
                (long)r.iterations,
                (long)r.cost,
                (long)r.fee,
                r.generator.Compress(),
                MemoryMarshal.AsBytes<uint>(r.generator_ref_list.ToArray()).ToArray(),
                bi);
        }

        return dt;
    }

    private DataTable ConvertRecordsToTable(IEnumerable<CoinInfoForStorage> records)
    {
        var dt = new DataTable();
        dt.Columns.Add(nameof(CoinInfo.coin_name), typeof(byte[]));
        dt.Columns.Add(nameof(CoinInfo.puzzle), typeof(byte[]));
        dt.Columns.Add(nameof(CoinInfo.parsed_puzzle), typeof(string));
        dt.Columns.Add(nameof(CoinInfo.solution), typeof(byte[]));
        dt.Columns.Add(nameof(CoinInfo.mods), typeof(string));
        dt.Columns.Add(nameof(CoinInfo.analysis), typeof(string));

        foreach (var r in records)
        {
            dt.Rows.Add(
                r.coin_name,
                r.puzzle,
                r.parsed_puzzle,
                r.solution,
                r.mods,
                r.analysis);
        }

        return dt;
    }

    private async Task<bool> CheckTableExistence() => await this.connection.CheckExistence(FullBlockTableName);
    private async Task<bool> CheckColumnExistence()
        => await this.connection.CheckColumnExistence(CoinClassTableName, nameof(CoinInfo.analysis));

    private async Task UpgradeDatabase()
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
    tx_parsed boolean DEFAULT false,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{FullBlockTableName}
    OWNER to postgres;

CREATE TABLE public.{CoinClassTableName}
(
    id serial NOT NULL,
    {nameof(CoinInfo.coin_name)} bytea NOT NULL UNIQUE,
    {nameof(CoinInfo.puzzle)} bytea NOT NULL,
    {nameof(CoinInfo.parsed_puzzle)} json NOT NULL,
    {nameof(CoinInfo.solution)} bytea NOT NULL,
    {nameof(CoinInfo.mods)} text,
    {nameof(CoinInfo.analysis)} json,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{CoinClassTableName}
    OWNER to postgres;
", connection);

        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException pex)
        {
            Console.WriteLine($"Failed to create db for block/coin-class due to [{pex.Message}], you may want to execute it yourself, here it is:");
            Console.WriteLine(cmd.CommandText);
        }
    }

    private async Task UpgradeDatabaseV2()
    {
        using var cmd = new NpgsqlCommand(
            $"ALTER TABLE public.{CoinClassTableName} ADD COLUMN IF NOT EXISTS {nameof(CoinInfo.analysis)} json;", connection);

        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException pex)
        {
            Console.WriteLine($"Failed to upgrade v2 db for block/coin-class due to [{pex.Message}], you may want to execute it yourself, here it is:");
            Console.WriteLine(cmd.CommandText);
        }
    }

    internal async Task InitializeIndex()
    {
        using var cmd = new NpgsqlCommand(@$"
CREATE INDEX IF NOT EXISTS idx_{FullBlockTableName}_index
    ON public.{FullBlockTableName} USING btree
    (index DESC NULLS LAST);
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