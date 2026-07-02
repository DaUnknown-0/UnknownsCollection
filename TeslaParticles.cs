// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * Electric-spark effect shown around a charged victim while in the danger zone. Uses a small pool of
 * SpriteRenderers with the shared UCFx soft-dot sprite (reliable in the IL2CPP build, unlike a
 * runtime ParticleSystem whose default material often fails to render). Each frame the sparks jitter in
 * a circle around the player and flicker their alpha for a crackling electric look.
 *
 * Tinted cyan for the positive pole / orange for the negative one (matching TeslaMeetingUI's own +/-
 * color coding), and fades out over ~0.4s (alpha AND scale) instead of hard-cutting when the victim
 * leaves the danger zone - escaping the danger zone is a small positive moment and deserves a soft
 * payoff rather than an abrupt pop. Re-entering danger mid-fade cleanly aborts the fade and snaps back
 * to full strength (no overlap with a fresh on-cycle).
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {
    public static class TeslaParticles {
        private const int Count = 12;
        private const float FadeDuration = 0.4f;
        private static readonly Color PlusTint = new Color(0.12f, 0.72f, 1f, 1f);   // cyan, matches TeslaMeetingUI
        private static readonly Color MinusTint = new Color(1f, 0.55f, 0f, 1f);     // orange, matches TeslaMeetingUI

        private static GameObject host;
        private static readonly List<SpriteRenderer> sparks = new List<SpriteRenderer>();
        private static readonly List<float> baseScale = new List<float>();
        private static bool shown;
        private static bool wasOn;       // last `on` value passed to SetActive
        private static bool fadingOut;   // mid fade-out (on just went false)
        private static float fadeStart;  // Time.time when the fade-out began

        // Registered once so a lingering danger-zone orbit can never survive a round reset, even if
        // Tesla.LocalCosmetics() (which normally self-heals every frame by calling Hide() once the
        // local player is no longer charged) somehow stops running mid-transition.
        static TeslaParticles() {
            UCFx.RegisterReset(Clear);
        }

        public static void SetActive(PlayerControl target, bool on, bool isPlus) {
            try {
                if (target == null) { Hide(); return; }
                Ensure();
                if (host == null) return;

                if (on) {
                    fadingOut = false; // re-entering danger mid-fade snaps straight back to full strength
                } else if (wasOn && !fadingOut) {
                    fadingOut = true;
                    fadeStart = Time.time;
                }
                wasOn = on;

                float fade = 1f;
                if (fadingOut) {
                    float u = Mathf.Clamp01((Time.time - fadeStart) / FadeDuration);
                    fade = 1f - u;
                    if (u >= 1f) { Hide(); return; }
                } else if (!on) {
                    Hide();
                    return;
                }

                host.transform.position = new Vector3(
                    target.GetTruePosition().x, target.GetTruePosition().y, -1.0f);

                Color tint = isPlus ? PlusTint : MinusTint;
                float t = Time.time;
                float scaleMul = Mathf.Lerp(0.55f, 1f, fade); // shrink slightly while fading out
                for (int i = 0; i < sparks.Count; i++) {
                    var s = sparks[i];
                    if (s == null) continue;
                    // Pseudo-random orbit + flicker, deterministic per-spark so it crackles smoothly.
                    float a = t * (3f + i * 0.7f) + i * 1.3f;
                    float r = 0.25f + 0.25f * Mathf.Abs(Mathf.Sin(t * 5f + i));
                    s.transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                    float alpha = (0.35f + 0.65f * Mathf.Abs(Mathf.Sin(t * 12f + i * 2f))) * fade;
                    s.color = new Color(tint.r, tint.g, tint.b, alpha);
                    float baseS = i < baseScale.Count ? baseScale[i] : 0.2f;
                    s.transform.localScale = Vector3.one * baseS * scaleMul;
                }
                if (!shown) { host.SetActive(true); shown = true; }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Tesla] particles failed: {e.Message}");
            }
        }

        public static void Hide() {
            if (host != null && shown) { host.SetActive(false); shown = false; }
            fadingOut = false;
            wasOn = false;
        }

        // Explicit reset hook (see the static constructor above): unconditionally hides the orbit,
        // independent of the `shown` bookkeeping flag, so it is safe to call at any time.
        public static void Clear() {
            try {
                if (host != null) host.SetActive(false);
                shown = false;
                fadingOut = false;
                wasOn = false;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Tesla] particles clear failed: {e.Message}");
            }
        }

        private static void Ensure() {
            if (host != null) return;
            // layer 11 like every TOR world object - the ship camera does not render Default
            host = new GameObject("TeslaSparks") { layer = 11 };
            for (int i = 0; i < Count; i++) {
                var go = new GameObject($"spark{i}") { layer = 11 };
                go.transform.SetParent(host.transform);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = UCFx.Dot;
                sr.color = PlusTint;
                float scale = 0.18f + 0.06f * (i % 3);
                go.transform.localScale = Vector3.one * scale;
                baseScale.Add(scale);
                UCFx.TryMakeAdditive(sr); // electric look; safely falls back to the default material
                sparks.Add(sr);
            }
            host.SetActive(false);
        }
    }
}
