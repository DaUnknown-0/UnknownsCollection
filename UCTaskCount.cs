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
 */

using System.Collections.Generic;
using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    internal static class UCTaskCount {
        private static readonly HashSet<byte> excluded = new HashSet<byte>();

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

        /// <summary>Takes this player's tasks out of the totals, once per recount.</summary>
        internal static void Subtract(GameData gd, NetworkedPlayerInfo data) {
            if (gd == null || data == null || !excluded.Add(data.PlayerId)) return;
            var info = TasksHandler.taskInfo(data);   // (completed, total)
            gd.TotalTasks -= info.Item2;
            gd.CompletedTasks -= info.Item1;
        }
    }
}
