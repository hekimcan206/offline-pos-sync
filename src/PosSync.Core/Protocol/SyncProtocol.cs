using System.Text.Json;
using System.Text.Json.Serialization;
using PosSync.Core.Model;

namespace PosSync.Core.Protocol;

/// <summary>Device → server: changes from the device outbox, oldest first.</summary>
public sealed record PushRequest(
    [property: JsonPropertyName("node")] string NodeId,
    [property: JsonPropertyName("schema")] int SchemaVersion,
    [property: JsonPropertyName("changes")] IReadOnlyList<Change> Changes);

/// <summary>
/// The outcome for every change in a push. <see cref="Duplicates"/> were already stored by an earlier attempt (a retry
/// after a lost response); the device treats them exactly like <see cref="Accepted"/> and clears them from its outbox.
/// </summary>
public sealed record PushResponse(
    [property: JsonPropertyName("accepted")] IReadOnlyList<Guid> Accepted,
    [property: JsonPropertyName("duplicates")] IReadOnlyList<Guid> Duplicates,
    [property: JsonPropertyName("rejected")] IReadOnlyList<Rejection> Rejected,
    [property: JsonPropertyName("serverHlc")] long ServerHlc);

public sealed record Rejection(
    [property: JsonPropertyName("id")] Guid ChangeId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>Server → device: changes in the store after <c>since</c>, in server sequence order.</summary>
public sealed record PullResponse(
    [property: JsonPropertyName("changes")] IReadOnlyList<PulledChange> Changes,
    [property: JsonPropertyName("cursor")] long Cursor,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("serverHlc")] long ServerHlc);

public sealed record PulledChange(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("change")] Change Change);

/// <summary>Stable rejection codes beyond the structural ones from the validator.</summary>
public static class RejectionCodes
{
    /// <summary>The change stamp is further in the future than the server allows (device clock is wrong).</summary>
    public const string ClockSkew = "clock_skew";

    /// <summary>The device tried to write into a store it is not registered for, or as another node.</summary>
    public const string Forbidden = "forbidden_scope";

    /// <summary>A change id the server already holds, but with a different body: a bug or a forged replay.</summary>
    public const string ConflictingDuplicate = "conflicting_duplicate";
}

/// <summary>The one set of JSON options used on both ends of the wire.</summary>
public static class SyncJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
