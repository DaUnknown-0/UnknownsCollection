// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Guesser integration for the Unknown's Collection impostor roles (Tesla, Saboteur).
 *
 * TOR's Guesser builds its role grid from RoleInfo.allRoleInfos and decides a correct hit by REFERENCE
 * comparison: getRoleInfoForPlayer(target, false).First() == the grid's roleInfo (MeetingPatch.cs). A
 * role only appears if it is in allRoleInfos AND not present in the spawn-settings dictionaries with a
 * rate of 0; our roles aren't in those dicts, so the rate gate is enforced here instead.
 *
 * Therefore, for the duration of a MEETING (when the guess grid is built), we insert the SAME RoleInfo
 * instances the role tags use - Tesla.TeslaInfo() / Saboteur.SaboteurInfo() - so a guess on the actual
 * Tesla/Saboteur matches by reference, and a wrong guess misfires. They are inserted right AFTER the
 * base Impostor entry so they sort with the impostor roles (not dumped at the end), and removed again
 * when the meeting closes. Gated on rate > 0 so guessing them never leaks whether one is in the game.
 *
 * This is separate from UCRoleDraft (which uses its own red, ImpostorRed-coloured entries needed for the
 * draft's faction filter, only during the intro).
 */

using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    public static class UCGuesser {
        private static bool TeslaGuessable() =>
            Tesla.SpawnRate != null && Tesla.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();

        private static bool SaboteurGuessable() =>
            Saboteur.SpawnRate != null && Saboteur.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();

        private static void Sync(bool add) {
            SetEntry(Tesla.TeslaInfo(), add && TeslaGuessable());
            SetEntry(Saboteur.SaboteurInfo(), add && SaboteurGuessable());
        }

        private static void SetEntry(RoleInfo ri, bool want) {
            try {
                bool has = RoleInfo.allRoleInfos.Contains(ri);
                if (want && !has) {
                    int idx = RoleInfo.allRoleInfos.IndexOf(RoleInfo.impostor);
                    if (idx < 0) RoleInfo.allRoleInfos.Add(ri);
                    else RoleInfo.allRoleInfos.Insert(idx + 1, ri); // sort with the impostor roles
                } else if (!want && has) {
                    RoleInfo.allRoleInfos.Remove(ri);
                }
            } catch (System.Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCGuesser] SetEntry failed: {e}");
            }
        }

        // Present the entries while a meeting (and thus the guess grid) is open; remove on close.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        [HarmonyPriority(Priority.First)] // before the guess UI reads allRoleInfos
        static class MeetingStartPatch {
            public static void Postfix() { Sync(true); }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        static class MeetingClosePatch {
            public static void Postfix() { Sync(false); }
        }

        // Safety: also drop them on a full reset, so they never linger into other systems.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { Sync(false); }
        }
    }
}
