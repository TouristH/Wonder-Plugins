// SPDX-License-Identifier: MIT
using Wonder.Extensibility;
using Wonder.PluginRegistry;

try
{
    if (args.Length == 0) throw new ArgumentException("Commands: validate, build-index, sync, issue, review, publish, reconcile, policy, report, check-package");
    var root = Environment.GetEnvironmentVariable("WONDER_REGISTRY_ROOT") ?? Directory.GetCurrentDirectory();
    var registry = new Registry(root);
    using var api = new GitHub(Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
    switch (args[0])
    {
        case "validate": await registry.Index(false); break;
        case "build-index": await registry.Index(true); break;
        case "sync": await Intake.Collect(registry,api); break;
        case "issue":
            var issueEvent = Json.Read(args[1]);
            if (Json.Text(issueEvent["issue"]!,"title").StartsWith("[Plugin]")) await Intake.Collect(registry,api,issueEvent);
            else if (Json.Text(issueEvent["issue"]!,"title").StartsWith("[Review]"))
            {
                var releases = await registry.Releases();
                var body = Json.Text(issueEvent["issue"]!,"body");
                var matches = releases.Where(x => body.Split('\n').Any(line => line.Trim() == Json.Text(x,"id"))).ToArray();
                if (matches.Length == 0) throw new InvalidDataException("No listed release matches this Review Issue");
                await api.Comment(registry.RepositoryName,Json.Number(issueEvent["issue"]!,"number"),"供维护者完成源码与行为检查后使用（每个版本独立验证）：\n\n"+string.Join("\n",matches.Select(x => $"`/verify {Json.Text(x,"id")}@g1:{Json.Text(x,"version")} sha256:{Json.Text(x,"sha256")}`")));
            }
            break;
        case "review":
            var reviewEvent = Json.Read(args[1]);
            if (reviewEvent["action"]?.GetValue<string>() != "created") throw new InvalidDataException("Only newly created comments are accepted");
            await Reviews.Write(registry,api,Json.Number(reviewEvent["comment"]!,"id")); break;
        case "publish": await Publication.Publish(registry,api); break;
        case "reconcile": await Publication.Reconcile(registry,api); break;
        case "policy":
            var policyEvent = Json.Read(args[1]);
            await Policy.Check(registry,api,Json.Number(policyEvent,"number"),Json.Text(policyEvent["pull_request"]!["head"]!,"sha")); break;
        case "report":
            if (args.Length != 3 || args[1] is not ("--generate" or "--verify")) throw new ArgumentException("report --generate|--verify <unpacked-plugin-directory>");
            if (args[1] == "--generate") PluginPackageReport.Write(args[2]); else PluginPackageReport.Verify(args[2]); break;
        case "check-package": await Packages.Inspect(await File.ReadAllBytesAsync(args[1])); break;
        default: throw new ArgumentException("Unknown command");
    }
    Console.WriteLine("Wonder registry check completed."); return 0;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
