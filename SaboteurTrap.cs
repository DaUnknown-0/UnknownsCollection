// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Saboteur trap - an invisible ground trap, modelled on TOR's Objects/Trap.cs (Trapper) but with its
 * own list/state and the Saboteur's rules:
 *   - invisible to everyone except the Saboteur (and other Impostors if the option is on);
 *   - cannot be placed in the same room as the emergency button, the reactor or the O2/LifeSupp system;
 *   - triggered when a valid victim (any non-Impostor; other Impostors only if the option is on; the
 *     Saboteur is always immune) walks into it -> stun (moveable=false + NetTransform.Halt) for the
 *     configured duration, then an optional limp (and a Saboteur self-limp toggle);
 *   - traps are inert below the configured minimum alive-player count, and are all cleared each meeting.
 *
 * Like TOR's Trap, triggering is client-driven: every client checks its OWN local player against the
 * armed traps and, on contact, broadcasts the trigger so the stun is applied consistently everywhere.
 * The RPC plumbing lives in Saboteur.cs (subtypes 6-8) and forwards into the Apply* methods here.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    public class SaboteurTrap {
        public static readonly List<SaboteurTrap> traps = new List<SaboteurTrap>();
        public static int nextId; // saboteur-local counter (only the Saboteur places traps -> unique)

        // After-stun limp schedule (synced because Trigger runs on every client) + saboteur self-limp.
        private static readonly Dictionary<byte, float> limpUntil = new Dictionary<byte, float>();
        private static bool selfLimping;

        public int id;
        public Vector2 pos;
        public GameObject obj;
        public bool armed;
        private readonly HashSet<byte> stunned = new HashSet<byte>();

        private static readonly Color Tint = new Color(0.72f, 0.25f, 1f, 0.85f); // violet, distinct from Trapper

        // ---- placement ---------------------------------------------------------------------------
        public static int ActiveCount => traps.Count;

        // Saboteur-local check: may a trap be placed at the local player's current spot? Forbidden in the
        // emergency-button / reactor / O2 rooms (same-room rule).
        public static bool CanPlaceHere() {
            try {
                var room = HudManager.Instance?.roomTracker?.LastRoom?.RoomId;
                if (room == null) return true; // unknown room (e.g. hallway) -> allowed
                switch (room.Value) {
                    case SystemTypes.Reactor:     // reactor / meltdown
                    case SystemTypes.Laboratory:  // Polus seismic (reactor-equivalent)
                    case SystemTypes.LifeSupp:    // O2
                    case SystemTypes.Cafeteria:   // emergency button (Skeld/Mira/Fungle)
                    case SystemTypes.MeetingRoom: // emergency button (other maps)
                        return false;
                    default:
                        return true;
                }
            } catch { return true; }
        }

        public static void Place(int id, float x, float y) {
            try {
                var t = new SaboteurTrap { id = id, pos = new Vector2(x, y) };

                var go = new GameObject("SaboteurTrap") { layer = 11 };
                go.AddSubmergedComponent(SubmergedCompatibility.Classes.ElevatorMover);
                go.transform.position = new Vector3(x, y, y / 1000f + 0.001f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = Trapper.getButtonSprite(); // public sprite; the internal Trap sprite is inaccessible
                sr.color = Tint;
                go.SetActive(LocalCanSee());
                t.obj = go;

                traps.Add(t);
                // Short arming delay so a victim isn't trapped the instant it is placed.
                var hud = HudManager.Instance;
                if (hud != null)
                    hud.StartCoroutine(Effects.Lerp(1.5f, new Action<float>((p) => { if (p == 1f) t.armed = true; })));
                else t.armed = true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] trap Place failed: {e}");
            }
        }

        // The local client sees traps if it is the Saboteur, or an Impostor while the option is on.
        private static bool LocalCanSee() {
            if (Saboteur.IsLocalSaboteur()) return true;
            var me = PlayerControl.LocalPlayer;
            bool meImpostor = me != null && me.Data != null && me.Data.Role != null && me.Data.Role.IsImpostor;
            return meImpostor && Saboteur.ImpostorsSeeTraps != null && Saboteur.ImpostorsSeeTraps.getBool();
        }

        // ---- trigger -----------------------------------------------------------------------------
        public static void Trigger(byte playerId, int id) {
            try {
                var t = traps.Find(x => x.id == id);
                var player = Helpers.playerById(playerId);
                if (t == null || player == null) return;

                player.moveable = false;
                player.NetTransform.Halt();
                t.stunned.Add(playerId);

                // Single-use: stop it from triggering again, but keep the object alive so it can be SHOWN.
                traps.Remove(t);

                // Reveal the trap (and play a sound) to the player who stepped in it - and to the
                // Saboteur - so the victim realises they are trapped. (Mirrors TOR's Trapper.)
                bool localInvolved = PlayerControl.LocalPlayer != null
                    && (PlayerControl.LocalPlayer.PlayerId == playerId || Saboteur.IsLocalSaboteur());
                if (t.obj != null && localInvolved) {
                    t.obj.SetActive(true);
                    try { SoundEffectsManager.play("trapperTrap"); } catch { }
                }

                float dur = Saboteur.TrapStunDuration != null ? Saboteur.TrapStunDuration.getFloat() : 5f;
                // Schedule the after-stun limp window (covers freeze + tail) on every client.
                if (Saboteur.TrappedLimp != null && Saboteur.TrappedLimp.getBool()) {
                    float tail = Saboteur.LimpDuration != null ? Saboteur.LimpDuration.getFloat() : 5f;
                    limpUntil[playerId] = Time.time + dur + tail;
                }

                var hud = HudManager.Instance;
                if (hud != null)
                    hud.StartCoroutine(Effects.Lerp(dur, new Action<float>((p) => {
                        if (p == 1f) {
                            if (player != null) player.moveable = true;
                            if (t.obj != null) UnityEngine.Object.Destroy(t.obj); // remove AFTER the stun
                        }
                    })));
                else { player.moveable = true; if (t.obj != null) UnityEngine.Object.Destroy(t.obj); }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] trap Trigger failed: {e}");
            }
        }

        // Per-frame: check the LOCAL player against armed traps; on contact, broadcast the trigger.
        public static void Update() {
            if (!Saboteur.active || traps.Count == 0) return;
            if (MeetingHud.Instance != null || ExileController.Instance != null) return;

            var me = PlayerControl.LocalPlayer;
            if (me == null || me.Data == null || me.Data.IsDead || !me.CanMove || me.inVent) return;
            if (Saboteur.IsLocalSaboteur()) return; // the saboteur is immune

            // Other impostors are immune unless the option says otherwise.
            bool meImpostor = me.Data.Role != null && me.Data.Role.IsImpostor;
            if (meImpostor && (Saboteur.TrapsHitImpostors == null || !Saboteur.TrapsHitImpostors.getBool())) return;

            // Traps are inert below the minimum alive-player count.
            if (Saboteur.AliveCount() < (Saboteur.MinAliveForTraps != null ? Saboteur.MinAliveForTraps.getFloat() : 3f)) return;

            float ud = 0.55f;
            var ss = MapUtilities.CachedShipStatus;
            if (ss != null && ss.AllVents != null && ss.AllVents.Length > 0 && ss.AllVents[0] != null)
                ud = ss.AllVents[0].UsableDistance / 2f;

            Vector2 here = me.GetTruePosition();
            foreach (var t in traps) {
                if (!t.armed || t.stunned.Contains(me.PlayerId)) continue;
                if (Vector2.Distance(here, t.pos) <= ud) {
                    Saboteur.SendTriggerTrap(me.PlayerId, t.id);
                    break;
                }
            }
        }

        public static void Clear() {
            foreach (var t in traps)
                if (t.obj != null) UnityEngine.Object.Destroy(t.obj);
            traps.Clear();
            limpUntil.Clear();
            selfLimping = false;
            nextId = 0;
        }

        // ---- self-limp toggle (saboteur) ---------------------------------------------------------
        public static bool SelfLimping => selfLimping;
        public static void SetSelfLimping(bool on) => selfLimping = on;

        // ---- limp slow (mirrors UsefulTORStuff/TrapperLimp) --------------------------------------
        private static float Ratio() =>
            Saboteur.LimpSpeedMultiplier != null ? Saboteur.LimpSpeedMultiplier.getFloat() : 0.5f;

        private static bool IsAlive(PlayerControl p) => p != null && p.Data != null && !p.Data.IsDead;

        private static bool ShouldLimp(byte id) {
            if (Saboteur.TrappedLimp != null && Saboteur.TrappedLimp.getBool()
                && limpUntil.TryGetValue(id, out float until) && Time.time < until) return true;
            if (Saboteur.SelfLimp != null && Saboteur.SelfLimp.getBool() && selfLimping
                && Saboteur.saboteur != null && Saboteur.saboteur.PlayerId == id) return true;
            return false;
        }

        [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
        static class PlayerPhysicsPatch {
            public static void Postfix(PlayerPhysics __instance) {
                try {
                    if (!__instance.AmOwner || __instance.myPlayer == null) return;
                    if (GameData.Instance != null && IsAlive(__instance.myPlayer) && __instance.myPlayer.CanMove
                        && ShouldLimp(__instance.myPlayer.PlayerId))
                        __instance.body.velocity *= Ratio();
                } catch { }
            }
        }

        [HarmonyPatch(typeof(CustomNetworkTransform), nameof(CustomNetworkTransform.FixedUpdate))]
        static class NetTransformPatch {
            public static void Postfix(CustomNetworkTransform __instance) {
                try {
                    if (__instance.AmOwner || __instance.myPlayer == null) return;
                    if (GameData.Instance != null && IsAlive(__instance.myPlayer) && __instance.myPlayer.CanMove
                        && ShouldLimp(__instance.myPlayer.PlayerId))
                        __instance.body.velocity *= Ratio();
                } catch { }
            }
        }
    }
}
