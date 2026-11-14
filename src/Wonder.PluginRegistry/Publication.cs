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
    public static async Task Publish(Registry registry, GitHub api, GitHub? issueApi = null)
    {
        var operation = Json.Read(registry.PathFor(".registry-operation.json"));
        var baseCommit = Git(registry.Root,"rev-parse","HEAD").Trim();
        var current = await api.Get($"/repos/{registry.RepositoryName}/git/ref/heads/main");
        if (Json.Text(current["object"]!,"sha") != baseCommit) throw new InvalidDataException("Main changed; rerun collection before publishing");
        var paths = Git(registry.Root,"ls-files","--modified","--others","--exclude-standard","-z").Split('\0',StringSplitOptions.RemoveEmptyEntries).Where(DataPath).Distinct().ToArray();
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
            if (kind == "sync")
            {
                var issueNumbers = previousOperation["issues"]!.AsArray().Concat(operation["issues"]!.AsArray())
                    .Select(issue => issue!.GetValue<long>()).Distinct();
                operation["issues"] = new JsonArray(issueNumbers.Select(number => (JsonNode?)JsonValue.Create(number)).ToArray());
            }
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
        else if (Json.Text(pr, "body") != body)
            pr = await api.Send("PATCH",$"/repos/{registry.RepositoryName}/pulls/{Json.Number(pr,"number")}",new JsonObject { ["body"] = body });
        if (pr["auto_merge"] == null)
        {
            var graph = await api.Send("POST","/graphql",new JsonObject { ["query"] = "mutation($id:ID!){enablePullRequestAutoMerge(input:{pullRequestId:$id,mergeMethod:SQUASH}){pullRequest{id}}}", ["variables"] = new JsonObject { ["id"] = pr["node_id"]!.DeepClone() } });
            if (graph["errors"] is { } errors) throw new InvalidDataException("Cannot enable protected auto-merge: " + errors.ToJsonString());
        }
        var url = Json.Text(pr, "html_url");
        Console.WriteLine(url);
        if (issueApi != null)
        {
            foreach (var number in operation["issues"]!.AsArray())
            {
                var issue = number!.GetValue<long>();
                await IssueLabels.Set(issueApi, registry.RepositoryName, issue,
                    kind == "review" ? IssueLabels.ReviewPendingMerge : IssueLabels.PendingMerge,
                    keepQueued: kind == "sync");
                if (kind == "sync") await issueApi.CommentOnce(registry.RepositoryName, issue,
                    $"自动技术校验已通过，等待收录 PR 合并：{url}。合并后才算收录。");
            }
        }
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
        var histories = releases.Select(entry => Reviews.Read(registry, entry)).OfType<JsonNode>().ToArray();
        var latestIssueReviews = histories.SelectMany(history => history["events"]!.AsArray())
            .GroupBy(item => Json.Number(item!, "issueNumber"))
            .ToDictionary(group => group.Key, group => group.Max(item => Json.Number(item!, "commentId")));
        foreach (var pr in pullRequests.DistinctBy(x => Json.Number(x,"number")).Where(x => x["merged_at"] != null && AppPullRequest(registry,x)))
        {
            JsonNode operation; try { operation = Operation(pr); } catch (InvalidDataException) { continue; }
            var kind = Json.Text(operation,"kind"); var branch = Json.Text(pr["head"]!,"ref");
            JsonNode? reviewEvent = null;
            JsonNode? reviewHistory = null;
            if (kind == "review")
            {
                var commentId = Json.Number(operation,"commentId"); var relative = Json.Text(operation,"path");
                var history = histories.FirstOrDefault(item => Reviews.Relative(item) == relative);
                if (branch != $"registry-bot/review/{commentId}" || history == null) continue;
                reviewHistory = history;
                reviewEvent = history["events"]!.AsArray().LastOrDefault();
                if (reviewEvent == null || Json.Number(reviewEvent, "commentId") != commentId) continue;
                if (latestIssueReviews[Json.Number(reviewEvent, "issueNumber")] != commentId) continue;
            }
            else if (kind != "sync" || !branch.StartsWith("registry-bot/sync/") ||
                operation["plugins"] is not JsonArray plugins || plugins.Any(id => !records.ContainsKey(id!.GetValue<string>()))) continue;
            var issueIds = reviewEvent == null
                ? operation["issues"]!.AsArray().Select(issue => issue!.GetValue<long>()).Distinct()
                : [Json.Number(reviewEvent, "issueNumber")];
            foreach (var id in issueIds)
            {
                var issuePath = $"/repos/{registry.RepositoryName}/issues/{id}";
                var marker = $"<!-- wonder-merged:{Json.Number(pr,"number")} -->";
                var comments = await api.Pages(issuePath + "/comments");
                var reported = comments.Any(x => x["user"]?["type"]?.GetValue<string>() == "Bot" && Json.Text(x,"body").Contains(marker));
                if (reported && reviewEvent != null) continue;
                var issue = await api.Get(issuePath);
                var verified = reviewEvent != null && Json.Text(reviewEvent, "status") == "verified";
                if (reviewEvent != null)
                {
                    if (!IssueLabels.IsSubmission(issue) && !IssueLabels.IsReviewRequest(issue)) continue;
                    await IssueLabels.Set(api, registry.RepositoryName, id, verified ? IssueLabels.Verified : IssueLabels.Revoked);
                }
                else
                {
                    if (!IssueLabels.IsSubmission(issue)) continue;
                    var publisher = Intake.SubmittedRepository(issue);
                    var record = operation["plugins"]!.AsArray().Select(plugin => records[plugin!.GetValue<string>()])
                        .FirstOrDefault(candidate => string.Equals(Json.Text(candidate["repository"]!, "fullName"),
                            publisher, StringComparison.OrdinalIgnoreCase));
                    if (record is null) continue;
                    if (!reported) await Intake.MarkListed(api, registry.RepositoryName, id);
                    if (Json.Text(issue, "state") == "open" && !IssueLabels.HasLabel(issue, IssueLabels.Verified))
                        await Reviews.OfferSubmissionApproval(api, registry.RepositoryName, id, record);
                    if (reported) continue;
                }
                if (verified && Json.Text(issue, "state") == "open")
                    await api.Send("PATCH", issuePath, new JsonObject { ["state"] = "closed", ["state_reason"] = "completed" });
                var status = "插件已收录，Issue 保持打开，等待维护者验证。";
                if (reviewEvent != null)
                {
                    var title = verified ? "人工验证通过" : "人工验证已撤销";
                    status = $"### {title}\n\n" +
                        $"- 发布包：`{Reviews.PackageIdentity(reviewHistory!)}`\n" +
                        $"- 维护者：@{Json.Text(reviewEvent, "reviewer")}\n" +
                        $"- 指令时间：{Json.Text(reviewEvent, "createdAt")}\n" +
                        $"- [维护者指令](https://github.com/{registry.RepositoryName}/issues/{id}#issuecomment-{Json.Number(reviewEvent, "commentId")})";
                    if (Json.Text(reviewEvent, "note") is { Length: > 0 } note)
                        status += "\n- 维护者备注：" + note;
                    status += verified ? "\n\n验证记录已写入索引，Issue 已关闭。" : "\n\n撤销记录已写入索引，仍可下载。";
                }
                await api.Comment(registry.RepositoryName, id, $"{status}\n\n{Json.Text(pr,"html_url")}\n\n{marker}");
            }
        }
    }
}
