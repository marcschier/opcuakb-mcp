# OpcUaKb.HostedAgent

Foundry **Hosted Agent** for the OPC UA Knowledge Base, implemented with the
**Microsoft Agent Framework + direct MCP client** pattern. Exposes the agent
over the **Responses protocol**.

This project replaces the legacy Bot Framework custom engine agent that
previously lived at `src/OpcUaKb.Agent/`. The old project has been removed
and is no longer part of the solution.

## How it works

The agent connects directly to `OpcUaKb.McpServer` (a Container App in the
KB resource group) using `ModelContextProtocol.Client`:

```csharp
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri(mcpServerUrl),
    Name = "opcua-kb-agent",
});
var mcpClient = await McpClient.CreateAsync(transport);
var mcpTools = await mcpClient.ListToolsAsync();   // 11 McpClientTool

AIAgent agent = projectClient.AsAIAgent(
    model: deployment,
    instructions: ...,
    tools: [.. mcpTools]);
```

Each MCP tool becomes its own `McpClientTool` (which is an `AIFunction`) with
its own name + JSON schema, so the model sees `search_docs`, `list_specs`,
`get_spec_summary`, etc. as 11 individual tools and can call them by name.

> **Why not the Foundry Toolbox?** An earlier revision used
> `projectClient.GetToolboxToolsAsync(toolboxName)` which wraps an MCP-type
> toolbox into a single opaque `McpTool` with no per-tool schema exposed to
> the model. GPT-4o saw one anonymous "McpTool" function and never picked
> any of the 11 underlying tools. The direct MCP client pattern fixes this.

The whole tool-using loop (model call → tool_call → MCP roundtrip →
tool_result → final answer) is run locally in the container by the Agent
Framework hosting library. The agent has ~70 lines of business logic.

## Region constraint

Foundry Hosted Agents are in preview and only available in select regions
(as of writing: **westus3**, westus, norwayeast, francecentral, japaneast).
Sweden Central, East US, and many other regions return
`"Unsupported region for Foundry Hosted Agents"` on `azd deploy`.

Recommended setup: colocate KB stack (Search, Storage, MCP server) **and** the
Hosted Agent's Foundry project in the same Hosted-Agent-supported region —
typically **westus3** under a single `rg-opcua-kb` resource group. The agent
calls the MCP server in-region over HTTPS.

## Local development

```bash
# One-time: scaffold azd config for this agent (already done in this repo)
azd ai agent init

# Provision Foundry resources in a Hosted-Agent-capable region
azd env new opcua-kb
azd env set AZURE_LOCATION westus3
azd env set AZURE_RESOURCE_GROUP rg-opcua-kb
azd env set ENABLE_HOSTED_AGENTS true
azd env set MCP_SERVER_URL https://<your-mcp-server-fqdn>/
azd env set AZURE_TENANT_ID <your-tenant-id>
azd provision

# Run the agent locally
azd ai agent run

# Invoke it
azd ai agent invoke --local "What is Part 9?"
```

For local runs without azd, copy `.env.example` to `.env` and fill in the
values, then `dotnet run`.

## Cloud deploy

```bash
azd deploy
```

## Environment variables

| Variable | Source | Notes |
|---|---|---|
| `FOUNDRY_PROJECT_ENDPOINT` | auto-injected (hosted) / `.env` (local) | |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | `agent.manifest.yaml` → `azd provision` | Defaults to `gpt-4o`. |
| `MCP_SERVER_URL` | `azd env set MCP_SERVER_URL` / `.env` | Base URL of the `OpcUaKb.McpServer` Container App. Public ingress; the server is rate-limited and api-key gated. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | auto-injected (hosted) | Optional for local. |

## References

- Upstream pattern: `ModelContextProtocol.Client.McpClient.CreateAsync` with
  `HttpClientTransport` — see `ModelContextProtocol.Core` 1.2.0.
- Foundry hosting library: `Microsoft.Agents.AI.Foundry.Hosting` 1.3.0-preview.
- Region support: <https://learn.microsoft.com/azure/foundry/agents/concepts/limits-quotas-regions>.
