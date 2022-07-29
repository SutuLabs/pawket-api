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

    public async Task<PuzzleArg> ParsePuzzle(string puzzleHex)
    {
        using var client = new HttpClient();
        var content = new StringContent(@$"{{""puzzle"":""{puzzleHex}""}}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"{TargetAddress}/parse_puzzle", content);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        var puz = JsonSerializer.Deserialize<PuzzleArg>(body);
        return puz;
    }

    public async Task<CoinInfo[]> ParseBlock(string generatorHex, string[] ref_generaters)
    {
        using var client = new HttpClient();
        var json = JsonSerializer.Serialize(new
        {
            ref_list = ref_generaters,
            generator = generatorHex,
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        client.Timeout = TimeSpan.FromSeconds(1000);

        var response = await client.PostAsync($"{TargetAddress}/parse_block", content);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        var coins = JsonSerializer.Deserialize<CoinInfoJson[]>(body);
        ulong ParseAmount(string amount)
            => string.IsNullOrWhiteSpace(amount) || amount == "()" ? 0
            : amount.StartsWith("0x") ? Convert.ToUInt64(amount.Unprefix0x(), 16)
            : ulong.Parse(amount);
        return coins
            .Select(_ => new CoinInfo(_.parent, _.puzzle, ParseAmount(_.amount), _.solution, _.coinname))
            .ToArray();
    }
}

