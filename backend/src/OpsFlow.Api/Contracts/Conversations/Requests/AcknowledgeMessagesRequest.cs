using System.ComponentModel.DataAnnotations;

namespace OpsFlow.Api.Contracts;

/// <summary>At least one value is required; each is the sequence of a chat message the caller has received or displayed.</summary>
public sealed record AcknowledgeMessagesRequest(
    [Range(1, int.MaxValue)] int? DeliveredSequence,
    [Range(1, int.MaxValue)] int? ReadSequence
);
