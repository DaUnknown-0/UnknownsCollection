// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCAirshipWrapUp (review 2026-10-02).
 *
 * AirshipExileController.WrapUpAndSpawn is a COROUTINE: calling it only builds the enumerator, so a
 * postfix runs before its body exiles anyone. Logic that needs the post-exile state (the Mixer swap
 * skips dead players, but saw the exiled one still alive) arms its action here instead; it runs once
 * the controller is destroyed, the last step of the coroutine and the moment the regular
 * ExileController.WrapUp postfix stands for. A give-up timer keeps a stalled cutscene from
 * swallowing it. Hooks that receive the exiled player explicitly (Follower, Saboteur, Poltergeist)
 * do not need this.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UnknownsCollection {
    internal static class UCAirshipWrapUp {
        private const float GiveUpSeconds = 30f;

        private sealed class Entry {
            public AirshipExileController Controller;
            public float GiveUpAt;
            public string Name;
            public Action Action;
        }

        private static readonly List<Entry> pending = new List<Entry>();

        internal static void Arm(AirshipExileController controller, string name, Action action) {
            if (action == null) return;
            pending.Add(new Entry {
                Controller = controller,
                GiveUpAt = Time.unscaledTime + GiveUpSeconds,
                Name = name,
                Action = action,
            });
        }

        internal static void Clear() => pending.Clear();

        private static bool Finished(Entry e) {
            try {
                if (e.Controller == null) return true;   // Unity null: the coroutine destroyed it
            } catch {
                return true;
            }
            return Time.unscaledTime >= e.GiveUpAt;
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                if (pending.Count == 0) return;
                for (int i = 0; i < pending.Count; ) {
                    var e = pending[i];
                    if (!Finished(e)) { i++; continue; }
                    pending.RemoveAt(i);
                    try {
                        e.Action();
                    } catch (Exception ex) {
                        UnknownsCollectionPlugin.Logger?.LogError($"[AirshipWrapUp] deferred {e.Name} failed: {ex}");
                    }
                }
            }
        }

        // A game that ends during the Airship exile must not carry the action into the next one.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch {
            public static void Prefix() => Clear();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinedPatch {
            public static void Postfix() => Clear();
        }
    }
}
