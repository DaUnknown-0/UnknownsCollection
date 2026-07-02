// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Shared visual effects for the crew-side roles (Shade, Scout - not self-only, publicly visible).
 *
 * Currently a single reusable effect: SpawnPoof, an airy puff that expands, drifts up and dissolves -
 * a tint-parameterized clone of PoltergeistFx's "Poof" (manifest end) animation, reused here for two
 * unrelated but visually similar public moments:
 *   - Shade.ApplyRevealBody: a dumped, dusty white-grey puff where a hidden body is found again.
 *   - Scout.ApplyActivate/ApplyDeactivate: a teal puff selling the phase-shift transition.
 * Both are already public/ungated events (the body becoming visible again, and the Scout's own
 * transparency, are both already visible to everyone), so this effect carries no info-leak risk and is
 * spawned locally by every client from its own copy of the same RPC-applier - no new RPC needed.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {
    public static class CrewFx {
        private sealed class Puff {
            public GameObject go;
            public SpriteRenderer[] parts;
            public float start;
            public float life;
            public Color tint;
            public int seed;
        }
        private static readonly List<Puff> puffs = new();

        static CrewFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        public static void SpawnPoof(Vector2 at, Color tint, float life = 0.9f, int count = 16) {
            try {
                var go = UCFx.NewFxRoot("CrewPoofFx", at);
                var parts = UCFx.MakeParts(go, count, i => (i % 3 == 0) ? UCFx.Streak : UCFx.Smoke);
                var p = new Puff {
                    go = go,
                    parts = parts,
                    start = Time.time,
                    life = life,
                    tint = tint,
                    seed = UnityEngine.Random.Range(0, 10000)
                };
                puffs.Add(p);
                Animate(p, 0f);
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[CrewFx] poof spawn failed: {ex.Message}");
            }
        }

        private static void Tick() {
            try {
                float now = Time.time;
                for (int i = puffs.Count - 1; i >= 0; i--) {
                    var p = puffs[i];
                    if (p.go == null || now - p.start >= p.life) {
                        if (p.go != null) UnityEngine.Object.Destroy(p.go);
                        puffs.RemoveAt(i);
                        continue;
                    }
                    Animate(p, (now - p.start) / p.life);
                }
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[CrewFx] poof tick failed: {ex.Message}");
            }
        }

        private static void Clear() {
            foreach (var p in puffs) if (p.go != null) UnityEngine.Object.Destroy(p.go);
            puffs.Clear();
        }

        // Puff expands, drifts up, dissolves - same curve as PoltergeistFx's Poof, parameterized by tint.
        private static void Animate(Puff p, float t) {
            for (int i = 0; i < p.parts.Length; i++) {
                var sr = p.parts[i];
                if (sr == null) continue;
                float u = Hash(p.seed + i);
                float v = Hash(p.seed + i * 7 + 3);
                float ang = u * Mathf.PI * 2f;
                float ease = 1f - (1f - t) * (1f - t);
                float r = 0.15f + ease * (0.7f + 0.5f * v);
                var pos = Rot(ang, r);
                pos.y += ease * 0.55f; // buoyant drift
                sr.transform.localPosition = pos;
                sr.transform.localScale = Vector3.one * (0.30f + 0.22f * v) * (0.6f + t * 0.8f);
                var baseTint = Color.Lerp(Color.white, p.tint, 0.35f + v * 0.35f);
                sr.color = new Color(baseTint.r, baseTint.g, baseTint.b, Mathf.Clamp01((1f - ease) * 0.85f));
                if (i % 3 == 0) sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(1f - t * 4f)); // brief core flash
            }
        }

        // ---- helpers ----
        private static Vector3 Rot(float ang, float r) => new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, 0f);
        private static float Hash(int n) { unchecked { n *= (int)2654435761u; n ^= n >> 13; return ((n & 0xFFFF) / 65535f); } }
    }
}
