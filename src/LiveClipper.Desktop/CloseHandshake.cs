using System.Globalization;
using System.Text.Json;

namespace LiveClipper.Desktop;

/// <summary>
/// 关窗前问 Web 编辑器“改动写完了没有”的握手。纯逻辑：不引用 WPF / WebView2 类型，方便单测。
/// 协议（页面侧见 web/src/features/editor/closeHandshake.ts）：
/// 1. 桌面壳先执行 <c>ProbeScript</c>，读页面全局标记 __lcCloseHandshake.installed；
///    只有明确返回 0 才代表没有编辑器响应者；探测超时、失败或无法解析都需用户确认。
/// 2. 有响应者时 Post {type:"lc-close-request",requestId}。
/// 3. 页面在 2.5 秒内 flush，回 {type:"lc-close-ack",requestId,dirty,flushed}。
/// 4. 桌面壳最多等 <c>AckTimeout</c>。只有 dirty=false 且 flushed=true 才直接关；
///    脏、没写完、超时、页面无响应一律让用户确认，绝不当成“已保存”。
/// </summary>
public sealed class CloseHandshake
{
    public const string RequestType="lc-close-request";
    public const string AckType="lc-close-ack";
    public const string MarkerName="__lcCloseHandshake";
    // 不能含双引号或反斜杠：tests/merge/r51-close-handshake.test.mjs 会把它原样抠出来在 Node 里执行，
    // 对照 TS 写下的标记。
    public const string ProbeScript="(()=>{const s=globalThis.__lcCloseHandshake;return s&&typeof s.installed==='number'?s.installed:0;})()";
    public static readonly TimeSpan ProbeTimeout=TimeSpan.FromSeconds(1);
    // 必须长于页面侧 CLOSE_FLUSH_TIMEOUT_MS（2500ms）：页面还在 flush 时桌面壳不能先判超时。
    public static readonly TimeSpan AckTimeout=TimeSpan.FromSeconds(3);

    private TaskCompletionSource<CloseAck>? _pending;
    private string? _requestId;
    private long _sequence;

    /// <summary>一轮握手正在进行（从探测开始，到确认框关掉为止）。</summary>
    public bool Running { get; private set; }

    /// <summary>开始一轮握手；已有一轮在跑时返回 false，调用方应继续取消这次关闭。</summary>
    public bool Begin()
    {
        if(Running)return false;
        Running=true;
        return true;
    }

    public void End()
    {
        Running=false;
        _pending=null;
        _requestId=null;
    }

    /// <summary>生成本轮请求：要 Post 给页面的 JSON，以及等 ack 的任务（只会由匹配的 ack 完成）。</summary>
    public (string Json,Task<CloseAck> Ack) CreateRequest()
    {
        _sequence++;
        _requestId="close-"+_sequence.ToString(CultureInfo.InvariantCulture);
        _pending=new TaskCompletionSource<CloseAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (JsonSerializer.Serialize(new {type=RequestType,requestId=_requestId}),_pending.Task);
    }

    /// <summary>
    /// 处理页面发来的消息。是 lc-close-ack 就返回 true：迟到的、过期的、requestId 对不上的也吞掉，
    /// 不落到通用消息分支。其余消息返回 false，交给原有处理。
    /// </summary>
    public bool HandleMessage(string? json)
    {
        var ack=ParseAck(json);
        if(ack is null)return false;
        var pending=_pending;
        if(pending is null)return true;
        if(ack.RequestId is not null&&ack.RequestId!=_requestId)return true;
        pending.TrySetResult(ack);
        return true;
    }

    /// <summary>解析 ack；不是 lc-close-ack 返回 null。缺字段或类型不对一律按“不安全”算。</summary>
    public static CloseAck? ParseAck(string? json)
    {
        if(string.IsNullOrWhiteSpace(json))return null;
        try
        {
            using var document=JsonDocument.Parse(json);
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return null;
            if(!root.TryGetProperty("type",out var type)||type.ValueKind!=JsonValueKind.String||type.GetString()!=AckType)return null;
            var requestId=root.TryGetProperty("requestId",out var id)&&id.ValueKind==JsonValueKind.String?id.GetString():null;
            // dirty 只有明确为 false 才算干净；flushed 只有明确为 true 才算写完。
            var dirty=!(root.TryGetProperty("dirty",out var d)&&d.ValueKind==JsonValueKind.False);
            var flushed=root.TryGetProperty("flushed",out var f)&&f.ValueKind==JsonValueKind.True;
            return new CloseAck(requestId,dirty,flushed);
        }
        catch(JsonException)
        {
            return null;
        }
    }

    /// <summary>0 表示没有响应者；正整数表示已安装；无法判断返回 null。</summary>
    private static int? ParseProbe(string? json)
    {
        if(string.IsNullOrWhiteSpace(json))return null;
        try
        {
            using var document=JsonDocument.Parse(json);
            var root=document.RootElement;
            return root.ValueKind==JsonValueKind.Number&&root.TryGetInt32(out var installed)&&installed>=0?installed:null;
        }
        catch(JsonException)
        {
            return null;
        }
    }

    public static async Task<CloseDecision> DecideAsync(Func<Task<string>> probe,Func<Task<CloseAck>> requestAck)
    {
        try
        {
            var installed=ParseProbe(await probe().WaitAsync(ProbeTimeout));
            if(installed is null)return CloseDecision.ConfirmUnresponsive;
            if(installed==0)return CloseDecision.Close;
            try{return Decide(await requestAck().WaitAsync(AckTimeout));}
            catch(TimeoutException){return CloseDecision.ConfirmTimeout;}
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine("close handshake could not confirm save: "+ex.Message);
            return CloseDecision.ConfirmUnresponsive;
        }
    }

    public static CloseDecision Decide(CloseAck? ack)
    {
        if(ack is null)return CloseDecision.ConfirmTimeout;
        if(ack.Dirty)return CloseDecision.ConfirmDirty;
        if(!ack.Flushed)return CloseDecision.ConfirmUnflushed;
        return CloseDecision.Close;
    }

    public static string ConfirmMessage(CloseDecision decision)
    {
        var reason=decision switch
        {
            CloseDecision.ConfirmDirty=>"编辑器里还有没保存的修改。",
            CloseDecision.ConfirmUnflushed=>"编辑器没能确认修改已经写入（可能是网络断开或保存失败）。",
            CloseDecision.ConfirmTimeout=>"编辑器没有在 "+AckTimeout.TotalSeconds.ToString("0",CultureInfo.InvariantCulture)+" 秒内回复保存结果。",
            CloseDecision.ConfirmUnresponsive=>"编辑器页面没有响应，无法确认修改是否已经保存。",
            _=>"无法确认编辑器里的修改是否已经保存。",
        };
        var newLine=Environment.NewLine;
        return reason+newLine+newLine+"是：仍然退出（未保存的修改可能丢失）"+newLine+"否：留在编辑器";
    }
}

public enum CloseDecision { Close, ConfirmDirty, ConfirmUnflushed, ConfirmTimeout, ConfirmUnresponsive }

/// <summary>页面回的 ack。RequestId 为 null 时视为回复当前这一轮。</summary>
public sealed record CloseAck(string? RequestId,bool Dirty,bool Flushed);
