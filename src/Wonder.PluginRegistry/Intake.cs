// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Wonder.PluginRegistry;

public static class Intake
{
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
            Json.Fields(release!, ["name","description","author","version","apiVersion","downloadUrl","sha256","size"], "permissions");
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

    public static async Task Collect(Registry registry, GitHub api, JsonNode? eventData = null)
    {
        var records = registry.Records();
        var offset = int.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER"), out var run) ? run : 0;
        // Two bounded pages: the newest queue plus a rotating older page. No full-list traversal.
        var issueQuery = $"/repos/{registry.RepositoryName}/issues?state=open&labels=plugin-submission&sort=created&direction=desc&per_page=100";
        var issues = (await api.Get(issueQuery+"&page=1")).AsArray().Select(x=>x!).ToList();
        var olderPage = 2 + (uint)offset / 7 % 32;
        issues.AddRange((await api.Get(issueQuery+$"&page={olderPage}")).AsArray().Select(x=>x!));
        var selected = Rotate(issues,offset*16,16).ToList();
        if (eventData?["issue"] is { } direct && eventData["action"]?.GetValue<string>() is "opened" or "reopened")
        {
            var live = await api.Get($"/repos/{registry.RepositoryName}/issues/{Json.Number(direct,"number")}");
            if (Json.Text(live,"title").StartsWith("[Plugin]")) selected.Insert(0,live);
        }
        var candidates = new Dictionary<string,List<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in selected.DistinctBy(x=>Json.Number(x,"number")).Take(17))
        {
            try { var name = IssueRepository(issue); if (!candidates.TryGetValue(name, out var requests)) candidates[name] = requests = []; requests.Add(Json.Number(issue,"number")); }
            catch (InvalidDataException error) { await api.CommentOnce(registry.RepositoryName,Json.Number(issue,"number"),"自动收录未通过："+error.Message+"。修正表单后关闭并重新打开 Issue 可重试。"); }
        }
        // Rotate existing publishers and discovery separately so one queue cannot starve the other.
        var existing = records.Values.Select(x => Json.Text(x["repository"]!,"fullName")).Order(StringComparer.Ordinal).ToArray();
        foreach (var name in Rotate(existing,offset*16,16)) candidates.TryAdd(name,[]);
        try
        {
            var query = "/search/repositories?q=topic%3Awonder-plugin+fork%3Afalse+archived%3Afalse&sort=updated&per_page=16";
            var search = await api.Get(query+"&page=1");
            foreach (var repo in search["items"]!.AsArray()) candidates.TryAdd(Json.Text(repo!,"full_name"),[]);
            var pages = Math.Min(62,(Json.Number(search,"total_count")+15)/16);
            if (pages > 1)
                foreach (var repo in (await api.Get(query+$"&page={2+(uint)offset%(pages-1)}"))["items"]!.AsArray()) candidates.TryAdd(Json.Text(repo!,"full_name"),[]);
        }
        catch (HttpRequestException error) { Console.WriteLine("Topic discovery deferred: "+error.Message); }
        var changed = new JsonArray(); var accepted = new JsonArray();
        foreach (var (name, requestIds) in candidates)
        {
            try
            {
                var next = await Publisher(api,name); var id = Json.Text(next,"id"); records.TryGetValue(id,out var old);
                await VerifyUpdate(old,next,api.Download);
                if (!Json.Equal(old,next)) { Json.Write(registry.PathFor($"registry/plugins/{id}.json"),next); changed.Add(id); }
                foreach (var issue in requestIds)
                {
                    accepted.Add(issue);
                    await api.CommentOnce(registry.RepositoryName,issue,Json.Equal(old,next)
                        ? "该插件已收录。维护者可对具体版本执行人工验证：\n\n"+Commands(next)
                        : "自动技术校验已通过，正在准备受保护的收录 PR；合并后才算收录。人工验证尚未完成。");
                }
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or FormatException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Console.WriteLine($"Deferred {name}: {error.Message}");
                foreach (var issue in requestIds) await api.CommentOnce(registry.RepositoryName,issue,"自动收录未完成："+error.Message+"。已有版本保留；修正后关闭并重新打开 Issue 可重试，网络失败也会在定时任务中重试。");
            }
        }
        await registry.Index(true);
        Json.Write(registry.PathFor(".registry-operation.json"), new JsonObject { ["kind"] = "sync", ["plugins"] = changed, ["issues"] = accepted });
    }
    public static string Commands(JsonNode record) => string.Join("\n",record["releases"]!.AsArray().Select(x=>$"`/verify {Json.Text(x!,"id")}@g1:{Json.Text(x!,"version")} sha256:{Json.Text(x!,"sha256")}`"));
}
