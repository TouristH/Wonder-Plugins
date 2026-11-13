// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Wonder.PluginRegistry;

public static class Publication
{
    public static string Git(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidDataException("Git failed: " + error);
        return output;
    }
    public static bool DataPath(string path) => path == "public/v1/index.json" || Regex.IsMatch(path,@"\Aregistry/plugins/[a-z0-9][a-z0-9._-]{0,79}\.json\z") || Regex.IsMatch(path,@"\Areviews/[a-z0-9][a-z0-9._-]{0,79}/g1/[0-9]+\.[0-9]+\.[0-9]+\.json\z");
    private static bool AppPullRequest(Registry registry, JsonNode pr) =>
        pr["user"]?["login"]?.GetValue<string>() == Json.Text(registry.Config,"registryBotLogin") &&
        Json.Number(pr["user"]!,"id") == Json.Number(registry.Config,"registryBotId") &&
        pr["user"]?["type"]?.GetValue<string>() == "Bot" && pr["base"]?["ref"]?.GetValue<string>() == "main" &&
        pr["base"]?["repo"]?["full_name"]?.GetValue<string>() == registry.RepositoryName &&
        pr["head"]?["repo"]?["full_name"]?.GetValue<string>() == registry.RepositoryName;
    public static async Task Publish(Registry registry, GitHub api)
    {
        var operation = Json.Read(registry.PathFor(".registry-operation.json"));
        var baseCommit = Git(registry.Root,"rev-parse","HEAD").Trim();
        var current = await api.Get($"/repos/{registry.RepositoryName}/git/ref/heads/main");
        if (Json.Text(current["object"]!,"sha") != baseCommit) throw new InvalidDataException("Main changed; rerun collection before publishing");
        var paths = Git(registry.Root,"ls-files","--modified","--others","--exclude-standard","-z").Split('\0',StringSplitOptions.RemoveEmptyEntries).Where(DataPath).ToArray();
        if (paths.Length == 0) { Console.WriteLine("No registry changes"); return; }
        if (paths.Any(x => !File.Exists(registry.PathFor(x)))) throw new InvalidDataException("Publication may not delete registry history");
        var commit = await api.Get($"/repos/{registry.RepositoryName}/git/commits/{baseCommit}");
        var tree = new JsonArray(paths.Order(StringComparer.Ordinal).Select(path => (JsonNode)new JsonObject { ["path"] = path, ["mode"] = "100644", ["type"] = "blob", ["content"] = File.ReadAllText(registry.PathFor(path)) }).ToArray());
        var newTree = await api.Send("POST",$"/repos/{registry.RepositoryName}/git/trees",new JsonObject { ["base_tree"] = commit["tree"]!["sha"]!.DeepClone(), ["tree"] = tree });
        var kind = Json.Text(operation,"kind");
        var branch = kind == "review" ? "registry-bot/review/" + Json.Number(operation,"commentId") : "registry-bot/sync/" + Json.Text(newTree,"sha")[..16];
        var existing = (await api.Get($"/repos/{registry.RepositoryName}/pulls?state=open&base=main&head={registry.RepositoryName.Split('/')[0]}:{branch}")).AsArray();
        var pr = existing.FirstOrDefault();
        if (pr != null)
        {
            var previousOperation = Operation(pr);
            if (!AppPullRequest(registry,pr) || Json.Text(pr["head"]!,"ref") != branch || Json.Text(previousOperation,"kind") != kind ||
                (kind == "review" && Json.Number(previousOperation,"commentId") != Json.Number(operation,"commentId")))
                throw new InvalidDataException("Existing PR does not belong to this App operation");
        }
        var newCommit = await api.Send("POST",$"/repos/{registry.RepositoryName}/git/commits",new JsonObject { ["message"] = "Update Wonder plugin " + kind, ["tree"] = newTree["sha"]!.DeepClone(), ["parents"] = new JsonArray(baseCommit) });
        try { await api.Send("POST",$"/repos/{registry.RepositoryName}/git/refs",new JsonObject { ["ref"] = "refs/heads/" + branch, ["sha"] = newCommit["sha"]!.DeepClone() }); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            var prior = await api.Get($"/repos/{registry.RepositoryName}/git/ref/heads/{branch}");
            var priorCommit = await api.Get($"/repos/{registry.RepositoryName}/git/commits/{Json.Text(prior["object"]!,"sha")}");
            var sameBase = priorCommit["parents"] is JsonArray parents && parents.Count == 1 && Json.Text(parents[0]!,"sha") == baseCommit;
            if (!Json.Equal(priorCommit["tree"]!["sha"],newTree["sha"]) || !sameBase)
            {
                if (pr == null) throw new InvalidDataException("Existing operation branch differs without an open App PR");
                await api.Send("PATCH",$"/repos/{registry.RepositoryName}/git/refs/heads/{branch}",new JsonObject { ["sha"] = newCommit["sha"]!.DeepClone(), ["force"] = true });
            }
        }
        var body = "Automated Wonder registry update. Technical collection does not imply human verification.\n\n<!-- wonder-operation:" + operation.ToJsonString() + " -->";
        if (pr == null) pr = await api.Send("POST",$"/repos/{registry.RepositoryName}/pulls",new JsonObject { ["title"] = "Update Wonder plugin " + kind, ["head"] = branch, ["base"] = "main", ["body"] = body });
        else await api.Send("PATCH",$"/repos/{registry.RepositoryName}/pulls/{Json.Number(pr,"number")}",new JsonObject { ["body"] = body });
        var graph = await api.Send("POST","/graphql",new JsonObject { ["query"] = "mutation($id:ID!){enablePullRequestAutoMerge(input:{pullRequestId:$id,mergeMethod:SQUASH}){pullRequest{id}}}", ["variables"] = new JsonObject { ["id"] = pr["node_id"]!.DeepClone() } });
        if (graph["errors"] is { } errors) throw new InvalidDataException("Cannot enable protected auto-merge: " + errors.ToJsonString());
        Console.WriteLine(Json.Text(pr,"html_url"));
    }
    public static JsonNode Operation(JsonNode pr)
    {
        var match = Regex.Match(Json.Text(pr,"body"), @"<!-- wonder-operation:(\{[^\r\n]*\}) -->");
        if (!match.Success) throw new InvalidDataException("Missing operation provenance");
        return Json.Parse(match.Groups[1].Value);
    }
    public static async Task Reconcile(Registry registry, GitHub api)
    {
        // This is a bounded recent-history reconciliation, not a complete collection.
        // Reaching its window must not stop all feedback once the repository has 200 PRs.
        var pullRequests = new List<JsonNode>();
        for (var page=1; page<=2; page++)
        {
            var batch = (await api.Get($"/repos/{registry.RepositoryName}/pulls?state=closed&base=main&sort=updated&direction=desc&per_page=100&page={page}")).AsArray();
            pullRequests.AddRange(batch.Select(x => x!));
            if (batch.Count < 100) break;
        }
        var releases = await registry.Releases(); var records = registry.Records();
        foreach (var pr in pullRequests.DistinctBy(x => Json.Number(x,"number")).Where(x => x["merged_at"] != null && AppPullRequest(registry,x)))
        {
            JsonNode operation; try { operation = Operation(pr); } catch (InvalidDataException) { continue; }
            var kind = Json.Text(operation,"kind"); var branch = Json.Text(pr["head"]!,"ref");
            if (kind == "review")
            {
                var commentId = Json.Number(operation,"commentId"); var relative = Json.Text(operation,"path");
                var entry = releases.FirstOrDefault(x => Reviews.Relative(x) == relative);
                if (branch != $"registry-bot/review/{commentId}" || entry == null ||
                    Reviews.Read(registry,entry)?["events"]?.AsArray().Any(x => Json.Number(x!,"commentId") == commentId) != true) continue;
            }
            else if (kind != "sync" || !branch.StartsWith("registry-bot/sync/") ||
                operation["plugins"] is not JsonArray plugins || plugins.Any(id => !records.ContainsKey(id!.GetValue<string>()))) continue;
            foreach (var issueId in operation["issues"]!.AsArray())
            {
                var id = issueId!.GetValue<long>(); var marker = $"<!-- wonder-merged:{Json.Number(pr,"number")} -->";
                var comments = await api.Pages($"/repos/{registry.RepositoryName}/issues/{id}/comments");
                if (comments.Any(x => x["user"]?["type"]?.GetValue<string>() == "Bot" && Json.Text(x,"body").Contains(marker))) continue;
                await api.Comment(registry.RepositoryName,id,$"变更已合并：{Json.Text(pr,"html_url")}。自动收录不代表人工验证；维护者可在本 Issue 对指定版本使用 /verify 或 /revoke-review。\n\n{marker}");
                // Keep submissions open for later version-specific commands and updates.
            }
        }
    }
}
