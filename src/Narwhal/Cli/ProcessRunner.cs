using System.Diagnostics;
using System.Text;

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

public sealed class ProcessTimeoutException(string command, TimeSpan timeout)
    : Exception($"command timed out after {timeout.TotalSeconds:0.###}s: {command}")
{
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

    /// <summary>Environment variables removed from the inherited environment.</summary>
    public IReadOnlyCollection<string>? RemoveEnvironment { get; init; }

    /// <summary>Write stdin to the process and close it.</summary>
    public string? Input { get; init; }

    /// <summary>Kill the process tree when it runs longer than this.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Characters retained per output stream; anything beyond is drained and dropped.</summary>
    public int MaxOutputChars { get; init; } = 256 * 1024;
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
        using var timeout = options.Timeout is { } limit ? new CancellationTokenSource(limit) : null;
        using var linked = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked?.Token ?? cancellationToken;

        var stdout = new List<string>();
        var stderr = new List<string>();

        try
        {
            var stdoutTask = PumpAsync(process.StandardOutput, stdout, options, token);
            var stderrTask = PumpAsync(process.StandardError, stderr, options, token);

            if (options.Input is not null)
            {
                await process.StandardInput.WriteAsync(options.Input.AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
            }
            process.StandardInput.Close();

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true)
        {
            KillTree(process);

            throw new ProcessTimeoutException(Describe(file, args), options.Timeout!.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillTree(process);
            throw;
        }

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

        if (options.RemoveEnvironment is not null)
        {
            foreach (var key in options.RemoveEnvironment)
                startInfo.Environment.Remove(key);
        }

        if (verbose)
            reporter.Detail($"$ {Describe(file, args)}");

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"failed to start {file}");
    }

    private async Task PumpAsync(StreamReader reader, List<string> sink, ProcessOptions options, CancellationToken ct)
    {
        // Read fixed-size chunks so a newline-free flood cannot allocate an unbounded line.
        var buffer = new char[4096];
        var line = new StringBuilder();
        var limit = Math.Max(0, options.MaxOutputChars);
        var captured = 0;
        var full = limit == 0;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
                break;

            var span = buffer.AsSpan(0, read);
            while (true)
            {
                var newline = span.IndexOf('\n');
                var segment = newline >= 0 ? span[..newline] : span;

                if (!full && segment.Length > 0)
                {
                    var take = Math.Min(segment.Length, limit - captured);
                    line.Append(segment[..take]);
                    captured += take;
                    full = captured >= limit;
                }

                if (newline < 0)
                    break;

                if (!full)
                {
                    captured++;
                    full = captured >= limit;
                }

                if (!full || line.Length > 0)
                    Emit(line);
                line.Clear();
                span = span[(newline + 1)..];
            }
        }

        if (line.Length > 0)
            Emit(line);

        void Emit(StringBuilder value)
        {
            var text = value.ToString();
            if (text.EndsWith('\r'))
                text = text[..^1];
            sink.Add(text);
            if (options.Raw)
                reporter.Raw(text);
            else if (options.Stream && verbose)
                reporter.Detail("  " + text);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process exited between cancellation and the kill.
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
