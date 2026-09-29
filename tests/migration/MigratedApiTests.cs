using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace WalletBackend.MigrationTests;

public class MigratedApiTests
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri((Environment.GetEnvironmentVariable("PAWKET_MIGRATION_URL")
            ?? "http://127.0.0.1:5058").TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(90),
    };

    private const string SpentCoin = "0x4ebd64fa38da4aa0b27cd80d50b3358de6e542155bfe432fb212d1a63308c9e2";
    private const string CatHint = "0x1a68b05cf7e480a16283ae003d405972b95f6bd53fbb12ff5b6b09f7e12a3352";
    private const string CatCoin = "EFFC41C56DCC22D1CB3AAA60197BAE18DAAC0F521B2CE9751F91F7D543252167";

    [Fact]
    public async Task PricesKeepProductionCurrenciesAndShape()
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "production-prices-2026-09-29.json")));
        using var response = await Client.GetAsync("Misc/prices");
        Assert.True(response.IsSuccessStatusCode);
        using var actual = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var expectedCurrencies = baseline.RootElement.EnumerateArray()
            .Select(p => p.GetProperty("to").GetString()).ToArray();
        var rows = actual.RootElement.EnumerateArray().ToArray();
        Assert.Equal(expectedCurrencies, rows.Select(p => p.GetProperty("to").GetString()).ToArray());
        foreach (var row in rows)
        {
            Assert.Equal("XCH", row.GetProperty("from").GetString());
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("source").GetString()));
            Assert.True(row.GetProperty("price").GetDecimal() > 0);
            Assert.True(row.GetProperty("time").GetDateTime() > DateTime.MinValue);
            Assert.Equal(5, row.EnumerateObject().Count());
        }
    }

    [Fact]
    public async Task SpentPuzzleAndSolutionRemainAvailable()
    {
        using var puzzle = await Post("Wallet/get-puzzle", new { parentCoinId = SpentCoin });
        Assert.Equal(SpentCoin, puzzle.RootElement.GetProperty("parentCoinId").GetString());
        Assert.Equal(1750000000000UL, puzzle.RootElement.GetProperty("amount").GetUInt64());
        Assert.StartsWith("0xff02", puzzle.RootElement.GetProperty("puzzleReveal").GetString());

        using var solution = await Post("Wallet/get-coin-solution", new { coinIds = new[] { SpentCoin } });
        var spend = Assert.Single(solution.RootElement.GetProperty("coinSpends").EnumerateArray());
        Assert.Equal(237759U, spend.GetProperty("confirmed_index").GetUInt32());
        Assert.Equal(239000U, spend.GetProperty("spent_index").GetUInt32());
        Assert.Equal(puzzle.RootElement.GetProperty("puzzleReveal").GetString(),
            spend.GetProperty("puzzle_reveal").GetString());
        Assert.Equal("0xff80ffff0180ff8080", spend.GetProperty("solution").GetString());
    }

    [Fact]
    public async Task ConfirmationHeightBoundsExcludeCoin()
    {
        using var puzzleResponse = await Client.PostAsJsonAsync("Wallet/get-puzzle", new
        {
            parentCoinId = SpentCoin, startHeight = 237760,
        });
        Assert.Equal(400, (int)puzzleResponse.StatusCode);
        Assert.Equal("Cannot find corresponding coin.", await puzzleResponse.Content.ReadAsStringAsync());

        using var solution = await Post("Wallet/get-coin-solution", new
        {
            coinIds = new[] { SpentCoin }, startHeight = 237760,
        });
        Assert.Empty(solution.RootElement.GetProperty("coinSpends").EnumerateArray());
    }

    [Fact]
    public async Task ClassAnalysisIsStableAcrossRepeatedRequests()
    {
        var request = new
        {
            puzzleHashes = new[] { CatHint }, coinType = "CatV2", includeAnalysis = true,
            includeSpentCoins = true, pageLength = 100,
        };
        for (var i = 0; i < 2; i++)
        {
            using var body = await Post("Wallet/records", request);
            var group = Assert.Single(body.RootElement.GetProperty("coins").EnumerateArray());
            Assert.False(group.TryGetProperty("balance", out _));
            var sample = group.GetProperty("records").EnumerateArray().FirstOrDefault(r =>
                r.GetProperty("coin").GetProperty("name").GetString() == CatCoin);
            Assert.Equal(CatCoin, sample.GetProperty("coin").GetProperty("name").GetString());
            Assert.Equal("0x46ef568a4ec5f5656adb630bb90ceabbd73557fbf654fcd1144d70f0642287aa",
                sample.GetProperty("analysis").GetProperty("tailProgramHash").GetString());
        }
    }

    [Theory]
    [InlineData("NftV1", "AC102D11A3B4073CA19856A368FB98BF0F7B5019DAF3E3D388FBD546D3B4F89F",
        "0x1746d5de2f1a5db40929070073aa15d82b39eceb9307e26b1da38ea84c3cbcd0")]
    [InlineData("DidV1", "FD283BBD98369889395AC5C92DFC9B35D2B0E16D91B92E93541C61DD044C2F62",
        "0x0b67d60ed13ad40c9f9005e7b649bda88b884732d813b38b377b09de91c9b970")]
    public async Task NftAndDidAnalysisSurviveHeightFilter(string coinType, string coinId, string launcherId)
    {
        using var body = await Post("Wallet/records", new
        {
            puzzleHashes = new[] { "0x82b7ad4c410fa706301cc4a1fc3cc0b34f1808813f8ab3a0e6774aed7b0f7131" },
            coinType, includeAnalysis = true, includeSpentCoins = true,
            startHeight = 9000688, endHeight = 9000689, pageLength = 100,
        });
        var group = Assert.Single(body.RootElement.GetProperty("coins").EnumerateArray());
        var sample = group.GetProperty("records").EnumerateArray().FirstOrDefault(r =>
            r.GetProperty("coin").GetProperty("name").GetString() == coinId);
        Assert.Equal(9000688U, sample.GetProperty("confirmedBlockIndex").GetUInt32());
        Assert.Equal(launcherId, sample.GetProperty("analysis").GetProperty("launcherId").GetString());
    }

    [Fact]
    public async Task BlocksKeepOrderReferencesAndGzip()
    {
        using var body = await Post("Wallet/get-block", new { indexes = new[] { 229001, 229003, 238292 } });
        var blocks = body.RootElement.GetProperty("blocks").EnumerateArray().ToArray();
        Assert.Equal(new ulong[] { 238292, 229003, 229001 }, blocks.Select(b => b.GetProperty("index").GetUInt64()));
        Assert.NotEmpty(body.RootElement.GetProperty("refBlocks").EnumerateArray());
        foreach (var block in blocks)
        {
            var compressed = Convert.FromBase64String(block.GetProperty("generator").GetString()!);
            using var gzip = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            gzip.CopyTo(plain);
            Assert.True(plain.Length > 0);
        }
    }

    [Theory]
    [InlineData("Name/recent")]
    [InlineData("Inscription/ticks")]
    [InlineData("Wallet/analysis")]
    public async Task DeprecatedRoutesAreAbsent(string route)
    {
        using var response = await Client.PostAsJsonAsync(route, new { puzzleHashes = Array.Empty<string>() });
        Assert.Equal(404, (int)response.StatusCode);
    }

    private static async Task<JsonDocument> Post(string route, object request)
    {
        using var response = await Client.PostAsJsonAsync(route, request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{route}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }
}
