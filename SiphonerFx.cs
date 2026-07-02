// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Siphoner visual effects.
 *
 * The Siphoner's drain has a configurable proximity range (Siphoner.DrainRange) but, before this,
 * absolutely no way to see it - the player had to guess the effective radius by trial and error. This
 * adds a faint pulsing ring of dots, radius == the current DrainRange option, orbiting the Siphoner
 * while the drain is active.
 *
 * Strictly self-only, gated EVERY tick (mirrors PoltergeistFx.TickAura): only the drain range itself is
 * shown, never whether an Impostor is actually caught in it right now - the Siphoner's own client has no
 * way to know that (only the host resolves impostor identity + the proximity check), so drawing a
 * "target acquired" indicator here would require new information the client doesn't have. Showing a
 * fixed-radius ring the Siphoner already implicitly knows the size of (it's their own option value) is
 * not a new leak.
 */

using System;
using UnityEngine;

namespace UnknownsCollection {
    public static class SiphonerFx {
        private static GameObject ringGo;
        private static SpriteRenderer[] ringParts;

        static SiphonerFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // No-op call target - exists purely so Siphoner.cs can force this class's static constructor
        // (and therefore the Tick/Reset registration above) to run at plugin bootstrap, since - unlike
        // FollowerFx/CrewFx - nothing here is triggered by a one-shot Spawn call; the ring is a fully
        // continuous, gate-checked-every-frame effect with no natural "first touch" of its own.
        public static void Init() { }

        private static void Tick() {
            try {
                bool show = Siphoner.DrainActive && Siphoner.IsLocalSiphoner()
                            && PlayerControl.LocalPlayer != null
                            && PlayerControl.LocalPlayer.Data != null
                            && !PlayerControl.LocalPlayer.Data.IsDead
                            && MeetingHud.Instance == null && ExileController.Instance == null;
                if (!show) {
                    if (ringGo != null) ringGo.SetActive(false);
                    return;
                }
                if (ringGo == null) {
                    ringGo = new GameObject("SiphonerRangeRing") { layer = 11 };
                    ringParts = UCFx.MakeParts(ringGo, 14, i => UCFx.Dot);
                }
                ringGo.SetActive(true);
                var pos = PlayerControl.LocalPlayer.GetTruePosition();
                ringGo.transform.position = new Vector3(pos.x, pos.y, -1.3f);

                float range = Mathf.Max(0.1f, Siphoner.CurrentDrainRange());
                float now = Time.time;
                float pulse = 0.94f + 0.06f * Mathf.Sin(now * 3f);
                var c = Siphoner.Color;
                for (int i = 0; i < ringParts.Length; i++) {
                    float a = (float)i / ringParts.Length * Mathf.PI * 2f + now * 0.5f;
                    float r = range * pulse;
                    ringParts[i].transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                    ringParts[i].transform.localScale = Vector3.one * 0.15f;
                    ringParts[i].color = new Color(c.r, c.g, c.b, 0.30f + 0.14f * Flicker(i));
                }
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Siphoner] fx tick failed: {ex.Message}");
            }
        }

        private static void Clear() {
            if (ringGo != null) { UnityEngine.Object.Destroy(ringGo); ringGo = null; ringParts = null; }
        }

        private static float Flicker(int i) => Mathf.Abs(Mathf.Sin(Time.time * 5f + i * 2.1f));
    }
}
