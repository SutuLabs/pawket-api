namespace NodeDBSyncer.Functions.ParseTx;

using System.Data;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NodeDBSyncer.Helpers;
using Npgsql;
using WalletServer.Helpers;
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

    public record GetUnparsedBlockResponse(BlockTransactionGeneratorRetrieval[] Blocks, BlockTransactionGeneratorRetrieval[] RefBlocks);
    public record BlockTransactionGeneratorRetrieval(ulong index, byte[] generator, uint[]? generator_ref_list);

    public async Task<GetUnparsedBlockResponse> GetUnparsedBlock(int number)
    {
        await this.connection.EnsureOpen();
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
        await this.connection.EnsureOpen();
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

    public async Task<int> UpdateTxAnalysis(AnalysisUpdateEntity[] changes)
    {
        await this.connection.EnsureOpen();
        var tmpTable = "_tmp_import_analysis_update_table";
        var analysisField = nameof(AnalysisUpdateEntity.analysis);
        var idField = nameof(AnalysisUpdateEntity.id);

        using var cmd = new NpgsqlCommand(
            $"CREATE TEMPORARY TABLE {tmpTable}" +
            $"({idField} bigint NOT NULL," +
            $" {analysisField} json NOT NULL," +
            $" PRIMARY KEY ({idField}));",
            connection);
        await cmd.ExecuteNonQueryAsync();

        var dataTable = changes.ConvertToDataTable();
        await this.connection.Import(dataTable, tmpTable);

        using var cmd2 = new NpgsqlCommand($"UPDATE {CoinClassTableName} SET {analysisField} = t.{analysisField}" +
            $" FROM {tmpTable} as t" +
            $" WHERE t.{idField} = {CoinClassTableName}.{idField};" +
            $"DROP TABLE {tmpTable};",
            connection);
        return await cmd2.ExecuteNonQueryAsync();
    }

    public async Task<long> GetLatestBlockSynced() => await GetMaxId(FullBlockTableName, "index");

    internal async Task<bool> CheckIndexExistence() => await this.connection.CheckExistence($"idx_{FullBlockTableName}_index");

    public async Task WriteCoinClassRecords(IEnumerable<CoinInfoForStorage> records)
    {
        await this.connection.EnsureOpen();
        await this.connection.Import(ConvertRecordsToTable(records), CoinClassTableName);
    }

    public async Task WriteBlockRecords(IEnumerable<BlockInfo> records)
    {
        await this.connection.EnsureOpen();
        await this.connection.Import(ConvertRecordsToTable(records), FullBlockTableName);
    }

    public async Task CleanCoinClassByBlock(long begin, long end)
    {
        await this.connection.EnsureOpen();
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

    public async Task RemoveCoinClass(byte[][] coins)
    {
        await this.connection.EnsureOpen();
        var sql = @$"DELETE FROM {CoinClassTableName} WHERE coin_name = ANY(@list)";
        await using var cmd = new NpgsqlCommand(sql, this.connection)
        {
            Parameters =
            {
                new("list", coins),
            }
        };

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<UnanalyzedTx[]> GetUnanalyzedTxs(int number)
    {
        await this.connection.EnsureOpen();
        var sql = $"SELECT cc.id,c.coin_name,cc.puzzle,cc.solution,c.amount,c.coin_parent,c.puzzle_hash" +
            $" FROM sync_coin_class cc" +
            $" JOIN sync_coin_record c ON c.coin_name=cc.coin_name" +
            $" WHERE cc.mods IN ('singleton_top_layer_v1_1(did_innerpuz(p2_delegated_puzzle_or_hidden_puzzle()))'," +
            $" 'singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),p2_delegated_puzzle_or_hidden_puzzle())))'," +
            $" 'singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),settlement_payments())))')" +
            $" AND cc.analysis IS NULL" +
            $" ORDER BY cc.id DESC" +
            $" LIMIT @limit";
        await using var cmd = new NpgsqlCommand(sql, this.connection)
        {
            Parameters =
            {
                new("limit", number),
            }
        };
        await using var reader = await cmd.ExecuteReaderAsync();

        var list = await ReadTxs(reader);

        return list;
    }

    private static async Task<UnanalyzedTx[]> ReadTxs(NpgsqlDataReader reader)
    {
        var list = new List<UnanalyzedTx>();
        while (await reader.ReadAsync())
        {
            var id = reader.GetFieldValue<long>(0);
            var coin_name = reader.GetFieldValue<byte[]>(1);
            var puzzle = reader.GetFieldValue<byte[]>(2);
            var solution = reader.GetFieldValue<byte[]>(3);
            var amount = reader.GetFieldValue<long>(4);
            var coin_parent = reader.GetFieldValue<byte[]>(5);
            var puzzle_hash = reader.GetFieldValue<byte[]>(6);
            list.Add(new UnanalyzedTx(
                id,
                coin_name.ToHexWithPrefix0x(),
                puzzle.Decompress().ToHexWithPrefix0x(),
                solution.Decompress().ToHexWithPrefix0x(),
                amount,
                coin_parent.ToHexWithPrefix0x(),
                puzzle_hash.ToHexWithPrefix0x()));
        }

        return list.ToArray();
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
        await this.connection.EnsureOpen();
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