// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Maniac visual effects. Same pooled-SpriteRenderer technique as PoltergeistFx (IL2CPP does not
 * reliably render runtime ParticleSystems), built on the shared UCFx sprite cache/registries instead
 * of wiring its own HudManager.Update/reset patches:
 *
 *   - Explosion:   a radial streak burst (the shockwave) plus rising ember dots and drifting smoke
 *                  puffs at the bomb's detonation point - the public kill-event payoff, alongside the
 *                  existing screen flash (close range) and boom sound (distance-attenuated).
 *   - HandoffWisp: a tiny, quick puff at the hand-off position when the bomb is passed. Who carries the
 *                  bomb is a secret, so this is spawned ONLY by the two involved clients (old/new
 *                  carrier) - see Maniac.ApplyPassBomb - never broadcast to bystanders.
 *
 * Both effects are driven from Tick(), registered once with UCFx.RegisterTick(); cleanup runs off
 * UCFx.RegisterReset() (round start + game end), mirroring PoltergeistFx's own Clear().
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {
    public static class ManiacFx {
        private static readonly Color Red = new Color(1f, 0.25f, 0.15f);
        private static readonly Color Orange = new Color(1f, 0.55f, 0.15f);
        private static readonly Color Soot = new Color(0.25f, 0.22f, 0.20f);

        static ManiacFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // ---- One-shot effect bookkeeping ----
        private sealed class Effect {
            public GameObject go;
            public SpriteRenderer[] parts;
            public float start;
            public float life;
            public int kind; // 0 explosion, 1 handoff wisp
            public int seed;
        }
        private static readonly List<Effect> effects = new();

        public static void SpawnExplosion(Vector2 at) => Spawn(0, at, 1.15f, 22);
        public static void SpawnHandoffWisp(Vector2 at) => Spawn(1, at, 0.55f, 10);

        public static void Tick() {
            try {
                float now = Time.time;
                for (int i = effects.Count - 1; i >= 0; i--) {
                    var e = effects[i];
                    if (e.go == null || now - e.start >= e.life) {
                        if (e.go != null) UnityEngine.Object.Destroy(e.go);
                        effects.RemoveAt(i);
                        continue;
                    }
                    Animate(e, (now - e.start) / e.life);
                }
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Maniac] fx tick failed: {ex.Message}");
            }
        }

        public static void Clear() {
            foreach (var e in effects) if (e.go != null) UnityEngine.Object.Destroy(e.go);
            effects.Clear();
        }

        private static void Spawn(int kind, Vector2 at, float life, int count) {
            try {
                var go = UCFx.NewFxRoot("ManiacFx", at);
                var e = new Effect {
                    go = go,
                    start = Time.time,
                    life = life,
                    kind = kind,
                    seed = UnityEngine.Random.Range(0, 10000)
                };
                e.parts = UCFx.MakeParts(go, count, i => {
                    if (kind != 0) return UCFx.Dot;
                    // Explosion mix: streaks sell the shockwave, smoke sells the aftermath, dots are embers.
                    int m = i % 4;
                    return m == 0 ? UCFx.Streak : m == 1 ? UCFx.Smoke : UCFx.Dot;
                });
                effects.Add(e);
                Animate(e, 0f);
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Maniac] fx spawn failed: {ex.Message}");
            }
        }

        private static void Animate(Effect e, float t) {
            for (int i = 0; i < e.parts.Length; i++) {
                var sr = e.parts[i];
                if (sr == null) continue;
                float u = Hash(e.seed + i);          // stable per-particle random 0..1
                float v = Hash(e.seed + i * 7 + 3);
                float ang = u * Mathf.PI * 2f;

                if (e.kind == 0) {
                    int m = i % 4;
                    if (m == 0) { // shockwave streaks: fast radial burst, quick fade
                        float ease = 1f - (1f - t) * (1f - t);
                        float r = ease * (1.1f + 0.6f * v);
                        sr.transform.localPosition = Rot(ang, r);
                        sr.transform.localRotation = Quaternion.Euler(0, 0, ang * Mathf.Rad2Deg);
                        sr.transform.localScale = new Vector3(0.55f * (1f - t * 0.4f), 0.12f, 1f);
                        sr.color = Tint(Color.Lerp(Color.white, Orange, 0.6f), Mathf.Clamp01(1f - t * 1.6f));
                    } else if (m == 1) { // smoke: slow drifting puffs that linger and rise
                        float rise = t * (0.6f + 0.4f * v);
                        float r = 0.15f + t * (0.35f + 0.3f * v);
                        var pos = Rot(ang, r);
                        pos.y += rise;
                        sr.transform.localPosition = pos;
                        sr.transform.localScale = Vector3.one * (0.35f + 0.25f * v) * (0.5f + t);
                        sr.color = Tint(Soot, (1f - t) * 0.55f);
                    } else { // embers: quick outward pop, fall/fade fast
                        float ease = 1f - (1f - t) * (1f - t);
                        float r = ease * (0.5f + 0.7f * v);
                        var pos = Rot(ang, r);
                        pos.y += -t * 0.15f + Mathf.Sin(ang) * 0.05f;
                        sr.transform.localPosition = pos;
                        sr.transform.localScale = Vector3.one * (0.16f + 0.12f * v) * (1f - t * 0.6f);
                        sr.color = Tint(Color.Lerp(Red, Orange, v), Mathf.Clamp01(1f - t * 1.3f));
                    }
                } else { // handoff wisp: tiny, quick puff right at the pass point
                    float ease = 1f - (1f - t) * (1f - t);
                    float r = 0.08f + ease * (0.22f + 0.15f * v);
                    var pos = Rot(ang, r);
                    pos.y += ease * 0.12f;
                    sr.transform.localPosition = pos;
                    sr.transform.localScale = Vector3.one * (0.13f + 0.08f * v) * (1f - t * 0.5f);
                    sr.color = Tint(Color.Lerp(Orange, Color.white, v * 0.4f), (1f - t) * 0.7f);
                }
            }
        }

        // ---- helpers ----
        private static Vector3 Rot(float ang, float r) => new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, 0f);
        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
        private static float Hash(int n) { unchecked { n *= (int)2654435761u; n ^= n >> 13; return ((n & 0xFFFF) / 65535f); } }
    }
}
