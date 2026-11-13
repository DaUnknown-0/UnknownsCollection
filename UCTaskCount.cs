// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UCTaskCount - one bookkeeping for every UC role whose tasks must not count toward the crew's task win.
 *
 * Nine roles (Bug, Collector, Copycat, Follower, King, Necromancer, Pelican, Poltergeist, Stalker) each
 * had their own RecomputeTaskCounts postfix that subtracted "their" player's tasks. Nobody checked
 * whether that player was already out: TOR's own prefix skips Lovers with a living killing partner,
 * the Lawyer, a dead Pursuer and the Thief, and the Poltergeist overlay sits on top of other roles. A
 * player could be subtracted twice, TotalTasks fell below the real crew total and TOR's host ended the
 * round as HumansByTask with crew tasks still open (Opus audit round 2, 2026-10-02).
 *
 * A first-in-line prefix starts every pass with TOR's own exclusions; Subtract removes a player only
 * once per pass.
 *
 * Ordered re-completions: several host features rebuild a player's task list (RpcSetTasks) and let the
 * player re-complete what was done before (Auditor revert, Gambler penalty, PlayerTuning callers in
 * other mods). Each feature kept its own list, so one feature's re-completions looked like fresh crew
 * progress to the other (a Gambler task bet won by an Auditor revert, an Auditor queue filled by a
 * Gambler penalty). Every rebuild now also registers here; CompleteTask consumers skip registered ids,
 * and a last-in-line postfix consumes them.
 */

using System.Collections.Generic;
using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    public static class UCTaskCount {
        private static readonly HashSet<byte> excluded = new HashSet<byte>();

        // Host: player -> task ids whose next completion was ordered by a task-list rebuild.
        private static readonly Dictionary<byte, HashSet<uint>> ordered = new Dictionary<byte, HashSet<uint>>();

        /// <summary>
        /// Host: the player will re-complete these task ids after a task-list rebuild. Their completions
        /// are bookkeeping, not crew progress, for every CompleteTask consumer.
        /// </summary>
        public static void ExpectRecomplete(byte pid, IEnumerable<uint> ids) {
            if (ids == null) return;
            if (!ordered.TryGetValue(pid, out var set)) ordered[pid] = set = new HashSet<uint>();
            foreach (uint id in ids) set.Add(id);
        }

        /// <summary>Host: this task stays open after a rebuild, its next completion is real again.</summary>
        internal static void CancelRecomplete(byte pid, uint id) {
            if (ordered.TryGetValue(pid, out var set) && set.Remove(id) && set.Count == 0) ordered.Remove(pid);
        }

        internal static bool IsOrderedRecomplete(byte pid, uint idx) =>
            ordered.TryGetValue(pid, out var set) && set.Contains(idx);

        // Runs after every consumer's postfix (they only look), then drops the id.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CompleteTask))]
        static class OrderedConsumePatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] uint idx) {
                if (__instance != null) CancelRecomplete(__instance.PlayerId, idx);
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class OrderedResetPatch {
            public static void Postfix() => ordered.Clear();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class OrderedLobbyResetPatch {
            public static void Postfix() => ordered.Clear();
        }

        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class PassStartPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix() {
                excluded.Clear();
                try {
                    foreach (var info in GameData.Instance.AllPlayers) {
                        if (info == null) continue;
                        // TOR's own skip list (TasksHandler.cs GameDataRecomputeTaskCountsPatch)
                        bool tor = (info.Object && info.Object.hasAliveKillingLover())
                                   || info.PlayerId == Lawyer.lawyer?.PlayerId
                                   || (info.PlayerId == Pursuer.pursuer?.PlayerId && Pursuer.pursuer.Data.IsDead)
                                   || info.PlayerId == Thief.thief?.PlayerId;
                        if (tor) excluded.Add(info.PlayerId);
                    }
                } catch { }
            }
        }

        /// <summary>
        /// Do this player's tasks count toward the crew's task bar right now? Runs one recount (cheap,
        /// idempotent: GameData's totals are rebuilt from the player data) so every exclusion of this
        /// pass is known: TOR's own (killing Lover, Lawyer, dead Pursuer, Thief, fake-task roles,
        /// impostors) and every UC role that subtracts itself (Bug, Collector, Copycat, Follower, King,
        /// Necromancer, Pelican, Poltergeist, Stalker). The Auditor harvests only these (04.10.).
        /// </summary>
        internal static bool CountsTowardBar(NetworkedPlayerInfo data) {
            try {
                if (data == null || GameData.Instance == null) return false;
                GameData.Instance.RecomputeTaskCounts();
                if (excluded.Contains(data.PlayerId)) return false;
                return TasksHandler.taskInfo(data).Item2 > 0;
            } catch { return false; }
        }

        /// <summary>
        /// Completed tasks of LIVING players whose tasks count toward the bar, with the same exclusions
        /// as CountsTowardBar (one recount for all players instead of one per player).
        /// </summary>
        internal static int CompletedByLiving() {
            try {
                var gd = GameData.Instance;
                if (gd == null) return 0;
                gd.RecomputeTaskCounts();
                int done = 0;
                foreach (var pi in gd.AllPlayers) {
                    if (pi == null || pi.Disconnected || pi.IsDead || excluded.Contains(pi.PlayerId)) continue;
                    done += TasksHandler.taskInfo(pi).Item1;
                }
                return done;
            } catch { return 0; }
        }

        /// <summary>Takes this player's tasks out of the totals, once per recount.</summary>
        internal static void Subtract(GameData gd, NetworkedPlayerInfo data) {
            if (gd == null || data == null || !excluded.Add(data.PlayerId)) return;
            var info = TasksHandler.taskInfo(data);   // (completed, total)
            gd.TotalTasks -= info.Item2;
            gd.CompletedTasks -= info.Item1;
        }
    }
}
