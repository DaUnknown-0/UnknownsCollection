// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Beacon visual/audio feedback. Before this file, the Beacon gave literally no feedback to anyone at
 * any point in the round - LightPatch (Beacon.cs) silently swaps the vanilla vision-radius number and
 * nothing else reacts. Both cues below are STRICTLY local/self-only and are NEVER anchored to the
 * Beacon's own world position: a visible/audible tell AT the Beacon would leak its identity and
 * location to Impostors - a passive role with no button has no other defence. See SPEC.md's
 * bug-beacon cluster note ("NIEMALS etwas am Beacon-Standort weltverankern").
 *
 *   - Share pulse: fires only on the PROFITING crewmate's own client, on the false->true edge of
 *     Beacon.LocalGetsShare() (edge-triggered state machine, same shape as PoltergeistFx.TickChannel) -
 *     beacon_share plays once and a warm-gold screen vignette fades in; both fade back out softly on
 *     the true->false edge (leaving) instead of a fixed-duration flash.
 *   - Status badge: a small HUD text, visible only to the Beacon itself, that lights up while Lights
 *     sabotage is active AND the local player IS the Beacon - the one moment the passive immunity
 *     actually matters, otherwise the role is silent for the entire round.
 *
 * Driven by UCFx's shared per-frame Tick / round-reset registries, same as every other UC FX class.
 */

using System;
using TheOtherRoles.Utilities;
using UnityEngine;
using UnityEngine.UI;

namespace UnknownsCollection {
    public static class BeaconFx {
        static BeaconFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // Unlike TeslaKillFx/SaboteurKillFx (touched naturally the first time their owner role fires a
        // gameplay-driven Play() call), this class is purely self-polling - nothing in Beacon.cs ever
        // references it otherwise, so its static constructor above would never run and the tick/reset
        // registration would silently never happen. Called once from Beacon.CreateOptions() (plugin
        // startup, unconditional) purely to force that one-time touch.
        public static void Init() { }

        private static void Tick() {
            try {
                TickSharePulse();
                TickStatusBadge();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[BeaconFx] tick failed: {e.Message}");
            }
        }

        private static void Clear() {
            wasSharing = false;
            shareAlpha = 0f;
            if (vignetteImg != null) vignetteImg.color = new Color(Beacon.Color.r, Beacon.Color.g, Beacon.Color.b, 0f);
            if (vignetteGo != null) vignetteGo.SetActive(false);
            if (badgeText != null) badgeText.gameObject.SetActive(false);
        }

        // ---- share pulse: warm vignette + one-shot chime, gated to the false->true edge only ----

        private static bool wasSharing;
        private static float shareAlpha;
        private static GameObject vignetteGo;
        private static RawImage vignetteImg;

        private static void TickSharePulse() {
            bool sharing = Beacon.LocalGetsShare();
            if (sharing && !wasSharing) UCAssets.PlayBeaconShare();
            wasSharing = sharing;

            // Fast fade in, gentle fade out ("sanftes Ausklingen beim Verlassen" per SPEC.md).
            float target = sharing ? 1f : 0f;
            float rate = sharing ? 6f : 1.8f;
            shareAlpha = Mathf.MoveTowards(shareAlpha, target, Time.deltaTime * rate);

            if (shareAlpha <= 0.001f) {
                if (vignetteGo != null) vignetteGo.SetActive(false);
                return;
            }
            EnsureVignette();
            if (vignetteGo == null || vignetteImg == null) return;
            vignetteGo.SetActive(true);
            // Slow living pulse while fully active, so it reads as an ongoing state rather than a
            // static tint.
            float pulse = sharing ? 0.85f + 0.15f * Mathf.Sin(Time.time * 2.2f) : 1f;
            float a = shareAlpha * 0.35f * pulse;
            vignetteImg.color = new Color(Beacon.Color.r, Beacon.Color.g, Beacon.Color.b, a);
        }

        private static void EnsureVignette() {
            if (vignetteGo != null) return;
            var hud = HudManager.Instance;
            if (hud == null) return;
            try {
                var canvasGo = new GameObject("BeaconShareVignette");
                canvasGo.transform.SetParent(hud.transform, false);
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 400; // above world/HUD, below Helpers.showFlash's full-screen flashes (999)

                var imgGo = new GameObject("Img");
                imgGo.transform.SetParent(canvasGo.transform, false);
                var rt = imgGo.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;

                vignetteImg = imgGo.AddComponent<RawImage>();
                vignetteImg.texture = BuildVignetteTex();
                vignetteImg.color = new Color(Beacon.Color.r, Beacon.Color.g, Beacon.Color.b, 0f);
                vignetteGo = canvasGo;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[BeaconFx] vignette build failed: {e.Message}");
            }
        }

        // Small radial-gradient texture: transparent centre, opaque toward the edges - a true vignette
        // rather than a flat full-screen tint (RawImage stretches this to fill the whole screen, so the
        // gradient reads correctly at any resolution).
        private static Texture2D BuildVignetteTex() {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            float c = (n - 1) / 2f;
            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++) {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (c + 1f);
                    float alpha = Mathf.Clamp01((d - 0.35f) / 0.65f);
                    alpha *= alpha;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            return tex;
        }

        // ---- status badge: self-only, lights up only while Lights sabotage is active AND local IS the Beacon ----

        private static TMPro.TextMeshPro badgeText;

        private static void TickStatusBadge() {
            bool show = Beacon.IsLocalBeacon() && LightsSabotageActive();
            if (!show) {
                if (badgeText != null) badgeText.gameObject.SetActive(false);
                return;
            }
            EnsureBadge();
            if (badgeText == null) return;
            badgeText.gameObject.SetActive(true);
            float pulse = 0.7f + 0.3f * Mathf.Sin(Time.time * 3f);
            badgeText.color = new Color(Beacon.Color.r, Beacon.Color.g, Beacon.Color.b, pulse);
        }

        private static void EnsureBadge() {
            if (badgeText != null) return;
            var hud = HudManager.Instance;
            if (hud == null) return;
            var go = new GameObject("BeaconStatusBadge");
            go.transform.SetParent(hud.transform);
            go.transform.localPosition = new Vector3(0f, 2.9f, -50f); // top-centre, near the sabotage banner area
            go.transform.localScale = Vector3.one;
            badgeText = go.AddComponent<TMPro.TextMeshPro>();
            badgeText.fontSize = 1.8f;
            badgeText.alignment = TMPro.TextAlignmentOptions.Center;
            badgeText.enableWordWrapping = false;
            badgeText.text = "* BEACON ACTIVE *";
        }

        // Same probe TOR's own SabotageTuning/Siphoner use to read whether Lights sabotage is currently
        // active (synced system state, identical on every client): cast the Electrical system to
        // SwitchSystem and read its IsActive flag.
        private static bool LightsSabotageActive() {
            try {
                var ship = MapUtilities.CachedShipStatus;
                if (ship == null || ship.Systems == null) return false;
                if (!ship.Systems.TryGetValue(SystemTypes.Electrical, out ISystemType sys) || sys == null) return false;
                var sw = sys.TryCast<SwitchSystem>();
                return sw != null && sw.IsActive;
            } catch {
                return false;
            }
        }
    }
}
