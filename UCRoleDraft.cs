// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Role-Draft integration for the Unknown's Collection roles.
 *
 * TOR's RoleDraft lets each player pick a role from RoleInfo.allRoleInfos, filtered by FACTION and the
 * spawn settings, then assigns the pick via RPCProcedure.setRole(roleId, ...). Our roles are NOT real
 * RoleIds - they are tags layered over a plain Impostor / Crewmate, normally chosen by a random
 * promotion at IntroCutscene.OnDestroy. To make them DRAFTABLE without touching TOR source we:
 *
 *   1. add lightweight "draft entries" (own RoleInfo, sentinel RoleId 200-206) to RoleInfo.allRoleInfos
 *      for the duration of the intro, only while each role's full spawn gate is met. The RoleInfo COLOR
 *      decides the draft faction (RoleInfo.isImpostor == color == Palette.ImpostorRed), so impostor
 *      entries use ImpostorRed and crew entries use their own (non-red) colour -> they are offered to
 *      crewmates, not impostors;
 *   2. inject each entry's spawn rate into the draft's imp/crew settings (reflection, the patch class is
 *      internal to TOR) so the draft's availability + 100%-forcing maths respect the configured rate;
 *   3. intercept RPCProcedure.setRole for the sentinel ids (a prefix that marks the player as the role
 *      instead of running TOR's switch). setRole runs on every client via the draft RPC, so the mark is
 *      consistent everywhere - no extra sync needed; and
 *   4. suppress the random promotion while the draft runs (each role's IntroEnd checks DraftWillRun()).
 *
 * The entries are removed again at the end of the intro so they never leak into in-game systems.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    public static class UCRoleDraft {
        // Sentinel RoleId bytes - TOR's RoleId enum only runs 0..56, so 200-206 are free and stable.
        public const byte TeslaDraftId = 200;
        public const byte SaboteurDraftId = 201;
        public const byte PoisonerDraftId = 202;
        public const byte SilencerDraftId = 203;
        public const byte IllusionistDraftId = 204;
        public const byte SiphonerDraftId = 205;
        public const byte WitnessDraftId = 206;
        public const byte BugDraftId = 207;
        public const byte ManiacDraftId = 208;
        public const byte FollowerDraftId = 209;
        public const byte ShadeDraftId = 210;
        public const byte CopycatDraftId = 211;
        public const byte ScoutDraftId = 212;
        public const byte BeaconDraftId = 213;
        public const byte CollectorDraftId = 214;
        public const byte ManipulatorDraftId = 215;

        // ---- Draft entry table (all UC roles) ----
        private class Entry {
            public byte id;
            public RoleInfo info;
            public bool impostor;             // imp faction (ImpostorRed) vs crew faction
            public Func<CustomOption> rateOpt;
            public Func<CustomOption> minOpt;
            public Action<byte> mark;
        }

        private static List<Entry> entries;
        private static List<Entry> Entries() {
            if (entries != null) return entries;
            entries = new List<Entry> {
                Make(TeslaDraftId,       "Tesla",       Palette.ImpostorRed, "Charge two players and bring them together",
                     true,  () => Tesla.SpawnRate,       () => Tesla.SpawnMinPlayers,       Tesla.MarkFromDraft),
                Make(SaboteurDraftId,    "Saboteur",    Palette.ImpostorRed, "Sabotage a task or lay a trap",
                     true,  () => Saboteur.SpawnRate,    () => Saboteur.SpawnMinPlayers,    Saboteur.MarkFromDraft),
                Make(PoisonerDraftId,   "Poisoner",   Palette.ImpostorRed, "Your kills poison the reporter; the Medic can save them",
                     true,  () => Poisoner.SpawnRate,   () => Poisoner.SpawnMinPlayers,   Poisoner.MarkFromDraft),
                Make(SilencerDraftId,    "Silencer",    Palette.ImpostorRed, "Mute a player for the next meeting",
                     true,  () => Silencer.SpawnRate,    () => Silencer.SpawnMinPlayers,    Silencer.MarkFromDraft),
                Make(IllusionistDraftId, "Illusionist", Palette.ImpostorRed, "Record a path and replay it as an unkillable clone",
                     true,  () => Illusionist.SpawnRate, () => Illusionist.SpawnMinPlayers, Illusionist.MarkFromDraft),
                Make(SiphonerDraftId,    "Siphoner",    Siphoner.Color,      "Drain the Impostor's kill power by standing near them",
                     false, () => Siphoner.SpawnRate,    () => Siphoner.SpawnMinPlayers,    Siphoner.MarkFromDraft),
                Make(WitnessDraftId,     "Witness",     Witness.Color,       "Be the sole witness of a kill and expose the killer",
                     false, () => Witness.SpawnRate,     () => Witness.SpawnMinPlayers,     Witness.MarkFromDraft),
                Make(BugDraftId,         "Bug",         Bug.Color,           "Survive until the end and win with the winning team",
                     false, () => Bug.SpawnRate,         () => Bug.SpawnMinPlayers,         Bug.MarkFromDraft),
                Make(ManiacDraftId,      "Maniac",      Palette.ImpostorRed, "Plant a bomb on a player that can be passed",
                     true,  () => Maniac.SpawnRate,      () => Maniac.SpawnMinPlayers,      Maniac.MarkFromDraft),
                Make(FollowerDraftId,    "Follower",    Follower.Color,      "Copy the role of the first player to die",
                     false, () => Follower.SpawnRate,    () => Follower.SpawnMinPlayers,    Follower.MarkFromDraft),
                Make(ShadeDraftId,       "Shade",       Palette.ImpostorRed, "Victim's body vanishes; others can find it by proximity",
                     true,  () => Shade.SpawnRate,       () => Shade.SpawnMinPlayers,       Shade.MarkFromDraft),
                Make(CopycatDraftId,     "Copycat",     Copycat.Color,       "Copy abilities you witness and win with the winners",
                     false, () => Copycat.SpawnRate,     () => Copycat.SpawnMinPlayers,     Copycat.MarkFromDraft),
                Make(ScoutDraftId,       "Scout",       Scout.Color,         "Go transparent and fast; lights don't affect you",
                     false, () => Scout.SpawnRate,       () => Scout.SpawnMinPlayers,       Scout.MarkFromDraft),
                Make(BeaconDraftId,      "Beacon",      Beacon.Color,        "Lights never affect you; crew shares your vision",
                     false, () => Beacon.SpawnRate,      () => Beacon.SpawnMinPlayers,      Beacon.MarkFromDraft),
                Make(CollectorDraftId,   "Collector",   Collector.Color,     "Find and collect the hidden relics to win alone",
                     false, () => Collector.SpawnRate,   () => Collector.SpawnMinPlayers,   Collector.MarkFromDraft),
                Make(ManipulatorDraftId, "Manipulator", Palette.ImpostorRed, "Make the ship's security devices lie",
                     true,  () => Manipulator.SpawnRate, () => Manipulator.SpawnMinPlayers, Manipulator.MarkFromDraft),
            };
            return entries;
        }

        private static Entry Make(byte id, string name, UnityEngine.Color color, string desc, bool impostor,
                                  Func<CustomOption> rateOpt, Func<CustomOption> minOpt, Action<byte> mark) {
            return new Entry {
                id = id,
                info = new RoleInfo(name, color, desc, desc, (RoleId)id),
                impostor = impostor,
                rateOpt = rateOpt,
                minOpt = minOpt,
                mark = mark,
            };
        }

        // Whether the Role Draft is the active assignment path (the draft option alone is sufficient
        // here; other gamemodes don't assign TOR roles, so our roles simply won't appear there).
        public static bool DraftWillRun() {
            try { return CustomOptionHolder.isDraftMode != null && CustomOptionHolder.isDraftMode.getBool(); }
            catch { return false; }
        }

        private static int PlayerCount() {
            int n = 0;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
                if (p != null && p.Data != null && !p.Data.Disconnected) n++;
            return n;
        }

        // A role is draftable only when its full spawn gate is met: draft on, everyone has the mod,
        // rate > 0, AND the lobby is at least its "Minimum Players To Spawn".
        private static bool Draftable(Entry e) {
            var rate = e.rateOpt();
            var min = e.minOpt();
            return DraftWillRun() && TeslaVersionHandshake.EveryoneHasMod()
                   && rate != null && rate.getSelection() > 0
                   && PlayerCount() >= (min != null ? min.getFloat() : 6f);
        }

        // Add/remove each entry from the draft list to match its current draftability.
        private static void SyncEntries() {
            foreach (var e in Entries()) SetEntry(e.info, Draftable(e), e.info.name);
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

        // Unconditionally drop all entries (end of intro / reset).
        private static void RemoveAll() {
            try {
                foreach (var e in Entries())
                    if (e.info != null) RoleInfo.allRoleInfos.Remove(e.info);
            } catch { }
        }

        // The Role Draft keys availability AND the 100%-forcing logic on RoleManagerSelectRolesPatch's
        // imp/crew/neutral settings (RoleId -> rate). Our sentinel roles aren't there by default, so this
        // postfix injects their spawn rate into the matching faction dictionary (imp for impostor roles,
        // crew for crew roles), including a 100% force. Reflection-based: the patch class is internal.
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
        // RoleAssignmentData; its public impSettings / crewSettings fields are managed dictionaries.
        //
        // IMPORTANT: this postfix must NEVER touch RoleInfo.allRoleInfos (Add/Remove). getRoleAssignmentData()
        // is called by TOR from inside "foreach (RoleInfo roleInfo in RoleInfo.allRoleInfos)" loops - both
        // the draft's own pick loop (RoleDraft.CoSelectRoles) and the Guesser shot-list build (guesserOnClick,
        // MeetingPatch.cs) call it once per iterated role. Mutating allRoleInfos here (as SyncEntries() used
        // to) throws "Collection was modified" on whichever loop happens to be running. impSettings/crewSettings
        // however are plain local dictionaries freshly created per call, not the enumerated collection, so
        // mutating THEM is always safe. Membership in allRoleInfos is instead kept in sync exactly once, from
        // ShowTeamPatch.Prefix below - that runs before IntroCutscene.ShowTeam's body (and therefore strictly
        // before RoleDraft's pick loop starts enumerating), so it never overlaps a live foreach over the list.
        public static void InjectDraftRates(object __result) {
            try {
                if (__result == null || !DraftWillRun()) return;
                var impField = __result.GetType().GetField("impSettings");
                var crewField = __result.GetType().GetField("crewSettings");
                var imp = impField?.GetValue(__result) as Dictionary<byte, int>;
                var crew = crewField?.GetValue(__result) as Dictionary<byte, int>;
                if (imp == null && crew == null) return;

                foreach (var e in Entries()) {
                    var dict = e.impostor ? imp : crew;
                    if (dict == null) continue;
                    if (Draftable(e)) dict[e.id] = e.rateOpt().getSelection();
                    else dict.Remove(e.id);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCRoleDraft] InjectDraftRates failed: {e}");
            }
        }

        // Add the draft entries just before the team/role-draft intro builds its role list. This is the
        // ONLY place allRoleInfos membership is synced: it runs as a Prefix, i.e. before ShowTeam's body
        // (and thus before RoleDraft's postfix-chained CoSelectRoles coroutine even exists), so there is
        // no live "foreach (... in RoleInfo.allRoleInfos)" it could ever collide with.
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.ShowTeam))]
        static class ShowTeamPatch {
            public static void Prefix() { if (DraftWillRun()) SyncEntries(); }
        }

        // Remove them once the intro ends (after CoSelectRoles has finished enumerating and returned), so
        // they never leak into in-game systems.
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.First)] // before the role random-pick postfixes
        static class OnDestroyPatch {
            public static void Postfix() { RemoveAll(); }
        }

        // Safety: also drop them on a full reset.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { RemoveAll(); }
        }

        // Intercept the draft pick for our sentinel roles: mark the player as that UC role instead of
        // running TOR's setRole switch (which has no case for these ids). Runs on every client.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.setRole))]
        [HarmonyPriority(Priority.High)]
        static class SetRolePatch {
            public static bool Prefix(byte roleId, byte playerId) {
                foreach (var e in Entries())
                    if (roleId == e.id) { e.mark(playerId); return false; }
                return true;
            }
        }
    }
}
