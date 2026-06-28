// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Role-Draft integration for the Unknown's Collection impostor roles (Tesla, Saboteur).
 *
 * TOR's RoleDraft lets each player pick a role from RoleInfo.allRoleInfos, filtered by faction and
 * spawn settings, and assigns the pick via RPCProcedure.setRole(roleId, ...). Our roles are NOT real
 * RoleIds - they are tags layered over a plain Impostor and are normally chosen by a random promotion
 * at IntroCutscene.OnDestroy. To make them DRAFTABLE without touching TOR source we:
 *
 *   1. add lightweight "draft entries" (own RoleInfo, ImpostorRed so the faction filter shows them to
 *      impostors, with a sentinel RoleId 200/201) to RoleInfo.allRoleInfos for the duration of the
 *      intro (only when the option is enabled);
 *   2. intercept RPCProcedure.setRole for those sentinel ids (a prefix that marks the player as Tesla/
 *      Saboteur instead of running TOR's switch). setRole runs on every client via the draft RPC, so
 *      the mark is naturally consistent everywhere - no extra sync needed; and
 *   3. suppress the random promotion while the draft runs (the draft decides instead).
 *
 * The entries are removed again at the end of the intro so they never leak into in-game systems (the
 * Guesser list, end screen, ...).
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    public static class UCRoleDraft {
        // Sentinel RoleId bytes - TOR's RoleId enum only runs 0..56, so 200/201 are free and stable.
        public const byte TeslaDraftId = 200;
        public const byte SaboteurDraftId = 201;

        private static RoleInfo teslaDraft, saboteurDraft;
        private static RoleInfo TeslaDraft() => teslaDraft ??= new RoleInfo(
            "Tesla", Palette.ImpostorRed, "Charge two players and bring them together",
            "Charge two players and bring them together", (RoleId)TeslaDraftId);
        private static RoleInfo SaboteurDraft() => saboteurDraft ??= new RoleInfo(
            "Saboteur", Palette.ImpostorRed, "Sabotage a task or lay a trap",
            "Sabotage a task or lay a trap", (RoleId)SaboteurDraftId);

        // Whether the Role Draft is the active assignment path. TOR's RoleDraft.isEnabled also requires a
        // Classic/Guesser gamemode, but TORMapOptions is internal; the draft option alone is sufficient
        // here (the other gamemodes don't assign TOR roles, so our roles simply won't appear there).
        public static bool DraftWillRun() {
            try { return CustomOptionHolder.isDraftMode != null && CustomOptionHolder.isDraftMode.getBool(); }
            catch { return false; }
        }

        // Connected (non-disconnected) players - the same lobby-size measure the random promotion uses.
        private static int PlayerCount() {
            int n = 0;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
                if (p != null && p.Data != null && !p.Data.Disconnected) n++;
            return n;
        }

        // A role is draftable only when its full spawn gate is met: enabled, everyone has the mod, AND
        // the lobby is at least its "Minimum Players To Spawn" - so the draft honours the same gates as
        // the normal random promotion (which is disabled while the draft runs).
        private static bool TeslaDraftable() =>
            DraftWillRun() && TeslaVersionHandshake.EveryoneHasMod()
            && Tesla.SpawnRate != null && Tesla.SpawnRate.getSelection() > 0
            && PlayerCount() >= (Tesla.SpawnMinPlayers != null ? Tesla.SpawnMinPlayers.getFloat() : 6f);

        private static bool SaboteurDraftable() =>
            DraftWillRun() && TeslaVersionHandshake.EveryoneHasMod()
            && Saboteur.SpawnRate != null && Saboteur.SpawnRate.getSelection() > 0
            && PlayerCount() >= (Saboteur.SpawnMinPlayers != null ? Saboteur.SpawnMinPlayers.getFloat() : 6f);

        // Add/remove each entry from the draft list to match its current draftability.
        private static void SyncEntries() {
            SetEntry(TeslaDraft(), TeslaDraftable(), "Tesla");
            SetEntry(SaboteurDraft(), SaboteurDraftable(), "Saboteur");
        }

        private static void SetEntry(RoleInfo ri, bool want, string name) {
            try {
                bool has = RoleInfo.allRoleInfos.Contains(ri);
                if (want && !has) {
                    RoleInfo.allRoleInfos.Add(ri);
                    UnknownsCollectionPlugin.Logger?.LogInfo($"[UCRoleDraft] {name} added to draft list.");
                } else if (!want && has) {
                    RoleInfo.allRoleInfos.Remove(ri);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCRoleDraft] SetEntry({name}) failed: {e}");
            }
        }

        // Unconditionally drop both entries (end of intro / reset).
        private static void RemoveAll() {
            try {
                if (teslaDraft != null) RoleInfo.allRoleInfos.Remove(teslaDraft);
                if (saboteurDraft != null) RoleInfo.allRoleInfos.Remove(saboteurDraft);
            } catch { }
        }

        // The Role Draft keys availability AND the 100%-forcing logic on RoleManagerSelectRolesPatch's
        // impSettings (RoleId -> rate). Our sentinel roles aren't there by default, so the draft treated
        // them as ordinary options (no rate, no forcing) and the random "N of M" trim could drop them.
        // This postfix injects their spawn rate so the draft respects the configured rate, including a
        // 100% force. Reflection-based because RoleManagerSelectRolesPatch is internal to TOR.
        public static void PatchDraftData(Harmony harmony) {
            try {
                var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.RoleManagerSelectRolesPatch");
                var m = t?.GetMethod("getRoleAssignmentData", BindingFlags.Public | BindingFlags.Static);
                if (m == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning("[UCRoleDraft] getRoleAssignmentData not found - draft rate injection disabled.");
                    return;
                }
                var post = typeof(UCRoleDraft).GetMethod(nameof(InjectDraftRates), BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(m, postfix: new HarmonyMethod(post));
                UnknownsCollectionPlugin.Logger?.LogInfo("[UCRoleDraft] Draft rate injection patched.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCRoleDraft] PatchDraftData failed: {e}");
            }
        }

        // Postfix on RoleManagerSelectRolesPatch.getRoleAssignmentData. __result is the (internal-nested)
        // RoleAssignmentData; its public impSettings field is a managed Dictionary<byte,int> we can edit.
        public static void InjectDraftRates(object __result) {
            try {
                if (__result == null || !DraftWillRun()) return;
                var field = __result.GetType().GetField("impSettings");
                if (field?.GetValue(__result) is not Dictionary<byte, int> imp) return;
                SyncEntries(); // keep allRoleInfos membership in step with the gates
                // Inject the rate only for a draftable role; otherwise make sure it's absent so the draft
                // never offers it (mirrors the spawn gate of the random promotion).
                if (TeslaDraftable()) imp[TeslaDraftId] = Tesla.SpawnRate.getSelection();
                else imp.Remove(TeslaDraftId);
                if (SaboteurDraftable()) imp[SaboteurDraftId] = Saboteur.SpawnRate.getSelection();
                else imp.Remove(SaboteurDraftId);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCRoleDraft] InjectDraftRates failed: {e}");
            }
        }

        // Add the draft entries just before the team/role-draft intro builds its role list.
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.ShowTeam))]
        static class ShowTeamPatch {
            public static void Prefix() { if (DraftWillRun()) SyncEntries(); }
        }

        // Remove them once the intro ends, so they never leak into in-game systems.
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.First)] // before the Tesla/Saboteur random-pick postfixes
        static class OnDestroyPatch {
            public static void Postfix() { RemoveAll(); }
        }

        // Safety: also drop them on a full reset.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { RemoveAll(); }
        }

        // Intercept the draft pick for our sentinel roles: mark the player as Tesla/Saboteur instead of
        // running TOR's setRole switch (which has no case for these ids). Runs on every client.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.setRole))]
        [HarmonyPriority(Priority.High)]
        static class SetRolePatch {
            public static bool Prefix(byte roleId, byte playerId) {
                if (roleId == TeslaDraftId) { Tesla.MarkFromDraft(playerId); return false; }
                if (roleId == SaboteurDraftId) { Saboteur.MarkFromDraft(playerId); return false; }
                return true;
            }
        }
    }
}
