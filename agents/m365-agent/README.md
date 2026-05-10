# OPC UA Expert — Microsoft Teams Custom Engine Agent

This folder contains the **Microsoft Teams app package** for the OPC UA Knowledge Base custom engine agent. The agent is a bot that answers natural-language questions about the OPC UA specification, NodeSets, and information models, grounded in the knowledge base built by the pipeline in this repo.

## Contents

```
agents/m365-agent/
├── appPackage/
│   ├── manifest.template.json   # Teams manifest v1.21 with ${{...}} placeholders
│   ├── color.png                # 192x192 color icon (RGBA)
│   └── outline.png              # 32x32 outline icon (RGBA)
├── generate_icons.py            # Regenerates the icons (uses Pillow)
└── README.md                    # This file
```

The bot itself (web app implementing the Bot Framework / Microsoft 365 Agents SDK protocol) is hosted as an Azure Container App. This folder only contains the **deployment artifact** that is uploaded to the Microsoft Teams admin center / Teams app catalog.

## Manifest substitution

`manifest.template.json` contains two placeholders that are replaced at deploy time:

| Placeholder       | Replaced with                                                                 |
|-------------------|-------------------------------------------------------------------------------|
| `${{BOT_ID}}`     | The Entra ID app registration **appId** (GUID) used by the bot for AAD auth. |
| `${{TEAMS_APP_ID}}` | A stable GUID identifying the Teams app itself (per-tenant or per-env).     |

Both values are emitted by the install script and substituted into a copy of the template. The substituted `manifest.json` is then zipped together with `color.png` and `outline.png` into the upload-ready `appPackage.zip`.

## Install (automated, end-to-end)

The recommended path is to run the install script from the repo root:

```powershell
# Windows / PowerShell
./scripts/install-agent.ps1 -Subscription <sub-id> -ResourceGroup rg-opcua-kb -Env <env>
```

```bash
# Linux / macOS / WSL
./scripts/install-agent.sh -s <sub-id> -g rg-opcua-kb -e <env>
```

The script:

1. Creates / reuses the Entra ID app registration (`BOT_ID`).
2. Provisions the Azure Bot Service resource bound to that appId.
3. Configures the Teams channel and points the messaging endpoint at `https://opcua-kb-agent.<env>.azurecontainerapps.io/api/messages`.
4. Substitutes `${{BOT_ID}}` and `${{TEAMS_APP_ID}}` in `manifest.template.json`.
5. Zips `manifest.json + color.png + outline.png` into `appPackage.zip`.
6. (Optional) Uploads the package to the Teams app catalog via Microsoft Graph.

## Local development

For local iteration on the bot logic, you don't need Teams at all — use the **Microsoft 365 Agents Playground** (`teamsapptester`):

```bash
# 1. Run the agent web app locally (listens on http://localhost:3978)
dotnet run --project src/OpcUaKb.Agent

# 2. In a second terminal, start the playground
npx teamsapptester
```

The playground emulates the Teams chat surface, sends Activity payloads to `http://localhost:3978/api/messages`, and renders the agent's responses — no Azure Bot Service, no tunnel, no manifest upload required.

For testing inside Teams against a local bot, use a tunnel (e.g. `devtunnel host -p 3978 --allow-anonymous`) and point the Bot Service messaging endpoint at the tunnel URL.

## Sideload (manual fallback)

If you can't or don't want to run the install script, you can sideload the package manually:

1. Build `appPackage.zip` yourself:
   - Copy `manifest.template.json` to `manifest.json`.
   - Replace `${{BOT_ID}}` with your bot's Entra appId (GUID).
   - Replace `${{TEAMS_APP_ID}}` with a freshly generated GUID (e.g. `[guid]::NewGuid()` in PowerShell).
   - Zip `manifest.json`, `color.png`, and `outline.png` at the **root** of the zip (not inside a subfolder).
2. In Microsoft Teams: **Apps → Manage your apps → Upload an app → Upload a custom app**, then select `appPackage.zip`.
3. Open a chat with the app, or add it to a team / group chat.

> Sideloading must be enabled by your Teams admin (App setup policies → "Upload custom apps"). Without it, ask your tenant admin to publish the package to the org-wide app catalog instead.

## References

- [Deploy a bot to Azure Bot Service manually](https://learn.microsoft.com/microsoft-365/agents-sdk/deploy-azure-bot-service-manually) — provisions the Bot Service + Teams channel resources used by `install-agent.*`.
- [Custom engine agents for Microsoft 365 Copilot — overview](https://learn.microsoft.com/microsoft-365-copilot/extensibility/overview-custom-engine-agent) — defines the `copilotAgents.customEngineAgents` manifest contract.
- [Teams app manifest schema reference](https://learn.microsoft.com/microsoftteams/platform/resources/schema/manifest-schema) — full field reference for v1.21.
- [Microsoft 365 Agents SDK quickstart sample manifest](https://github.com/microsoft/Agents/blob/main/samples/dotnet/quickstart/appManifest) — upstream reference this scaffolding is modeled on.
- [Microsoft 365 Agents Playground (`teamsapptester`)](https://learn.microsoft.com/microsoftteams/platform/concepts/build-and-test/microsoft-365-agents-playground) — local emulator used during development.
