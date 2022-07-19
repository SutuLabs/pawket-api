namespace WalletServer.Helpers
{
    public static class Misc
    {
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
