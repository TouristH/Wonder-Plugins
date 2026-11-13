// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Wonder.PluginRegistry;

public sealed record ReviewCommand(string Action, string Id, int Generation, string Version, string Hash, string Note);
public static class Reviews
{
    public static ReviewCommand Parse(string body)
    {
        var m = Regex.Match(body, @"\A/(verify|revoke-review) ([a-z0-9][a-z0-9._-]{0,79})@g([1-9][0-9]*):([0-9]+\.[0-9]+\.[0-9]+) sha256:([0-9a-f]{64})(?: ([^\r\n]{1,4096}))?\z");
        if (!m.Success || !int.TryParse(m.Groups[3].Value,out var generation)) throw new InvalidDataException("Use /verify id@g1:version sha256:<hash> [note] or /revoke-review ... reason");
        return new(m.Groups[1].Value,Json.Id(m.Groups[2].Value),generation,Json.Version(m.Groups[4].Value),Json.Hash(m.Groups[5].Value),m.Groups[6].Value);
    }
    public static bool Trusted(JsonNode config, JsonNode actor) => config["trustedReviewers"]!.AsArray().Any(x => x!.GetValue<string>() == Json.Text(actor,"login")) &&
        config["trustedReviewerIds"]?[Json.Text(actor,"login")]?.GetValue<long>() == Json.Number(actor,"id");
    public static string Relative(JsonNode entry) => $"reviews/{Json.Id(Json.Text(entry,"id"))}/g1/{Json.Version(Json.Text(entry,"version"))}.json";
    public static JsonNode? Read(Registry registry, JsonNode entry)
    {
        var path = registry.PathFor(Relative(entry)); if (!File.Exists(path)) return null;
        var history = Json.Read(path);
        Json.Fields(history,["schemaVersion","id","generation","version","sha256","events"]);
        if (Json.Number(history,"schemaVersion") != 1 || Json.Number(history,"generation") != 1 ||
            new[] {"id","version","sha256"}.Any(key => !Json.Equal(history[key],entry[key]))) throw new InvalidDataException("Review is not bound to this package");
        long last = 0;
        foreach (var item in history["events"]!.AsArray())
        {
            Json.Fields(item!,["commentId","issueNumber","reviewer","reviewerId","status","createdAt","note"]);
            if (Json.Number(item!,"commentId") <= last || Json.Number(item!,"reviewerId") <= 0 || Json.Number(item!,"issueNumber") <= 0 ||
                Json.Text(item!,"status") is not ("verified" or "revoked") || !DateTimeOffset.TryParse(Json.Text(item!,"createdAt"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out _))
                throw new InvalidDataException("Invalid review history");
            last = Json.Number(item!,"commentId");
        }
        if (last == 0) throw new InvalidDataException("Empty review history");
        var latest = history["events"]!.AsArray().Last()!;
        if (Json.Text(latest,"status") == "verified" && !Trusted(registry.Config,new JsonObject { ["login"] = latest["reviewer"]!.DeepClone(), ["id"] = latest["reviewerId"]!.DeepClone() }))
            throw new InvalidDataException("Verified reviewer no longer trusted; revoke before removing reviewer");
        return history;
    }
    public static async Task<JsonNode> Apply(Registry registry, GitHub api, long commentId, Func<string,long,Task<byte[]>>? downloader = null)
    {
        var comment = await api.Get($"/repos/{registry.RepositoryName}/issues/comments/{commentId}");
        if (!Trusted(registry.Config,comment["user"]!)) throw new InvalidDataException("Only configured reviewer login and numeric ID may review");
        var command = Parse(Json.Text(comment,"body"));
        if (command.Generation != 1) throw new InvalidDataException("Unknown plugin generation");
        if (Json.Text(comment,"created_at") != Json.Text(comment,"updated_at")) throw new InvalidDataException("Review commands must be new comments, not edited commands");
        var issueUrl = new Uri(Json.Text(comment,"issue_url"));
        if (issueUrl.Host != "api.github.com" || !issueUrl.AbsolutePath.StartsWith($"/repos/{registry.RepositoryName}/issues/")) throw new InvalidDataException("Invalid review Issue");
        var issue = await api.Get(issueUrl.AbsolutePath);
        if (issue["pull_request"] != null || !(Json.Text(issue,"title").StartsWith("[Plugin]") || Json.Text(issue,"title").StartsWith("[Review]"))) throw new InvalidDataException("Review on the Plugin submission or Review Issue");
        var entry = (await registry.Releases()).SingleOrDefault(x => Json.Text(x,"id") == command.Id && Json.Text(x,"version") == command.Version) ?? throw new InvalidDataException("Release is not listed");
        if (Json.Text(entry,"sha256") != command.Hash) throw new InvalidDataException("Review hash differs from the listed package");
        if (Json.Text(issue,"title").StartsWith("[Plugin]") && Json.Text(entry,"sourceUrl") != "https://github.com/" + Intake.IssueRepository(issue)) throw new InvalidDataException("Command targets another publisher");
        if (Json.Text(issue,"title").StartsWith("[Review]") && !Regex.IsMatch(Json.Text(issue,"body"), @"(?m)^"+Regex.Escape(command.Id)+@"\s*$")) throw new InvalidDataException("Review Issue must identify plugin ID");
        var comments = await api.Pages(issueUrl.AbsolutePath + "/comments");
        foreach (var newer in comments.Where(x => Json.Number(x,"id") > commentId && Trusted(registry.Config,x["user"]!)))
        {
            ReviewCommand other; try { other = Parse(Json.Text(newer,"body")); } catch (InvalidDataException) { continue; }
            if (other.Id == command.Id && other.Version == command.Version && other.Generation == command.Generation && other.Hash == command.Hash)
                throw new InvalidDataException("A newer reviewer command supersedes this command");
        }
        var history = Read(registry,entry) ?? new JsonObject { ["schemaVersion"] = 1, ["id"] = command.Id, ["generation"] = 1, ["version"] = command.Version, ["sha256"] = command.Hash, ["events"] = new JsonArray() };
        var events = history["events"]!.AsArray();
        if (events.LastOrDefault() is { } last)
        {
            if (Json.Number(last,"commentId") > commentId) throw new InvalidDataException("A newer stored review supersedes this command");
            if (Json.Number(last,"commentId") == commentId) return history;
        }
        // A removed or unreachable asset must never prevent withdrawing its verification.
        if (command.Action == "verify") Packages.Match(entry, await Packages.Inspect(await (downloader ?? api.Download)(Json.Text(entry,"downloadUrl"),Json.Number(entry,"size"))));
        events.Add(new JsonObject { ["commentId"] = commentId, ["issueNumber"] = Json.Number(issue,"number"), ["reviewer"] = comment["user"]!["login"]!.DeepClone(), ["reviewerId"] = comment["user"]!["id"]!.DeepClone(),
            ["status"] = command.Action == "verify" ? "verified" : "revoked", ["createdAt"] = comment["created_at"]!.DeepClone(), ["note"] = command.Note });
        return history;
    }
    public static async Task Write(Registry registry, GitHub api, long commentId)
    {
        var history = await Apply(registry,api,commentId);
        Json.Write(registry.PathFor(Relative(history)),history); await registry.Index(true);
        Json.Write(registry.PathFor(".registry-operation.json"),new JsonObject { ["kind"] = "review", ["commentId"] = commentId, ["path"] = Relative(history), ["issues"] = new JsonArray(history["events"]!.AsArray().Last()!["issueNumber"]!.DeepClone()) });
    }
}
