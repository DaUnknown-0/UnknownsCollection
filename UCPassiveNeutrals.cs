// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCPassiveNeutrals (review 2026-10-02).
 *
 * TOR's Helpers.isKiller counts every neutral as a killer except its own passive ones (Jester,
 * Arsonist, Vulture, Lawyer, Pursuer). UC's passive neutrals carry isNeutral RoleInfos too, so the
 * Snitch in "Killers" mode pointed arrows and map dots at a Bug, Collector, Copycat, Necromancer or a
 * Follower before his takeover, and those players got the "Snitch revealed" warning. Same fix
 * MultiJester uses for its extra Jesters: a postfix that takes them back out. The Hunter's prey check
 * asks IsPassive directly as well, so there is exactly one list.
 */

using HarmonyLib;
using TheOtherRoles;

namespace UnknownsCollection {
    internal static class UCPassiveNeutrals {
        private static bool Is(PlayerControl holder, PlayerControl p) =>
            holder != null && p != null && holder.PlayerId == p.PlayerId;

        internal static bool IsPassive(PlayerControl p) =>
            (Bug.active && Is(Bug.bug, p))
            || (Collector.active && Is(Collector.collector, p))
            || (Follower.active && !Follower.hasCopied && Is(Follower.follower, p))
            || (Copycat.active && Is(Copycat.copycat, p))
            || (Necromancer.active && Is(Necromancer.necromancer, p));

        [HarmonyPatch(typeof(Helpers), nameof(Helpers.isKiller))]
        static class IsKillerPatch {
            public static void Postfix(PlayerControl player, ref bool __result) {
                try {
                    if (__result && player != null && player.Data != null && player.Data.Role != null
                        && !player.Data.Role.IsImpostor && IsPassive(player))
                        __result = false;
                } catch { }
            }
        }
    }
}
