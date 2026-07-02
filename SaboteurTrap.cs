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
        public SpriteRenderer sr;
        public bool armed;
        public float placedAt;
        private readonly HashSet<byte> stunned = new HashSet<byte>();

        // Fallback tint (violet, distinct from Trapper) - only used when the custom icon is missing
        // and the trap has to reuse the Trapper button texture.
        private static readonly Color FallbackTint = new Color(0.72f, 0.25f, 1f, 0.85f);

        // Real Trapper IN-GAME trap sprite, loaded from TOR's own assembly (same resource + 300 ppu that
        // TOR's Objects/Trap uses). Shown to the VICTIM the moment the trap springs so a sprung Saboteur
        // trap is visually indistinguishable from a Trapper trap. TOR's Trap class is internal, so we load
        // the embedded sprite directly rather than call its getTrapSprite(). Cached after first load.
        private static Sprite trapperTrapSprite;
        private static Sprite TrapperTrapSprite() {
            if (trapperTrapSprite != null) return trapperTrapSprite;
            try { trapperTrapSprite = Helpers.loadSpriteFromResources("TheOtherRoles.Resources.Trapper_Trap_Ingame.png", 300f); }
            catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[Saboteur] Trapper trap sprite load failed: {e.Message}"); }
            return trapperTrapSprite;
        }
        private const float SteadyAlpha = 0.85f;   // final alpha once fully armed (matches the old flat 0.85)
        private const float GrowInTime = 0.2f;     // scale/alpha fade-in duration after placement

        // Short-lived visual-only effects (e.g. the stun-release flash) that outlive the trap object
        // itself; tracked so a round reset can hard-destroy one still mid-flight.
        private static readonly List<GameObject> transientEffects = new List<GameObject>();

        // ---- placement ---------------------------------------------------------------------------
        public static int ActiveCount => traps.Count;

        // Saboteur-local check: may a trap be placed at the local player's current spot? Two layers:
        //   1. Room rule: forbidden in the emergency-button / reactor / O2 rooms.
        //   2. MAP-AGNOSTIC distance rule: forbidden within 4.5 units of any critical-sabotage console
        //      (ResetReactor / ResetSeismic / StopCharles) or the emergency button itself. This is what
        //      covers the Airship's helicopter consoles (their rooms are map-specific and not in the
        //      switch) and any future map without touching this code again.
        public static bool CanPlaceHere() {
            try {
                var room = HudManager.Instance?.roomTracker?.LastRoom?.RoomId;
                if (room != null) {
                    switch (room.Value) {
                        case SystemTypes.Reactor:     // reactor / meltdown
                        case SystemTypes.Laboratory:  // Polus seismic (reactor-equivalent)
                        case SystemTypes.LifeSupp:    // O2
                        case SystemTypes.Cafeteria:   // emergency button (Skeld/Mira/Fungle)
                        case SystemTypes.MeetingRoom: // emergency button (other maps)
                            return false;
                    }
                }
                return !NearCriticalSpot(PlayerControl.LocalPlayer.GetTruePosition(), 4.5f);
            } catch { return true; }
        }

        // True if `pos` is close to a critical-sabotage console or the emergency button. The console
        // list is rebuilt per ShipStatus instance (same cache-invalidation rule as the Saboteur's
        // task-console scan - stale cross-map caches held destroyed objects).
        private static readonly List<Vector2> criticalSpots = new List<Vector2>();
        private static ShipStatus criticalSpotsShip;
        public static bool NearCriticalSpot(Vector2 pos, float dist) {
            try {
                var ship = ShipStatus.Instance;
                if (ship == null) return false;
                if (criticalSpotsShip != ship) {
                    criticalSpotsShip = ship;
                    criticalSpots.Clear();
                    foreach (var c in UnityEngine.Object.FindObjectsOfType<Console>()) {
                        if (c == null || c.TaskTypes == null) continue;
                        foreach (var tt in c.TaskTypes) {
                            if (tt == TaskTypes.ResetReactor || tt == TaskTypes.ResetSeismic
                                || tt == TaskTypes.StopCharles) {
                                criticalSpots.Add(c.transform.position);
                                break;
                            }
                        }
                    }
                    if (ship.EmergencyButton != null)
                        criticalSpots.Add(ship.EmergencyButton.transform.position);
                }
                foreach (var spot in criticalSpots)
                    if (Vector2.Distance(pos, spot) < dist) return true;
                return false;
            } catch { return false; }
        }

        public static void Place(int id, float x, float y) {
            try {
                var t = new SaboteurTrap { id = id, pos = new Vector2(x, y), placedAt = Time.time };

                var go = new GameObject("SaboteurTrap") { layer = 11 };
                go.AddSubmergedComponent(SubmergedCompatibility.Classes.ElevatorMover);
                go.transform.position = new Vector3(x, y, y / 1000f + 0.001f);
                var sr = go.AddComponent<SpriteRenderer>();
                // Same texture as the Saboteur's trap BUTTON (both load at 115 ppu, so the ground
                // size stays the same). Fallback: Trapper button sprite, violet-tinted.
                var sprite = UCAssets.SaboteurTrapIcon;
                if (sprite != null) {
                    sr.sprite = sprite;
                    sr.color = new Color(1f, 1f, 1f, 0f); // fades/pulses in over GrowInTime, see AnimateVisual
                } else {
                    sr.sprite = Trapper.getButtonSprite();
                    sr.color = new Color(FallbackTint.r, FallbackTint.g, FallbackTint.b, 0f);
                }
                go.transform.localScale = Vector3.one * 0.5f; // grows in via AnimateVisual, see below
                bool visible = LocalCanSee();
                go.SetActive(visible);
                t.obj = go;
                t.sr = sr;

                // Local placement confirmation - gated the same way the sprite itself is (LocalCanSee):
                // Place() runs on every client via a broadcast RPC, so this must stay a plain local Play()
                // (never PlayAt) or a nearby-but-blind crewmate would hear a mystery cue at the exact spot
                // and learn "a trap exists here" despite not being allowed to see it. No dedicated "trap
                // arm" asset exists in the sound table, so the generic saboteur confirmation blip is reused.
                if (visible) {
                    try { UCAssets.PlaySaboteurMark(UCAssets.VolSoft); } catch { }
                }

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

        // Grows the sprite in from a slightly smaller scale and fades its alpha in over GrowInTime;
        // while still arming, it also gets a gentle alpha pulse (capped by the fade-in envelope) so
        // viewers can tell "still arming" from "live" (armed=true settles to the steady SteadyAlpha).
        // Runs for every visible trap every frame regardless of whether the LOCAL player could trigger
        // it (called unconditionally from Update(), before the trigger-only early returns).
        public void AnimateVisual() {
            if (obj == null || sr == null) return;
            float age = Time.time - placedAt;
            float grow = Mathf.Clamp01(age / GrowInTime);
            grow = grow * grow * (3f - 2f * grow); // smoothstep
            obj.transform.localScale = Vector3.one * Mathf.Lerp(0.5f, 1f, grow);

            float alpha;
            if (!armed) {
                float pulse = 0.45f + 0.25f * Mathf.Sin(Time.time * 4f);
                alpha = Mathf.Min(grow, pulse);
            } else {
                alpha = grow * SteadyAlpha;
            }
            var c = sr.color;
            sr.color = new Color(c.r, c.g, c.b, alpha);
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

                // Reveal the trap to the player who stepped in it - and to the Saboteur - so the victim
                // realises they are trapped. (Mirrors TOR's Trapper: only victim + trapper see it spring.)
                bool localIsSaboteur = Saboteur.IsLocalSaboteur();
                bool localIsVictim = PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.PlayerId == playerId;
                bool localInvolved = localIsVictim || localIsSaboteur;
                if (t.obj != null && localInvolved) {
                    t.obj.SetActive(true);
                    if (localIsVictim && !localIsSaboteur) {
                        // DISGUISE: to the victim the sprung trap looks EXACTLY like a triggered Trapper
                        // trap - the real in-game sprite (white, full alpha) plus the Trapper's own trigger
                        // sound - so they blame a Trapper instead of suspecting a Saboteur. No violet spark
                        // burst here (that would give it away); TOR's Trapper reveal is sprite + sound only.
                        if (t.sr != null) {
                            var disguise = TrapperTrapSprite();
                            if (disguise != null) { t.sr.sprite = disguise; t.sr.color = Color.white; }
                        }
                        try { SoundEffectsManager.play("trapperTrap"); } catch { }
                    } else {
                        // The Saboteur's own view keeps the saboteur marker + its snap/spark cue.
                        try {
                            UCAssets.PlayTrapSnap(t.pos);
                            SaboteurKillFx.PlayMiniBurst(t.pos);
                        } catch { }
                    }
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
                            // Release cue ONLY for the Saboteur (violet snap + flash). The victim gets no
                            // release effect: a real Trapper trap simply frees you when it expires, so a
                            // violet flash on the victim's screen would break the disguise.
                            if (localIsSaboteur) {
                                try {
                                    UCAssets.PlayTrapSnap(t.pos, 0.35f);
                                    SpawnReleaseFlash(t.pos);
                                } catch { }
                            }
                            if (t.obj != null) UnityEngine.Object.Destroy(t.obj); // remove AFTER the stun
                        }
                    })));
                else { player.moveable = true; if (t.obj != null) UnityEngine.Object.Destroy(t.obj); }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] trap Trigger failed: {e}");
            }
        }

        // Brief local fade-out flash at the stun-release beat (localInvolved gated, same as the reveal).
        // Deliberately small and localized rather than a full Helpers.showFlash - only the two parties
        // who already saw the reveal see this, so a screen-wide flash would be overkill for them.
        private static void SpawnReleaseFlash(Vector2 at) {
            try {
                var go = UCFx.NewFxRoot("SaboteurTrapRelease", at, -1.0f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = UCFx.Ring;
                sr.color = new Color(FallbackTint.r, FallbackTint.g, FallbackTint.b, 0.9f);
                UCFx.TryMakeAdditive(sr);

                var hud = HudManager.Instance;
                if (hud == null) { UnityEngine.Object.Destroy(go); return; }

                transientEffects.Add(go);
                hud.StartCoroutine(Effects.Lerp(0.3f, new Action<float>((p) => {
                    if (go == null) return;
                    go.transform.localScale = Vector3.one * Mathf.Lerp(0.5f, 1.4f, p);
                    sr.color = new Color(FallbackTint.r, FallbackTint.g, FallbackTint.b, 0.9f * (1f - p));
                    if (p >= 1f) {
                        transientEffects.Remove(go);
                        UnityEngine.Object.Destroy(go);
                    }
                })));
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Saboteur] trap release flash failed: {e.Message}");
            }
        }

        // Per-frame: animate every visible trap's placement/arm visuals, then check the LOCAL player
        // against armed traps and broadcast a trigger on contact.
        public static void Update() {
            if (!Saboteur.active || traps.Count == 0) return;
            if (MeetingHud.Instance != null || ExileController.Instance != null) return;

            // Visual animation runs for anyone who can currently see a trap, independent of whether the
            // local player could ever trigger it (e.g. the Saboteur is always immune but still watches
            // their own placed traps arm).
            foreach (var t in traps) t.AnimateVisual();

            var me = PlayerControl.LocalPlayer;
            if (me == null || me.Data == null || me.Data.IsDead || !me.CanMove || me.inVent) return;
            if (Saboteur.IsLocalSaboteur()) return; // the saboteur is immune to triggering, not to viewing

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
            foreach (var go in transientEffects)
                if (go != null) UnityEngine.Object.Destroy(go);
            transientEffects.Clear();
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
