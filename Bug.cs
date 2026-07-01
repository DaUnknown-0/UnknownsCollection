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

        // The Bug win is handled entirely by attribute-based patches (RpcEndGameHijackPatch +
        // OnGameEndPatch), picked up by PatchAll — no reflection needed here.
        public static void TryPatch(Harmony harmony) { }

        private const int BugWinReason = 18;

        // The Bug's PlayerId, snapshotted at game-end BEFORE TOR's resetVariables wipes bugPlayerId.
        // Deliberately NOT part of resetVariables: TOR's own end-of-game reset would clear it before our
        // Priority.Last postfix could read it. Re-snapshotted every game-end, so a stale value is
        // harmless (the postfix also gates on gameOverReason == BugWinReason).
        private static byte winnerBugId = byte.MaxValue;

        // TeamJackalWin from TOR's CustomGameOverReason enum (EndGamePatch.cs). The Jackal is a "team"
        // win the Bug should also hijack; the other custom reasons (Lovers 10, Mini 12, Jester 13,
        // Arsonist 14, Vulture 15, Prosecutor 16) are neutral solo wins the Bug must NOT steal.
        private const int TeamJackalWinReason = 11;

        // Host-authoritative Bug win ("survive to the end -> win alone"): when a TEAM win is about to be
        // broadcast and the Bug is still alive, rewrite the reason to BugWinReason in-place. This reuses
        // the single RpcEndGame the original caller already makes — no second broadcast, no per-frame
        // instant win. Only the three team wins qualify: vanilla Crew/Impostor (reason < 10) and the
        // Jackal team (11). Neutral solo wins (Jester, Arsonist, Vulture, Lovers, Prosecutor, Mini) are
        // left untouched, so the Bug never steals those.
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.RpcEndGame))]
        static class RpcEndGameHijackPatch {
            public static void Prefix(ref GameOverReason endReason) {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!BugIsAliveAndActive()) return;
                    int r = (int)endReason;
                    if (r >= 10 && r != TeamJackalWinReason) return; // only Crew/Impostor (<10) or Jackal (11)
                    endReason = (GameOverReason)BugWinReason;
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Bug survived to the end — hijacking win.");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] RpcEndGame hijack failed: {e}");
                }
            }
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

        // ---- Glitch sound (embedded raw PCM, like TeslaSound) ----
        private static AudioClip GetGlitchClip() {
            if (glitchClip != null) return glitchClip;
            glitchClip = BugSound.LoadClip();
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
                bugPlayerId = byte.MaxValue;
                // NOTE: winnerBugId is intentionally NOT reset here (see its declaration).
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
            // Runs before TOR's OnGameEnd postfix calls resetVariables(): snapshot the Bug's id so the
            // postfix below can still award the win after bugPlayerId has been reset. Fires on every
            // client (OnGameEnd runs everywhere), so all clients agree on the winner.
            public static void Prefix() {
                if (active && bugPlayerId != byte.MaxValue) winnerBugId = bugPlayerId;
            }

            // Runs AFTER TOR's postfix (Priority.Last), so our winner list has the final say. Keys on
            // the host-broadcast BugWinReason, which every client sees via TOR's OnGameEndPatch.Prefix.
            public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ref EndGameResult endGameResult) {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != BugWinReason) return;
                    if (winnerBugId == byte.MaxValue) return;

                    PlayerControl bugPlayer = Helpers.playerById(winnerBugId);
                    if (bugPlayer == null || bugPlayer.Data == null) return;

                    EndGameResult.CachedWinners.Clear();
                    EndGameResult.CachedWinners.Add(new CachedPlayerData(bugPlayer.Data));
                    // 12 is intentionally outside TOR's WinCondition enum (0-10): no vanilla end-screen
                    // branch matches it, and the Bug draws its own green "Bug Wins" banner in EndGameFxPatch.
                    SetWinCondition(12);
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Bug wins alone! (survived to the end)");
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

                    if (__instance.BackgroundBar != null && UnknownsCollectionPlugin.BugGlitchEnabled.Value)
                        __instance.BackgroundBar.material.SetColor("_Color", Color);

                    if (UnknownsCollectionPlugin.BugGlitchEnabled.Value) {
                        var fx = __instance.gameObject.AddComponent<BugGlitchEffect>();
                        fx.mgr = __instance;
                        UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Glitch effect attached to end screen.");
                    }
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

            private RawImage glitchOverlay;
            private Texture2D glitchTex;
            private float glitchEndTime;

            private void Start() {
                nextPulse = Time.time + UnityEngine.Random.Range(0.1f, 0.5f);
                if (mgr != null && mgr.WinText != null) {
                    baseWinStr = mgr.WinText.text;
                    baseWinPos = mgr.WinText.transform.localPosition;
                }
                CreateGlitchOverlay();
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] BugGlitchEffect started!");
            }

            private void CreateGlitchOverlay() {
                try {
                    if (mgr == null) return;
                    var go = new GameObject("BugGlitchCanvas");
                    go.transform.SetParent(mgr.transform, false);
                    var canvas = go.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 999;

                    var imgGo = new GameObject("BugGlitchImg");
                    imgGo.transform.SetParent(go.transform, false);
                    var rt = imgGo.AddComponent<RectTransform>();
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;

                    glitchOverlay = imgGo.AddComponent<RawImage>();

                    int cols = 128, rows = 72;
                    glitchTex = new Texture2D(cols, rows, TextureFormat.RGBA32, false);
                    glitchTex.filterMode = FilterMode.Point;
                    glitchTex.wrapMode = TextureWrapMode.Clamp;
                    ClearGlitchTex();
                    glitchOverlay.texture = glitchTex;
                    glitchOverlay.color = new Color(1f, 1f, 1f, 1f);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] CreateGlitchOverlay failed: {e}");
                }
            }

            private void ClearGlitchTex() {
                if (glitchTex == null) return;
                var cols = glitchTex.width;
                var rows = glitchTex.height;
                var pixels = new Color32[cols * rows];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(0, 0, 0, 0);
                glitchTex.SetPixels32(pixels);
                glitchTex.Apply();
            }

            private void TriggerBlockGlitch() {
                if (glitchTex == null) return;
                int cols = glitchTex.width;
                int rows = glitchTex.height;

                var pixels = new Color32[cols * rows];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(0, 0, 0, 0);

                int blockY = UnityEngine.Random.Range(0, rows - 8);
                int blockH = UnityEngine.Random.Range(4, 12);
                int offset = UnityEngine.Random.Range(-20, 21);
                glitchEndTime = Time.time + UnityEngine.Random.Range(0.08f, 0.2f);

                byte gray = (byte)UnityEngine.Random.Range(20, 60);
                for (int r = blockY; r < blockY + blockH && r < rows; r++) {
                    for (int c = 0; c < cols; c++) {
                        int src = r * cols + c;
                        int dst = r * cols + ((c + offset + cols) % cols);
                        pixels[dst] = new Color32(gray, gray, gray, 180);
                    }
                }

                glitchTex.SetPixels32(pixels);
                glitchTex.Apply();
            }

            private static string ColorToHex(Color c) =>
                $"{(byte)(c.r * 255):X2}{(byte)(c.g * 255):X2}{(byte)(c.b * 255):X2}";

            private void Update() {
                try {
                    if (mgr == null) return;
                    float t = Time.time;

                    if (mgr.BackgroundBar != null) {
                        float hue = Mathf.PingPong(t * 0.4f, 1f);
                        mgr.BackgroundBar.material.SetColor("_Color",
                            Color.HSVToRGB(hue, 0.7f, 1f));
                    }

                    if (t > nextPulse) {
                        nextPulse = t + UnityEngine.Random.Range(0.2f, 0.5f);
                        int r = UnityEngine.Random.Range(0, 6);
                        if (r < 2) TriggerBlockGlitch();

                        if (mgr.WinText != null) {
                            mgr.WinText.richText = true;
                            int len = baseWinStr?.Length ?? 10;
                            if (r < 3) {
                                string glitched = "";
                                for (int i = 0; i < len; i++) {
                                    char c = (char)UnityEngine.Random.Range(33, 127);
                                    Color rc = new Color(
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value);
                                    glitched += $"<color=#{ColorToHex(rc)}>{c}</color>";
                                }
                                mgr.WinText.text = glitched;
                            } else if (r < 4) {
                                char[] chars = baseWinStr?.ToCharArray() ?? new char[0];
                                string mixed = "";
                                for (int i = 0; i < chars.Length; i++) {
                                    char c = UnityEngine.Random.value < 0.5f
                                        ? (char)UnityEngine.Random.Range(33, 127) : chars[i];
                                    Color rc = new Color(
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value);
                                    mixed += $"<color=#{ColorToHex(rc)}>{c}</color>";
                                }
                                mgr.WinText.text = mixed;
                            } else {
                                string colored = "";
                                foreach (char c in baseWinStr) {
                                    Color rc = new Color(
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value);
                                    colored += $"<color=#{ColorToHex(rc)}>{c}</color>";
                                }
                                mgr.WinText.text = colored;
                            }
                            mgr.WinText.transform.localPosition = baseWinPos;
                        }
                    }

                    if (glitchEndTime > 0 && t > glitchEndTime) {
                        glitchEndTime = 0;
                        ClearGlitchTex();
                    }

                    if (mgr.WinText != null) {
                        if (UnityEngine.Random.value < 0.015f)
                            mgr.WinText.color = new Color(
                                UnityEngine.Random.value, UnityEngine.Random.value,
                                UnityEngine.Random.value, 1f);
                        else
                            mgr.WinText.color = Color.white;
                    }

                    if (bonusText != null) {
                        if (UnityEngine.Random.value < 0.02f) {
                            bonusText.color = new Color(
                                Mathf.Clamp01(Color.r + UnityEngine.Random.Range(-0.3f, 0.3f)),
                                Mathf.Clamp01(Color.g + UnityEngine.Random.Range(-0.3f, 0.3f)),
                                Mathf.Clamp01(Color.b + UnityEngine.Random.Range(-0.3f, 0.3f)));
                        } else {
                            bonusText.color = Color;
                        }
                        bonusText.transform.localPosition = baseBonusPos;
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
