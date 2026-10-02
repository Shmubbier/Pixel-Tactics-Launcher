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

        // Enumerate the bot's moves on a battle, return the single best-scored one.
        static BotMove BestMove(Battle b)
        {
            var ms = BotEnumerateMoves(b, MakeSlot(b, 0, null), b.P[0], b.P[1]);
            ms.Sort((x, y) => y.Score.CompareTo(x.Score));
            return ms.Count > 0 ? ms[0] : null;
        }
        static bool IsAttackTo(BotMove m, int tw, int tl) => m != null && m.Kind == 2 && m.Payload[4] == tw && m.Payload[5] == tl;

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

            // Counter blocker in the center Vanguard: bot melee hero in the center lane. Enemy center front is a
            // Counter Knight; a side lane has a squishy grunt. The bot should clear the blocker, not chip the grunt.
            var tb1 = MakeBattle(1, 4, 2, 20, 20);
            AddUnit(tb1.P[0], 2, 1, 50, atk: 5, max: 5);
            AddUnit(tb1.P[1], 2, 1, 35, atk: 3, max: 9, abil: UnitAbility.Counter);
            AddUnit(tb1.P[1], 2, 0, 20, atk: 1, max: 3);
            Check(IsAttackTo(BestMove(tb1), 2, 1), "attack: clears counter blocker over side chip");

            // Ranged bot unit prefers a high-value backline target over the frontmost tank in a column.
            // (Wave 1 so the Flank ranged unit is ready; the brief's wave 2 left it idle.)
            var tb2 = MakeBattle(1, 4, 1, 20, 20);
            tb2.P[1].LeaderAbilities = UnitAbility.RImmunity; // no leader snipe candidate; isolate the column choice
            AddUnit(tb2.P[0], 1, 0, 62, atk: 4, max: 5, abil: UnitAbility.RangedAttack);
            AddUnit(tb2.P[1], 2, 2, 70, atk: 1, max: 9);
            AddUnit(tb2.P[1], 2, 0, 71, atk: 6, max: 3, aggro: 3);
            var best2 = BestMove(tb2);
            Check(best2 != null && best2.Kind == 2 && best2.Payload[5] == 0, "attack: ranged picks the high-value column");

            // Tier 4 live: a ranged unit may shoot the BACKLINE (no frontmost requirement). Enemy column 0 has a
            // frontmost tank and a high-value squishy behind it (non-lethal, so tier 4 not tier 3). No intercept,
            // foe leader RImmunity so the leader snipe does not dominate. Best move is the rear squishy.
            var tb5 = MakeBattle(1, 4, 1, 20, 20);
            tb5.P[1].LeaderAbilities = UnitAbility.RImmunity;
            AddUnit(tb5.P[0], 1, 0, 62, atk: 4, max: 5, abil: UnitAbility.RangedAttack);
            AddUnit(tb5.P[1], 2, 0, 70, atk: 1, max: 20);
            AddUnit(tb5.P[1], 0, 0, 71, atk: 6, max: 9);
            var best5 = BestMove(tb5);
            Check(IsAttackTo(best5, 0, 0) && best5.Score >= 200 && best5.Score < 300, "attack: ranged shoots backline (tier 4 live)");

            // Intercept still wins: a squishy behind an Intercept unit is not offered (the shot would redirect).
            var tb6 = MakeBattle(1, 4, 1, 20, 20);
            tb6.P[1].LeaderAbilities = UnitAbility.RImmunity;
            AddUnit(tb6.P[0], 1, 0, 62, atk: 4, max: 5, abil: UnitAbility.RangedAttack);
            AddUnit(tb6.P[1], 2, 0, 70, atk: 1, max: 20, abil: UnitAbility.Intercept);
            AddUnit(tb6.P[1], 0, 0, 71, atk: 6, max: 9);
            var ms6 = BotEnumerateMoves(tb6, MakeSlot(tb6, 0, null), tb6.P[0], tb6.P[1]);
            Check(!ms6.Exists(m => m.Kind == 2 && m.Payload[4] == 0 && m.Payload[5] == 0) && ms6.Exists(m => m.Kind == 2 && m.Payload[4] == 2 && m.Payload[5] == 0),
                  "attack: no shot offered behind an Intercept unit; interceptor is offered");

            // BotAggro folds into the attack tiebreak (focus-fire): two equal chips, the aggro'd one wins.
            var tb7 = MakeBattle(1, 4, 2, 20, 20);
            AddUnit(tb7.P[0], 2, 1, 50, atk: 2, max: 5);
            AddUnit(tb7.P[0], 2, 0, 51, atk: 2, max: 5);
            AddUnit(tb7.P[1], 2, 1, 20, atk: 1, max: 9);
            AddUnit(tb7.P[1], 2, 0, 21, atk: 1, max: 9, aggro: 8);
            Check(BestMove(tb7) is BotMove m7 && m7.Kind == 2 && m7.Payload[5] == 0, "attack: BotAggro biases focus-fire");

            // Leader lethal is taken (and reaches the 800 shortcut).
            var tb3 = MakeBattle(1, 4, 2, 20, 3);
            AddUnit(tb3.P[0], 2, 1, 50, atk: 9, max: 5);
            var best3 = BestMove(tb3);
            Check(best3 != null && best3.Score >= 800 && IsAttackTo(best3, 1, 1), "attack: lethal leader hit >= 800");

            // Melee-frontmost attacker gate survives the scoring rewrite: a Flank melee BEHIND its own Vanguard
            // ally produces NO attack candidate, while the Vanguard ally in front does.
            var tb4 = MakeBattle(1, 4, 2, 20, 20);
            AddUnit(tb4.P[0], 2, 1, 50, atk: 3, max: 5);
            AddUnit(tb4.P[0], 1, 1, 51, atk: 3, max: 5);
            AddUnit(tb4.P[1], 2, 1, 20, atk: 2, max: 9);
            var ms4 = BotEnumerateMoves(tb4, MakeSlot(tb4, 0, null), tb4.P[0], tb4.P[1]);
            bool flankOffered = ms4.Exists(m => m.Kind == 2 && m.Payload[2] == 1 && m.Payload[3] == 1);
            bool vanOffered   = ms4.Exists(m => m.Kind == 2 && m.Payload[2] == 2 && m.Payload[3] == 1);
            Check(!flankOffered && vanOffered, "attack: blocked flank melee gets no attack; front vanguard does");

            // Given two equal-value chip targets, one a non-lethal Counter unit and one plain, the bot picks the
            // plain one (melee into a surviving Counter costs retaliation).
            var cb = MakeBattle(1, 4, 2, 20, 20);
            AddUnit(cb.P[0], 2, 0, 50, atk: 3, max: 5);
            AddUnit(cb.P[0], 2, 2, 51, atk: 3, max: 5);
            AddUnit(cb.P[1], 2, 0, 35, atk: 7, max: 9, abil: UnitAbility.Counter);
            AddUnit(cb.P[1], 2, 2, 20, atk: 2, max: 9);
            var cpick = BestMove(cb);
            Check(cpick != null && cpick.Kind == 2 && cpick.Payload[5] == 2, "counter: avoids non-lethal melee into Counter");

            // A LETHAL melee hit into a Counter unit is NOT penalized (it dies before retaliating).
            Check(BotCounterPenalty(MakeUnitState(9, counter: false), MakeUnitState(2, counter: true), dmg: 9, kills: true, targetIsLeader: false) == 0, "counter: lethal hit not penalized");
            // RANGED into a Counter unit is NOT penalized (counter is melee-only).
            Check(BotCounterPenalty(MakeUnitState(3, ranged: true), MakeUnitState(7, counter: true), dmg: 3, kills: false, targetIsLeader: false) == 0, "counter: ranged not penalized");

            // THE regression: on a board where attacking is clearly best, Easy must pick an attack, not filler.
            var ez = MakeBattle(1, 4, 2, 20, 20);
            AddUnit(ez.P[0], 2, 1, 50, atk: 4, max: 5);
            AddUnit(ez.P[1], 2, 1, 20, atk: 2, max: 4);
            ez.P[0].Units[Key(0,0)] = new BUnit { Card = 99, IsCorpse = true };  // a clear-corpse filler option exists
            var easyAttacks = 0;
            for (int i = 0; i < 200; i++)
            {
                var ems = BotEnumerateMoves(ez, MakeSlot(ez, 0, new List<ushort>{ 10 }), ez.P[0], ez.P[1]);
                var epick = BotChooseGreedyOrBlunder(ems, blunder: true);
                if (epick != null && epick.Kind == 2) easyAttacks++;
            }
            Check(easyAttacks > 100, "easy: attacks the clear majority of the time (was 0 before), got " + easyAttacks);

            // Greedy (no blunder) always takes the top-scored move.
            var gms = BotEnumerateMoves(ez, MakeSlot(ez, 0, new List<ushort>{ 10 }), ez.P[0], ez.P[1]);
            gms.Sort((x,y)=>y.Score.CompareTo(x.Score));
            var greedy = BotChooseGreedyOrBlunder(new List<BotMove>(gms), blunder: false);
            Check(greedy.Score == gms[0].Score, "greedy: takes the best move");

            // Guaranteed win is taken even with blunder on.
            var ewin = MakeBattle(1, 4, 2, 20, 2);
            AddUnit(ewin.P[0], 2, 1, 50, atk: 9, max: 5);
            var ewm = BotEnumerateMoves(ewin, MakeSlot(ewin, 0, null), ewin.P[0], ewin.P[1]);
            Check(BotChooseGreedyOrBlunder(ewm, blunder: true).Score >= 800, "easy: still takes a guaranteed win");

            Console.WriteLine("botai-test: " + _fail + " failure(s)");
            return _fail;
        }
    }
}
