// SPDX-License-Identifier: MIT
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wonder.Extensibility;
using Wonder.Extensibility.Contracts;
using Wonder.PluginRegistry;

public sealed class RegistryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("wonder-registry-tests-").FullName;
    private const string Id = "io.github.example.toolbox";
    private const string Source = "https://github.com/example/toolbox";
    private Registry registry;
    public RegistryTests()
    {
        registry = new(root);
        Json.Write(registry.PathFor("catalog/plugins.json"),new JsonArray());
        Json.Write(registry.PathFor("repository.json"),Json.Parse("""{"sourceUrl":"https://github.com/example/registry","registryBotLogin":"test[bot]","registryBotId":55,"trustedReviewers":["maintainer"],"trustedReviewerIds":{"maintainer":42}}"""));
    }
    private byte[] Package(string version="1.0.0", bool report=true, bool tamper=false, string? entryType=null, bool badEntry=false, int apiVersion=1, PluginConfigurationSchema? configurationSchema=null)
    {
        var folder = Path.Combine(root,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        File.Copy(typeof(PackageProbe).Assembly.Location,Path.Combine(folder,"plugin.dll"));
        if (badEntry) File.WriteAllText(Path.Combine(folder,"plugin.dll"),"MZ invalid assembly");
        File.WriteAllText(Path.Combine(folder,"plugin.json"),JsonSerializer.Serialize(new PluginManifest { Id=Id,Name="Test",Description="Test package",Author="Example",Version=version,ApiVersion=apiVersion,ConfigurationSchema=configurationSchema,EntryAssembly="plugin.dll",EntryType=entryType ?? typeof(PackageProbe).FullName! },Json.Options));
        File.WriteAllText(Path.Combine(folder,"LICENSE"),"MIT test fixture"); File.WriteAllText(Path.Combine(folder,"RISK.md"),"Full trust. Static analysis is not a safety guarantee.");
        if (report) PluginPackageReport.Write(folder);
        if (tamper) File.WriteAllText(Path.Combine(folder,"RISK.md"),"changed after reporting");
        var zip=folder+".zip"; ZipFile.CreateFromDirectory(folder,zip); return File.ReadAllBytes(zip);
    }
    private static JsonNode Record(params JsonNode[] entries) => new JsonObject { ["schemaVersion"]=1,["id"]=Id,["generation"]=1,["repository"]=new JsonObject{["fullName"]="example/toolbox",["id"]=10,["ownerId"]=20},["releases"]=new JsonArray(entries.Select(x=>x.DeepClone()).ToArray()) };
    private async Task<JsonNode> Entry(byte[] bytes,string version="1.0.0") { var entry=await Packages.Inspect(bytes); entry["sourceUrl"]=Source;entry["downloadUrl"]=$"{Source}/releases/download/v{version}/plugin.zip";return entry; }
    [Theory][InlineData(false,false)][InlineData(true,true)]
    public async Task MissingOrStaleGeneratedReportRejectsPackage(bool report,bool tamper) => await Assert.ThrowsAnyAsync<Exception>(()=>Packages.Inspect(Package(report:report,tamper:tamper)));
    [Fact] public async Task ValidPackageDoesNotExecutePlugin() { PackageProbe.Called=false;await Packages.Inspect(Package());Assert.False(PackageProbe.Called); }
    [Theory][InlineData(true)][InlineData(false)] public async Task EntryMustBeManagedAndNameAnExistingType(bool badEntry)
    {
        await Assert.ThrowsAnyAsync<Exception>(()=>Packages.Inspect(Package(entryType:badEntry?null:"Missing.Plugin",badEntry:badEntry)));
    }
    [Fact] public async Task PublisherCannotSetReviewOrUnknownPublicFields()
    {
        var entry=await Entry(Package());entry["review"]=new JsonObject{["status"]="verified"};
        Assert.Throws<InvalidDataException>(()=>Packages.FixedRelease(entry,"example/toolbox"));
    }
    [Fact]
    public async Task ApiTwoConfigurationSurvivesIntakeAndIndexAndMustMatchThePackage()
    {
        const string configuration = """
            {"fields":[{"key":"greeting","label":"Greeting","default":"Hello","maxLength":120}]}
            """;
        var bytes = Package(apiVersion: 2, configurationSchema: JsonSerializer.Deserialize<PluginConfigurationSchema>(configuration, Json.Options));
        var entry = await Entry(bytes);

        var publisherEntry = entry.DeepClone();
        foreach (var key in new[] { "id", "sourceUrl" }) publisherEntry.AsObject().Remove(key);
        var manifest = new JsonObject { ["schemaVersion"] = 1, ["id"] = Id, ["releases"] = new JsonArray(publisherEntry) };
        var content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        using var api = new GitHub(null, new Reply(request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/example/toolbox" => Json.Parse("""{"full_name":"example/toolbox","default_branch":"main","private":false,"fork":false,"archived":false,"id":10,"owner":{"id":20,"login":"example"}}"""),
            "/repos/example/toolbox/commits/main" => Json.Parse("""{"sha":"abc"}"""),
            "/repos/example/toolbox/contents/_manifest.json" => new JsonObject { ["size"] = content.Length, ["type"] = "file", ["encoding"] = "base64", ["content"] = content },
            _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath)
        }));
        var publisher = await Intake.Publisher(api, "example/toolbox");
        await Intake.VerifyUpdate(null, publisher, (_, _) => Task.FromResult(bytes));
        Json.Write(registry.PathFor($"registry/plugins/{Id}.json"), publisher);
        var indexed = (await registry.BuildIndex())["plugins"]![0]!;
        Assert.Equal(2, Json.Number(indexed, "apiVersion"));
        Assert.Equal("Hello", indexed["configurationSchema"]!["fields"]![0]!["default"]!.GetValue<string>());
        publisher["releases"]![0]!["configurationSchema"]!["fields"]![0]!["default"] = "Changed";
        await Assert.ThrowsAsync<InvalidDataException>(() => Intake.VerifyUpdate(null, publisher, (_, _) => Task.FromResult(bytes)));
    }

    [Fact] public void QueueRotationEventuallyVisitsEveryOpenIssue()
    {
        var issues=Enumerable.Range(0,200).ToArray();
        var visited=Enumerable.Range(0,25).SelectMany(run=>Intake.Rotate(issues,run*16,16)).ToHashSet();
        Assert.Equal(200,visited.Count);Assert.Empty(Intake.Rotate(Array.Empty<int>(),99,16));
    }
    [Fact] public async Task IntakePreservesEveryVersionAndRejectsChangedHistory()
    {
        var one=Package();var first=await Entry(one); var old=Record(first);
        var two=Package("1.1.0");var second=await Entry(two,"1.1.0"); var next=Record(first,second);
        var downloads=0;
        await Intake.VerifyUpdate(old,next,(_,_)=>{downloads++;return Task.FromResult(two);});Assert.Equal(1,downloads);
        var changed=next.DeepClone();changed["releases"]![0]!["sha256"]=new string('a',64);
        await Assert.ThrowsAsync<InvalidDataException>(()=>Intake.VerifyUpdate(old,changed,(_,_)=>Task.FromResult(two)));
        Json.Write(registry.PathFor($"registry/plugins/{Id}.json"),next);
        var entries=(await registry.BuildIndex())["plugins"]!.AsArray();Assert.Equal(2,entries.Count);Assert.Equal("1.1.0",Json.Text(entries[0]!,"version"));Assert.All(entries,x=>Assert.Null(x!["review"]));
    }
    [Fact] public async Task NumericPublisherIdentityCannotBeReused()
    {
        var entry=await Entry(Package()); var old=Record(entry);var next=old.DeepClone();next["repository"]!["id"]=999;
        await Assert.ThrowsAsync<InvalidDataException>(()=>Intake.VerifyUpdate(old,next,(_,_)=>throw new Exception("must not download")));
    }
    [Fact] public void SubmissionIssueQueuesWithoutApproval()
    {
        var issue=Json.Parse("""{"title":"[Plugin] Toolbox","body":"### 插件仓库 / Repository\n\nhttps://github.com/example/toolbox\n"}""");
        Assert.Equal("example/toolbox",Intake.IssueRepository(issue));
        issue["pull_request"]=new JsonObject();Assert.Throws<InvalidDataException>(()=>Intake.IssueRepository(issue));
    }
    [Fact] public void ReviewerNeedsBothLoginAndNumericIdentity()
    {
        Assert.True(Reviews.Trusted(registry.Config,Json.Parse("""{"login":"maintainer","id":42}""")));
        Assert.False(Reviews.Trusted(registry.Config,Json.Parse("""{"login":"maintainer","id":43}""")));
    }
    [Theory][InlineData("verified")][InlineData("revoked")]
    public async Task ReviewIsExactVersionAndHashAndDoesNotRemoveDownloads(string status)
    {
        var entry=await Entry(Package());Json.Write(registry.PathFor($"registry/plugins/{Id}.json"),Record(entry));
        var history=History(entry,status);Json.Write(registry.PathFor(Reviews.Relative(entry)),history);
        var index=await registry.BuildIndex();Assert.Equal(status,Json.Text(index["plugins"]![0]!["review"]!,"status"));
        history["sha256"]=new string('f',64);Json.Write(registry.PathFor(Reviews.Relative(entry)),history);
        await Assert.ThrowsAsync<InvalidDataException>(()=>registry.BuildIndex());
    }
    private static JsonNode History(JsonNode entry,string status) => new JsonObject{["schemaVersion"]=1,["id"]=Id,["generation"]=1,["version"]="1.0.0",["sha256"]=entry["sha256"]!.DeepClone(),["events"]=new JsonArray(new JsonObject{["commentId"]=100,["issueNumber"]=1,["reviewer"]="maintainer",["reviewerId"]=42,["status"]=status,["createdAt"]="2026-01-01T00:00:00Z",["note"]="Test"})};
    [Fact] public async Task RevocationKeepsHistoryAfterReviewerLeaves()
    {
        var entry=await Entry(Package());Json.Write(registry.PathFor($"registry/plugins/{Id}.json"),Record(entry));
        var history=History(entry,"verified");var revocation=history["events"]![0]!.DeepClone();revocation["commentId"]=101;revocation["status"]="revoked";
        history["events"]!.AsArray().Add(revocation);Json.Write(registry.PathFor(Reviews.Relative(entry)),history);
        var settings=registry.Config;settings["trustedReviewers"]=new JsonArray();settings["trustedReviewerIds"]=new JsonObject();Json.Write(registry.PathFor("repository.json"),settings);
        Assert.Equal("revoked",Json.Text((await registry.BuildIndex())["plugins"]![0]!["review"]!,"status"));
        history["events"]!.AsArray().RemoveAt(1);Json.Write(registry.PathFor(Reviews.Relative(entry)),history);
        await Assert.ThrowsAsync<InvalidDataException>(()=>registry.BuildIndex());
    }
    [Fact] public async Task ReviewCommandInOriginalSubmissionUsesLiveActorAndExactPackage()
    {
        var bytes=Package();var entry=await Entry(bytes);Json.Write(registry.PathFor($"registry/plugins/{Id}.json"),Record(entry));
        var comment=Json.Parse($$"""{"id":101,"body":"/verify {{Id}}@g1:1.0.0 sha256:{{Json.Text(entry,"sha256")}}","user":{"login":"maintainer","id":42},"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","issue_url":"https://api.github.com/repos/example/registry/issues/1"}""");
        var issue=SubmissionIssue();
        using var api=new GitHub(null,new Reply(request=>request.RequestUri!.AbsolutePath.EndsWith("comments/101") ? comment : request.RequestUri.AbsolutePath.EndsWith("/comments") ? new JsonArray(comment.DeepClone()) : issue));
        var history=await Reviews.Apply(registry,api,101,(_,_)=>Task.FromResult(bytes));Assert.Equal("verified",Json.Text(history["events"]![0]!,"status"));
        comment["user"]!["id"]=43;await Assert.ThrowsAsync<InvalidDataException>(()=>Reviews.Apply(registry,api,101,(_,_)=>Task.FromResult(bytes)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortApprovalVerifiesOnlyTheBoundPackage(bool reviewRequest)
    {
        var bytes = Package();
        var entry = await Entry(bytes);
        Json.Write(registry.PathFor($"registry/plugins/{Id}.json"), Record(entry));
        var hash = Json.Text(entry, "sha256");
        var issue = SubmissionIssue();
        if (reviewRequest)
        {
            issue["title"] = "[Review] Toolbox";
            issue["labels"] = new JsonArray(new JsonObject { ["name"] = "review-request" });
            issue["body"] = $"### 插件 ID / Plugin ID\n\n{Id}\n\n### 插件版本 / Version\n\n1.0.0\n\n### 发布包 SHA-256 / SHA-256\n\n{hash}";
        }
        var command = ReviewComment(entry, 102, "verify");
        command["body"] = "/ap";
        var target = ReviewComment(entry, 101, "verify");
        target["body"] = $"验证目标：`{Id}@g1:1.0.0 sha256:{hash}`\n\n请维护者检查此版本后在本 Issue 发送 `/ap`。";
        target["user"] = Json.Parse("""{"login":"github-actions[bot]","id":41898282,"type":"Bot"}""");
        var comments = new JsonArray(target, command);
        using var api = new GitHub(null, new Reply(request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/example/registry/issues/comments/102" => command,
            "/repos/example/registry/issues/1/comments" => comments,
            _ => issue
        }));
        var history = await Reviews.Apply(registry, api, 102, (_, _) => Task.FromResult(bytes));
        Assert.Equal(hash, Json.Text(history, "sha256"));
        Assert.Equal("verified", Json.Text(history["events"]![0]!, "status"));

        var ambiguous = target.DeepClone();
        ambiguous["id"] = 100;
        ambiguous["body"] = Json.Text(target, "body").Replace("@g1:1.0.0", "@g1:2.0.0");
        comments.Add(ambiguous);
        await Assert.ThrowsAsync<InvalidDataException>(() => Reviews.Apply(registry, api, 102));
        comments.Remove(ambiguous);

        if (reviewRequest)
        {
            var originalBody = Json.Text(issue, "body");
            issue["body"] = originalBody.Replace(hash, new string('a', 64));
            await Assert.ThrowsAsync<InvalidDataException>(() => Reviews.Apply(registry, api, 102));
            issue["body"] = originalBody;
        }
        target["id"] = 103;
        await Assert.ThrowsAsync<InvalidDataException>(() => Reviews.Apply(registry, api, 102));
        target["id"] = 101;
        target["user"]!["id"] = 42;
        await Assert.ThrowsAsync<InvalidDataException>(() => Reviews.Apply(registry, api, 102));
    }

    [Fact] public async Task DelayedVerifyCannotOverwriteNewerRevocation()
    {
        var bytes=Package();var entry=await Entry(bytes);Json.Write(registry.PathFor($"registry/plugins/{Id}.json"),Record(entry));
        var comment=Json.Parse($$"""{"id":101,"body":"/verify {{Id}}@g1:1.0.0 sha256:{{Json.Text(entry,"sha256")}}","user":{"login":"maintainer","id":42},"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","issue_url":"https://api.github.com/repos/example/registry/issues/1"}""");
        var revoke=comment.DeepClone();revoke["id"]=102;revoke["body"]=Json.Text(comment,"body").Replace("/verify ","/revoke-review ")+" new issue found";
        var issue=Json.Parse("""{"number":1,"title":"[Plugin] Test","body":"### 插件仓库 / Repository\n\nhttps://github.com/example/toolbox\n"}""");
        using var api=new GitHub(null,new Reply(request=>request.RequestUri!.AbsolutePath.EndsWith("comments/101") ? comment : request.RequestUri.AbsolutePath.EndsWith("/comments") ? new JsonArray(comment.DeepClone(),revoke.DeepClone()) : issue));
        var downloads=0;
        await Assert.ThrowsAsync<InvalidDataException>(()=>Reviews.Apply(registry,api,101,(_,_)=>{downloads++;return Task.FromResult(bytes);}));
        Assert.Equal(0,downloads);Assert.Null(Reviews.Read(registry,entry));
    }
    [Fact] public void ReviewCommandsCannotOmitHashOrTargetAnotherGenerationSyntax()
    {
        Assert.Throws<InvalidDataException>(()=>Reviews.Parse("/verify plugin@1.0.0"));
        var command=Reviews.Parse($"/revoke-review {Id}@g1:1.0.0 sha256:{new string('a',64)} reason");Assert.Equal("revoke-review",command.Action);
    }
    [Fact] public async Task RevocationDoesNotDependOnAssetStillBeingAvailable()
    {
        var entry=await Entry(Package());Json.Write(registry.PathFor($"registry/plugins/{Id}.json"),Record(entry));
        Json.Write(registry.PathFor(Reviews.Relative(entry)),History(entry,"verified"));
        var comment=ReviewComment(entry,101,"revoke-review");
        using var api=ReviewApi(comment);
        var history=await Reviews.Apply(registry,api,101,(_,_)=>throw new HttpRequestException("Asset deleted"));
        Assert.Equal(2,history["events"]!.AsArray().Count);
        Assert.Equal("revoked",Json.Text(history["events"]![1]!,"status"));
    }
    [Fact] public async Task OldCommandFromAnotherIssueCannotReplayNewerStoredReview()
    {
        var entry=await Entry(Package());Json.Write(registry.PathFor($"registry/plugins/{Id}.json"),Record(entry));
        var history=History(entry,"revoked");history["events"]![0]!["commentId"]=102;
        history["events"]![0]!["issueNumber"]=2;Json.Write(registry.PathFor(Reviews.Relative(entry)),history);
        using var api=ReviewApi(ReviewComment(entry,101,"verify"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>Reviews.Apply(registry,api,101,(_,_)=>throw new Exception("must not download")));
        Assert.Equal("revoked",Json.Text(Reviews.Read(registry,entry)!["events"]![0]!,"status"));
    }
    [Fact] public async Task RetryRebasesExistingReviewPullRequest()
    {
        Publication.Git(root,"init");Publication.Git(root,"config","user.name","Registry Test");Publication.Git(root,"config","user.email","test@example.invalid");
        Publication.Git(root,"add",".");Publication.Git(root,"commit","-m","baseline");
        var head=Publication.Git(root,"rev-parse","HEAD").Trim();
        var operation=Json.Parse("""{"kind":"review","commentId":101,"path":"reviews/io.github.example.toolbox/g1/1.0.0.json","issues":[1]}""");
        Json.Write(registry.PathFor(".registry-operation.json"),operation);
        Json.Write(registry.PathFor("public/v1/index.json"),new JsonObject());
        var updated=false;var autoMerge=false;
        var pr=MergedPr(operation,7);pr["merged_at"]=null;pr["state"]="open";pr["node_id"]="PR_test";
        using var api=new GitHub(null,new Reply(request=>
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("/git/ref/heads/main")) return new JsonObject { ["object"]=new JsonObject { ["sha"]=head } };
            if(path.EndsWith("/git/trees")) return Json.Parse("""{"sha":"new-tree"}""");
            if(path.EndsWith("/git/commits") && request.Method==HttpMethod.Post) return Json.Parse("""{"sha":"new-commit"}""");
            if(path.EndsWith("/git/refs") && request.Method==HttpMethod.Post) throw new HttpRequestException("Exists",null,HttpStatusCode.UnprocessableEntity);
            if(path.EndsWith("/git/ref/heads/registry-bot/review/101")) return Json.Parse("""{"object":{"sha":"previous-commit"}}""");
            if(path.Contains("/git/commits/")) return Json.Parse("""{"tree":{"sha":"old-tree"}}""");
            if(path.EndsWith("/git/refs/heads/registry-bot/review/101") && request.Method==HttpMethod.Patch)
            {
                var body=Json.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal("new-commit",Json.Text(body,"sha"));Assert.True(body["force"]!.GetValue<bool>());updated=true;return new JsonObject();
            }
            if(path.EndsWith("/pulls") && request.Method==HttpMethod.Get) return new JsonArray(pr.DeepClone());
            if(path.EndsWith("/pulls/7") && request.Method==HttpMethod.Patch) return pr;
            if(path=="/graphql") {autoMerge=true;return new JsonObject();}
            throw new Exception("Unexpected request: "+request.Method+" "+path);
        }));
        await Publication.Publish(registry,api);Assert.True(updated);Assert.True(autoMerge);
        pr["user"]!["id"]=56;updated=false;autoMerge=false;
        await Assert.ThrowsAsync<InvalidDataException>(()=>Publication.Publish(registry,api));
        Assert.False(updated);Assert.False(autoMerge);
    }
    [Fact] public async Task ReconcileRequiresLandedReviewAndHandlesFullHistoryPages()
    {
        var entry=await Entry(Package());Json.Write(registry.PathFor($"registry/plugins/{Id}.json"),Record(entry));
        var operation=Json.Parse($$"""{"kind":"review","commentId":100,"path":"{{Reviews.Relative(entry)}}","issues":[1]}""");
        var pr=MergedPr(operation,7);var posts=0;var pages=0;var closes=0;
        using var api=new GitHub(null,new Reply(request=>
        {
            if(request.RequestUri!.AbsolutePath.EndsWith("/pulls"))
            {
                pages++;return new JsonArray(Enumerable.Range(0,100).Select(_=>pr.DeepClone()).ToArray());
            }
            if(request.Method==HttpMethod.Post) {posts++;return new JsonObject();}
            return new JsonArray();
        }));
        await Publication.Reconcile(registry,api);Assert.Equal(0,posts);Assert.Equal(2,pages);
        Json.Write(registry.PathFor(Reviews.Relative(entry)),History(entry,"verified"));
        using var landedApi=new GitHub(null,new Reply(request=>
        {
            if(request.RequestUri!.AbsolutePath.EndsWith("/pulls"))
            {
                var spoofed=pr.DeepClone();spoofed["number"]=8;spoofed["user"]!["type"]="User";
                var pending=pr.DeepClone();pending["number"]=9;pending["merged_at"]=null;
                return new JsonArray(pr.DeepClone(),spoofed,pending);
            }
            if (request.Method==HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/issues/1")) return SubmissionIssue();
            if (request.Method==HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/labels")) return new JsonArray();
            if (request.Method==HttpMethod.Patch)
            {
                var body=Json.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal("closed",Json.Text(body,"state"));
                closes++;
                return new JsonObject();
            }
            if(request.Method==HttpMethod.Post) {posts++;return new JsonObject();}
            return new JsonArray();
        }));
        await Publication.Reconcile(registry,landedApi);Assert.Equal(1,posts);Assert.Equal(1,closes);
        using var repeatedApi=new GitHub(null,new Reply(request=>
        {
            if(request.RequestUri!.AbsolutePath.EndsWith("/pulls")) return new JsonArray(pr.DeepClone());
            if(request.Method==HttpMethod.Post) {posts++;return new JsonObject();}
            return Json.Parse("""[{"user":{"type":"Bot"},"body":"Already reported <!-- wonder-merged:7 -->"}]""");
        }));
        await Publication.Reconcile(registry,repeatedApi);Assert.Equal(1,posts);
    }

    [Fact]
    public async Task LandedIntakeKeepsIssueOpenAndSetsSeparateIntakeAndReviewLabels()
    {
        var entry = await Entry(Package());
        Json.Write(registry.PathFor($"registry/plugins/{Id}.json"), Record(entry));
        var pr = MergedPr(new JsonObject { ["kind"] = "sync", ["plugins"] = new JsonArray(Id), ["issues"] = new JsonArray(1L) }, 7, "registry-bot/sync/abc");
        var addedLabels = new List<string>();
        var postedBodies = new List<string>();
        using var api = new GitHub(null, new Reply(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/pulls")) return new JsonArray(pr.DeepClone());
            if (path.EndsWith("/issues/1"))
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return SubmissionIssue();
            }
            if (path.EndsWith("/labels"))
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                var body = Json.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                addedLabels.Add(body["labels"]!.AsArray().Single()!.GetValue<string>());
                return new JsonArray();
            }
            if (path.EndsWith("/comments"))
            {
                if (request.Method == HttpMethod.Post)
                    postedBodies.Add(Json.Text(Json.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()), "body"));
                return new JsonArray();
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }));
        await Publication.Reconcile(registry, api);
        Assert.Equal(new[] { IssueLabels.Listed, IssueLabels.PendingReview }, addedLabels);
        Assert.Contains(postedBodies, body => body.Contains("sha256:" + Json.Text(entry, "sha256")) && body.Contains("/ap"));
    }
    private static JsonNode ReviewComment(JsonNode entry,long id,string action) => Json.Parse($$"""{"id":{{id}},"body":"/{{action}} {{Id}}@g1:1.0.0 sha256:{{Json.Text(entry,"sha256")}} reason","user":{"login":"maintainer","id":42},"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","issue_url":"https://api.github.com/repos/example/registry/issues/1"}""");
    private static GitHub ReviewApi(JsonNode comment) => new(null,new Reply(request=>request.RequestUri!.AbsolutePath.EndsWith("comments/"+Json.Number(comment,"id")) ? comment : request.RequestUri.AbsolutePath.EndsWith("/comments") ? new JsonArray(comment.DeepClone()) : SubmissionIssue()));
    private static JsonNode SubmissionIssue() => Json.Parse("""{"number":1,"title":"[Plugin] Test","state":"open","labels":[{"name":"plugin-submission"}],"body":"### 插件仓库 / Repository\n\nhttps://github.com/example/toolbox\n"}""");
    private static JsonNode MergedPr(JsonNode operation,long number,string? branch = null) => new JsonObject
    {
        ["number"]=number,["merged_at"]="2026-01-01T00:00:00Z",["html_url"]=$"https://github.com/example/registry/pull/{number}",
        ["body"]="<!-- wonder-operation:"+operation.ToJsonString()+" -->",["user"]=Json.Parse("""{"login":"test[bot]","id":55,"type":"Bot"}"""),
        ["head"]=new JsonObject { ["ref"]=branch ?? "registry-bot/review/"+Json.Number(operation,"commentId"),["repo"]=new JsonObject { ["full_name"]="example/registry" } },
        ["base"]=Json.Parse("""{"ref":"main","repo":{"full_name":"example/registry"}}""")
    };
    public void Dispose()
    {
        foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) File.SetAttributes(file,FileAttributes.Normal);
        Directory.Delete(root,true);
    }
    private sealed class Reply(Func<HttpRequestMessage,JsonNode> respond):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(respond(request).ToJsonString())}); }
}
public sealed class PackageProbe:IWonderPlugin { public static bool Called; public PackageProbe(){Called=true;}public void Activate(IPluginContext context){Called=true;}public void Dispose(){} }
