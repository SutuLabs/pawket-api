using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace WalletBackend.BaselineTests;

public class LegacyApiContractTests
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri(
            (Environment.GetEnvironmentVariable("PAWKET_BASELINE_URL") ?? "http://127.0.0.1:5057")
                .TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(60),
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (var (manifest, snapshot) in new[]
        {
            ("cases.json", "static-snapshot.json"),
            ("data-cases.json", "data-snapshot.json"),
        })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(AssetPath(manifest)));
            foreach (var item in document.RootElement.EnumerateArray())
                yield return new object[] { manifest, snapshot, item.GetProperty("id").GetString()! };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ResponseMatchesFrozenBaseline(string manifest, string snapshot, string caseId)
    {
        using var cases = JsonDocument.Parse(File.ReadAllText(AssetPath(manifest)));
        using var baseline = JsonDocument.Parse(File.ReadAllText(AssetPath(snapshot)));
        var testCase = Find(cases.RootElement, "id", caseId);
        var saved = Find(baseline.RootElement, "case", caseId);
        AssertJsonEqual(testCase, saved.GetProperty("request"), $"{caseId}.request");

        using var request = new HttpRequestMessage(
            new HttpMethod(testCase.GetProperty("method").GetString()!),
            testCase.GetProperty("path").GetString()!.TrimStart('/'));
        if (testCase.TryGetProperty("body", out var requestBody))
            request.Content = new StringContent(requestBody.GetRawText(), Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var expected = saved.GetProperty("response");
        Assert.Equal(expected.GetProperty("status").GetInt32(), (int)response.StatusCode);

        using var actualBody = ParseBody(text);
        var actual = actualBody.RootElement.Clone();
        if (testCase.GetProperty("path").GetString() == "/Wallet/get-block"
            && response.IsSuccessStatusCode)
            actual = NormalizeBlockGenerators(actual);

        AssertJsonEqual(expected.GetProperty("body"), actual, $"{caseId}.body");
    }

    [Fact]
    public async Task RecordsHeightFieldsDoNotCurrentlyFilterResults()
    {
        const string hash = "0x5e203e8472a28befa0fa47a7a27cd38ba2d4f699e008f342cd3efea1d233b9c2";
        var basic = await PostJson("/Wallet/records", new { puzzleHashes = new[] { hash }, pageLength = 1 });
        var withHeights = await PostJson("/Wallet/records", new
        {
            puzzleHashes = new[] { hash }, pageLength = 1,
            startHeight = 999999999, endHeight = 1,
        });
        Assert.Equal(200, basic.Status);
        Assert.Equal(basic, withHeights);
    }

    [Fact]
    public async Task RecordsRejectMoreThanThreeHundredPuzzleHashes()
    {
        var hashes = Enumerable.Repeat(new string('0', 64), 301).ToArray();
        var result = await PostJson("/Wallet/records", new { puzzleHashes = hashes });
        Assert.Equal(400, result.Status);
        Assert.Equal("Valid puzzle hash number per request is 300", result.Body);
    }

    [Fact]
    public async Task PuzzleMissingCoinReturnsCurrentError()
    {
        var result = await PostJson("/Wallet/get-puzzle", new
        {
            parentCoinId = "0x" + new string('0', 64),
        });
        Assert.Equal(400, result.Status);
        Assert.Equal("Cannot find corresponding coin.", result.Body);
    }

    [Fact]
    public async Task CoinSolutionLegacyRequestKeepsSingleCoinSpendShape()
    {
        var result = await PostJson("/Wallet/get-coin-solution", new
        {
            coinId = "0xb8893cceeb6cdb2281a43491ded917e38ecf4c5fb742e2c2af9425f416ecc0cd",
        });
        Assert.Equal(200, result.Status);
        using var body = JsonDocument.Parse(result.Body);
        Assert.False(body.RootElement.TryGetProperty("coinSpends", out _));
        var spend = body.RootElement.GetProperty("coinSpend");
        Assert.Equal(191UL, spend.GetProperty("coin").GetProperty("amount").GetUInt64());
        Assert.Equal("", spend.GetProperty("puzzle_reveal").GetString());
        Assert.Equal("", spend.GetProperty("solution").GetString());
    }

    [Fact]
    public async Task CoinSolutionUnknownIdReturnsEmptyArray()
    {
        var result = await PostJson("/Wallet/get-coin-solution", new
        {
            coinIds = new[] { "0x" + new string('0', 64) },
        });
        Assert.Equal(200, result.Status);
        using var body = JsonDocument.Parse(result.Body);
        Assert.Equal(0, body.RootElement.GetProperty("coinSpends").GetArrayLength());
    }

    [Fact]
    public async Task SpentCoinHasRealPuzzleAndSolutionInBothEndpoints()
    {
        const string coinId = "0x4ebd64fa38da4aa0b27cd80d50b3358de6e542155bfe432fb212d1a63308c9e2";
        const string puzzleReveal = "0xff02ffff01ff02ffff01ff02ffff03ff0bffff01ff02ffff03ffff09ff05ffff1dff0bffff1effff0bff0bffff02ff06ffff04ff02ffff04ff17ff8080808080808080ffff01ff02ff17ff2f80ffff01ff088080ff0180ffff01ff04ffff04ff04ffff04ff05ffff04ffff02ff06ffff04ff02ffff04ff17ff80808080ff80808080ffff02ff17ff2f808080ff0180ffff04ffff01ff32ff02ffff03ffff07ff0580ffff01ff0bffff0102ffff02ff06ffff04ff02ffff04ff09ff80808080ffff02ff06ffff04ff02ffff04ff0dff8080808080ffff01ff0bffff0101ff058080ff0180ff018080ffff04ffff01b08e359d29671b4fc2f3b1683bc791879c3484bf2a2adbeebaa43c0fd16d64430a3767bdb4c91aec30e437cb01f12f50b1ff018080";

        var puzzleResponse = await PostJson("/Wallet/get-puzzle", new { parentCoinId = coinId });
        Assert.Equal(200, puzzleResponse.Status);
        using var puzzle = JsonDocument.Parse(puzzleResponse.Body);
        Assert.Equal(coinId, puzzle.RootElement.GetProperty("parentCoinId").GetString());
        Assert.Equal(1750000000000UL, puzzle.RootElement.GetProperty("amount").GetUInt64());
        Assert.Equal(puzzleReveal, puzzle.RootElement.GetProperty("puzzleReveal").GetString());

        var solutionResponse = await PostJson("/Wallet/get-coin-solution", new { coinIds = new[] { coinId } });
        Assert.Equal(200, solutionResponse.Status);
        using var solution = JsonDocument.Parse(solutionResponse.Body);
        var spend = Assert.Single(solution.RootElement.GetProperty("coinSpends").EnumerateArray());
        Assert.Equal(puzzleReveal, spend.GetProperty("puzzle_reveal").GetString());
        Assert.Equal("0xff80ffff0180ff8080", spend.GetProperty("solution").GetString());
        Assert.Equal(237759UL, spend.GetProperty("confirmed_index").GetUInt64());
        Assert.Equal(239000UL, spend.GetProperty("spent_index").GetUInt64());
    }

    [Fact]
    public async Task BlockUnknownHeightReturnsEmptyGroups()
    {
        var result = await PostJson("/Wallet/get-block", new { indexes = new[] { 99999999 } });
        Assert.Equal(200, result.Status);
        using var body = JsonDocument.Parse(result.Body);
        Assert.Equal(0, body.RootElement.GetProperty("blocks").GetArrayLength());
        Assert.Equal(0, body.RootElement.GetProperty("refBlocks").GetArrayLength());
    }

    [Fact]
    public async Task BlockDuplicateHeightReturnsOnlyOneBlock()
    {
        var result = await PostJson("/Wallet/get-block", new { indexes = new[] { 229001, 229001 } });
        Assert.Equal(200, result.Status);
        using var body = JsonDocument.Parse(result.Body);
        var block = Assert.Single(body.RootElement.GetProperty("blocks").EnumerateArray());
        Assert.Equal(229001UL, block.GetProperty("index").GetUInt64());
        Assert.Equal(225698U, Assert.Single(block.GetProperty("generator_ref_list").EnumerateArray()).GetUInt32());
        Assert.Equal(225698UL, Assert.Single(body.RootElement.GetProperty("refBlocks").EnumerateArray()).GetProperty("index").GetUInt64());
    }

    [Fact]
    public async Task BlockWithoutGeneratorStillReturnsGzipPayload()
    {
        var result = await PostJson("/Wallet/get-block", new { indexes = new[] { 1 } });
        Assert.Equal(200, result.Status);
        using var body = JsonDocument.Parse(result.Body);
        var block = Assert.Single(body.RootElement.GetProperty("blocks").EnumerateArray());
        Assert.Equal(1UL, block.GetProperty("index").GetUInt64());
        Assert.Equal(0, block.GetProperty("generator_ref_list").GetArrayLength());
        Assert.Equal(0, body.RootElement.GetProperty("refBlocks").GetArrayLength());
        using var compressed = new MemoryStream(Convert.FromBase64String(block.GetProperty("generator").GetString()!));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        Assert.Equal(-1, gzip.ReadByte());
    }

    [Theory]
    [InlineData("CatV2", "0x1a68b05cf7e480a16283ae003d405972b95f6bd53fbb12ff5b6b09f7e12a3352",
        "EFFC41C56DCC22D1CB3AAA60197BAE18DAAC0F521B2CE9751F91F7D543252167", 9000600U, 9000703U,
        4, "tailProgramHash", "0x46ef568a4ec5f5656adb630bb90ceabbd73557fbf654fcd1144d70f0642287aa")]
    [InlineData("NftV1", "0x82b7ad4c410fa706301cc4a1fc3cc0b34f1808813f8ab3a0e6774aed7b0f7131",
        "AC102D11A3B4073CA19856A368FB98BF0F7B5019DAF3E3D388FBD546D3B4F89F", 9000688U, 9007817U,
        19, "launcherId", "0x1746d5de2f1a5db40929070073aa15d82b39eceb9307e26b1da38ea84c3cbcd0")]
    [InlineData("DidV1", "0x82b7ad4c410fa706301cc4a1fc3cc0b34f1808813f8ab3a0e6774aed7b0f7131",
        "FD283BBD98369889395AC5C92DFC9B35D2B0E16D91B92E93541C61DD044C2F62", 9000688U, 9010330U,
        13, "launcherId", "0x0b67d60ed13ad40c9f9005e7b649bda88b884732d813b38b377b09de91c9b970")]
    public async Task RecordsCoinTypeReturnsRealClassAndAnalysis(
        string coinType, string hint, string expectedCoin, uint confirmed, uint spent,
        int analysisFieldCount, string analysisField, string analysisValue)
    {
        var result = await PostJson("/Wallet/records", new
        {
            puzzleHashes = new[] { hint }, coinType, includeAnalysis = true,
            includeSpentCoins = true, pageLength = 5,
        });
        Assert.Equal(200, result.Status);
        using var body = JsonDocument.Parse(result.Body);
        var group = Assert.Single(body.RootElement.GetProperty("coins").EnumerateArray());
        Assert.Equal(hint, group.GetProperty("puzzleHash").GetString());
        Assert.False(group.TryGetProperty("balance", out _));
        Assert.False(group.TryGetProperty("balanceInfo", out _));
        var record = Assert.Single(group.GetProperty("records").EnumerateArray());
        Assert.Equal(expectedCoin, record.GetProperty("coin").GetProperty("name").GetString());
        Assert.Equal(confirmed, record.GetProperty("confirmedBlockIndex").GetUInt32());
        Assert.Equal(spent, record.GetProperty("spentBlockIndex").GetUInt32());
        var analysis = record.GetProperty("analysis");
        Assert.Equal(analysisFieldCount, analysis.EnumerateObject().Count());
        Assert.Equal(analysisValue, analysis.GetProperty(analysisField).GetString());
    }

    private static async Task<(int Status, string Body)> PostJson(string path, object body)
    {
        using var response = await Client.PostAsJsonAsync(path.TrimStart('/'), body);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string AssetPath(string name) => Path.Combine(AppContext.BaseDirectory, name);

    private static JsonElement Find(JsonElement array, string property, string value)
    {
        foreach (var item in array.EnumerateArray())
            if (item.GetProperty(property).GetString() == value)
                return item;
        throw new InvalidOperationException($"Missing {property}={value} in baseline assets.");
    }

    private static JsonDocument ParseBody(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { return JsonDocument.Parse(JsonSerializer.Serialize(text)); }
    }

    private static JsonElement NormalizeBlockGenerators(JsonElement body)
    {
        var normalized = JsonNode.Parse(body.GetRawText())!;
        foreach (var group in new[] { "blocks", "refBlocks" })
        {
            if (normalized[group] is not JsonArray blocks) continue;
            foreach (var block in blocks)
            {
                var encoded = block!["generator"]!.GetValue<string>();
                using var compressed = new MemoryStream(Convert.FromBase64String(encoded));
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var plain = new MemoryStream();
                gzip.CopyTo(plain);
                block["generator"] = "gzip-content-sha256:"
                    + Convert.ToHexString(SHA256.HashData(plain.ToArray())).ToLowerInvariant();
            }
        }
        using var document = JsonDocument.Parse(normalized.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void AssertJsonEqual(JsonElement expected, JsonElement actual, string path)
    {
        Assert.True(expected.ValueKind == actual.ValueKind,
            $"{path}: expected {expected.ValueKind}, got {actual.ValueKind}");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedProperties = expected.EnumerateObject().ToArray();
                Assert.Equal(expectedProperties.Length, actual.EnumerateObject().Count());
                foreach (var property in expectedProperties)
                {
                    Assert.True(actual.TryGetProperty(property.Name, out var value),
                        $"{path}: missing {property.Name}");
                    AssertJsonEqual(property.Value, value, $"{path}.{property.Name}");
                }
                break;
            case JsonValueKind.Array:
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (var index = 0; index < expected.GetArrayLength(); index++)
                    AssertJsonEqual(expected[index], actual[index], $"{path}[{index}]");
                break;
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;
            case JsonValueKind.Number:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(expected.GetBoolean(), actual.GetBoolean());
                break;
        }
    }
}
