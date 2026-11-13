// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Wonder.PluginRegistry;

public static class Policy
{
    public static async Task Check(Registry trusted, GitHub api, long number, string eventHead)
    {
        var pr = await api.Get($"/repos/{trusted.RepositoryName}/pulls/{number}");
        var head = Json.Text(pr["head"]!,"sha"); var baseSha = Json.Text(pr["base"]!,"sha");
        if (head != eventHead || baseSha != Publication.Git(trusted.Root,"rev-parse","HEAD").Trim()) throw new InvalidDataException("PR changed; rerun current policy");
        if (!System.Text.RegularExpressions.Regex.IsMatch(head,"^[0-9a-f]{40}$")) throw new InvalidDataException("Invalid commit SHA");
        Publication.Git(trusted.Root,"fetch","--no-tags","origin",head);
        var paths = Publication.Git(trusted.Root,"diff","--name-only","-z",baseSha,head).Split('\0',StringSplitOptions.RemoveEmptyEntries);
        if (paths.Any(x => x.StartsWith("plugins/", StringComparison.Ordinal) && x.EndsWith(".json", StringComparison.Ordinal) || x == "migrations/v2-bootstrap.json"))
            throw new InvalidDataException("Legacy identity and release archives are immutable");
        var actor = pr["user"]!;
        var bot = Json.Text(actor,"login") == Json.Text(trusted.Config,"registryBotLogin") && Json.Number(actor,"id") == Json.Number(trusted.Config,"registryBotId") && Json.Text(actor,"type") == "Bot";
        var branch = Json.Text(pr["head"]!,"ref");
        if (!bot)
        {
            if (paths.Any(Publication.DataPath) || branch.StartsWith("registry-bot/")) throw new InvalidDataException("Registry facts require the App workflow");
            if (paths.Any(x => x == "repository.json" || x.StartsWith(".github/")) && !Reviews.Trusted(trusted.Config,actor)) throw new InvalidDataException("Trust configuration requires a current trusted maintainer");
            Console.WriteLine("Non-registry code PR: no registry facts changed"); return;
        }
        if (Json.Text(pr["head"]!["repo"]!,"full_name") != trusted.RepositoryName || Json.Text(pr["base"]!,"ref") != "main" || paths.Any(x => !Publication.DataPath(x)))
            throw new InvalidDataException("App PR must contain only registry data in this repository");
        var temporary = Directory.CreateTempSubdirectory("wonder-policy-").FullName;
        try
        {
            foreach (var relative in Publication.Git(trusted.Root,"ls-files","-z").Split('\0',StringSplitOptions.RemoveEmptyEntries))
            {
                if (relative.StartsWith(".git/")) continue;
                var destination = Path.Combine(temporary,relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(trusted.PathFor(relative),destination);
            }
            foreach (var relative in paths)
            {
                var tree = Publication.Git(trusted.Root,"ls-tree",head,"--",relative);
                if (!tree.StartsWith("100644 blob ") && !tree.StartsWith("100755 blob ")) throw new InvalidDataException("Data deletion or symlink is forbidden");
                var text = Publication.Git(trusted.Root,"show",head+":"+relative);
                if (text.Length > 1024*1024) throw new InvalidDataException("Registry JSON exceeds limit");
                Json.Write(Path.Combine(temporary,relative),Json.Parse(text));
            }
            var candidate = new Registry(temporary); var operation = Publication.Operation(pr);
            if (Json.Text(operation,"kind") == "review")
            {
                var commentId = Json.Number(operation,"commentId");
                if (branch != $"registry-bot/review/{commentId}") throw new InvalidDataException("Review branch mismatch");
                var expected = await Reviews.Apply(trusted,api,commentId); var relative = Reviews.Relative(expected);
                if (paths.Any(x => x != relative && x != "public/v1/index.json") || !paths.Contains(relative) || !Json.Equal(expected,Json.Read(candidate.PathFor(relative)))) throw new InvalidDataException("Review differs from trusted command replay");
            }
            else if (Json.Text(operation,"kind") == "sync" && branch.StartsWith("registry-bot/sync/"))
            {
                var old = trusted.Records(); var next = candidate.Records();
                foreach (var relative in paths.Where(x => x != "public/v1/index.json"))
                {
                    if (!relative.StartsWith("registry/plugins/")) throw new InvalidDataException("Sync cannot change reviews");
                    var id = Path.GetFileNameWithoutExtension(relative); var value = next[id];
                    var canonical = await Intake.Publisher(api,Json.Text(value["repository"]!,"fullName"));
                    if (!Json.Equal(canonical,value)) throw new InvalidDataException("Sync differs from publisher manifest");
                    old.TryGetValue(id,out var prior); await Intake.VerifyUpdate(prior,value,api.Download);
                }
                if (old.Keys.Except(next.Keys).Any()) throw new InvalidDataException("Sync removed history");
            }
            else throw new InvalidDataException("Unknown registry operation");
            await candidate.Index(false);
        }
        finally { Directory.Delete(temporary,true); }
    }
}
