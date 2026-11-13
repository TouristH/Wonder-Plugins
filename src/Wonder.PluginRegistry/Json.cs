// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Wonder.PluginRegistry;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static JsonNode Parse(string text) => JsonNode.Parse(text, documentOptions: new() { AllowDuplicateProperties = false }) ?? throw new InvalidDataException("Empty JSON");
    public static JsonNode Read(string path) { var info = new FileInfo(path); if (info.Length > 1024 * 1024 || info.LinkTarget != null) throw new InvalidDataException("Invalid JSON file"); return Parse(File.ReadAllText(path)); }
    public static string Text(JsonNode node, string name) => node[name]?.GetValue<string>() ?? throw new InvalidDataException($"Missing {name}");
    public static long Number(JsonNode node, string name) => long.TryParse(node[name]?.ToJsonString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidDataException($"Invalid integer {name}");
    public static string Render(JsonNode value) => value.ToJsonString(Options) + "\n";
    public static void Write(string path, JsonNode value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, Render(value)); }
    public static string Id(string value) => Regex.IsMatch(value, "^[a-z0-9][a-z0-9._-]{0,79}$") ? value : throw new InvalidDataException("Invalid plugin ID");
    public static string Version(string value) => Regex.IsMatch(value, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$") && System.Version.TryParse(value, out _) ? value : throw new InvalidDataException("Invalid version");
    public static string Repository(string value) => Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$") && !value.Split('/').Any(x => x is "." or "..") ? value : throw new InvalidDataException("Invalid GitHub repository");
    public static string Hash(string value) => Regex.IsMatch(value, "^[0-9a-f]{64}$") ? value : throw new InvalidDataException("Invalid SHA-256");
    public static bool Equal(JsonNode? one, JsonNode? two) => JsonNode.DeepEquals(one, two);
    public static void Fields(JsonNode node, IEnumerable<string> required, params string[] optional)
    {
        if (node is not JsonObject value || required.Any(key => !value.ContainsKey(key)) || value.Any(pair => !required.Contains(pair.Key) && !optional.Contains(pair.Key)))
            throw new InvalidDataException("Unexpected or missing JSON fields");
    }
    public static void Nonempty(JsonNode node, string name, int limit)
    {
        var value = Text(node,name);
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit) throw new InvalidDataException($"Invalid {name}");
    }
}
