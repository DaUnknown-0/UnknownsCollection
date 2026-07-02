// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Saboteur kill effect - the generic "electric death" played on a victim who finished a sabotaged
 * task console. A short burst of crackling sparks converges on the victim, blooms into a bright impact
 * flash and then dissipates while a violet screen flash (Saboteur-only) and a zap cue (everyone,
 * distance-attenuated) fire; the regular death animation takes over afterwards (the lethal murder is a
 * separate RPC). v1 uses a generic electric look; per-task animations (med-scan laser, ...) are a later
 * phase (the kill RPC already carries a taskType slot for that).
 *
 * Also hosts two smaller, related one-shot/continuous effects that share the same pooled-sprite
 * technique and reset bookkeeping:
 *   - PlayMiniBurst: a scaled-down version of the same converge/impact/dissipate burst, reused by
 *     SaboteurTrap for the stun-start beat.
 *   - the sabotage-mark ring: a continuous, strictly self-only pulsing marker at the currently sabotaged
 *     console, shown ONLY to the local Saboteur (gated every frame - see TickMarker).
 *
 * Self-contained (its own sprite-part pools + coroutines/tick, sourced from the shared UCFx sprite
 * cache), so it does not interfere with the Tesla's continuous danger sparks.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class SaboteurKillFx {
        private const int SparkCount = 14;
        private const float Duration = 0.95f;
        private static readonly Color Violet = new Color(0.72f, 0.25f, 1f, 1f);
        private static readonly Color BrightViolet = new Color(0.88f, 0.62f, 1f, 1f);

        // End of the "converge" beat / start of "dissipate", as a fraction of the burst's duration.
        // The impact core flash blooms around the same point (see SpawnZapBurst).
        private const float ConvergeFrac = 0.2f;

        // Running one-shot effect hosts, tracked so a round reset can hard-destroy any burst that is
        // still mid-flight (the Lerp coroutine normally cleans itself up, but resetVariables can land
        // mid-burst if a round ends right after a kill).
        private static readonly List<GameObject> activeEffects = new();

        // ---- sabotage-mark: continuous, self-only pulsing ring at the marked console ----
        private static GameObject markerGo;
        private static SpriteRenderer[] markerParts;
        private static bool markerOn;
        private static Vector2 markerPos;

        static SaboteurKillFx() {
            UCFx.RegisterReset(Clear);
            UCFx.RegisterTick(TickMarker);
        }

        public static void Clear() {
            try {
                foreach (var go in activeEffects) if (go != null) UnityEngine.Object.Destroy(go);
                activeEffects.Clear();
                if (markerGo != null) { UnityEngine.Object.Destroy(markerGo); markerGo = null; markerParts = null; }
                markerOn = false;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Saboteur] kill FX clear failed: {e.Message}");
            }
        }

        public static void Play(PlayerControl victim) {
            try {
                if (victim == null) return;

                // Screen flash: gated to the Saboteur themselves (deliberate design choice, NOT distance -
                // see SPEC "Vom User getroffene Design-Entscheidungen" #3), so only the killer gets the
                // full-screen "you did it" cue. The particle burst + zap sound stay distance-attenuated and
                // visible/audible to everyone nearby, unchanged.
                if (Saboteur.IsLocalSaboteur())
                    Helpers.showFlash(new Color(0.6f, 0.1f, 0.95f, 1f), 0.5f);
                UCAssets.PlayZap(victim.GetTruePosition());

                SpawnZapBurst(victim.GetTruePosition(), SparkCount, Duration, 1f);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Saboteur] kill FX failed: {e.Message}");
            }
        }

        // Smaller reusable burst for the trap trigger's stun-start beat (SaboteurTrap.Trigger) - same
        // converge/impact/dissipate technique at a reduced scale/duration. No screen flash and no zap
        // sound here - the trap already has its own PlayTrapSnap cue for that beat.
        public static void PlayMiniBurst(Vector2 at) {
            try { SpawnZapBurst(at, 8, 0.35f, 0.6f); }
            catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[Saboteur] trap burst failed: {e.Message}"); }
        }

        // Converge -> impact flash -> dissipate structure (mirrors PoltergeistFx's DoorBurst pattern):
        // sparks rush inward from a wider radius during the first ConvergeFrac of the duration, a bright
        // core flash blooms right around that same beat, then the sparks fall into the original chaotic
        // fast-orbit/flicker and fade out over the remainder. Streak sprites (every 3rd spark, same trick
        // DoorBurst uses) are oriented radially to read as jagged bolt segments.
        private static void SpawnZapBurst(Vector2 at, int count, float duration, float scaleMul) {
            var host = UCFx.NewFxRoot("SaboteurZap", at, -1.0f);

            var sparks = UCFx.MakeParts(host, count, i => (i % 3 == 0) ? UCFx.Streak : UCFx.Dot);
            for (int i = 0; i < sparks.Length; i++) {
                var sr = sparks[i];
                if (sr == null) continue;
                sr.color = Violet;
                sr.transform.localScale = Vector3.one * (0.18f + 0.08f * (i % 3)) * scaleMul;
                UCFx.TryMakeAdditive(sr); // electric look; safely falls back to the default material
            }

            var coreGo = new GameObject("core") { layer = 11 };
            coreGo.transform.SetParent(host.transform);
            var core = coreGo.AddComponent<SpriteRenderer>();
            core.sprite = UCFx.Spark;
            core.color = new Color(BrightViolet.r, BrightViolet.g, BrightViolet.b, 0f);
            UCFx.TryMakeAdditive(core);

            var hud = HudManager.Instance;
            if (hud == null) { UnityEngine.Object.Destroy(host); return; }

            activeEffects.Add(host);
            hud.StartCoroutine(Effects.Lerp(duration, new Action<float>((t) => {
                if (host == null) return;
                float time = Time.time;

                for (int i = 0; i < sparks.Length; i++) {
                    var s = sparks[i];
                    if (s == null) continue;
                    bool isStreak = i % 3 == 0;
                    float a = time * (14f + i * 1.3f) + i * 1.7f;       // fast electric orbit
                    float orbitR = 0.15f + 0.45f * Mathf.Abs(Mathf.Sin(time * 22f + i));

                    if (t < ConvergeFrac) {
                        // Converge inward from a wider radius toward the impact point.
                        float ease = t / ConvergeFrac;
                        ease *= ease; // ease-in
                        float startR = 0.85f + 0.25f * ((i % 5) / 5f);
                        float r = Mathf.Lerp(startR, orbitR, ease);
                        s.transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                    } else {
                        s.transform.localPosition = new Vector3(Mathf.Cos(a) * orbitR, Mathf.Sin(a) * orbitR, 0f);
                    }
                    if (isStreak) s.transform.localRotation = Quaternion.Euler(0, 0, a * Mathf.Rad2Deg);

                    float flicker = 0.4f + 0.6f * Mathf.Abs(Mathf.Sin(time * 30f + i * 2f));
                    float fade = t < ConvergeFrac ? 1f : Mathf.Clamp01(1f - (t - ConvergeFrac) / (1f - ConvergeFrac));
                    s.color = new Color(Violet.r, Violet.g, Violet.b, flicker * fade);
                }

                // Impact core flash: quick bloom centered on the converge/dissipate seam, then fade.
                if (core != null) {
                    float bloom = Mathf.Clamp01(1f - Mathf.Abs(t - ConvergeFrac) * 8f);
                    core.transform.localScale = Vector3.one * (0.5f + bloom * 0.9f) * scaleMul;
                    core.color = new Color(BrightViolet.r, BrightViolet.g, BrightViolet.b, bloom);
                }

                if (t >= 1f) {
                    activeEffects.Remove(host);
                    UnityEngine.Object.Destroy(host);
                }
            })));
        }

        // ---- sabotage-mark: continuous pulsing ring at the currently marked console ----

        // Called from Saboteur.ApplySetSabotagedConsole/ApplyClearSabotage. Only ever turned ON by a
        // client where Saboteur.IsLocalSaboteur() already held true at the call site (one-shot gate);
        // TickMarker below re-checks the same condition every frame on top of that (continuous gate, per
        // the Info-Leak-Regel / PoltergeistFx.TickAura pattern) so a stale `markerOn=true` can never leak.
        public static void SetMarker(Vector2 at, bool on) { markerPos = at; markerOn = on; }

        private static void TickMarker() {
            bool show = markerOn && Saboteur.IsLocalSaboteur();
            if (!show) {
                if (markerGo != null) markerGo.SetActive(false);
                return;
            }
            if (markerGo == null) {
                markerGo = UCFx.NewFxRoot("SaboteurMark", markerPos, -1.3f);
                markerParts = UCFx.MakeParts(markerGo, 8, i => UCFx.Dot);
            }
            markerGo.SetActive(true);
            markerGo.transform.position = new Vector3(markerPos.x, markerPos.y, -1.3f);

            float now = Time.time;
            float pulse = 0.85f + 0.15f * Mathf.Sin(now * 3.2f);
            for (int i = 0; i < markerParts.Length; i++) {
                var sr = markerParts[i];
                if (sr == null) continue;
                float a = now * 1.4f + i * Mathf.PI * 2f / markerParts.Length;
                float r = 0.32f * pulse;
                sr.transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                sr.transform.localScale = Vector3.one * 0.16f;
                sr.color = new Color(Violet.r, Violet.g, Violet.b, 0.5f + 0.25f * Flicker(i));
            }
        }

        private static float Flicker(int i) => Mathf.Abs(Mathf.Sin(Time.time * 5f + i * 2.1f));
    }
}
