using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveClipper.Core;

/// <summary>
/// 受管理事件选择的显式清空标记（v1）：该 sourceId 的全部事件片段已在宿主被取消勾选到零，
/// 序列因此被允许为空。只携带 sourceId，不携带事件清单；重新勾选该源事件后由宿主移除条目。
/// Wire: { "version": 1, "sourceId": string }；工程字段为数组 clearedEventSelections，
/// 对应 FC 引擎 sequence.hostClearedEventSelections。
/// </summary>
public sealed record TimelineEventSelectionClear
{
    [JsonPropertyName("version")] public int Version {get;init;}=1;
    [JsonPropertyName("sourceId")] public string SourceId {get;init;}="";
}

internal static class EditorProjectEventSelection
{
    /// <summary>Codec 入口的 raw 形状闸（仿 EditorProjectVideoFx.ValidateProject）：字段缺省/不是数组即通过，存在则严格校验。</summary>
    internal static void ValidateProject(JsonElement root)
    {
        if (!Find(root, "clearedEventSelections", out var entries) || entries.ValueKind != JsonValueKind.Array) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) throw Invalid("cleared-event-selection-invalid");
            foreach (var field in entry.EnumerateObject())
                if (!new[] { "version", "sourceId" }.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                    throw Invalid("cleared-event-selection-invalid-field");
            if (!Find(entry, "version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetRawText() != "1")
                throw Invalid("cleared-event-selection-invalid-version");
            if (!Find(entry, "sourceId", out var sourceId) || sourceId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(sourceId.GetString()))
                throw Invalid("cleared-event-selection-invalid-source");
            if (!seen.Add(sourceId.GetString()!)) throw Invalid("cleared-event-selection-duplicate-source");
        }
    }

    /// <summary>
    /// Typed 一致性闸（EditorRenderService.Validate 调用）：返回被标记清空的 sourceId 列表。
    /// 规则：字段存在时 timeline 必须是显式数组（[] 合法；null 是 legacy 语义，与标记矛盾）；
    /// version 必须为 1、sourceId 唯一非空；不得与仍存在的同源托管片段共存。
    /// </summary>
    internal static List<string> ValidateAndCollect(EditorProject plan, TimelineItem[] items)
    {
        var entries = plan.ClearedEventSelections;
        if (plan.EmptySequenceDuration is { } retained &&
            (!double.IsFinite(retained) || retained < 0 || retained > 21600 || plan.Timeline is not { Length: 0 } || entries is not { Length: > 0 }))
            throw new ArgumentException("空序列保留时长无效：仅限有明确清空身份的空时间线，时长须为 0–21600 秒");
        if (entries is null) return [];
        if (plan.Timeline is null)
            throw new ArgumentException("受管理事件清空标记要求 timeline 为显式数组（[] 或非空），不能是缺省/null");
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null || entry.Version != 1 || string.IsNullOrWhiteSpace(entry.SourceId))
                throw new ArgumentException("受管理事件清空标记无效：version 必须为 1，sourceId 必须非空");
            if (!seen.Add(entry.SourceId)) throw new ArgumentException($"受管理事件清空标记重复：{entry.SourceId}");
            result.Add(entry.SourceId);
        }
        var placed = items.Where(x => x.EventPlacement is { } placement && placement.Version == 1 && !string.IsNullOrWhiteSpace(placement.SourceId))
            .Select(x => x.EventPlacement!.SourceId).ToHashSet(StringComparer.Ordinal);
        if (result.Any(placed.Contains))
            throw new ArgumentException("受管理事件清空标记与时间线矛盾：被标记清空的源仍有事件片段在时间线上");
        // Zero-only 语义：清空标记只许可全局 0 items 的空时间线。宿主在片段重新存在时已在
        // 同一次历史 commit 前移除标记；任何残留标记 + 非空 items 都是明确非法，不自动 normalize。
        if (items.Length != 0)
            throw new ArgumentException("受管理事件清空标记只允许空时间线：时间线仍存在片段时不得保留清空标记（先移除标记或先清空全部片段）");
        return result;
    }

    private static bool Find(JsonElement owner, string name, out JsonElement value)
    {
        value = default;
        if (owner.ValueKind != JsonValueKind.Object) return false;
        var found = false; // last case-alias wins, same as the Web deserializer
        foreach (var field in owner.EnumerateObject())
        {
            if (!string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = field.Value;
            found = true;
        }
        return found;
    }

    private static EditorProjectSchemaException Invalid(string message, Exception? inner = null)
        => new(EditorProjectSchemaErrors.Invalid, message, innerException: inner);
}
