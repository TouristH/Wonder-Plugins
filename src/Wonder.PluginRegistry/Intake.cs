// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Wonder.PluginRegistry;

public static class Intake
{
    public static string? SubmittedRepository(JsonNode issue)
    {
        try { return IssueRepository(issue); }
        catch (InvalidDataException) { return null; }
    }

    public static string IssueRepository(JsonNode issue)
    {
        if (issue["pull_request"] != null || !Json.Text(issue,"title").StartsWith("[Plugin]")) throw new InvalidDataException("Not a plugin submission");
        var match = Regex.Match(Json.Text(issue, "body"), @"(?m)^### (?:插件仓库 / Repository|仓库地址 / Repository URL)\s*\r?\n+https://github\.com/([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)\s*(?:\r?\n|$)");
        if (!match.Success) throw new InvalidDataException("Use the Plugin submission form and a GitHub repository URL");
        return Json.Repository(match.Groups[1].Value);
    }
    public static async Task<JsonNode> Publisher(GitHub api, string name)
    {
        name = Json.Repository(name);
        var repository = await api.Get($"/repos/{name}");
        if (repository["private"]!.GetValue<bool>() || repository["fork"]!.GetValue<bool>() || repository["archived"]!.GetValue<bool>() ||
            !string.Equals(Json.Text(repository,"full_name"), name, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Publisher must be a public, active non-fork repository");
        var branch = Json.Text(repository, "default_branch");
        var commit = await api.Get($"/repos/{name}/commits/{Uri.EscapeDataString(branch)}");
        var content = await api.Get($"/repos/{name}/contents/_manifest.json?ref={Json.Text(commit,"sha")}");
        if (Json.Number(content,"size") > 1024*1024 || Json.Text(content,"type") != "file" || Json.Text(content,"encoding") != "base64") throw new InvalidDataException("Invalid publisher manifest");
        var manifest = Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Json.Text(content,"content"))));
        Json.Fields(manifest,["schemaVersion","id","releases"]);
        var id = Json.Id(Json.Text(manifest,"id"));
        if (Json.Number(manifest,"schemaVersion") != 1 || !id.StartsWith("io.github." + Json.Text(repository["owner"]!,"login").ToLowerInvariant()+".",StringComparison.Ordinal))
            throw new InvalidDataException("Plugin ID must use its publisher namespace");
        var releases = manifest["releases"]!.AsArray();
        if (releases.Count is < 1 or > 128) throw new InvalidDataException("Publisher must list 1–128 releases");
        var versions = new HashSet<string>();
        foreach (var release in releases)
        {
            Json.Fields(release!, ["name","description","author","version","apiVersion","downloadUrl","sha256","size"], "permissions", "configurationSchema");
            release!["id"] = id; release["sourceUrl"] = $"https://github.com/{name}";
            Packages.FixedRelease(release, name);
            if (!versions.Add(Json.Text(release,"version"))) throw new InvalidDataException("Duplicate publisher version");
        }
        return new JsonObject { ["schemaVersion"] = 1, ["id"] = id, ["generation"] = 1,
            ["repository"] = new JsonObject { ["fullName"] = name, ["id"] = repository["id"]!.DeepClone(), ["ownerId"] = repository["owner"]!["id"]!.DeepClone() },
            ["releases"] = new JsonArray(releases.OrderBy(x => System.Version.Parse(Json.Text(x!,"version"))).Select(x => x!.DeepClone()).ToArray()) };
    }
    public static async Task VerifyUpdate(JsonNode? existing, JsonNode candidate, Func<string,long,Task<byte[]>> download)
    {
        if (existing != null)
        {
            foreach (var field in new[] { "id", "generation", "repository" })
                if (!Json.Equal(existing[field], candidate[field])) throw new InvalidDataException("Publisher identity is immutable");
            foreach (var previous in existing["releases"]!.AsArray())
                if (!candidate["releases"]!.AsArray().Any(next => Json.Equal(previous,next))) throw new InvalidDataException("Published versions are immutable and may not be removed");
        }
        foreach (var release in candidate["releases"]!.AsArray())
        {
            if (existing?["releases"]!.AsArray().Any(previous => Json.Equal(previous,release)) == true) continue;
            Packages.Match(release!, await Packages.Inspect(await download(Json.Text(release!,"downloadUrl"), Json.Number(release!,"size"))));
        }
    }
    public static IEnumerable<T> Rotate<T>(IReadOnlyList<T> values, int offset, int count) =>
        values.Count == 0 ? [] : values.Skip((int)((uint)offset % values.Count)).Concat(values).Take(Math.Min(count,values.Count));

    public static bool IsQueued(JsonNode issue) => IssueLabels.IsSubmission(issue) &&
        IssueLabels.HasLabel(issue, IssueLabels.Queued) && Json.Text(issue, "state") == "open";

    public static async Task Collect(Registry registry, GitHub api, JsonNode? eventData = null)
    {
        var repository = registry.RepositoryName;
        var offset = int.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER"), out var run) ? run : 0;
        List<JsonNode> selected;
        if (eventData != null)
        {
            if (eventData["issue"] is not { } issue || eventData["action"]?.GetValue<string>() is not ("opened" or "reopened" or "labeled")) return;
            var live = await api.Get($"/repos/{repository}/issues/{Json.Number(issue, "number")}");
            if (!IsQueued(live)) return;
            selected = [live];
        }
        else
        {
            // Bounded queue scan: the newest page plus a rotating older page.
            var issueQuery = $"/repos/{repository}/issues?state=open&labels={IssueLabels.PluginSubmission}%2C{IssueLabels.Queued}&sort=created&direction=desc&per_page=100";
            var issues = (await api.Get(issueQuery + "&page=1")).AsArray().Select(x => x!).ToList();
            var olderPage = 2 + (uint)offset / 7 % 32;
            issues.AddRange((await api.Get(issueQuery + $"&page={olderPage}")).AsArray().Select(x => x!));
            selected = Rotate(issues, offset * 16, 16).Where(IsQueued).ToList();
        }

        var records = registry.Records();
        var candidates = new Dictionary<string,List<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in selected.DistinctBy(x => Json.Number(x, "number")))
        {
            var number = Json.Number(issue, "number");
            try
            {
                var name = IssueRepository(issue);
                if (!candidates.TryGetValue(name, out var requests)) candidates[name] = requests = [];
                requests.Add(number);
            }
            catch (InvalidDataException error) { await ReportFailure(api, repository, number, error); }
        }
        if (eventData == null)
        {
            var existing = records.Values.Select(x => Json.Text(x["repository"]!, "fullName")).Order(StringComparer.Ordinal).ToArray();
            foreach (var name in Rotate(existing, offset * 16, 16)) candidates.TryAdd(name, []);
        }

        var changed = new JsonArray();
        var accepted = new JsonArray();
        foreach (var (name, requestIds) in candidates)
        {
            JsonNode next;
            JsonNode? old;
            try
            {
                next = await Publisher(api, name);
                records.TryGetValue(Json.Text(next, "id"), out old);
                if (old == null && requestIds.Count == 0)
                    throw new InvalidDataException("A new plugin ID needs a queued submission Issue");
                await VerifyUpdate(old, next, api.Download);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or FormatException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Console.WriteLine($"Deferred {name}: {error.Message}");
                foreach (var issue in requestIds) await ReportFailure(api, repository, issue, error);
                continue;
            }
            if (Json.Equal(old, next))
            {
                foreach (var issue in requestIds)
                {
                    await MarkListed(api, repository, issue);
                    await api.CommentOnce(repository, issue, "该插件已收录。维护者可对具体版本执行人工验证：\n\n" + Commands(next));
                }
                continue;
            }
            var id = Json.Text(next, "id");
            Json.Write(registry.PathFor($"registry/plugins/{id}.json"), next);
            changed.Add(id);
            foreach (var issue in requestIds) accepted.Add(issue);
        }
        if (changed.Count == 0) return;
        await registry.Index(true);
        Json.Write(registry.PathFor(".registry-operation.json"), new JsonObject { ["kind"] = "sync", ["plugins"] = changed, ["issues"] = accepted });
    }

    private static async Task ReportFailure(GitHub api, string repository, long issue, Exception error)
    {
        // Leave transient GitHub failures queued so the scheduled intake can retry.
        if (error is HttpRequestException http && (http.StatusCode == null || (int)http.StatusCode is 403 or 429 or >= 500)) return;
        await IssueLabels.Set(api, repository, issue, IssueLabels.IntakeFailed);
        await api.CommentOnce(repository, issue, "自动收录未通过：" + error.Message + "。修正后请通过投稿表单重新提交，或请维护者将本 Issue 重新加入收录队列。");
    }

    public static async Task MarkListed(GitHub api, string repository, long issue)
    {
        await IssueLabels.Set(api, repository, issue, IssueLabels.Listed);
        await IssueLabels.Set(api, repository, issue, IssueLabels.PendingReview, onlyIfReviewUnset: true);
    }

    public static string Commands(JsonNode record) => string.Join("\n",record["releases"]!.AsArray().Select(x=>$"`/verify {Json.Text(x!,"id")}@g1:{Json.Text(x!,"version")} sha256:{Json.Text(x!,"sha256")}`"));
}
