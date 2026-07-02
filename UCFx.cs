// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCFx - shared particle-effect infrastructure for ALL Unknown's Collection FX frameworks.
 *
 * IL2CPP note: runtime ParticleSystems are unreliable in the AU build, so every visual effect in the
 * mod is a small pool of SpriteRenderers with a procedurally generated sprite, animated by hand each
 * frame from a Tick(). This class centralizes the sprite cache (previously duplicated across
 * PoltergeistFx/TeslaParticles/SaboteurKillFx) plus two small registries so every current AND future
 * FX cluster can hook into a single per-frame driver and a single round-reset/cleanup driver instead
 * of wiring its own HudManager.Update/resetVariables patches.
 *
 * Ownership stays with the individual FX classes: they keep their own Animate() curves, their own
 * Tick() call sites (e.g. PoltergeistFx.Tick() is still invoked directly from Poltergeist.cs's own
 * HudManager.Update postfix - untouched by this file) and simply register a Clear()/reset delegate
 * here so a future cluster can no longer forget to clean up on round start.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class UCFx {
        // ---- Shared procedural sprite cache (built once, HideAndDontSave, reused by every FX class) ----
        private static Sprite dot;
        private static Sprite streak;
        private static Sprite ring;
        private static Sprite spark;
        private static Sprite smoke;

        public static Sprite Dot { get { if (dot == null) dot = BuildDot(); return dot; } }
        public static Sprite Streak { get { if (streak == null) streak = BuildStreak(); return streak; } }
        public static Sprite Ring { get { if (ring == null) ring = BuildRing(); return ring; } }
        public static Sprite Spark { get { if (spark == null) spark = BuildSpark(); return spark; } }
        public static Sprite Smoke { get { if (smoke == null) smoke = BuildSmoke(); return smoke; } }

        // ---- Registries: per-frame tick + round-reset/game-end cleanup ----
        private static readonly List<Action> ticks = new();
        private static readonly List<Action> resets = new();

        public static void RegisterTick(Action tick) { if (tick != null) ticks.Add(tick); }
        public static void RegisterReset(Action reset) { if (reset != null) resets.Add(reset); }

        // ---- GameObject helpers (layer 11 everywhere - the ship camera does not render Default) ----

        public static GameObject NewFxRoot(string name, Vector2 at, float z = -1.5f) {
            var go = new GameObject(name) { layer = 11 };
            go.transform.position = new Vector3(at.x, at.y, z);
            return go;
        }

        public static SpriteRenderer[] MakeParts(GameObject parent, int count, Func<int, Sprite> spriteFor) {
            var parts = new SpriteRenderer[count];
            for (int i = 0; i < count; i++) {
                var go = new GameObject($"p{i}") { layer = 11 };
                if (parent != null) go.transform.SetParent(parent.transform);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = spriteFor != null ? spriteFor(i) : Dot;
                parts[i] = sr;
            }
            return parts;
        }

        // Optional additive material for electric-look effects. Shader.Find can return null if the
        // shader was stripped from the IL2CPP build, so callers always get a safe default-material
        // fallback instead of a broken renderer.
        private static Shader additiveShader;
        private static bool additiveShaderChecked;

        public static void TryMakeAdditive(SpriteRenderer sr) {
            if (sr == null) return;
            try {
                if (!additiveShaderChecked) {
                    additiveShader = Shader.Find("Legacy Shaders/Particles/Additive");
                    additiveShaderChecked = true;
                }
                if (additiveShader != null) sr.material = new Material(additiveShader);
                // else: leave the default (alpha-blended) material - safe fallback.
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCFx] additive material failed: {e.Message}");
            }
        }

        // ---- Drivers ----

        private static void RunTicks() {
            for (int i = 0; i < ticks.Count; i++) {
                try { ticks[i]?.Invoke(); }
                catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[UCFx] tick failed: {e.Message}"); }
            }
        }

        private static void RunResets() {
            for (int i = 0; i < resets.Count; i++) {
                try { resets[i]?.Invoke(); }
                catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[UCFx] reset failed: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() { RunTicks(); }
        }

        // Same patch target as Poltergeist.ResetAll (resetVariables runs at round start).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { RunResets(); }
        }

        // Belt-and-suspenders cleanup at game end too (same pattern as Tesla.cs' own OnGameEnd patch) -
        // resetVariables already clears everything at the *next* game's start, but running the same
        // registry here means a lingering effect never survives into the post-game screen.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch {
            public static void Postfix() { RunResets(); }
        }

        // ---- Sprite builders ----

        // Soft radial dot (24px). Identical to the former PoltergeistFx.BuildDot.
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

        // Horizontal soft streak (48x12): bright center line fading to the ends and edges.
        // Identical to the former PoltergeistFx.BuildStreak.
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

        // Soft ring (32px): alpha peaks around r~0.78 and falls off softly toward both the center and
        // the outer edge (parabolic band), for orbit/aura-style duration indicators.
        private static Sprite BuildRing() {
            const int n = 32;
            const float peak = 0.78f;
            const float halfWidth = 0.22f;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            float c = (n - 1) / 2f;
            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++) {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (c + 1f);
                    float delta = (d - peak) / halfWidth;
                    float alpha = Mathf.Clamp01(1f - delta * delta);
                    alpha *= alpha;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            var s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            s.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            return s;
        }

        // Hard bright spark (16px): steep falloff for a small, punchy core - electric-arc look.
        private static Sprite BuildSpark() {
            const int n = 16;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            float c = (n - 1) / 2f;
            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++) {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (c + 1f);
                    float alpha = Mathf.Clamp01(1f - d);
                    alpha = alpha * alpha * alpha * alpha; // steeper than Dot -> hard, bright core
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            var s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            s.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            return s;
        }

        // Irregular soft smoke puff (32px): radial falloff perturbed by deterministic bilinear value
        // noise, so the silhouette reads as an uneven puff instead of a perfect circle. Fully
        // deterministic (no runtime RNG) since the sprite is built once and cached.
        private static Sprite BuildSmoke() {
            const int n = 32;
            const int cell = 8;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            float c = (n - 1) / 2f;
            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++) {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (c + 1f);
                    float radial = Mathf.Clamp01(1f - d);
                    radial *= radial;

                    int gx = x / cell, gy = y / cell;
                    float fx = (x % cell) / (float)cell;
                    float fy = (y % cell) / (float)cell;
                    float n00 = ValueNoiseHash(gx, gy);
                    float n10 = ValueNoiseHash(gx + 1, gy);
                    float n01 = ValueNoiseHash(gx, gy + 1);
                    float n11 = ValueNoiseHash(gx + 1, gy + 1);
                    float nx0 = Mathf.Lerp(n00, n10, fx);
                    float nx1 = Mathf.Lerp(n01, n11, fx);
                    float noise = Mathf.Lerp(nx0, nx1, fy);

                    float alpha = Mathf.Clamp01(radial * (0.55f + 0.55f * noise));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            var s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            s.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            return s;
        }

        private static float ValueNoiseHash(int x, int y) {
            unchecked {
                int n = x * 374761393 + y * 668265263;
                n = (n ^ (n >> 13)) * 1274126177;
                n ^= n >> 16;
                return (n & 0x7fffffff) / (float)int.MaxValue;
            }
        }
    }
}
