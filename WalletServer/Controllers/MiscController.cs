using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Prometheus;
using WalletServer.Helpers;

namespace WalletServer.Controllers;

[ApiController]
[Route("[controller]")]
public class MiscController : ControllerBase
{
    private readonly ILogger<MiscController> logger;
    private readonly PriceCacheService priceCache;

    private static readonly Counter RequestPriceCount = Metrics.CreateCounter("request_price_total", "Number of Price request.");
    private static readonly Counter RequestTailDbCount = Metrics.CreateCounter("request_taildb_total", "Number of taildb request.");

    public MiscController(ILogger<MiscController> logger, PriceCacheService priceCache)
    {
        this.logger = logger;
        this.priceCache = priceCache;
    }

    public record PriceResponse(string Source, string From, string To, decimal Price, DateTime Time);

    [HttpGet("prices")]
    public async Task<IActionResult> GetPrice()
    {
        RequestPriceCount.Inc();

        var prices = this.priceCache.GetLatestPrices();
        this.priceCache.RefreshInBackgroundIfDue();
        return this.Ok(prices);
    }

    [HttpGet("taildb")]
    public IActionResult GetTailDb()
    {
        RequestTailDbCount.Inc();

        using var fs = System.IO.File.OpenRead("tails.json");
        using var sr = new StreamReader(fs);
        var bytes = sr.ReadToEnd();
        return this.Content(bytes, "application/json");
    }

    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        var fileVersion = typeof(Program)
            .GetTypeInfo()
            .Assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
            .Version ?? "-1";
        return this.Ok(fileVersion);
    }

}

public record TailEntity(
    string id,
    string name,
    string code,
    string description,
    string category,
    string uri);
