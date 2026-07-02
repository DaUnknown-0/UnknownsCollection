// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Follower visual effects.
 *
 * The Follower's role takeover (ApplyShiftRole in Follower.cs) previously had a sound cue
 * (UCAssets.PlayFollowerShift) but no visual payoff at all for the single most consequential moment of
 * the role - a full team/role/ability swap. This adds a one-shot "energy surge" burst (dots/rings
 * implode toward the Follower, then detonate outward with a brief streak flash) using the same pooled
 * SpriteRenderer technique as PoltergeistFx (no runtime ParticleSystems in the IL2CPP build).
 *
 * Strictly self-only: the takeover itself is secret (nobody else may learn who the Follower is or that
 * a shift even happened), so SpawnShift() must only ever be called by Follower.cs under the exact same
 * `f == PlayerControl.LocalPlayer` gate as the existing sound cue - this file does not re-check that
 * gate itself, the caller owns it.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {
    public static class FollowerFx {
        // ---- One-shot effect bookkeeping (mirrors PoltergeistFx) ----
        private sealed class Effect {
            public GameObject go;
            public SpriteRenderer[] parts;
            public float start;
            public float life;
            public int seed;
        }
        private static readonly List<Effect> effects = new();

        static FollowerFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        public static void SpawnShift(Vector2 at) => Spawn(at, 0.9f, 16);

        private static void Spawn(Vector2 at, float life, int count) {
            try {
                var go = UCFx.NewFxRoot("FollowerShiftFx", at);
                var parts = UCFx.MakeParts(go, count, i => (i % 3 == 0) ? UCFx.Streak : (i % 2 == 0 ? UCFx.Ring : UCFx.Dot));
                var e = new Effect {
                    go = go,
                    parts = parts,
                    start = Time.time,
                    life = life,
                    seed = UnityEngine.Random.Range(0, 10000)
                };
                effects.Add(e);
                Animate(e, 0f);
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Follower] fx spawn failed: {ex.Message}");
            }
        }

        private static void Tick() {
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
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Follower] fx tick failed: {ex.Message}");
            }
        }

        private static void Clear() {
            foreach (var e in effects) if (e.go != null) UnityEngine.Object.Destroy(e.go);
            effects.Clear();
        }

        // Two-phase burst: particles implode toward the Follower up to `Split`, then detonate outward
        // and fade for the remainder of the lifetime. Streaks flash briefly right at the turn.
        private const float Split = 0.35f;

        private static void Animate(Effect e, float t) {
            for (int i = 0; i < e.parts.Length; i++) {
                var sr = e.parts[i];
                if (sr == null) continue;
                float u = Hash(e.seed + i);          // stable per-particle random 0..1
                float v = Hash(e.seed + i * 7 + 3);
                float ang = u * Mathf.PI * 2f + v * 1.5f;
                bool isStreak = i % 3 == 0;
                bool isRing = !isStreak && i % 2 == 0;

                float r, alpha;
                if (t < Split) {
                    float k = t / Split;
                    float ease = k * k;
                    r = Mathf.Lerp(1.1f * (0.6f + v), 0.05f, ease);
                    alpha = k;
                } else {
                    float k = (t - Split) / (1f - Split);
                    float ease = 1f - (1f - k) * (1f - k);
                    r = Mathf.Lerp(0.05f, 0.95f * (0.6f + v), ease);
                    alpha = 1f - k;
                }

                float swirl = ang + t * (isStreak ? 1.5f : 3.5f) * (u > 0.5f ? 1f : -1f);
                sr.transform.localPosition = Rot(swirl, r);
                sr.transform.localRotation = Quaternion.Euler(0, 0, swirl * Mathf.Rad2Deg);

                if (isStreak) {
                    // Brief white flash right at the implode->explode turn - sells the "detonation".
                    float flash = Mathf.Clamp01(1f - Mathf.Abs(t - Split) * 5f);
                    sr.transform.localScale = new Vector3(0.5f, 0.09f, 1f);
                    sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(flash * 0.9f));
                } else {
                    sr.transform.localScale = Vector3.one * (isRing ? (0.20f + 0.10f * v) : (0.14f + 0.08f * v));
                    var c = Follower.Color;
                    sr.color = new Color(c.r, c.g, c.b, Mathf.Clamp01(alpha * (0.55f + 0.45f * Flicker(i))));
                }
            }
        }

        // ---- helpers (same stable-hash technique as PoltergeistFx) ----
        private static Vector3 Rot(float ang, float r) => new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, 0f);
        private static float Flicker(int i) => Mathf.Abs(Mathf.Sin(Time.time * 9f + i * 2.3f));
        private static float Hash(int n) { unchecked { n *= (int)2654435761u; n ^= n >> 13; return ((n & 0xFFFF) / 65535f); } }
    }
}
