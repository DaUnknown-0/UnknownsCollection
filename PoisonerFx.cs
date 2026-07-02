// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Poisoner visual effects. Same pooled-SpriteRenderer technique as PoltergeistFx (IL2CPP does not
 * reliably render runtime ParticleSystems), built on the shared UCFx sprite cache/registries instead
 * of wiring its own HudManager.Update/reset patches:
 *
 *   - Cleanse: a white/green poof (expanding ring core + rising sparkle dots) at the Antidote target's
 *              position, the visual payoff for a successful cure.
 *
 * IMPORTANT: Poisoner.ApplyAntidote runs on EVERY client (it is reached via the SubAntidote RPC), but
 * whether a given player was poisoned is private information. SpawnCleanse must only ever be called by
 * the caller after gating on PlayerControl.LocalPlayer being the medic OR the cured target - see
 * Poisoner.ApplyAntidote. This file does not gate anything itself; it only draws what it's told to.
 *
 * Driven from Tick(), registered once with UCFx.RegisterTick(); cleanup runs off UCFx.RegisterReset()
 * (round start + game end), mirroring PoltergeistFx's own Clear().
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {
    public static class PoisonerFx {
        private static readonly Color White = new Color(0.94f, 0.99f, 0.96f);
        private static readonly Color Green = new Color(0.45f, 0.95f, 0.55f);

        static PoisonerFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // ---- One-shot effect bookkeeping ----
        private sealed class Effect {
            public GameObject go;
            public SpriteRenderer[] parts;
            public float start;
            public float life;
            public int seed;
        }
        private static readonly List<Effect> effects = new();

        public static void SpawnCleanse(Vector2 at) => Spawn(at, 0.9f, 15);

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
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Poisoner] fx tick failed: {ex.Message}");
            }
        }

        public static void Clear() {
            foreach (var e in effects) if (e.go != null) UnityEngine.Object.Destroy(e.go);
            effects.Clear();
        }

        private static void Spawn(Vector2 at, float life, int count) {
            try {
                var go = UCFx.NewFxRoot("PoisonerFx", at);
                var e = new Effect {
                    go = go,
                    start = Time.time,
                    life = life,
                    seed = UnityEngine.Random.Range(0, 10000)
                };
                e.parts = UCFx.MakeParts(go, count, i => (i % 3 == 0) ? UCFx.Ring : UCFx.Dot);
                effects.Add(e);
                Animate(e, 0f);
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Poisoner] fx spawn failed: {ex.Message}");
            }
        }

        private static void Animate(Effect e, float t) {
            for (int i = 0; i < e.parts.Length; i++) {
                var sr = e.parts[i];
                if (sr == null) continue;
                float u = Hash(e.seed + i);          // stable per-particle random 0..1
                float v = Hash(e.seed + i * 7 + 3);
                float ang = u * Mathf.PI * 2f;

                if (i % 3 == 0) { // expanding cleanse ring, dissolves outward
                    float ease = 1f - (1f - t) * (1f - t);
                    float scale = 0.25f + ease * 0.9f;
                    sr.transform.localPosition = Vector3.zero;
                    sr.transform.localScale = Vector3.one * scale;
                    sr.color = Tint(Color.Lerp(White, Green, 0.4f), (1f - t) * 0.7f);
                } else { // rising sparkle dots, drift up and out
                    float rise = t * (0.5f + 0.4f * v);
                    float r = 0.12f + t * (0.3f + 0.25f * v);
                    var pos = Rot(ang, r);
                    pos.y += rise;
                    sr.transform.localPosition = pos;
                    sr.transform.localScale = Vector3.one * (0.14f + 0.10f * v) * (1f - t * 0.5f);
                    sr.color = Tint(Color.Lerp(Green, White, v), (1f - t) * 0.85f);
                }
            }
        }

        // ---- helpers ----
        private static Vector3 Rot(float ang, float r) => new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, 0f);
        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
        private static float Hash(int n) { unchecked { n *= (int)2654435761u; n ^= n >> 13; return ((n & 0xFFFF) / 65535f); } }
    }
}
