# Firebase setup for the PTO launcher

The launcher uses one **Firebase Realtime Database** over the REST API (no SDK). You
do not pre-create any nodes: RTDB creates them on first write. What you need to do is
(1) point the launcher at your database, and (2) set database **rules** that allow the
paths below.

## 1. Point the launcher at the database

Put your RTDB URL (the `https://<project>-default-rtdb.<region>.firebasedatabase.app`
form, no trailing slash) in one of:

- env var `PTO_FIREBASE_URL`, or
- a file `firebase.txt` beside `ptolaunch.exe`.

Optional auth token (legacy DB secret or an ID token) via `PTO_FIREBASE_AUTH` /
appended as `?auth=`. With a DB secret set, it bypasses rules and you can leave the
rules locked; without one, use the open rules in step 3.

## 2. Data model (what lives where)

| Path | Shape | Written by | Read by |
|------|-------|-----------|---------|
| `queue/<steamid>` | `{steamId, name, ts}` | both peers (heartbeat) | both peers (pairing) |
| `matches/<pairkey>` | `{authority, ts}` | authority | guest (confirm) |
| `ranked/<steamid>` | `{wins, losses}` | authority (after each match) | launcher (rank, win rate, ladder) |
| `profiles/<steamid>` | `{name, avatarHero, ts}` | **each client, its own** (name on startup; `avatarHero` when the player picks their portrait) | launcher (resolve names + show each player's chosen hero) |
| `history/<steamid>/<pushid>` | `{ts, win, opp}` | authority (both players, after each match) | launcher (match list, activity heatmap, records) |

`queue`, `matches`, `ranked` already existed. **`profiles` and `history` are new** for
this build. Nothing to create by hand.

## 3. Database rules

Add rules that allow read/write on all five paths. For a private/closed test DB, the
simplest working rules are:

```json
{
  "rules": {
    "queue":    { ".read": true, ".write": true },
    "matches":  { ".read": true, ".write": true },
    "ranked":   { ".read": true, ".write": true },
    "profiles": { ".read": true, ".write": true },
    "history":  { ".read": true, ".write": true }
  }
}
```

Paste these in Firebase console -> Realtime Database -> Rules -> Publish. (If you set a
DB secret in `PTO_FIREBASE_AUTH`, you can keep the default locked rules instead, since
the secret bypasses them.)

**Security note:** these open rules let anyone with the URL read/write, so scores and
history are spoofable. That is fine for a closed playtest but not for a public release.
The proper fix (already flagged in the launcher README) is a Cloud Function that
validates a Steam auth ticket before writing `ranked`/`history`; the client code does
not need to change for that.

## What is NOT in Firebase (yet)

- **Decks** are now read from the local server's `data/<steamid>.decks` file (the launcher
  spawns that server, so it owns the file). They are local, not in Firebase. Card art is a
  hero-sprite stand-in until the real per-card art is exported.
- **Heroes table** (per-hero win/KDA) still comes from game-side data that isn't tracked
  yet, so that one panel shows an empty state.
- **Match KDA / leader / deck** aren't reported by the game, so match tiles show the
  result (win/loss), opponent, and time only. Add those to each `history` entry once the
  game reports them, and the launcher will show them.
