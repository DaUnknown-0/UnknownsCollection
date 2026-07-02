// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Saboteur kill effect - the generic "electric death" played on a victim who finished a sabotaged
 * task console. A short burst of crackling sparks orbits the victim while a violet flash and a zap
 * cue fire, then the regular death animation takes over (the lethal murder is a separate RPC).
 *
 * Self-contained one-shot effect (its own sprite pool + coroutine), so it does not interfere with the
 * Tesla's continuous danger sparks. v1 uses a generic electric look; per-task animations (med-scan
 * laser, ...) are a later phase (the kill RPC already carries a taskType slot for that).
 */

using System;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class SaboteurKillFx {
        private const int SparkCount = 14;
        private const float Duration = 0.75f;
        private static readonly Color Violet = new Color(0.72f, 0.25f, 1f, 1f);

        private static Sprite dot;

        public static void Play(PlayerControl victim) {
            try {
                if (victim == null) return;

                // Screen flash + electrocution zap (distance-attenuated; everyone runs Play via the FX RPC).
                Helpers.showFlash(new Color(0.6f, 0.1f, 0.95f, 1f), 0.5f);
                UCAssets.PlayZap(victim.GetTruePosition());

                if (dot == null) dot = BuildDotSprite();
                // layer 11 like every TOR world object - the ship camera does not render Default
                var host = new GameObject("SaboteurZap") { layer = 11 };
                host.transform.position = new Vector3(
                    victim.GetTruePosition().x, victim.GetTruePosition().y, -1.0f);

                var sparks = new SpriteRenderer[SparkCount];
                for (int i = 0; i < SparkCount; i++) {
                    var go = new GameObject($"zap{i}") { layer = 11 };
                    go.transform.SetParent(host.transform);
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = dot;
                    sr.color = Violet;
                    go.transform.localScale = Vector3.one * (0.18f + 0.08f * (i % 3));
                    sparks[i] = sr;
                }

                var hud = HudManager.Instance;
                if (hud == null) { UnityEngine.Object.Destroy(host); return; }

                hud.StartCoroutine(Effects.Lerp(Duration, new Action<float>((t) => {
                    if (host == null) return;
                    float time = Time.time;
                    for (int i = 0; i < sparks.Length; i++) {
                        var s = sparks[i];
                        if (s == null) continue;
                        float a = time * (14f + i * 1.3f) + i * 1.7f;       // fast electric orbit
                        float r = 0.15f + 0.45f * Mathf.Abs(Mathf.Sin(time * 22f + i));
                        s.transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                        float flicker = 0.4f + 0.6f * Mathf.Abs(Mathf.Sin(time * 30f + i * 2f));
                        float fade = 1f - t;                                  // fade out over the burst
                        s.color = new Color(Violet.r, Violet.g, Violet.b, flicker * fade);
                    }
                    if (t >= 1f) UnityEngine.Object.Destroy(host);
                })));
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Saboteur] kill FX failed: {e.Message}");
            }
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
                    alpha *= alpha;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        }
    }
}
