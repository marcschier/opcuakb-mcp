# OpcUaKb.HostedAgent

Foundry **Hosted Agent** for the OPC UA Knowledge Base, implemented with the
**Agent Framework + Foundry Toolbox (server-side tools)** pattern.

This project replaces the legacy Bot Framework custom engine agent that
previously lived at `src/OpcUaKb.Agent/`. The old project has been removed
(see the migration commit) and is no longer part of the solution.

## How it works

`GetToolboxToolsAsync(toolboxName)` fetches the tool definitions from a
Foundry Toolbox (declared as a `resources:` entry in `agent.manifest.yaml`)
and passes them to `AsAIAgent(..., tools: ...)` as **server-side tools**. At
runtime Foundry invokes those tools on the agent's behalf via the Responses
API — the container only needs the control-plane call to fetch definitions
and never brokers MCP connections itself. As a result this agent has ~10
lines of business logic — no manual history hydration, no manual tool
dispatch loop.

The toolbox `opcua-kb-tools` wraps the existing `OpcUaKb.McpServer`
Container App (publicly exposed; rate-limited via `MCP_ANON_RATE_LIMIT=100`).

## Local development

```bash
# One-time: scaffold azd config for this agent
azd ai agent init

# Provision Foundry resources (creates the toolbox declared in agent.manifest.yaml)
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
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | `agent.manifest.yaml` → `azd provision` | Defaults to the existing `gpt-4o` deployment in `opcua-kb-foundry`. |
| `TOOLBOX_NAME` | `agent.manifest.yaml` | Must match the `name:` of the `kind: toolbox` resource (`opcua-kb-tools`). |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | auto-injected (hosted) | Optional for local. |

## References

- Spec: `~/.copilot/session-state/plan.md` — Hosted Agent migration plan.
- Upstream sample: `microsoft-foundry/foundry-samples`, path
  `samples/csharp/hosted-agents/agent-framework/foundry-toolbox-server-side/`.
