using System;
using System.Collections.Generic;
using DiscordRPC;

namespace PtoLauncher;

// Discord Rich Presence, driven entirely by the packets the launcher already proxies between the
// game and the server (no game-client injection). The launcher sniffs a few opcodes and maps them
// to a presence:
//   - op46 Login / op3 BattleEnd  -> "In the menus" (idle), session timer
//   - op55 StartStage [deckId,sid] -> "World X - Stage Y (Difficulty)", match timer, "as <Leader>"
//   - op0  Queue [deckId]          -> "Player vs Player", match timer, "as <Leader>"
//   - op47 AddDeck (server->game)  -> builds deckId -> leader, so op55/op0's deckId names the leader
// The Discord Application/Client id and the large-image asset key come from launcher settings; with
// no id set, this is a silent no-op so the launcher runs unchanged.
sealed class DiscordPresence
{
    readonly object _lock = new object();
    DiscordRpcClient? _client;
    string _largeKey = "logo";
    DateTime _sessionStart = DateTime.UtcNow;
    readonly Dictionary<int, int> _deckLeader = new Dictionary<int, int>(); // deckId -> leader REAL

    static readonly string[] _diff = { "Easy", "Hard", "Challenge" };

    // real -> leader name (card_init.gml, cards with _special == 2).
    static readonly Dictionary<int, string> _leaderNames = new Dictionary<int, string>
    {
        {1,"Lixis"},{4,"Kallistar"},{5,"Heketch"},{6,"Hikaru"},{8,"Kavri"},{9,"Arec"},{10,"Mikhail"},
        {11,"Lesandra"},{14,"Sagas"},{16,"Rexan"},{18,"Magdelina"},{21,"Khadath"},{22,"Demitras"},
        {23,"Hepzibah"},{70,"Merjoram"},{71,"Cague"},{72,"Andrus"},{73,"Luca"},{74,"Rayne"},{75,"Eligor"},
        {76,"Uriah"},{77,"Ivo"},{78,"Lizaveta"},{79,"Riflam"},{99,"Baenvier"},{100,"Pendros"},
        {107,"Lukas"},{108,"Batrov"},{111,"Malandrax"},
    };

    // appId: the Discord Application (Client) id. largeKey: an art asset key uploaded to that app.
    // Both come from launcher settings; empty appId disables presence entirely.
    public void Init(string? appId, string? largeKey)
    {
        if (string.IsNullOrWhiteSpace(appId)) { Console.WriteLine("discord: no app id set; rich presence disabled"); return; }
        if (!string.IsNullOrWhiteSpace(largeKey)) _largeKey = largeKey!;
        try
        {
            lock (_lock)
            {
                _client = new DiscordRpcClient(appId);
                _client.Initialize();
                _sessionStart = DateTime.UtcNow;
            }
            SetMenu();
            Console.WriteLine("discord: rich presence enabled");
        }
        catch (Exception ex) { Console.WriteLine($"discord: could not start rich presence ({ex.Message})"); _client = null; }
    }

    public void Shutdown()
    {
        lock (_lock) { try { _client?.ClearPresence(); _client?.Dispose(); } catch { } _client = null; }
    }

    // ---- packet-driven state (called from the launcher's opcode sniffers) ------------------
    public void OnLogin() => SetMenu();
    public void OnBattleEnd() => SetMenu();

    public void OnAddDeck(byte[] pkt)
    {
        // [7-byte header][deckId u8][name \0-terminated][back u16][land u16][31x card u16]; card[0] = leader.
        try
        {
            int p = 7;
            if (pkt.Length <= p) return;
            int deckId = pkt[p++];
            while (p < pkt.Length && pkt[p] != 0) p++;   // skip name
            p++;                                         // skip null terminator
            p += 4;                                      // skip back u16 + land u16
            if (p + 2 > pkt.Length) return;
            int leaderCard = BitConverter.ToUInt16(pkt, p);
            lock (_lock) _deckLeader[deckId] = leaderCard / 2;   // store leader REAL
        }
        catch { }
    }

    public void OnStartStage(byte[] pkt)
    {
        try
        {
            if (pkt.Length < 10) return;
            int deckId = pkt[7];
            int sid = BitConverter.ToUInt16(pkt, 8);
            DecodeStage(sid, out int world, out int stage, out int diff);
            string details = "World " + world + " - Stage " + stage + " (" + _diff[Math.Clamp(diff, 0, 2)] + ")";
            SetPresence(details, LeaderState(deckId), DateTime.UtcNow);
        }
        catch { }
    }

    public void OnQueue(byte[] pkt)
    {
        try
        {
            int deckId = pkt.Length > 7 ? pkt[7] : 0;
            SetPresence("Player vs Player", LeaderState(deckId), DateTime.UtcNow);
        }
        catch { }
    }

    // ---- presence helpers -----------------------------------------------------------------
    void SetMenu() => SetPresence("In the menus", null, _sessionStart);

    string? LeaderState(int deckId)
    {
        int real;
        lock (_lock) { if (!_deckLeader.TryGetValue(deckId, out real)) return null; }
        return _leaderNames.TryGetValue(real, out var name) ? "as " + name : null;
    }

    void SetPresence(string details, string? state, DateTime startUtc)
    {
        lock (_lock)
        {
            if (_client == null) return;
            try
            {
                _client.SetPresence(new RichPresence
                {
                    Details = details,
                    State = state,
                    Timestamps = new Timestamps { Start = startUtc },
                    Assets = new Assets { LargeImageKey = _largeKey, LargeImageText = "Pixel Tactics Online" },
                });
            }
            catch (Exception ex) { Console.WriteLine($"discord: set presence failed ({ex.Message})"); }
        }
    }

    // sid -> (world 1-3, stage 1-9, difficulty 0-2). Mirrors the server's StageDecompose; World 3's
    // 9th node lives at sids 73-75.
    static void DecodeStage(int sid, out int world, out int stage, out int diff)
    {
        if (sid >= 73 && sid <= 75) { world = 3; stage = 9; diff = sid - 73; return; }
        int w = (sid - 1) / 24;
        int within = (sid - 1) % 24;
        world = w + 1;
        diff = within / 8;
        stage = within % 8 + 1;
    }
}
