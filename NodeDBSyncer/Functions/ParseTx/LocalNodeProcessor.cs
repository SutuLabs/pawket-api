namespace NodeDBSyncer.Functions.ParseTx;

using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WalletServer.Helpers;

public class LocalNodeProcessor
{
    private readonly string TargetAddress;
    public LocalNodeProcessor(string targetAddress)
    {
        this.TargetAddress = targetAddress;
    }

    public async Task<PuzzleArg?> ParsePuzzle(string puzzleHex)
    {
        using var client = new HttpClient();
        var content = new StringContent(@$"{{""puzzle"":""{puzzleHex}""}}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"{TargetAddress}/parse_puzzle", content);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        var puz = JsonSerializer.Deserialize<PuzzleArg>(body);
        return puz;
    }

    public async Task<CoinInfo[]> ParseBlock(byte[] generator, byte[][] refGenerators)
    {
        var generatorHex = generator.ToHexWithPrefix0x();
        var refGeneratorsHex = refGenerators.Select(_ => _.ToHexWithPrefix0x()).ToArray();
        using var client = new HttpClient();
        var json = JsonSerializer.Serialize(new
        {
            ref_list = refGeneratorsHex,
            generator = generatorHex,
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        client.Timeout = TimeSpan.FromSeconds(1000);

        var response = await client.PostAsync($"{TargetAddress}/parse_block", content);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        var coins = JsonSerializer.Deserialize<CoinInfoJson[]>(body);
        if (coins == null) return Array.Empty<CoinInfo>();
        return coins
            .Select(_ => new CoinInfo(_.coin_name, _.puzzle, _.parsed_puzzle, _.solution, _.mods, _.analysis))
            .ToArray();
    }
}

