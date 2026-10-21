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
 *
 * Audit 21.10.: isKiller is a small managed TOR method that TOR calls every frame once the Snitch is
 * revealed, so .NET tiering can drop the postfix exactly when it matters. Two state fallbacks on Il2Cpp
 * methods (stable) cover what the players see: the passive neutral's own "Snitch is alive" warning is
 * hidden, and on the Snitch's map the dots of passive neutrals are switched off (TOR only enables a dot
 * when it creates it and afterwards just moves it).
 */

using System.Collections.Generic;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

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

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class WarningFallbackPatch {
            public static void Postfix() {
                try {
                    if (Snitch.text == null || Snitch.targets != Snitch.Targets.Killers) return;
                    var lp = PlayerControl.LocalPlayer;
                    if (lp == null || lp.Data == null || lp.Data.Role == null || lp.Data.Role.IsImpostor || !IsPassive(lp)) return;
                    if (Snitch.text.gameObject.activeSelf) Snitch.text.gameObject.SetActive(false);
                } catch { }
            }
        }

        private static readonly System.Reflection.FieldInfo herePointsField =
            AccessTools.Field(AccessTools.TypeByName("TheOtherRoles.Patches.MapBehaviourPatch"), "herePoints");

        [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
        static class MapFallbackPatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix() {
                try {
                    if (herePointsField == null || Snitch.snitch == null || Snitch.targets != Snitch.Targets.Killers) return;
                    var lp = PlayerControl.LocalPlayer;
                    if (lp == null || lp.PlayerId != Snitch.snitch.PlayerId || lp.Data == null || lp.Data.IsDead) return;   // ghosts see everybody
                    if (herePointsField.GetValue(null) is not Dictionary<byte, SpriteRenderer> points || points.Count == 0) return;
                    foreach (var kv in points) {
                        if (kv.Value == null || !kv.Value.enabled) continue;
                        var p = Helpers.playerById(kv.Key);
                        if (p != null && p.Data != null && p.Data.Role != null && !p.Data.Role.IsImpostor && IsPassive(p))
                            kv.Value.enabled = false;
                    }
                } catch { }
            }
        }
    }
}
