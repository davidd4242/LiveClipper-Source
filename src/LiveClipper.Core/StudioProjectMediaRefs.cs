using System.Text.Encodings.Web;
using System.Text.Json;

namespace LiveClipper.Core;

public static class StudioProjectMediaRefs
{
    public sealed record Location(string MediaId, string EntityId, string FieldPath);

    private static readonly JsonSerializerOptions PathStrings = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static (IReadOnlyList<string> Missing, IReadOnlyList<Location> Locations) FindMissing(
        EditorProject project, string projectId, Func<string, bool> clipExists, Func<string, string, bool> assetExists)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(clipExists);
        ArgumentNullException.ThrowIfNull(assetExists);
        var missing = new List<string>();
        var locations = new List<Location>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddMissing(string id, string entityId, string fieldPath)
        {
            if (seen.Add(id)) missing.Add(id);
            locations.Add(new(id, entityId, fieldPath));
        }

        void AddClip(string? id, string entityId, string fieldPath)
        {
            if (!string.IsNullOrWhiteSpace(id) && !clipExists(id)) AddMissing(id, entityId, fieldPath);
        }

        void AddAsset(string? id, string kind, string entityId, string fieldPath)
        {
            if (!string.IsNullOrWhiteSpace(id) && !assetExists(id, kind)) AddMissing(id, entityId, fieldPath);
        }

        void AddItems(TimelineItem[]? items, string fieldPath)
        {
            if (items is null) return;
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item is null) continue;
                var path = $"{fieldPath}[{i}]";
                AddClip(item.ClipId, item.Id, path + ".clipId");
                AddClip(item.EventPlacement?.SourceId, item.Id, path + ".eventPlacement.sourceId");
                AddAsset(item.StickerAssetId, "sticker", item.Id, path + ".stickerAssetId");
                AddAsset(item.SoundAssetId, "effect", item.Id, path + ".soundAssetId");
            }
        }

        AddAsset(project.MusicAssetId, "music", projectId, "$.musicAssetId");
        AddAsset(project.EffectAssetId, "effect", projectId, "$.effectAssetId");
        AddItems(project.Timeline, "$.timeline");
        AddItems(project.SilenceTrimBackup, "$.silenceTrimBackup");
        if (project.NestedSequences is { } sequences)
            foreach (var pair in sequences)
                AddItems(pair.Value?.Timeline, "$.nestedSequences" + Key(pair.Key) + ".timeline");
        if (project.SourceTranscripts is { } transcripts)
            foreach (var id in transcripts.Keys)
                AddClip(id, id, "$.sourceTranscripts" + Key(id));
        if (project.CaptionOverrides is { } captions)
            foreach (var id in captions.Keys)
                AddClip(id, id, "$.captionOverrides" + Key(id));

        if (project.TimelineCaptions is { } timelineCaptions)
            for (var i = 0; i < timelineCaptions.Length; i++)
                AddClip(timelineCaptions[i].SourceClipId, timelineCaptions[i].Id, $"$.timelineCaptions[{i}].sourceClipId");
        if (project.ClearedEventSelections is { } cleared)
            for (var i = 0; i < cleared.Length; i++)
                AddClip(cleared[i].SourceId, projectId, $"$.clearedEventSelections[{i}].sourceId");

        CollectHostedReplicaV1(project.ReplicaExtension, projectId, AddClip, AddAsset);
        return (missing.ToArray(), locations.ToArray());
    }

    private static void CollectHostedReplicaV1(
        ReplicaExtension? extension, string projectId,
        Action<string, string, string> addClip, Action<string, string, string, string> addAsset)
    {
        if (extension is null || extension.Version != 1 || extension.CodecVersion != 1
            || StringField(extension.Compatibility, "projection") != "lc-editor-next-active-sequence-v1") return;
        var document = extension.Document;
        if (!IsHostedWrapper(document) || !ObjectField(document, "sequence", out var sequence)) return;
        const string rootPath = "$.replicaExtension.document";
        CollectSequence(sequence, rootPath + ".sequence");
        // Conflicting backups have no independent host namespace; this is not structural validation.
        if (ObjectField(document, "projectState", out var state) && ObjectField(state, "sequence", out var mirror)
            && JsonElement.DeepEquals(sequence, mirror))
            CollectSequence(mirror, rootPath + ".projectState.sequence");

        void CollectSequence(JsonElement root, string path)
        {
            if (ObjectField(root, "hostMusic", out var music) && StringField(music, "musicAssetId") is { } assetId)
                addAsset(assetId, "music", projectId, path + ".hostMusic.musicAssetId");
            var pending = new Stack<(JsonElement Node, string Path, bool IsSequence)>();
            pending.Push((root, path, true));
            while (pending.TryPop(out var entry))
            {
                var (node, nodePath, isSequence) = entry;
                if (isSequence)
                {
                    if (ObjectField(node, "hostCaptions", out var hostCaptions))
                        foreach (var caption in hostCaptions.EnumerateObject())
                            addClip(caption.Name, caption.Name, nodePath + ".hostCaptions" + Key(caption.Name));
                    CollectSources(node, "hostBinSources", "sourceId", nodePath);
                    CollectSources(node, "hostEventOriginals", "sourceId", nodePath);
                    CollectSources(node, "hostClearedEventSelections", "sourceId", nodePath);
                    CollectSources(node, "captions", "sourceClipId", nodePath);
                    PushTracks(node, "audioTracks", nodePath);
                    PushTracks(node, "videoTracks", nodePath);
                    continue;
                }
                if (StringField(node, "sourceId") is { } id)
                    addClip(id, StringField(node, "key") is { Length: > 0 } key ? key : projectId, nodePath + ".sourceId");
                if (ObjectField(node, "eventPlacement", out var placement) && StringField(placement, "sourceId") is { } eventSource)
                    addClip(eventSource, StringField(node, "key") ?? projectId, nodePath + ".eventPlacement.sourceId");
                if (ObjectField(node, "multicam", out var multicam) && ArrayField(multicam, "angles", out var angles))
                    for (var i = angles.GetArrayLength() - 1; i >= 0; i--)
                    {
                        var angle = angles[i];
                        if (angle.ValueKind != JsonValueKind.Object) continue;
                        var anglePath = $"{nodePath}.multicam.angles[{i}]";
                        PushClips(angle, anglePath);
                        if (ObjectField(angle, "clip", out var snapshot)) pending.Push((snapshot, anglePath + ".clip", false));
                    }
                if (ObjectField(node, "nestedSeq", out var nested)) pending.Push((nested, nodePath + ".nestedSeq", true));
            }

            void CollectSources(JsonElement parent, string name, string field, string parentPath)
            {
                if (!ArrayField(parent, name, out var entries)) return;
                for (var i = 0; i < entries.GetArrayLength(); i++)
                    if (entries[i].ValueKind == JsonValueKind.Object && StringField(entries[i], field) is { } source)
                        addClip(source, StringField(entries[i], "id") ?? StringField(entries[i], "key") ?? projectId,
                            $"{parentPath}.{name}[{i}].{field}");
            }

            // Push in reverse so DFS visits video before audio, nested before angles, and clip before clips.
            void PushTracks(JsonElement parent, string name, string parentPath)
            {
                if (!ArrayField(parent, name, out var tracks)) return;
                for (var i = tracks.GetArrayLength() - 1; i >= 0; i--)
                    if (tracks[i].ValueKind == JsonValueKind.Object) PushClips(tracks[i], $"{parentPath}.{name}[{i}]");
            }

            void PushClips(JsonElement parent, string parentPath)
            {
                if (!ArrayField(parent, "clips", out var clips)) return;
                for (var i = clips.GetArrayLength() - 1; i >= 0; i--)
                    if (clips[i].ValueKind == JsonValueKind.Object) pending.Push((clips[i], $"{parentPath}.clips[{i}]", false));
            }
        }
    }

    /// <summary>Copies a project and remaps only registered source-media identities; performs no storage operations.</summary>
    public static EditorProject Remap(EditorProject project, IReadOnlyDictionary<string, string> clipIdMap,
        IReadOnlyDictionary<string, string>? eventIdMap = null, IReadOnlyDictionary<string, string>? assetIdMap = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clipIdMap);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in clipIdMap) map.Add(pair.Key, pair.Value);
        var targetSources = new Dictionary<string, string>(StringComparer.Ordinal);
        string Map(string id, string path)
        {
            if (string.IsNullOrWhiteSpace(id)) return id;
            if (!map.TryGetValue(id, out var target) || string.IsNullOrWhiteSpace(target))
                throw new ArgumentException($"Missing non-empty source mapping at {path}: {id}", nameof(clipIdMap));
            if (targetSources.TryGetValue(target, out var source) && source != id)
                throw new ArgumentException($"Source mapping key collision at {path}: {source} and {id} -> {target}", nameof(clipIdMap));
            targetSources[target] = id;
            return target;
        }

        var events = eventIdMap is null ? null : new Dictionary<string, string>(eventIdMap, StringComparer.Ordinal);
        var targetEvents = new Dictionary<string, string>(StringComparer.Ordinal);
        string MapEvent(string id, string path)
        {
            if (events is null) return id;
            if (string.IsNullOrWhiteSpace(id) || !events.TryGetValue(id, out var target) || string.IsNullOrWhiteSpace(target))
                throw new ArgumentException($"Missing non-empty event mapping at {path}: {id}", nameof(eventIdMap));
            if (targetEvents.TryGetValue(target, out var source) && source != id)
                throw new ArgumentException($"Event mapping key collision at {path}: {source} and {id} -> {target}", nameof(eventIdMap));
            targetEvents[target] = id;
            return target;
        }

        var assets = assetIdMap is null ? null : new Dictionary<string, string>(assetIdMap, StringComparer.Ordinal);
        var targetAssets = new Dictionary<string, string>(StringComparer.Ordinal);
        string? MapAsset(string? id, string path)
        {
            if (assets is null || string.IsNullOrWhiteSpace(id)) return id;
            if (!assets.TryGetValue(id, out var target) || string.IsNullOrWhiteSpace(target))
                throw new ArgumentException($"Missing non-empty asset mapping at {path}: {id}", nameof(assetIdMap));
            if (targetAssets.TryGetValue(target, out var source) && source != id)
                throw new ArgumentException($"Asset mapping key collision at {path}: {source} and {id} -> {target}", nameof(assetIdMap));
            targetAssets[target] = id;
            return target;
        }

        // JSON cloning detaches every mutable array/dictionary, including effect parameters and nested timelines.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var copy = JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(project, options), options)!;
        TimelineItem[]? Items(TimelineItem[]? items, string path) => items?.Select((item, i) => item with
        {
            ClipId = Map(item.ClipId, $"{path}[{i}].clipId"),
            StickerAssetId = MapAsset(item.StickerAssetId, $"{path}[{i}].stickerAssetId"),
            SoundAssetId = MapAsset(item.SoundAssetId, $"{path}[{i}].soundAssetId"),
            EventPlacement = item.EventPlacement is { } placement
                ? placement with
                {
                    SourceId = Map(placement.SourceId, $"{path}[{i}].eventPlacement.sourceId"),
                    EventId = MapEvent(placement.EventId, $"{path}[{i}].eventPlacement.eventId")
                } : null
        }).ToArray();
        Dictionary<string, TranscriptSegment[]>? Captions(Dictionary<string, TranscriptSegment[]>? values, string path)
        {
            if (values is null) return null;
            var result = new Dictionary<string, TranscriptSegment[]>(StringComparer.Ordinal);
            foreach (var pair in values)
                if (!result.TryAdd(Map(pair.Key, path + Key(pair.Key)), pair.Value))
                    throw new ArgumentException($"Source mapping key collision at {path}", nameof(clipIdMap));
            return result;
        }

        return copy with
        {
            MusicAssetId = MapAsset(copy.MusicAssetId, "$.musicAssetId"),
            EffectAssetId = MapAsset(copy.EffectAssetId, "$.effectAssetId"),
            Timeline = Items(copy.Timeline, "$.timeline"),
            SilenceTrimBackup = Items(copy.SilenceTrimBackup, "$.silenceTrimBackup"),
            NestedSequences = copy.NestedSequences?.ToDictionary(pair => pair.Key,
                pair => pair.Value with { Timeline = Items(pair.Value.Timeline, "$.nestedSequences" + Key(pair.Key) + ".timeline")! },
                StringComparer.Ordinal),
            CaptionOverrides = Captions(copy.CaptionOverrides, "$.captionOverrides"),
            SourceTranscripts = Captions(copy.SourceTranscripts, "$.sourceTranscripts"),
            TimelineCaptions = copy.TimelineCaptions?.Select((cue, i) => cue with
            {
                SourceClipId = cue.SourceClipId is { } id ? Map(id, $"$.timelineCaptions[{i}].sourceClipId") : null
            }).ToArray(),
            ClearedEventSelections = copy.ClearedEventSelections?.Select((entry, i) => entry with
            {
                SourceId = Map(entry.SourceId, $"$.clearedEventSelections[{i}].sourceId")
            }).ToArray(),
            ReplicaExtension = RemapHostedReplica(copy.ReplicaExtension, Map, events is null ? null : MapEvent, assets is null ? null : MapAsset)
        };
    }

    private static ReplicaExtension? RemapHostedReplica(ReplicaExtension? extension, Func<string, string, string> map,
        Func<string, string, string>? mapEvent, Func<string?, string, string?>? mapAsset)
    {
        if (extension is null) return null;
        const string path = "$.replicaExtension.document";
        var document = extension.Document;
        if (extension.Version != 1 || extension.CodecVersion != 1
            || StringField(extension.Compatibility, "projection") != "lc-editor-next-active-sequence-v1"
            || !IsHostedWrapper(document) || !ObjectField(document, "sequence", out var sequence))
            throw new NotSupportedException(path + ": only hosted active-sequence-v1 archives can be remapped");
        CheckCatalog(document, path);
        if (document.TryGetProperty("projectState", out var state))
        {
            if (state.ValueKind != JsonValueKind.Object || !ObjectField(state, "sequence", out var mirror)
                || !JsonElement.DeepEquals(sequence, mirror))
                throw new NotSupportedException(path + ".projectState.sequence: inconsistent active-sequence mirror");
            CheckCatalog(state, path + ".projectState");
            foreach (var name in new[] { "sequences", "activeSequence" })
            {
                var hasRoot = document.TryGetProperty(name, out var rootField);
                var hasState = state.TryGetProperty(name, out var stateField);
                if (name == "sequences" && hasRoot != hasState
                    || hasRoot && hasState && !JsonElement.DeepEquals(rootField, stateField))
                    throw new NotSupportedException(path + ".projectState." + name + ": inconsistent sequence catalog mirror");
            }
        }
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
            WriteObject(writer, extension.Json, (name, value) =>
            {
                if (name == "document") WriteDocument(writer, value);
                else value.WriteTo(writer);
            });
        using var result = JsonDocument.Parse(output.ToArray());
        return new ReplicaExtension(result.RootElement);

        void CheckCatalog(JsonElement owner, string ownerPath)
        {
            if (!owner.TryGetProperty("sequences", out var catalog) || catalog.ValueKind == JsonValueKind.Null) return;
            if (catalog.ValueKind != JsonValueKind.Array) throw new NotSupportedException(ownerPath + ".sequences: expected catalog array");
            if (catalog.GetArrayLength() == 0) return;
            if (catalog.GetArrayLength() != 1 || catalog[0].ValueKind != JsonValueKind.Object)
                throw new NotSupportedException(ownerPath + ".sequences: additional sequences are not supported");
            var entry = catalog[0];
            // The native catalog is metadata plus a separate clips list, not a sequence graph.
            // Accept only an explicitly identified current entry whose list is empty.
            if (StringField(entry, "kind") != "main" || StringField(entry, "name") is not { } name
                || name != StringField(sequence, "name") || !ArrayField(entry, "clips", out var clips) || clips.GetArrayLength() != 0
                || entry.EnumerateObject().Any(p => p.Name is not ("id" or "name" or "kind" or "frames" or "clips")))
                throw new NotSupportedException(ownerPath + ".sequences[0]: not a provably metadata-only current sequence");
            if (owner.TryGetProperty("activeSequence", out var active)
                && (active.ValueKind != JsonValueKind.String || active.GetString() != name))
                throw new NotSupportedException(ownerPath + ".activeSequence: not the current sequence name");
        }
        void WriteDocument(Utf8JsonWriter writer, JsonElement value) => WriteObject(writer, value, (name, field) =>
        {
            if (name == "sequence") WriteSequence(writer, field, path + ".sequence");
            else if (name == "projectState") WriteObject(writer, field, (stateName, stateField) =>
            {
                if (stateName == "sequence") WriteSequence(writer, stateField, path + ".projectState.sequence");
                else stateField.WriteTo(writer);
            });
            else field.WriteTo(writer);
        });
        void WriteSequence(Utf8JsonWriter writer, JsonElement value, string nodePath) => WriteObject(writer, value, (name, field) =>
        {
            switch (name)
            {
                case "videoTracks": case "audioTracks":
                    WriteArray(writer, field, nodePath + "." + name, (track, trackPath) =>
                        WriteObject(writer, track, (trackName, trackField) =>
                        {
                            if (trackName == "clips") WriteClips(writer, trackField, trackPath + ".clips");
                            else trackField.WriteTo(writer);
                        }));
                    break;
                case "hostMusic":
                    if (mapAsset is null || field.ValueKind == JsonValueKind.Null) { field.WriteTo(writer); break; }
                    WriteObject(writer, field, (musicName, musicField) =>
                    {
                        if (musicName != "musicAssetId" || musicField.ValueKind == JsonValueKind.Null) musicField.WriteTo(writer);
                        else
                        {
                            if (musicField.ValueKind != JsonValueKind.String)
                                throw new NotSupportedException(nodePath + ".hostMusic.musicAssetId: expected asset string");
                            writer.WriteStringValue(mapAsset(musicField.GetString(), nodePath + ".hostMusic.musicAssetId"));
                        }
                    });
                    break;
                case "hostCaptions":
                    if (field.ValueKind == JsonValueKind.Null) { field.WriteTo(writer); break; }
                    if (field.ValueKind != JsonValueKind.Object) throw new NotSupportedException(nodePath + ".hostCaptions: expected object");
                    writer.WriteStartObject();
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var caption in field.EnumerateObject())
                    {
                        var key = map(caption.Name, nodePath + ".hostCaptions" + Key(caption.Name));
                        if (!keys.Add(key)) throw new ArgumentException(nodePath + ".hostCaptions: source mapping key collision");
                        writer.WritePropertyName(key);
                        caption.Value.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                    break;
                case "captions":
                    WriteSources(writer, field, "sourceClipId", nodePath + ".captions");
                    break;
                case "hostBinSources": case "hostEventOriginals": case "hostClearedEventSelections":
                    WriteSources(writer, field, "sourceId", nodePath + "." + name);
                    break;
                default: field.WriteTo(writer); break;
            }
        });
        void WriteClips(Utf8JsonWriter writer, JsonElement value, string nodePath)
            => WriteArray(writer, value, nodePath, (clip, clipPath) => WriteClip(writer, clip, clipPath));
        void WriteClip(Utf8JsonWriter writer, JsonElement value, string nodePath) => WriteObject(writer, value, (name, field) =>
        {
            switch (name)
            {
                case "sourceId": WriteSource(writer, field, nodePath + ".sourceId"); break;
                case "eventPlacement":
                    if (field.ValueKind == JsonValueKind.Null) field.WriteTo(writer);
                    else
                    {
                        var eventPath = nodePath + ".eventPlacement";
                        var eventId = mapEvent is null ? null : mapEvent(StringField(field, "eventId") ?? "", eventPath + ".eventId");
                        WriteObject(writer, field, (placementName, placementField) =>
                        {
                            if (placementName == "sourceId") WriteSource(writer, placementField, eventPath + ".sourceId");
                            else if (placementName == "eventId" && mapEvent is not null) writer.WriteStringValue(eventId);
                            else placementField.WriteTo(writer);
                        });
                    }
                    break;
                case "nestedSeq":
                    if (field.ValueKind == JsonValueKind.Null) field.WriteTo(writer);
                    else WriteSequence(writer, field, nodePath + ".nestedSeq");
                    break;
                case "multicam":
                    if (field.ValueKind == JsonValueKind.Null) { field.WriteTo(writer); break; }
                    WriteObject(writer, field, (multiName, multiField) =>
                    {
                        if (multiName != "angles") { multiField.WriteTo(writer); return; }
                        WriteArray(writer, multiField, nodePath + ".multicam.angles", (angle, anglePath) =>
                            WriteObject(writer, angle, (angleName, angleField) =>
                            {
                                if (angleName == "clips") WriteClips(writer, angleField, anglePath + ".clips");
                                else if (angleName == "clip" && angleField.ValueKind != JsonValueKind.Null) WriteClip(writer, angleField, anglePath + ".clip");
                                else angleField.WriteTo(writer);
                            }));
                    });
                    break;
                default: field.WriteTo(writer); break;
            }
        });
        void WriteSources(Utf8JsonWriter writer, JsonElement value, string field, string nodePath)
            => WriteArray(writer, value, nodePath, (entry, entryPath) => WriteSourceObject(writer, entry, field, entryPath));
        void WriteSourceObject(Utf8JsonWriter writer, JsonElement value, string sourceField, string nodePath)
            => WriteObject(writer, value, (name, field) =>
            {
                if (name == sourceField) WriteSource(writer, field, nodePath + "." + name);
                else field.WriteTo(writer);
            });
        void WriteSource(Utf8JsonWriter writer, JsonElement field, string fieldPath)
        {
            if (field.ValueKind == JsonValueKind.Null) { field.WriteTo(writer); return; }
            if (field.ValueKind != JsonValueKind.String) throw new NotSupportedException(fieldPath + ": expected source string");
            writer.WriteStringValue(map(field.GetString()!, fieldPath));
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement value, Action<string, JsonElement> writeValue)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new NotSupportedException("Expected hosted object");
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            writeValue(property.Name, property.Value);
        }
        writer.WriteEndObject();
    }

    private static void WriteArray(Utf8JsonWriter writer, JsonElement value, string path, Action<JsonElement, string> writeValue)
    {
        if (value.ValueKind == JsonValueKind.Null) { value.WriteTo(writer); return; }
        if (value.ValueKind != JsonValueKind.Array) throw new NotSupportedException(path + ": expected hosted array");
        writer.WriteStartArray();
        var i = 0;
        foreach (var entry in value.EnumerateArray()) writeValue(entry, $"{path}[{i++}]");
        writer.WriteEndArray();
    }

    private static bool IsHostedWrapper(JsonElement document)
    {
        var hasMarker = document.TryGetProperty("__fcproj", out var marker);
        var hasFormat = document.TryGetProperty("format", out var format);
        var hasVersion = document.TryGetProperty("version", out var version);
        if (hasMarker && !NumberIs(marker, 1) || hasVersion && !NumberIs(version, 1)) return false;
        if (hasFormat && (format.ValueKind != JsonValueKind.String || format.GetString() != "filmcraft-project" || !hasVersion)) return false;
        if (document.TryGetProperty("bridgeSchema", out var bridge) && !NumberIs(bridge, 2)) return false;
        return hasMarker || hasFormat && hasVersion;
    }

    private static bool NumberIs(JsonElement value, int expected)
        => value.ValueKind == JsonValueKind.Number && JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(expected));

    private static string? StringField(JsonElement value, string name)
        => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static bool ObjectField(JsonElement value, string name, out JsonElement field)
        => value.TryGetProperty(name, out field) && field.ValueKind == JsonValueKind.Object;

    private static bool ArrayField(JsonElement value, string name, out JsonElement field)
        => value.TryGetProperty(name, out field) && field.ValueKind == JsonValueKind.Array;

    private static string Key(string value) => "[" + JsonSerializer.Serialize(value, PathStrings) + "]";
}
