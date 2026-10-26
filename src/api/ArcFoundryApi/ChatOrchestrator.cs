using System.Text.Json;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using ArcFoundryApi.Tools;

namespace ArcFoundryApi;

public class ChatOrchestrator
{
    private readonly ChatClient _chatClient;
    private readonly ArcTools _arcTools;
    private readonly GitHubTools _githubTools;
    private readonly IConfiguration _config;

    private const string SystemPrompt = """
        You are the Arc SRE Copilot, a custom AI agent built on Microsoft Foundry for a client
        who needs more customization than the standard Azure SRE Agent product offers.

        You can inspect and diagnose servers connected via Azure Arc - the EXACT SAME inventory
        (vm-onprem-sql, vm-onprem-iis) used in the client's Azure SRE Agent demo. This proves
        that Azure Arc is a connectivity bridge independent of which "brain" reasons over it.

        Capabilities you have via tools:
        - list_arc_machines: see all Arc-connected servers and their status
        - get_machine_inventory: get OS/agent details for one machine
        - run_diagnostic: run a READ-ONLY diagnostic command on a machine. This explicitly
          INCLUDES SQL Server queries that only READ data (e.g. `SELECT ...`) via sqlcmd, IIS
          status checks (e.g. `Get-WebAppPoolState`, `Get-Service`), and OS-level checks
          (e.g. `systemctl status`, `df -h`, `Get-Process`). A SQL SELECT query is a read-only
          diagnostic, NOT a state-changing action - do not refuse it. The backend enforces an
          explicit deny-list (DROP/DELETE/INSERT/UPDATE/GRANT/REVOKE/KILL/service
          stop-start-restart/etc.) and will block and explain if you ever attempt one of those -
          you do not need to pre-emptively refuse read operations yourself.
        - query_log_analytics: query telemetry (if a workspace ID is provided)
        - create_github_issue: document a finding as a GitHub issue (always labeled [Foundry POC])

        You are expected to demonstrate real read-only diagnostics across all three layers of
        this environment when asked: the operating system (vm-onprem-sql Linux, vm-onprem-iis
        Windows), the SQL Server database (DemoDB, via sqlcmd SELECT queries), and IIS
        (Default Web Site / DefaultAppPool status on vm-onprem-iis). Always use the exact
        credentials and paths the operator gives you in the conversation.

        Be concise, cite the real evidence you gathered from tool calls, and be explicit that
        this is a proof-of-concept custom agent, complementary to (not a replacement for) the
        production Azure SRE Agent demo running on the same infrastructure.
        """;

    public ChatOrchestrator(IConfiguration config, Azure.Core.TokenCredential credential, ArcTools arcTools, GitHubTools githubTools)
    {
        _config = config;
        _arcTools = arcTools;
        _githubTools = githubTools;

        var endpoint = new Uri(config["Foundry:Endpoint"]!);
        var deployment = config["Foundry:Deployment"] ?? "gpt-4o-arc-agent";

        // This tenant enforces disableLocalAuth on Cognitive Services accounts (same policy
        // we hit earlier with Key Vault public network access on the main Arc demo) - so this
        // POC authenticates with Microsoft Entra ID via the same managed identity used for Arc,
        // instead of an API key.
        var azureClient = new AzureOpenAIClient(endpoint, credential);
        _chatClient = azureClient.GetChatClient(deployment);
    }

    private static readonly ChatTool ListArcMachinesTool = ChatTool.CreateFunctionTool(
        "list_arc_machines",
        "List all Azure Arc-connected servers in the demo environment, with their connection status and OS.");

    private static readonly ChatTool GetMachineInventoryTool = ChatTool.CreateFunctionTool(
        "get_machine_inventory",
        "Get detailed inventory (OS, Arc agent version, whether SQL Server was auto-discovered, cloud metadata) for one named Arc machine.",
        BinaryData.FromString("""{"type":"object","properties":{"machineName":{"type":"string","description":"e.g. vm-onprem-sql or vm-onprem-iis"}},"required":["machineName"]}"""));

    private static readonly ChatTool RunDiagnosticTool = ChatTool.CreateFunctionTool(
        "run_diagnostic",
        "Run a READ-ONLY diagnostic command on an Arc-connected machine (e.g. check a service status, disk usage, running processes). State-changing commands are blocked by design in this proof of concept.",
        BinaryData.FromString("""{"type":"object","properties":{"machineName":{"type":"string"},"script":{"type":"string","description":"Shell (Linux) or PowerShell (Windows) read-only diagnostic command"}},"required":["machineName","script"]}"""));

    private static readonly ChatTool CreateGitHubIssueTool = ChatTool.CreateFunctionTool(
        "create_github_issue",
        "Create a GitHub issue documenting a diagnostic finding. Always labeled [Foundry POC] to distinguish from the production SRE Agent's own issues.",
        BinaryData.FromString("""{"type":"object","properties":{"title":{"type":"string"},"body":{"type":"string","description":"Markdown body with the evidence gathered"}},"required":["title","body"]}"""));

    public async Task<(string reply, List<object> toolCallLog)> SendAsync(List<ChatMessage> history, string userMessage)
    {
        var messages = new List<ChatMessage> { new SystemChatMessage(SystemPrompt) };
        messages.AddRange(history);
        messages.Add(new UserChatMessage(userMessage));

        var options = new ChatCompletionOptions
        {
            Tools = { ListArcMachinesTool, GetMachineInventoryTool, RunDiagnosticTool, CreateGitHubIssueTool }
        };

        var toolCallLog = new List<object>();

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var response = await _chatClient.CompleteChatAsync(messages, options);
            var completion = response.Value;

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                messages.Add(new AssistantChatMessage(completion));

                foreach (var toolCall in completion.ToolCalls)
                {
                    var resultJson = await ExecuteToolAsync(toolCall.FunctionName, toolCall.FunctionArguments.ToString());
                    toolCallLog.Add(new { tool = toolCall.FunctionName, arguments = toolCall.FunctionArguments.ToString(), result = resultJson });
                    messages.Add(new ToolChatMessage(toolCall.Id, resultJson));
                }
                continue;
            }

            var finalText = string.Join("", completion.Content.Select(c => c.Text));
            return (finalText, toolCallLog);
        }

        return ("The agent reached the maximum number of tool-call iterations without a final answer.", toolCallLog);
    }

    private async Task<string> ExecuteToolAsync(string name, string argsJson)
    {
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = args.RootElement;

        object result = name switch
        {
            "list_arc_machines" => await _arcTools.ListArcMachinesAsync(),
            "get_machine_inventory" => await _arcTools.GetMachineInventoryAsync(root.GetProperty("machineName").GetString()!),
            "run_diagnostic" => await _arcTools.RunDiagnosticAsync(
                root.GetProperty("machineName").GetString()!,
                root.GetProperty("script").GetString()!),
            "create_github_issue" => await _githubTools.CreateIssueAsync(
                root.GetProperty("title").GetString()!,
                root.GetProperty("body").GetString()!),
            _ => new { error = $"Unknown tool: {name}" }
        };

        return JsonSerializer.Serialize(result);
    }
}
