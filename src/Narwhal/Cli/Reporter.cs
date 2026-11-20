namespace Narwhal.Cli;

/// <summary>Human and machine output. Status goes to stderr so stdout stays parseable.</summary>
public sealed class Reporter
{
    private readonly bool _color;
    private readonly TextWriter _out;
    private readonly TextWriter _error;

    public Reporter(bool json, bool verbose, bool? color = null, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        Json = json;
        Verbose = verbose;
        _out = stdout ?? Console.Out;
        _error = stderr ?? Console.Error;
        _color = color ?? (!Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null);
    }

    public bool Json { get; }
    public bool Verbose { get; }
    public TextWriter Out => _out;

    public void Step(string message) => Write(_error, "\u001b[1;36m", "==> ", message);

    public void Info(string message) => Write(_error, null, null, message);

    public void Ok(string message) => Write(_error, "\u001b[32m", "  ok ", message);

    public void Warn(string message) => Write(_error, "\u001b[33m", "warn ", message);

    public void Error(string message) => Write(_error, "\u001b[31m", "error ", message);

    public void Detail(string message)
    {
        if (!Verbose)
            return;
        Write(_error, "\u001b[2m", "     ", message);
    }

    /// <summary>Unmodified output (container logs, tool output the user asked for).</summary>
    public void Raw(string line) => _out.WriteLine(line);

    public void Data(string line) => _out.WriteLine(line);

    private void Write(TextWriter writer, string? color, string? prefix, string message)
    {
        if (Json)
            return;

        if (_color && color is not null)
            writer.Write(color);
        if (prefix is not null)
            writer.Write(prefix);
        writer.Write(message);
        if (_color && color is not null)
            writer.Write("\u001b[0m");
        writer.WriteLine();
    }

    public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, TextWriter? writer = null)
    {
        if (Json)
            return;

        writer ??= _out;
        var widths = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            widths[i] = headers[i].Length;
            foreach (var row in rows)
            {
                if (i < row.Count)
                    widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        static string Pad(string value, int width) => value.PadRight(width);

        writer.WriteLine(string.Join("  ", headers.Select((h, i) => Pad(h.ToUpperInvariant(), widths[i]))).TrimEnd());
        foreach (var row in rows)
        {
            var cells = new List<string>(headers.Count);
            for (var i = 0; i < headers.Count; i++)
                cells.Add(Pad(i < row.Count ? row[i] : string.Empty, widths[i]));
            writer.WriteLine(string.Join("  ", cells).TrimEnd());
        }
    }
}
