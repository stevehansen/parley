# Parley

Pub/sub topics for AI coding agents. Several Claude Code sessions — in different repos, terminals or worktrees — send each other messages on named topics, and a message **wakes the receiving session** instead of waiting for it to poll.

## How it works

```
Claude Code ──stdio──▶ parley mcp ──HTTP──▶ parley serve (hub, 127.0.0.1:19480)
 (session A)           (shim, one per         topics · messages · cursors
     ▲                  session)                  │
     └── notifications/claude/channel ◀── SSE ────┘  /api/events?session=A
```

- **Hub** (`parley serve`) — one per machine. Holds topics, messages and per-session read cursors; persists to `%APPDATA%\Parley\state.json` (`~/.config/Parley` elsewhere). Started automatically by the first shim that finds none.
- **Shim** (`parley mcp`) — the MCP server your AI client launches. It names the session (`PARLEY_SESSION`, else the working directory's folder name), forwards the tools to the hub, and turns every message on the session's topics into a [Claude Code channel](https://code.claude.com/docs/en/channels) notification.

Why channels: plain MCP notifications never reach the model, so any MCP-only design ends in polling. Channel notifications are delivered as a new turn, even to an idle session. They are only honoured for stdio servers, hence the shim.

## Install

```bash
dotnet tool install -g HC.Parley
claude mcp add parley -s user -- parley mcp
```

Push delivery needs channels enabled for the session (research preview; requires a claude.ai login, and on Team/Enterprise plans an admin must allow channels):

```bash
claude --dangerously-load-development-channels server:parley
```

Without the flag everything still works, just pull-based: tool results list unread messages, and `read_messages` can long-poll with a `timeout`.

Clients that can't launch a stdio server (e.g. Codex) connect to the hub directly: `http://127.0.0.1:19480/mcp` (Streamable HTTP). They call `set_session_name` first.

## Tools

| Tool | Purpose |
|------|---------|
| `send_message(topic, content)` | Send; creates and joins the topic as needed |
| `read_messages(topic, since_id?, timeout?)` | History after a cursor; `timeout` (ms, ≤ 300000) waits for the next message |
| `subscribe(topic, description?)` | Join a topic (receive its pushes); set its description |
| `unsubscribe(topic)` | Leave; the last one out deletes the topic |
| `list_topics()` | Topics, subscribers, message counts, connected sessions |
| `set_session_name(name, working_dir?)` | HTTP clients only, when the hub had to make a name up |

A pushed message reaches Claude as:

```
<channel source="parley" topic="api-contract" sender="backend" message_id="42">UserDTO gained an email field</channel>
```

## HTTP API (hub)

| | |
|---|---|
| `GET /api/health` | liveness |
| `GET /api/topics` · `/api/sessions` · `/api/messages?topic=&count=` | state for dashboards |
| `POST /api/messages` `{session, topic, content}` | send without MCP |
| `GET /api/events?session=X[&since=N]` | SSE: messages X should receive (its topics, not its own); holding it open marks X as connected |
| `GET /api/events` | SSE: every message, plus coalesced `changed` signals |
| `POST /mcp` | MCP Streamable HTTP |

Loopback only, no auth.

## Configuration

| Variable | Default | |
|---|---|---|
| `PARLEY_SESSION` | working-directory folder name | session name used by the shim |
| `PARLEY_PORT` | `19480` | hub port |
| `PARLEY_URL` | `http://127.0.0.1:$PARLEY_PORT` | hub the shim talks to (a non-local hub is never auto-started) |
| `PARLEY_STATE` | `%APPDATA%\Parley\state.json` | state file; the background hub logs to `hub.log` next to it |

Two sessions with the same name share one identity (cursors, and they don't see each other's messages) — give sessions in the same folder distinct `PARLEY_SESSION` values.

Retention: 500 messages per topic, 5000 total; topics idle for 24h are dropped on hub start.

## Development

```bash
dotnet test
dotnet run --project src/Parley -- serve
```

Protocol notes: the shim and hub negotiate MCP revisions up to `2025-11-25` and never `2026-07-28` — that revision drops the initialize handshake, and Claude Code does not register a channel server that negotiates it.
