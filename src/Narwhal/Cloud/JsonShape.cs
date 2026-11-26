using System.Text.Json;

namespace Narwhal.Cloud;

/// <summary>Small strict shape checks shared by the payload, signature, and trust parsers.</summary>
internal static class JsonShape
{
    public static CloudArtifactException Error(string code, string path, string reason) =>
        new(code, $"{path}: {reason}");

    public static JsonElement Object(JsonElement value, string path, string code = CloudArtifactErrorCodes.SchemaInvalid) =>
        value.ValueKind == JsonValueKind.Object ? value : throw Error(code, path, "expected an object");

    public static JsonElement Array(JsonElement value, string path, string code = CloudArtifactErrorCodes.SchemaInvalid) =>
        value.ValueKind == JsonValueKind.Array ? value : throw Error(code, path, "expected an array");

    public static string String(JsonElement value, string path, string code = CloudArtifactErrorCodes.SchemaInvalid) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Error(code, path, "expected a string");

    public static string NonEmptyString(JsonElement value, string path, string code = CloudArtifactErrorCodes.SchemaInvalid)
    {
        var text = String(value, path, code);
        if (text.Length == 0)
            throw Error(code, path, "expected a non-empty string");
        return text;
    }

    public static long Integer(JsonElement value, string path, long minimum, long maximum, string code = CloudArtifactErrorCodes.SchemaInvalid)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw Error(code, path, "expected an integer");
        if (number < minimum || number > maximum)
            throw Error(code, path, $"integer outside {minimum}..{maximum}");
        return number;
    }

    public static JsonElement Required(JsonElement obj, string path, string name, string code = CloudArtifactErrorCodes.SchemaInvalid) =>
        obj.TryGetProperty(name, out var value)
            ? value
            : throw Error(code, path, $"missing required field '{name}'");

    /// <summary>
    /// Looks up an optional member. The out value is only meaningful when this returns true;
    /// on a miss it is <see cref="JsonValueKind.Undefined"/> and must not be inspected.
    /// </summary>
    public static bool Optional(JsonElement obj, string name, out JsonElement value) =>
        obj.TryGetProperty(name, out value);

    public static void Members(JsonElement obj, string path, string code, params string[] allowed)
    {
        foreach (var member in obj.EnumerateObject())
        {
            if (System.Array.IndexOf(allowed, member.Name) < 0)
                throw Error(code, path, $"unexpected field '{member.Name}'");
        }
    }

    public static bool IsNull(JsonElement value) => value.ValueKind == JsonValueKind.Null;
}
