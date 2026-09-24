# Cross-device Parley

Status: implemented (phases 0–2 below), not yet released. Goal: agents on several of the user's machines share one set of topics, and the user follows along from any device, phone included.

## Decision: one hub, joined devices

One device runs the hub (`parley serve`, as today). Other devices *join* it and run only shims (`parley mcp`), no local hub. Transport is the user's own overlay network (NetBird, Tailscale, WireGuard); Parley does no TLS or NAT traversal.

Rejected:
- **Hubs that replicate to each other**: needs global message ids (they're `++int`), dedupe, offline catch-up, and conflict rules for topic deletes and cursors. Weeks of work to solve a problem an always-on hub removes.
- **Hosted relay**: accounts, multi-tenant, running costs. A different product.

Cost of the choice: while the hub device sleeps, every joined device loses Parley. The fix is operational: run the hub on the always-on machine (desktop, NAS, Pi).

## Network

- Peers reach the hub on its overlay address, e.g. `http://100.67.218.227:19480` or `http://steve-beast.netbird.cloud:19480`. WireGuard encrypts the traffic, so plain HTTP inside the overlay is fine.
- **Specific addresses, never a wildcard.** The hub always binds `127.0.0.1`. While shared, it also binds every local address inside `allowFrom` (default `100.64.0.0/10`, the NetBird and Tailscale range). `HubEndpoints` feeds these to Kestrel as reloadable configuration, re-evaluated when sharing changes and on `NetworkChange.NetworkAddressChanged`. So an overlay that connects after the hub started gets picked up, and Kestrel rebinds only what changed.
  - Why not `0.0.0.0`: tested on Windows, with Kestrel on `0.0.0.0:P` a plain `TcpListener` could still bind `127.0.0.1:P`. A second hub would start and win local traffic. A wildcard would also expose the hub on every network.
- Connections from an address outside `allowFrom` get a 403. The first line of defence is still the overlay's own access policy (NetBird: allow TCP 19480 to the hub peer from your own group).
- Windows Firewall: the first overlay bind may prompt or silently block. `parley devices add` prints a `New-NetFirewallRule` scoped to `allowFrom`. Parley never changes firewall settings itself.
- **No HTTPS.** The browser shows "not secure", the cookie can't be `Secure`, and there's no web push. NetBird has no peer certificates yet ([netbirdio/netbird#5479](https://github.com/netbirdio/netbird/issues/5479)); a reverse proxy works meanwhile. `parley join https://…` keeps port 443 for that case.

## Auth: device tokens from pairing codes

Loopback stays token-free (any local process is trusted, as before). Every other request needs a device credential, except `GET /` (the page itself, so a browser can show the pairing form) and `POST /api/pair`.

Pairing:
1. On the hub: `parley devices add laptop`, or **Devices → + Add** in the hub machine's web UI. This prints a one-time code (`K7F-M2Q-9XD`, about 45 bits, 10 minutes) and the join URL(s). The hub names the device, so the joining side has nothing to choose.
2. A terminal device: `parley join <url> <code>` trades the code for a token (`prly_` + 32 random bytes) and never prints it.
3. A browser: the page gets a 401 from `/api/health` and shows a code field (dashes added as you type); `POST /api/pair {code, cookie: true}` sets the token as a cookie. The Add dialog shows a QR code for `<overlay url>/#/pair/<code>`, rendered on the hub (QRCoder, SVG data URL) because the page loads no external assets. Opening that link pairs without typing, and the dialog closes itself once the code is redeemed (device changes reach the dashboard stream as `changed`).

Details:
- Codes are single use. Pairing a name again replaces the old device and revokes its token. After 10 wrong codes, every pending code is dropped.
- Storage on the hub: `sharing.json` holds `{allowFrom, devices: [{name, tokenSha256, pairedAt, lastSeen, version}]}`, hashes only. On a joined device: `remote.json` holds `{url, token, device}`, created empty and then `chmod 600` on Unix; `%APPDATA%` is already per-user on Windows.
- On the wire: `Authorization: Bearer` from the shim and CLI. The web UI uses an `HttpOnly; SameSite=Strict` cookie, because `EventSource` can't send headers.
- Revocation: `parley devices remove <name>`, or × in the Devices panel. It also aborts that device's in-flight requests (`Device.Revoked` → `HttpContext.Abort`): event streams and `read_messages` long-polls. A browser that gets unpaired falls back to the pairing form. `parley leave` unpairs itself via `POST /api/unpair`.
- **Sharing is implied by devices.** The hub listens beyond loopback exactly while a device is paired or a code is pending. Removing the last device closes the network side; there's no switch to forget.
- **Hub-only**: `/api/shutdown`, `/api/update` and `/api/devices*` refuse remote callers, even paired ones. A remote web UI shows "run `parley update` on the hub machine" instead of the Update button.

## Host filter and CSRF

- Loopback connections keep the loopback-Host rule. Without it, a DNS-rebinding page on the hub machine could use the token-free loopback API.
- Remote connections accept any Host, because the credential is the gate. A rebinding page on another peer runs on a foreign origin, so the browser doesn't send the cookie.
- JSON-only mutations plus `SameSite=Strict` cover CSRF.

## Session identity

Names stay global: `Api` on the desktop and `Api` on the laptop are one session. They share cursors and `@Api`, as two terminals in one folder already do. `PARLEY_SESSION` splits them.

- `Session.Device` records the machine the session last called or listened from (the hub's own name for loopback). The web UI and `parley status` show it once sessions span more than one device.
- Not built (yet): a warning when one name is live on two devices at once, and always-qualified names like `laptop/Api`. Qualified names would rename every existing session, cursor and `@name` topic. Reconsider if shared names cause duplicate work in practice.
- Any authenticated device can post under any name, as the web UI always could. That's acceptable while every device belongs to one user; see "Next: sharing across people".

## Joined-device CLI

| Command | Effect |
|---|---|
| `parley join <url> <code>` | Trade the code, write `remote.json`, register the MCP clients. Uninstalls a local hub service, if any (conversations stay on disk). `http://host` without a port means `:19480`. |
| `parley leave` | Unpair on the hub (best effort) and delete `remote.json`. Sessions use a local hub again after a restart. |
| `parley status` | Shows "joined as laptop" and the hub's sessions and topics, plus an API-version mismatch. |
| `parley install` | Only registers clients: no hub service is needed while joined. |
| `parley update` | Updates this device's tool only, and doesn't start a local hub afterwards. |

`PARLEY_URL` / `PARLEY_TOKEN` still override `remote.json`. The joined hub's token is only ever sent to the joined hub, never to a `PARLEY_URL` override.

## Fixes that came first (phase 0)

1. `parley update` stopped "the hub" via `HubUrl`: it read the pid from `/api/health` and killed that pid locally. With a remote URL, that's another machine's pid. It now always uses `ParleyConfig.LocalHubUrl`.
2. The shim's event stream had no idle limit, so after sleep or a network change, pushes could stop silently forever on a half-open connection. `SseReader` now throws `TimeoutException` after 2.5 missed heartbeats (about 62 s), and the push loop reconnects with `since=`.

Known leftover: the hub counts a vanished peer's listener as connected until a heartbeat write fails, which can take minutes.

## Version skew

- `/api/health` returns `apiVersion` (`Protocol.ApiVersion`, bumped only on breaking API changes), plus `device` and `local`. `parley join` and `status` compare the version and say which side to update.
- The shim and CLI send `X-Parley-Version`, and the Devices list shows each device's version.
- Keep API changes additive.

## Agent contract

`AgentInstructions` and docs/agents.md now say that other sessions may be on other machines: paths and `localhost` URLs mean the sender's machine.

## Phases

| Phase | Scope | State |
|---|---|---|
| 0 | The two fixes above | done |
| 1 | Registry, endpoints, auth gate, pairing API, `devices`/`join`/`leave`, token-carrying shim and CLI, status/update aware of joined devices, `apiVersion`, agent docs, tests | done |
| 2 | Web UI: pairing form + cookie, Devices panel with add/remove on the hub machine, a QR code that pairs a phone by scanning, device labels on sessions, hub-only Update button, small mobile fixes | done |
| Later, only if needed | A QR code in the terminal for `parley devices add`, a shim outbox while the hub is down, a Docker image for an always-on hub, a warning when one name is live on two devices, a read-only viewer role, built-in TLS | — |

## Next: sharing across people

Two people (e.g. brothers) co-working on a topic breaks today's assumption that every device belongs to one user:
- Identity per person, not per device: senders can't be spoofed across people, and devices group under a person.
- Per-topic access (who may read or post), instead of all or nothing. `@name` direct lines need that most.
- Who runs the hub: one person's machine exposed to the other's overlay (NetBird can share a peer across accounts, or a shared network), or a small hosted hub.
- Etiquette in the agent contract: a message from another person's agent is less trusted than one from your own user.
