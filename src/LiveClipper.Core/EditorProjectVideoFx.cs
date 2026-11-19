using System.Text.Json;
using System.Text.Json.Serialization;
using LiveClipper.Core.EditorFx;

namespace LiveClipper.Core;

public sealed partial record TimelineItem
{
    [JsonPropertyName("catalogFxVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CatalogFxVersion { get; init; }

    [JsonPropertyName("catalogFx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CatalogFxInstance[]? CatalogFx { get; init; }
}

internal static class EditorProjectVideoFx
{
    internal static void ValidateProject(JsonElement root)
    {
        ValidateItems(root, "timeline");
        ValidateItems(root, "silenceTrimBackup");
        if (Find(root, "nestedSequences", out var nested) && nested.ValueKind == JsonValueKind.Object)
            foreach (var entry in nested.EnumerateObject()) ValidateItems(entry.Value, "timeline");
    }

    private static void ValidateItems(JsonElement owner, string name)
    {
        if (!Find(owner, name, out var items) || items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
        {
            var hasFx = Find(item, "catalogFx", out var fx);
            var hasVersion = Find(item, "catalogFxVersion", out var version);
            if (!hasFx && !hasVersion) continue;
            if (!hasFx || !hasVersion || version.ValueKind != JsonValueKind.Number || version.GetRawText() != "1")
                throw Invalid("catalog-fx-invalid-version");
            if (fx.ValueKind != JsonValueKind.Array) throw Invalid(VideoFxChain.InvalidChain);
            if (Find(item, "track", out var track) && (track.ValueKind != JsonValueKind.Number || !track.TryGetInt32(out var n) || n is < 1 or > 3))
                throw Invalid("catalog-fx-video-only");
            var effects = new List<CatalogFxInstance>();
            foreach (var raw in fx.EnumerateArray())
            {
                if (raw.ValueKind != JsonValueKind.Object) throw Invalid(VideoFxChain.InvalidChain);
                foreach (var field in raw.EnumerateObject())
                    if (!new[] { "id", "enabled", "params" }.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                        throw Invalid("catalog-fx-invalid-field");
                if (!Find(raw, "id", out var id) || id.ValueKind != JsonValueKind.String
                    || !Find(raw, "enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !Find(raw, "params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                    throw Invalid(VideoFxChain.InvalidChain);
                var values = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var parameter in parameters.EnumerateObject())
                {
                    if (parameter.Value.ValueKind != JsonValueKind.Number || !parameter.Value.TryGetDouble(out var value)
                        || !double.IsFinite(value) || !values.TryAdd(parameter.Name, value))
                        throw Invalid(VideoFxChain.InvalidParam);
                }
                effects.Add(new(id.GetString()!, enabled.GetBoolean(), values));
            }
            try { VideoFxChain.Validate(effects); }
            catch (ArgumentException ex) { throw Invalid(ex.Message, ex); }
        }
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
