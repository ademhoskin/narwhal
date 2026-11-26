using System.Text.Json;

namespace Narwhal.Cloud;

/// <summary>
/// Strict UTF-8 JSON parsing on top of <see cref="JsonDocument"/>. The validation pass rejects
/// duplicate object keys, non-integer numbers, lone surrogates, and trailing values before the
/// document is materialized.
/// </summary>
internal static class StrictJson
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    public static JsonDocument Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            if (!reader.Read())
                throw Invalid("empty JSON document");

            SkipValue(ref reader);
            if (reader.Read())
                throw Invalid("trailing bytes after JSON document");

            var documentReader = new Utf8JsonReader(utf8, ReaderOptions);
            documentReader.Read();
            return JsonDocument.ParseValue(ref documentReader);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw Malformed();
        }
    }

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                SkipObject(ref reader);
                break;
            case JsonTokenType.StartArray:
                SkipArray(ref reader);
                break;
            case JsonTokenType.String:
                ReadString(ref reader);
                break;
            case JsonTokenType.Number:
                if (!reader.TryGetInt64(out _))
                    throw Invalid("only canonical integers are accepted");
                break;
            case JsonTokenType.True:
            case JsonTokenType.False:
            case JsonTokenType.Null:
                break;
            default:
                throw Invalid("unexpected JSON token");
        }
    }

    private static void SkipObject(ref Utf8JsonReader reader)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw Invalid("expected a JSON property name");

            var name = ReadString(ref reader);
            if (!keys.Add(name))
                throw Invalid("duplicate JSON object key");

            if (!reader.Read())
                throw Invalid("truncated JSON object");
            SkipValue(ref reader);
        }

        throw Invalid("unterminated JSON object");
    }

    private static void SkipArray(ref Utf8JsonReader reader)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return;
            SkipValue(ref reader);
        }

        throw Invalid("unterminated JSON array");
    }

    private static string ReadString(ref Utf8JsonReader reader)
    {
        string? value;
        try
        {
            value = reader.GetString();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw Invalid("invalid UTF-8 string");
        }

        if (value is null || !IsValidUtf16(value))
            throw Invalid("invalid Unicode string");
        return value;
    }

    private static bool IsValidUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    return false;
                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static CloudArtifactException Invalid(string message) =>
        new(CloudArtifactErrorCodes.JsonInvalid, message);

    private static CloudArtifactException Malformed() =>
        new(CloudArtifactErrorCodes.JsonInvalid, "malformed JSON");
}
