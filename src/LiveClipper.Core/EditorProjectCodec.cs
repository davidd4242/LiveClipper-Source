using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveClipper.Core;

/// <summary>Stable machine-readable codes for editor project schema failures. The HTTP layer maps these to error.code.</summary>
public static class EditorProjectSchemaErrors
{
    public const string TooNew="project-schema-too-new";
    public const string Invalid="project-schema-invalid";
}

/// <summary>Raised when a stored or submitted editor project cannot be accepted because of its schemaVersion.</summary>
public class EditorProjectSchemaException:Exception
{
    public EditorProjectSchemaException(string code,string message,int? schemaVersion=null,Exception? innerException=null):base(message,innerException)
    {
        Code=code;
        SchemaVersion=schemaVersion;
    }
    public string Code { get; }
    public int? SchemaVersion { get; }
}

/// <summary>The project was written by a newer build. Fail closed: never read it as an older schema and never overwrite it.</summary>
public sealed class EditorProjectSchemaTooNewException(int schemaVersion,int supportedVersion)
    :EditorProjectSchemaException(EditorProjectSchemaErrors.TooNew,$"editor project schemaVersion {schemaVersion} is newer than the supported {supportedVersion}",schemaVersion)
{
    public int SupportedVersion { get; } = supportedVersion;
}

/// <summary>
/// Reads, validates and stamps the root <c>schemaVersion</c> of stored editor project JSON.
/// A missing property means a legacy (version 0) document. The property name is matched case-insensitively,
/// so a CLR PascalCase <c>SchemaVersion</c> and the camelCase <c>schemaVersion</c> are the same field;
/// two spellings in one document are rejected instead of guessing which one wins.
/// </summary>
public static class EditorProjectCodec
{
    public const int CurrentSchemaVersion=1;
    public const int LegacySchemaVersion=0;
    public const string SchemaVersionPropertyName="schemaVersion";

    public static int ReadSchemaVersion(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document=JsonDocument.Parse(json);
        }
        catch(JsonException ex)
        {
            throw Invalid("editor project JSON is not valid JSON",ex);
        }
        using(document)return ReadSchemaVersion(document.RootElement);
    }

    public static int ReadSchemaVersion(JsonElement root)
    {
        if(root.ValueKind!=JsonValueKind.Object)throw Invalid("editor project JSON root must be an object");
        JsonElement? found=null;
        foreach(var property in root.EnumerateObject())
        {
            if(!string.Equals(property.Name,SchemaVersionPropertyName,StringComparison.OrdinalIgnoreCase))continue;
            if(found is not null)throw Invalid("editor project JSON has more than one schemaVersion property");
            found=property.Value;
        }
        if(found is not { } value)return LegacySchemaVersion;
        if(value.ValueKind!=JsonValueKind.Number)throw Invalid("editor project schemaVersion must be an integer number");
        var raw=value.GetRawText();
        if(raw.IndexOfAny(['.','e','E'])>=0||!value.TryGetInt32(out var version))throw Invalid($"editor project schemaVersion must be an integer, got {raw}");
        if(version<0)throw Invalid($"editor project schemaVersion must not be negative, got {version}");
        return version;
    }

    /// <summary>Returns the document's schemaVersion when this build can read it; throws <see cref="EditorProjectSchemaTooNewException"/> otherwise.</summary>
    public static int EnsureSupported(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document=JsonDocument.Parse(json);
            return EnsureSupported(document.RootElement);
        }
        catch(JsonException ex)
        {
            throw Invalid("editor project JSON is not valid JSON",ex);
        }
    }

    public static int EnsureSupported(JsonElement root)
    {
        var version=ReadSchemaVersion(root);
        if(version>CurrentSchemaVersion)throw new EditorProjectSchemaTooNewException(version,CurrentSchemaVersion);
        ReplicaExtension.ValidateProject(root);
        EditorProjectVideoFx.ValidateProject(root);
        EditorProjectAudioFx.ValidateProject(root);
        EditorProjectGraphics.ValidateProject(root);
        EditorProjectEventSelection.ValidateProject(root);
        return version;
    }

    private static readonly JsonSerializerOptions ProjectOptions=new(JsonSerializerDefaults.Web);

    public static EditorProject Deserialize(string json)
    {
        EnsureSupported(json);
        using var document=JsonDocument.Parse(json);
        var project=JsonSerializer.Deserialize<EditorProject>(json,ProjectOptions)??throw Invalid("editor project JSON root must be an object");
        return EditorProjectGraphics.ApplyDefaults(project,document.RootElement);
    }

    public static EditorProject Deserialize(JsonElement root)
    {
        EnsureSupported(root);
        var project=root.Deserialize<EditorProject>(ProjectOptions)??throw Invalid("editor project JSON root must be an object");
        return EditorProjectGraphics.ApplyDefaults(project,root);
    }

    public static bool IsSupported(int schemaVersion)=>schemaVersion>=LegacySchemaVersion&&schemaVersion<=CurrentSchemaVersion;

    /// <summary>
    /// Returns the JSON with the root schemaVersion set to <see cref="CurrentSchemaVersion"/>.
    /// Already-current documents are returned unchanged. An existing property keeps its spelling; a missing one is added as <c>schemaVersion</c>.
    /// Refuses (throws) for invalid or too-new documents so a newer project is never silently downgraded.
    /// </summary>
    public static string StampSchemaVersion(string json)
    {
        var version=EnsureSupported(json);
        using(var document=JsonDocument.Parse(json))
        {
            if(document.RootElement.EnumerateObject().Any(p=>string.Equals(p.Name,"replicaExtension",StringComparison.OrdinalIgnoreCase)))
            {
                if(version==CurrentSchemaVersion)return json;
                // Write each original value without materializing JSON numbers or rewriting the opaque archive.
                using var output=new MemoryStream();
                using(var writer=new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject();
                    var stamped=false;
                    foreach(var property in document.RootElement.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        if(string.Equals(property.Name,SchemaVersionPropertyName,StringComparison.OrdinalIgnoreCase))
                        {
                            writer.WriteNumberValue(CurrentSchemaVersion);
                            stamped=true;
                        }
                        else writer.WriteRawValue(property.Value.GetRawText());
                    }
                    if(!stamped)writer.WriteNumber(SchemaVersionPropertyName,CurrentSchemaVersion);
                    writer.WriteEndObject();
                }
                return System.Text.Encoding.UTF8.GetString(output.ToArray());
            }
        }
        JsonObject root;
        string? existing=null;
        try
        {
            root=JsonNode.Parse(json) as JsonObject??throw Invalid("editor project JSON root must be an object");
            foreach(var property in root)
            {
                if(!string.Equals(property.Key,SchemaVersionPropertyName,StringComparison.OrdinalIgnoreCase))continue;
                existing=property.Key;
                break;
            }
        }
        catch(Exception ex) when(ex is JsonException or ArgumentException)
        {
            throw Invalid("editor project JSON cannot be re-stamped (duplicate property names?)",ex);
        }
        if(version==CurrentSchemaVersion&&existing is not null)return json;
        root[existing??SchemaVersionPropertyName]=CurrentSchemaVersion;
        return root.ToJsonString();
    }

    private static EditorProjectSchemaException Invalid(string message,Exception? innerException=null)=>new(EditorProjectSchemaErrors.Invalid,message,null,innerException);
}
