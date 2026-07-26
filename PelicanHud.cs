// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * PelicanHud - the two on-screen readouts of the Pelican (Paket W3).
 *
 *  BELLY   Self-only. A small pelican_belly icon on the left edge plus the names of everyone the
 *          Pelican is currently carrying. Only the Pelican's own client ever calls ShowBelly(), and
 *          the caller re-gates it EVERY frame (Poltergeist/Shade precedent) instead of relying on a
 *          one-time check at creation - a stale overlay would leak "these players are dead" to the
 *          whole lobby.
 *  HUNT    Public. The countdown of the hunt phase, shown to EVERY player (that is the point: the
 *          last survivor has to feel the clock too). Turns red and pulses under ten seconds.
 *
 * WHY IT LOOKS LIKE THIS
 * ----------------------
 *  - Both elements are parented to HudManager.transform and therefore live in HUD space, NOT in the
 *    ship world: no layer 11 here (that rule is for procedural WORLD objects), and no CameraSafeArea
 *    fitting either - the HUD root already carries the game's own safe-area transform, exactly like
 *    TeslaIndicator's labels and SaboteurScanUI's rects.
 *  - TextMeshPro components are ADDED to fresh GameObjects rather than cloned from
 *    KillButton.cooldownTimerText: a clone inherits that button's RectTransform and would need the
 *    whole pivot/sizeDelta/margin collapse dance before its transform position means anything. A
 *    fresh component starts with an empty rect, so the transform IS the anchor.
 *  - The HUNT countdown gives way to the top-corner version list instead of sitting at a fixed
 *    height: that list is the vanilla PingTracker text and grows with every installed mod, so its
 *    real rendered bounds decide how far down the countdown goes (see PositionHunt).
 *  - Everything the labels print is ASCII (the HUD font has no glyphs for the usual box-drawing /
 *    check-mark characters - they render as empty rectangles). Player names are passed through as
 *    they are; those already render everywhere else in the game.
 */

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UnknownsCollection {
    public static class PelicanHud {
        private static readonly Color Teal = new Color(0.16f, 0.78f, 0.74f, 1f);
        private static readonly Color Warn = new Color(1f, 0.45f, 0.15f, 1f);

        private static TMPro.TextMeshPro bellyText;
        private static SpriteRenderer bellyIcon;
        private static TMPro.TextMeshPro huntText;

        // The top-corner version list is the vanilla PingTracker text, and it grows DOWNWARDS with
        // every installed mod - with six of them it reaches right into the countdown (playtest
        // 2026-07-26). Instead of guessing a height, the countdown is parked under the block's real
        // rendered bounds; it only ever moves DOWN from its designed spot, so a lobby with one mod
        // still gets the position this HUD was laid out for.
        private static PingTracker pingTracker;

        [HarmonyPatch(typeof(PingTracker), nameof(PingTracker.Update))]
        static class PingTrackerCachePatch {
            public static void Postfix(PingTracker __instance) { pingTracker = __instance; }
        }

        // ---- Belly (Pelican only) -------------------------------------------------------------

        private static void EnsureBelly() {
            if (bellyText != null) return;
            var hud = HudManager.Instance;
            if (hud == null) return;

            var iconGo = new GameObject("PelicanBellyIcon");
            iconGo.transform.SetParent(hud.transform);
            iconGo.transform.localPosition = new Vector3(-3.95f, 0.75f, -50f);
            iconGo.transform.localScale = Vector3.one * 0.60f;
            bellyIcon = iconGo.AddComponent<SpriteRenderer>();
            bellyIcon.sprite = UCAssets.PelicanBellySprite;

            var go = new GameObject("PelicanBellyText");
            go.transform.SetParent(hud.transform);
            go.transform.localPosition = new Vector3(-3.95f, 0.35f, -50f);
            go.transform.localScale = Vector3.one;
            bellyText = go.AddComponent<TMPro.TextMeshPro>();
            bellyText.fontSize = 1.25f;
            bellyText.alignment = TMPro.TextAlignmentOptions.Center;
            bellyText.enableWordWrapping = false;
            bellyText.color = Teal;
        }

        // names: display names of everyone currently swallowed (may be empty - the icon then simply
        // reads "0" so the Pelican still sees his own organ).
        public static void ShowBelly(List<string> names) {
            EnsureBelly();
            if (bellyText == null) return;
            if (bellyIcon != null) {
                bellyIcon.gameObject.SetActive(true);
                // The belly "fills up": a gentle tint shift plus a slow breathing pulse once it is
                // carrying anyone, so the Pelican notices the state change out of the corner of his eye.
                bool full = names != null && names.Count > 0;
                float pulse = full ? 1f + 0.05f * Mathf.Sin(Time.time * 2.2f) : 1f;
                bellyIcon.transform.localScale = Vector3.one * 0.60f * pulse;
                bellyIcon.color = full ? Color.white : new Color(1f, 1f, 1f, 0.55f);
            }
            bellyText.gameObject.SetActive(true);
            int count = names?.Count ?? 0;
            if (count == 0) {
                bellyText.text = UCLocalization.Tr("uc.ui.pelican.belly_empty");
                bellyText.color = new Color(Teal.r, Teal.g, Teal.b, 0.6f);
                return;
            }
            // Three names is all that fits without wrapping into the button row; the rest is a count.
            string list = string.Join(", ", names.GetRange(0, Mathf.Min(3, count)));
            if (count > 3) list += UCLocalization.Tr("uc.ui.pelican.belly_more", count - 3);
            bellyText.text = UCLocalization.Tr("uc.ui.pelican.belly_label", count) + "\n" + list;
            bellyText.color = Teal;
        }

        public static void HideBelly() {
            if (bellyText != null && bellyText.gameObject.activeSelf) bellyText.gameObject.SetActive(false);
            if (bellyIcon != null && bellyIcon.gameObject.activeSelf) bellyIcon.gameObject.SetActive(false);
        }

        // ---- Hunt countdown (everyone) --------------------------------------------------------

        private const float HuntY = 2.05f;      // designed spot: top centre, under the sabotage banner
        private const float HuntMinY = 0.95f;   // however long the version list gets, never sink past this
        private const float HuntGap = 0.28f;    // clearance between the version block and the countdown

        private static void EnsureHunt() {
            if (huntText != null) return;
            var hud = HudManager.Instance;
            if (hud == null) return;
            var go = new GameObject("PelicanHuntCountdown");
            go.transform.SetParent(hud.transform);
            go.transform.localPosition = new Vector3(0f, HuntY, -50f);
            go.transform.localScale = Vector3.one;
            huntText = go.AddComponent<TMPro.TextMeshPro>();
            huntText.fontSize = 2.4f;
            huntText.alignment = TMPro.TextAlignmentOptions.Center;
            huntText.enableWordWrapping = false;
        }

        // Drop the countdown below the version block. Renderer.bounds is the mesh's real world-space
        // box, so this counts whatever the block actually prints - vanilla ping line, TOR, and every
        // mod that appended a line of its own - without this file knowing any of them.
        private static void PositionHunt() {
            if (huntText == null) return;
            float y = HuntY;
            try {
                var hud = HudManager.Instance;
                if (hud != null && pingTracker != null && pingTracker.text != null) {
                    var r = pingTracker.text.GetComponent<Renderer>();
                    if (r != null && r.isVisible) {
                        float localBottom = hud.transform.InverseTransformPoint(
                            new Vector3(0f, r.bounds.min.y, 0f)).y;
                        y = Mathf.Clamp(localBottom - HuntGap, HuntMinY, HuntY);
                    }
                }
            } catch { }
            var p = huntText.transform.localPosition;
            if (!Mathf.Approximately(p.y, y)) huntText.transform.localPosition = new Vector3(p.x, y, p.z);
        }

        public static void ShowHunt(float secondsLeft) {
            EnsureHunt();
            if (huntText == null) return;
            huntText.gameObject.SetActive(true);
            float s = Mathf.Max(0f, secondsLeft);
            int mm = (int)(s / 60f);
            int ss = (int)(s % 60f);
            huntText.text = UCLocalization.Tr("uc.ui.pelican.hunt_title") + "  " + mm.ToString("0") + ":" + ss.ToString("00");
            PositionHunt();
            if (s <= 10f) {
                // Final ten seconds: a hard red flicker that speeds up as the clock drains.
                float t = Mathf.PingPong(Time.time * Mathf.Lerp(3f, 9f, 1f - s / 10f), 1f);
                huntText.color = Color.Lerp(Warn, new Color(1f, 0.05f, 0.05f, 1f), t);
            } else {
                huntText.color = Teal;
            }
        }

        public static void HideHunt() {
            if (huntText != null && huntText.gameObject.activeSelf) huntText.gameObject.SetActive(false);
        }

        public static void HideAll() { HideBelly(); HideHunt(); }
    }
}
