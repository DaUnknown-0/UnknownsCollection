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
using UnityEngine.UI;
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
        public static byte bugPlayerId = byte.MaxValue;

        private const byte RpcId = 198;
        private const byte SubSetBug = 0;

        private static AudioClip glitchClip;

        private static RoleInfo bugInfo;
        public static RoleInfo BugInfo() => bugInfo ??= new RoleInfo(
            "Bug", Color, "Survive until the end to win alone",
            "Survive until the end to win alone", RoleId.Crewmate)
        { isNeutral = true };

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1480, Types.Neutral, "Bug",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1481, Types.Neutral, "Bug Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Bug] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;
                var checkType = torAsm.GetType("TheOtherRoles.Patches.CheckEndCriteriaPatch");
                if (checkType == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning("[Bug] CheckEndCriteriaPatch not found — own win disabled.");
                    return;
                }

                var methodNames = new[] {
                    "CheckAndEndGameForCrewmateWin",
                    "CheckAndEndGameForImpostorWin",
                    "CheckAndEndGameForJackalWin",
                    "CheckAndEndGameForTaskWin",
                    "CheckAndEndGameForSabotageWin"
                };
                var prefix = new HarmonyMethod(typeof(Bug), nameof(InterceptorPrefix));

                foreach (var name in methodNames) {
                    var m = checkType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
                    if (m != null) {
                        harmony.Patch(m, prefix: prefix);
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[Bug] Patched {name} for Bug win intercept.");
                    } else {
                        UnknownsCollectionPlugin.Logger?.LogWarning($"[Bug] {name} not found — skipped.");
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Bug] TryPatch failed: {e}");
            }
        }

        public static bool triggerBugWin;

        private const int BugWinReason = 18;

        public static bool InterceptorPrefix(ref bool __result) {
            try {
                if (BugIsAliveAndActive()) {
                    triggerBugWin = true;
                    GameManager.Instance.RpcEndGame((GameOverReason)BugWinReason, false);
                    __result = true;
                    return false;
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Bug] InterceptorPrefix failed: {e}");
            }
            return true;
        }

        private static bool BugIsAliveAndActive() =>
            active && bug != null && bug.Data != null && !bug.Data.IsDead && !bug.Data.Disconnected;

        private static FieldInfo winConditionField;
        private static TMPro.TMP_Text bonusText;
        private static Vector3 baseBonusPos;
        private static void SetWinCondition(int value) {
            try {
                if (winConditionField == null) {
                    var atdType = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.AdditionalTempData");
                    if (atdType != null)
                        winConditionField = atdType.GetField("winCondition", BindingFlags.Public | BindingFlags.Static);
                }
                if (winConditionField != null) {
                    var wcEnum = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.WinCondition");
                    if (wcEnum != null)
                        winConditionField.SetValue(null, Enum.ToObject(wcEnum, value));
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Bug] SetWinCondition failed: {e}");
            }
        }

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
                    int channels = wavBytes[22];
                    int sampleRate = BitConverter.ToInt32(wavBytes, 24);
                    int bitsPerSample = BitConverter.ToInt16(wavBytes, 34);
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
            bugPlayerId = active ? id : byte.MaxValue;
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
                triggerBugWin = false;
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
        [HarmonyPriority(Priority.Last)]
        static class OnGameEndPatch {
            public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ref EndGameResult endGameResult) {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != BugWinReason) return;
                    if (bugPlayerId == byte.MaxValue) return;

                    PlayerControl bugPlayer = Helpers.playerById(bugPlayerId);
                    if (bugPlayer == null || bugPlayer.Data == null) return;

                    EndGameResult.CachedWinners.Clear();
                    EndGameResult.CachedWinners.Add(new CachedPlayerData(bugPlayer.Data));
                    SetWinCondition(12);
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Bug wins! (own win condition)");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] OnGameEnd failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.SetEverythingUp))]
        [HarmonyPriority(Priority.Last)]
        static class EndGameFxPatch {
            public static void Postfix(EndGameManager __instance) {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != BugWinReason) return;

                    if (__instance.WinText != null) {
                        GameObject bonus = UnityEngine.Object.Instantiate(__instance.WinText.gameObject);
                        bonus.transform.position = new Vector3(__instance.WinText.transform.position.x,
                            __instance.WinText.transform.position.y - 0.5f,
                            __instance.WinText.transform.position.z);
                        bonus.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
                        bonusText = bonus.GetComponent<TMP_Text>();
                        bonusText.text = "Bug Wins";
                        bonusText.color = Color;
                        baseBonusPos = bonus.transform.localPosition;
                    }

                    if (__instance.BackgroundBar != null)
                        __instance.BackgroundBar.material.SetColor("_Color", Color);

                    var fx = __instance.gameObject.AddComponent<BugGlitchEffect>();
                    fx.mgr = __instance;
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Glitch effect attached to end screen.");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] EndGameFx failed: {e}");
                }
            }
        }

        private class BugGlitchEffect : MonoBehaviour {
            static BugGlitchEffect() => ClassInjector.RegisterTypeInIl2Cpp<BugGlitchEffect>();
            public EndGameManager mgr;
            private float nextPulse;
            private string baseWinStr;
            private Vector3 baseWinPos;

            private RawImage staticImage;
            private Texture2D noiseTex;
            private float nextNoiseFrame;

            private void Start() {
                nextPulse = Time.time + UnityEngine.Random.Range(0.1f, 0.5f);
                if (mgr != null && mgr.WinText != null) {
                    baseWinStr = mgr.WinText.text;
                    baseWinPos = mgr.WinText.transform.localPosition;
                }
                CreateStaticOverlay();
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] BugGlitchEffect started!");
            }

            private void CreateStaticOverlay() {
                try {
                    if (mgr == null) return;
                    var go = new GameObject("BugStaticCanvas");
                    go.transform.SetParent(mgr.transform, false);
                    var canvas = go.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 999;

                    var imgGo = new GameObject("BugStaticImg");
                    imgGo.transform.SetParent(go.transform, false);
                    var rt = imgGo.AddComponent<RectTransform>();
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;

                    staticImage = imgGo.AddComponent<RawImage>();
                    staticImage.color = new Color(1f, 1f, 1f, 0.06f);

                    noiseTex = new Texture2D(64, 36, TextureFormat.RGBA32, false);
                    noiseTex.filterMode = FilterMode.Point;
                    noiseTex.wrapMode = TextureWrapMode.Clamp;
                    staticImage.texture = noiseTex;
                    nextNoiseFrame = Time.time;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] CreateStaticOverlay failed: {e}");
                }
            }

            private void UpdateStatic() {
                if (noiseTex == null || staticImage == null) return;
                if (Time.time < nextNoiseFrame) return;
                nextNoiseFrame = Time.time + UnityEngine.Random.Range(0.08f, 0.15f);
                int w = noiseTex.width, h = noiseTex.height;
                var pixels = new Color32[w * h];
                for (int i = 0; i < pixels.Length; i++) {
                    byte v = UnityEngine.Random.value < 0.5f ? byte.MaxValue : byte.MinValue;
                    pixels[i] = new Color32(v, v, v, byte.MaxValue);
                }
                noiseTex.SetPixels32(pixels);
                noiseTex.Apply();
            }

            private void Update() {
                try {
                    if (mgr == null) return;
                    float t = Time.time;

                    UpdateStatic();

                    if (mgr.BackgroundBar != null) {
                        float flash = Mathf.PingPong(t * 0.6f, 1f);
                        Color barColor = Color.HSVToRGB(flash, 0.8f, 1f);
                        if (UnityEngine.Random.value < 0.02f) barColor = Color.white;
                        mgr.BackgroundBar.material.SetColor("_Color", barColor);
                    }

                    if (mgr.WinText != null) {
                        if (t > nextPulse) {
                            nextPulse = t + UnityEngine.Random.Range(0.2f, 0.6f);
                            int r = UnityEngine.Random.Range(0, 6);
                            if (r == 0) {
                                string glitched = "";
                                int len = baseWinStr?.Length ?? 10;
                                for (int i = 0; i < len; i++)
                                    glitched += (char)UnityEngine.Random.Range(33, 127);
                                mgr.WinText.text = glitched;
                            } else {
                                mgr.WinText.text = baseWinStr;
                            }
                            if (r < 2) {
                                mgr.WinText.transform.localPosition = baseWinPos + new Vector3(
                                    UnityEngine.Random.Range(-6f, 6f),
                                    UnityEngine.Random.Range(-1.5f, 1.5f), 0);
                            } else {
                                mgr.WinText.transform.localPosition = baseWinPos;
                            }
                        }
                        float sx = Mathf.Sin(t * 25f) * 0.3f;
                        float sy = Mathf.Cos(t * 20f) * 0.2f;
                        mgr.WinText.transform.localPosition += new Vector3(sx, sy, 0);

                        if (UnityEngine.Random.value < 0.015f)
                            mgr.WinText.color = new Color(
                                UnityEngine.Random.value, UnityEngine.Random.value,
                                UnityEngine.Random.value, 1f);
                        else
                            mgr.WinText.color = Color.white;
                    }

                    if (bonusText != null) {
                        if (UnityEngine.Random.value < 0.02f) {
                            bonusText.transform.localPosition = baseBonusPos + new Vector3(
                                UnityEngine.Random.Range(-8f, 8f),
                                UnityEngine.Random.Range(-2f, 2f), 0);
                            bonusText.color = new Color(
                                Mathf.Clamp01(Color.r + UnityEngine.Random.Range(-0.3f, 0.3f)),
                                Mathf.Clamp01(Color.g + UnityEngine.Random.Range(-0.3f, 0.3f)),
                                Mathf.Clamp01(Color.b + UnityEngine.Random.Range(-0.3f, 0.3f)));
                        } else {
                            bonusText.transform.localPosition = baseBonusPos;
                            bonusText.color = Color;
                        }
                    }
                } catch { }
            }
        }

        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class TaskCountPatch {
            public static void Postfix(GameData __instance) {
                try {
                    if (bug == null || bug.Data == null) return;
                    var (completed, total) = TasksHandler.taskInfo(bug.Data);
                    __instance.TotalTasks -= total;
                    __instance.CompletedTasks -= completed;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] TaskCountPatch failed: {e}");
                }
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
