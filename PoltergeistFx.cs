// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Poltergeist visual effects. Same reliable technique as TeslaParticles (pooled SpriteRenderers with
 * procedurally generated sprites - runtime ParticleSystems often fail to render in the IL2CPP build),
 * but layered: a soft radial dot AND an elongated streak sprite, combined into
 *
 *   - DoorBurst:  violet/cyan wisps imploding into the door plus radial streaks (the "slam"),
 *   - HexBurst:   stars spiraling up around the hexed player,
 *   - Poof:       an airy white puff that expands, drifts up and dissolves (manifest end),
 *   - Channel:    a pulsing cyan orb ring while the Ghost Hand holds a reactor console,
 *   - Aura:       faint wisps orbiting the Poltergeist, shown ONLY to the Poltergeist itself.
 *
 * All effects are droven from Tick(), called every frame by Poltergeist's HudManager.Update patch.
 * One-shot effects own a small GameObject that is destroyed when their lifetime ends.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {
    public static class PoltergeistFx {
        private static readonly Color Violet = new Color(0.62f, 0.42f, 1f);
        private static readonly Color Cyan = new Color(0.47f, 0.92f, 1f);
        private static readonly Color White = new Color(0.94f, 0.97f, 1f);

        private static Sprite dot;
        private static Sprite streak;

        // ---- One-shot effect bookkeeping ----
        private sealed class Effect {
            public GameObject go;
            public SpriteRenderer[] parts;
            public float start;
            public float life;
            public int kind; // 0 door, 1 hex, 2 poof
            public Vector2 origin;
            public PlayerControl follow; // hex follows its target
            public int seed;
        }
        private static readonly List<Effect> effects = new();

        // ---- Continuous effects ----
        private static GameObject auraGo;
        private static SpriteRenderer[] auraParts;
        private static GameObject channelGo;
        private static SpriteRenderer[] channelParts;
        private static Vector2 channelPos;
        private static bool channelOn;

        public static void SpawnDoorBurst(Vector2 at) => Spawn(0, at, 1.1f, 18, null);
        public static void SpawnHexBurst(PlayerControl target) {
            if (target != null) Spawn(1, target.GetTruePosition(), 1.2f, 14, target);
        }
        public static void SpawnPoof(Vector2 at) => Spawn(2, at, 0.95f, 20, null);

        public static void SetChannel(Vector2 at, bool on) { channelPos = at; channelOn = on; }

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
                TickAura();
                TickChannel();
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Poltergeist] fx tick failed: {ex.Message}");
            }
        }

        public static void Clear() {
            foreach (var e in effects) if (e.go != null) UnityEngine.Object.Destroy(e.go);
            effects.Clear();
            if (auraGo != null) { UnityEngine.Object.Destroy(auraGo); auraGo = null; auraParts = null; }
            if (channelGo != null) { UnityEngine.Object.Destroy(channelGo); channelGo = null; channelParts = null; }
            channelOn = false;
        }

        // ---- one-shots ----

        private static void Spawn(int kind, Vector2 at, float life, int count, PlayerControl follow) {
            try {
                Ensure();
                var e = new Effect {
                    // layer 11 like every TOR world object - the ship camera does not render Default
                    go = new GameObject("PoltergeistFx") { layer = 11 },
                    parts = new SpriteRenderer[count],
                    start = Time.time,
                    life = life,
                    kind = kind,
                    origin = at,
                    follow = follow,
                    seed = UnityEngine.Random.Range(0, 10000)
                };
                e.go.transform.position = new Vector3(at.x, at.y, -1.5f);
                for (int i = 0; i < count; i++) {
                    var go = new GameObject($"p{i}") { layer = 11 };
                    go.transform.SetParent(e.go.transform);
                    var sr = go.AddComponent<SpriteRenderer>();
                    // Mix dots and streaks; streaks sell motion, dots sell volume.
                    sr.sprite = (i % 3 == 0) ? streak : dot;
                    e.parts[i] = sr;
                }
                effects.Add(e);
                Animate(e, 0f);
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Poltergeist] fx spawn failed: {ex.Message}");
            }
        }

        private static void Animate(Effect e, float t) {
            if (e.follow != null && e.follow.gameObject != null)
                e.go.transform.position = new Vector3(e.follow.GetTruePosition().x, e.follow.GetTruePosition().y, -1.5f);

            for (int i = 0; i < e.parts.Length; i++) {
                var sr = e.parts[i];
                if (sr == null) continue;
                float u = Hash(e.seed + i);          // stable per-particle random 0..1
                float v = Hash(e.seed + i * 7 + 3);
                float ang = u * Mathf.PI * 2f;

                switch (e.kind) {
                    case 0: { // Door slam: wisps rush INWARD (1.4 -> 0.1), streaks flash outward at impact
                        bool isStreak = i % 3 == 0;
                        if (isStreak) {
                            float flash = Mathf.Clamp01(1f - Mathf.Abs(t - 0.25f) * 6f);
                            float r = 0.25f + t * 1.1f;
                            sr.transform.localPosition = Rot(ang, r);
                            sr.transform.localRotation = Quaternion.Euler(0, 0, ang * Mathf.Rad2Deg);
                            sr.transform.localScale = new Vector3(0.65f, 0.10f, 1f);
                            sr.color = Tint(Cyan, flash * 0.9f);
                        } else {
                            float ease = 1f - (1f - t) * (1f - t);   // ease-out toward the door
                            float r = Mathf.Lerp(1.4f * (0.5f + v), 0.08f, ease);
                            float swirl = ang + t * (2.5f + v * 2f);
                            sr.transform.localPosition = Rot(swirl, r);
                            sr.transform.localScale = Vector3.one * (0.24f + 0.12f * v) * (1f - t * 0.5f);
                            sr.color = Tint(Color.Lerp(Violet, Cyan, v), (1f - t) * (0.55f + 0.45f * Flicker(i, 11f)));
                        }
                        break;
                    }
                    case 1: { // Hex: sparkles spiral upward around the target
                        float rise = t * (0.9f + 0.5f * v);
                        float r = 0.45f * (1f - t * 0.35f);
                        float swirl = ang + t * (5f + v * 3f);
                        sr.transform.localPosition = new Vector3(Mathf.Cos(swirl) * r, rise - 0.3f + Mathf.Sin(swirl) * r * 0.35f, 0f);
                        sr.transform.localScale = Vector3.one * (0.16f + 0.10f * v) * (1f - t * t);
                        sr.color = Tint(Color.Lerp(Cyan, White, u), (1f - t) * (0.6f + 0.4f * Flicker(i, 14f)));
                        if (i % 3 == 0) { // streaks act as tiny star twinkles here
                            sr.transform.localScale = new Vector3(0.30f * (1f - t), 0.07f, 1f);
                            sr.transform.localRotation = Quaternion.Euler(0, 0, u * 360f + t * 90f);
                        }
                        break;
                    }
                    default: { // Poof: puff expands, drifts up, dissolves
                        float ease = 1f - (1f - t) * (1f - t);
                        float r = 0.15f + ease * (0.7f + 0.5f * v);
                        var pos = Rot(ang, r);
                        pos.y += ease * 0.55f;                        // buoyant drift
                        sr.transform.localPosition = pos;
                        sr.transform.localScale = Vector3.one * (0.30f + 0.22f * v) * (0.6f + t * 0.8f);
                        sr.color = Tint(Color.Lerp(White, Cyan, v * 0.5f), (1f - ease) * 0.85f);
                        if (i % 3 == 0) sr.color = Tint(White, Mathf.Clamp01(1f - t * 4f)); // brief core flash
                        break;
                    }
                }
            }
        }

        // ---- continuous: aura around the local Poltergeist (only it sees this) ----

        private static void TickAura() {
            bool show = Poltergeist.IsLocalPoltergeist()
                        && PlayerControl.LocalPlayer != null
                        && PlayerControl.LocalPlayer.Data != null
                        && PlayerControl.LocalPlayer.Data.IsDead
                        && MeetingHud.Instance == null;
            if (!show) {
                if (auraGo != null) auraGo.SetActive(false);
                return;
            }
            Ensure();
            if (auraGo == null) {
                auraGo = new GameObject("PoltergeistAura") { layer = 11 };
                auraParts = new SpriteRenderer[7];
                for (int i = 0; i < auraParts.Length; i++) {
                    var go = new GameObject($"a{i}") { layer = 11 };
                    go.transform.SetParent(auraGo.transform);
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = dot;
                    auraParts[i] = sr;
                }
            }
            auraGo.SetActive(true);
            var p = PlayerControl.LocalPlayer.GetTruePosition();
            auraGo.transform.position = new Vector3(p.x, p.y, -1.2f);
            float now = Time.time;
            for (int i = 0; i < auraParts.Length; i++) {
                float a = now * (0.8f + i * 0.13f) + i * 0.9f;
                float r = 0.42f + 0.10f * Mathf.Sin(now * 1.7f + i * 2.1f);
                auraParts[i].transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r * 0.75f + 0.05f, 0f);
                auraParts[i].transform.localScale = Vector3.one * (0.14f + 0.05f * Mathf.Sin(now * 2.3f + i));
                auraParts[i].color = Tint(Violet, 0.22f + 0.10f * Flicker(i, 5f));
            }
        }

        // ---- continuous: channel ring while the Ghost Hand holds a console ----

        private static void TickChannel() {
            if (!channelOn) {
                if (channelGo != null) channelGo.SetActive(false);
                return;
            }
            Ensure();
            if (channelGo == null) {
                channelGo = new GameObject("PoltergeistChannel") { layer = 11 };
                channelParts = new SpriteRenderer[9];
                for (int i = 0; i < channelParts.Length; i++) {
                    var go = new GameObject($"c{i}") { layer = 11 };
                    go.transform.SetParent(channelGo.transform);
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = dot;
                    channelParts[i] = sr;
                }
            }
            channelGo.SetActive(true);
            channelGo.transform.position = new Vector3(channelPos.x, channelPos.y, -1.5f);
            float now = Time.time;
            float pulse = 0.85f + 0.15f * Mathf.Sin(now * 5f);
            for (int i = 0; i < channelParts.Length; i++) {
                float a = now * 2.2f + i * Mathf.PI * 2f / channelParts.Length;
                float r = 0.34f * pulse;
                channelParts[i].transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                channelParts[i].transform.localScale = Vector3.one * 0.16f;
                channelParts[i].color = Tint(Cyan, 0.55f + 0.30f * Flicker(i, 9f));
            }
        }

        // ---- helpers ----

        private static Vector3 Rot(float ang, float r) => new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, 0f);
        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
        private static float Flicker(int i, float speed) => Mathf.Abs(Mathf.Sin(Time.time * speed + i * 2.3f));
        private static float Hash(int n) { unchecked { n *= (int)2654435761u; n ^= n >> 13; return ((n & 0xFFFF) / 65535f); } }

        private static void Ensure() {
            if (dot == null) dot = BuildDot();
            if (streak == null) streak = BuildStreak();
        }

        // Soft radial dot (like TeslaParticles, slightly larger for smoother scaling).
        private static Sprite BuildDot() {
            const int n = 24;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            float c = (n - 1) / 2f;
            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++) {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (c + 1f);
                    float alpha = Mathf.Clamp01(1f - d);
                    alpha *= alpha;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            var s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            s.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            return s;
        }

        // Horizontal soft streak: bright center line fading to the ends and edges.
        private static Sprite BuildStreak() {
            const int w = 48, h = 12;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++) {
                    float fx = 1f - Mathf.Abs(x - (w - 1) / 2f) / (w / 2f);
                    float fy = 1f - Mathf.Abs(y - (h - 1) / 2f) / (h / 2f);
                    float alpha = Mathf.Clamp01(fx * fx * fy * fy * 1.6f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            var s = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), h);
            s.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            return s;
        }
    }
}
