using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;

namespace WalletServer.Helpers;

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

    public static string GetReferer(this HttpContext httpContext, ILogger? logger = null)
    {
        try
        {
            var referer = httpContext.Request.Headers.Referer;
            var r = StringValues.IsNullOrEmpty(referer) ? null : (string)referer;

            if (r != null) return SimplifyReferer(r);

            // iOS don't send referer, we collect user agent instead
            var agent = httpContext.Request.Headers.UserAgent;
            var a = StringValues.IsNullOrEmpty(agent) ? null : (string)agent;

            if (a != null) return SimplifyAgent(a);

            return "UNKNOWN";
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "caught exception when getting referer");
            return "EXCEPTION";
        }
    }

    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> source) where T : class
    {
        foreach (var item in source)
        {
            if (item is not null) yield return item;
        }
    }

    private static string SimplifyReferer(string referer)
    {
        try
        {
            var uri = new Uri(referer);
            var portHint = uri.Port == 443 ? "" : $":{uri.Port}";
            var hostHint = uri.Host switch
            {
                var s when s.EndsWith(".xch.cool") => "*.xch.cool",
                var s => s,
            };
            return $"{hostHint}{portHint}";
        }
        catch (Exception)
        {
            return referer;
        }
    }

    private static string SimplifyAgent(string agent)
    {
        try
        {
            var reIos = new Regex(@"(?<type>iPad|iPhone|iphone|iPod).*?(OS |os |OS_)((?<version>\d+)((_|\.)\d)?((_|\.)\d)?)");
            var match = reIos.Match(agent);
            if (match.Success)
            {
                var type = match.Groups["type"].Value;
                var version = match.Groups["version"].Value;
                return $"{type}{version}";
            }

            return agent;
        }
        catch (Exception)
        {
            return agent;
        }
    }
}