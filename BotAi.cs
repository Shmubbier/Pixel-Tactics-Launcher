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

        public static UnitState Of(BUnit u, int wave, int lane, PlayerState owner, PlayerState enemy)
        {
            bool ranged = (u.Abilities & UnitAbility.RangedAttack) != 0;
            int attack = u.Atk + u.Strength + u.TempStrength;
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
}
