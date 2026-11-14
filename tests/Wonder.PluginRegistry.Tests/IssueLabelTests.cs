// SPDX-License-Identifier: MIT
using System.Net;
using System.Text.Json.Nodes;

namespace Wonder.PluginRegistry.Tests;

public sealed class IssueLabelTests
{
    [Fact]
    public async Task TransitionReplacesOnlyManagedLabelsAndCanBeRepeated()
    {
        using var issue = new IssueHandler();
        using var api = new GitHub(null, issue);

        await IssueLabels.Set(api, "example/registry", 42, "listed");
        Assert.Equal(0, issue.WriteRequests);

        issue.Labels.Add("plugin-submission");
        await IssueLabels.Set(api, "example/registry", 42, "listed");
        await IssueLabels.Set(api, "example/registry", 42, "pending-review", onlyIfReviewUnset: true);

        Assert.Equal(new[] { "bug", "listed", "plugin-submission", "plugin-yank", "review-request", "review-revoked" }, issue.Labels.Order());
        Assert.Equal(4, issue.WriteRequests);

        await IssueLabels.Set(api, "example/registry", 42, "review-pending-merge");
        await IssueLabels.Set(api, "example/registry", 42, "review-pending-merge");

        Assert.Equal(new[] { "bug", "listed", "plugin-submission", "plugin-yank", "review-pending-merge", "review-request" }, issue.Labels.Order());
        Assert.Equal(6, issue.WriteRequests);
    }

    private sealed class IssueHandler : HttpMessageHandler
    {
        private const string IssuePath = "/repos/example/registry/issues/42";
        public HashSet<string> Labels { get; } = ["review-request", "queued-for-intake", "pending-validation", "listed-unreviewed", "review-revoked", "bug", "plugin-yank"];
        public int WriteRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == IssuePath)
                return Reply(new JsonObject { ["number"] = 42, ["title"] = "[Plugin] Plugin release", ["labels"] = LabelObjects() });

            WriteRequests++;
            if (request.Method == HttpMethod.Post && path == IssuePath + "/labels")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                Labels.UnionWith(body["labels"]!.AsArray().Select(label => label!.GetValue<string>()));
                return Reply(LabelObjects());
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith(IssuePath + "/labels/", StringComparison.Ordinal))
            {
                Assert.True(Labels.Remove(Uri.UnescapeDataString(path[(IssuePath.Length + "/labels/".Length)..])));
                return Reply(LabelObjects());
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}");
        }

        private JsonArray LabelObjects() => new(Labels.Select(label => (JsonNode)new JsonObject { ["name"] = label }).ToArray());

        private static HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
    }
}
