// SPDX-License-Identifier: MIT
using System.Net;
using System.Text.Json.Nodes;

namespace Wonder.PluginRegistry.Tests;

public sealed class IntakeQueueTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectSubmissionWithoutLiveQueueLabelsHasNoSideEffects(bool hasSubmissionLabel)
    {
        var root = Directory.CreateTempSubdirectory("wonder-intake-test-");
        try
        {
            var registry = new Registry(root.FullName);
            Json.Write(registry.PathFor("repository.json"), Json.Parse("""{"sourceUrl":"https://github.com/example/registry"}"""));
            Json.Write(registry.PathFor("catalog/plugins.json"), new JsonArray());
            await registry.Index(true);

            var live = Json.Parse("""{"number":42,"title":"[Plugin] Toolbox","state":"open","body":"### 插件仓库 / Repository\n\nhttps://github.com/example/toolbox\n","labels":[]}""");
            if (hasSubmissionLabel)
                live["labels"]!.AsArray().Add(new JsonObject { ["name"] = "plugin-submission" });
            var submission = Json.Parse("""{"action":"opened","issue":{"number":42,"title":"[Plugin] Toolbox","labels":[{"name":"plugin-submission"},{"name":"queued-for-intake"}]}}""");

            using var handler = new IntakeApi(live);
            using var api = new GitHub(null, handler);
            await Intake.Collect(registry, api, submission);

            Assert.Equal(1, handler.LiveIssueReads);
            Assert.Equal(0, handler.PublisherReads);
            Assert.Equal(0, handler.WriteRequests);
            Assert.False(File.Exists(registry.PathFor(".registry-operation.json")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class IntakeApi(JsonNode live) : HttpMessageHandler
    {
        public int LiveIssueReads { get; private set; }
        public int PublisherReads { get; private set; }
        public int WriteRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                WriteRequests++;
                return Reply("[]");
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path == "/repos/example/registry/issues/42")
            {
                LiveIssueReads++;
                return Reply(live.ToJsonString());
            }
            if (path.StartsWith("/repos/example/toolbox", StringComparison.Ordinal))
            {
                PublisherReads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Reply(path == "/search/repositories" ? """{"total_count":0,"items":[]}""" : "[]");
        }

        private static Task<HttpResponseMessage> Reply(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
