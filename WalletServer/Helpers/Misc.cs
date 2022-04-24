namespace WalletServer.Helpers
{
    public static class Misc
    {
        public static string Unprefix0x(this string hex)
        {
            return hex.StartsWith("0x") ? hex[2..] : hex;
        }

        public static string GetRealIp(this HttpContext httpContext)
        {
            if (httpContext.Request.Headers.TryGetValue("X-Real-IP", out var realIp) && !string.IsNullOrWhiteSpace(realIp))
            {
                return realIp;
            }

            return httpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        }
    }
}
