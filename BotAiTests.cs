using System;
using System.Collections.Generic;

namespace PtoServer;

partial class Program
{
    // Console self-check for the bot AI (run: dotnet run --project PtoServer.csproj -- botai-test).
    // Nested so it can reach Program's private bot methods and the internal Battle/PlayerState/BUnit types.
    static class BotAiTests
    {
        static int _fail;
        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) _fail++;
        }

        // Build a Battle with the bot at P[0] and the human at P[1]. leaderReal is the card/2 leader id.
        static Battle MakeBattle(int botLeaderReal, int foeLeaderReal, int wave, int botLeaderLife, int foeLeaderLife)
        {
            var b = new Battle { Wave = wave, Round = 2, BotP = 0, BotDifficulty = 2, Active = 0 };
            b.P[0] = new PlayerState { LeaderCard = (ushort)(botLeaderReal * 2), LeaderLife = botLeaderLife, LeaderMax = botLeaderLife };
            b.P[1] = new PlayerState { LeaderCard = (ushort)(foeLeaderReal * 2), LeaderLife = foeLeaderLife, LeaderMax = foeLeaderLife };
            return b;
        }

        // Place a unit on side ps at (wave,lane). atk/max/armor explicit; abilities OR-ed UnitAbility flags.
        static void AddUnit(PlayerState ps, int wave, int lane, ushort card, int atk, int max,
                            UnitAbility abil = UnitAbility.None, int armor = 0, int damage = 0, int aggro = 0)
        {
            ps.Units[Key(wave, lane)] = new BUnit
            {
                Card = card, Atk = atk, Max = max, Armor = armor, Damage = damage,
                Abilities = abil, BotAggro = aggro,
            };
        }

        // A minimal BattleSlot for the bot/foe with no network (decision functions must not Send).
        // (Brief said ClientInfo; the real type of BattleSlot.Me is Waiting.)
        static BattleSlot MakeSlot(Battle b, int p, List<ushort> hand)
        {
            return new BattleSlot { P = p, Battle = b, Hand = hand ?? new List<ushort>(), Me = new Waiting { User = p == 0 ? "bot" : "foe", IsBot = p == 0 } };
        }

        static UnitState MakeUnitState(int atk, bool ranged = false, bool counter = false, bool frontmost = false,
                                       int remaining = 5, int armor = 0)
            => UnitState.ForTest(ranged, counter, atk, remaining, armor, frontmost);

        public static int Run()
        {
            _fail = 0;
            // Sanity: a freshly built battle has the bot leader alive and no units.
            var b = MakeBattle(1, 4, 2, 20, 20);
            Check(b.P[0].Units.Count == 0, "sanity: empty board");
            Check(b.P[0].LeaderLife == 20, "sanity: bot leader life set");

            // UnitState: flags come straight off abilities; derived fields off stats/position.
            var us = MakeBattle(1, 4, 2, 20, 20);
            AddUnit(us.P[0], 2, 1, 35, atk: 3, max: 5, abil: UnitAbility.Counter, armor: 1, damage: 1);
            var st = UnitState.Of(us.P[0].Units[Key(2,1)], 2, 1, us.P[0], us.P[1]);
            Check(st.HasCounter && !st.IsRanged, "unitstate: counter flag");
            Check(st.Attack == 3 && st.Remaining == 4 && st.Armor == 1, "unitstate: derived stats");
            Check(st.IsFrontmostInLane, "unitstate: frontmost when alone in lane");

            // A ranged unit sitting behind a frontline ally is still ranged and NOT frontmost.
            AddUnit(us.P[0], 1, 1, 62, atk: 2, max: 5, abil: UnitAbility.RangedAttack);
            var rg = UnitState.Of(us.P[0].Units[Key(1,1)], 1, 1, us.P[0], us.P[1]);
            Check(rg.IsRanged && !rg.IsFrontmostInLane, "unitstate: ranged behind ally not frontmost");

            // Scoring: better tier always outranks worse tier, and nothing but a leader KILL reaches 800.
            Check(BotTargetScore(0, 50) >= 800, "score: tier0 win >= 800 shortcut");
            Check(BotTargetScore(1, 99) < 800 && BotTargetScore(1, 0) > BotTargetScore(2, 99), "score: tiers ordered under 800");

            var atkMelee = MakeUnitState(atk: 4, ranged: false);
            var atkRanged = MakeUnitState(atk: 4, ranged: true);
            double tb;
            // Center-lane frontmost Counter blocker -> UNBLOCK (tier 1).
            var blocker = MakeUnitState(atk: 3, counter: true, frontmost: true);
            Check(BotClassifyTarget(atkMelee, blocker, targetIsLeader: false, kills: false, threatensMyLeader: false, out tb) == 1, "classify: counter blocker -> unblock");
            // A plain frontmost hero my melee just chips -> CHIP (tier 5).
            var grunt = MakeUnitState(atk: 2, frontmost: true);
            Check(BotClassifyTarget(atkMelee, grunt, false, false, false, out tb) == 5, "classify: plain chip -> tier5");
            // Same grunt but it threatens my leader -> DEFEND (tier 2).
            Check(BotClassifyTarget(atkMelee, grunt, false, false, true, out tb) == 2, "classify: threat -> defend");
            // A kill on a hero -> KILL (tier 3).
            Check(BotClassifyTarget(atkMelee, grunt, false, true, false, out tb) == 3, "classify: kill -> tier3");
            // Ranged hitting a non-front backline unit -> RANGED-REACH (tier 4).
            var back = MakeUnitState(atk: 2, frontmost: false);
            Check(BotClassifyTarget(atkRanged, back, false, false, false, out tb) == 4, "classify: ranged reach -> tier4");

            Console.WriteLine("botai-test: " + _fail + " failure(s)");
            return _fail;
        }
    }
}
