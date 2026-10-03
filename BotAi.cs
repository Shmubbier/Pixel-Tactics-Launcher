using System;
using System.Collections.Generic;

namespace PtoServer;

partial class Program
{
    // Legible per-unit facts the target-priority rules read, computed once from a BUnit + its board
    // position. Keeps the rules off raw Abilities/position math.
    readonly struct UnitState
    {
        public readonly bool IsRanged, HasCounter, HasReflect, IsHeroKiller, HasIntercept, IsIncorporeal, IsRImmune;
        public readonly int Attack, Remaining, Armor;
        public readonly bool IsFrontmostInLane, ThreatensEnemyLeader;

        UnitState(bool ranged, bool counter, bool reflect, bool heroKiller, bool intercept, bool incorporeal,
                  bool rimmune, int attack, int remaining, int armor, bool frontmost, bool threatensLeader)
        { IsRanged = ranged; HasCounter = counter; HasReflect = reflect; IsHeroKiller = heroKiller;
          HasIntercept = intercept; IsIncorporeal = incorporeal; IsRImmune = rimmune; Attack = attack;
          Remaining = remaining; Armor = armor; IsFrontmostInLane = frontmost; ThreatensEnemyLeader = threatensLeader; }

        // Board-free factory for pure classifier tests.
        public static UnitState ForTest(bool ranged, bool counter, int attack, int remaining, int armor, bool frontmost, bool reflect = false)
            => new UnitState(ranged, counter, reflect, false, false, false, false, attack, remaining, armor, frontmost, false);

        public static UnitState Of(BUnit u, int wave, int lane, PlayerState owner, PlayerState enemy)
        {
            bool ranged = (u.Abilities & UnitAbility.RangedAttack) != 0;
            int attack = u.Atk;
            // Frontmost = reachable by melee: no LIVING, non-incorporeal ally in a higher wave of this lane.
            bool frontmost = true;
            for (int w = 2; w > wave; w--)
            {
                BUnit ally;
                if (owner.Units.TryGetValue(Key(w, lane), out ally) && ally != null && !ally.IsCorpse
                    && (ally.Abilities & UnitAbility.Incorporeal) == 0) { frontmost = false; break; }
            }
            // Could this unit damage the enemy leader if it attacked now? Ranged (unless RImmune leader),
            // or center-lane melee with the enemy Vanguard center (2,1) empty.
            bool enemyLeaderRanged = (enemy.LeaderAbilities & UnitAbility.RImmunity) == 0;
            bool threatens;
            if (ranged) threatens = enemyLeaderRanged;
            else
            {
                BUnit guard; bool centerBlocked = enemy.Units.TryGetValue(Key(2, 1), out guard) && guard != null && !guard.IsCorpse;
                threatens = lane == 1 && !centerBlocked;
            }
            return new UnitState(ranged,
                (u.Abilities & UnitAbility.Counter) != 0,
                (u.Abilities & UnitAbility.Reflect) != 0,
                (u.Abilities & UnitAbility.HeroKiller) != 0,
                (u.Abilities & UnitAbility.Intercept) != 0,
                (u.Abilities & UnitAbility.Incorporeal) != 0,
                (u.Abilities & UnitAbility.RImmunity) != 0,
                attack, Math.Max(1, u.Max - u.Damage), u.Armor, frontmost, threatens);
        }
    }

    // tiers 1..5 -> 500..100; tiebreak clamped to [0,99] so tiers never overlap and nothing but a real
    // leader KILL (tier 0, handled by the +800 bonus in BotAddAttack) trips the >=800 guaranteed-win path.
    static double BotTargetScore(int tier, double tiebreak)
        => tier <= 0 ? 800.0 : (6 - tier) * 100.0 + Math.Min(99.0, Math.Max(0.0, tiebreak));

    // Classify one attack into a priority tier (0 best). tiebreak orders candidates within a tier.
    static int BotClassifyTarget(in UnitState atk, in UnitState tgt, bool targetIsLeader, bool kills,
                                 bool threatensMyLeader, out double tiebreak)
    {
        int dmg = Math.Max(0, (atk.IsHeroKiller && !targetIsLeader ? atk.Attack * 2 : atk.Attack) - tgt.Armor);
        tiebreak = Math.Min(99, dmg * 4 + (kills ? 20 : 0) + tgt.Attack);
        if (targetIsLeader) return kills ? 0 : 2;                          // leader: win, else high-value offense
        if (threatensMyLeader) return 2;                                   // DEFEND
        if (kills) return 3;                                               // KILL
        if (!atk.IsRanged && tgt.IsFrontmostInLane && tgt.HasCounter) return 1; // UNBLOCK a counter blocker
        if (atk.IsRanged && !tgt.IsFrontmostInLane) return 4;             // RANGED-REACH the backline
        return 5;                                                          // CHIP
    }

    // Cost of attacking into a Counter/Reflect target.
    // Melee, non-lethal hit into a surviving Counter unit: the attacker eats max(1, tgt.Attack - atk.Armor)
    // back (PtoServer.cs:5369). Subtract that; blow it up if the retaliation would kill the attacker.
    // Lethal melee, or any ranged hit, is safe. Reflect: subtract the damage we would see reflected.
    static double BotCounterPenalty(in UnitState atk, in UnitState tgt, int dmg, bool kills, bool targetIsLeader)
    {
        if (targetIsLeader || kills || atk.IsRanged) return 0;
        double penalty = 0;
        if (tgt.HasCounter)
        {
            int back = Math.Max(1, tgt.Attack - atk.Armor);
            penalty += back * 6 + (back >= atk.Remaining ? 400 : 0);
        }
        if (tgt.HasReflect) penalty += dmg * 4;
        return penalty;
    }

    const double EasyBlunderChance = 0.35;

    // Easy picker: usually the best move, but sometimes a deliberately imperfect one (a weak human still
    // attacks, just mis-targets). Never passes up a guaranteed win; never degenerates into pure filler when
    // a real move exists. "blunder" is false for the greedy path.
    static BotMove BotChooseGreedyOrBlunder(List<BotMove> moves, bool blunder)
    {
        if (moves.Count == 0) return null;
        moves.Sort((x, y) => y.Score.CompareTo(x.Score));
        if (moves[0].Score >= 800) { moves[0].Rank = 0; moves[0].Pool = moves.Count; moves[0].Top = moves[0].Score; return moves[0]; }
        int idx = 0;
        bool roll; lock (_rng) roll = blunder && _rng.NextDouble() < EasyBlunderChance;
        if (roll)
        {
            // Pick among the reasonable alternatives: everything within 200 score of the best (keeps it from
            // choosing pure filler like draw/clear-corpse when a decent move exists), excluding the very best.
            double cut = moves[0].Score - 200;
            int hi = 1; while (hi < moves.Count && moves[hi].Score >= cut) hi++;
            if (hi > 1) lock (_rng) idx = _rng.Next(1, hi);
        }
        moves[idx].Rank = idx; moves[idx].Pool = moves.Count; moves[idx].Top = moves[0].Score;
        return moves[idx];
    }
}
