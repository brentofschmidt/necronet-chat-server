# Necronet Chat Server

Real-time chat backbone for Necronet. A .NET 8 **SignalR** server that the
React web client connects to over WebSocket, and that the C# game server will
later publish into. It fans messages out to subscribers; persistence and
rich population still live in Supabase (the web app's existing RPCs).

## Architecture (v1)

```
Browser ──WebSocket(JWT)──► SignalR Hub (/hubs/chat) ──► channel groups ──► subscribers
                                   │
Game server / webhook ──HTTP──► /internal/publish (shared secret)
                                   │
                            Supabase Postgres (membership checks via Npgsql)
                            Supabase JWT (browser auth)
```

- **`Necronet.Contracts`** — shared DTOs + the `IChatClient` interface. The
  game server will reference this same library so both sides agree on shapes.
- **`Necronet.ChatServer`** — the SignalR host.
  - `Hubs/ChatHub.cs` — browser edge. `JoinChannel` / `LeaveChannel` map a
    connection into/out of a channel's SignalR group. Joining is gated on a
    real DB membership check.
  - `/internal/publish` — transport-neutral seam to fan a message out to a
    channel. Guarded by a shared secret, not the user JWT. This is where the
    web client's post-send hook, a DB webhook, or the game server pushes.
  - SignalR Groups == channels. `ChannelGroups.ForChannel(id)` is the
    canonical group name (don't hand-build `"channel:..."` strings).

### Deliberately deferred
- **Redis backplane** — only needed for multiple instances. Add later with one
  line: `builder.Services.AddSignalR().AddStackExchangeRedis("<conn>")`.
- **Who owns the write path** — server-as-writer vs. signal-and-refetch. The
  hub doesn't care; `/internal/publish` is the neutral seam until that's decided.
- **Game-server transport + proximity/spatial chat** — second pass.
- **Rich payloads** — entity pills, reaction aggregates, per-viewer flags.
  `ChatMessageDto` carries plain fields for now.

## Setup

1. Fill in `src/Necronet.ChatServer/appsettings.Development.json` (gitignored):
   - **`Supabase:JwtSecret`** — Supabase dashboard → Settings → API → JWT Secret.
   - **`Supabase:Issuer`** — `https://<project-ref>.supabase.co/auth/v1`.
   - **`ConnectionStrings:Supabase`** — Settings → Database → Connection string
     (the direct 5432 connection; URI or key-value form both work with Npgsql).
   - **`Internal:PublishSecret`** — any long random string.
   - **`Cors:AllowedOrigins`** — your web dev origin (default `http://localhost:5173`).

2. Run:
   ```sh
   dotnet run --project src/Necronet.ChatServer
   ```

3. Health check: `GET http://localhost:5xxx/healthz` → `{ "status": "ok" }`
   (the exact port prints on startup / lives in `Properties/launchSettings.json`).

## Quick smoke test

Once the web client is wired to connect (with a valid Supabase session token via
`?access_token=`), join a channel, then push a message through the seam:

```sh
curl -X POST http://localhost:5xxx/internal/publish \
  -H "X-Internal-Secret: <your Internal:PublishSecret>" \
  -H "Content-Type: application/json" \
  -d '{
        "message": {
          "id": "11111111-1111-1111-1111-111111111111",
          "channelId": "<a channel id you are a member of>",
          "senderAccountId": "<some user id>",
          "senderDisplayName": "Mortwell",
          "body": "hello from the chat server",
          "sentAt": "2026-06-10T12:00:00Z"
        }
      }'
```

Any browser subscribed to that channel's group receives `ReceiveMessage`.

## Next steps
- Wire the React client: `@microsoft/signalr` connection to `/hubs/chat` with
  `accessTokenFactory` returning the Supabase session token, then `JoinChannel`
  per open channel and handle `ReceiveMessage`.
- Decide the write path (who calls `/internal/publish`).
- Game-server publish path + proximity chat.
- Redis backplane when scaling past one instance.
