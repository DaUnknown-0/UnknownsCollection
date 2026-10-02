// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * MixerDiag - freeplay autotest for the Mixer (env UC_DIAG_MIXER=1, off otherwise).
 *
 * 10 s after the ship is up: a few roles are switched on in the local freeplay options (Medic, Seer,
 * Paramedic, Camouflager, Vampire), the local player becomes the Mixer, the first dummy is mixed
 * (crew -> crew), the second dummy is made a Morphling through Role Control and mixed (killer ->
 * another impostor role, the Mixer learns "Morphling" and is used up). Every step is logged.
 */

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UnknownsCollection {
    public static class MixerDiag {
        private static readonly bool On = Environment.GetEnvironmentVariable("UC_DIAG_MIXER") == "1";
        private static float at = -1f;
        private static int stage;

        private static void Log(string s) => UnknownsCollectionPlugin.Logger?.LogInfo("[MixerDiag] " + s);

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                if (!On) return;
                try {
                    var lp = PlayerControl.LocalPlayer;
                    if (ShipStatus.Instance == null || lp == null) { at = -1f; stage = 0; return; }
                    if (at < 0f) { at = Time.realtimeSinceStartup + 10f; return; }
                    if (Time.realtimeSinceStartup < at || stage >= 4) return;
                    var dummies = PlayerControl.AllPlayerControls.ToArray().Where(p => p != null && p.PlayerId != lp.PlayerId && p.Data != null && !p.Data.IsDead).ToList();
                    if (stage == 0) {
                        stage = 1;
                        foreach (var o in new[] { CustomOptionHolder.medicSpawnRate, CustomOptionHolder.seerSpawnRate, CustomOptionHolder.camouflagerSpawnRate,
                                                  CustomOptionHolder.vampireSpawnRate, Paramedic.SpawnRate })
                            o?.updateSelection(10);
                        Mixer.SendSet(lp.PlayerId);
                        Log($"options on, Mixer = {lp.Data.PlayerName}, {dummies.Count} dummies.");
                        at = Time.realtimeSinceStartup + 2f;
                    } else if (stage == 1) {
                        stage = 2;
                        if (dummies.Count > 0) Log("crew mix: " + Mixer.DiagMixNow(dummies[0].PlayerId));
                        if (dummies.Count > 0) Log("crew mix again: " + Mixer.DiagMixNow(dummies[0].PlayerId));
                        // the second dummy as a Morphling (an impostor killer), Role Control's own path
                        if (dummies.Count > 1) {
                            Type rc = null;
                            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { rc ??= asm.GetType("ForceImpostorMod.RoleControl"); }
                            var set = rc?.GetMethod("SetTorRole", BindingFlags.Public | BindingFlags.Static);
                            var apply = rc?.GetMethod("ApplyNow", BindingFlags.Public | BindingFlags.Static);
                            Log($"Role Control: {set?.Invoke(null, new object[] { dummies[1].PlayerId, RoleId.Morphling })} / {apply?.Invoke(null, new object[] { dummies[1].PlayerId })}");
                        }
                        at = Time.realtimeSinceStartup + 3f;
                    } else if (stage == 2) {
                        stage = 3;
                        if (dummies.Count > 1) {
                            Log($"before killer mix: {dummies[1].Data.PlayerName} impostor={dummies[1].Data.Role?.IsImpostor}, roles: " +
                                string.Join(", ", RoleInfo.getRoleInfoForPlayer(dummies[1], false).Select(r => r.name)));
                            Log("killer mix: " + Mixer.DiagMixNow(dummies[1].PlayerId));
                            Log("roles now: " + string.Join(", ", RoleInfo.getRoleInfoForPlayer(dummies[1], false).Select(r => r.name)) +
                                $", impostor={dummies[1].Data.Role?.IsImpostor}");
                        }
                        at = Time.realtimeSinceStartup + 3f;
                    } else if (stage == 3) {
                        stage = 4;
                        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UC_mixer_diag.png"));
                        Log("diag: done");
                    }
                } catch (Exception e) { Log($"failed: {e}"); stage = 4; }
            }
        }
    }
}
