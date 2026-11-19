using System.Text.Json.Nodes;

namespace LiveClipper.Core;

public static class StudioCommandMediaRefs
{
    public static IReadOnlyList<string> Collect(IEnumerable<EditorCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) ids.Add(id);
        }

        void AddItems(JsonNode? node, string clipIdKey)
        {
            if (node is not JsonArray items) return;
            foreach (var item in items) Add(Text(Property(item, clipIdKey)));
        }

        void AddProject(JsonNode? node)
        {
            if (node is not JsonObject project) return;
            // Match the case-insensitive property names accepted by EditorProjectCodec.
            foreach (var property in project)
            {
                if (string.Equals(property.Key, "Timeline", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Key, "SilenceTrimBackup", StringComparison.OrdinalIgnoreCase))
                {
                    AddItems(property.Value, "ClipId");
                }
                else if (string.Equals(property.Key, "NestedSequences", StringComparison.OrdinalIgnoreCase)
                    && property.Value is JsonObject sequences)
                {
                    foreach (var sequence in sequences)
                        AddItems(Property(sequence.Value, "Timeline"), "ClipId");
                }
                else if (string.Equals(property.Key, "SourceTranscripts", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Key, "CaptionOverrides", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value is JsonObject sources)
                        foreach (var source in sources) Add(source.Key);
                }
            }
        }

        foreach (var command in commands)
        {
            // The executor owns malformed-command errors; this preflight only reads known references.
            if (command is null) continue;
            switch (command.Op)
            {
                case "addItems":
                    AddItems(Property(command.Args, "items"), "clipId");
                    break;
                case "setCaptions":
                    Add(Text(Property(command.Args, "sourceClipId")));
                    break;
                case "replaceProject":
                    AddProject(Property(command.Args, "project"));
                    break;
            }
        }

        return ids.ToArray();
    }

    public static IReadOnlyList<string> FindMissing(IEnumerable<EditorCommand> commands, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var missing = new List<string>();
        foreach (var id in Collect(commands))
            if (!exists(id)) missing.Add(id);
        return missing.ToArray();
    }

    private static JsonNode? Property(JsonNode? node, string name)
    {
        if (node is not JsonObject obj) return null;
        JsonNode? value = null;
        // Web deserialization uses the last value when property casing aliases repeat.
        foreach (var property in obj)
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)) value = property.Value;
        return value;
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
