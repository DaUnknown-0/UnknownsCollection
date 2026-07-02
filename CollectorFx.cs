// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Collector visual effects that don't belong to the relics themselves (CollectorRelics.cs already
 * owns the relic idle animation and the pickup/arrival bursts). This file is the self-only payoff for
 * having already WON in "Survive To The End" mode: once the Collector holds every relic, the game
 * doesn't end immediately - Collector.RpcEndGameHijackPatch only hijacks the NEXT team win, which can
 * be minutes away. Until then the only feedback was a static "RELICS n/n" button label.
 *
 * TickAura mirrors PoltergeistFx.TickAura EXACTLY: a faint ring of orbiting gold sparks around the
 * local player, gated on the local Collector's own identity, re-checked every single Tick frame and
 * never cached. This is a severe info-leak risk if it were ever visible to anyone else - it marks the
 * single most important kill/protect target left in the round - so the gate is deliberately as strict
 * as Poltergeist's.
 */

using System;
using UnityEngine;

namespace UnknownsCollection {
    public static class CollectorFx {
        private static readonly Color Gold = new Color(1f, 0.86f, 0.4f);

        static CollectorFx() {
            UCFx.RegisterTick(TickAura);
            UCFx.RegisterReset(Clear);
        }

        // Touched once from Collector.CreateOptions() (plugin load) purely to force this type's
        // static constructor - and therefore the RegisterTick/RegisterReset calls above - to run
        // early, before the first round could ever need the aura. See ManipulatorFx.Init() for the
        // same reasoning.
        public static void Init() { }

        private static GameObject auraGo;
        private static SpriteRenderer[] auraParts;

        private static void TickAura() {
            try {
                bool show = Collector.IsLocalCollector()
                            && Collector.HasAllRelics()
                            && (Collector.WinMode?.getSelection() ?? 0) == 1 // Survive To The End only
                            && PlayerControl.LocalPlayer != null
                            && PlayerControl.LocalPlayer.Data != null
                            && !PlayerControl.LocalPlayer.Data.IsDead
                            && MeetingHud.Instance == null;
                if (!show) {
                    if (auraGo != null) auraGo.SetActive(false);
                    return;
                }
                if (auraGo == null) {
                    auraGo = new GameObject("CollectorAura") { layer = 11 };
                    auraParts = new SpriteRenderer[6];
                    for (int i = 0; i < auraParts.Length; i++) {
                        var go = new GameObject($"a{i}") { layer = 11 };
                        go.transform.SetParent(auraGo.transform);
                        var sr = go.AddComponent<SpriteRenderer>();
                        sr.sprite = i % 2 == 0 ? UCFx.Dot : UCFx.Spark;
                        auraParts[i] = sr;
                    }
                }
                auraGo.SetActive(true);
                var p = PlayerControl.LocalPlayer.GetTruePosition();
                auraGo.transform.position = new Vector3(p.x, p.y, -1.2f);
                float now = Time.time;
                for (int i = 0; i < auraParts.Length; i++) {
                    float a = now * (0.7f + i * 0.11f) + i * 1.05f;
                    float r = 0.40f + 0.09f * Mathf.Sin(now * 1.5f + i * 1.7f);
                    auraParts[i].transform.localPosition =
                        new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r * 0.75f + 0.05f, 0f);
                    auraParts[i].transform.localScale = Vector3.one * (0.12f + 0.04f * Mathf.Sin(now * 2.1f + i));
                    auraParts[i].color = Tint(Gold, 0.24f + 0.12f * Flicker(i, 4.5f));
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Collector] aura tick failed: {e.Message}");
            }
        }

        public static void Clear() {
            if (auraGo != null) { UnityEngine.Object.Destroy(auraGo); auraGo = null; auraParts = null; }
        }

        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
        private static float Flicker(int i, float speed) => Mathf.Abs(Mathf.Sin(Time.time * speed + i * 2.3f));
    }
}
