public class AppSettings
{
    public string Host { get; set; } = "localhost";
    public string Path { get; set; } = "";
    public uint Port { get; set; }
    public NetworkEnum? Network { get; set; }
    public string? OnlineDbConnString { get; set; }
    public uint OnlineUserStaySeconds { get; set; } = 90;
    public string? PriceSourceUrl { get; set; }
}

public enum NetworkEnum
{
    mainnet,
    testnet,
}
