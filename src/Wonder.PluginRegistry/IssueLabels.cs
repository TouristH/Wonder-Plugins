// SPDX-License-Identifier: MIT
using System.Net;
using System.Text.Json.Nodes;

namespace Wonder.PluginRegistry;

public static class IssueLabels
{
    public const string PluginSubmission = "plugin-submission";
    public const string ReviewRequest = "review-request";

    public const string Queued = "queued-for-intake";
    public const string PendingMerge = "pending-merge";
    public const string Listed = "listed";
    public const string IntakeFailed = "validation-failed";

    public const string PendingReview = "pending-review";
    public const string ReviewPendingMerge = "review-pending-merge";
    public const string Verified = "review-verified";
    public const string Revoked = "review-revoked";
    public const string ReviewNeedsFix = "review-needs-fix";

    private static readonly HashSet<string> IntakeStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        Queued, PendingMerge, Listed, IntakeFailed, "listed-unreviewed", "pending-validation", "validated"
    };

    private static readonly HashSet<string> ReviewStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        PendingReview, ReviewPendingMerge, Verified, Revoked, ReviewNeedsFix
    };

    public static bool HasLabel(JsonNode issue, string label) => LabelNames(issue).Contains(label, StringComparer.OrdinalIgnoreCase);

    public static bool IsSubmission(JsonNode issue) => IsTypedIssue(issue, "[Plugin]", PluginSubmission);

    public static bool IsReviewRequest(JsonNode issue) => IsTypedIssue(issue, "[Review]", ReviewRequest);

    private static bool IsTypedIssue(JsonNode issue, string prefix, string label) => issue["pull_request"] == null &&
        Json.Text(issue, "title").StartsWith(prefix, StringComparison.Ordinal) && HasLabel(issue, label);

    private static IEnumerable<string> LabelNames(JsonNode issue) =>
        issue["labels"] is JsonArray labels ? labels.Select(label => Json.Text(label!, "name")) : [];

    public static async Task Set(GitHub api, string repository, long issue, string status, bool onlyIfReviewUnset = false, bool keepQueued = false)
    {
        var statuses = status switch
        {
            Queued or PendingMerge or Listed or IntakeFailed => IntakeStatuses,
            PendingReview or ReviewPendingMerge or Verified or Revoked or ReviewNeedsFix => ReviewStatuses,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown registry Issue status")
        };
        var issuePath = $"/repos/{Json.Repository(repository)}/issues/{issue}";
        var live = await api.Get(issuePath);
        if (!IsSubmission(live) && (statuses == IntakeStatuses || !IsReviewRequest(live))) return;

        var labels = LabelNames(live).ToArray();
        if (onlyIfReviewUnset && statuses == ReviewStatuses && ReviewStatuses.Overlaps(labels)) return;
        var labelsPath = issuePath + "/labels";
        if (!labels.Contains(status, StringComparer.OrdinalIgnoreCase))
            await api.Send("POST", labelsPath, new JsonObject { ["labels"] = new JsonArray(status) });

        foreach (var stale in labels.Where(label => statuses.Contains(label) && !label.Equals(status, StringComparison.OrdinalIgnoreCase) &&
            (!keepQueued || !label.Equals(Queued, StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                await api.Send("DELETE", labelsPath + "/" + Uri.EscapeDataString(stale));
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                // A maintainer or another run may have already removed the label.
            }
        }
    }
}
