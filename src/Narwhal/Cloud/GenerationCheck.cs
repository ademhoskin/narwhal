namespace Narwhal.Cloud;

/// <summary>Persisted ordering state used to accept or reject a generation.</summary>
public sealed class GenerationState
{
    public long HighestGeneration { get; init; }
    public string? HighestPayloadDigest { get; init; }
    public long? BlockedGeneration { get; init; }
}

/// <summary>The outcome of the generation and digest ordering rules.</summary>
public enum GenerationDecision
{
    Accept,
    Idempotent,
    Replay,
    Conflict,
    Blocked,
}

/// <summary>Generation ordering from design section 2.7.</summary>
public static class GenerationCheck
{
    public static GenerationDecision Evaluate(long generation, string payloadDigest, GenerationState state)
    {
        if (state.BlockedGeneration == generation)
            return GenerationDecision.Blocked;

        if (generation > state.HighestGeneration)
            return GenerationDecision.Accept;

        if (generation == state.HighestGeneration)
        {
            return state.HighestPayloadDigest is not null &&
                   CloudEncoding.FixedTimeEquals(payloadDigest, state.HighestPayloadDigest)
                ? GenerationDecision.Idempotent
                : GenerationDecision.Conflict;
        }

        return GenerationDecision.Replay;
    }
}
