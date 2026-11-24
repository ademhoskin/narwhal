using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Narwhal.Cloud;

/// <summary>Semantic rules from design section 2.3 that do not need registry access.</summary>
internal static class DesiredSemantics
{
    public static void Validate(DesiredDocument document)
    {
        var services = document.Services;
        var platform = "linux/" + document.Target.Arch;

        var trustIds = new HashSet<string>(StringComparer.Ordinal);
        if (document.Trust is not null)
        {
            foreach (var key in document.Trust)
            {
                if (!trustIds.Add(key.KeyId))
                    throw Invalid("payload.trust", "duplicate key ID");
            }
        }

        foreach (var (name, service) in services)
        {
            if (!document.Images.TryGetValue(service.Image, out var image))
                throw Invalid($"services.{name}.image", "image digest is not present in images");
            if (!string.Equals(image.Platform, platform, StringComparison.Ordinal))
                throw Invalid($"services.{name}.image", "image platform does not match the target architecture");

            if (service.Env is not null && service.Secrets is not null)
            {
                foreach (var key in service.Env.Keys)
                {
                    if (service.Secrets.ContainsKey(key))
                        throw Invalid($"services.{name}", "a key appears in both env and secrets");
                }
            }

            foreach (var port in service.Ports)
            {
                if (!IsValidPortSpec(port))
                    throw Invalid($"services.{name}.ports", "invalid port mapping or out-of-range port");
            }

            foreach (var volume in service.Volumes)
            {
                if (!IsValidVolumeSpec(volume))
                    throw Invalid($"services.{name}.volumes", "invalid volume; binds must be under /srv/narwhal/");
            }

            foreach (var dependency in service.DependsOn)
            {
                if (!services.ContainsKey(dependency))
                    throw Invalid($"services.{name}.depends_on", "unknown service");
            }
        }

        ValidateAcyclic(services);

        foreach (var rule in document.Target.Ingress)
        {
            if (rule.Port == 22)
                throw Invalid("payload.target.ingress", "port 22 is not allowed in target ingress");
            foreach (var source in rule.Source)
            {
                if (!IsValidCidr(source))
                    throw Invalid("payload.target.ingress.source", "expected an IPv4 or IPv6 CIDR");
            }
        }

        if (document.Previous is not null && document.Generation <= document.Previous.Generation)
            throw Invalid("payload.previous", "previous generation must be lower than generation");
    }

    private static void ValidateAcyclic(IReadOnlyDictionary<string, ServiceSpec> services)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 visiting, 2 done
        foreach (var name in services.Keys)
            Visit(name);

        void Visit(string name)
        {
            if (state.TryGetValue(name, out var current))
            {
                if (current == 2)
                    return;
                throw Invalid("payload.services", "dependency cycle");
            }

            state[name] = 1;
            foreach (var dependency in services[name].DependsOn)
                Visit(dependency);
            state[name] = 2;
        }
    }

    private static bool IsValidPortSpec(string spec)
    {
        var value = spec;
        var slash = value.LastIndexOf('/');
        if (slash >= 0)
        {
            var protocol = value[(slash + 1)..];
            if (protocol is not ("tcp" or "udp" or "sctp"))
                return false;
            value = value[..slash];
        }

        var parts = value.Split(':');
        if (parts.Length is < 1 or > 3)
            return false;

        foreach (var part in parts)
        {
            if (part.Length == 0)
                return false;
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                return false;
        }

        return true;
    }

    private static bool IsValidVolumeSpec(string spec)
    {
        var parts = spec.Split(':');
        if (parts.Length is < 2 or > 3)
            return false;

        var source = parts[0];
        var destination = parts[1];
        if (source.Length == 0 || destination.Length == 0 || !destination.StartsWith('/'))
            return false;
        if (parts.Length == 3 && parts[2].Length == 0)
            return false;

        if (!source.StartsWith('/'))
            return IsNamedVolume(source);

        const string root = "/srv/narwhal/";
        if (!source.StartsWith(root, StringComparison.Ordinal))
            return false;

        foreach (var segment in source[root.Length..].Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
        }

        return true;
    }

    private static bool IsNamedVolume(string source)
    {
        if (source.Length == 0 || !char.IsAsciiLetterOrDigit(source[0]))
            return false;
        foreach (var c in source)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or '-'))
                return false;
        }

        return true;
    }

    private static bool IsValidCidr(string value)
    {
        if (value.Contains('%'))
            return false;

        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1 || value.IndexOf('/', slash + 1) >= 0)
            return false;

        if (!IPAddress.TryParse(value[..slash], out var address))
            return false;
        if (!int.TryParse(value[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
            return false;

        var maximum = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return prefix >= 0 && prefix <= maximum;
    }

    private static CloudArtifactException Invalid(string path, string reason) =>
        JsonShape.Error(CloudArtifactErrorCodes.SemanticInvalid, path, reason);
}
