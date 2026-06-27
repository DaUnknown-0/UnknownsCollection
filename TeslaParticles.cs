// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * Electric-spark effect shown around a charged victim while in the danger zone. Uses a small pool of
 * SpriteRenderers with a procedurally generated soft-dot sprite (reliable in the IL2CPP build, unlike a
 * runtime ParticleSystem whose default material often fails to render). Each frame the sparks jitter in
 * a circle around the player and flicker their alpha for a crackling electric look.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {
    public static class TeslaParticles {
        private const int Count = 12;
        private static readonly Color Tint = new Color(0.35f, 0.8f, 1f, 1f);

        private static GameObject host;
        private static readonly List<SpriteRenderer> sparks = new List<SpriteRenderer>();
        private static Sprite dot;
        private static bool shown;

        public static void SetActive(PlayerControl target, bool on) {
            try {
                if (!on || target == null) { Hide(); return; }
                Ensure();
                if (host == null) return;

                host.transform.position = new Vector3(
                    target.GetTruePosition().x, target.GetTruePosition().y, -1.0f);

                float t = Time.time;
                for (int i = 0; i < sparks.Count; i++) {
                    var s = sparks[i];
                    if (s == null) continue;
                    // Pseudo-random orbit + flicker, deterministic per-spark so it crackles smoothly.
                    float a = t * (3f + i * 0.7f) + i * 1.3f;
                    float r = 0.25f + 0.25f * Mathf.Abs(Mathf.Sin(t * 5f + i));
                    s.transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                    float alpha = 0.35f + 0.65f * Mathf.Abs(Mathf.Sin(t * 12f + i * 2f));
                    s.color = new Color(Tint.r, Tint.g, Tint.b, alpha);
                }
                if (!shown) { host.SetActive(true); shown = true; }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Tesla] particles failed: {e.Message}");
            }
        }

        public static void Hide() {
            if (host != null && shown) { host.SetActive(false); shown = false; }
        }

        private static void Ensure() {
            if (host != null) return;
            if (dot == null) dot = BuildDotSprite();
            host = new GameObject("TeslaSparks");
            for (int i = 0; i < Count; i++) {
                var go = new GameObject($"spark{i}");
                go.transform.SetParent(host.transform);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = dot;
                sr.color = Tint;
                go.transform.localScale = Vector3.one * (0.18f + 0.06f * (i % 3));
                sparks.Add(sr);
            }
            host.SetActive(false);
        }

        // 16x16 soft radial dot (white, radial alpha falloff). Tinted per spark via SpriteRenderer.color.
        private static Sprite BuildDotSprite() {
            const int n = 16;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            float c = (n - 1) / 2f;
            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++) {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (c + 1f);
                    float alpha = Mathf.Clamp01(1f - d);
                    alpha *= alpha; // softer edge
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        }
    }
}
