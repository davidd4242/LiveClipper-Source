using LiveClipper.Core;

public static class AiConnectionResolver
{
    public static AiConnection Resolve(AiConnection requested,LiveSettings settings)
    {
        if(!string.IsNullOrWhiteSpace(requested.ApiKey))return requested;
        if(!string.Equals(requested.Provider,settings.AiProvider,StringComparison.OrdinalIgnoreCase))return requested;
        var savedEndpoint=settings.AiEndpoint;
        var normalizedEndpoint=AiAnalysisService.NormalizeEndpoint(savedEndpoint);
        if(normalizedEndpoint is null||!string.Equals(normalizedEndpoint,AiAnalysisService.NormalizeEndpoint(requested.Endpoint),StringComparison.Ordinal))return requested;
        var storedKey=settings.AiApiKey;
        if(string.IsNullOrWhiteSpace(storedKey))return requested;
        // 已保存配置未完成（缺模型）时不借旧 Key 生成；不改写 requested.Model 来间接拦截。
        if(string.IsNullOrWhiteSpace(settings.AiModel))return requested;
        string key;
        try{key=CookieProtector.Unprotect(storedKey);}
        catch{throw new InvalidOperationException("已保存的 API Key 无法解密，请在设置中重新输入");}
        // 只补凭据：不替换请求里的地址与模型。
        return string.IsNullOrWhiteSpace(key)?requested:requested with {ApiKey=key};
    }
}
