// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Wonder.PluginRegistry;

public sealed class Registry(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public JsonNode Config => Json.Read(Path.Combine(Root, "repository.json"));
    public string RepositoryName => Json.Repository(Json.Text(Config, "sourceUrl").Replace("https://github.com/", "", StringComparison.Ordinal));
    public string PathFor(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public Dictionary<string, JsonNode> Records()
    {
        var path = PathFor("registry/plugins");
        return !Directory.Exists(path) ? [] : Directory.EnumerateFiles(path, "*.json").ToDictionary(file => Path.GetFileNameWithoutExtension(file)!, file =>
        {
            var record = Json.Read(file);
            Json.Fields(record,["schemaVersion","id","generation","repository","releases"]);
            var id = Json.Id(Json.Text(record, "id"));
            if (Path.GetFileName(file) != id + ".json" || Json.Number(record, "schemaVersion") != 1 || Json.Number(record, "generation") != 1)
                throw new InvalidDataException("Invalid registry identity");
            var repository = record["repository"]!;
            Json.Fields(repository,["fullName","id","ownerId"]);
            var fullName = Json.Repository(Json.Text(repository, "fullName"));
            if (Json.Number(repository, "id") <= 0 || Json.Number(repository, "ownerId") <= 0 || !id.StartsWith("io.github." + fullName.Split('/')[0].ToLowerInvariant() + "."))
                throw new InvalidDataException("Invalid publisher identity");
            var versions = new HashSet<string>();
            var releases = record["releases"]!.AsArray();
            if (releases.Count is < 1 or > 128) throw new InvalidDataException("Invalid release count");
            foreach (var release in releases)
            {
                Packages.FixedRelease(release!, fullName);
                if (Json.Text(release!, "id") != id || !versions.Add(Json.Text(release!, "version"))) throw new InvalidDataException("Duplicate or incorrect release");
            }
            return record;
        })!;
    }
    public async Task<List<JsonNode>> Releases()
    {
        var list = Records().Values.SelectMany(record => record["releases"]!.AsArray().Select(x => x!.DeepClone())).ToList();
        foreach (var local in Json.Read(PathFor("catalog/plugins.json")).AsArray())
        {
            Json.Fields(local!,["package","sourceUrl"]);
            var package = Json.Text(local!, "package");
            if (!System.Text.RegularExpressions.Regex.IsMatch(package, "^packages/[a-z0-9][a-z0-9._-]{0,111}\\.zip$")) throw new InvalidDataException("Invalid local package path");
            var entry = await Packages.Inspect(await File.ReadAllBytesAsync(PathFor(package)));
            if (package != $"packages/{entry["id"]!.GetValue<string>()}-{entry["version"]!.GetValue<string>()}.zip") throw new InvalidDataException("Local package filename mismatch");
            entry["downloadUrl"] = $"https://raw.githubusercontent.com/{RepositoryName}/main/{package}";
            entry["sourceUrl"] = local!["sourceUrl"]!.DeepClone();
            list.Add(entry);
        }
        var keys = new HashSet<string>();
        foreach (var entry in list) if (!keys.Add(Json.Text(entry, "id") + ":" + Json.Text(entry, "version"))) throw new InvalidDataException("Duplicate ID/version");
        return list;
    }
    public async Task<JsonNode> BuildIndex()
    {
        var entries = await Releases();
        foreach (var entry in entries)
        {
            var review = Reviews.Read(this, entry);
            if (review != null)
            {
                var last = review["events"]!.AsArray().Last()!;
                entry["review"] = new JsonObject { ["status"] = last["status"]!.DeepClone(), ["reviewer"] = last["reviewer"]!.DeepClone(),
                    ["reviewedAt"] = last["createdAt"]!.DeepClone(), ["sha256"] = entry["sha256"]!.DeepClone() };
            }
        }
        return new JsonObject { ["schemaVersion"] = 1, ["product"] = "wonder", ["plugins"] = new JsonArray(entries.OrderBy(x => Json.Text(x,"id"), StringComparer.Ordinal)
            .ThenByDescending(x => System.Version.Parse(Json.Text(x,"version"))).ToArray()) };
    }
    public async Task Index(bool write)
    {
        var index = await BuildIndex();
        if (System.Text.Encoding.UTF8.GetByteCount(Json.Render(index)) > 1024*1024) throw new InvalidDataException("Index exceeds client size limit");
        if (write) Json.Write(PathFor("public/v1/index.json"), index);
        else if (!Json.Equal(Json.Read(PathFor("public/v1/index.json")), index)) throw new InvalidDataException("Stale index; run build-index");
    }
}
