using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveClipper.Domain;

namespace LiveClipper.Core;

public sealed record AiConnection(string Provider,string Endpoint,string Model,string? ApiKey);
public sealed record ModelDownloadState(string State,string Model,int Progress,string? Detail);
public sealed class AiCompletionException(string message) : Exception(message);
public sealed record AiAdvice(string Summary,IReadOnlyList<string> Captions,IReadOnlyList<string> Tips);
public sealed record AiAnalysisResult(IReadOnlyList<StudioEvent> Events,AiAdvice Advice,string? AdviceError);

public sealed class AiAnalysisService(Store store,StudioStore studio,CandidatePipelineService? pipeline=null,HttpClient? transport=null)
{
    private static readonly HttpClient Client=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(4)};
    private static readonly HttpClient ModelsClient=new(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}){Timeout=Timeout.InfiniteTimeSpan};
    private static readonly HttpClient PullClient=new(){Timeout=Timeout.InfiniteTimeSpan};
    private readonly object _pullGate=new();
    private CancellationTokenSource? _pullStop;
    public ModelDownloadState DownloadState {get;private set;}=new("idle","",0,null);
    public static string? NormalizeEndpoint(string? endpoint)
    {
        if(!Uri.TryCreate(endpoint?.Trim().TrimEnd('/'),UriKind.Absolute,out var uri)
            ||uri.Scheme is not ("http" or "https")||uri.UserInfo.Length>0)return null;
        return uri.AbsoluteUri.TrimEnd('/');
    }
    public static bool IsLoopbackCompatible(AiConnection connection)=>connection.Provider=="compatible"
        &&NormalizeEndpoint(connection.Endpoint) is {} endpoint&&new Uri(endpoint).IsLoopback;
    public static Uri Endpoint(AiConnection connection)
    {
        if(connection.Provider=="ollama")return OllamaRuntime.BaseUri;
        if(connection.Provider!="compatible")throw new ArgumentException("请选择本地 Ollama 或兼容 API");
        var endpoint=NormalizeEndpoint(connection.Endpoint);
        if(endpoint is null)throw new ArgumentException("云端 API 地址必须使用 HTTPS");
        var uri=new Uri(endpoint);
        if(uri.Scheme!="https"&&!uri.IsLoopback)throw new ArgumentException("云端 API 地址必须使用 HTTPS");
        return new Uri(endpoint+"/");
    }
    public async Task<IReadOnlyList<string>> ModelsAsync(AiConnection connection,CancellationToken token)
    {
        var root=Endpoint(connection);
        var ollama=connection.Provider=="ollama";
        // Discovery only reads the chosen service; starting a daemon is a separate action.
        using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(root,ollama?"api/tags":"models"));
        // 无 Key 的兼容服务也允许查询；有 Key 时只发给用户当前地址，且不跟随重定向。
        if(!ollama&&!string.IsNullOrWhiteSpace(connection.ApiKey))request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",connection.ApiKey);
        HttpResponseMessage response;
        try{response=await ModelsClient.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);}
        catch(HttpRequestException) when(ollama){throw new InvalidOperationException("无法连接本机 Ollama，请先安装并启动 Ollama");}
        using(response)
        {
            var code=(int)response.StatusCode;
            if(code is 401 or 403)throw new HttpRequestException($"认证失败 ({code})：API Key 无效或没有权限",null,response.StatusCode);
            if(code is 404 or 405)throw new HttpRequestException($"该地址不支持模型列表查询 ({code})，请检查 API 地址是否为 /v1 根路径",null,response.StatusCode);
            if(code is >=300 and <400)throw new HttpRequestException($"服务返回重定向 ({code})，出于安全不会跟随，请直接填写最终地址",null,response.StatusCode);
            if(!response.IsSuccessStatusCode)throw new HttpRequestException($"模型列表请求失败 ({code})",null,response.StatusCode);
            try
            {
                using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var list=ollama?doc.RootElement.GetProperty("models"):doc.RootElement.GetProperty("data");
                return list.EnumerateArray().Select(x=>ollama?x.GetProperty("name").GetString():x.GetProperty("id").GetString()).Where(x=>!string.IsNullOrWhiteSpace(x)).Cast<string>().Order().ToArray();
            }
            catch(Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {throw new JsonException("服务返回的模型列表格式无法识别");}
        }
    }
    public bool StartPull(string model)
    {
        if(!Regex.IsMatch(model,@"^[A-Za-z0-9._:/-]{2,100}$"))throw new ArgumentException("模型名称无效");
        lock(_pullGate)
        {
            if(_pullStop is not null)return false;
            _pullStop=new CancellationTokenSource();DownloadState=new("downloading",model,0,"正在连接 Ollama");
            var stop=_pullStop;
            _=Task.Run(async()=>
            {
                try
                {
                    await OllamaRuntime.EnsureAsync(stop.Token);
                    using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(OllamaRuntime.BaseUri,"api/pull")) {Content=JsonContent.Create(new {model,stream=true})};
                    using var response=await PullClient.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,stop.Token);
                    response.EnsureSuccessStatusCode();
                    await using var stream=await response.Content.ReadAsStreamAsync(stop.Token);using var reader=new StreamReader(stream);
                    var succeeded=false;
                    while(await reader.ReadLineAsync(stop.Token) is { } line)
                    {
                        using var json=JsonDocument.Parse(line);var item=json.RootElement;
                        if(item.TryGetProperty("error",out var error))throw new InvalidOperationException(error.GetString());
                        var detail=item.TryGetProperty("status",out var status)?status.GetString():"下载中";
                        if(detail=="success")succeeded=true;
                        var progress=DownloadState.Progress;
                        if(item.TryGetProperty("total",out var total)&&item.TryGetProperty("completed",out var completed)&&total.GetInt64()>0)
                            progress=Math.Clamp((int)(completed.GetInt64()*100/total.GetInt64()),0,99);
                        DownloadState=new("downloading",model,progress,detail);
                    }
                    if(!succeeded)throw new InvalidOperationException("模型下载连接中断，请重试；已下载的文件会继续使用");
                    DownloadState=new("completed",model,100,"模型已可使用");
                }
                catch(OperationCanceledException){DownloadState=new("cancelled",model,0,"已取消");}
                catch(HttpRequestException ex) when(ex.StatusCode is not null){DownloadState=new("failed",model,DownloadState.Progress,$"模型下载请求失败 ({(int)ex.StatusCode})，请检查模型名称或 Ollama 服务日志");}
                catch(HttpRequestException){DownloadState=new("failed",model,DownloadState.Progress,"无法连接本机 Ollama，请点击设置页的检查并启动服务");}
                catch(Exception ex){DownloadState=new("failed",model,0,ex.Message);}
                finally{lock(_pullGate){stop.Dispose();_pullStop=null;}}
            });
            return true;
        }
    }
    public void CancelPull(){lock(_pullGate)_pullStop?.Cancel();}
    public AiAdvice? SavedAdvice(string clipId)
    {
        var json=studio.Document("ai-advice",clipId);
        if(string.IsNullOrWhiteSpace(json))return null;
        var clip=store.Clips().FirstOrDefault(x=>x.Id==clipId);
        if(clip is null)return null;
        var effective=EffectiveTranscript.FromProjectJson(studio.Project(clipId),clipId,store.TranscriptJson(clip.EventId));
        try
        {
            using var saved=JsonDocument.Parse(json);
            if(saved.RootElement.ValueKind!=JsonValueKind.Object||!saved.RootElement.TryGetProperty("TranscriptCacheKey",out var key)
                ||key.ValueKind!=JsonValueKind.String||key.GetString()!=effective.CacheKey)return null;
            return JsonSerializer.Deserialize<AiAdvice>(json);
        }
        catch(JsonException){return null;}
    }
    public Task<AiAnalysisResult> AnalyzeAsync(Clip clip,AiConnection connection,CancellationToken token)=>AnalyzeAsync(clip,connection,token,null);
    public async Task<AiAnalysisResult> AnalyzeAsync(Clip clip,AiConnection connection,CancellationToken token,EditorProject? project)
    {
        if(string.IsNullOrWhiteSpace(connection.Model))throw new ArgumentException("请选择 AI 模型");
        if(connection.Provider=="compatible"&&string.IsNullOrWhiteSpace(connection.ApiKey))throw new ArgumentException("请输入 API Key");
        var original=store.TranscriptJson(clip.EventId);
        var transcript=project is null
            ?EffectiveTranscript.FromProjectJson(studio.Project(clip.Id),clip.Id,original)
            :EffectiveTranscript.Resolve(project,clip.Id,original);
        var segments=transcript.Segments;
        if(segments.Length==0)throw new InvalidOperationException("字幕没有可分析的文字");
        // 先读 skill：资源缺失/为空时直接抛出，不被下方 advice 的 catch 吞掉。
        var skill=AiSkillText.SubtitleAnalysisSystemPrompt;
        // 事件提炼优先走全片候选流水线（分块缓存 + 去重合并 + 最终排序 + 边界优化）。
        IReadOnlyList<StudioEvent> result=pipeline is not null
            ?await pipeline.ExtractEventsWithTranscriptAsync(clip,connection,token,transcript)
            :await LegacyEventsAsync(clip,connection,segments,token,skill);
        // 事件提炼完后再要一份「总结 + 发布文案 + 切片建议」，失败不影响已生成的事件。
        AiAdvice advice=new("",[],[]);string? adviceError=null;
        try{advice=await AdviseAsync(result,connection,token,transport,skill);}
        catch(Exception ex)when(ex is not OperationCanceledException){adviceError=ex.Message;}
        studio.SaveDocument("ai-advice",clip.Id,JsonSerializer.Serialize(new {advice.Summary,advice.Captions,advice.Tips,TranscriptCacheKey=transcript.CacheKey}));
        return new(result,advice,adviceError);
    }
    /// <summary>无流水线时的旧路径：按 70 句一批顺序提炼。</summary>
    private async Task<List<StudioEvent>> LegacyEventsAsync(Clip clip,AiConnection connection,IReadOnlyList<TranscriptSegment> segments,CancellationToken token,string skill)
    {
        var result=new List<StudioEvent>();
        foreach(var batch in segments.Chunk(70))
        {
            token.ThrowIfCancellationRequested();
            var lines=string.Join("\n",batch.Select(x=>$"{x.Start:0.0}-{x.End:0.0}: {x.Text}"));
            var prompt="从直播转录字幕中提炼值得切片的事件。只返回 JSON 对象，格式为 {\"events\":[{\"topic\":\"话题主题\",\"description\":\"简介\",\"start\":秒数,\"end\":秒数,\"hook\":\"开头钩子\"}]}。topic 必须概括该段讨论的话题或主题；description 用 1-3 句说明发生了什么；start/end 必须取自字幕时间并覆盖完整事件，最多 5 个，避免捏造事实。字幕：\n"+lines;
            var content=await AskAsync(connection,prompt,token,transport,skill);
            using var json=ParseObject(content);
            if(!json.RootElement.TryGetProperty("events",out var events)||events.ValueKind!=JsonValueKind.Array)throw new InvalidOperationException("AI 未返回事件列表，请重试或换用其他模型");
            foreach(var item in events.EnumerateArray().Take(5))
            {
                if(!item.TryGetProperty("start",out var startValue)||!item.TryGetProperty("end",out var endValue)||!startValue.TryGetDouble(out var start)||!endValue.TryGetDouble(out var end))continue;
                start=Math.Clamp(start,batch[0].Start,batch[^1].End);end=Math.Clamp(end,batch[0].Start,batch[^1].End);
                if(end-start<1)continue;
                var topic=Text(item,"topic",70);if(topic.Length==0)topic=Text(item,"title",70);if(topic.Length==0)continue;
                var description=Text(item,"description",500);if(description.Length==0)description=Text(item,"summary",500);
                var ev=new StudioEvent(Guid.NewGuid().ToString("N"),clip.Id,start,end,topic,description,Text(item,"hook",150),"ai");
                studio.SaveEvent(ev);result.Add(ev);
            }
        }
        return result;
    }
    private static async Task<AiAdvice> AdviseAsync(IReadOnlyList<StudioEvent> events,AiConnection connection,CancellationToken token,HttpClient? client,string systemPrompt)
    {
        if(events.Count==0)return new("",[],[]);
        var lines=string.Join("\n",events.Select(x=>$"{x.Start:0.0}-{x.End:0.0}: {x.Title} — {x.Summary}"));
        var prompt="以下是直播切片候选事件的标题与摘要。只返回 JSON 对象，格式为 {\"summary\":\"...\",\"captions\":[\"...\"],\"tips\":[\"...\"]}。要求：summary 是一句话总结整场内容，60 字内；captions 是 3 条可直接发布的切片文案，各含吸引人的标题和一句正文，每条 100 字内；tips 是 3 条切片制作建议（节奏、封面、标题方向）。不要捏造事实。事件：\n"+lines;
        var content=await AskAsync(connection,prompt,token,client,systemPrompt);
        using var json=ParseObject(content);
        return new(Text(json.RootElement,"summary",200),Texts(json.RootElement,"captions",3,200),Texts(json.RootElement,"tips",3,200));
    }
    private static IReadOnlyList<string> Texts(JsonElement item,string name,int take,int limit)
    {
        if(!item.TryGetProperty(name,out var x)||x.ValueKind!=JsonValueKind.Array)return [];
        return x.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>v.GetString()?.Trim()??"").Where(v=>v.Length>0).Select(v=>v.Length<=limit?v:v[..limit]).Take(take).ToArray();
    }
    private static string Text(JsonElement item,string name,int limit)
    {
        var value=item.TryGetProperty(name,out var x)&&x.ValueKind==JsonValueKind.String?x.GetString()?.Trim()??"":"";
        return value.Length<=limit?value:value[..limit];
    }
    /// <summary>解析模型输出，允许 Markdown 代码块、尾随逗号和缺少右括号。</summary>
    public static JsonDocument ParseObject(string text)=>JsonDocument.Parse(ExtractObject(text));
    private static string ExtractObject(string text)
    {
        if(string.IsNullOrWhiteSpace(text))throw new InvalidOperationException("AI 响应为空");
        var trimmed=text.Trim();
        if(trimmed.StartsWith("```",StringComparison.Ordinal))
        {
            var firstLine=trimmed.IndexOf('\n');
            if(firstLine>=0)trimmed=trimmed[(firstLine+1)..];
            var lastFence=trimmed.LastIndexOf("```",StringComparison.Ordinal);
            if(lastFence>=0)trimmed=trimmed[..lastFence];
        }
        var start=trimmed.IndexOf('{');var end=trimmed.LastIndexOf('}');
        if(start<0||end<start)throw new InvalidOperationException("AI 响应中没有 JSON 对象");
        var candidate=Regex.Replace(trimmed[start..(end+1)],@"\s*,\s*([}\]])","$1");
        return CloseJson(candidate);
    }
    private static string CloseJson(string text)
    {
        var stack=new Stack<char>();var inString=false;var escaped=false;
        foreach(var c in text)
        {
            if(inString)
            {
                if(escaped)escaped=false;
                else if(c=='\\')escaped=true;
                else if(c=='"')inString=false;
                continue;
            }
            if(c=='"')inString=true;
            else if(c is '{' or '[')stack.Push(c);
            else if(c is '}' or ']')stack.Pop();
        }
        if(inString)text+='"';
        while(stack.Count>0)text+=stack.Pop() is '{'?'}':']';
        return text;
    }
    public static async Task<string> AskAsync(AiConnection connection,string prompt,CancellationToken token,HttpClient? client=null,string? systemPrompt=null,int attemptLimit=3,int? localOutputLimit=null,int? localContextLimit=null)
    {
        if(attemptLimit is <1 or >3)throw new ArgumentOutOfRangeException(nameof(attemptLimit));
        if(string.IsNullOrWhiteSpace(connection.Model))throw new ArgumentException("请选择 AI 模型");
        if(connection.Provider=="compatible"&&string.IsNullOrWhiteSpace(connection.ApiKey))throw new ArgumentException("请输入 API Key");
        Exception? last=null;
        for(var attempt=0;attempt<attemptLimit;attempt++)
        {
            try{return await AskOnceAsync(connection,prompt,token,client,systemPrompt,localOutputLimit,localContextLimit);}
            catch(Exception ex) when(!token.IsCancellationRequested&&attempt<attemptLimit-1&&ex is HttpRequestException or InvalidOperationException)
            {
                last=ex;
                await Task.Delay(TimeSpan.FromSeconds(attempt+1),token);
            }
        }
        throw last??new InvalidOperationException("AI 请求失败");
    }
    private static Dictionary<string,object> LocalOptions(int outputLimit,int? contextLimit)
    {
        var options=new Dictionary<string,object>{{"temperature",.1},{"num_predict",outputLimit}};
        if(contextLimit is {} context)options["num_ctx"]=context;
        return options;
    }
    private static async Task<string> AskOnceAsync(AiConnection connection,string prompt,CancellationToken token,HttpClient? client,string? systemPrompt=null,int? localOutputLimit=null,int? localContextLimit=null)
    {
        var messages=string.IsNullOrEmpty(systemPrompt)
            ?new[]{new {role="user",content=prompt}}
            :new[]{new {role="system",content=systemPrompt},new {role="user",content=prompt}};
        var endpoint=Endpoint(connection);
        var local=connection.Provider=="ollama";
        // Official DeepSeek defaults to high thinking. This bounded JSON pipeline
        // requires final content, not a 4000-token reasoning-only completion.
        var deepSeek=connection.Provider=="compatible" && endpoint.Scheme=="https" && endpoint.IsDefaultPort
            && endpoint.Host.Equals("api.deepseek.com",StringComparison.OrdinalIgnoreCase)
            && connection.Model is "deepseek-flash" or "deepseek-v4-flash" or "deepseek-v4-pro";
        var requestId=Guid.NewGuid().ToString("N");
        var timer=Stopwatch.StartNew();
        var phase="runtime_ready";
        AiRequestDiagnostics.Write(requestId,"request_started",new {provider=connection.Provider});
        try
        {
        if(local&&!(localOutputLimit is not null&&client is not null))await OllamaRuntime.EnsureAsync(token);
        AiRequestDiagnostics.Write(requestId,"runtime_ready",new {elapsedMs=timer.ElapsedMilliseconds});
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(endpoint,local?"api/chat":"chat/completions"));
        if(!local&&!string.IsNullOrWhiteSpace(connection.ApiKey))request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",connection.ApiKey);
        request.Content=JsonContent.Create(local
            ? localOutputLimit is {} limit ? (object)new {model=connection.Model,messages,format="json",stream=false,options=LocalOptions(limit,localContextLimit)} : (object)new {model=connection.Model,messages,format="json",stream=false,options=new {temperature=.1}}
            :deepSeek ? (object)new {model=connection.Model,messages,temperature=.1,max_tokens=4000,thinking=new {type="disabled"}}
            :new {model=connection.Model,messages,temperature=.1,max_tokens=4000});
        phase="awaiting_response";
        AiRequestDiagnostics.Write(requestId,"request_sent",new {elapsedMs=timer.ElapsedMilliseconds});
        using var response=await SendAsync(request,connection.Provider,token,client);
        // SendAsync uses ResponseContentRead: this milestone includes response body buffering.
        phase="response_received";
        AiRequestDiagnostics.Write(requestId,phase,new {elapsedMs=timer.ElapsedMilliseconds,status=(int)response.StatusCode});
        if(!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI 请求失败 (HTTP {(int)response.StatusCode})");
        var body=await response.Content.ReadAsStringAsync(token);
        using var doc=JsonDocument.Parse(body);
        if(local)AiRequestDiagnostics.Write(requestId,"ollama_timings",new {
            loadDurationNs=AiRequestDiagnostics.Duration(doc.RootElement,"load_duration"),
            promptEvalDurationNs=AiRequestDiagnostics.Duration(doc.RootElement,"prompt_eval_duration"),
            evalDurationNs=AiRequestDiagnostics.Duration(doc.RootElement,"eval_duration"),
            totalDurationNs=AiRequestDiagnostics.Duration(doc.RootElement,"total_duration")});
        if(!local)AiRequestDiagnostics.WriteUsage(requestId,doc.RootElement);
        var choice=local?default:doc.RootElement.GetProperty("choices")[0];
        var message=local?doc.RootElement.GetProperty("message"):choice.GetProperty("message");
        var result=message.GetProperty("content").GetString()??"";
        if(local)AiRequestDiagnostics.Write(requestId,"ollama_usage",new {promptTokens=AiRequestDiagnostics.Duration(doc.RootElement,"prompt_eval_count"),completionTokens=AiRequestDiagnostics.Duration(doc.RootElement,"eval_count")});
        var finishReason=local? (doc.RootElement.TryGetProperty("done_reason",out var doneReason)&&doneReason.ValueKind==JsonValueKind.String?doneReason.GetString():null) : !local && choice.TryGetProperty("finish_reason",out var finish) && finish.ValueKind==JsonValueKind.String
            ? finish.GetString() : null;
        var finishKind=finishReason is "stop" or "length" or "content_filter" or "tool_calls" or "insufficient_system_resource" ? finishReason : "unknown";
        AiRequestDiagnostics.Write(requestId,"completion_metadata",new {finishReason=finishKind,contentCharacters=result.Length,
            reasoningPresent=message.TryGetProperty("reasoning_content",out var reasoning) && reasoning.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(reasoning.GetString())});
        // Completed-but-empty/truncated generations must not cause automatic paid retries
        // or be parsed as a usable partial answer. Never substitute reasoning_content.
        if(local&&localOutputLimit is not null)
        {
            if(!doc.RootElement.TryGetProperty("done",out var done)||done.ValueKind!=JsonValueKind.True||finishKind!="stop")throw new AiCompletionException($"本地模型未完整结束（done_reason={finishKind}），未重试或应用");
            if(string.IsNullOrWhiteSpace(result))throw new AiCompletionException("本地模型返回空内容，未重试或应用");
        }
        if(deepSeek && finishKind=="length") throw new AiCompletionException("AI 输出达到 token 上限，未返回完整结果（finish_reason=length）；未自动重试。");
        if(deepSeek && string.IsNullOrWhiteSpace(result)) throw new AiCompletionException($"AI 未返回最终内容（finish_reason={finishKind}）；未自动重试。");
        AiRequestDiagnostics.Write(requestId,"completed",new {elapsedMs=timer.ElapsedMilliseconds});
        return result;
        }
        catch(Exception ex)
        {
            var kind=ex is OperationCanceledException?(token.IsCancellationRequested?"caller_cancelled":"timeout"):ex.GetType().Name;
            AiRequestDiagnostics.Write(requestId,"failed",new {phase,kind,elapsedMs=timer.ElapsedMilliseconds});
            throw;
        }
    }
    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,string provider,CancellationToken token,HttpClient? client)
    {
        try{return await (client??Client).SendAsync(request,token);}
        catch(HttpRequestException ex) when(provider=="ollama")
        {throw new InvalidOperationException("无法连接本机 Ollama，请先安装并启动 Ollama",ex);}
    }
}
