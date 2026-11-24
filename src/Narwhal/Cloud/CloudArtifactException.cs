namespace Narwhal.Cloud;

/// <summary>Stable error codes for failed artifact verification.</summary>
public static class CloudArtifactErrorCodes
{
    public const string JsonInvalid = "json_invalid";
    public const string NonCanonical = "non_canonical";
    public const string SchemaInvalid = "schema_invalid";
    public const string SemanticInvalid = "semantic_invalid";
    public const string DigestMismatch = "digest_mismatch";
    public const string SignatureInvalid = "signature_invalid";
    public const string TrustSetInvalid = "trust_set_invalid";
    public const string TargetMismatch = "target_mismatch";
    public const string ProjectMismatch = "project_mismatch";
    public const string GenerationReplay = "generation_replay";
    public const string GenerationConflict = "generation_conflict";
    public const string GenerationBlocked = "generation_blocked";
    public const string LayerInvalid = "layer_invalid";
    public const string ContextInvalid = "context_invalid";
}

/// <summary>A fail-closed artifact or trust-set validation error.</summary>
public sealed class CloudArtifactException : Exception
{
    public CloudArtifactException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
