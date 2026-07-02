// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Charge indicator shown to a charged victim. A small HUD label bottom-centre that reads "+ GELADEN" /
 * "- GELADEN" (polarity-colored cyan/orange, matching TeslaMeetingUI's own +/- color coding), switches
 * to a green "+ SCHUTZ" / "- SCHUTZ" during the post-meeting grace window (Tesla.InGrace(), so the
 * player isn't shown a false-safe baseline while actually protected), and to a pulsing red
 * "+ GEFAHR +" / "- GEFAHR -" once the victim is within trigger distance of their partner. The pulse
 * itself accelerates as the (locally mirrored, non-authoritative) countdown drains, so the visual
 * urgency actually tracks the real danger instead of a fixed rate.
 *
 * A small procedural lightning-bolt glyph (two crossed streak segments, built from the shared UCFx
 * sprite cache) sits to the left of the text and re-tints with it - the HUD font has no lightning/emoji
 * glyph of its own (renders as a box), so this reads at a glance instead of relying on ASCII markers
 * alone. NOT layer 11: this lives in HUD space under HudManager.transform, not the ship world (same as
 * every other HUD-parented sprite in this mod, e.g. SaboteurScanUI's rects).
 *
 * ShowSelfStatus/HideSelfStatus is a second, separate small label that gives the Tesla itself a rough,
 * explicitly non-authoritative read on whether its current pair is closing in - purely cosmetic, the
 * host's own Tesla.HostCountdown() is the only thing that actually decides life or death.
 */

using UnityEngine;

namespace UnknownsCollection {
    public static class TeslaIndicator {
        private static readonly Color Cyan = new Color(0.12f, 0.72f, 1f, 1f);
        private static readonly Color Orange = new Color(1f, 0.55f, 0f, 1f);
        private static readonly Color GraceGreen = new Color(0.45f, 0.85f, 0.45f, 1f);

        private static TMPro.TextMeshPro text;
        private static SpriteRenderer[] iconParts; // small procedural lightning-bolt glyph

        private static void Ensure() {
            if (text != null) return;
            var hud = HudManager.Instance;
            if (hud == null) return;
            var go = new GameObject("TeslaChargeIndicator");
            go.transform.SetParent(hud.transform);
            go.transform.localPosition = new Vector3(0f, -2.6f, -50f);
            go.transform.localScale = Vector3.one;
            text = go.AddComponent<TMPro.TextMeshPro>();
            text.fontSize = 2.2f;
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.enableWordWrapping = false;

            // Two short streak strokes forming a rough lightning-bolt zigzag next to the text.
            var iconGo = new GameObject("TeslaChargeIcon");
            iconGo.transform.SetParent(go.transform);
            iconGo.transform.localPosition = new Vector3(-1.9f, 0.02f, -0.1f);
            iconGo.transform.localScale = Vector3.one;
            iconParts = new SpriteRenderer[2];
            for (int i = 0; i < iconParts.Length; i++) {
                var p = new GameObject($"bolt{i}");
                p.transform.SetParent(iconGo.transform);
                var sr = p.AddComponent<SpriteRenderer>();
                sr.sprite = UCFx.Streak;
                iconParts[i] = sr;
            }
            iconParts[0].transform.localPosition = new Vector3(-0.05f, 0.09f, 0f);
            iconParts[0].transform.localRotation = Quaternion.Euler(0f, 0f, -50f);
            iconParts[0].transform.localScale = new Vector3(0.16f, 0.09f, 1f);
            iconParts[1].transform.localPosition = new Vector3(0.05f, -0.09f, 0f);
            iconParts[1].transform.localRotation = Quaternion.Euler(0f, 0f, -50f);
            iconParts[1].transform.localScale = new Vector3(0.16f, 0.09f, 1f);
        }

        // isPlus: which pole the local (charged) player holds. danger/grace: mutually exclusive states
        // (danger is only ever true outside of grace - see Tesla.LocalCosmetics). countdownFrac: 1 at
        // full time remaining, 0 at imminent death - only meaningful while danger is true.
        public static void Show(bool isPlus, bool danger, bool grace, float countdownFrac) {
            Ensure();
            if (text == null) return;
            text.gameObject.SetActive(true);
            string pole = isPlus ? "+" : "-";
            Color c;
            string label;
            if (danger) {
                float urgency = Mathf.Clamp01(1f - countdownFrac);
                float speed = Mathf.Lerp(2.5f, 10f, urgency); // escalates as the countdown drains
                float t = Mathf.PingPong(Time.time * speed, 1f);
                c = Color.Lerp(new Color(1f, 0.85f, 0.85f, 1f), new Color(1f, 0.05f, 0.05f, 1f), t);
                label = $"{pole} GEFAHR {pole}";
            } else if (grace) {
                c = GraceGreen;
                label = $"{pole} SCHUTZ";
            } else {
                c = isPlus ? Cyan : Orange;
                label = $"{pole} GELADEN";
            }
            text.color = c;
            text.text = label;
            if (iconParts != null)
                for (int i = 0; i < iconParts.Length; i++)
                    if (iconParts[i] != null) iconParts[i].color = c;
        }

        public static void Hide() {
            if (text != null && text.gameObject.activeSelf) text.gameObject.SetActive(false);
        }

        // ---- Tesla's own (non-authoritative) pair-status readout ----

        private static TMPro.TextMeshPro selfText;

        private static void EnsureSelf() {
            if (selfText != null) return;
            var hud = HudManager.Instance;
            if (hud == null) return;
            var go = new GameObject("TeslaSelfStatus");
            go.transform.SetParent(hud.transform);
            go.transform.localPosition = new Vector3(-3.3f, -2.15f, -50f);
            go.transform.localScale = Vector3.one;
            selfText = go.AddComponent<TMPro.TextMeshPro>();
            selfText.fontSize = 1.3f;
            selfText.alignment = TMPro.TextAlignmentOptions.Center;
            selfText.enableWordWrapping = false;
        }

        public static void ShowSelfStatus(bool close) {
            EnsureSelf();
            if (selfText == null) return;
            selfText.gameObject.SetActive(true);
            selfText.color = close ? new Color(1f, 0.55f, 0.3f, 1f) : new Color(0.7f, 0.7f, 0.7f, 1f);
            selfText.text = close ? "Pair closing in..." : "Pair separated";
        }

        public static void HideSelfStatus() {
            if (selfText != null && selfText.gameObject.activeSelf) selfText.gameObject.SetActive(false);
        }
    }
}
