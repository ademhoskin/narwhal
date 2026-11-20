using System.Diagnostics;

namespace Narwhal.Cli;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    public string Combined => string.IsNullOrEmpty(StdErr) ? StdOut : string.IsNullOrEmpty(StdOut) ? StdErr : StdOut + "\n" + StdErr;
}

public sealed class ToolNotFoundException(string tool, string hint) : Exception($"required tool '{tool}' was not found on PATH.\n{hint}")
{
    public string Tool { get; } = tool;
}

public sealed class ProcessFailureException(string command, ProcessResult result)
    : Exception($"command failed (exit {result.ExitCode}): {command}\n{result.Combined.TrimEnd()}")
{
    public ProcessResult Result { get; } = result;
}

public sealed record ProcessOptions
{
    public string? WorkingDirectory { get; init; }

    /// <summary>Stream tool output to the reporter as detail lines.</summary>
    public bool Stream { get; init; }

    /// <summary>Stream output verbatim to stdout (used for logs).</summary>
    public bool Raw { get; init; }

    /// <summary>Throw <see cref="ProcessFailureException"/> on a non-zero exit.</summary>
    public bool Check { get; init; }

    /// <summary>Environment variables layered on top of the current environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Write stdin to the process and close it.</summary>
    public string? Input { get; init; }
}

/// <summary>Runs external tools without a shell, streaming output as it arrives.</summary>
public sealed class ProcessRunner(Reporter reporter, bool verbose)
{
    public async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, ProcessOptions options,
        CancellationToken cancellationToken = default)
    {
        Process process;
        try
        {
            process = StartProcess(file, args, options);
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 2)
        {
            throw new ToolNotFoundException(file, HintFor(file));
        }

        using var _ = process;
        var stdout = new List<string>();
        var stderr = new List<string>();

        var stdoutTask = PumpAsync(process.StandardOutput, stdout, options, cancellationToken);
        var stderrTask = PumpAsync(process.StandardError, stderr, options, cancellationToken);

        if (options.Input is not null)
        {
            await process.StandardInput.WriteAsync(options.Input.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        process.StandardInput.Close();

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(cancellationToken);

        var result = new ProcessResult(process.ExitCode, string.Join('\n', stdout), string.Join('\n', stderr));
        if (options.Check && !result.Ok)
            throw new ProcessFailureException(Describe(file, args), result);
        return result;
    }

    private Process StartProcess(string file, IReadOnlyList<string> args, ProcessOptions options)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        if (options.Environment is not null)
        {
            foreach (var (key, value) in options.Environment)
                startInfo.Environment[key] = value;
        }

        if (verbose)
            reporter.Detail($"$ {Describe(file, args)}");

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"failed to start {file}");
    }

    private async Task PumpAsync(StreamReader reader, List<string> sink, ProcessOptions options, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            sink.Add(line);
            if (options.Raw)
                reporter.Raw(line);
            else if (options.Stream && verbose)
                reporter.Detail("  " + line);
        }
    }

    public static string Describe(string file, IReadOnlyList<string> args) =>
        args.Count == 0 ? file : $"{file} {string.Join(' ', args.Select(Quote))}";

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./:=@,+".Contains(c)) ? arg : $"'{arg}'";

    private static string HintFor(string file) => file switch
    {
        "nix" => "Install Nix with flakes enabled: https://nixos.org/download",
        "podman" => "Install podman: https://podman.io/docs/installation",
        _ => $"Set NARWHAL_{file.ToUpperInvariant()} to its absolute path.",
    };
}
