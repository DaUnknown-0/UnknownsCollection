// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * IdeasPackDiag - autotest for the ideas pack (Giant, Surveyor). Freeplay has no intro and therefore
 * no role assignment, so this makes the local player the Giant and the Surveyor 10 s after the ship
 * is up, marks the current room and saves a screenshot to BepInEx/UC_ideas_diag.png. Off by default;
 * config [Diagnostics] Ideas Pack Test.
 */

using System;
using HarmonyLib;
using UnityEngine;

namespace UnknownsCollection {
    public static class IdeasPackDiag {
        internal static BepInEx.Configuration.ConfigEntry<bool> Enabled;
        private static float at = -1f;
        private static int stage;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                try {
                    if (Enabled == null || !Enabled.Value) return;
                    var lp = PlayerControl.LocalPlayer;
                    if (ShipStatus.Instance == null || lp == null) { at = -1f; stage = 0; return; }
                    if (at < 0f) { at = Time.realtimeSinceStartup + 10f; return; }
                    if (Time.realtimeSinceStartup < at) return;
                    if (stage == 0) {
                        stage = 1;
                        Giant.SendSet(lp.PlayerId);
                        Surveyor.SendSet(lp.PlayerId);
                        string tracked = "?";
                        try { tracked = HudManager.Instance?.roomTracker?.LastRoom?.RoomId.ToString() ?? "none"; } catch { }
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[IdeasPackDiag] before mark: room tracker {tracked}, position {lp.GetTruePosition()}.");
                        Surveyor.DiagMarkHere();
                        at = Time.realtimeSinceStartup + 2f;
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[IdeasPackDiag] Giant + Surveyor set on {lp.Data?.PlayerName}, scale {lp.transform.localScale.x:F2}.");
                    } else if (stage == 1) {
                        stage = 2;
                        string shot = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UC_ideas_diag.png");
                        ScreenCapture.CaptureScreenshot(shot);
                        UnknownsCollectionPlugin.Logger?.LogInfo(
                            $"[IdeasPackDiag] diag: done, scale {lp.transform.localScale.x:F2}, screenshot -> {shot}");
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[IdeasPackDiag] failed: {e.Message}");
                    stage = 2;
                }
            }
        }
    }
}
