// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Wonder.PluginRegistry;

public sealed class GitHub : IDisposable
{
    private readonly HttpClient client;
    public GitHub(string? token, HttpMessageHandler? handler = null)
    {
        client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Wonder-PluginRegistry/1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    public async Task<JsonNode> Send(string method, string path, JsonNode? body = null)
    {
        if (!path.StartsWith('/') || path.StartsWith("//")) throw new InvalidDataException("Invalid API path");
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://api.github.com" + path);
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        if (content.Length > 8 * 1024 * 1024) throw new InvalidDataException("GitHub response too large");
        return content.Length == 0 ? new JsonObject() : Json.Parse(content);
    }
    public Task<JsonNode> Get(string path) => Send("GET", path);
    public async Task<IReadOnlyList<JsonNode>> Pages(string path, int maxPages = 10)
    {
        var result = new List<JsonNode>();
        for (var page = 1; page <= maxPages; page++)
        {
            var items = (await Get(path + (path.Contains('?') ? "&" : "?") + $"per_page=100&page={page}")).AsArray();
            result.AddRange(items.Select(x => x!.DeepClone()));
            if (items.Count < 100) return result;
        }
        throw new InvalidDataException("GitHub pagination limit reached; narrow the request");
    }
    public async Task<byte[]> Download(string url, long limit)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0) throw new InvalidDataException("Download requires HTTPS");
        // Assets are public. Do not send the API bearer token to asset hosts or redirects.
        using var downloader = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var response = await downloader.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https" || response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Invalid asset response");
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920]; int count;
        while ((count = await stream.ReadAsync(chunk)) != 0) { if (buffer.Length + count > limit) throw new InvalidDataException("Asset exceeds size limit"); buffer.Write(chunk, 0, count); }
        return buffer.ToArray();
    }
    public async Task Comment(string repository, long issue, string text)
    {
        await Send("POST", $"/repos/{Json.Repository(repository)}/issues/{issue}/comments", new JsonObject { ["body"] = text });
    }
    public async Task CommentOnce(string repository, long issue, string text)
    {
        var marker = "<!-- wonder-status:"+Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))+" -->";
        var comments = await Pages($"/repos/{Json.Repository(repository)}/issues/{issue}/comments");
        if (!comments.Any(x=>x["user"]?["type"]?.GetValue<string>() == "Bot" && Json.Text(x,"body").Contains(marker)))
            await Comment(repository,issue,text+"\n\n"+marker);
    }
    public void Dispose() => client.Dispose();
}
