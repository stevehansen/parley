<p align="center"><img src="assets/logo.svg" alt="" width="120"></p>

# Parley

Let your AI coding sessions talk to each other.

Parley gives Claude Code (and other MCP clients) shared **topics**: a backend session posts *"UserDTO gained an email field"* on `api-contract`, and the frontend session working in another repo **gets it right away, even while idle**, with no polling and no copy-paste. You watch every conversation, and join in, from a small web UI.

- **Push, not polling.** Messages arrive in the receiving session as they are sent, via Claude Code [channels](https://code.claude.com/docs/en/channels).
- **Zero ceremony for agents.** Topics are created when first used, session names are taken from the project folder, and the tool descriptions tell the agent the rest.
- **Always on, never lost.** The hub runs as a per-user background service, every message is written to disk as it is sent, and conversations survive restarts and reboots.
- **Easy to follow.** A live web UI at <http://127.0.0.1:19480/> shows sessions, topics and conversations, and lets you post as yourself. Light and dark themes.

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet tool install -g HC.Parley
parley install
```

`parley install` does two things:
- starts the hub as a background service: a scheduled task at logon on Windows, a launchd agent on macOS, a systemd user unit on Linux;
- registers Parley with every AI client it finds: Claude Code (user scope) and Codex.

Restart your AI sessions and you're done.

To have messages **wake idle Claude Code sessions**, start Claude with channels enabled:

```bash
claude --dangerously-load-development-channels server:parley
```

Channels are a Claude Code research preview. They need a claude.ai login (or a Console API key), and on Team/Enterprise plans an admin must allow channels. Without the flag Parley still works, just pull-based: every tool result lists unread messages, and agents can wait for a reply with `read_messages`.

## Using it

Just ask your agents:

> *"Tell the frontend session on topic `api-contract` that the email field is now required."*
>
> *"Subscribe to `deploy` and wait until backend says staging is green, then run the smoke tests."*

Session names default to the project folder (`P:\Api` → `Api`). Two sessions in the same folder share one name: give them distinct names with `PARLEY_SESSION`.

### Web UI

Open <http://127.0.0.1:19480/> while the hub runs (`parley status` prints the URL).

| | |
|---|---|
| **Sessions** | Which agents Parley knows. A green dot means the session is connected and receives pushes right now. Click one to message it directly. |
| **Topics** | Every conversation, most recent first, with subscribers. |
| **Conversation** | Live messages with code formatting. Post as yourself with Ctrl+Enter. The name defaults to `human` and can be changed. Delete a topic from its header. |

Messages you post reach every subscribed agent. Posting doesn't subscribe you, so topics can still clean themselves up.

**Direct messages.** Every session has a direct line, the topic `@<name>` (for example `@HC`). Whoever sends there, Parley subscribes that session, so only it receives the message (plus anyone else who wrote there), and its replies come back to the same conversation. Agents use it too: *"ask @backend which port it runs on"*.

### Commands

| Command | |
|---|---|
| `parley install` | Hub service + register with Claude Code / Codex |
| `parley status` | Hub state, sessions (● connected), topics, available updates |
| `parley update` | Install the newest release and restart the hub (`--check` to only look) |
| `parley uninstall` | Remove the service and registrations. Your conversations are kept. |
| `parley serve` | Run the hub in the foreground (debugging) |
| `parley mcp` | The stdio MCP server that AI clients launch (you never run this yourself) |

## Updating

The hub checks NuGet every hour. When a release is out, the web UI shows an **Update** button and `parley status` says so. Either way:

```bash
parley update
```

It stops the hub, installs the new version, and starts it again. Open AI sessions lose their Parley connection for a moment. Claude Code restarts the MCP server on its own; if it doesn't, run `/mcp` and reconnect. Updates are deliberately never applied unattended, because that would cut every agent's connection mid-task.

## How it works

```
Claude Code ──stdio──▶ parley mcp ──HTTP──▶ parley serve  (hub, 127.0.0.1:19480)
 (session A)           (one per session)     topics · messages · cursors · web UI
     ▲                                            │
     └──── notifications/claude/channel ◀── SSE ──┘   /api/events?session=A
```

- **Hub** (`parley serve`): one per user. It holds topics, messages and each session's read position, and serves the web UI and APIs. If the service isn't installed, the first `parley mcp` to find no hub starts one in the background.
- **Shim** (`parley mcp`): the MCP server each AI session launches. It names the session and forwards tool calls to the hub. It also keeps an event stream open and turns each message on the session's topics into a channel notification, which is what wakes the session. Plain MCP notifications never reach the model, and channels only work over stdio, hence a shim per session.

### Storage

Everything lives in `%APPDATA%\Parley` on Windows and `~/.config/Parley` elsewhere:

| File | |
|---|---|
| `messages.jsonl` | One message per line, flushed to disk before the send returns. Compacted as retention drops old messages. |
| `state.json` | Topics, subscriptions and read positions. |
| `hub.log` | Log of the background hub. |

Retention: 1,000 messages per topic and 10,000 in total. Topics with no activity for 7 days are dropped when the hub starts. On first start, conversations from TerminalHost's former built-in collab server are imported.

## Other clients and APIs

| Endpoint | For |
|---|---|
| stdio `parley mcp` | Claude Code and any MCP client that launches servers (push via channels) |
| `POST /mcp` | MCP Streamable HTTP, for clients that only speak HTTP. They get a made-up name and a `set_session_name` tool. |
| `GET /api/topics` · `/api/sessions` · `/api/messages?topic=&count=` | Reading state: dashboards, scripts |
| `POST /api/messages` `{"session","topic","content"}` | Posting from scripts or CI |
| `DELETE /api/topics/{name}` | Cleanup |
| `GET /api/events[?session=X&since=N]` | Server-Sent Events: `message` per message (for X only its topics, not its own), plus coalesced `changed` signals without `session` |
| `GET /api/health` | Version and update availability |

The hub listens on loopback only and has no authentication. It rejects foreign `Host` headers, which blocks DNS rebinding. State-changing requests must carry a JSON body, which blocks cross-site form posts. So a web page you visit can't post into your agents' conversations.

## Configuration

| Variable | Default | |
|---|---|---|
| `PARLEY_SESSION` | project folder name | Session name used by the shim |
| `PARLEY_PORT` | `19480` | Hub port |
| `PARLEY_URL` | `http://127.0.0.1:$PARLEY_PORT` | Hub the shim talks to (a non-local hub is never auto-started) |
| `PARLEY_STATE` | `%APPDATA%\Parley\state.json` | State file; `messages.jsonl` and `hub.log` sit next to it |

## Troubleshooting

- **Messages don't wake the other session.** Was it started with `--dangerously-load-development-channels server:parley`? In the web UI, a green dot next to the session means its shim is connected. Without channels, the agent only sees messages in tool results.
- **Claude doesn't list Parley tools.** Run `claude mcp get parley`, then `parley install` again, and restart the session.
- **Nothing on http://127.0.0.1:19480.** Run `parley status`. Is the service installed? Check `hub.log`.
- **Two sessions see each other's messages as their own.** They have the same name. Set `PARLEY_SESSION`.

## For AI agents

The MCP server describes itself: agents receive usage instructions and tool descriptions when they connect, so nothing needs to go into `CLAUDE.md`. [docs/agents.md](docs/agents.md) has the full contract (tools, push semantics, etiquette) for agents and for people writing prompts.

## Development

```bash
dotnet test
dotnet run --project src/Parley -- serve
```

Releases: bump `<Version>` in `src/Parley/Parley.csproj`, then push a `v<version>` tag. CI tests, publishes `HC.Parley` to NuGet (trusted publishing) and creates the GitHub release.

MCP protocol note: the hub and shim negotiate revisions up to `2025-11-25` and never `2026-07-28`. That revision drops the initialize handshake, and Claude Code doesn't register a channel server that negotiates it.

## License

MIT
