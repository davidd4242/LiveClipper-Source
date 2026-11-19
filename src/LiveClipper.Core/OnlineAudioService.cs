using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace LiveClipper.Core;

public sealed record OnlineAudio(string Id,string Title,string Creator,string Source,string Kind,double Seconds,string License,string LicenseUrl,string LandingUrl,string AudioUrl,string Attribution);
public sealed record OnlineAudioSearch(int Page,int PageCount,int Total,string Query,IReadOnlyList<OnlineAudio> Results);
public sealed class OnlineAudioService(StudioStore studio,AudioLibraryService library,MediaTools media)
{
    private static readonly HttpClient Client=CreateClient();
    private readonly ConcurrentDictionary<string,(OnlineAudio Item,DateTimeOffset At)> _results=new();
    private readonly ConcurrentDictionary<string,(OnlineAudioSearch Result,DateTimeOffset At)> _searchCache=new();
    private readonly SemaphoreSlim _downloads=new(2,2);
    private static HttpClient CreateClient(){var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(45)};client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveClipper/0.2");return client;}
    private static string Text(JsonElement item,string name)=>item.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    public static string TranslateQuery(string text)=>text.Trim() switch
    {
        "钢琴"=>"piano","轻快"=>"upbeat","舒缓"=>"relaxing","悬念"=>"suspense","搞笑"=>"funny","紧张"=>"tension","电子"=>"electronic","雨声"=>"rain","掌声"=>"applause","转场"=>"whoosh","爆炸"=>"explosion","点击"=>"click","笑声"=>"laugh","胜利"=>"victory","恐怖"=>"horror","背景音乐"=>"instrumental",_=>text.Trim()
    };
    public async Task<OnlineAudioSearch> SearchAsync(string query,string kind,string source,string license,int page,CancellationToken token)
    {
        if(query.Length>200||kind is not ("music" or "effect")||source is not ("all" or "jamendo" or "freesound")||license is not ("usable" or "cc0")||page is <1 or >20)throw new ArgumentException("线上音频搜索参数无效");
        query=TranslateQuery(query);if(query.Length==0)query=kind=="music"?"instrumental":"whoosh";
        var key=$"{query}|{kind}|{source}|{license}|{page}";
        if(_searchCache.TryGetValue(key,out var cached)&&DateTimeOffset.UtcNow-cached.At<TimeSpan.FromMinutes(10))return cached.Result;
        // Many Freesound records have no category, so a mandatory sound_effect
        // filter would hide valid sound effects. Keep keyword searches inclusive.
        var baseUrl="https://api.openverse.org/v1/audio/?q="+Uri.EscapeDataString(query)+"&page_size=12&page="+page+"&license="+(license=="cc0"?"cc0,pdm":"cc0,pdm,by")+"&filter_dead=true"+(source=="all"?"":"&source="+source);
        var category=kind=="music"&&source!="freesound"?"&category=music":"";
        using var response=await Client.GetAsync(baseUrl+category,token);
        if(response.StatusCode==HttpStatusCode.TooManyRequests)throw new InvalidOperationException("线上音频源暂时限制请求，请稍后搜索；已经下载的素材仍可使用");
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"线上音频源请求失败 ({(int)response.StatusCode})");
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));var root=doc.RootElement;
        JsonDocument? fallback=null;
        if(category.Length>0&&root.GetProperty("result_count").GetInt32()==0)
        {
            using var retry=await Client.GetAsync(baseUrl,token);retry.EnsureSuccessStatusCode();
            fallback=JsonDocument.Parse(await retry.Content.ReadAsStringAsync(token));root=fallback.RootElement;
        }
        using var fallbackDocument=fallback;var results=new List<OnlineAudio>();
        foreach(var x in root.GetProperty("results").EnumerateArray())
        {
            var code=Text(x,"license");if(code is not ("cc0" or "pdm" or "by"))continue;
            var audio=Text(x,"url");if(!AllowedUrl(audio))continue;
            var duration=x.TryGetProperty("duration",out var d)&&d.TryGetDouble(out var ms)?ms/1000:0;
            var item=new OnlineAudio(Text(x,"id"),Text(x,"title"),Text(x,"creator"),Text(x,"source"),kind,duration,code=="by"?"CC BY "+Text(x,"license_version"):code=="pdm"?"Public Domain":"CC0",Text(x,"license_url"),Text(x,"foreign_landing_url"),audio,Text(x,"attribution"));
            _results[item.Id]=(item,DateTimeOffset.UtcNow);results.Add(item);
        }
        var result=new OnlineAudioSearch(root.GetProperty("page").GetInt32(),Math.Min(20,root.GetProperty("page_count").GetInt32()),root.GetProperty("result_count").GetInt32(),query,results);
        _searchCache[key]=(result,DateTimeOffset.UtcNow);
        foreach(var old in _searchCache.Where(x=>DateTimeOffset.UtcNow-x.Value.At>TimeSpan.FromMinutes(20)).Take(100).ToArray())_searchCache.TryRemove(old.Key,out _);
        return result;
    }
    public static bool AllowedUrl(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||!uri.IsDefaultPort)return false;
        var host=uri.DnsSafeHost;return new[]{"freesound.org","jamendo.com","archive.org","wikimedia.org"}.Any(domain=>host==domain||host.EndsWith("."+domain,StringComparison.OrdinalIgnoreCase));
    }
    public async Task<AudioAsset> DownloadAsync(string id,string kind,CancellationToken token)
    {
        if(!_results.TryGetValue(id,out var found))throw new ArgumentException("搜索结果已过期，请重新搜索再下载");
        if(kind is not ("music" or "effect"))throw new ArgumentException("请选择音乐或音效");
        var existing=studio.Document("online-audio-id",id+":"+kind);if(existing is not null&&studio.Assets().FirstOrDefault(x=>x.Id==existing&&File.Exists(x.Path)) is {} known)return known;
        await _downloads.WaitAsync(token);string? temporary=null;
        try
        {
            var item=found.Item;var url=item.AudioUrl;HttpResponseMessage? response=null;
            for(var redirect=0;redirect<5;redirect++)
            {
                if(!AllowedUrl(url))throw new InvalidOperationException("素材下载地址不受支持，请使用来源页面下载后导入");
                response?.Dispose();response=await Client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token);
                if((int)response.StatusCode is >=300 and <400&&response.Headers.Location is {} location){url=new Uri(new Uri(url),location).AbsoluteUri;continue;}break;
            }
            using(response)
            {
                if(response is null||!response.IsSuccessStatusCode)throw new InvalidOperationException("素材地址已失效或来源拒绝下载，可打开来源页面查看其他版本");
                if(response.Content.Headers.ContentLength is >80_000_000)throw new InvalidOperationException("该音频超过 80 MB，请选择较短素材");
                var mime=response.Content.Headers.ContentType?.MediaType??"";
                var extension=mime switch {"audio/ogg"=>".ogg","audio/wav" or "audio/x-wav"=>".wav","audio/flac" or "audio/x-flac"=>".flac","audio/mp4"=>".m4a",_=>Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant()};
                if(extension is not (".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a"))extension=".mp3";
                temporary=Path.Combine(Path.GetTempPath(),"liveclipper-online-"+Guid.NewGuid().ToString("N")+extension);
                await using(var stream=await response.Content.ReadAsStreamAsync(token))await using(var target=File.Create(temporary))
                {
                    var buffer=new byte[65536];long total=0;int count;
                    while((count=await stream.ReadAsync(buffer,token))>0){total+=count;if(total>80_000_000)throw new InvalidOperationException("素材超过 80 MB");await target.WriteAsync(buffer.AsMemory(0,count),token);}
                }
                using var probe=JsonDocument.Parse(await media.RunAsync(media.Ffprobe,["-v","error","-show_streams","-of","json",temporary],token));
                if(!probe.RootElement.GetProperty("streams").EnumerateArray().Any(x=>Text(x,"codec_type")=="audio"))throw new InvalidOperationException("来源返回的文件不是有效音频");
                using var input=File.OpenRead(temporary);var asset=library.Import(item.Title+extension,input,kind,"线上 · "+item.Source);
                studio.SaveDocument("audio-attribution",asset.Id,JsonSerializer.Serialize(item));studio.SaveDocument("online-audio-id",id+":"+kind,asset.Id);return asset;
            }
        }
        catch(TaskCanceledException ex) when(!token.IsCancellationRequested){throw new InvalidOperationException("线上音频下载超时，请重试或打开来源页面",ex);}
        finally{if(temporary is not null&&File.Exists(temporary))File.Delete(temporary);_downloads.Release();}
    }
}
