// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Illusionist visual effects: a small red/white "poof" burst used both when the decoy clone
 * materializes (Playback, synced to PlayCloneShimmer) and when it dissolves (natural path end / the
 * explicit despawn RPC - see IllusionistClone.DespawnWithFx). Same pooled-SpriteRenderer technique as
 * PoltergeistFx (IL2CPP has no reliable runtime ParticleSystems), sourced from the shared UCFx sprite
 * cache. Unlike PoltergeistFx (still driven by an explicit call from Poltergeist.cs's own
 * HudManager.Update postfix, kept that way for compatibility), this class self-registers with UCFx's
 * tick/reset registries so no per-role call site is needed.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {
    public static class IllusionistFx {
        private static readonly Color Red = new Color(0.85f, 0.18f, 0.20f);
        private static readonly Color White = new Color(0.96f, 0.94f, 0.94f);

        static IllusionistFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // ---- one-shot effect bookkeeping (same shape as PoltergeistFx's Effect) ----
        private sealed class Effect {
            public GameObject go;
            public SpriteRenderer[] parts;
            public float start;
            public float life;
            public int seed;
        }
        private static readonly List<Effect> effects = new();

        // Shared by both the materialize (Spawn) and dissolve (Despawn) beats - visually the two ends of
        // the same "illusion" bookend, just red/white instead of Poltergeist's violet/cyan.
        public static void SpawnMaterializePoof(Vector2 at) => Spawn(at, 0.5f, 16);

        private static void Spawn(Vector2 at, float life, int count) {
            try {
                var host = UCFx.NewFxRoot("IllusionistFx", at);
                var e = new Effect {
                    go = host,
                    parts = UCFx.MakeParts(host, count, i => (i % 3 == 0) ? UCFx.Streak : UCFx.Dot),
                    start = Time.time,
                    life = life,
                    seed = UnityEngine.Random.Range(0, 10000)
                };
                effects.Add(e);
                Animate(e, 0f);
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Illusionist] fx spawn failed: {ex.Message}");
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
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Illusionist] fx tick failed: {ex.Message}");
            }
        }

        public static void Clear() {
            foreach (var e in effects) if (e.go != null) UnityEngine.Object.Destroy(e.go);
            effects.Clear();
        }

        // Puff expands, drifts up slightly, dissolves - same shape as PoltergeistFx's Poof kind, just
        // tinted red/white (impostor identity) instead of violet/cyan.
        private static void Animate(Effect e, float t) {
            for (int i = 0; i < e.parts.Length; i++) {
                var sr = e.parts[i];
                if (sr == null) continue;
                float u = Hash(e.seed + i);
                float v = Hash(e.seed + i * 7 + 3);
                float ang = u * Mathf.PI * 2f;

                float ease = 1f - (1f - t) * (1f - t);
                float r = 0.12f + ease * (0.55f + 0.40f * v);
                var pos = Rot(ang, r);
                pos.y += ease * 0.40f; // slight buoyant drift
                sr.transform.localPosition = pos;
                sr.transform.localScale = Vector3.one * (0.26f + 0.18f * v) * (0.6f + t * 0.7f);
                sr.color = Tint(Color.Lerp(White, Red, v * 0.6f), (1f - ease) * 0.9f);
                if (i % 3 == 0) sr.color = Tint(White, Mathf.Clamp01(1f - t * 4f)); // brief core flash
            }
        }

        private static Vector3 Rot(float ang, float r) => new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, 0f);
        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
        private static float Hash(int n) { unchecked { n *= (int)2654435761u; n ^= n >> 13; return ((n & 0xFFFF) / 65535f); } }
    }
}
