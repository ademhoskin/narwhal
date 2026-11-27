using System.Text.RegularExpressions;

namespace Narwhal.Reconcile;

/// <summary>Resolves the provider host identity used in status, best effort and never fatal.</summary>
public static partial class HostIdentity
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(2);

    public const string EnvironmentVariable = "NARWHAL_HOST_ID";
    public const string FilePath = "/etc/narwhal/host-id";

    /// <summary>Returns a configured, environment, file, or provider metadata identity, or null.</summary>
    public static async Task<string?> ResolveAsync(
        string provider,
        string? configured,
        CancellationToken cancellationToken)
    {
        if (Valid(configured))
            return configured;
        if (Valid(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            return Environment.GetEnvironmentVariable(EnvironmentVariable);

        try
        {
            if (File.Exists(FilePath))
            {
                var fileValue = (await File.ReadAllTextAsync(FilePath, cancellationToken)).Trim();
                if (Valid(fileValue))
                    return fileValue;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataTimeout);
        try
        {
            var value = provider switch
            {
                "aws" => await AwsAsync(timeout.Token),
                "digitalocean" => await GetAsync(
                    "http://169.254.169.254/metadata/v1/id",
                    timeout.Token),
                "gcp" => await GetAsync(
                    "http://metadata.google.internal/computeMetadata/v1/instance/id",
                    timeout.Token,
                    ("Metadata-Flavor", "Google")),
                _ => null,
            };
            return Valid(value) ? value!.Trim() : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> AwsAsync(CancellationToken cancellationToken)
    {
        using var tokenRequest = new HttpRequestMessage(
            HttpMethod.Put,
            "http://169.254.169.254/latest/api/token");
        tokenRequest.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token-ttl-seconds", "60");
        using var tokenResponse = await Http.SendAsync(tokenRequest, cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
            return null;
        var token = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "http://169.254.169.254/latest/meta-data/instance-id");
        request.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", token);
        using var response = await Http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken)
            : null;
    }

    private static async Task<string?> GetAsync(
        string url,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        using var response = await Http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken)
            : null;
    }

    private static bool Valid(string? value) =>
        value is { Length: > 0 and <= 256 } &&
        HostIdPattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostIdPattern();
}
