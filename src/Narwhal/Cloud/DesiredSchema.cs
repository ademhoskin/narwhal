namespace Narwhal.Cloud;

/// <summary>Strict schema validation for the <c>narwhal.desired/v1</c> payload.</summary>
internal static class DesiredSchema
{
    public static DesiredDocument Validate(JsonValue root)
    {
        var obj = JsonShape.Object(root, "payload");

        // The discriminator is checked before any other field is interpreted.
        var schema = JsonShape.String(JsonShape.Required(obj, "payload", "schema"), "payload.schema");
        if (!string.Equals(schema, DesiredDocument.SchemaName, StringComparison.Ordinal))
            throw Invalid("payload.schema", "unknown schema discriminator");

        var version = JsonShape.Integer(
            JsonShape.Required(obj, "payload", "version"),
            "payload.version",
            0,
            long.MaxValue);
        if (version != DesiredDocument.SchemaVersion)
            throw Invalid("payload.version", "unknown schema version");

        JsonShape.Members(
            obj,
            "payload",
            CloudArtifactErrorCodes.SchemaInvalid,
            "schema",
            "version",
            "project",
            "generation",
            "created",
            "previous",
            "target",
            "trust",
            "images",
            "services",
            "deploy",
            "agent");

        var project = JsonShape.String(JsonShape.Required(obj, "payload", "project"), "payload.project");
        if (!CloudPatterns.ProjectName().IsMatch(project))
            throw Invalid("payload.project", "invalid project name");

        var generation = JsonShape.Integer(
            JsonShape.Required(obj, "payload", "generation"),
            "payload.generation",
            1,
            9007199254740991);

        var created = ParseTimestamp(JsonShape.Required(obj, "payload", "created"), "payload.created");

        PreviousArtifact? previous = null;
        if (JsonShape.Optional(obj, "previous", out var previousValue) && !JsonShape.IsNull(previousValue))
            previous = ParsePrevious(previousValue, "payload.previous");

        var target = ParseTarget(JsonShape.Required(obj, "payload", "target"), "payload.target");

        IReadOnlyList<TrustedKey>? trust = null;
        if (JsonShape.Optional(obj, "trust", out var trustValue))
        {
            var trustArray = JsonShape.Array(trustValue, "payload.trust");
            if (trustArray.Items.Count == 0)
                throw Invalid("payload.trust", "expected at least one key");
            var keys = new List<TrustedKey>(trustArray.Items.Count);
            for (var i = 0; i < trustArray.Items.Count; i++)
                keys.Add(TrustedKeyParser.Parse(trustArray.Items[i], $"payload.trust[{i}]", CloudArtifactErrorCodes.SchemaInvalid));
            trust = keys;
        }

        var images = ParseImages(JsonShape.Required(obj, "payload", "images"), "payload.images");
        var services = ParseServices(JsonShape.Required(obj, "payload", "services"), "payload.services");

        DeploySpec? deploy = null;
        if (JsonShape.Optional(obj, "deploy", out var deployValue))
            deploy = ParseDeploy(deployValue, "payload.deploy");

        AgentSpec? agent = null;
        if (JsonShape.Optional(obj, "agent", out var agentValue))
            agent = ParseAgent(agentValue, "payload.agent");

        return new DesiredDocument
        {
            Schema = schema,
            Version = (int)version,
            Project = project,
            Generation = generation,
            Created = created,
            Previous = previous,
            Target = target,
            Trust = trust,
            Images = images,
            Services = services,
            Deploy = deploy,
            Agent = agent,
        };
    }

    private static PreviousArtifact ParsePrevious(JsonValue value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "generation", "digest", "manifest_digest");
        return new PreviousArtifact
        {
            Generation = JsonShape.Integer(JsonShape.Required(obj, path, "generation"), $"{path}.generation", 1, long.MaxValue),
            Digest = Digest(JsonShape.Required(obj, path, "digest"), $"{path}.digest"),
            ManifestDigest = Digest(JsonShape.Required(obj, path, "manifest_digest"), $"{path}.manifest_digest"),
        };
    }

    private static TargetSpec ParseTarget(JsonValue value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "provider", "region", "arch", "selector", "ingress");

        var provider = Enum(obj, path, "provider", ["aws", "digitalocean", "gcp"]);
        var region = JsonShape.NonEmptyString(JsonShape.Required(obj, path, "region"), $"{path}.region");
        var arch = Enum(obj, path, "arch", ["amd64", "arm64"]);

        var selectorObject = JsonShape.Object(JsonShape.Required(obj, path, "selector"), $"{path}.selector");
        if (selectorObject.Members.Count == 0)
            throw Invalid($"{path}.selector", "expected at least one selector");
        var selector = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in selectorObject.Members)
            selector.Add(member.Key, JsonShape.NonEmptyString(member.Value, $"{path}.selector.{member.Key}"));

        var ingressArray = JsonShape.Array(JsonShape.Required(obj, path, "ingress"), $"{path}.ingress");
        var ingress = new List<IngressRule>(ingressArray.Items.Count);
        for (var i = 0; i < ingressArray.Items.Count; i++)
            ingress.Add(ParseIngressRule(ingressArray.Items[i], $"{path}.ingress[{i}]"));

        return new TargetSpec
        {
            Provider = provider,
            Region = region,
            Arch = arch,
            Selector = selector,
            Ingress = ingress,
        };
    }

    private static IngressRule ParseIngressRule(JsonValue value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "protocol", "port", "source");

        var protocol = Enum(obj, path, "protocol", ["tcp", "udp"]);
        var port = (int)JsonShape.Integer(JsonShape.Required(obj, path, "port"), $"{path}.port", 1, 65535);

        var sourceArray = JsonShape.Array(JsonShape.Required(obj, path, "source"), $"{path}.source");
        if (sourceArray.Items.Count == 0)
            throw Invalid($"{path}.source", "expected at least one source");
        var sources = ParseUniqueStrings(sourceArray, $"{path}.source");

        return new IngressRule { Protocol = protocol, Port = port, Source = sources };
    }

    private static IReadOnlyDictionary<string, ImageSpec> ParseImages(JsonValue value, string path)
    {
        var obj = JsonShape.Object(value, path);
        if (obj.Members.Count == 0)
            throw Invalid(path, "expected at least one image");

        var images = new Dictionary<string, ImageSpec>(StringComparer.Ordinal);
        foreach (var member in obj.Members)
        {
            if (!CloudPatterns.Digest().IsMatch(member.Key))
                throw Invalid($"{path}.{member.Key}", "image keys must be sha256 digests");
            images.Add(member.Key, ParseImage(member.Value, $"{path}.{member.Key}"));
        }

        return images;
    }

    private static ImageSpec ParseImage(JsonValue value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "repository", "platform", "size");

        var repository = JsonShape.String(JsonShape.Required(obj, path, "repository"), $"{path}.repository");
        if (!CloudPatterns.Repository().IsMatch(repository))
            throw Invalid($"{path}.repository", "invalid repository");

        return new ImageSpec
        {
            Repository = repository,
            Platform = Enum(obj, path, "platform", ["linux/amd64", "linux/arm64"]),
            Size = JsonShape.Integer(JsonShape.Required(obj, path, "size"), $"{path}.size", 1, long.MaxValue),
        };
    }

    private static IReadOnlyDictionary<string, ServiceSpec> ParseServices(JsonValue value, string path)
    {
        var obj = JsonShape.Object(value, path);
        if (obj.Members.Count == 0)
            throw Invalid(path, "expected at least one service");

        var services = new Dictionary<string, ServiceSpec>(StringComparer.Ordinal);
        foreach (var member in obj.Members)
            services.Add(member.Key, ParseService(member.Value, $"{path}.{member.Key}"));

        return services;
    }

    private static ServiceSpec ParseService(JsonValue value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(
            obj,
            path,
            CloudArtifactErrorCodes.SchemaInvalid,
            "image",
            "command",
            "entrypoint",
            "env",
            "secrets",
            "ports",
            "volumes",
            "health",
            "depends_on",
            "restart",
            "user",
            "workdir",
            "stop_timeout");

        var image = Digest(JsonShape.Required(obj, path, "image"), $"{path}.image");

        IReadOnlyList<string>? command = null;
        if (JsonShape.Optional(obj, "command", out var commandValue))
            command = Strings(JsonShape.Array(commandValue, $"{path}.command"), $"{path}.command");

        string? entrypoint = null;
        if (JsonShape.Optional(obj, "entrypoint", out var entrypointValue))
            entrypoint = JsonShape.String(entrypointValue, $"{path}.entrypoint");

        IReadOnlyDictionary<string, string>? env = null;
        if (JsonShape.Optional(obj, "env", out var envValue))
        {
            var envObject = JsonShape.Object(envValue, $"{path}.env");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in envObject.Members)
                map.Add(member.Key, JsonShape.String(member.Value, $"{path}.env.{member.Key}"));
            env = map;
        }

        IReadOnlyDictionary<string, SecretRef>? secrets = null;
        if (JsonShape.Optional(obj, "secrets", out var secretsValue))
        {
            var secretsObject = JsonShape.Object(secretsValue, $"{path}.secrets");
            var map = new Dictionary<string, SecretRef>(StringComparer.Ordinal);
            foreach (var member in secretsObject.Members)
                map.Add(member.Key, ParseSecretRef(member.Value, $"{path}.secrets.{member.Key}"));
            secrets = map;
        }

        var ports = ArrayOrEmpty(obj, "ports", path, static (value, itemPath) =>
        {
            var text = JsonShape.String(value, itemPath);
            if (!CloudPatterns.Port().IsMatch(text))
                throw Invalid(itemPath, "invalid port mapping");
            return text;
        });

        var volumes = ArrayOrEmpty(obj, "volumes", path, static (value, itemPath) =>
            JsonShape.String(value, itemPath));

        HealthSpec? health = null;
        if (JsonShape.Optional(obj, "health", out var healthValue) && !JsonShape.IsNull(healthValue))
            health = ParseHealth(healthValue, $"{path}.health");

        var dependsOn = ArrayOrEmpty(obj, "depends_on", path, static (value, itemPath) =>
            JsonShape.String(value, itemPath), unique: true);

        string? restart = null;
        if (JsonShape.Optional(obj, "restart", out var restartValue))
            restart = Enum(restartValue, $"{path}.restart", ["no", "on-failure", "always", "unless-stopped"]);

        string? user = null;
        if (JsonShape.Optional(obj, "user", out var userValue))
            user = JsonShape.String(userValue, $"{path}.user");

        string? workdir = null;
        if (JsonShape.Optional(obj, "workdir", out var workdirValue))
            workdir = JsonShape.String(workdirValue, $"{path}.workdir");

        int? stopTimeout = null;
        if (JsonShape.Optional(obj, "stop_timeout", out var stopTimeoutValue))
            stopTimeout = (int)JsonShape.Integer(stopTimeoutValue, $"{path}.stop_timeout", 1, 300);

        return new ServiceSpec
        {
            Image = image,
            Command = command,
            Entrypoint = entrypoint,
            Env = env,
            Secrets = secrets,
            Ports = ports,
            Volumes = volumes,
            Health = health,
            DependsOn = dependsOn,
            Restart = restart,
            User = user,
            Workdir = workdir,
            StopTimeout = stopTimeout,
        };
    }

    private static SecretRef ParseSecretRef(JsonValue value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "source", "id", "version", "key");

        var source = Enum(
            obj,
            path,
            "source",
            ["aws-ssm", "aws-secretsmanager", "gcp-secretmanager", "digitalocean-secrets", "file"]);

        string? version = null;
        if (JsonShape.Optional(obj, "version", out var versionValue))
            version = JsonShape.String(versionValue, $"{path}.version");

        string? key = null;
        if (JsonShape.Optional(obj, "key", out var keyValue))
            key = JsonShape.String(keyValue, $"{path}.key");

        return new SecretRef
        {
            Source = source,
            Id = JsonShape.NonEmptyString(JsonShape.Required(obj, path, "id"), $"{path}.id"),
            Version = version,
            Key = key,
        };
    }

    private static HealthSpec ParseHealth(JsonValue value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "command", "interval", "timeout", "retries", "start_period");

        int? startPeriod = null;
        if (JsonShape.Optional(obj, "start_period", out var startPeriodValue))
            startPeriod = (int)JsonShape.Integer(startPeriodValue, $"{path}.start_period", 0, 3600);

        return new HealthSpec
        {
            Command = JsonShape.NonEmptyString(JsonShape.Required(obj, path, "command"), $"{path}.command"),
            Interval = (int)JsonShape.Integer(JsonShape.Required(obj, path, "interval"), $"{path}.interval", 1, 300),
            Timeout = (int)JsonShape.Integer(JsonShape.Required(obj, path, "timeout"), $"{path}.timeout", 1, 300),
            Retries = (int)JsonShape.Integer(JsonShape.Required(obj, path, "retries"), $"{path}.retries", 1, 100),
            StartPeriod = startPeriod,
        };
    }

    private static DeploySpec ParseDeploy(JsonValue value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "network", "wait_seconds");

        string? network = null;
        if (JsonShape.Optional(obj, "network", out var networkValue))
            network = JsonShape.String(networkValue, $"{path}.network");

        int? waitSeconds = null;
        if (JsonShape.Optional(obj, "wait_seconds", out var waitValue))
            waitSeconds = (int)JsonShape.Integer(waitValue, $"{path}.wait_seconds", 1, 3600);

        return new DeploySpec { Network = network, WaitSeconds = waitSeconds };
    }

    private static AgentSpec ParseAgent(JsonValue value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "version", "repository", "digest", "binary_sha256");

        return new AgentSpec
        {
            Version = JsonShape.NonEmptyString(JsonShape.Required(obj, path, "version"), $"{path}.version"),
            Repository = JsonShape.NonEmptyString(JsonShape.Required(obj, path, "repository"), $"{path}.repository"),
            Digest = Digest(JsonShape.Required(obj, path, "digest"), $"{path}.digest"),
            BinarySha256 = Hex64(JsonShape.Required(obj, path, "binary_sha256"), $"{path}.binary_sha256"),
        };
    }

    private static string Digest(JsonValue value, string path)
    {
        var text = JsonShape.String(value, path);
        if (!CloudPatterns.Digest().IsMatch(text))
            throw Invalid(path, "expected a sha256 digest");
        return text;
    }

    private static string Hex64(JsonValue value, string path)
    {
        var text = JsonShape.String(value, path);
        if (!CloudPatterns.Hex64().IsMatch(text))
            throw Invalid(path, "expected 64 lowercase hex characters");
        return text;
    }

    private static string Enum(JsonObject obj, string path, string name, string[] allowed)
    {
        var text = JsonShape.String(JsonShape.Required(obj, path, name), $"{path}.{name}");
        if (System.Array.IndexOf(allowed, text) < 0)
            throw Invalid($"{path}.{name}", "unexpected value");
        return text;
    }

    private static string Enum(JsonValue value, string path, string[] allowed)
    {
        var text = JsonShape.String(value, path);
        if (System.Array.IndexOf(allowed, text) < 0)
            throw Invalid(path, "unexpected value");
        return text;
    }

    private static string ParseTimestamp(JsonValue value, string path)
    {
        var text = JsonShape.String(value, path);
        if (!CloudEncoding.TryParseUtcTimestamp(text, out _))
            throw Invalid(path, "expected an RFC 3339 UTC timestamp");
        return text;
    }

    private static IReadOnlyList<string> Strings(JsonArray array, string path)
    {
        var items = new List<string>(array.Items.Count);
        for (var i = 0; i < array.Items.Count; i++)
            items.Add(JsonShape.String(array.Items[i], $"{path}[{i}]"));
        return items;
    }

    private static IReadOnlyList<string> ParseUniqueStrings(JsonArray array, string path)
    {
        var items = Strings(array, path);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!seen.Add(item))
                throw Invalid(path, "duplicate array item");
        }

        return items;
    }

    private static IReadOnlyList<string> ArrayOrEmpty(
        JsonObject obj,
        string name,
        string path,
        Func<JsonValue, string, string> read,
        bool unique = false)
    {
        if (!JsonShape.Optional(obj, name, out var value))
            return [];

        var array = JsonShape.Array(value, $"{path}.{name}");
        var items = new List<string>(array.Items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < array.Items.Count; i++)
        {
            var itemPath = $"{path}.{name}[{i}]";
            var item = read(array.Items[i], itemPath);
            if (unique && !seen.Add(item))
                throw Invalid(itemPath, "duplicate array item");
            items.Add(item);
        }

        return items;
    }

    private static CloudArtifactException Invalid(string path, string reason) =>
        JsonShape.Error(CloudArtifactErrorCodes.SchemaInvalid, path, reason);
}
