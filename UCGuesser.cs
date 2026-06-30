// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Guesser integration for all Unknown's Collection roles.
 *
 * TOR's Guesser builds its role grid from RoleInfo.allRoleInfos and decides a correct hit by REFERENCE
 * comparison: getRoleInfoForPlayer(target, false).First() == the grid's roleInfo (MeetingPatch.cs). A
 * role only appears if it is in allRoleInfos AND not present in the spawn-settings dictionaries with a
 * rate of 0; our roles aren't in those dicts, so the rate gate is enforced here instead.
 *
 * Therefore, for the duration of a MEETING (when the guess grid is built), we insert the SAME RoleInfo
 * instances the role tags use so a guess on the actual role matches by reference, and a wrong guess
 * misfires. Impostor entries are inserted right AFTER the base Impostor entry; crew entries after the
 * base Crewmate entry. Removed again when the meeting closes. Gated on rate > 0 so guessing them never
 * leaks whether one is in the game.
 *
 * This is separate from UCRoleDraft (which uses its own coloured entries needed for the draft's faction
 * filter, only during the intro).
 */

using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    public static class UCGuesser {
        // ---- Guessability gates (rate > 0 + everyone has the mod) ----
        private static bool TeslaGuessable() =>
            Tesla.SpawnRate != null && Tesla.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool SaboteurGuessable() =>
            Saboteur.SpawnRate != null && Saboteur.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool PoisonerGuessable() =>
            Poisoner.SpawnRate != null && Poisoner.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool SilencerGuessable() =>
            Silencer.SpawnRate != null && Silencer.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool IllusionistGuessable() =>
            Illusionist.SpawnRate != null && Illusionist.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool SiphonerGuessable() =>
            Siphoner.SpawnRate != null && Siphoner.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool WitnessGuessable() =>
            Witness.SpawnRate != null && Witness.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool BugGuessable() =>
            Bug.SpawnRate != null && Bug.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool ManiacGuessable() =>
            Maniac.SpawnRate != null && Maniac.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool FollowerGuessable() =>
            Follower.SpawnRate != null && Follower.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool ShadeGuessable() =>
            Shade.SpawnRate != null && Shade.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool CopycatGuessable() =>
            Copycat.SpawnRate != null && Copycat.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool ScoutGuessable() =>
            Scout.SpawnRate != null && Scout.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod();
        private static bool BeaconGuessable() =>
            Beacon.SpawnRate != null && Beacon.SpawnRate.getSelection() > 0
            && TeslaVersionHandshake.EveryoneHasMod()
            && !(Beacon.NotGuessable != null && Beacon.NotGuessable.getBool());

        private static void Sync(bool add) {
            // Impostor roles — insert after the base Impostor entry
            SetEntry(Tesla.TeslaInfo(),       add && TeslaGuessable(),       RoleInfo.impostor);
            SetEntry(Saboteur.SaboteurInfo(), add && SaboteurGuessable(),    RoleInfo.impostor);
            SetEntry(Poisoner.PoisonerInfo(), add && PoisonerGuessable(), RoleInfo.impostor);
            SetEntry(Silencer.SilencerInfo(), add && SilencerGuessable(),   RoleInfo.impostor);
            SetEntry(Illusionist.IllusionistInfo(), add && IllusionistGuessable(), RoleInfo.impostor);
            SetEntry(Maniac.ManiacInfo(),     add && ManiacGuessable(),      RoleInfo.impostor);
            SetEntry(Shade.ShadeInfo(),       add && ShadeGuessable(),        RoleInfo.impostor);
            // Crew / Neutral roles — insert after the base Crewmate entry
            SetEntry(Siphoner.SiphonerInfo(), add && SiphonerGuessable(),   RoleInfo.crewmate);
            SetEntry(Witness.WitnessInfo(),   add && WitnessGuessable(),    RoleInfo.crewmate);
            SetEntry(Bug.BugInfo(),           add && BugGuessable(),        RoleInfo.crewmate);
            SetEntry(Follower.FollowerInfo(), add && FollowerGuessable(),   RoleInfo.crewmate);
            SetEntry(Copycat.CopycatInfo(),   add && CopycatGuessable(),    RoleInfo.crewmate);
            SetEntry(Scout.ScoutInfo(),       add && ScoutGuessable(),      RoleInfo.crewmate);
            SetEntry(Beacon.BeaconInfo(),     add && BeaconGuessable(),     RoleInfo.crewmate);
        }

        private static void SetEntry(RoleInfo ri, bool want, RoleInfo after) {
            try {
                bool has = RoleInfo.allRoleInfos.Contains(ri);
                if (want && !has) {
                    int idx = RoleInfo.allRoleInfos.IndexOf(after);
                    if (idx < 0) RoleInfo.allRoleInfos.Add(ri);
                    else RoleInfo.allRoleInfos.Insert(idx + 1, ri); // sort with the correct faction
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
