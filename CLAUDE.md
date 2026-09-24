# CLAUDE.md

Parley: pub/sub topics for AI coding agents with push delivery through Claude Code channels. See README.md for the user-facing picture.

## Layout

- `src/Parley/Hub/CollabHub.cs` — all state (sessions, topics, messages, cursors) + JSON persistence. Subscriptions are persisted *as cursors*: every subscription has one, unsubscribe removes it.
- `src/Parley/Hub/HubServer.cs` — `parley serve`: Kestrel, REST, SSE `/api/events`, `/mcp`.
- `src/Parley/Mcp/` — tool surface (`CollabTools`) and the Streamable HTTP endpoint (identity: `X-Session` header, else `Mcp-Session-Id` issued at initialize).
- `src/Parley/Shim/McpShim.cs` — `parley mcp`: stdio MCP server per session; forwards tools to the hub with `X-Session`, holds `/api/events?session=` open, emits `notifications/claude/channel`. Auto-starts a hub.

## Rules

- `parley mcp` owns stdout for JSON-RPC: never write anything else there. Diagnostics go through `Log` (stderr).
- Never negotiate MCP `2026-07-28` (see `Protocol`): channel servers on that revision are not registered by Claude Code.
- Pushed messages must not advance read cursors — a client without channels enabled drops them silently.

## Test

```bash
dotnet test
```
