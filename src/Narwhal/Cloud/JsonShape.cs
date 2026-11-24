namespace Narwhal.Cloud;

/// <summary>Small strict shape checks shared by the payload, signature, and trust parsers.</summary>
internal static class JsonShape
{
    public static CloudArtifactException Error(string code, string path, string reason) =>
        new(code, $"{path}: {reason}");

    public static JsonObject Object(JsonValue value, string path, string code = CloudArtifactErrorCodes.SchemaInvalid) =>
        value as JsonObject ?? throw Error(code, path, "expected an object");

    public static JsonArray Array(JsonValue value, string path, string code = CloudArtifactErrorCodes.SchemaInvalid) =>
        value as JsonArray ?? throw Error(code, path, "expected an array");

    public static string String(JsonValue value, string path, string code = CloudArtifactErrorCodes.SchemaInvalid) =>
        value is JsonString text
            ? text.Value
            : throw Error(code, path, "expected a string");

    public static string NonEmptyString(JsonValue value, string path, string code = CloudArtifactErrorCodes.SchemaInvalid)
    {
        var text = String(value, path, code);
        if (text.Length == 0)
            throw Error(code, path, "expected a non-empty string");
        return text;
    }

    public static long Integer(JsonValue value, string path, long minimum, long maximum, string code = CloudArtifactErrorCodes.SchemaInvalid)
    {
        if (value is not JsonNumber number)
            throw Error(code, path, "expected an integer");
        if (number.Value < minimum || number.Value > maximum)
            throw Error(code, path, $"integer outside {minimum}..{maximum}");
        return number.Value;
    }

    public static JsonValue Required(JsonObject obj, string path, string name, string code = CloudArtifactErrorCodes.SchemaInvalid) =>
        obj.Find(name) ?? throw Error(code, path, $"missing required field '{name}'");

    public static bool Optional(JsonObject obj, string name, out JsonValue value)
    {
        var found = obj.Find(name);
        if (found is null)
        {
            value = JsonNull.Instance;
            return false;
        }

        value = found;
        return true;
    }

    public static void Members(JsonObject obj, string path, string code, params string[] allowed)
    {
        foreach (var member in obj.Members)
        {
            if (System.Array.IndexOf(allowed, member.Key) < 0)
                throw Error(code, path, $"unexpected field '{member.Key}'");
        }
    }

    public static bool IsNull(JsonValue value) => value is JsonNull;
}
