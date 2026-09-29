using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace WalletBackend.MigrationTests;

/// <summary>Retained boundary cases from the PostgreSQL-backed baseline.</summary>
public class LegacyEdgeCasesTests
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri((Environment.GetEnvironmentVariable("PAWKET_MIGRATION_URL")
            ?? "http://127.0.0.1:5058").TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(90),
    };
    private const string KnownHash = "0x5e203e8472a28befa0fa47a7a27cd38ba2d4f699e008f342cd3efea1d233b9c2";
    private const string Unknown = "0x0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public async Task MoreThanThreeHundredPuzzleHashesRetainsValidation()
    {
        using var response = await Client.PostAsJsonAsync("Wallet/records", new
        {
            puzzleHashes = Enumerable.Repeat(new string('0', 64), 301).ToArray(),
        });
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal("Valid puzzle hash number per request is 300", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MissingPuzzleRetainsError()
    {
        using var response = await Client.PostAsJsonAsync("Wallet/get-puzzle", new { parentCoinId = Unknown });
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal("Cannot find corresponding coin.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownSolutionIdReturnsEmptyArray()
    {
        using var body = await Post("Wallet/get-coin-solution", new { coinIds = new[] { Unknown } });
        Assert.Empty(body.RootElement.GetProperty("coinSpends").EnumerateArray());
    }

    [Fact]
    public async Task LegacySingleCoinSolutionRetainsShape()
    {
        using var body = await Post("Wallet/get-coin-solution", new
        {
            coinId = "0x4ebd64fa38da4aa0b27cd80d50b3358de6e542155bfe432fb212d1a63308c9e2",
        });
        Assert.False(body.RootElement.TryGetProperty("coinSpends", out _));
        Assert.Equal(1750000000000UL, body.RootElement.GetProperty("coinSpend")
            .GetProperty("coin").GetProperty("amount").GetUInt64());
    }

    [Fact]
    public async Task HintReturnsRealChild()
    {
        using var body = await Post("Wallet/records", new
        {
            puzzleHashes = new[] { "0x1a68b05cf7e480a16283ae003d405972b95f6bd53fbb12ff5b6b09f7e12a3352" },
            hint = true, includeSpentCoins = true,
        });
        Assert.Contains(body.RootElement.GetProperty("coins").EnumerateArray()
            .SelectMany(g => g.GetProperty("records").EnumerateArray()), r =>
            r.GetProperty("coin").GetProperty("name").GetString() ==
            "EFFC41C56DCC22D1CB3AAA60197BAE18DAAC0F521B2CE9751F91F7D543252167");
    }

    [Fact]
    public async Task MultiplePuzzleHashesKeepGrouping()
    {
        using var body = await Post("Wallet/records", new { puzzleHashes = new[] { KnownHash, Unknown }, pageLength = 1 });
        var groups = body.RootElement.GetProperty("coins").EnumerateArray().ToArray();
        Assert.Contains(groups, g => g.GetProperty("puzzleHash").GetString() == KnownHash);
        Assert.All(groups, g => Assert.Single(g.GetProperty("records").EnumerateArray()));
    }

    [Fact]
    public async Task PaginationReturnsDistinctDescendingCoins()
    {
        using var first = await Post("Wallet/records", new
        {
            puzzleHashes = new[] { KnownHash }, includeSpentCoins = false, pageStart = 0, pageLength = 1,
        });
        using var second = await Post("Wallet/records", new
        {
            puzzleHashes = new[] { KnownHash }, includeSpentCoins = false, pageStart = 1, pageLength = 1,
        });
        var firstRecord = Assert.Single(Assert.Single(first.RootElement.GetProperty("coins").EnumerateArray())
            .GetProperty("records").EnumerateArray());
        var secondRecord = Assert.Single(Assert.Single(second.RootElement.GetProperty("coins").EnumerateArray())
            .GetProperty("records").EnumerateArray());
        Assert.NotEqual(firstRecord.GetProperty("coin").GetProperty("name").GetString(),
            secondRecord.GetProperty("coin").GetProperty("name").GetString());
        Assert.True(firstRecord.GetProperty("confirmedBlockIndex").GetUInt32() >=
            secondRecord.GetProperty("confirmedBlockIndex").GetUInt32());
    }

    [Fact]
    public async Task MultipleCoinIdsOmitUnknown()
    {
        using var body = await Post("Wallet/get-coin-solution", new
        {
            coinIds = new[] { "0x4ebd64fa38da4aa0b27cd80d50b3358de6e542155bfe432fb212d1a63308c9e2", Unknown },
        });
        Assert.Single(body.RootElement.GetProperty("coinSpends").EnumerateArray());
    }

    [Fact]
    public async Task HistoricalAmount191CoinAndUnknownKeepArrayShape()
    {
        using var body = await Post("Wallet/get-coin-solution", new
        {
            coinIds = new[] { "0xb8893cceeb6cdb2281a43491ded917e38ecf4c5fb742e2c2af9425f416ecc0cd", Unknown },
        });
        var spend = Assert.Single(body.RootElement.GetProperty("coinSpends").EnumerateArray());
        Assert.Equal(191UL, spend.GetProperty("coin").GetProperty("amount").GetUInt64());
        Assert.StartsWith("0xff", spend.GetProperty("puzzle_reveal").GetString());
    }

    [Fact]
    public async Task UnknownBlockReturnsEmptyGroups()
    {
        using var body = await Post("Wallet/get-block", new { indexes = new[] { 99999999 } });
        Assert.Empty(body.RootElement.GetProperty("blocks").EnumerateArray());
        Assert.Empty(body.RootElement.GetProperty("refBlocks").EnumerateArray());
    }

    [Fact]
    public async Task DuplicateBlockReturnsOneBlockAndReference()
    {
        using var body = await Post("Wallet/get-block", new { indexes = new[] { 229001, 229001 } });
        Assert.Equal(229001UL, Assert.Single(body.RootElement.GetProperty("blocks").EnumerateArray())
            .GetProperty("index").GetUInt64());
        Assert.Equal(225698UL, Assert.Single(body.RootElement.GetProperty("refBlocks").EnumerateArray())
            .GetProperty("index").GetUInt64());
    }

    [Fact]
    public async Task GeneratorlessBlockStillReturnsEmptyGzipPayload()
    {
        using var body = await Post("Wallet/get-block", new { indexes = new[] { 1 } });
        var block = Assert.Single(body.RootElement.GetProperty("blocks").EnumerateArray());
        using var compressed = new MemoryStream(Convert.FromBase64String(block.GetProperty("generator").GetString()!));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        Assert.Equal(-1, gzip.ReadByte());
    }

    private static async Task<JsonDocument> Post(string route, object request)
    {
        using var response = await Client.PostAsJsonAsync(route, request);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{route}: {(int)response.StatusCode} {payload}");
        return JsonDocument.Parse(payload);
    }
}
