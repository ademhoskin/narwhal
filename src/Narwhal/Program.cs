using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Narwhal.Cli;
using Narwhal.Commands;
using Narwhal.Config;
using Narwhal.Nix;
using Narwhal.Reconcile;
using Narwhal.Runtime;

namespace Narwhal;

public static class App
{
    public const string Version = "0.1.0";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            Console.Out.WriteLine(CommandLine.Usage());
            return 0;
        }

        // `narwhal help [command]` is handled before parsing so it never needs a manifest.
        if (args.Length > 0 && args[0] == "help")
        {
            var spec = args.Length > 1 ? CommandLine.Find(args[1]) : null;
            Console.Out.WriteLine(spec is null ? CommandLine.Usage() : CommandLine.Usage(spec));
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        CliOptions options;
        try
        {
            options = CommandLine.Parse(args);
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }

        if (options.Help)
        {
            var spec = options.Command.Length > 0 ? CommandLine.Find(options.Command) : null;
            Console.Out.WriteLine(spec is null ? CommandLine.Usage() : CommandLine.Usage(spec));
            return 0;
        }

        using var sigterm = !OperatingSystem.IsWindows() && RegistersSigterm(options.Command)
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            })
            : null;

        try
        {
            RejectManifestOptionsForAgentCommand(options);
            return await DispatchAsync(options, args, cancellation.Token);
        }
        catch (UsageException e)
        {
            return Fail(options, "error: " + e.Message, 1);
        }
        catch (ManifestException e)
        {
            return Fail(options, "error: " + e.Message, 1);
        }
        catch (ToolNotFoundException e)
        {
            return Fail(options, "error: " + e.Message, 3);
        }
        catch (BuildException e)
        {
            var message = "error: " + e.Message + (e.Detail is null ? "" : "\n" + Indent(e.Detail));
            return Fail(options, message, 2);
        }
        catch (ContainerException e)
        {
            return Fail(options, "error: " + e.Message, 3);
        }
        catch (ProcessFailureException e)
        {
            return Fail(options, "error: " + e.Message, 3);
        }
        catch (ReproducibilityException e)
        {
            return Fail(options, "error: " + e.Message, 4);
        }
        catch (AgentConfigException e)
        {
            return Fail(options, "error: " + e.Message, 1);
        }
        catch (ReconcileException e)
        {
            return Fail(options, "error: " + e.Message, (int)e.ExitCode);
        }
        catch (OperationCanceledException)
        {
            return Fail(options, "interrupted", 130);
        }
        catch (IOException e)
        {
            return Fail(options, "error: " + e.Message, 3);
        }
    }

    private static bool IsAgentCommand(string command) =>
        command is "daemon" or "guard" or "install-units" or "install-guard";

    internal static bool RegistersSigterm(string command) =>
        command is "reconcile" || IsAgentCommand(command);

    private static void RejectManifestOptionsForAgentCommand(CliOptions options)
    {
        if (!IsAgentCommand(options.Command))
            return;
        if (options.Directory is not null)
            throw new UsageException($"option '--dir' is not valid for '{options.Command}'");
        if (options.File is not null)
            throw new UsageException($"option '--file' is not valid for '{options.Command}'");
        if (options.LockPath is not null)
            throw new UsageException($"option '--lock' is not valid for '{options.Command}'");
        if (options.Quiet)
            throw new UsageException($"option '--quiet' is not valid for '{options.Command}'");
    }

    private static async Task<int> DispatchAsync(
        CliOptions options,
        string[] args,
        CancellationToken cancellationToken)
    {
        var reporter = new Reporter(options.Json, options.Verbose, options.NoColor ? false : null);

        switch (options.Command)
        {
            case "version":
                reporter.Data(Version);
                return 0;
            case "init":
                return InitCommand.Run(options, reporter);
            case "reconcile":
                return await ReconcileCommand.RunAsync(options, reporter, cancellationToken);
            case "status":
                return await StatusCommand.RunAsync(options, reporter, cancellationToken);
            case "daemon":
            case "guard":
            case "install-units":
            case "install-guard":
                return await AgentCommands.RunAsync(args, cancellationToken);
        }

        var context = CommandContext.Create(options);

        return options.Command switch
        {
            "plan" => await BuildCommands.PlanAsync(context, options, cancellationToken),
            "build" => await BuildCommands.BuildAsync(context, options, cancellationToken),
            "verify" => await BuildCommands.VerifyAsync(context, options, cancellationToken),
            "push" => await BuildCommands.PushAsync(context, options, cancellationToken),
            "up" or "deploy" => await RunCommands.UpAsync(context, options, cancellationToken),
            "down" => await RunCommands.DownAsync(context, options, cancellationToken),
            "ps" => await RunCommands.PsAsync(context, cancellationToken),
            "logs" => await RunCommands.LogsAsync(context, options, cancellationToken),
            _ => throw new UsageException($"unknown command '{options.Command}'"),
        };
    }

    private static int Fail(CliOptions options, string message, int exitCode)
    {
        Console.Error.WriteLine(message);

        if (options.Json)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", false);
                writer.WriteString("error", message.Replace("error: ", "", StringComparison.Ordinal));
                writer.WriteNumber("exit_code", exitCode);
                writer.WriteEndObject();
            }
            Console.Out.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        return exitCode;
    }

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(line => "  " + line));
}
