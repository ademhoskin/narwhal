using Narwhal.Config;

namespace Narwhal.Cli;

public sealed class UsageException(string message) : Exception(message);

public sealed record CommandSpec(
    string Name,
    string Summary,
    string Usage,
    string[] ValueOptions,
    string[] Flags,
    string? Elaboration = null);

public sealed class CliOptions
{
    public string Command { get; set; } = "";
    public List<string> Positionals { get; } = [];
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> ValueLists { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

    public bool Json { get; set; }
    public bool Verbose { get; set; }
    public bool Quiet { get; set; }
    public bool NoColor { get; set; }
    public bool Help { get; set; }
    public string? Directory { get; set; }
    public string? File { get; set; }
    public string? LockPath { get; set; }

    public string? Value(string name) => Values.TryGetValue(name, out var value) ? value : null;

    /// <summary>Every value given for a repeatable option, in order.</summary>
    public IReadOnlyList<string> ListValue(string name) =>
        ValueLists.TryGetValue(name, out var values) ? values : [];

    public int? IntValue(string name, int min = 0, int max = int.MaxValue)
    {
        var raw = Value(name);
        if (raw is null)
            return null;
        if (!int.TryParse(raw, out var value) || value < min || value > max)
            throw new UsageException($"option '--{name}' expects a number between {min} and {max}, got '{raw}'");
        return value;
    }

    public bool Flag(string name) => Flags.Contains(name);
}

public static class CommandLine
{
    public static readonly CommandSpec[] Commands =
    [
        new("init", "scaffold a manifest and flake",
            "narwhal init [directory] [--name <name>] [--narwhal-ref <flake>] [--force]",
            ["name", "narwhal-ref"], ["force"]),
        new("plan", "show services, build targets and lock status",
            "narwhal plan", [], ["refresh"]),
        new("build", "build images with nix and load them into podman",
            "narwhal build [service...] [--system <system>]... [--locked] [--no-load] [--rebuild]",
            ["system"], ["locked", "frozen", "no-load", "rebuild"]),
        new("up", "build images and start the service container",
            "narwhal up [service...] [--build] [--no-build] [--wait <seconds>]",
            ["wait"], ["build", "no-build"]),
        new("down", "stop and remove this project's containers",
            "narwhal down [--volumes]",
            [], ["volumes"]),
        new("ps", "list service containers",
            "narwhal ps", [], []),
        new("logs", "show container logs",
            "narwhal logs [service...] [--follow] [--tail <lines>] [--timestamps]",
            ["tail"], ["follow", "timestamps"]),
        new("push", "push built images to their registries",
            "narwhal push [service...] [--system <system>]...", ["system"], []),
        new("verify", "rebuild and prove images match the lock file",
            "narwhal verify [service...] [--system <system>]... [--no-load]", ["system"], ["no-load"]),
        new("version", "print the narwhal version", "narwhal version", [], []),
    ];

    public static CommandSpec? Find(string name) =>
        Commands.FirstOrDefault(c => c.Name == name) ?? (name == "deploy" ? Commands.First(c => c.Name == "up") : null);

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        CommandSpec? spec = null;
        var index = 0;

        while (index < args.Length)
        {
            var token = args[index];
            if (token == "--")
            {
                index++;
                while (index < args.Length)
                    options.Positionals.Add(args[index++]);
                break;
            }

            if (token.Length > 1 && token[0] == '-')
            {
                var (name, inlineValue) = SplitOption(token);
                switch (name)
                {
                    case "h" or "help":
                        options.Help = true;
                        index++;
                        continue;
                    case "json":
                        options.Json = true;
                        index++;
                        continue;
                    case "v" or "verbose":
                        options.Verbose = true;
                        index++;
                        continue;
                    case "no-color":
                        options.NoColor = true;
                        index++;
                        continue;
                    case "quiet":
                        options.Quiet = true;
                        index++;
                        continue;
                    case "C" or "dir":
                        options.Directory = inlineValue ?? RequireValue(args, ref index, "--dir");
                        if (inlineValue is not null)
                            index++;
                        continue;
                    case "file":
                        options.File = inlineValue ?? RequireValue(args, ref index, "--file");
                        if (inlineValue is not null)
                            index++;
                        continue;
                    case "lock":
                        options.LockPath = inlineValue ?? RequireValue(args, ref index, "--lock");
                        if (inlineValue is not null)
                            index++;
                        continue;
                }

                if (spec is null)
                    throw new UsageException($"unknown option '{token}'");

                var commandOption = name switch
                {
                    "f" => "follow",
                    "t" => "timestamps",
                    _ => name,
                };

                if (spec.ValueOptions.Contains(commandOption))
                {
                    var value = inlineValue ?? RequireValue(args, ref index, "--" + commandOption);
                    options.Values[commandOption] = value;
                    if (!options.ValueLists.TryGetValue(commandOption, out var values))
                        options.ValueLists[commandOption] = values = [];
                    values.Add(value);
                    if (inlineValue is not null)
                        index++;
                    continue;
                }

                if (spec.Flags.Contains(commandOption))
                {
                    if (inlineValue is not null)
                        throw new UsageException($"option '--{commandOption}' does not take a value");
                    options.Flags.Add(commandOption);
                    index++;
                    continue;
                }

                throw new UsageException($"unknown option '{token}' for '{spec.Name}'\n\n{spec.Usage}");
            }

            if (spec is null)
            {
                if (!options.Help)
                {
                    options.Command = token;
                    spec = Find(token) ?? throw new UsageException($"unknown command '{token}'\n\n{Usage()}");
                }
            }
            else
            {
                options.Positionals.Add(token);
            }

            index++;
        }

        if (options.Command.Length == 0 && !options.Help)
            throw new UsageException("no command given");

        return options;
    }

    private static (string Name, string? Value) SplitOption(string token)
    {
        var trimmed = token.TrimStart('-');
        var equals = trimmed.IndexOf('=');
        return equals < 0 ? (trimmed, null) : (trimmed[..equals], trimmed[(equals + 1)..]);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new UsageException($"option '{option}' requires a value");
        var value = args[index + 1];
        index += 2;
        return value;
    }

    public static string Usage()
    {
        var lines = new List<string>
        {
            "narwhal " + App.Version + " - reproducible container builds and deployment",
            "",
            "usage: narwhal <command> [options]",
            "",
            "commands:",
        };

        var width = Commands.Max(c => c.Name.Length);
        foreach (var command in Commands)
            lines.Add($"  {command.Name.PadRight(width + 2)}{command.Summary}");

        lines.AddRange(
        [
            "",
            "global options:",
            "  --file <path>      manifest to use (default: search for narwhal.toml)",
            "  --lock <path>      lock file to use (default: narwhal.lock.json next to the manifest)",
            "  -C, --dir <path>   run as if started in <path>",
            "  --json             machine-readable output where supported",
            "  -v, --verbose      show the commands narwhal runs",
            "  --no-color         disable colour",
            "  -h, --help         show this help",
            "",
            "run 'narwhal help <command>' for details about a command.",
        ]);

        return string.Join('\n', lines);
    }

    public static string Usage(CommandSpec spec)
    {
        var lines = new List<string>
        {
            spec.Usage,
            "",
            spec.Summary + ".",
        };

        if (spec.Elaboration is not null)
            lines.AddRange(["", spec.Elaboration]);

        if (spec.ValueOptions.Length > 0)
            lines.AddRange(["", "options:"]);
        foreach (var option in spec.ValueOptions)
            lines.Add($"  --{option} <value>");

        foreach (var option in spec.Flags)
            lines.Add($"  --{option}");

        return string.Join('\n', lines);
    }

    /// <summary>Validate that positional service names exist in the manifest.</summary>
    public static void EnsureServicesExist(Manifest manifest, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (manifest.Find(name) is null)
                throw new UsageException($"unknown service '{name}' (known: {string.Join(", ", manifest.Services.Select(s => s.Name))})");
        }
    }
}
