// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Charge indicator shown to a charged victim. A small HUD label that reads "⚡ GELADEN"; when the
 * victim is within trigger distance of their partner it switches to a pulsing red "⚡ GEFAHR" (no
 * number, by design). Created once under HudManager and positioned bottom-centre in HUD space.
 */

using UnityEngine;

namespace UnknownsCollection {
    public static class TeslaIndicator {
        private static TMPro.TextMeshPro text;

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
        }

        public static void Show(bool danger) {
            Ensure();
            if (text == null) return;
            text.gameObject.SetActive(true);
            if (danger) {
                float t = Mathf.PingPong(Time.time * 4f, 1f);
                text.color = Color.Lerp(new Color(1f, 0.85f, 0.85f, 1f), new Color(1f, 0.05f, 0.05f, 1f), t);
                text.text = "⚡ GEFAHR";
            } else {
                text.color = new Color(0.12f, 0.72f, 1f, 1f);
                text.text = "⚡ GELADEN";
            }
        }

        public static void Hide() {
            if (text != null && text.gameObject.activeSelf) text.gameObject.SetActive(false);
        }
    }
}
