# Pixel Tactics Online (Steam P2P)

A clean-room compatibility server for the abandoned **Pixel Tactics Online** client, plus
a launcher (`ptolaunch`) that connects two players peer-to-peer over Steam's relay with no
setup. The original server (`ptotemserv.ddns.net`) is gone, so this reimplements the wire
protocol from scratch and the client connects again like nothing happened.

No warranty is provided and everything should be considered **alpha** at best. The battle
engine is complete and the campaign is fully playable offline; the online relay path works
but still wants more real two-machine mileage.

This is the **`steam-p2p`** branch and the default. `main` is the older central-server
(Tailscale / cloud-VM) build, now archived under `archive/tailscale` with its guide in
[`DEPLOY.md`](DEPLOY.md). Everything below is the P2P model.

## What is this

Two pieces, split cleanly:

- **`PtoServer.cs`** is the clean-room game server: the server-authoritative battle engine
  plus accounts, decks, and the ranked ladder. It listens on raw TCP and runs locally on
  each player's machine.
- **`Launcher/ptolaunch`** is the networking and deployment layer. The game only ever talks
  to `127.0.0.1`; the launcher tunnels those bytes to the peer over Steam's relay, starts the
  local server, and handles matchmaking. See [`Launcher/README.md`](Launcher/README.md).

Accounts, decks, and campaign progress stay on each machine (the local server's `data/`).
Only genuinely shared state lives in Firebase (Realtime Database over plain REST, no SDK):
the match queue and the ranked ladder. Firebase is reached over outbound HTTPS, so it
sidesteps CGNAT with nothing to forward and no VM to keep alive. Friends come from Steam.

Matchmaking is symmetric, with no host or joiner to pick. When two peers pair in the queue,
the lower SteamID is silently elected the authority and runs its local server; the other
connects to it over the relay. Both sides compute the same pairing from the same queue
snapshot, so there is nothing to negotiate. When a match ends, both drop back to offline.

## Running it

The shipped thing is the `PTO_C151_P2P_Playtest` bundle (built separately, not in git).
Grab the latest from the repo's Releases. Per player:

- Install the **.NET Desktop Runtime 10 (x64)**. The campaign needs nothing else.
- Have **Steam** running and signed in (two online players need two different accounts).
  Steam is also what the game uses to log you in.
- Run the `Pixel Tactics Online` shortcut, or `launcher\ptolaunch.exe`, and click PLAY.

That is the whole flow. The launcher starts the local server, points the client at
`127.0.0.1`, loads the matchmaking URL (baked in), and launches the game. There are no
scripts to run and nothing to configure. It starts Online and falls back to Offline on its
own when there is no network or Steam; the gear toggles it by hand. The bundle's
`README.txt` has the player-facing walkthrough and `ONLINE_TESTING.txt` covers PvP.

## What works

- **Accounts.** Register and login, salted SHA-256, persisted under `data/`. The Steam build
  auto-logs in from the SteamID (op46 mode 2, get-or-create).
- **Lobby, collection, deckbuilder.** Full card collection; decks build and persist per user.
- **Battle engine, server-authoritative.** Every draw, summon, move, attack, and turn is a
  server-computed message: waves, melee and ranged, intercept, counter, armor, cover, leader
  HP and win/loss, all hero orders and spells, passives, positional and ongoing auras, traps,
  scry, discard, and all 25 leaders' abilities with themed cast effects.
- **Singleplayer campaign.** Three worlds, 25 nodes, 76 stages, each with Easy / Hard /
  Challenge AI that unlock in order. Progress is saved per difficulty. Playable fully offline.
- **Bot.** Minimax plus beam-search lookahead on the harder tiers; it summons, attacks, plays
  orders and wave-spells, arms traps, uses its leader, and protects its own leader.
- **Ranked ladder.** A personal climb ladder, mirrored to the shared Firebase ladder by the
  authority tailing its match log.
- **Launcher.** Symmetric matchmaking, auto-elected authority, Steam relay tunnel,
  offline-first, ranked sync, and continuous client-IP enforcement.

Local progress and ranked files are HMAC-signed, so a hand-edited `stages.txt` or `ranks.txt`
is rejected on load.

## How the networking works

The client is a GameMaker Studio 2 game. It talks to the server over a raw TCP socket, always
at `127.0.0.1:15338`, which is the launcher's proxy:

```
authority (lower SteamID):  game -> 127.0.0.1:15338 (ptolaunch) -> local PtoServer (15339)
                                                        ^
                   guest's peer == Steam relay ==> ptolaunch bridges to the local server

guest:  game -> 127.0.0.1:15338 (ptolaunch) == Steam relay ==> authority's ptolaunch -> its server
```

The tunnel is a byte-for-byte pump, so the wire framing and the WAN client patches
(connect-on-login, TCP reassembly, port config) are untouched.

The Steam client is GMS2 and does not honor DisableSandbox, so it ignores the `settings.ini`
next to the exe and reads `%LOCALAPPDATA%\ptoc\settings.ini` instead. `ptolaunch` writes and
continuously enforces `IP=127.0.0.1` there, and the client's built-in default is patched to
`127.0.0.1` too, so it always reaches the local launcher.

### Wire format (little-endian, identical both directions)

| offset | type | meaning                                          |
|-------:|------|--------------------------------------------------|
| 0      | u8   | opcode                                            |
| 1      | u16  | magic = `1374` (client reads but ignores it)      |
| 3      | u32  | total length in bytes, including this header      |
| 7...   | ...  | payload                                           |

A `buffer_string` is raw UTF-8 bytes followed by one `0x00` terminator.

### Key opcodes

| op | name                   | direction | notes                                                  |
|---:|------------------------|-----------|--------------------------------------------------------|
| 46 | login / register       | C-S, S-C  | Steam build: mode 2 is get-or-create keyed on SteamID  |
| 48 | loaded (door to lobby) | S-C       | bool legend, u16 rank                                  |
| 49 | add_card_to_collection | S-C       | the account blob arrives as many op49 packets          |
| 60 | stages                 | S-C       | campaign unlock bitmap                                 |
|  0 | queue (go online)      | C-S       | the launcher's go-online trigger                       |
|  3 | battle_end             | S-C       | the match-over signal                                  |
| 55 | start_stage            | C-S       | singleplayer campaign stage vs AI                      |
| 57 | concede                | C-S       | sender concedes, opponent wins                         |

The full opcode table and every handler live in [`docs/PROTOCOL.md`](docs/PROTOCOL.md).

## Compiling

Both halves are plain .NET 10.

Server (runs locally on each machine):

```sh
dotnet publish PtoServer.csproj -c Release -o out/server
```

Launcher (x64 to match the 64-bit `steam_api64.dll`):

```sh
dotnet publish Launcher/Launcher.csproj -c Release -o out/launcher
```

To build the distributable playtest bundle (patches the client, builds server and launcher,
packages the `.7z`):

```sh
pwsh build/build.ps1
```

`ptolaunch` self-checks with no Steam or Firebase needed: `demo` (byte tunnel), `matchdemo`
(pairing and election), `queuedemo` and `rankeddemo` (Firebase logic against a local fake
RTDB), and `check` (live Steam, Firebase, and server preflight).

## Notes

- Reverse engineered from `data.win` with UndertaleModTool (decompiled GML). The `1374` magic
  and the port constants are the client's own.
- Clean-room reimplementation for interoperability with an abandoned service. No original
  server code was available, confirmed with the original developer.
- Steam identity is client-asserted (no auth ticket in this build), so the shared ladder is
  spoofable. Fine for a private or friends launch. The hardening ceiling is a Steam-auth-ticket
  Cloud Function that validates identity and writes.
- Licensed under the terms in [`LICENSE`](LICENSE).
