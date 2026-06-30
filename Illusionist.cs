// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Illusionist (Impostor)
 *
 * A normal TOR Impostor is silently promoted to "The Illusionist" at game start (host-authoritative
 * pick, broadcast via RPC 195). The Illusionist RECORDS its own walking path (for a configurable
 * length), then at any time (with a cooldown) PLAYS IT BACK: a clone that looks exactly like the
 * Illusionist walks the recorded path. The clone wears a Medic-shield glow and is effectively a
 * protected player - any kill attempt on it is blocked with a shield flash (it never dies).
 *
 * The recorded path is broadcast on playback; every client builds + replays its own clone locally
 * (see IllusionistClone). The kill-block interaction lives in KillButtonDoClickPatch below.
 *
 * ARCHITECTURE mirrors Tesla/Saboteur: own RoleInfo tag over the real Impostor role, custom RPC (195),
 * client-side FX. Gated by the mod-wide No-Start handshake (everyone has the mod).
 *
 * Options live in the 1450-1455 block. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Illusionist {
        // ---- Theme ----
        public static readonly Color Color = Palette.ImpostorRed; // impostor role -> red role tag
        public const float SampleInterval = 0.1f;                 // path sampling resolution

        // ---- Options (IDs 1450-1455) ----
        public static CustomOption SpawnRate;        // 1450 (header) - impostor role chance
        public static CustomOption SpawnMinPlayers;  // 1451 - minimum LOBBY players to spawn
        public static CustomOption RecordLength;      // 1452 - max recording length (seconds)
        public static CustomOption PlaybackCooldown;  // 1453 - cooldown between playbacks
        public static CustomOption BlockPenalty;      // 1454 - a blocked kill costs the killer a full cooldown
        public static CustomOption ShieldVisibleAll;  // 1455 - the shield glow is visible to everyone

        // ---- Runtime state ----
        public static PlayerControl illusionist;
        public static bool active;

        private static readonly List<Vector2> recordBuffer = new();
        private static readonly List<bool> ventBuffer = new(); // per-sample "in a vent" flag, parallel to recordBuffer

        // Receiver-side reassembly buffer for the chunked clone-path RPC (see SendSpawnClone).
        private static readonly List<Vector2> rxPts = new();
        private static readonly List<bool> rxVnt = new();
        private static bool recording;
        private static float recordStart;
        private static float lastSample;

        // ---- Custom RPC (195) subtypes ----
        private const byte RpcId = 195; // == UnknownsCollectionPlugin.IllusionistRpcId
        private const byte SubSetIllusionist = 0; // illusionistId
        private const byte SubSpawnClone = 1;     // count, then count*(x,y) floats
        private const byte SubFlash = 2;          // (none) - shield flash on the clone everywhere
        private const byte SubDespawn = 3;        // (none)

        // ---- Role identity ----
        private static RoleInfo illusionistInfo;
        public static RoleInfo IllusionistInfo() => illusionistInfo ??= new RoleInfo(
            "Illusionist", Color, "Record a path and replay it as an unkillable clone",
            "Record a path and replay it as an unkillable clone", RoleId.Impostor);

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1450, Types.Impostor, "Illusionist",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1451, Types.Impostor, "Illusionist Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                RecordLength = CustomOption.Create(1452, Types.Impostor, "Illusionist Max Recording Length",
                    8f, 2f, 20f, 1f, SpawnRate);
                PlaybackCooldown = CustomOption.Create(1453, Types.Impostor, "Illusionist Playback Cooldown",
                    30f, 10f, 90f, 2.5f, SpawnRate);
                BlockPenalty = CustomOption.Create(1454, Types.Impostor, "Blocked Kill Costs The Killer A Cooldown",
                    true, SpawnRate);
                ShieldVisibleAll = CustomOption.Create(1455, Types.Impostor, "Clone Shield Visible To Everyone",
                    true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Illusionist] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { /* all patches are attribute-based */ }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalIllusionist() =>
            illusionist != null && PlayerControl.LocalPlayer != null && illusionist.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static float RecordLengthValue() => RecordLength != null ? RecordLength.getFloat() : 8f;

        private static float KillRange() =>
            AmongUs.GameOptions.GameOptionsData.KillDistances[
                Mathf.Clamp(GameOptionsManager.Instance.currentNormalGameOptions.KillDistance, 0, 2)];

        // ====================================================================
        // Custom RPC senders (each applies locally too)
        // ====================================================================
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetIllusionist(byte id) {
            try {
                var w = BeginRpc(SubSetIllusionist);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetIllusionist(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] SendSetIllusionist failed: {e}"); }
        }

        private static void SendSpawnClone(List<Vector2> pts, List<bool> ventFlags) {
            try {
                // A full recording can be up to RecordLength/SampleInterval samples (e.g. 20s/0.1s =
                // 200) at 9 bytes each (2 floats + 1 bool) ~= 1.8 KB - well over Among Us' per-RPC
                // packet limit, which would disconnect the sender. So the path is split into chunks
                // (TOR chunks its option sharing for the same reason). first/last flags let the
                // receiver reassemble the path and spawn the clone only once it is complete.
                const int ChunkPoints = 80; // 80 * 9 bytes = 720 B/RPC, safely under the limit
                int total = pts.Count;
                for (int start = 0; start < total; start += ChunkPoints) {
                    int count = Math.Min(ChunkPoints, total - start);
                    var w = BeginRpc(SubSpawnClone);
                    w.Write(start == 0);                // first chunk
                    w.Write(start + count >= total);    // last chunk
                    w.Write(count);
                    for (int i = start; i < start + count; i++) {
                        w.Write(pts[i].x); w.Write(pts[i].y);
                        w.Write(i < ventFlags.Count && ventFlags[i]);
                    }
                    AmongUsClient.Instance.FinishRpcImmediately(w);
                }
                IllusionistClone.Spawn(pts, ventFlags, SampleInterval); // sender applies locally
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] SendSpawnClone failed: {e}"); }
        }

        public static void SendCloneFlash() {
            try {
                var w = BeginRpc(SubFlash);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                IllusionistClone.Flash(0.4f);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] SendCloneFlash failed: {e}"); }
        }

        public static void SendDespawnClone() {
            try {
                var w = BeginRpc(SubDespawn);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                IllusionistClone.Despawn();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] SendDespawnClone failed: {e}"); }
        }

        // ---- Appliers (run on every client) ----
        private static void ApplySetIllusionist(byte id) {
            illusionist = Helpers.playerById(id);
            active = illusionist != null;
            if (active) UCPromotion.Claim(id);
            recordBuffer.Clear();
            ventBuffer.Clear();
            recording = false;
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Illusionist] The Illusionist is {illusionist.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySetIllusionist(playerId);

        // ====================================================================
        // RPC receiver
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubSetIllusionist: ApplySetIllusionist(reader.ReadByte()); break;
                        case SubSpawnClone: {
                            bool first = reader.ReadBoolean();
                            bool last = reader.ReadBoolean();
                            int count = reader.ReadInt32();
                            if (first) { rxPts.Clear(); rxVnt.Clear(); }
                            for (int i = 0; i < count; i++) {
                                rxPts.Add(new Vector2(reader.ReadSingle(), reader.ReadSingle()));
                                rxVnt.Add(reader.ReadBoolean());
                            }
                            if (last) {
                                IllusionistClone.Spawn(new List<Vector2>(rxPts), new List<bool>(rxVnt), SampleInterval);
                                rxPts.Clear(); rxVnt.Clear();
                            }
                            break;
                        }
                        case SubFlash: IllusionistClone.Flash(0.4f); break;
                        case SubDespawn: IllusionistClone.Despawn(); break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        // ====================================================================
        // Round reset
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                illusionist = null;
                active = false;
                recordBuffer.Clear();
                ventBuffer.Clear();
                rxPts.Clear();
                rxVnt.Clear();
                recording = false;
                IllusionistClone.Despawn();
            }
        }

        // ====================================================================
        // Game start: host picks the Illusionist among plain Impostors and broadcasts it.
        // ====================================================================
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

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;
                    SendSetIllusionist(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Per-frame: record sampling (local), clone replay (all). Despawn clone at meetings.
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    IllusionistClone.Update();

                    if (!IsLocalIllusionist() || !recording || InMeeting()) return;
                    var me = PlayerControl.LocalPlayer;
                    if (!IsAlive(me)) { recording = false; return; }
                    if (Time.time - lastSample >= SampleInterval) {
                        recordBuffer.Add(me.GetTruePosition());
                        ventBuffer.Add(me.inVent);
                        lastSample = Time.time;
                    }
                    if (Time.time - recordStart >= RecordLengthValue()) recording = false; // auto-stop when full
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] HudUpdate failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() { recording = false; IllusionistClone.Despawn(); }
        }

        // ====================================================================
        // Kill interaction: a kill aimed at the clone (closer than any real target) is blocked + flashed.
        // ====================================================================
        [HarmonyPatch(typeof(KillButton), nameof(KillButton.DoClick))]
        [HarmonyPriority(Priority.High)]
        static class KillButtonDoClickPatch {
            public static bool Prefix(KillButton __instance) {
                try {
                    if (!active || !IllusionistClone.IsActive()) return true;
                    var me = PlayerControl.LocalPlayer;
                    if (me == null || me.Data == null || me.Data.IsDead || me.Data.Role == null || !me.Data.Role.IsImpostor) return true;
                    if (__instance == null || __instance.isCoolingDown || !me.CanMove) return true;

                    Vector2 here = me.GetTruePosition();
                    float cloneDist = Vector2.Distance(here, IllusionistClone.Position());
                    if (cloneDist > KillRange()) return true; // clone not in reach -> let the normal kill run

                    float realDist = __instance.currentTarget != null
                        ? Vector2.Distance(here, __instance.currentTarget.GetTruePosition()) : float.MaxValue;
                    if (cloneDist > realDist) return true; // a real target is closer -> normal kill

                    // The clone intercepts the kill: shield flash, no death.
                    SendCloneFlash();
                    if (BlockPenalty == null || BlockPenalty.getBool())
                        me.SetKillTimer(GameOptionsManager.Instance.currentNormalGameOptions.KillCooldown);
                    __instance.SetTarget(null);
                    return false;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] kill intercept failed: {e}");
                    return true;
                }
            }
        }

        // ====================================================================
        // Buttons: RECORD (toggle) + PLAYBACK (spawn clone).
        // ====================================================================
        private static TheOtherRoles.Objects.CustomButton recordButton;
        private static TheOtherRoles.Objects.CustomButton playbackButton;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    var recordSprite = Helpers.loadSpriteFromResources("TheOtherRoles.Resources.SampleButton.png", 115f);
                    var playbackSprite = Helpers.loadSpriteFromResources("TheOtherRoles.Resources.MorphButton.png", 115f);

                    recordButton = new TheOtherRoles.Objects.CustomButton(
                        () => { // OnClick: toggle recording
                            if (recording) { recording = false; return; }
                            recordBuffer.Clear();
                            ventBuffer.Clear();
                            recording = true;
                            recordStart = Time.time;
                            lastSample = 0f;
                        },
                        () => active && IsLocalIllusionist()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove,
                        () => { },
                        recordSprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowLeft,
                        __instance, KeyCode.R, false, "RECORD");
                    recordButton.MaxTimer = 0f;
                    recordButton.Timer = 0f;

                    playbackButton = new TheOtherRoles.Objects.CustomButton(
                        () => { // OnClick: spawn the clone from the recorded path
                            if (recording || recordBuffer.Count == 0) return;
                            SendSpawnClone(new List<Vector2>(recordBuffer), new List<bool>(ventBuffer));
                            playbackButton.Timer = playbackButton.MaxTimer;
                        },
                        () => active && IsLocalIllusionist() && !recording && recordBuffer.Count > 0
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove,
                        () => { },
                        playbackSprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter,
                        __instance, KeyCode.F, false, "PLAYBACK");
                    playbackButton.MaxTimer = PlaybackCooldown != null ? PlaybackCooldown.getFloat() : 30f;
                    playbackButton.Timer = 10f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] Button creation failed: {e}");
                }
            }
        }

        // ====================================================================
        // Role identity: show the Illusionist as its own role over the Impostor entry.
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || illusionist == null || p == null || p != illusionist || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) {
                            __result[i] = IllusionistInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, IllusionistInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
