using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using ModelContextProtocol.Client;
using OpenAI;
using System.Text.Json;

public class MCPChat
{
    #region Private members
    private readonly IConfiguration _config;
    private readonly IChatClient _client;
    private readonly List<ChatMessage> _messageHistory = [];
    private DateTime _lastSentTime;
    private readonly IList<IMcpClient> _mcpClients = [];
    private readonly IList<McpClientTool> _tools = [];
    #endregion

    #region Constructors and initializers

    public MCPChat(IConfiguration config)
    {
        _config = config;
        IChatClient chatClient = CreateLocalClient();
        _client = new ChatClientBuilder(chatClient)
            .UseFunctionInvocation()
            .Build();
        var systemPrompt = _config["ChatClient:SystemPrompt"] ?? "You are a helpful assistant.";
        _messageHistory.Clear();
        _messageHistory.Add(new ChatMessage(ChatRole.System, systemPrompt));
        _lastSentTime = DateTime.MinValue;  // force reinit of mcp clients and tools on first message
        //_ = Task.Run(async () => await InitializeMCPClientsAndTools());        
    }

    private class McpServerConfigSection
    {
        public required string Name { get; set; }
        public required string Endpoint { get; set; }
        public required bool Enabled { get; set; }
    };

    private async Task InitializeMCPClientsAndTools()
    {
        var mcpServers = _config.GetSection("McpServer").Get<List<McpServerConfigSection>>();
        foreach (var server in mcpServers ?? [])
        {
            if (!server.Enabled) continue;
            try
            {
                Console.WriteLine($"Adding MCP server {server.Name}");
                _mcpClients.Add(await CreateClientAsync(server.Endpoint));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create MCP client for {server}: {ex.Message}");
            }
        }

        foreach (var mcpClient in _mcpClients)
        {
            var t = await GetTools(mcpClient);
            foreach (var tool in t) _tools.Add(tool);
        }
        //options.Tools.Add(AIFunctionFactory.Create(Reconnect, "Reconnect", "Reconnects to the MCP server and reinitializes the client."));
    }

    #endregion

    #region Chat client builders

    private IChatClient CreateAzureOpenAIClient()
    {
        string azureApiKey = _config["ChatClient:AzureOpenAI:ApiKey"] ?? "InvalidApiKey";
        string azureApiEndpoint = _config["ChatClient:AzureOpenAI:Endpoint"] ?? "InvalidEndpoint";
        string model = _config["ChatClient:AzureOpenAI:Model"] ?? "gpt-4.1";
        var azureOpenAIClient = new AzureOpenAIClient(new Uri(azureApiEndpoint), new AzureKeyCredential(azureApiKey));
        IChatClient chatClient = azureOpenAIClient.GetChatClient(model).AsIChatClient();
        return chatClient;
    }

    private IChatClient CreateOpenAIClient()
    {
        string openAIKey = _config["ChatClient:OpenAI:ApiKey"] ?? "InvalidApiKey";
        string model = _config["ChatClient:OpenAI:Model"] ?? "gpt-4";
        IChatClient chatClient = new OpenAIClient(openAIKey).GetChatClient(model).AsIChatClient();
        return chatClient;
    }

    private IChatClient CreateOllamaClient()
    {
        //var chatOptions = new ChatOptions();
        //chatOptions.AddOllamaOption(OllamaOption.Think, Think);
        string endpoint = _config["ChatClient:Ollama:Endpoint"] ?? "http://localhost:11434";
        string model = _config["ChatClient:Ollama:Model"] ?? "llama3.2";
        IChatClient chatClient = new OllamaApiClient(new Uri(endpoint), model);
        return chatClient;
    }

    private IChatClient CreateLocalClient()
    {
        return _config["ChatClient:Use"] switch
        {
            "AzureOpenAI" => CreateAzureOpenAIClient(),
            "OpenAI" => CreateOpenAIClient(),
            "Ollama" => CreateOllamaClient(),
            _ => throw new ArgumentException($"Unknown provider: {_config["ChatProvider"]}"),
        };
    }

    #endregion

    #region MCP Client helpers

    private static async Task<IMcpClient> CreateClientAsync(string url)
    {
        Console.WriteLine($"Creating HTTP MCP client, url: {url} ");
        var transportOptions = new SseClientTransportOptions
        {
            Endpoint = new Uri(url),
            ConnectionTimeout = TimeSpan.FromHours(24)
        };
        var transport = new SseClientTransport(transportOptions);
        IMcpClient mcpClient = await McpClientFactory.CreateAsync(transport);
        Console.WriteLine($"Client connected to server {mcpClient.ServerInfo.Name} {mcpClient.ServerInfo.Version}\n");
        return mcpClient;
    }

    private static async Task<IList<McpClientTool>> GetTools(IMcpClient mcpClient)
    {
        Console.WriteLine($"Tools available for {mcpClient.ServerInfo.Name}: ");
        IList<McpClientTool> tools = await mcpClient.ListToolsAsync();
        foreach (McpClientTool tool in tools) { Console.WriteLine($"{tool}"); }
        Console.WriteLine();
        return tools;
    }

    public async Task ReconnectMCPClients()
    {
        _mcpClients.Clear();
        _tools.Clear();
        await InitializeMCPClientsAndTools();
    }

    #endregion

    #region Public methods

    public async Task<String> Send(string message)
    {
        if ((DateTime.UtcNow - _lastSentTime).TotalMinutes > 2)
        {
            Console.WriteLine("Reinitializng MCP clients due to inactivity");
            await ReconnectMCPClients();
            _lastSentTime = DateTime.UtcNow;
        }
        _messageHistory.Add(new(ChatRole.User, message));
        List<ChatResponseUpdate> updates = [];
        ChatOptions options = new()
        {
            Tools = [.. _tools]
        };
        Console.WriteLine("=========================================================");
        Console.WriteLine(message);
        var chatResponse = await _client.GetResponseAsync(_messageHistory, options);
        //Console.WriteLine(chatResponse.Text);
        foreach (var m in chatResponse.Messages)
        {
            Console.WriteLine(m.GetString());
        }
        Console.WriteLine("=========================================================");
        _messageHistory.AddRange(chatResponse.Messages);
        return chatResponse.Text ?? string.Empty;
    }

    #endregion
}

#region Extension methods for logging
static class DictionaryExtensions
{
    public static string ToStringSingleLine<T, V>(this IDictionary<T, V>? d)
    {
        if (d == null) return "";
        return string.Join(", ", d.Select(a => $"{a.Key}: {a.Value}"));
    }
}

static class MessageExtensions
{
    public static string GetString(this ChatMessage m)
    {
        string s = "";
        if (!string.IsNullOrEmpty(m.Text))
        {
            // plain text message
            s = "-------------------- Plain text ----------------------------\n";
            s += m.Text;
        }
        else if (m.Role == ChatRole.Assistant)
        {
            // function call
            s = "-------------------- Function call -------------------------\n";
            foreach (var c in m.Contents)
            {
                if (c is FunctionCallContent functionCall)
                {
                    s += $"{functionCall.Name}({functionCall.Arguments.ToStringSingleLine()})\n";
                }
                else
                {
                    s += $"Content type: {c.GetType().Name}\n";
                }
            }
        }
        else if (m.Role == ChatRole.Tool)
        {
            // function result
            s = "-------------------- Function result -----------------------\n";
            foreach (var c in m.Contents)
            {
                if (c is FunctionResultContent functionResult)
                {
                    var document = JsonDocument.Parse(functionResult.Result?.ToString() ?? "{}");
                    s += $"{document.RootElement.GetProperty("content")[0].GetProperty("text").GetString()}\n";
                    //s += $"{functionResult.Result?.ToString()}\n";
                }
                else
                {
                    s += $"Content type: {c.GetType().Name}\n";
                }
            }

        }
        else if (m.Role == ChatRole.User && m.Contents.Count == 1 && m.Contents[0] is TextContent textContent)
        {
            // user text
            s = "-------------------- User text -------------------------\n";
            s += textContent.Text;
        }
        else if (m.Role == ChatRole.Assistant && m.Contents.Count >= 1 && m.Contents[0] is TextReasoningContent reasoningContent)
        {
            // assistant reasoning
            s = "-------------------- Assistant reasoning -------------------------\n";
            s += reasoningContent.Text;
        }
        else
        {
            s = "-------------------- Something -----------------------------\n";
            s += $"{m.Role} with {m.Contents.Count} contents.";
        }

        // model thoughts
        foreach (var toughts in m.Contents.OfType<TextReasoningContent>())
        {
            s = "-------------------- Thoughts -------------------------\n";
            s += toughts.Text + "\n";
        }
        // model response

        return s;
    }
}
#endregion
