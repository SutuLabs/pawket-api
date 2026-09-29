using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace WalletBackend.MigrationTests;

public class RecordsCursorTests
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri((Environment.GetEnvironmentVariable("PAWKET_MIGRATION_URL")
            ?? "http://127.0.0.1:5058").TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(90),
    };
    private const string LargeHint = "0x82b7ad4c410fa706301cc4a1fc3cc0b34f1808813f8ab3a0e6774aed7b0f7131";

    [Theory]
    [InlineData("xyz")]
    [InlineData("0x00")]
    [InlineData("")]
    public async Task InvalidHashReturnsBadRequest(string hash)
    {
        using var coins = await Client.PostAsJsonAsync("Wallet/coins", new { puzzleHash = hash });
        Assert.Equal(400, (int)coins.StatusCode);
        using var records = await Client.PostAsJsonAsync("Wallet/records", new { puzzleHashes = new[] { hash } });
        Assert.Equal(400, (int)records.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"PuzzleHash\":null}")]
    public async Task IncompleteCursorReturnsBadRequest(string json)
    {
        var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        using var response = await Client.PostAsJsonAsync("Wallet/coins", new { puzzleHash = LargeHint, cursor });
        Assert.Equal(400, (int)response.StatusCode);
    }

    [Fact]
    public async Task OversizedCursorReturnsBadRequest()
    {
        using var response = await Client.PostAsJsonAsync("Wallet/coins", new
        {
            puzzleHash = LargeHint, cursor = new string('A', 4097),
        });
        Assert.Equal(400, (int)response.StatusCode);
    }

    [Fact]
    public async Task OldPageRouteIsNotExposed()
    {
        using var response = await Client.PostAsJsonAsync("Wallet/records-page", new { puzzleHash = LargeHint });
        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task CursorRetrievesEveryLargeHintCoinWithoutDuplicates()
    {
        string? cursor = null;
        long? snapshot = null;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = 0;
        do
        {
            using var response = await Client.PostAsJsonAsync("Wallet/coins", new
            {
                puzzleHash = LargeHint, hint = true, includeSpentCoins = true,
                pageLength = 1000, cursor,
            });
            var payload = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"page {pages}: {(int)response.StatusCode} {payload}");
            using var body = JsonDocument.Parse(payload);
            var height = body.RootElement.GetProperty("peekHeight").GetInt64();
            snapshot ??= height;
            Assert.Equal(snapshot, height);
            foreach (var group in body.RootElement.GetProperty("coins").EnumerateArray())
            {
                Assert.Equal(LargeHint, group.GetProperty("puzzleHash").GetString());
                Assert.False(group.TryGetProperty("balance", out _));
                Assert.False(group.TryGetProperty("balanceInfo", out _));
                foreach (var record in group.GetProperty("records").EnumerateArray())
                {
                    Assert.True(record.GetProperty("confirmedBlockIndex").GetUInt32() <= height);
                    Assert.False(record.TryGetProperty("analysis", out _));
                    Assert.True(ids.Add(record.GetProperty("coin").GetProperty("name").GetString()!));
                }
            }
            cursor = body.RootElement.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            pages++;
            Assert.True(pages < 20, "Cursor failed to terminate");
        } while (cursor != null);
        Assert.True(ids.Count > 8000, $"Only {ids.Count} large-hint coins returned");
    }

    [Fact]
    public async Task CursorRejectsChangedQuery()
    {
        using var first = await Client.PostAsJsonAsync("Wallet/coins", new
        {
            puzzleHash = LargeHint, hint = true, includeSpentCoins = true, pageLength = 1,
        });
        Assert.True(first.IsSuccessStatusCode);
        using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var cursor = body.RootElement.GetProperty("nextCursor").GetString();
        using var changed = await Client.PostAsJsonAsync("Wallet/coins", new
        {
            puzzleHash = LargeHint, hint = false, includeSpentCoins = true, pageLength = 1, cursor,
        });
        Assert.Equal(400, (int)changed.StatusCode);
    }

    [Fact]
    public async Task CursorHonorsConfirmationHeightFilter()
    {
        using var response = await Client.PostAsJsonAsync("Wallet/coins", new
        {
            puzzleHash = LargeHint, hint = true, includeSpentCoins = true,
            startHeight = 9000688, endHeight = 9000689, pageLength = 1000,
        });
        Assert.True(response.IsSuccessStatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var records = body.RootElement.GetProperty("coins").EnumerateArray()
            .SelectMany(g => g.GetProperty("records").EnumerateArray()).ToArray();
        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.Equal(9000688U, r.GetProperty("confirmedBlockIndex").GetUInt32()));
    }
}
