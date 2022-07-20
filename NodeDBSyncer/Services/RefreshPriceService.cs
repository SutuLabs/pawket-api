namespace NodeDBSyncer;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodeDBSyncer.Helpers;

internal class RefreshPriceService : BaseRefreshService
{
    private readonly AppSettings appSettings;
    private readonly PersistentHelper persistentHelper;

    public RefreshPriceService(
        ILogger<RefreshPriceService> logger,
        PersistentHelper persistentHelper,
        IOptions<AppSettings> appSettings)
        : base(logger, nameof(RefreshPriceService), 2, 60, 10)
    {
        this.appSettings = appSettings.Value;
        this.persistentHelper = persistentHelper;
    }

    protected override async Task DoWorkAsync(CancellationToken token)
    {
        var ps = this.appSettings.PriceSource?.ToLower();
        var prices = ps == "ftx"
            ? GetFtftxPrices().ToArray()
            : GetCoinBasePrices().ToArray();

        if (token.IsCancellationRequested)
        {
            this.logger.LogInformation($"Refresh work cancelled.");
        }
        else
        {
            await this.persistentHelper.PersistentPrice(prices.ToArray());
        }
    }

    private IEnumerable<PriceEntity> GetCoinBasePrices()
    {
        var urls = new[] {
                "https://www.coinbase.com/api/v2/assets/prices/chia-network?base=USDT",
                "https://www.coinbase.com/api/v2/assets/prices/chia-network?base=CNY",
            };
        using WebClient wc = new WebClient();
        if (!string.IsNullOrWhiteSpace(this.appSettings.PriceProxy))
        {
            wc.Proxy = new WebProxy(this.appSettings.PriceProxy);
        }

        foreach (var url in urls)
        {
            var str = wc.DownloadString(url);

            var priceBase = Newtonsoft.Json.JsonConvert.DeserializeObject<CoinBasePriceBase>(str);
            var d = priceBase.data;
            var from = d.@base;
            var to = d.currency;
            var price = d.prices.latest;
            var time = d.prices.latest_price.timestamp;
            var entity = new PriceEntity("coinbase", from, to, price, time);
            yield return entity;
        }
    }

    private IEnumerable<PriceEntity> GetFtftxPrices()
    {
        var rate = GetFtftxCnyUsdRate();
        var urls = new[] {
                //"https://pc.ftftx.com/pair/market/kline?pairId=510514&type=6",//Huobi
                "https://pc.ftftx.com/pair/market/kline?pairId=510029&type=6",//OKX
            };
        using WebClient wc = new();
        if (!string.IsNullOrWhiteSpace(this.appSettings.PriceProxy))
        {
            wc.Proxy = new WebProxy(this.appSettings.PriceProxy);
        }


        foreach (var url in urls)
        {
            var str = wc.DownloadString(url);

            var priceBase = Newtonsoft.Json.JsonConvert.DeserializeObject<FtftxPriceData>(str);
            var d = priceBase.data.Last();
            var from = "XCH";
            var to = "USDT";
            var price = d[1];
            var time = DateTimeOffset.FromUnixTimeMilliseconds((long)d[0]).UtcDateTime;
            var entity = new PriceEntity("ftftx", from, to, price, time);
            yield return entity;

            if (rate.HasValue)
            {
                yield return new PriceEntity("ftftx", from, "CNY", price * rate.Value, time);
            }
        }
    }

    private decimal? GetFtftxCnyUsdRate()
    {
        try
        {
            var url = "https://pc.ftftx.com/currencyRate/list";
            using WebClient wc = new();
            if (!string.IsNullOrWhiteSpace(this.appSettings.PriceProxy))
            {
                wc.Proxy = new WebProxy(this.appSettings.PriceProxy);
            }
            var str = wc.DownloadString(url);
            var rate = Newtonsoft.Json.JsonConvert.DeserializeObject<FtftxCurrencyRateData>(str);
            return rate.data.TryGetValue("USD", out var value) ? value : null;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "cannot get rate");
            return null;
        }
    }
}

public record PriceEntity(string source, string from, string to, decimal price, DateTime time);

public record CoinBasePriceBase(CoinBasePriceData data);
public record CoinBasePriceData(string @base, string currency, CoinBasePriceDataPrices prices);
public record CoinBasePriceDataPrices(decimal latest, CoinBasePriceDataPrice latest_price);
public record CoinBasePriceDataPrice(CoinBasePriceDataPriceAmount amount, DateTime timestamp);
public record CoinBasePriceDataPriceAmount(decimal amount, string currency);

public record FtftxPriceData(long code, string info, decimal[][] data);
public record FtftxCurrencyRateData(long code, string info, Dictionary<string, decimal> data);