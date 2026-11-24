using Narwhal.Cloud;

namespace Narwhal.Reconcile;

public static class ServiceActions
{
    public const string None = "none";
    public const string Create = "create";
    public const string Recreate = "recreate";
    public const string Start = "start";
    public const string Stop = "stop";
}

public sealed record ServiceChange(ServicePlan Plan, string Action, ContainerObservation? Observed, string Reason)
{
    public bool Mutates => Action != ServiceActions.None;
}

/// <summary>The desired-versus-actual plan. Podman observations are the source of truth.</summary>
public sealed class DiffPlan
{
    public required IReadOnlyList<ServiceChange> Changes { get; init; }
    public required IReadOnlyList<ContainerObservation> Orphans { get; init; }

    public bool HasMutations => Changes.Any(change => change.Mutates) || Orphans.Count > 0;

    public IReadOnlyList<ServiceChange> InDependencyOrder => Changes.Where(change => change.Action != ServiceActions.Stop).ToList();
}

public static class ServiceDiffer
{
    /// <summary>Computes one action per desired service plus orphan containers.</summary>
    public static DiffPlan Diff(
        IReadOnlyList<ServicePlan> plans,
        IReadOnlyList<ContainerObservation> observations,
        IReadOnlyDictionary<string, bool> imageMatches)
    {
        var byService = new Dictionary<string, List<ContainerObservation>>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            var service = observation.Service;
            if (service.Length == 0)
                continue;
            if (!byService.TryGetValue(service, out var list))
                byService[service] = list = [];
            list.Add(observation);
        }

        var changes = new List<ServiceChange>(plans.Count);
        var orphans = new List<ContainerObservation>();

        foreach (var plan in plans)
        {
            byService.TryGetValue(plan.Name, out var candidates);
            var exact = candidates?.FirstOrDefault(candidate =>
                candidate.Project.Length > 0 &&
                string.Equals(candidate.Name, ReconcileNames.Container(candidate.Project, plan.Name), StringComparison.Ordinal));

            if (candidates is not null)
            {
                foreach (var duplicate in candidates.Where(candidate => !ReferenceEquals(candidate, exact)))
                    orphans.Add(duplicate);
            }

            if (exact is null)
            {
                changes.Add(new ServiceChange(plan, ServiceActions.Create, null, "container is missing"));
                continue;
            }

            changes.Add(Decide(plan, exact, imageMatches.GetValueOrDefault(exact.Name)));
        }

        var desiredNames = new HashSet<string>(plans.Select(plan => plan.Name), StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (observation.Service.Length == 0 || !desiredNames.Contains(observation.Service))
            {
                if (!orphans.Contains(observation))
                    orphans.Add(observation);
            }
        }

        orphans.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return new DiffPlan { Changes = changes, Orphans = orphans };
    }

    private static ServiceChange Decide(ServicePlan plan, ContainerObservation observed, bool imageMatches)
    {
        if (!imageMatches)
            return new ServiceChange(plan, ServiceActions.Recreate, observed, "image digest is not proven locally");

        if (!string.Equals(observed.Label(ReconcileLabels.ConfigHash), plan.ConfigHash, StringComparison.Ordinal))
            return new ServiceChange(plan, ServiceActions.Recreate, observed, "config hash changed");

        if (!string.Equals(observed.Label(ReconcileLabels.Image), plan.Spec.Image, StringComparison.Ordinal))
            return new ServiceChange(plan, ServiceActions.Recreate, observed, "image digest label changed");

        if (!string.Equals(NormalizeRestart(observed.RestartPolicy), NormalizeRestart(plan.Spec.Restart ?? "unless-stopped"), StringComparison.Ordinal))
            return new ServiceChange(plan, ServiceActions.Recreate, observed, "restart policy changed");

        if (!PortsMatch(plan.Spec, observed.Ports))
            return new ServiceChange(plan, ServiceActions.Recreate, observed, "published ports changed");

        if (!DesiredMounts(plan.Spec).IsSubsetOf(observed.Mounts.Select(MountKey).ToHashSet(StringComparer.Ordinal)))
            return new ServiceChange(plan, ServiceActions.Recreate, observed, "mounts changed");

        if (!DesiredEnvironment(plan.Spec).IsSubsetOf(observed.Environment.ToHashSet(StringComparer.Ordinal)))
            return new ServiceChange(plan, ServiceActions.Recreate, observed, "environment changed");

        return observed.State switch
        {
            "running" => new ServiceChange(plan, ServiceActions.None, observed, "matches"),
            "exited" or "created" or "stopped" => new ServiceChange(plan, ServiceActions.Start, observed, "container is stopped"),
            _ => new ServiceChange(plan, ServiceActions.Recreate, observed, $"container state is {observed.State}"),
        };
    }

    internal static bool PortsMatch(ServiceSpec service, IReadOnlyList<string> observed)
    {
        var desired = DesiredPortKeys(service);
        if (desired.Count != observed.Count)
            return false;

        var remaining = observed.ToList();
        foreach (var wanted in desired)
        {
            var index = remaining.FindIndex(entry =>
                TryParseObservedPort(entry, out var container, out var host, out var protocol) &&
                string.Equals(container, wanted.Container, StringComparison.Ordinal) &&
                string.Equals(protocol, wanted.Protocol, StringComparison.Ordinal) &&
                (wanted.Host == "*" || string.Equals(host, wanted.Host, StringComparison.Ordinal)));
            if (index < 0)
                return false;
            remaining.RemoveAt(index);
        }

        return remaining.Count == 0;
    }

    private static List<PortSpec> DesiredPortKeys(ServiceSpec service)
    {
        var keys = new List<PortSpec>(service.Ports.Count);
        foreach (var spec in service.Ports)
        {
            var value = spec;
            var protocol = "tcp";
            var slash = value.LastIndexOf('/');
            if (slash >= 0)
            {
                protocol = value[(slash + 1)..];
                value = value[..slash];
            }

            var parts = value.Split(':');
            var container = parts.Length switch
            {
                1 => parts[0],
                2 => parts[1],
                _ => parts[2],
            };
            var host = parts.Length switch
            {
                1 => "*",
                2 => parts[0],
                _ => parts[1],
            };
            keys.Add(new PortSpec(container, host, protocol));
        }

        return keys;
    }

    private static bool TryParseObservedPort(string value, out string container, out string host, out string protocol)
    {
        container = host = protocol = "";
        var slash = value.IndexOf('/');
        var colon = slash < 0 ? -1 : value.IndexOf(':', slash + 1);
        if (slash <= 0 || colon <= slash + 1)
            return false;
        container = value[..slash];
        protocol = value[(slash + 1)..colon];
        host = value[(colon + 1)..];
        return container.Length > 0 && protocol.Length > 0 && host.Length > 0;
    }

    private readonly record struct PortSpec(string Container, string Host, string Protocol);

    internal static HashSet<string> DesiredPorts(ServiceSpec service)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in service.Ports)
        {
            var value = spec;
            var protocol = "tcp";
            var slash = value.LastIndexOf('/');
            if (slash >= 0)
            {
                protocol = value[(slash + 1)..];
                value = value[..slash];
            }

            var parts = value.Split(':');
            var container = parts.Length switch
            {
                1 => parts[0],
                2 => parts[1],
                _ => parts[2],
            };
            var host = parts.Length switch
            {
                1 => "*",
                2 => parts[0],
                _ => parts[1],
            };
            keys.Add(PodmanRuntime.PortKey(container, host, protocol));
        }

        return keys;
    }

    internal static HashSet<string> DesiredMounts(ServiceSpec service)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in service.Volumes)
        {
            var parts = spec.Split(':');
            if (parts.Length < 2)
                continue;
            var readOnly = parts.Length == 3 && parts[2].Split(',').Contains("ro", StringComparer.Ordinal);
            keys.Add(MountKey(parts[0], parts[1], readOnly));
        }

        return keys;
    }

    internal static HashSet<string> DesiredEnvironment(ServiceSpec service)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (service.Env is null)
            return values;
        foreach (var (key, value) in service.Env)
            values.Add($"{key}={value}");
        return values;
    }

    private static string MountKey(MountObservation mount) =>
        MountKey(mount.Source, mount.Destination, mount.ReadOnly);

    private static string MountKey(string source, string destination, bool readOnly) =>
        $"{source}:{destination}:{(readOnly ? "ro" : "rw")}";

    private static string NormalizeRestart(string value) => value.Length == 0 ? "no" : value;
}
