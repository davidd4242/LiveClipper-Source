using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveClipper.Core;

public static class ReplicaExtensionErrors
{
    public const string Invalid = "replica-extension-invalid";
    public const string Unsupported = "replica-extension-unsupported";
}

public sealed class ReplicaExtensionException(string code, string fieldPath, string reason)
    : EditorProjectSchemaException(code, $"{fieldPath}: {reason}")
{
    public string FieldPath { get; } = fieldPath;
}

/// <summary>An immutable archive, not a second editable projection. Numeric tokens never pass through CLR numbers.</summary>
[JsonConverter(typeof(ReplicaExtensionJsonConverter))]
public sealed class ReplicaExtension
{
    public const int CurrentVersion = 1;
    public const int CurrentCodecVersion = 1;
    private const string Path = "$.replicaExtension";
    private readonly JsonElement _json;

    public ReplicaExtension(JsonElement json)
    {
        Validate(json);
        _json = json.Clone();
    }

    public int Version => _json.GetProperty("version").GetInt32();
    public int CodecVersion => _json.GetProperty("codecVersion").GetInt32();
    public JsonElement Document => _json.GetProperty("document");
    public JsonElement ExactTimeMap => _json.GetProperty("exactTimeMap");
    public JsonElement Compatibility => _json.GetProperty("compatibility");
    public JsonElement Opaque => _json.TryGetProperty("opaque", out var value) ? value : default;
    public JsonElement Json => _json;

    internal static void ValidateProject(JsonElement project)
    {
        var found = false;
        foreach (var property in project.EnumerateObject())
        {
            if (!string.Equals(property.Name, "replicaExtension", StringComparison.OrdinalIgnoreCase)) continue;
            if (found) throw Invalid(Path, "duplicate replicaExtension property");
            found = true;
            if (property.Value.ValueKind != JsonValueKind.Null) Validate(property.Value);
        }
    }

    private static void Validate(JsonElement json)
    {
        Object(json, Path);
        UniqueFields(json, Path);
        VersionField(json, "version", CurrentVersion, Path, required: true);
        VersionField(json, "codecVersion", CurrentCodecVersion, Path, required: true);
        Object(Required(json, "document", Path), Path + ".document");
        Object(Required(json, "exactTimeMap", Path), Path + ".exactTimeMap");
        var compatibility = Required(json, "compatibility", Path);
        Object(compatibility, Path + ".compatibility");
        UniqueFields(compatibility, Path + ".compatibility");
        VersionField(compatibility, "requiredVersion", CurrentVersion, Path + ".compatibility");
        VersionField(compatibility, "requiredCodecVersion", CurrentCodecVersion, Path + ".compatibility");
        foreach (var property in compatibility.EnumerateObject())
        {
            if (property.Name is "requiredVersion" or "requiredCodecVersion") continue;
            // Unknown optional metadata is archived; unimplemented requirements must not be treated as warnings.
            if (property.Name.StartsWith("required", StringComparison.OrdinalIgnoreCase))
                throw Unsupported(Path + ".compatibility." + property.Name, "unknown required compatibility constraint");
        }
        if (json.TryGetProperty("opaque", out var opaque)) Object(opaque, Path + ".opaque");
    }

    private static void Object(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid(path, "must be an object");
    }

    private static void UniqueFields(JsonElement value, string path)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw Invalid(path + "." + property.Name, "duplicate property");
    }

    private static JsonElement Required(JsonElement value, string name, string path)
        => value.TryGetProperty(name, out var result) ? result : throw Invalid(path + "." + name, "is required");

    private static void VersionField(JsonElement value, string name, int supported, string path, bool required = false)
    {
        if (!value.TryGetProperty(name, out var version))
        {
            if (required) throw Invalid(path + "." + name, "is required");
            return;
        }
        var raw = version.GetRawText();
        if (version.ValueKind != JsonValueKind.Number || raw.IndexOfAny(['.', 'e', 'E']) >= 0 ||
            !version.TryGetInt32(out var number) || number < 1)
            throw Invalid(path + "." + name, "must be a positive integer version");
        if (number > supported) throw Unsupported(path + "." + name, $"required version {raw} exceeds supported version {supported}");
    }

    private static ReplicaExtensionException Invalid(string path, string reason) => new(ReplicaExtensionErrors.Invalid, path, reason);
    private static ReplicaExtensionException Unsupported(string path, string reason) => new(ReplicaExtensionErrors.Unsupported, path, reason);
}

public sealed class ReplicaExtensionJsonConverter : JsonConverter<ReplicaExtension>
{
    public override ReplicaExtension Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return new ReplicaExtension(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ReplicaExtension value, JsonSerializerOptions options)
        => writer.WriteRawValue(value.Json.GetRawText());
}
