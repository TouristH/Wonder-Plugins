// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Wonder.PluginRegistry;

public sealed record ReviewCommand(string Action, string Id, int Generation, string Version, string Hash, string Note);
public static class Reviews
{
    private const long GitHubActionsBotId = 41898282;

    internal static string PackageIdentity(JsonNode entry) =>
        $"{Json.Text(entry, "id")}@g1:{Json.Text(entry, "version")} sha256:{Json.Text(entry, "sha256")}";

    private static string ApprovalPrompt(JsonNode entry) =>
        $"验证目标：`{PackageIdentity(entry)}`\n\n请维护者核对对应源码、构建方式、依赖及实际行为，确认后发送 `/ap`。";

    internal static async Task OfferSubmissionApproval(GitHub api, string repository, long issue, JsonNode record)
    {
        var releases = record["releases"]!.AsArray();
        var details = string.Join("\n", releases.Select(entry =>
            $"- `{PackageIdentity(entry!)}` · {Json.Number(entry!, "size")} 字节"));
        var next = releases.Count == 1 ? ApprovalPrompt(releases[0]!) :
            "此次投稿涉及多个版本。请用“请求人工验证”表单指定一个版本和它的 SHA-256。";
        await api.CommentOnce(repository, issue, "### 自动技术校验通过\n\n" +
            "- 核对了发布者仓库、默认分支清单和固定标签 Release ZIP。\n" +
            "- 核对了 ZIP 字节数、SHA-256、必需文件、入口及包内清单。\n" +
            "- 重新生成权限报告，并核对包内报告与文件指纹、分析结果是否一致。\n\n" +
            details + "\n\n" + next);
    }

    public static async Task OpenRequest(Registry registry, GitHub api, long issueNumber)
    {
        var repository = registry.RepositoryName;
        var issue = await api.Get($"/repos/{repository}/issues/{issueNumber}");
        if (!IssueLabels.IsReviewRequest(issue) || Json.Text(issue, "state") != "open") return;
        if (IssueLabels.HasLabel(issue, IssueLabels.Verified) || IssueLabels.HasLabel(issue, IssueLabels.Revoked) ||
            IssueLabels.HasLabel(issue, IssueLabels.ReviewPendingMerge)) return;
        var body = Json.Text(issue, "body");
        var entry = (await registry.Releases()).SingleOrDefault(release => MatchesRequestedRelease(body, release) &&
            HasField(body, "发布包 SHA-256 / SHA-256", Json.Text(release, "sha256")));
        if (entry == null)
        {
            await IssueLabels.Set(api, repository, issueNumber, IssueLabels.ReviewNeedsFix);
            await api.CommentOnce(repository, issueNumber, "找不到此表单指定的发布包。请核对插件 ID、版本和 SHA-256，修正后重开 Issue。");
            return;
        }
        await IssueLabels.Set(api, repository, issueNumber, IssueLabels.PendingReview);
        await api.CommentOnce(repository, issueNumber,
            "验证申请已匹配公开索引中的版本和 SHA-256。\n\n" + ApprovalPrompt(entry));
    }

    private static bool MatchesRequestedRelease(string body, JsonNode release) =>
        HasField(body, "插件 ID / Plugin ID", Json.Text(release, "id")) &&
        HasField(body, "插件版本 / Version", Json.Text(release, "version"));

    private static bool HasField(string body, string label, string value) =>
        Regex.IsMatch(body, $@"(?m)^### {Regex.Escape(label)}[^\S\r\n]*\r?\n(?:[^\S\r\n]*\r?\n)*{Regex.Escape(value)}[^\S\r\n]*\r?$");

    public static ReviewCommand Parse(string body)
    {
        var m = Regex.Match(body, @"\A/(verify|revoke-review) ([a-z0-9][a-z0-9._-]{0,79})@g([1-9][0-9]*):([0-9]+\.[0-9]+\.[0-9]+) sha256:([0-9a-f]{64})(?: ([^\r\n]{1,4096}))?\z");
        if (!m.Success || !int.TryParse(m.Groups[3].Value,out var generation)) throw new InvalidDataException("Use /verify id@g1:version sha256:<hash> [note] or /revoke-review ... reason");
        return new(m.Groups[1].Value,Json.Id(m.Groups[2].Value),generation,Json.Version(m.Groups[4].Value),Json.Hash(m.Groups[5].Value),m.Groups[6].Value);
    }
    private static bool IsUnedited(JsonNode comment) =>
        Json.Text(comment, "created_at") == Json.Text(comment, "updated_at");

    private static ReviewCommand ResolveCommand(JsonNode comment, IReadOnlyList<JsonNode> comments)
    {
        var body = Json.Text(comment, "body");
        if (body.Trim() != "/ap") return Parse(body);

        var id = Json.Number(comment, "id");
        var targets = comments
            .Where(candidate => Json.Number(candidate, "id") < id && candidate["user"] is { } author &&
                Json.Text(author, "type") == "Bot" && Json.Text(author, "login") == "github-actions[bot]" &&
                Json.Number(author, "id") == GitHubActionsBotId && IsUnedited(candidate))
            .SelectMany(candidate => Regex.Matches(Json.Text(candidate, "body"),
                @"(?m)^验证目标：`([a-z0-9][a-z0-9._-]{0,79}@g1:[0-9]+\.[0-9]+\.[0-9]+ sha256:[0-9a-f]{64})`\r?$")
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length != 1)
            throw new InvalidDataException("/ap requires one exact version and SHA-256 posted by the registry bot before the command");
        return Parse("/verify " + targets[0]);
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
        if (!IsUnedited(comment)) throw new InvalidDataException("Review commands must be new comments, not edited commands");
        var issueUrl = new Uri(Json.Text(comment,"issue_url"));
        if (issueUrl.Host != "api.github.com" || !issueUrl.AbsolutePath.StartsWith($"/repos/{registry.RepositoryName}/issues/")) throw new InvalidDataException("Invalid review Issue");
        var issue = await api.Get(issueUrl.AbsolutePath);
        var submission = IssueLabels.IsSubmission(issue);
        if (!submission && !IssueLabels.IsReviewRequest(issue)) throw new InvalidDataException("Review on the Plugin submission or Review Issue");
        var comments = await api.Pages(issueUrl.AbsolutePath + "/comments");
        var command = ResolveCommand(comment, comments);
        if (command.Generation != 1) throw new InvalidDataException("Unknown plugin generation");
        var entry = (await registry.Releases()).SingleOrDefault(x => Json.Text(x,"id") == command.Id && Json.Text(x,"version") == command.Version) ?? throw new InvalidDataException("Release is not listed");
        if (Json.Text(entry,"sha256") != command.Hash) throw new InvalidDataException("Review hash differs from the listed package");
        if (submission && !string.Equals(Json.Text(entry,"sourceUrl"), "https://github.com/" + Intake.IssueRepository(issue), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Command targets another publisher");
        if (!submission && !MatchesRequestedRelease(Json.Text(issue, "body"), entry)) throw new InvalidDataException("Review Issue must identify the exact plugin ID and version");
        if (!submission && Json.Text(comment, "body").Trim() == "/ap" &&
            !HasField(Json.Text(issue, "body"), "发布包 SHA-256 / SHA-256", command.Hash))
            throw new InvalidDataException("Review Issue must identify the exact package SHA-256");
        foreach (var newer in comments.Where(x => Json.Number(x,"id") > commentId && IsUnedited(x) && Trusted(registry.Config,x["user"]!)))
        {
            ReviewCommand other; try { other = ResolveCommand(newer, comments); } catch (InvalidDataException) { continue; }
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
