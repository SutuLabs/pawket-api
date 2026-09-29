using System.IO.Compression;
using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace WalletBackend.MigrationTests;

/// <summary>Replay every retained legacy snapshot request against the Chia-backed API.</summary>
public class RetainedBaselineCasesTests
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri((Environment.GetEnvironmentVariable("PAWKET_MIGRATION_URL")
            ?? "http://127.0.0.1:5058").TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(90),
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (var file in new[] { "cases.json", "data-cases.json" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Asset(file)));
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var path = item.GetProperty("path").GetString()!;
                if (path.StartsWith("/Name/") || path.StartsWith("/Inscription/")) continue;
                yield return new object[] { file, item.GetProperty("id").GetString()! };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RetainedLegacyRequestPreservesContract(string file, string id)
    {
        using var cases = JsonDocument.Parse(File.ReadAllText(Asset(file)));
        var item = cases.RootElement.EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        using var request = new HttpRequestMessage(new HttpMethod(item.GetProperty("method").GetString()!),
            item.GetProperty("path").GetString()!.TrimStart('/'));
        if (item.TryGetProperty("body", out var body))
            request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        if (file == "cases.json")
        {
            using var snapshots = JsonDocument.Parse(File.ReadAllText(Asset("static-snapshot.json")));
            var expected = snapshots.RootElement.EnumerateArray().Single(c => c.GetProperty("case").GetString() == id)
                .GetProperty("response");
            if (id == "records-invalid-type")
            {
                Assert.Equal(200, (int)response.StatusCode);
                using var retired = JsonDocument.Parse(payload);
                Assert.Empty(retired.RootElement.GetProperty("coins").EnumerateArray());
                return;
            }
            Assert.Equal(expected.GetProperty("status").GetInt32(), (int)response.StatusCode);
            if (id is "solution-missing-id" or "block-missing-indexes")
                Assert.Equal(expected.GetProperty("body").GetString(), payload);
            else if (id == "version")
                Assert.Equal(expected.GetProperty("body").GetString(), payload);
            else
            {
                using var actual = JsonDocument.Parse(payload);
                AssertJsonEqual(expected.GetProperty("body"), actual.RootElement, id);
            }
            return;
        }

        Assert.True(response.IsSuccessStatusCode, $"{id}: {(int)response.StatusCode} {payload}");
        using var result = JsonDocument.Parse(payload);
        var root = result.RootElement;
        switch (id)
        {
            case "records-unknown-puzzle":
                Assert.Empty(root.GetProperty("coins").EnumerateArray());
                break;
            case "records-hint-unmatched-in-slice":
                // The old partial database could be empty while the current chain has matches.
                Assert.Equal(JsonValueKind.Array, root.GetProperty("coins").ValueKind);
                break;
            case "records-spent-coin":
            case "records-unspent-filter":
            case "records-unspent-page-0":
            case "records-unspent-page-1":
                Assert.True(root.GetProperty("peekHeight").GetUInt64() > 0);
                foreach (var group in root.GetProperty("coins").EnumerateArray())
                {
                    Assert.Equal(item.GetProperty("body").GetProperty("puzzleHashes")[0].GetString(),
                        group.GetProperty("puzzleHash").GetString());
                    var records = group.GetProperty("records").EnumerateArray().ToArray();
                    Assert.InRange(records.Length, 1, item.GetProperty("body").GetProperty("pageLength").GetInt32());
                    foreach (var coin in records)
                        if (id != "records-spent-coin") Assert.False(coin.GetProperty("spent").GetBoolean());
                }
                break;
            case "coin-solution-spent":
                var spend = Assert.Single(root.GetProperty("coinSpends").EnumerateArray());
                using (var snapshots = JsonDocument.Parse(File.ReadAllText(Asset("data-snapshot.json"))))
                {
                    var old = snapshots.RootElement.EnumerateArray()
                        .Single(c => c.GetProperty("case").GetString() == id)
                        .GetProperty("response").GetProperty("body").GetProperty("coinSpends")[0];
                    Assert.Equal(old.EnumerateObject().Count(), spend.EnumerateObject().Count());
                    foreach (var field in new[] { "coin", "confirmed_index", "spent_index", "timestamp" })
                        AssertJsonEqual(old.GetProperty(field), spend.GetProperty(field), id + "." + field);
                }
                Assert.StartsWith("0xff", spend.GetProperty("puzzle_reveal").GetString());
                Assert.StartsWith("0xff", spend.GetProperty("solution").GetString());
                break;
            case "block-with-generator":
                using (var snapshots = JsonDocument.Parse(File.ReadAllText(Asset("data-snapshot.json"))))
                {
                    var expected = snapshots.RootElement.EnumerateArray()
                        .Single(c => c.GetProperty("case").GetString() == id)
                        .GetProperty("response").GetProperty("body");
                    AssertJsonEqual(expected, NormalizeBlockGenerators(root), id);
                }
                break;
            default:
                throw new InvalidOperationException($"Unreviewed retained baseline case: {id}");
        }
    }

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, name);

    private static JsonElement NormalizeBlockGenerators(JsonElement body)
    {
        var normalized = JsonNode.Parse(body.GetRawText())!;
        foreach (var group in new[] { "blocks", "refBlocks" })
        {
            foreach (var block in (JsonArray)normalized[group]!)
            {
                using var compressed = new MemoryStream(Convert.FromBase64String(block!["generator"]!.GetValue<string>()));
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
        Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: JSON kind differs");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.Equal(expected.EnumerateObject().Count(), actual.EnumerateObject().Count());
                foreach (var field in expected.EnumerateObject())
                {
                    Assert.True(actual.TryGetProperty(field.Name, out var value), $"{path}: missing {field.Name}");
                    AssertJsonEqual(field.Value, value, path + "." + field.Name);
                }
                break;
            case JsonValueKind.Array:
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (var i = 0; i < expected.GetArrayLength(); i++)
                    AssertJsonEqual(expected[i], actual[i], $"{path}[{i}]");
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
