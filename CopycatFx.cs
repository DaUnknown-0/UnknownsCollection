// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Copycat visual effects:
 *   - ShieldAura: a faint orbiting ring shown ONLY to the local Copycat while the copied Shield ability
 *     is active - gated fresh every frame exactly like PoltergeistFx.TickAura. The Copycat's whole point
 *     is to pass as ordinary crew, so this must never be visible to anyone else (no "visible to everyone"
 *     option like Illusionist's clone shield - see Copycat.StartShield).
 *   - MorphShimmer: a short sparkle burst at the Copycat's own position when Morphling starts or ends
 *     (the look change itself is already visible to everyone, so this carries no extra information).
 *   - ShootTracer: a quick streak from the Copycat to its Shoot target, giving the ranged ability a
 *     distinct "shot fired" beat instead of reading exactly like a melee kill.
 *
 * Same pooled-SpriteRenderer technique as every other UC FX class (IL2CPP has no reliable runtime
 * ParticleSystems), sourced from the shared UCFx sprite cache. Self-registers with UCFx's tick/reset
 * registries - no per-role call site is needed for ticking or cleanup.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {
    public static class CopycatFx {
        private static Color Purple => Copycat.Color; // matches the Copycat's own role-identity color
        private static readonly Color White = new Color(0.95f, 0.90f, 0.98f);

        static CopycatFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // A static class's type initializer only runs on first access to one of its members. The one-shot
        // effects (shimmer/tracer) touch this class naturally when spawned, but the shield aura is purely
        // passive (TickAura polls Copycat.shielded on its own, no explicit "aura on" call) - so if Shield
        // happened to be the very first ability a Copycat ever uses, nothing would have touched this class
        // yet and the aura's Tick registration would never have run. Copycat.StartShield() calls this to
        // guarantee the class (and therefore the tick registration) is initialized no later than the
        // moment the aura first needs to be possible.
        public static void EnsureInitialized() { }

        // ---- one-shot effect bookkeeping (shimmer + tracer) ----
        private sealed class Effect {
            public GameObject go;
            public SpriteRenderer[] parts;
            public float start;
            public float life;
            public int kind; // 0 shimmer (morph), 1 tracer (shoot)
            public int seed;
            public Vector2 from, to;
        }
        private static readonly List<Effect> effects = new();

        // ---- continuous: self-only shield aura ----
        private static GameObject auraGo;
        private static SpriteRenderer[] auraParts;

        public static void SpawnMorphShimmer(Vector2 at) => Spawn(0, at, at, 0.40f, 14);
        public static void SpawnShootTracer(Vector2 from, Vector2 to) => Spawn(1, from, to, 0.14f, 10);

        private static void Spawn(int kind, Vector2 from, Vector2 to, float life, int count) {
            try {
                var host = UCFx.NewFxRoot("CopycatFx", from);
                var e = new Effect {
                    go = host,
                    parts = UCFx.MakeParts(host, count, i => (kind == 1 || i % 3 == 0) ? UCFx.Streak : UCFx.Dot),
                    start = Time.time,
                    life = life,
                    kind = kind,
                    from = from,
                    to = to,
                    seed = UnityEngine.Random.Range(0, 10000)
                };
                effects.Add(e);
                Animate(e, 0f);
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Copycat] fx spawn failed: {ex.Message}");
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
                TickAura();
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Copycat] fx tick failed: {ex.Message}");
            }
        }

        public static void Clear() {
            foreach (var e in effects) if (e.go != null) UnityEngine.Object.Destroy(e.go);
            effects.Clear();
            if (auraGo != null) { UnityEngine.Object.Destroy(auraGo); auraGo = null; auraParts = null; }
        }

        private static void Animate(Effect e, float t) {
            if (e.kind == 1) { // tracer: a short streak sweeping from shooter to target, fading fast
                Vector2 head = Vector2.Lerp(e.from, e.to, Mathf.Clamp01(t * 2.2f));
                Vector2 dir = e.to - e.from;
                float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                for (int i = 0; i < e.parts.Length; i++) {
                    var sr = e.parts[i];
                    if (sr == null) continue;
                    float v = Hash(e.seed + i);
                    float trail = i / (float)Mathf.Max(1, e.parts.Length - 1);
                    // Positions are relative to the root, which stays fixed at `from` (never moved).
                    Vector2 segPos = Vector2.Lerp(e.from, head, Mathf.Clamp01(1f - trail * 0.5f)) - e.from;
                    sr.transform.localPosition = new Vector3(segPos.x, segPos.y, 0f);
                    sr.transform.localRotation = Quaternion.Euler(0, 0, ang);
                    sr.transform.localScale = new Vector3(0.5f - trail * 0.25f, 0.07f, 1f);
                    sr.color = Tint(Color.Lerp(White, Purple, v * 0.5f), (1f - t) * (1f - trail * 0.4f));
                }
                return;
            }

            // shimmer: a brief upward-drifting sparkle burst at the Copycat's own position
            for (int i = 0; i < e.parts.Length; i++) {
                var sr = e.parts[i];
                if (sr == null) continue;
                float u = Hash(e.seed + i);
                float v = Hash(e.seed + i * 7 + 3);
                float ang = u * Mathf.PI * 2f;
                float ease = 1f - (1f - t) * (1f - t);
                float r = 0.10f + ease * (0.40f + 0.30f * v);
                var pos = Rot(ang, r);
                pos.y += ease * 0.30f;
                sr.transform.localPosition = pos;
                sr.transform.localScale = Vector3.one * (0.18f + 0.12f * v) * (0.6f + t * 0.6f);
                sr.color = Tint(Color.Lerp(White, Purple, v), (1f - ease) * 0.85f);
            }
        }

        // ---- continuous: shield aura, visible ONLY to the local Copycat while shielded. Gate is
        // re-checked from scratch every frame, exactly like PoltergeistFx.TickAura. ----
        private static void TickAura() {
            bool show = Copycat.active && Copycat.shielded && Copycat.IsLocalCopycat()
                        && PlayerControl.LocalPlayer != null
                        && PlayerControl.LocalPlayer.Data != null
                        && !PlayerControl.LocalPlayer.Data.IsDead
                        && MeetingHud.Instance == null;
            if (!show) {
                if (auraGo != null) auraGo.SetActive(false);
                return;
            }
            if (auraGo == null) {
                auraGo = new GameObject("CopycatAura") { layer = 11 };
                auraParts = new SpriteRenderer[6];
                for (int i = 0; i < auraParts.Length; i++) {
                    var go = new GameObject($"a{i}") { layer = 11 };
                    go.transform.SetParent(auraGo.transform);
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = UCFx.Ring;
                    auraParts[i] = sr;
                }
            }
            auraGo.SetActive(true);
            var p = PlayerControl.LocalPlayer.GetTruePosition();
            auraGo.transform.position = new Vector3(p.x, p.y, -1.2f);
            float now = Time.time;
            for (int i = 0; i < auraParts.Length; i++) {
                float a = now * (0.7f + i * 0.11f) + i * 1.1f;
                float r = 0.40f + 0.08f * Mathf.Sin(now * 1.5f + i * 2f);
                auraParts[i].transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r * 0.7f + 0.05f, 0f);
                auraParts[i].transform.localScale = Vector3.one * (0.22f + 0.05f * Mathf.Sin(now * 2f + i));
                auraParts[i].color = Tint(Purple, 0.20f + 0.10f * Flicker(i, 4.5f));
            }
        }

        private static Vector3 Rot(float ang, float r) => new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, 0f);
        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
        private static float Flicker(int i, float speed) => Mathf.Abs(Mathf.Sin(Time.time * speed + i * 2.3f));
        private static float Hash(int n) { unchecked { n *= (int)2654435761u; n ^= n >> 13; return ((n & 0xFFFF) / 65535f); } }
    }
}
