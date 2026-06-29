// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using TMPro;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Bug {
        public static readonly Color Color = new Color(0.20f, 1f, 0.35f);

        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;

        public static PlayerControl bug;
        public static bool active;

        private const byte RpcId = 198;
        private const byte SubSetBug = 0;

        private static AudioClip glitchClip;

        private static RoleInfo bugInfo;
        public static RoleInfo BugInfo() => bugInfo ??= new RoleInfo(
            "Bug", Color, "Survive until the end and win with the winning team",
            "Survive until the end and win with the winning team", RoleId.Crewmate)
        { isNeutral = true };

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1480, Types.Crewmate, "Bug",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1481, Types.Crewmate, "Bug Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Bug] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { }

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalBug() =>
            bug != null && PlayerControl.LocalPlayer != null && bug.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        private static AudioClip GetGlitchClip() {
            if (glitchClip != null) return glitchClip;
            try {
                string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string wavPath = Path.Combine(dllDir, "sfx_similar.wav");
                if (!File.Exists(wavPath)) {
                    wavPath = Path.Combine(Directory.GetParent(dllDir)?.FullName ?? dllDir, "sfx_similar.wav");
                }
                if (File.Exists(wavPath)) {
                    byte[] wavBytes = File.ReadAllBytes(wavPath);
                    // Parse WAV header to create AudioClip
                    int channels = wavBytes[22];
                    int sampleRate = BitConverter.ToInt32(wavBytes, 24);
                    int bitsPerSample = BitConverter.ToInt16(wavBytes, 34);
                    // Find "data" chunk (skip any extra chunks between fmt and data)
                    int offset = 12;
                    int dataSize = 0, dataOffset = 0;
                    while (offset < wavBytes.Length - 8) {
                        string chunkId = System.Text.Encoding.ASCII.GetString(wavBytes, offset, 4);
                        int chunkSize = BitConverter.ToInt32(wavBytes, offset + 4);
                        if (chunkId == "data") { dataOffset = offset + 8; dataSize = chunkSize; break; }
                        offset += 8 + chunkSize;
                    }
                    if (dataSize > 0) {
                        int bytesPerSample = bitsPerSample / 8;
                        int sampleCount = dataSize / bytesPerSample;
                        float[] samples = new float[sampleCount];
                        for (int i = 0; i < sampleCount; i++) {
                            if (bytesPerSample == 2)
                                samples[i] = BitConverter.ToInt16(wavBytes, dataOffset + i * 2) / 32768f;
                            else
                                samples[i] = (wavBytes[dataOffset + i] - 128) / 128f;
                        }
                        glitchClip = AudioClip.Create("BugGlitch", sampleCount / channels, channels, sampleRate, false);
                        glitchClip.SetData(samples, 0);
                        UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Glitch sound loaded.");
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Bug] Could not load glitch sound: {e.Message}");
            }
            return glitchClip;
        }

        private static bool BugCanWinAlongside(GameOverReason reason) {
            int r = (int)reason;
            if (r <= 6) return true; // standard crew/impostor wins
            return r == 10 || r == 11; // LoversWin or TeamJackalWin
        }

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetBug(byte id) {
            try {
                var w = BeginRpc(SubSetBug);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetBug(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Bug] SendSetBug failed: {e}"); }
        }

        private static void ApplySetBug(byte id) {
            bug = Helpers.playerById(id);
            active = bug != null;
            if (active) UCPromotion.Claim(id);
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Bug] The Bug is {bug.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySetBug(playerId);

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    if (subtype == SubSetBug) ApplySetBug(reader.ReadByte());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                bug = null;
                active = false;
            }
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainCrewmate).ToList();
                    if (candidates.Count == 0) return;
                    SendSetBug(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] IntroEnd pick failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        static class MeetingClosePatch {
            public static void Postfix() {
                try {
                    if (!active || bug == null || !IsAlive(bug)) return;
                    var clip = GetGlitchClip();
                    if (clip != null) {
                        SoundManager.Instance.PlaySound(clip, false, 0.6f);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] meeting sound failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)] // after TOR's OnGameEnd removes/re-adds winners
        static class OnGameEndPatch {
            public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ref EndGameResult endGameResult) {
                try {
                    if (!active || bug == null || !IsAlive(bug)) return;
                    if (!BugCanWinAlongside(TheOtherRoles.Patches.OnGameEndPatch.gameOverReason)) return;

                    bool alreadyWinner = false;
                    foreach (var w in EndGameResult.CachedWinners.GetFastEnumerator()) {
                        if (w.PlayerName == bug.Data.PlayerName) { alreadyWinner = true; break; }
                    }
                    if (!alreadyWinner) {
                        EndGameResult.CachedWinners.Add(new CachedPlayerData(bug.Data));
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[Bug] Bug added to winners.");
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] OnGameEnd failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.SetEverythingUp))]
        static class EndGameFxPatch {
            public static void Postfix(EndGameManager __instance) {
                try {
                    if (!active || bug == null) return;
                    bool bugWon = false;
                    foreach (var w in EndGameResult.CachedWinners.GetFastEnumerator()) {
                        if (w.PlayerName == bug.Data.PlayerName) { bugWon = true; break; }
                    }
                    if (!bugWon) return;

                    if (__instance.WinText != null) {
                        GameObject bonus = UnityEngine.Object.Instantiate(__instance.WinText.gameObject);
                        bonus.transform.position = new Vector3(__instance.WinText.transform.position.x,
                            __instance.WinText.transform.position.y - 0.7f,
                            __instance.WinText.transform.position.z);
                        bonus.transform.localScale = new Vector3(0.6f, 0.6f, 1f);
                        var txt = bonus.GetComponent<TMP_Text>();
                        txt.text = "BUG SURVIVED";
                        txt.color = Color;
                    }

                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] EndGameFx failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.Update))]
        static class EndGameUpdatePatch {
            public static void Postfix(EndGameManager __instance) {
                try {
                    if (!active || bug == null) return;
                    bool bugWon = false;
                    foreach (var w in EndGameResult.CachedWinners.GetFastEnumerator()) {
                        if (w.PlayerName == bug.Data.PlayerName) { bugWon = true; break; }
                    }
                    if (!bugWon || __instance == null) return;

                    float t = Time.time;
                    if (__instance.BackgroundBar != null) {
                        float hue = Mathf.PingPong(t * 0.3f, 1f);
                        __instance.BackgroundBar.material.SetColor("_Color",
                            Color.HSVToRGB(hue, 0.8f, 0.9f));
                    }
                    // Re-fetch WinText each frame since it's always there during the end screen
                    if (__instance.WinText != null) {
                        float sx = Mathf.Sin(t * 15f) * 2f;
                        float sy = Mathf.Cos(t * 12f) * 2f;
                        __instance.WinText.transform.localPosition = new Vector3(sx, sy, __instance.WinText.transform.localPosition.z);
                    }
                } catch { }
            }
        }

        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || bug == null || p == null || p != bug || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = BugInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, BugInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}