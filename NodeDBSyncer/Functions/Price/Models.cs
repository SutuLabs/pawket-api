namespace NodeDBSyncer.Functions.Price;

public record PriceEntity(string source, string from, string to, decimal price, DateTime time);

public record CoinBasePriceBase(CoinBasePriceData data);
public record CoinBasePriceData(string @base, string currency, CoinBasePriceDataPrices prices);
public record CoinBasePriceDataPrices(decimal latest, CoinBasePriceDataPrice latest_price);
public record CoinBasePriceDataPrice(CoinBasePriceDataPriceAmount amount, DateTime timestamp);
public record CoinBasePriceDataPriceAmount(decimal amount, string currency);

public record FtftxPriceData(long code, string info, decimal[][] data);
public record FtftxCurrencyRateData(long code, string info, Dictionary<string, decimal> data);
