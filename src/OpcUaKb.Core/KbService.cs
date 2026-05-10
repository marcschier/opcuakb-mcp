using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

// ═══════════════════════════════════════════════════════════════════════
// Knowledge Base Retrieve + GPT-4o Completion Service
// Calls the Azure AI Search KB retrieve API for grounding, then sends
// the grounded context to GPT-4o for answer synthesis.
// ═══════════════════════════════════════════════════════════════════════

public sealed class KbService
{
    const string DefaultKbName = "opcua-kb";
    const string DefaultGptDeployment = "gpt-4o";
    const string SearchApiVersion = "2025-11-01-preview";
    const string AoaiApiVersion = "2024-10-21";

    readonly HttpClient _searchHttp;
    readonly HttpClient _aoaiHttp;
    readonly string _searchEndpoint;
    readonly string _aoaiEndpoint;
    readonly string _kbName;
    readonly string _gptDeployment;
    readonly TokenCredential? _credential;
    readonly string? _aoaiApiKey;
    readonly bool _available;

    static readonly string SystemPrompt = """
        You are an OPC UA expert assistant. You answer questions about OPC UA technology
        using the OPC UA reference specifications as your knowledge base.
        
        When answering:
        - Cite specification part numbers and sections (e.g., "Part 4, Section 5.6.2")
        - Be technically precise — use correct OPC UA terminology
        - If the grounding data doesn't cover the question, say so
        
        You have access to grounding data from reference.opcfoundation.org covering
        all OPC UA specification parts, companion specs, and NodeSet definitions.
        """;

    public KbService()
    {
        _searchEndpoint = Environment.GetEnvironmentVariable("SEARCH_ENDPOINT")
            ?? throw new InvalidOperationException("Set SEARCH_ENDPOINT environment variable");
        var searchApiKey = Environment.GetEnvironmentVariable("SEARCH_API_KEY")
            ?? throw new InvalidOperationException("Set SEARCH_API_KEY environment variable");

        _kbName = Environment.GetEnvironmentVariable("KB_NAME") ?? DefaultKbName;
        _gptDeployment = Environment.GetEnvironmentVariable("GPT_DEPLOYMENT") ?? DefaultGptDeployment;

        _searchHttp = new HttpClient();
        _searchHttp.DefaultRequestHeaders.Add("api-key", searchApiKey);

        _aoaiHttp = new HttpClient();

        // AOAI auth: API key takes precedence, then MI via DefaultAzureCredential
        _aoaiEndpoint = Environment.GetEnvironmentVariable("AOAI_ENDPOINT") ?? "";
        _aoaiApiKey = Environment.GetEnvironmentVariable("AOAI_API_KEY");

        if (!string.IsNullOrEmpty(_aoaiEndpoint))
        {
            if (!string.IsNullOrEmpty(_aoaiApiKey))
            {
                _aoaiHttp.DefaultRequestHeaders.Add("api-key", _aoaiApiKey);
                _credential = null;
            }
            else
            {
                _credential = new DefaultAzureCredential();
            }
            _available = true;
        }
        else
        {
            _available = false;
        }
    }

    /// <summary>Whether RAG is available (AOAI_ENDPOINT configured).</summary>
    public bool Available => _available;

    /// <summary>
    /// Retrieve grounding data from the KB, then generate a GPT-4o answer.
    /// </summary>
    public async Task<string> AskAsync(string query, string? context = null)
    {
        if (!_available)
            return "RAG not available — set AOAI_ENDPOINT environment variable to enable.";

        // Step 1: Retrieve grounding from KB
        var grounding = await RetrieveGroundingAsync(query, context);

        // Step 2: Generate answer with GPT-4o
        return await ChatCompletionAsync(query, grounding, context);
    }

    async Task<string?> RetrieveGroundingAsync(string query, string? context)
    {
        var messages = new List<object>();

        // Add conversation context if provided
        if (!string.IsNullOrWhiteSpace(context))
        {
            var lines = context.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var role = i % 2 == 0 ? "user" : "assistant";
                messages.Add(new
                {
                    role,
                    content = new[] { new { type = "text", text = lines[i].Trim() } }
                });
            }
        }

        messages.Add(new
            {
                role = "user",
                content = new[] { new { type = "text", text = query } }
            }
        );

        var body = new
        {
            messages,
            retrievalReasoningEffort = new { kind = "medium" }
        };

        var response = await _searchHttp.PostAsync(
            $"{_searchEndpoint}/knowledgebases/{_kbName}/retrieve?api-version={SearchApiVersion}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        if (!response.IsSuccessStatusCode) return null;

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return json?["response"]?[0]?["content"]?[0]?["text"]?.GetValue<string>();
    }

    async Task<string> ChatCompletionAsync(string query, string? grounding, string? context)
    {
        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt }
        };

        if (!string.IsNullOrWhiteSpace(grounding))
        {
            messages.Add(new
            {
                role = "system",
                content = $"Use the following OPC UA specification data to answer the user's question. " +
                          $"Cite [ref_id:N] references where applicable.\n\n{grounding}"
            });
        }

        // Add conversation context for follow-up questions
        if (!string.IsNullOrWhiteSpace(context))
        {
            var lines = context.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var role = i % 2 == 0 ? "user" : "assistant";
                messages.Add(new { role, content = lines[i].Trim() });
            }
        }

        messages.Add(new { role = "user", content = query });

        var body = new
        {
            messages,
            model = _gptDeployment,
            temperature = 0.3,
            max_tokens = 2000
        };

        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_aoaiEndpoint}/openai/deployments/{_gptDeployment}/chat/completions?api-version={AoaiApiVersion}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        // Set auth header — Bearer token for MI, api-key already set as default header
        if (_credential != null)
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
                default);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }

        var response = await _aoaiHttp.SendAsync(req);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? "(no response from GPT-4o)";
    }
}
