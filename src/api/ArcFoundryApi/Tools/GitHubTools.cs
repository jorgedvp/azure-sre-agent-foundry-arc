using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ArcFoundryApi.Tools;

/// <summary>
/// Minimal GitHub REST wrapper for the Foundry agent to open an Issue documenting a finding.
/// Every issue created by this POC is tagged with the "foundry-poc" label so it is never
/// confused with the Issues/PRs created by the production Azure SRE Agent in the same repo.
/// </summary>
public class GitHubTools(HttpClient httpClient, IConfiguration config)
{
    private readonly string _owner = config["GitHub:Owner"] ?? "jorgedvp";
    private readonly string _repo = config["GitHub:Repo"] ?? "azure-sre-agent-customer-demo";
    private readonly string _token = config["GitHub:Token"] ?? Environment.GetEnvironmentVariable("GITHUB_PAT") ?? "";

    public async Task<object> CreateIssueAsync(string title, string body)
    {
        if (string.IsNullOrEmpty(_token))
            return new { error = "GitHub token not configured. Set GitHub:Token in configuration or GITHUB_PAT env var." };

        var url = $"https://api.github.com/repos/{_owner}/{_repo}/issues";
        var finalTitle = title.StartsWith("[Foundry POC]", StringComparison.OrdinalIgnoreCase) ? title : $"[Foundry POC] {title}";
        var payload = new { title = finalTitle, body, labels = new[] { "foundry-poc" } };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("azure-sre-agent-foundry-arc-poc", "1.0"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var resp = await httpClient.SendAsync(req);
        var respBody = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return new { error = $"GitHub issue creation failed: {resp.StatusCode} - {respBody}" };

        var doc = JsonDocument.Parse(respBody);
        return new
        {
            number = doc.RootElement.GetProperty("number").GetInt32(),
            url = doc.RootElement.GetProperty("html_url").GetString()
        };
    }
}
