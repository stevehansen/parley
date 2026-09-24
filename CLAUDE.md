# CLAUDE.md

Parley: pub/sub topics for AI coding agents, with push delivery through Claude Code channels, a web UI, and a per-user hub service. README.md is the user-facing picture; docs/agents.md is the contract agents are given.

## Layout

- `src/Parley/Hub/CollabHub.cs` — all state (sessions, topics, messages, cursors), retention. Subscriptions are persisted *as cursors*: every subscription has one, unsubscribe removes it.
- `src/Parley/Hub/HubStore.cs` — files: `messages.jsonl` (append + fsync per message, compacted), `state.json` (topics/cursors), one-time TerminalHost import.
- `src/Parley/Hub/HubServer.cs` — `parley serve`: Kestrel, REST, SSE `/api/events`, `/mcp`, web UI at `/`. Access gate: loopback trusted (with a loopback Host, anti DNS-rebinding), anyone else needs an allowed network + device token; JSON-only mutations (anti CSRF).
- `src/Parley/Sharing/` — cross-device (design: docs/design/cross-device.md). `DeviceRegistry`: pairing codes, device tokens (hashed, `sharing.json`), `allowFrom`. `HubEndpoints`: Kestrel endpoints, reloaded live as sharing/addresses change. `JoinedHub`: `remote.json` on a device using another machine's hub.
- `src/Parley/Mcp/` — tool surface (`CollabTools`), agent instructions (`AgentInstructions`, keep in step with docs/agents.md), Streamable HTTP endpoint.
- `src/Parley/Shim/McpShim.cs` — `parley mcp`: stdio MCP server per session; forwards tools to the hub with `X-Session`, holds `/api/events?session=` open, emits `notifications/claude/channel`. Auto-starts a hub.
- `src/Parley/Web/index.html` — the whole web UI (embedded resource, no external assets).
- `src/Parley/Update/` — NuGet update check, `parley update` (moves in-use tool files aside on Windows so running shims don't block it, see `ToolFiles`), service registration (schtasks / launchd / systemd user).
- `src/Parley/Cli/Commands.cs` — `install` / `uninstall` / `status`; `SharingCommands.cs` — `devices` / `join` / `leave`.

## Rules

- `parley mcp` owns stdout for JSON-RPC: never write anything else there. Diagnostics go through `Log` (stderr).
- Never negotiate MCP `2026-07-28` (see `Protocol`): channel servers on that revision are not registered by Claude Code.
- Pushed messages must not advance read cursors — a client without channels enabled drops them silently.
- Never auto-apply updates: the user decides when the hub restarts on a new version.
- `parley update` must not kill shims: running sessions keep their (old) shim, which reconnects to the new hub.
- Web UI edits need a rebuild (the page is embedded).
- Never bind a wildcard address: a second hub could still claim 127.0.0.1 next to it (Windows), and it exposes every network. Bind loopback plus specific overlay addresses (`HubEndpoints`).
- Anything acting on the hub *process* (stop, kill by pid) uses `ParleyConfig.LocalHubUrl`, never `HubUrl`, which may point at another machine.
- A joined hub's token goes only to that hub (`ParleyConfig.Hub()`); stopping/updating the hub and managing devices stay loopback-only.

## Test / release

```bash
dotnet test
```

Release: bump `<Version>` in `src/Parley/Parley.csproj`, push tag `v<version>` → `.github/workflows/release.yml` publishes HC.Parley to NuGet via trusted publishing (`vars.NUGET_USER`).
