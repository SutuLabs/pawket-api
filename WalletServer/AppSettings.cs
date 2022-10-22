public class AppSettings
{
    public string Host { get; set; } = "localhost";
    public string Path { get; set; } = "";
    public uint Port { get; set; }
    public NetworkSettings Network { get; set; } = new NetworkSettings();
    public string? OnlineDbConnString { get; set; }
    public uint OnlineUserStaySeconds { get; set; } = 90;
    public string? PriceSourceUrl { get; set; }
    public string CnsCreatorPuzzleHash { get; set; } = "0x0eb720d9195ffe59684b62b12d54791be7ad3bb6207f5eb92e0e1b40ecbc1155";
}

public class NetworkSettings
{
    public string Name { get; set; } = "mainnet";
    public string OfferUploadTarget { get; set; } = "https://api.dexie.space/v1/offers";
    public string ChainId { get; set; } = "ccd5bb71183532bff220ba46c268991a3ff07eb358e8255a65c30a2dce0e5fbb";
    public string Symbol { get; set; } = "XCH";
    public string Prefix { get; set; } = "xch";
    public int Decimal { get; set; } = 12;
    public string ExplorerUrl { get; set; } = "https://www.spacescan.io/xch/";
}
