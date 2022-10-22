namespace NodeDBSyncer.Functions.ParseTx;

using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using WalletServer.Helpers;

public class LocalNodeProcessor
{
    private readonly string TargetAddress;
    public LocalNodeProcessor(string targetAddress)
    {
        this.TargetAddress = targetAddress;
    }

    public async Task<string?> GetVersion()
    {
        using var client = new HttpClient();

        var response = await client.GetAsync($"{TargetAddress}/version");
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        var ret = JsonConvert.DeserializeAnonymousType(body, new { version = "" });
        return ret?.version;
    }

    public async Task<PuzzleArg?> ParsePuzzle(string puzzleHex)
    {
        using var client = new HttpClient();
        var content = new StringContent(@$"{{""puzzle"":""{puzzleHex}""}}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"{TargetAddress}/parse_puzzle", content);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        var puz = JsonConvert.DeserializeObject<PuzzleArg>(body);
        return puz;
    }

    public async Task<AnalysisResult?> AnalyzeTx(UnanalyzedTx tx)
    {
        using var client = new HttpClient();
        var json = JsonConvert.SerializeObject(tx);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"{TargetAddress}/analyze_tx", content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return null;

        var result = JsonConvert.DeserializeObject<AnalysisResult>(body);
        return result;
    }

    public async Task<CoinInfo[]> ParseBlock(byte[] generator, byte[][] refGenerators)
    {
        var generatorHex = generator.ToHexWithPrefix0x();
        var refGeneratorsHex = refGenerators.Select(_ => _.ToHexWithPrefix0x()).ToArray();
        using var client = new HttpClient();
        var json = JsonConvert.SerializeObject(new
        {
            ref_list = refGeneratorsHex,
            generator = generatorHex,
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        client.Timeout = TimeSpan.FromSeconds(1000);

        var response = await client.PostAsync($"{TargetAddress}/parse_block", content);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        var coins = JsonConvert.DeserializeObject<CoinInfoJson[]>(body);
        if (coins == null) return Array.Empty<CoinInfo>();
        return coins
            .Select(_ => new CoinInfo(_.coin_name, _.puzzle, _.parsed_puzzle, _.solution, _.mods, _.analysis))
            .ToArray();
    }
}

public record PuzzleArg(string? mod, PuzzleArg[]? args, string? raw);
public record CoinInfo(string coin_name, string puzzle, PuzzleArg parsed_puzzle, string solution, string mods, string analysis);
public record AnalysisResult(string coin_name, PuzzleArg parsed_puzzle, string mods, string analysis);
