using System.Text.Json.Serialization;

namespace LiveClipper.Core;

/// <summary>Host-owned provenance of a timeline item placed from a studio event. Wire: {version:1,sourceId,eventId}.</summary>
public sealed record TimelineEventPlacement
{
    [JsonPropertyName("version")] public int Version {get;init;}=1;
    [JsonPropertyName("sourceId")] public string SourceId {get;init;}="";
    [JsonPropertyName("eventId")] public string EventId {get;init;}="";
}

public sealed partial record TimelineItem
{
    [JsonPropertyName("eventPlacement")]
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public TimelineEventPlacement? EventPlacement {get;init;}
}
