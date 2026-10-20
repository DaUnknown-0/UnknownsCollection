// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UCAssetStoreDiag - autotest for the outsourced assets (UCAssetStore). Only active with the environment variable
 * UC_DIAG_ASSETS=1 (never in normal play). Eight seconds into a round (freeplay) it puts the "Virus" hat on the local
 * player, plays the Tesla warning, loads one button animation and a werewolf flipbook, logs what came from the store
 * and takes a screen shot to <game>/UCShots/assets. Ends with "[UCAssetsDiag] done".
 */

using HarmonyLib;
using System;
using System.IO;
using UnityEngine;

namespace UnknownsCollection {
    internal static class UCAssetStoreDiag {
        private static readonly bool On = Environment.GetEnvironmentVariable("UC_DIAG_ASSETS") == "1";
        private static float startAt = -1f, shotAt = -1f;
        private static bool done;

        private static void Log(string s) => UnknownsCollectionPlugin.Logger?.LogInfo("[UCAssetsDiag] " + s);

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                if (!On || done) return;
                try { Tick(); } catch (Exception e) { Log($"failed: {e.Message}"); done = true; }
            }
        }

        private static void Tick() {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null || ShipStatus.Instance == null) return;
            if (startAt < 0f) { startAt = Time.time + 8f; return; }
            if (Time.time < startAt) return;
            if (shotAt < 0f) {
                Log($"store: {UCAssetStore.ReadyCount}/{UCAssetStore.Total} files ready, folder {UCAssetStore.Folder}");
                lp.RpcSetHat("hat_Virus");
                UCAssets.PlayTeslaWarning();
                var frames = UCAssets.GetFrames("siphoner_drain");
                bool wolf = UCAssets.GetSprite("UnknownsCollection.Resources.anim.werewolf_death_f00.png", 200f) != null;
                bool hatFrame = UCAssets.HunterHatSprite != null;
                Log($"hat set: {lp.Data?.DefaultOutfit?.HatId}; tesla_warning clip: {(UCAssets.GetClipByName("tesla_warning") != null ? "loaded" : "missing")}; " +
                    $"siphoner frames: {(frames != null ? frames.Length.ToString() : "none")}; werewolf_death_f00: {wolf}; hunter hat sprite: {hatFrame}; " +
                    $"waiting(tesla_warning): {UCAssetStore.Waiting("UnknownsCollection.Resources.tesla_warning.ogg")}");
                shotAt = Time.time + 3f;
                return;
            }
            if (Time.time < shotAt) return;
            string dir = Path.Combine(BepInEx.Paths.GameRootPath, "UCShots", "assets");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"uc_hat_{DateTime.Now:HHmmss}.png");
            ScreenCapture.CaptureScreenshot(file);
            Log($"shot -> {file}");
            Log("done");
            done = true;
        }
    }
}
