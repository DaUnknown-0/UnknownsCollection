// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * IdeasPackDiag - autotest for the ideas pack. Freeplay has no intro and therefore no role
 * assignment, so 10 s after the ship is up this makes the local player the Giant, the Surveyor and
 * the Hypnotist and marks the current room.
 *
 * Freeplay never runs TOR's CustomButton.HudUpdate (TOR's HudManager.Update postfix wants
 * GameState Started); TOR Role Control 1.6.10+ ticks the buttons there itself, which this test
 * relies on. 3 s later it switches the faction to Impostor exactly like Role Control does (PlayerTuning.ApplySetFaction -> RoleManager.SetRole),
 * the step after which every ability button froze in a solo round on 2026-10-02. Button states
 * (timer, label) are logged before and 4 s after, then a screenshot goes to
 * BepInEx/UC_ideas_diag.png. Off by default; config [Diagnostics] Ideas Pack Test.
 */

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UnknownsCollection {
    public static class IdeasPackDiag {
        internal static BepInEx.Configuration.ConfigEntry<bool> Enabled;
        private static float at = -1f;
        private static int stage;

        private static void LogButtons(string when) {
            int dead = 0, total = 0;
            foreach (var b in TheOtherRoles.Objects.CustomButton.buttons) {
                total++;
                if (b == null || b.actionButtonGameObject == null) { dead++; continue; }
                if (!b.actionButtonGameObject.activeSelf) continue;
                string lbl = "?";
                try { lbl = b.actionButtonLabelText != null ? b.actionButtonLabelText.text + (b.actionButtonLabelText.enabled ? "" : " (hidden)") : "null"; } catch { }
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[IdeasPackDiag] {when}: button '{b.Sprite?.name}': buttonText='{b.buttonText}', label='{lbl}', timer {b.Timer:F2}/{b.MaxTimer:F0}");
            }
            UnknownsCollectionPlugin.Logger?.LogInfo($"[IdeasPackDiag] {when}: {total} button(s) in TOR's list, {dead} dead.");
        }

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
                        Surveyor.DiagMarkHere();
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[IdeasPackDiag] Giant + Surveyor set on {lp.Data?.PlayerName}; label guard {(Environment.GetEnvironmentVariable("UC_LABELGUARD_OFF") == "1" ? "OFF" : "on")}.");
                        at = Time.realtimeSinceStartup + 3f;
                    } else if (stage == 1) {
                        stage = 2;
                        LogButtons("before faction");
                        // Role Control's own path (what its F7 overlay does), else the bare faction switch
                        Type rc = null, ucRole = null;
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                            rc ??= asm.GetType("ForceImpostorMod.RoleControl");
                            ucRole ??= asm.GetType("ForceImpostorMod.UcRole");
                        }
                        if (rc != null && ucRole != null) {
                            var set = rc.GetMethod("SetUcRole", BindingFlags.Public | BindingFlags.Static);
                            var apply = rc.GetMethod("ApplyNow", BindingFlags.Public | BindingFlags.Static);
                            var r1 = set?.Invoke(null, new object[] { lp.PlayerId, Enum.Parse(ucRole, "Hypnotist") });
                            var r2 = apply?.Invoke(null, new object[] { lp.PlayerId });
                            UnknownsCollectionPlugin.Logger?.LogInfo($"[IdeasPackDiag] Role Control: {r1} / {r2}");
                        } else {
                            var m = typeof(PlayerTuning).GetMethod("ApplySetFaction", BindingFlags.NonPublic | BindingFlags.Static);
                            m?.Invoke(null, new object[] { lp.PlayerId, true });
                            UnknownsCollectionPlugin.Logger?.LogInfo($"[IdeasPackDiag] faction -> Impostor ({(m != null ? "done" : "method missing")}).");
                        }
                        // the victim view, with its darkness mask: the first dummy is hypnotised
                        foreach (var p in PlayerControl.AllPlayerControls.ToArray())
                            if (p != null && p.PlayerId != lp.PlayerId && p.Data != null && !p.Data.IsDead) { Hypnotist.DiagHypnotize(p.PlayerId); break; }
                        HypnotistView.DiagForce = true;
                        at = Time.realtimeSinceStartup + 4f;
                    } else if (stage == 2) {
                        stage = 3;
                        LogButtons("4 s after faction");
                        string shot = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UC_ideas_diag.png");
                        ScreenCapture.CaptureScreenshot(shot);
                        // then the lights go out: the victim's view has to shrink like a crewmate's
                        try { ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Sabotage, (byte)SystemTypes.Electrical); } catch { }
                        at = Time.realtimeSinceStartup + 6f;
                    } else if (stage == 3) {
                        stage = 5;
                        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UC_ideas_diag_dark.png"));
                        at = Time.realtimeSinceStartup + 1f;
                    } else if (stage == 5) {
                        stage = 4;
                        HypnotistView.DiagForce = false;
                        UnknownsCollectionPlugin.Logger?.LogInfo(
                            $"[IdeasPackDiag] diag: done, scale {lp.transform.localScale.x:F2}, screenshot -> {System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UC_ideas_diag.png")}");
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[IdeasPackDiag] failed: {e.Message}");
                    stage = 4;
                }
            }
        }
    }
}
