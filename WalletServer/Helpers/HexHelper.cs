namespace WalletServer.Helpers
{
    public static class HexHelper
    {
        public static string Unprefix0x(this string hex)
        {
            return hex.StartsWith("0x") ? hex[2..] : hex;
        }

        public static string Prefix0x(this string hex)
        {
            return hex.StartsWith("0x") ? hex : $"0x{hex}";
        }

        public static string ToHexWithPrefix0x(this byte[]? buff)
        {
            var hex = HexMate.Convert.ToHexString(buff ?? Array.Empty<byte>(), HexMate.HexFormattingOptions.Lowercase);
            return $"0x{hex}";
        }

        public static byte[] ToHexBytes(this string hex)
        {
            return HexMate.Convert.FromHexString(hex.Unprefix0x().AsSpan());
        }
    }
}