using System.Text.Json;

namespace Narwhal.Cloud;

/// <summary>
/// Strict UTF-8 JSON tree that preserves member order and rejects duplicate keys,
/// non-integer numbers, lone surrogates, and trailing values.
/// </summary>
internal static class JsonAstParser
{
    public static JsonValue Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });

            if (!reader.Read())
                throw new CloudArtifactException(CloudArtifactErrorCodes.JsonInvalid, "empty JSON document");

            var value = ReadValue(ref reader);
            if (reader.Read())
                throw new CloudArtifactException(CloudArtifactErrorCodes.JsonInvalid, "trailing bytes after JSON document");
            return value;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new CloudArtifactException(CloudArtifactErrorCodes.JsonInvalid, "malformed JSON");
        }
    }

    private static JsonValue ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                return ReadObject(ref reader);
            case JsonTokenType.StartArray:
                return ReadArray(ref reader);
            case JsonTokenType.String:
                return new JsonString(ReadString(ref reader));
            case JsonTokenType.Number:
                if (!reader.TryGetInt64(out var number))
                    throw new CloudArtifactException(
                        CloudArtifactErrorCodes.JsonInvalid,
                        "only canonical integers are accepted");
                return new JsonNumber(number);
            case JsonTokenType.True:
                return new JsonBoolean(true);
            case JsonTokenType.False:
                return new JsonBoolean(false);
            case JsonTokenType.Null:
                return JsonNull.Instance;
            default:
                throw new CloudArtifactException(CloudArtifactErrorCodes.JsonInvalid, "unexpected JSON token");
        }
    }

    private static JsonObject ReadObject(ref Utf8JsonReader reader)
    {
        var members = new List<KeyValuePair<string, JsonValue>>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return new JsonObject(members);
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new CloudArtifactException(CloudArtifactErrorCodes.JsonInvalid, "expected a JSON property name");

            var name = ReadString(ref reader);
            if (!keys.Add(name))
                throw new CloudArtifactException(CloudArtifactErrorCodes.JsonInvalid, "duplicate JSON object key");

            if (!reader.Read())
                throw new CloudArtifactException(CloudArtifactErrorCodes.JsonInvalid, "truncated JSON object");
            members.Add(new KeyValuePair<string, JsonValue>(name, ReadValue(ref reader)));
        }

        throw new CloudArtifactException(CloudArtifactErrorCodes.JsonInvalid, "unterminated JSON object");
    }

    private static JsonArray ReadArray(ref Utf8JsonReader reader)
    {
        var items = new List<JsonValue>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return new JsonArray(items);
            items.Add(ReadValue(ref reader));
        }

        throw new CloudArtifactException(CloudArtifactErrorCodes.JsonInvalid, "unterminated JSON array");
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
            throw new CloudArtifactException(CloudArtifactErrorCodes.JsonInvalid, "invalid UTF-8 string");
        }

        if (value is null || !IsValidUtf16(value))
            throw new CloudArtifactException(CloudArtifactErrorCodes.JsonInvalid, "invalid Unicode string");
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
}

internal abstract class JsonValue;

internal sealed class JsonNull : JsonValue
{
    public static JsonNull Instance { get; } = new();

    private JsonNull()
    {
    }
}

internal sealed class JsonBoolean(bool value) : JsonValue
{
    public bool Value { get; } = value;
}

internal sealed class JsonNumber(long value) : JsonValue
{
    public long Value { get; } = value;
}

internal sealed class JsonString(string value) : JsonValue
{
    public string Value { get; } = value;
}

internal sealed class JsonArray(List<JsonValue> items) : JsonValue
{
    public List<JsonValue> Items { get; } = items;
}

internal sealed class JsonObject(List<KeyValuePair<string, JsonValue>> members) : JsonValue
{
    public List<KeyValuePair<string, JsonValue>> Members { get; } = members;

    public JsonValue? Find(string name)
    {
        foreach (var member in Members)
        {
            if (string.Equals(member.Key, name, StringComparison.Ordinal))
                return member.Value;
        }

        return null;
    }

    public bool TryFind(string name, out JsonValue value)
    {
        var found = Find(name);
        if (found is null)
        {
            value = JsonNull.Instance;
            return false;
        }

        value = found;
        return true;
    }
}
