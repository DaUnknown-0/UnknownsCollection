// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCVision - the single CalculateLightRadius postfix of Unknown's Collection.
 *
 * WHY
 * ---
 * Scout, Beacon, Poltergeist and Werewolf each used to hang their own postfix on
 * ShipStatus.CalculateLightRadius, all with default priority and several of them writing __result
 * ABSOLUTELY (`__result = MaxLightRadius * CrewLightMod`) instead of composing. With two of them
 * active on the same player the outcome depended purely on Harmony's patch order, which is undefined
 * between patch classes - a configured feature would silently do nothing, and which one lost changed
 * with the load order. See AUDIT-2026-08-11.md, M-5.
 *
 * Chance (ChanceMod, a separate assembly with no reference to us) stays its own postfix, pinned to
 * Priority.Last so it runs after this pipeline. That is safe because its contribution is purely
 * multiplicative (`__result * vis`) and therefore order-independent - it needs to be after us, not
 * inside us. This is the "Option A" decision from M5_ENTSCHEIDUNG_ERWARTET.txt.
 *
 * ORDER (and why)
 * ---------------
 *   0. TOR's own result comes in. (TOR patches CalculateLightRadius with a PREFIX that returns false,
 *      so a postfix is the only place that can have the last word - see Werewolf.cs's original note.)
 *   1. Multiplicative dampers   - Poltergeist's Blind hex (x0.35). First, so it scales the honest
 *                                 base value rather than a granted full-vision radius.
 *   2. Full-vision grants       - Scout ability, Beacon (self + nearby crew with line of sight),
 *                                 Poltergeist's Night Vision hex. Applied as Mathf.Max, NEVER as a
 *                                 hard assignment: that alone makes them commutative among each
 *                                 other, so their relative order stops mattering at all.
 *   3. Werewolf night           - a whole-map lighting REGIME, not a per-role bonus: while it is up
 *                                 every player's radius is redefined. Runs last and overwrites the
 *                                 grants above on purpose; a Scout lighting up the map would defeat
 *                                 the entire point of the night.
 *   4. (Chance, Priority.Last)  - multiplicative, outside this file.
 *
 * Adding a new vision feature: put a predicate on the role (like Scout.WantsFullVision) and call it
 * from the matching stage below - do NOT add another CalculateLightRadius patch.
 */

using System;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class UCVision {
        // The radius a fully-lit crewmate has. Every "full vision" grant resolves to this, so the
        // grants can be combined with Max() instead of fighting over an assignment.
        private static float FullCrewRadius(ShipStatus ship) =>
            ship.MaxLightRadius * GameOptionsManager.Instance.currentNormalGameOptions.CrewLightMod;

        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.CalculateLightRadius))]
        static class Pipeline {
            public static void Postfix(ref float __result, ShipStatus __instance,
                                       [HarmonyArgument(0)] NetworkedPlayerInfo p) {
                try {
                    if (p == null || __instance == null) return;

                    // --- 1. multiplicative dampers -------------------------------------------------
                    float damp = Poltergeist.VisionDamp(p);
                    if (damp != 1f) __result *= damp;

                    // --- 2. full-vision grants (Max, so order among them is irrelevant) ------------
                    if (Scout.WantsFullVision(p)
                        || Beacon.WantsFullVision(p)
                        || Poltergeist.WantsFullVision(p)) {
                        __result = Mathf.Max(__result, FullCrewRadius(__instance));
                    }

                    // --- 3. Werewolf night: total override, last word inside UC --------------------
                    Werewolf.ApplyNightOverride(ref __result, __instance, p);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCVision] pipeline failed: {e}");
                }
            }
        }
    }
}
