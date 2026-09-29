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
