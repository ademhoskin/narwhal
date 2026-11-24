using System.Text.RegularExpressions;

namespace Narwhal.Cloud;

internal static partial class CloudPatterns
{
    [GeneratedRegex("""^[a-z0-9]([a-z0-9._-]{0,61}[a-z0-9])?$""", RegexOptions.CultureInvariant)]
    public static partial Regex ProjectName();

    [GeneratedRegex("""^sha256:[0-9a-f]{64}$""", RegexOptions.CultureInvariant)]
    public static partial Regex Digest();

    [GeneratedRegex("""^[0-9a-f]{16}$""", RegexOptions.CultureInvariant)]
    public static partial Regex KeyId();

    [GeneratedRegex("""^[0-9a-f]{64}$""", RegexOptions.CultureInvariant)]
    public static partial Regex Hex64();

    [GeneratedRegex("""^[a-z0-9]+([._-][a-z0-9]+)*(:[0-9]+)?(/[a-z0-9]+([._-][a-z0-9]+)*)+$""", RegexOptions.CultureInvariant)]
    public static partial Regex Repository();

    [GeneratedRegex("""^[0-9]{1,5}(:[0-9]{1,5}){0,2}(/(tcp|udp|sctp))?$""", RegexOptions.CultureInvariant)]
    public static partial Regex Port();

    [GeneratedRegex("""^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?Z$""", RegexOptions.CultureInvariant)]
    public static partial Regex Timestamp();

    [GeneratedRegex("""^[A-Za-z0-9+/]*={0,2}$""", RegexOptions.CultureInvariant)]
    public static partial Regex Base64();

    [GeneratedRegex("""^[A-Za-z0-9_-]*$""", RegexOptions.CultureInvariant)]
    public static partial Regex Base64Url();
}
