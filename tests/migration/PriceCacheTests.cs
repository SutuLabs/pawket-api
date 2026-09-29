using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WalletServer.Helpers;
using Xunit;

namespace WalletBackend.MigrationTests;

public class PriceCacheTests
{
    [Fact]
    public async Task ConcurrentRequestsStartOneExternalRefreshAndPersistIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pawket-price-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = 0;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync();
            Interlocked.Increment(ref received);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            await Task.Delay(200);
            const string body = "[{\"source\":\"test\",\"from\":\"XCH\",\"to\":\"USD\",\"price\":12.5,\"time\":\"2026-09-29T00:00:00\"}]";
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(bytes);
        });

        var settings = Options.Create(new AppSettings
        {
            PriceCachePath = Path.Combine(directory, "prices.sqlite"),
            PriceSourceUrl = $"http://127.0.0.1:{port}/prices",
            PriceRefreshMinutes = 10,
        });
        try
        {
            using (var cache = new PriceCacheService(NullLogger<PriceCacheService>.Instance, settings))
            {
                Assert.Empty(cache.GetLatestPrices());
                Parallel.For(0, 50, _ => cache.RefreshInBackgroundIfDue());
                await server.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(SpinWait.SpinUntil(() => cache.GetLatestPrices().Length == 1,
                    TimeSpan.FromSeconds(5)));
                Assert.Equal(1, Volatile.Read(ref received));
                Assert.Equal(12.5m, cache.GetLatestPrices()[0].Price);
            }
            using var reopened = new PriceCacheService(NullLogger<PriceCacheService>.Instance, settings);
            Assert.Equal(12.5m, reopened.GetLatestPrices()[0].Price);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }
}
