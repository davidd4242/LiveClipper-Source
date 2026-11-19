using System.Text.Json;
using System.Text.Json.Serialization;
using LiveClipper.Core.EditorFx;

namespace LiveClipper.Core;

public sealed partial record TimelineItem
{
    [JsonPropertyName("catalogAudioFxVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CatalogAudioFxVersion { get; init; }

    [JsonPropertyName("catalogAudioFx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CatalogAudioFxInstance[]? CatalogAudioFx { get; init; }
}

internal static class EditorProjectAudioFx
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
            var hasFx = Find(item, "catalogAudioFx", out var fx);
            var hasVersion = Find(item, "catalogAudioFxVersion", out var version);
            if (!hasFx && !hasVersion) continue;
            if (!hasFx || !hasVersion || version.ValueKind != JsonValueKind.Number || version.GetRawText() != "1")
                throw Invalid(AudioFxChain.InvalidVersion);
            if (fx.ValueKind != JsonValueKind.Array) throw Invalid(AudioFxChain.InvalidChain);
            if (Find(item, "track", out var track) && (track.ValueKind != JsonValueKind.Number || !track.TryGetInt32(out var n) || n is not (1 or 4 or 5 or 6)))
                throw Invalid(AudioFxChain.UnsupportedTrack);
            var effects = new List<CatalogAudioFxInstance>();
            foreach (var raw in fx.EnumerateArray())
            {
                if (raw.ValueKind != JsonValueKind.Object) throw Invalid(AudioFxChain.InvalidChain);
                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in raw.EnumerateObject())
                    if (!fields.Add(field.Name) || !new[] { "id", "enabled", "params" }.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                        throw Invalid("catalog-audio-fx-invalid-field");
                if (!Find(raw, "id", out var id) || id.ValueKind != JsonValueKind.String
                    || !Find(raw, "enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !Find(raw, "params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                    throw Invalid(AudioFxChain.InvalidChain);
                var values = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var parameter in parameters.EnumerateObject())
                {
                    if (parameter.Value.ValueKind != JsonValueKind.Number || !parameter.Value.TryGetDouble(out var value)
                        || !double.IsFinite(value) || !values.TryAdd(parameter.Name, value))
                        throw Invalid(AudioFxChain.InvalidParam);
                }
                effects.Add(new(id.GetString()!, enabled.GetBoolean(), values));
            }
            try { AudioFxChain.Build(effects); }
            catch (ArgumentException ex) { throw Invalid(ex.Message, ex); }
        }
    }

    private static bool Find(JsonElement owner, string name, out JsonElement value)
    {
        value = default;
        if (owner.ValueKind != JsonValueKind.Object) return false;
        var found = false;
        foreach (var field in owner.EnumerateObject())
        {
            if (!string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            // Only new audio fields tighten duplicate handling; legacy container/track aliases keep last-wins.
            if (found && name is "catalogAudioFx" or "catalogAudioFxVersion") throw Invalid("catalog-audio-fx-duplicate-field");
            value = field.Value;
            found = true;
        }
        return found;
    }

    private static EditorProjectSchemaException Invalid(string message, Exception? inner = null)
        => new(EditorProjectSchemaErrors.Invalid, message, innerException: inner);
}
