using System.Text.Json;
using chia.dotnet;
using NodeDBSyncer.Functions.ParseTx;
using NodeDBSyncer.Helpers;
using Npgsql;

// Three public-mainnet spends, discovered by SampleScanner in [9000000, 9000700).
var samples = new[]
{
    new Sample("CatV2", "0xab8e77337b648522787becc3315ed894c2fe55675f081870b8f42f65c2e5459b",
        "0xeffc41c56dcc22d1cb3aaa60197bae18daac0f521b2ce9751f91f7d543252167",
        "0x1a68b05cf7e480a16283ae003d405972b95f6bd53fbb12ff5b6b09f7e12a3352"),
    new Sample("NftV1", "0x3c55e361941995c3823c7838d9d7612447d906aa2e447bf0aa279fd08af2d514",
        "0xac102d11a3b4073ca19856a368fb98bf0f7b5019daf3e3d388fbd546d3b4f89f",
        "0x82b7ad4c410fa706301cc4a1fc3cc0b34f1808813f8ab3a0e6774aed7b0f7131"),
    new Sample("DidV1", "0x7b3f34f73edec6954b5fde3462b225e17c7b9cc22460bbcce500ecd460ef7992",
        "0xfd283bbd98369889395ac5c92dfc9b35d2b0e16d91b92e93541c61dd044c2f62",
        "0x82b7ad4c410fa706301cc4a1fc3cc0b34f1808813f8ab3a0e6774aed7b0f7131"),
};

string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required");
byte[] Hex(string value) => Convert.FromHexString(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value);

using var rpc = new HttpRpcClient(new EndpointInfo
{
    CertPath = Env("PAWKET_CHIA_CERT"), KeyPath = Env("PAWKET_CHIA_KEY"), Uri = new Uri(Env("PAWKET_CHIA_URI")),
});
var node = new FullNodeProxy(rpc, "client");
var processor = new LocalNodeProcessor(Env("PAWKET_PROCESSOR_URI"));
var connString = new NpgsqlConnectionStringBuilder
{
    Host = "postgres", Username = Env("PG_USER"), Password = Env("PG_PASSWORD"), Database = Env("PG_DBNAME"),
}.ConnectionString;
await using var db = new NpgsqlConnection(connString);
await db.OpenAsync();
await using var tx = await db.BeginTransactionAsync();

await using var state = new NpgsqlCommand("SELECT spent_index FROM sync_state WHERE id=1", db, tx);
var cutoff = (long)(await state.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing sync_state"));
foreach (var sample in samples)
{
    var parent = await node.GetCoinRecordByName(sample.ParentId);
    var child = await node.GetCoinRecordByName(sample.ChildId);
    if (!parent.Spent || !child.Coin.ParentCoinInfo.Equals(sample.ParentId, StringComparison.OrdinalIgnoreCase) ||
        parent.ConfirmedBlockIndex > cutoff || parent.SpentBlockIndex > cutoff ||
        child.ConfirmedBlockIndex > cutoff || child.SpentBlockIndex > cutoff)
        throw new InvalidOperationException($"Sample {sample.Type} is not valid within the baseline cutoff");
    var spend = await node.GetPuzzleAndSolution(sample.ParentId, parent.SpentBlockIndex);
    var parsed = await processor.AnalyzeTx(new RawUnanalyzedTx(
        spend.PuzzleReveal, spend.Solution, checked((long)parent.Coin.Amount),
        parent.Coin.ParentCoinInfo, parent.Coin.PuzzleHash))
        ?? throw new InvalidOperationException($"Failed to analyze {sample.Type}");
    var recognized = parsed.mods.StartsWith("cat_v2(", StringComparison.Ordinal) ? "CatV2" :
        parsed.mods.StartsWith("singleton_top_layer_v1_1(did_innerpuz(", StringComparison.Ordinal) ? "DidV1" :
        parsed.mods.StartsWith("singleton_top_layer_v1_1(nft_state_layer(", StringComparison.Ordinal) ? "NftV1" : "Other";
    if (recognized != sample.Type || !parsed.coin_name.Equals(sample.ParentId, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Processor mismatch for {sample.Type}");

    await InsertCoin(parent, sample.ParentId);
    await InsertCoin(child, sample.ChildId);
    await using (var hint = new NpgsqlCommand(@"
        INSERT INTO sync_hint_record(id, coin_name, hint)
        SELECT (SELECT COALESCE(MAX(id),0)+1 FROM sync_hint_record), @coin, @hint
        WHERE NOT EXISTS (SELECT 1 FROM sync_hint_record WHERE coin_name=@coin AND hint=@hint)", db, tx))
    {
        hint.Parameters.AddWithValue("coin", Hex(sample.ChildId));
        hint.Parameters.AddWithValue("hint", Hex(sample.Hint));
        await hint.ExecuteNonQueryAsync();
    }
    await using (var klass = new NpgsqlCommand(@"
        INSERT INTO sync_coin_class(coin_name, puzzle, parsed_puzzle, solution, mods, analysis)
        SELECT @coin, @puzzle, CAST(@parsed AS json), @solution, @mods, CAST(@analysis AS json)
        WHERE NOT EXISTS (SELECT 1 FROM sync_coin_class WHERE coin_name=@coin)", db, tx))
    {
        klass.Parameters.AddWithValue("coin", Hex(sample.ParentId));
        klass.Parameters.AddWithValue("puzzle", Hex(spend.PuzzleReveal).Compress());
        klass.Parameters.AddWithValue("parsed", JsonSerializer.Serialize(parsed.parsed_puzzle));
        klass.Parameters.AddWithValue("solution", Hex(spend.Solution).Compress());
        klass.Parameters.AddWithValue("mods", parsed.mods);
        klass.Parameters.AddWithValue("analysis", parsed.analysis);
        await klass.ExecuteNonQueryAsync();
    }
    Console.WriteLine($"Seeded {sample.Type} parent={sample.ParentId} child={sample.ChildId} hint={sample.Hint}");
}
await tx.CommitAsync();

async Task InsertCoin(CoinRecord record, string coinId)
{
    await using var cmd = new NpgsqlCommand(@"
        INSERT INTO sync_coin_record(id, coin_name, confirmed_index, spent_index, coinbase,
            puzzle_hash, coin_parent, amount, timestamp)
        SELECT (SELECT COALESCE(MAX(id),0)+1 FROM sync_coin_record), @coin, @confirmed, @spent,
            @coinbase, @puzzle_hash, @parent, @amount, @timestamp
        WHERE NOT EXISTS (SELECT 1 FROM sync_coin_record WHERE coin_name=@coin)", db, tx);
    cmd.Parameters.AddWithValue("coin", Hex(coinId));
    cmd.Parameters.AddWithValue("confirmed", (long)record.ConfirmedBlockIndex);
    cmd.Parameters.AddWithValue("spent", (long)record.SpentBlockIndex);
    cmd.Parameters.AddWithValue("coinbase", record.Coinbase);
    cmd.Parameters.AddWithValue("puzzle_hash", Hex(record.Coin.PuzzleHash));
    cmd.Parameters.AddWithValue("parent", Hex(record.Coin.ParentCoinInfo));
    cmd.Parameters.AddWithValue("amount", checked((long)record.Coin.Amount));
    cmd.Parameters.AddWithValue("timestamp", checked((long)record.Timestamp));
    await cmd.ExecuteNonQueryAsync();
}

internal record Sample(string Type, string ParentId, string ChildId, string Hint);
