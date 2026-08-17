// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCLabelThrottle - one shared gate for dynamic CustomButton labels (AUDIT-2026-08-16).
 *
 * WHY
 * ---
 * Several roles rebuild their button caption on every single HudManager.Update frame: a
 * UCLocalization.Tr() lookup plus a string.Format (params array), and in the Necromancer's case an
 * entire LINQ count that resolves every thrall through Helpers.playerById (a linear scan over
 * AllPlayerControls, so O(thralls x players) per frame). The displayed value changes at most a few
 * times a second, so the vast majority of that work is thrown away immediately.
 *
 * Poltergeist got a hand-written version of this fix first (Poltergeist.cs, the three ghost
 * buttons). This class is that same idea in one place, so the remaining call sites do not each
 * grow their own set of "last value" fields.
 *
 * WHY A PLAIN TIME GATE AND NOT CHANGE DETECTION
 * ---------------------------------------------
 * Change detection needs the value BEFORE it can compare, which for the Necromancer means paying
 * the expensive count anyway - the exact cost we are trying to avoid. A time gate skips the whole
 * block instead. Four repaints a second is invisible for HUD text, and it keeps a mid-round
 * language switch working: UCLocalization polls every 0.5s and dynamic strings only pick a new
 * language up by calling Tr() again, so a caption that repaints purely on value change would stay
 * in the old language for as long as its value happens to hold (Poltergeist.cs documents the same
 * trap).
 *
 * USAGE
 *   if (UCLabelThrottle.Due("necromancer.army")) { btn.buttonText = UCLocalization.Tr(...); }
 *
 * The key is a caller-chosen constant string, one per label. Keys are never player- or round-
 * specific, so a stale entry can only ever cause one extra repaint - but the table is still cleared
 * on round reset and lobby change, because "static state that outlives a lobby" is a mistake this
 * project has made often enough to make the rule unconditional.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UnknownsCollection {
    public static class UCLabelThrottle {
        // 4 repaints per second. Fast enough that no one can see the difference on a caption,
        // slow enough to remove ~95% of the work on a 60fps client.
        private const float Interval = 0.25f;

        private static readonly Dictionary<string, float> lastPaint = new Dictionary<string, float>();

        // True at most once per Interval per key. Records the timestamp when it returns true, so a
        // caller that skips the repaint does not push the next one further out.
        public static bool Due(string key) {
            try {
                float now = Time.time;
                if (lastPaint.TryGetValue(key, out float last) && now - last < Interval) return false;
                lastPaint[key] = now;
                return true;
            } catch {
                return true; // never suppress a label because the throttle itself failed
            }
        }

        // Forces the next Due() for every key. Called on round reset and lobby change below, and
        // usable directly by a role that rebuilds its buttons and wants the first paint immediately.
        public static void Clear() {
            try { lastPaint.Clear(); } catch { }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class RoundResetPatch {
            public static void Postfix() => UCResetGuard.Run("UCLabelThrottle", Clear);
        }

        // PlayerIds and button objects are per-lobby; a timestamp from the previous lobby would at
        // worst delay one repaint, but the rule here is unconditional (see the file header).
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("UCLabelThrottle", Clear);
        }
    }
}
