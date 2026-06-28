// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Corrupter (Impostor)
 *
 * A normal TOR Impostor is silently promoted to "The Corrupter" at game start (host-authoritative pick,
 * broadcast via RPC 193). Whenever the Corrupter kills, a "corruption zone" is laid at the body. Living
 * crew who walk INTO that zone see drifting, flickering copies of real players (see CorruptionZone) -
 * pure local hallucinations meant to confuse witnesses. Zones expire after a while and are cleared at
 * every meeting.
 *
 * ARCHITECTURE mirrors Tesla/Saboteur: own RoleInfo tag over the real Impostor role, a small custom RPC
 * (193), client-side FX. Gated by the mod-wide No-Start handshake (everyone has the mod).
 *
 * Options live in the 1430-1437 block. See ID-Registry.md.
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
    public static class Corrupter {
        // ---- Theme ----
        public static readonly Color Color = Palette.ImpostorRed; // impostor role -> red role tag

        // ---- Options (IDs 1430-1437) ----
        public static CustomOption SpawnRate;        // 1430 (header) - impostor role chance
        public static CustomOption SpawnMinPlayers;  // 1431 - minimum LOBBY players to spawn
        public static CustomOption ZoneRadius;        // 1432 - zone disc radius (world units)
        public static CustomOption ZoneDuration;      // 1433 - zone lifetime (seconds)
        public static CustomOption MaxZones;          // 1434 - max simultaneously active zones
        public static CustomOption FiguresPerZone;    // 1435 - fake figures per zone
        public static CustomOption DriftSpeed;        // 1436 - figure drift / flicker speed
        public static CustomOption ImpostorsSeeZones; // 1437 - impostors also see the figures

        // ---- Runtime state ----
        public static PlayerControl corrupter;
        public static bool active;

        // ---- Custom RPC (193) subtypes ----
        private const byte RpcId = 193; // == UnknownsCollectionPlugin.CorrupterRpcId
        private const byte SubSetCorrupter = 0; // corrupterId
        private const byte SubAddZone = 1;      // x, y
        private const byte SubClearZones = 2;   // (none)

        // ---- Option accessors (used by CorruptionZone) ----
        public static float ZoneRadiusValue() => ZoneRadius != null ? ZoneRadius.getFloat() : 2.5f;
        public static float ZoneDurationValue() => ZoneDuration != null ? ZoneDuration.getFloat() : 30f;
        public static int MaxZonesValue() => MaxZones != null ? Mathf.RoundToInt(MaxZones.getFloat()) : 3;
        public static int FiguresPerZoneValue() => FiguresPerZone != null ? Mathf.RoundToInt(FiguresPerZone.getFloat()) : 3;
        public static float DriftSpeedValue() => DriftSpeed != null ? DriftSpeed.getFloat() : 0.6f;
        public static bool ImpostorsSeeZonesValue() => ImpostorsSeeZones != null && ImpostorsSeeZones.getBool();

        // ---- Role identity ----
        private static RoleInfo corrupterInfo;
        public static RoleInfo CorrupterInfo() => corrupterInfo ??= new RoleInfo(
            "Corrupter", Color, "Your kills haunt the area with false visions",
            "Your kills haunt the area with false visions", RoleId.Impostor);

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1430, Types.Impostor, "Corrupter",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1431, Types.Impostor, "Corrupter Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                ZoneRadius = CustomOption.Create(1432, Types.Impostor, "Corruption Zone Radius",
                    2.5f, 1f, 6f, 0.5f, SpawnRate);
                ZoneDuration = CustomOption.Create(1433, Types.Impostor, "Corruption Zone Duration",
                    30f, 5f, 90f, 5f, SpawnRate);
                MaxZones = CustomOption.Create(1434, Types.Impostor, "Corrupter Max Active Zones",
                    3f, 1f, 8f, 1f, SpawnRate);
                FiguresPerZone = CustomOption.Create(1435, Types.Impostor, "Fake Figures Per Zone",
                    3f, 1f, 6f, 1f, SpawnRate);
                DriftSpeed = CustomOption.Create(1436, Types.Impostor, "Figure Drift / Flicker Speed",
                    0.6f, 0.1f, 2f, 0.1f, SpawnRate);
                ImpostorsSeeZones = CustomOption.Create(1437, Types.Impostor, "Impostors Also See The Figures",
                    false, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Corrupter] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Corrupter] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { /* all patches are attribute-based */ }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);

        // ====================================================================
        // Custom RPC senders (each applies locally too)
        // ====================================================================
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetCorrupter(byte id) {
            try {
                var w = BeginRpc(SubSetCorrupter);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetCorrupter(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Corrupter] SendSetCorrupter failed: {e}"); }
        }

        private static void SendAddZone(float x, float y) {
            try {
                var w = BeginRpc(SubAddZone);
                w.Write(x);
                w.Write(y);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                CorruptionZone.Place(x, y);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Corrupter] SendAddZone failed: {e}"); }
        }

        // ---- Appliers (run on every client) ----
        private static void ApplySetCorrupter(byte id) {
            corrupter = Helpers.playerById(id);
            active = corrupter != null;
            if (active) UCPromotion.Claim(id);
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Corrupter] The Corrupter is {corrupter.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySetCorrupter(playerId);

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
                        case SubSetCorrupter: ApplySetCorrupter(reader.ReadByte()); break;
                        case SubAddZone: { float x = reader.ReadSingle(); float y = reader.ReadSingle(); CorruptionZone.Place(x, y); break; }
                        case SubClearZones: CorruptionZone.Clear(); break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Corrupter] HandleRpc failed: {e}");
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
                corrupter = null;
                active = false;
                CorruptionZone.Clear();
            }
        }

        // ====================================================================
        // Game start: host picks the Corrupter among plain Impostors and broadcasts it.
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
                    SendSetCorrupter(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Corrupter] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Kill detection (host): lay a zone at the body of every Corrupter kill.
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || corrupter == null || __instance == null || target == null) return;
                    if (__instance.PlayerId != corrupter.PlayerId) return;
                    Vector2 at = target.GetTruePosition();
                    SendAddZone(at.x, at.y);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Corrupter] kill->zone failed: {e}");
                }
            }
        }

        // ====================================================================
        // Per-frame figure update + clear zones at every meeting.
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try { CorruptionZone.Update(); } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Corrupter] zone update failed: {e}"); }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() { CorruptionZone.Clear(); }
        }

        // ====================================================================
        // Role identity: show the Corrupter as its own role over the Impostor entry.
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || corrupter == null || p == null || p != corrupter || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) {
                            __result[i] = CorrupterInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, CorrupterInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Corrupter] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
