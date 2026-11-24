namespace Narwhal.Cloud;

/// <summary>The configured identity of the host that accepts an artifact.</summary>
public sealed class TargetExpectation
{
    public required string Project { get; init; }
    public required string Provider { get; init; }
    public required string Region { get; init; }
    public required string Arch { get; init; }
    public required IReadOnlyDictionary<string, string> Selector { get; init; }
}

/// <summary>Compares a signed target with the local configuration.</summary>
public static class TargetValidation
{
    public static void Validate(DesiredDocument document, TargetExpectation expectation)
    {
        if (!string.Equals(document.Project, expectation.Project, StringComparison.Ordinal))
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.ProjectMismatch,
                "artifact project does not match the configured project");

        if (!string.Equals(document.Target.Provider, expectation.Provider, StringComparison.Ordinal) ||
            !string.Equals(document.Target.Region, expectation.Region, StringComparison.Ordinal) ||
            !string.Equals(document.Target.Arch, expectation.Arch, StringComparison.Ordinal))
        {
            throw Mismatch("artifact provider, region, or arch does not match the configured target");
        }

        foreach (var (key, value) in document.Target.Selector)
        {
            if (!expectation.Selector.TryGetValue(key, out var expected) ||
                !string.Equals(expected, value, StringComparison.Ordinal))
            {
                throw Mismatch("artifact selector does not match the configured target");
            }
        }
    }

    private static CloudArtifactException Mismatch(string message) =>
        new(CloudArtifactErrorCodes.TargetMismatch, message);
}
