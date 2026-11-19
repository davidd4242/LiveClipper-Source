using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveClipper.Core;

/// <summary>
/// 一条编辑命令（P1.7c 命令层第一步）。Args 用 JsonNode 承载，未知键一律拒绝而不是静默忽略——
/// 这与 WeftCut 的 "no silent outcomes" 是同一条规矩：传了但没生效必须报错。
/// </summary>
public sealed record EditorCommand(string Op, JsonNode? Args);

/// <summary>一次命令的落点回执。先落地再汇报；被拒时整批不落，不产生回执。</summary>
public sealed record CommittedRecord(
    string OpId,
    string Command,
    string[] Changed,
    string[] Created,
    string[] Removed,
    AdjustedField[] Adjusted);

/// <summary>被服务端改写过、必须告诉调用方的值（帧栅格吸附等）。</summary>
public sealed record AdjustedField(string Key, string Field, double Value, string Why);

/// <summary>
/// 纯函数式命令执行器：不碰 IO、不碰 revision。端点负责持久化与 revision，测试直接测这里。
/// 任何一条命令非法就抛 EditorCommandException，调用方据此整批不落。
/// </summary>
public static class EditorCommandExecutor
{
    public static EditorProject Apply(EditorProject project, EditorCommand command, out CommittedRecord record, int? commandIndex = null)
    {
        var adjusted = new List<AdjustedField>();
        var changed = new List<string>();
        var created = new List<string>();
        var removed = new List<string>();
        EditorProject next;

        // 参数类型兜底闸门：JsonNode 取值类型不符会抛 InvalidOperationException / FormatException 这类框架异常，
        // 而端点只把 EditorCommandException 转 400，其余一律漏成 500。下面的 helpers 已逐个按 JSON 类型把关，
        // 这里只兜漏网的路径（例如 replaceProject 的反序列化）。
        // Validate(next) 必须留在 try 之外：它抛的 ArgumentException 是既有契约，不能被这里吃掉。
        try
        {
            switch (command.Op)
            {
            case "setRange":
            {
                OnlyKeys(command.Args, "start", "end");
                var start = Number(command.Args, "start");
                var end = Number(command.Args, "end");
                if (end <= start) throw Bad("setRange 需要 end > start");
                next = project with { Start = start, End = end };
                break;
            }
            case "moveItems":
            {
                OnlyKeys(command.Args, "moves");
                var items = Timeline(project);
                var moves = Array(command.Args, "moves");
                foreach (var move in moves)
                {
                    OnlyKeys(move, "key", "track", "start");
                    var key = Text(move, "key");
                    var index = IndexOf(items, key);
                    var target = items[index];
                    var track = OptionalNumber(move, "track") ?? target.Track;
                    var start = OptionalNumber(move, "start") ?? target.Start;
                    if (track is < 1 or > 6) throw Bad($"片段 {key} 的轨道必须在 1–6");
                    if (start < 0) throw Bad($"片段 {key} 的起点不能为负");
                    var snapped = Snap(start, project.Fps);
                    if (Math.Abs(snapped - start) > 0.0005) adjusted.Add(new AdjustedField(key, "start", snapped, "snap-to-frame"));
                    var span = target.End - target.Start;
                    items[index] = target with { Track = (int)track, Start = snapped, End = snapped + span };
                    changed.Add(key);
                }
                EnsureNoOverlap(items);
                next = project with { Timeline = items };
                break;
            }
            case "trimItem":
            {
                OnlyKeys(command.Args, "key", "start", "end", "duration");
                var items = Timeline(project);
                var key = Text(command.Args, "key");
                var index = IndexOf(items, key);
                var target = items[index];
                var start = OptionalNumber(command.Args, "start") ?? target.Start;
                var duration = OptionalNumber(command.Args, "duration");
                var end = duration is { } length ? start + length : OptionalNumber(command.Args, "end") ?? target.End;
                if (end <= start) throw Bad($"片段 {key} 裁剪后时长必须为正");
                if (start < 0) throw Bad($"片段 {key} 的起点不能为负");
                if (Math.Abs(start - Snap(start, project.Fps)) > 0.0005) adjusted.Add(new AdjustedField(key, "start", Snap(start, project.Fps), "snap-to-frame"));
                var snappedStart = Snap(start, project.Fps);
                var snappedEnd = Snap(end, project.Fps);
                items[index] = target with { Start = snappedStart, End = snappedEnd };
                EnsureNoOverlap(items);
                next = project with { Timeline = items };
                changed.Add(key);
                break;
            }
            case "splitItem":
            {
                OnlyKeys(command.Args, "key", "at");
                var items = Timeline(project).ToList();
                var key = Text(command.Args, "key");
                var at = Snap(Number(command.Args, "at"), project.Fps);
                var index = IndexOf(items.ToArray(), key);
                var target = items[index];
                if (at <= target.Start + 0.0005 || at >= target.End - 0.0005)
                    throw Bad($"片段 {key} 的切点必须落在片段内部");
                var tail = Guid.NewGuid().ToString("N");
                items[index] = target with { End = at };
                items.Insert(index + 1, target with { Id = tail, Start = at });
                EnsureNoOverlap(items);
                next = project with { Timeline = items.ToArray() };
                changed.Add(key);
                created.Add(tail);
                break;
            }
            case "removeItems":
            {
                OnlyKeys(command.Args, "keys");
                var items = Timeline(project).ToList();
                foreach (var node in Array(command.Args, "keys"))
                {
                    if (node is null || node.GetValueKind() is not JsonValueKind.String)
                        throw Bad("removeItems 的 keys 必须是字符串数组");
                    var key = node.GetValue<string>();
                    var index = IndexOf(items.ToArray(), key);
                    items.RemoveAt(index);
                    removed.Add(key);
                }
                if (items.Count == 0) throw Bad("时间线至少保留 1 个片段");
                next = project with { Timeline = items.ToArray() };
                break;
            }
            case "setBaseAudio":
            {
                OnlyKeys(command.Args, "muted");
                var muted = Arg(command.Args, "muted") ?? throw Bad("命令缺少 muted 布尔值");
                next = project with { BaseAudioMuted = ToBool(muted, "muted") };
                break;
            }
            case "setTrackState":
            {
                OnlyKeys(command.Args, "mute", "solo", "hide", "lock", "opacity");
                next = project;
                if (OptionalArray(command.Args, "mute") is { } mute) next = next with { MutedTracks = Numbers(mute) };
                if (OptionalArray(command.Args, "solo") is { } solo) next = next with { SoloedTracks = Numbers(solo) };
                if (OptionalArray(command.Args, "hide") is { } hide) next = next with { HiddenTracks = Numbers(hide) };
                if (OptionalArray(command.Args, "lock") is { } locked) next = next with { LockedTracks = Numbers(locked) };
                if (OptionalArray(command.Args, "opacity") is { } opacity) next = next with { TrackOpacities = Doubles(opacity) };
                break;
            }
            case "setRenderSettings":
            {
                OnlyKeys(command.Args, "resolution", "fps", "bitrateKbps", "format");
                next = project;
                if (OptionalNumber(command.Args, "resolution") is { } resolution) next = next with { Resolution = (int)resolution };
                if (OptionalNumber(command.Args, "fps") is { } fps) next = next with { Fps = (int)fps };
                if (OptionalNumber(command.Args, "bitrateKbps") is { } bitrate) next = next with { BitrateKbps = (int)bitrate };
                if (command.Args?["format"] is { } format) next = next with { Format = ToText(format, "format") };
                break;
            }
            case "setItemProps":
            {
                // 只暴露"不会改变时间布局"的字段：speed / start / end 都会动到序列长度，
                // 必须走 moveItems / trimItem，免得这里静默改时长。
                OnlyKeys(command.Args, "items");
                var items = Timeline(project).ToList();
                foreach (var entry in Array(command.Args, "items"))
                {
                    OnlyKeys(entry, "key", "volumeDb", "opacity", "zoom", "rotation", "positionX", "positionY", "disabled", "fadeIn", "fadeOut");
                    var key = Text(entry, "key");
                    var index = items.FindIndex(item => item.Id == key);
                    if (index < 0) throw Bad($"时间线上没有片段 {key}");
                    var target = items[index];
                    if (OptionalNumber(entry, "volumeDb") is { } volume) target = target with { VolumeDb = Clamp(entry, "volumeDb", volume, -60, 12, adjusted, key) };
                    if (OptionalNumber(entry, "opacity") is { } opacity) target = target with { Opacity = Clamp(entry, "opacity", opacity, 0, 1, adjusted, key) };
                    // 下面几个区间必须与 EditorRenderService.Validate 保持一致：夹宽了就会"回执说已限制、随后 Validate 又抛",
                    // 调用方拿到的 adjusted 是假的。zoom [1,2]、rotation [-180,180]、positionX/Y [-0.5,0.5] 都取自 Validate。
                    if (OptionalNumber(entry, "zoom") is { } zoom) target = target with { Zoom = Clamp(entry, "zoom", zoom, 1, 2, adjusted, key) };
                    if (OptionalNumber(entry, "rotation") is { } rotation) target = target with { Rotation = Clamp(entry, "rotation", rotation, -180, 180, adjusted, key) };
                    if (OptionalNumber(entry, "positionX") is { } x) target = target with { PositionX = Clamp(entry, "positionX", x, -0.5, 0.5, adjusted, key) };
                    if (OptionalNumber(entry, "positionY") is { } y) target = target with { PositionY = Clamp(entry, "positionY", y, -0.5, 0.5, adjusted, key) };
                    if (OptionalNumber(entry, "fadeIn") is { } fadeIn) target = target with { FadeIn = Clamp(entry, "fadeIn", fadeIn, 0, 5, adjusted, key) };
                    if (OptionalNumber(entry, "fadeOut") is { } fadeOut) target = target with { FadeOut = Clamp(entry, "fadeOut", fadeOut, 0, 5, adjusted, key) };
                    if (entry?["disabled"] is { } disabled) target = target with { Disabled = ToBool(disabled, "disabled") };
                    items[index] = target;
                    changed.Add(key);
                }
                // 校验最终结果，避免同一片段分多条 entry 更新时，中间态误触发总时长限制。
                foreach (var item in items)
                {
                    if ((item.NestedSequenceId is null or "") && item.FadeIn + item.FadeOut > EditorRenderService.ItemOutputDuration(item) + 0.001)
                        throw Bad($"片段 {item.Id} 的淡入淡出时长无效：fadeIn + fadeOut 不能超过片段输出时长（容差 0.001 秒）");
                }
                next = project with { Timeline = items.ToArray() };
                break;
            }
            case "setTransition":
            {
                // 剪点转场挂在剪点右侧片段（key）的入点上，track 由片段自身推导，不接受调用方传入——
                // 传进来的 track 与片段不符时 ResolveTransitions 会静默丢弃整条转场。
                // 按 D29 语义：transition 必须显式出现，值为 null 才是清除；键缺失会变成静默空操作，直接拒。
                OnlyKeys(command.Args, "key", "transition");
                var items = Timeline(project);
                var key = Text(command.Args, "key");
                var index = IndexOf(items, key);
                var target = items[index];
                if (!HasKey(command.Args, "transition")) throw Bad("setTransition 需要 transition（对象或 null）");
                var kept = project.Transitions is null ? new List<EditorTransition>() : project.Transitions.ToList();
                if (Arg(command.Args, "transition") is not { } spec)
                {
                    if (!kept.Any(x => x.ItemId == key)) throw Bad($"片段 {key} 上没有转场可清除");
                    foreach (var one in kept.Where(x => x.ItemId == key)) removed.Add(one.Id);
                    kept.RemoveAll(x => x.ItemId == key);
                    next = project with { Transitions = kept.Count == 0 ? null : kept.ToArray() };
                    changed.Add(key);
                    break;
                }
                if (spec is not JsonObject) throw Bad("transition 必须是对象或 null");
                OnlyKeys(spec, "kind", "duration", "align");
                // kind / align 的未知值一律拒：渲染层对未知值是静默降级成 cross-dissolve / center 的，
                // 命令层静默降级就等于"传了但没生效"。
                var kind = Arg(spec, "kind") is { } kindNode ? ToText(kindNode, "kind") : "cross-dissolve";
                if (kind is not ("cross-dissolve" or "dip-to-black" or "dip-to-white"))
                    throw Bad($"转场类型 {kind} 不支持（允许：cross-dissolve/dip-to-black/dip-to-white）");
                var align = Arg(spec, "align") is { } alignNode ? ToText(alignNode, "align") : "center";
                if (align is not ("center" or "start" or "end"))
                    throw Bad($"转场对齐 {align} 不支持（允许：center/start/end）");
                var requested = Number(spec, "duration");
                if (requested <= 0) throw Bad($"片段 {key} 的转场时长必须为正");
                // 剪点合法性前置检查：下面这几条在渲染层都是静默丢弃，存进去再丢对调用方不可见。
                var lane = items.Where(x => EditorRenderService.TrackOf(x) == EditorRenderService.TrackOf(target))
                    .OrderBy(x => x.Start).ToList();
                var position = lane.FindIndex(x => x.Id == key);
                if (position <= 0) throw Bad($"片段 {key} 是该轨道第一个片段，左侧没有剪点可放转场");
                var previous = lane[position - 1];
                if (target.GapBefore > 0.001) throw Bad($"片段 {key} 与前一片段之间有空隙，剪点处无法放转场");
                if (target.Disabled || previous.Disabled) throw Bad($"片段 {key} 或前一片段已停用，无法放转场");
                var gridFps = EditorRenderService.TransitionFps(project);
                var minDuration = EditorRenderService.TransitionMinDuration(gridFps);
                var snappedDuration = Snap(requested, project.Fps);
                if (Math.Abs(snappedDuration - requested) > 0.0005)
                    adjusted.Add(new AdjustedField(key, "transition.duration", snappedDuration, "snap-to-frame"));
                if (snappedDuration < minDuration) throw Bad($"片段 {key} 的转场时长至少要 1 帧");
                var maxDuration = Math.Min(EditorRenderService.ItemOutputDuration(previous), EditorRenderService.ItemOutputDuration(target));
                if (align == "end") maxDuration = Math.Min(maxDuration, EditorRenderService.ItemOutputDuration(previous) * 2 / 3);
                if (maxDuration < minDuration) throw Bad($"片段 {key} 或前一片段太短，放不下 1 帧转场");
                // 转场规格 §3：duration 存用户请求值（便于之后把片段改长再恢复），生效值由两侧片段长度 clamp。
                // 生效值短于请求值时用 adjusted 回报，不改存储值。1e-6 对齐渲染层的帧比较容差。
                var effective = Math.Floor(Math.Min(snappedDuration, maxDuration) * gridFps + 1e-6) / gridFps;
                if (effective < snappedDuration - 0.0005)
                    adjusted.Add(new AdjustedField(key, "transition.effectiveDuration", effective, "受两侧片段长度限制"));
                var slot = kept.FindIndex(x => x.ItemId == key);
                var id = slot >= 0 ? kept[slot].Id : $"transition-{key}";
                kept.RemoveAll(x => x.ItemId == key);
                var entry = new EditorTransition(id, key, EditorRenderService.TrackOf(target), kind, snappedDuration, align);
                if (slot >= 0) kept.Insert(Math.Min(slot, kept.Count), entry);
                else kept.Add(entry);
                next = project with { Transitions = kept.ToArray() };
                changed.Add(key);
                if (slot < 0) created.Add(id);
                break;
            }
            case "setCaptions":
            {
                // 写 CaptionOverrides（键=源 clipId）。沿用模型注释的 own-key 语义：
                // 空数组=显式清空该源的字幕，键缺省/null=沿用 store。所以 segments 必须显式出现。
                OnlyKeys(command.Args, "sourceClipId", "segments");
                var sourceClipId = Text(command.Args, "sourceClipId");
                if (string.IsNullOrWhiteSpace(sourceClipId)) throw Bad("命令缺少 sourceClipId");
                var items = Timeline(project);
                // 时间线没引用这个源时，写进去的覆盖是永远不会被读到的死数据，直接拒。
                if (!items.Any(x => x.ClipId == sourceClipId))
                    throw Bad($"时间线上没有引用源 {sourceClipId} 的片段");
                if (!HasKey(command.Args, "segments")) throw Bad("setCaptions 需要 segments（数组或 null）");
                var overrides = project.CaptionOverrides is null
                    ? new Dictionary<string, TranscriptSegment[]>(StringComparer.Ordinal)
                    : new Dictionary<string, TranscriptSegment[]>(project.CaptionOverrides, StringComparer.Ordinal);
                if (Arg(command.Args, "segments") is null)
                {
                    if (!overrides.Remove(sourceClipId)) throw Bad($"源 {sourceClipId} 没有字幕覆盖可移除");
                    next = project with { CaptionOverrides = overrides.Count == 0 ? null : overrides };
                    removed.Add(sourceClipId);
                    break;
                }
                var nodes = Array(command.Args, "segments");
                if (nodes.Count > 2000) throw Bad($"源 {sourceClipId} 的字幕条数不能超过 2000");
                var segments = new List<TranscriptSegment>(nodes.Count);
                var ordinal = 0;
                foreach (var node in nodes)
                {
                    OnlyKeys(node, "start", "end", "text");
                    var text = Text(node, "text");
                    if (string.IsNullOrWhiteSpace(text)) throw Bad($"字幕第 {ordinal + 1} 条的 text 不能为空");
                    if (text.Length > 500) throw Bad($"字幕第 {ordinal + 1} 条的 text 不能超过 500 字");
                    var segStart = Number(node, "start");
                    var segEnd = Number(node, "end");
                    if (segStart < 0) throw Bad($"字幕第 {ordinal + 1} 条的 start 不能为负");
                    if (segEnd <= segStart) throw Bad($"字幕第 {ordinal + 1} 条需要 end > start");
                    var segSnappedStart = Snap(segStart, project.Fps);
                    var segSnappedEnd = Snap(segEnd, project.Fps);
                    if (Math.Abs(segSnappedStart - segStart) > 0.0005)
                        adjusted.Add(new AdjustedField(sourceClipId, $"segments[{ordinal}].start", segSnappedStart, "snap-to-frame"));
                    if (Math.Abs(segSnappedEnd - segEnd) > 0.0005)
                        adjusted.Add(new AdjustedField(sourceClipId, $"segments[{ordinal}].end", segSnappedEnd, "snap-to-frame"));
                    if (segSnappedEnd <= segSnappedStart) throw Bad($"字幕第 {ordinal + 1} 条吸附到帧后时长为 0");
                    segments.Add(new TranscriptSegment(segSnappedStart, segSnappedEnd, text));
                    ordinal++;
                }
                overrides[sourceClipId] = segments.ToArray();
                next = project with { CaptionOverrides = overrides };
                changed.Add(sourceClipId);
                break;
            }
            case "addItems":
            {
                // id 一律服务端 mint（同 splitItem）：Validate 要求 id 唯一，让调用方传 id 只是把冲突推到校验层。
                // 外观字段（opacity / zoom / volumeDb 等）不在这里，创建后走 setItemProps——
                // 与 setItemProps 拒 speed / start / end 是同一条分工。
                OnlyKeys(command.Args, "items", "track");
                var defaultTrack = OptionalNumber(command.Args, "track") ?? 1;
                if (defaultTrack is < 1 or > 6) throw Bad("addItems 的 track 必须在 1–6");
                var nodes = Array(command.Args, "items");
                if (nodes.Count == 0) throw Bad("addItems 需要至少 1 个片段");
                if (nodes.Count > 50) throw Bad("addItems 单次不能超过 50 个片段");
                var items = Timeline(project).ToList();
                if (items.Count + nodes.Count > 200)
                    throw Bad($"时间线片段总数不能超过 200（当前 {items.Count}，本次新增 {nodes.Count}）");
                var ordinal = 0;
                foreach (var node in nodes)
                {
                    OnlyKeys(node, "clipId", "start", "end", "duration", "track", "kind", "gapBefore");
                    var clipId = Text(node, "clipId");
                    if (string.IsNullOrWhiteSpace(clipId)) throw Bad($"第 {ordinal + 1} 个新片段缺少 clipId");
                    var track = OptionalNumber(node, "track") ?? defaultTrack;
                    if (track is < 1 or > 6) throw Bad($"第 {ordinal + 1} 个新片段的轨道必须在 1–6");
                    var kind = Arg(node, "kind") is { } kindNode ? ToText(kindNode, "kind") : "video";
                    if (kind is not ("video" or "hook" or "insert"))
                        throw Bad($"第 {ordinal + 1} 个新片段的 kind {kind} 不支持（允许：video/hook/insert）");
                    var start = Number(node, "start");
                    if (start < 0) throw Bad($"第 {ordinal + 1} 个新片段的起点不能为负");
                    var span = OptionalNumber(node, "duration");
                    var end = span is { } length
                        ? start + length
                        : OptionalNumber(node, "end") ?? throw Bad($"第 {ordinal + 1} 个新片段需要 end 或 duration");
                    if (end <= start) throw Bad($"第 {ordinal + 1} 个新片段的时长必须为正");
                    var gapBefore = OptionalNumber(node, "gapBefore") ?? 0;
                    if (gapBefore is < 0 or > EditorRenderService.MaxTimelineSeconds) throw Bad($"第 {ordinal + 1} 个新片段的 gapBefore 必须在 0–21600");
                    var id = Guid.NewGuid().ToString("N");
                    var snappedStart = Snap(start, project.Fps);
                    var snappedEnd = Snap(end, project.Fps);
                    var snappedGap = Snap(gapBefore, project.Fps);
                    if (Math.Abs(snappedStart - start) > 0.0005) adjusted.Add(new AdjustedField(id, "start", snappedStart, "snap-to-frame"));
                    if (Math.Abs(snappedEnd - end) > 0.0005) adjusted.Add(new AdjustedField(id, "end", snappedEnd, "snap-to-frame"));
                    if (Math.Abs(snappedGap - gapBefore) > 0.0005) adjusted.Add(new AdjustedField(id, "gapBefore", snappedGap, "snap-to-frame"));
                    if (snappedEnd <= snappedStart) throw Bad($"第 {ordinal + 1} 个新片段吸附到帧后时长为 0");
                    items.Add(new TimelineItem(id, clipId, snappedStart, snappedEnd, kind, Track: (int)track, GapBefore: snappedGap));
                    created.Add(id);
                    ordinal++;
                }
                EnsureNoOverlap(items);
                next = project with { Timeline = items.ToArray() };
                break;
            }
            case "replaceProject":
            {
                OnlyKeys(command.Args, "project");
                // Deserialize 对结构不符的 JSON 抛 JsonException/NotSupportedException，照样得变 400。
                EditorProject? parsed;
                try
                {
                    var submitted = command.Args?["project"];
                    if (submitted is not null && submitted is not JsonObject) throw Bad("replaceProject 的 project 必须是工程对象");
                    parsed = submitted is null ? null : EditorProjectCodec.Deserialize(submitted.ToJsonString());
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    throw Bad($"replaceProject 的 project 不是合法的工程对象：{ex.Message}");
                }
                var replacement = parsed ?? throw Bad("replaceProject 需要 project");
                next = replacement;
                foreach (var item in Timeline(next)) changed.Add(item.Id);
                break;
            }
            default:
                throw Bad($"未知命令 {command.Op}");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or InvalidCastException or OverflowException or NotSupportedException or JsonException)
        {
            // 漏到这里说明还有取值点没按 JSON 类型把关：也必须变成 400，而不是 500。
            throw Bad(commandIndex is { } at
                ? $"第 {at + 1} 条命令（{command.Op}）的参数类型不对：{ex.Message}"
                : $"命令 {command.Op} 的参数类型不对：{ex.Message}");
        }

        // 每条命令落地前都过一遍既有校验：形状不对就整批拒绝，绝不把"渲染时才炸"的工程存进去。
        EditorRenderService.Validate(next);
        record = new CommittedRecord(Guid.NewGuid().ToString("N"), command.Op, changed.ToArray(), created.ToArray(), removed.ToArray(), adjusted.ToArray());
        return next;
    }

    public static (EditorProject Project, CommittedRecord[] Records) ApplyBatch(EditorProject project, IReadOnlyList<EditorCommand> commands)
    {
        if (commands.Count == 0) throw Bad("commands 不能为空");
        if (commands.Count > 200) throw Bad("单次命令数不能超过 200");
        var current = project;
        var records = new List<CommittedRecord>(commands.Count);
        // 用下标循环：类型错的兜底消息要能说出"第几条命令"，调用方才知道该改哪一条。
        for (var i = 0; i < commands.Count; i++)
        {
            current = Apply(current, commands[i], out var record, i);
            records.Add(record);
        }
        return (current, records.ToArray());
    }

    /// <summary>帧栅格吸附：一律按 fps 取整到最近帧，调用方从 adjusted 得知被改了多少。</summary>
    public static double Snap(double seconds, int fps)
    {
        var rate = fps is 24 or 25 or 30 or 50 or 60 ? fps : 30;
        return Math.Round(seconds * rate, MidpointRounding.AwayFromZero) / rate;
    }

    private static TimelineItem[] Timeline(EditorProject project) => project.Timeline ?? [];

    private static int IndexOf(TimelineItem[] items, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw Bad("命令缺少 key");
        var matches = items.Select((item, index) => (item, index)).Where(x => x.item.Id == key).Select(x => x.index).ToArray();
        return matches.Length switch
        {
            0 => throw Bad($"找不到片段 {key}"),
            1 => matches[0],
            _ => throw Bad($"片段 {key} 不唯一"),
        };
    }

    /// <summary>同一轨道同一时刻不允许两个片段重叠——渲染会取错，必须在写入前拒绝。</summary>
    private static void EnsureNoOverlap(IReadOnlyList<TimelineItem> items)
    {
        foreach (var group in items.GroupBy(x => x.Track))
        {
            var ordered = group.OrderBy(x => x.Start).ToArray();
            for (var i = 1; i < ordered.Length; i++)
            {
                if (ordered[i].Start < ordered[i - 1].End - 0.0005)
                    throw Bad($"轨道 {group.Key} 上片段 {ordered[i - 1].Id} 与 {ordered[i].Id} 重叠");
            }
        }
    }

    private static void OnlyKeys(JsonNode? args, params string[] allowed)
    {
        if (args is not JsonObject obj) throw Bad("命令缺少参数对象");
        foreach (var property in obj)
        {
            if (!allowed.Contains(property.Key, StringComparer.Ordinal))
                throw Bad($"命令里有不认识的参数 {property.Key}（允许：{string.Join("/", allowed)}）");
        }
    }

    private static JsonNode? Arg(JsonNode? args, string name)
    {
        if (args is not JsonObject obj) throw Bad("命令缺少参数对象");
        if (!obj.TryGetPropertyValue(name, out var value)) return null;
        return value;
    }

    /// <summary>
    /// 键是否真的出现过。Arg() 对"没传这个键"和"传了但值是 JSON null"都返回 null，
    /// 而 setTransition / setCaptions 要区分这两者：null 是"清除"，缺省是"没说"，后者必须报错。
    /// </summary>
    private static bool HasKey(JsonNode? args, string name)
    {
        if (args is not JsonObject obj) throw Bad("命令缺少参数对象");
        return obj.ContainsKey(name);
    }

    private static string Text(JsonNode? args, string name)
        => Arg(args, name) is { } node ? ToText(node, name) : throw Bad($"命令缺少 {name}");

    /// <summary>夹到区间内；真的被夹住时记一条 adjusted，让调用方知道值被改过。</summary>
    private static double Clamp(JsonNode? args, string field, double value, double min, double max, List<AdjustedField> adjusted, string key)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped != value) adjusted.Add(new AdjustedField(key, field, clamped, $"已限制在 {min}–{max}"));
        return clamped;
    }

    private static double Number(JsonNode? args, string name)
        => OptionalNumber(args, name) ?? throw Bad($"命令缺少 {name}");

    private static double? OptionalNumber(JsonNode? args, string name)
    {
        var node = Arg(args, name);
        if (node is null) return null;
        var value = ToDouble(node, name);
        if (!double.IsFinite(value)) throw Bad($"{name} 必须是有限数");
        return value;
    }

    private static JsonArray Array(JsonNode? args, string name)
        => Arg(args, name) as JsonArray ?? throw Bad($"命令缺少 {name} 数组");

    private static JsonArray? OptionalArray(JsonNode? args, string name)
    {
        var node = Arg(args, name);
        if (node is null) return null;
        return node as JsonArray ?? throw Bad($"参数 {name} 必须是数组，收到{KindName(node)}");
    }

    private static int[] Numbers(JsonArray array)
        => array.Select(node => (int)ElementNumber(node)).ToArray();

    private static double[] Doubles(JsonArray array)
        => array.Select(node => ElementNumber(node)).ToArray();

    private static double ElementNumber(JsonNode? node)
    {
        if (node is null || node.GetValueKind() is not JsonValueKind.Number) throw Bad("数组元素必须是数字");
        return node.GetValue<double>();
    }

    /// <summary>
    /// 下面三个取值器先用不抛异常的 GetValueKind() 判 JSON 类型，再取值：
    /// 直接 GetValue&lt;T&gt;() 在类型不符时抛的是 InvalidOperationException/FormatException，
    /// 端点不认这类异常，会变成 500；类型错属于调用方的错，必须是 400。
    /// </summary>
    private static string ToText(JsonNode node, string name)
    {
        if (node.GetValueKind() is not JsonValueKind.String) throw Bad($"参数 {name} 必须是字符串，收到{KindName(node)}");
        return node.GetValue<string>();
    }

    private static double ToDouble(JsonNode node, string name)
    {
        if (node.GetValueKind() is not JsonValueKind.Number) throw Bad($"参数 {name} 必须是数字，收到{KindName(node)}");
        return node.GetValue<double>();
    }

    private static bool ToBool(JsonNode node, string name)
    {
        var kind = node.GetValueKind();
        if (kind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad($"参数 {name} 必须是 true/false，收到{KindName(node)}");
        return node.GetValue<bool>();
    }

    private static string KindName(JsonNode node) => node.GetValueKind() switch
    {
        JsonValueKind.Object => "对象",
        JsonValueKind.Array => "数组",
        JsonValueKind.String => "字符串",
        JsonValueKind.Number => "数字",
        JsonValueKind.True or JsonValueKind.False => "布尔值",
        JsonValueKind.Null => "null",
        _ => "未知类型"
    };

    private static EditorCommandException Bad(string message) => new(message);
}

/// <summary>命令被拒。Message 直接面向调用方，必须写清"哪里不对、该怎么改"。</summary>
public sealed class EditorCommandException(string message) : Exception(message);
