using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace Narwhal.Cloud;

/// <summary>JSON Canonicalization Scheme (RFC 8785) restricted to integer numbers.</summary>
public static class CanonicalJson
{
    /// <summary>Returns the canonical UTF-8 form of a strict JSON document.</summary>
    /// <exception cref="CloudArtifactException">The document is malformed, has duplicate keys, or uses non-integer numbers.</exception>
    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8) => CanonicalJsonWriter.Write(JsonAstParser.Parse(utf8));

    /// <summary>True when the bytes are already the canonical form of a strict JSON document.</summary>
    public static bool IsCanonical(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return Canonicalize(utf8).AsSpan().SequenceEqual(utf8);
        }
        catch (CloudArtifactException)
        {
            return false;
        }
    }
}

internal static class CanonicalJsonWriter
{
    public static byte[] Write(JsonValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteValue(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteValue(ArrayBufferWriter<byte> buffer, JsonValue value)
    {
        switch (value)
        {
            case JsonNull:
                WriteAscii(buffer, "null");
                break;
            case JsonBoolean boolean:
                WriteAscii(buffer, boolean.Value ? "true" : "false");
                break;
            case JsonNumber number:
                WriteNumber(buffer, number.Value);
                break;
            case JsonString text:
                WriteString(buffer, text.Value);
                break;
            case JsonArray array:
                buffer.GetSpan(1)[0] = (byte)'[';
                buffer.Advance(1);
                for (var i = 0; i < array.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        buffer.GetSpan(1)[0] = (byte)',';
                        buffer.Advance(1);
                    }

                    WriteValue(buffer, array.Items[i]);
                }

                buffer.GetSpan(1)[0] = (byte)']';
                buffer.Advance(1);
                break;
            case JsonObject obj:
                WriteObject(buffer, obj);
                break;
            default:
                throw new InvalidOperationException("unknown JSON value");
        }
    }

    private static void WriteObject(ArrayBufferWriter<byte> buffer, JsonObject obj)
    {
        var members = obj.Members.ToArray();
        Array.Sort(members, static (left, right) => string.CompareOrdinal(left.Key, right.Key));

        buffer.GetSpan(1)[0] = (byte)'{';
        buffer.Advance(1);
        for (var i = 0; i < members.Length; i++)
        {
            if (i > 0)
            {
                buffer.GetSpan(1)[0] = (byte)',';
                buffer.Advance(1);
            }

            WriteString(buffer, members[i].Key);
            buffer.GetSpan(1)[0] = (byte)':';
            buffer.Advance(1);
            WriteValue(buffer, members[i].Value);
        }

        buffer.GetSpan(1)[0] = (byte)'}';
        buffer.Advance(1);
    }

    private static void WriteNumber(ArrayBufferWriter<byte> buffer, long value)
    {
        var span = buffer.GetSpan(20);
        if (!Utf8Formatter.TryFormat(value, span, out var written))
            throw new InvalidOperationException("cannot format an integer");
        buffer.Advance(written);
    }

    private static void WriteString(ArrayBufferWriter<byte> buffer, string value)
    {
        buffer.GetSpan(1)[0] = (byte)'"';
        buffer.Advance(1);

        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"':
                    WriteAscii(buffer, "\\\"");
                    continue;
                case '\\':
                    WriteAscii(buffer, "\\\\");
                    continue;
                case '\b':
                    WriteAscii(buffer, "\\b");
                    continue;
                case '\t':
                    WriteAscii(buffer, "\\t");
                    continue;
                case '\n':
                    WriteAscii(buffer, "\\n");
                    continue;
                case '\f':
                    WriteAscii(buffer, "\\f");
                    continue;
                case '\r':
                    WriteAscii(buffer, "\\r");
                    continue;
            }

            if (c < 0x20)
            {
                var span = buffer.GetSpan(6);
                span[0] = (byte)'\\';
                span[1] = (byte)'u';
                span[2] = (byte)'0';
                span[3] = (byte)'0';
                span[4] = HexDigit((c >> 4) & 0xf);
                span[5] = HexDigit(c & 0xf);
                buffer.Advance(6);
                continue;
            }

            var rune = char.IsHighSurrogate(c) ? new Rune(char.ConvertToUtf32(c, value[++i])) : new Rune(c);
            var length = rune.EncodeToUtf8(utf8);
            utf8[..length].CopyTo(buffer.GetSpan(length));
            buffer.Advance(length);
        }

        buffer.GetSpan(1)[0] = (byte)'"';
        buffer.Advance(1);
    }

    private static byte HexDigit(int value) => value < 10 ? (byte)('0' + value) : (byte)('a' + value - 10);

    private static void WriteAscii(ArrayBufferWriter<byte> buffer, string text)
    {
        var span = buffer.GetSpan(text.Length);
        for (var i = 0; i < text.Length; i++)
            span[i] = (byte)text[i];
        buffer.Advance(text.Length);
    }
}
