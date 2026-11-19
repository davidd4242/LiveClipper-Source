using Microsoft.Web.WebView2.Core;

namespace LiveClipper.Desktop;

/// <summary>从 WebView2 的 Cookie 容器里读出 B 站登录 Cookie（软件内置登录与自动同步共用）。</summary>
internal static class BilibiliCookies
{
    private static readonly string[] Urls=
    [
        "https://www.bilibili.com","https://passport.bilibili.com","https://api.bilibili.com","https://live.bilibili.com"
    ];
    public static async Task<string?> ReadAsync(CoreWebView2 core)
    {
        var cookies=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var url in Urls)
            foreach(var cookie in await core.CookieManager.GetCookiesAsync(url))
                if(!string.IsNullOrEmpty(cookie.Name))cookies[cookie.Name]=cookie.Value;
        return cookies.TryGetValue("SESSDATA",out var sess)&&!string.IsNullOrEmpty(sess)
            ?string.Join("; ",cookies.Select(x=>$"{x.Key}={x.Value}"))
            :null;
    }
}
