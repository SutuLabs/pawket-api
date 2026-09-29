using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using chia.dotnet;
using NodeDBSyncer.Functions.ParseTx;

if (args.Length != 2 || !uint.TryParse(args[0], out var start) ||
    !uint.TryParse(args[1], out var end) || end <= start || end - start > 100000)
{
    Console.Error.WriteLine("Usage: SampleScanner START END (end exclusive, maximum 100000 blocks)");
    return 2;
}

var cert = Environment.GetEnvironmentVariable("PAWKET_CHIA_CERT") ?? throw new InvalidOperationException("PAWKET_CHIA_CERT is required");
var key = Environment.GetEnvironmentVariable("PAWKET_CHIA_KEY") ?? throw new InvalidOperationException("PAWKET_CHIA_KEY is required");
var uri = Environment.GetEnvironmentVariable("PAWKET_CHIA_URI") ?? throw new InvalidOperationException("PAWKET_CHIA_URI is required");
var processorUri = Environment.GetEnvironmentVariable("PAWKET_PROCESSOR_URI") ?? throw new InvalidOperationException("PAWKET_PROCESSOR_URI is required");

using var rpc = new HttpRpcClient(new EndpointInfo { CertPath = cert, KeyPath = key, Uri = new Uri(uri) });
var node = new FullNodeProxy(rpc, "client");
var processor = new LocalNodeProcessor(processorUri);
var found = new HashSet<string>(StringComparer.Ordinal);
long totalSpends = 0;
var scanned = start;

while (scanned < end && found.Count < 3)
{
    var chunkEnd = Math.Min(scanned + 100, end);
    var records = (await node.GetBlockRecords(scanned, chunkEnd)).OrderBy(r => r.Height).ToArray();
    foreach (var block in records.Where(r => r.IsTransactionBlock))
    {
        var spends = (await node.GetBlockSpends(block.HeaderHash)).ToArray();
        if (spends.Length == 0) continue;
        totalSpends += spends.Length;
        var inputs = spends.Select(s => new RawUnanalyzedTx(
            s.PuzzleReveal, s.Solution, checked((long)s.Coin.Amount),
            s.Coin.ParentCoinInfo, s.Coin.PuzzleHash)).ToArray();
        var analyses = await processor.AnalyzeTxs(inputs)
            ?? throw new InvalidOperationException($"coin processor failed at block {block.Height}");
        foreach (var result in analyses)
        {
            var type = result.mods.StartsWith("cat_v2(", StringComparison.Ordinal) ? "CatV2" :
                result.mods.StartsWith("singleton_top_layer_v1_1(did_innerpuz(", StringComparison.Ordinal) ? "DidV1" :
                result.mods.StartsWith("singleton_top_layer_v1_1(nft_state_layer(", StringComparison.Ordinal) ? "NftV1" : null;
            if (type is null || !found.Add(type)) continue;
            using var analysis = JsonDocument.Parse(result.analysis);
            var root = analysis.RootElement;
            var hint = root.TryGetProperty("nextCoin", out var nextCoin) && nextCoin.TryGetProperty("hint", out var nextHint) ? nextHint.GetString() :
                root.TryGetProperty("hintPuzzle", out var hintPuzzle) ? hintPuzzle.GetString() : null;
            if (!string.IsNullOrEmpty(hint) && !hint.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hint = "0x" + hint;
            var children = (await node.GetCoinRecordsByParentIds(new[] { result.coin_name }, true)).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type,
                blockHeight = block.Height,
                coinId = result.coin_name,
                mods = result.mods,
                hint,
                childIds = children.Select(c => c.Coin.Name).ToArray(),
                analysisLength = result.analysis?.Length ?? 0,
                analysisSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.analysis ?? ""))).ToLowerInvariant(),
            }));
            if (found.Count == 3) break;
        }
        if (found.Count == 3) break;
    }
    scanned = chunkEnd;
    Console.Error.WriteLine($"Scanned through {scanned - 1}; spends={totalSpends}; types={string.Join(",", found)}");
}

return found.Count == 3 ? 0 : 1;
