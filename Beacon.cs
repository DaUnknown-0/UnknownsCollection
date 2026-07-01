// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Beacon (Crewmate)
 *
 * A normal TOR Crewmate is silently promoted to "The Beacon" at game start (host-authoritative pick,
 * broadcast via RPC 204). The Beacon is never affected by lights sabotage (always has full vision).
 * Additionally, any crewmate within ShareRadius of the Beacon AND with a clear line of sight to the
 * Beacon (no wall in between, mirroring Witness.CanSee) shares that full vision: ShipStatus.
 * CalculateLightRadius is patched so the crewmate's own radius is boosted to the Beacon's full radius
 * while in range and visible (LightPatch below) - Among Us only ever renders a single vision circle
 * centered on the local player, so there is no way to give a separate circle around the Beacon itself.
 *
 * Options live in the 1540-1543 block. See ID-Registry.md.
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
using AmongUs.GameOptions;

namespace UnknownsCollection {
    public static class Beacon {
        // ---- Theme ----
        public static readonly Color Color = new Color(1f, 0.92f, 0.35f); // warm gold

        // ---- Options (IDs 1540-1543) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption ShareRadius;
        public static CustomOption NotGuessable;

        // ---- Runtime state ----
        public static PlayerControl beacon;
        public static bool active;

        // ---- Custom RPC (204) subtypes ----
        private const byte RpcId = 204;
        private const byte SubSetBeacon = 0;

        // ---- Role identity ----
        private static RoleInfo beaconInfo;
        public static RoleInfo BeaconInfo() => beaconInfo ??= new RoleInfo(
            "Beacon", Color, "Lights never affect you; crewmates share your vision",
            "Lights never affect you; crewmates share your vision", RoleId.Crewmate);

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1540, Types.Crewmate, "Beacon",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1541, Types.Crewmate, "Beacon Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                ShareRadius = CustomOption.Create(1542, Types.Crewmate, "Beacon Share Radius",
                    15f, 5f, 30f, 1f, SpawnRate);
                NotGuessable = CustomOption.Create(1543, Types.Crewmate, "Beacon Not Guessable",
                    false, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Beacon] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Beacon] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { }

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalBeacon() =>
            beacon != null && PlayerControl.LocalPlayer != null && beacon.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        // Within ShareRadius AND a clear line of sight to the Beacon (no wall in between) - mirrors
        // Witness.CanSee so a crewmate on the other side of a wall doesn't get the vision boost.
        private static bool CanSeeBeacon(PlayerControl crew) {
            Vector2 from = crew.GetTruePosition();
            Vector2 to = beacon.GetTruePosition();
            Vector2 dir = to - from;
            float mag = dir.magnitude;
            float shareDist = ShareRadius != null ? ShareRadius.getFloat() : 15f;
            if (mag > shareDist) return false;
            if (mag < 0.05f) return true;
            return !PhysicsHelpers.AnyNonTriggersBetween(from, dir.normalized, mag, Constants.ShipAndObjectsMask);
        }

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetBeacon(byte id) {
            try {
                var w = BeginRpc(SubSetBeacon);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetBeacon(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Beacon] SendSetBeacon failed: {e}"); }
        }

        private static void ApplySetBeacon(byte id) {
            beacon = Helpers.playerById(id);
            active = beacon != null;
            if (active) UCPromotion.Claim(id);
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Beacon] The Beacon is {beacon.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySetBeacon(playerId);

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    if (subtype == SubSetBeacon) ApplySetBeacon(reader.ReadByte());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Beacon] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                beacon = null;
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
                    SendSetBeacon(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Beacon] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Light radius: Beacon always has full vision; nearby crewmates share it. ----
        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.CalculateLightRadius))]
        static class LightPatch {
            public static void Postfix(ref float __result, ShipStatus __instance, [HarmonyArgument(0)] NetworkedPlayerInfo p) {
                try {
                    if (!active || beacon == null || p == null || !IsAlive(beacon)) return;

                    float fullRadius = __instance.MaxLightRadius
                        * GameOptionsManager.Instance.currentNormalGameOptions.CrewLightMod;

                    if (p.PlayerId == beacon.PlayerId) {
                        __result = fullRadius;
                        return;
                    }

                    // Boost nearby non-impostor crewmates - only while they actually have a clear line of
                    // sight to the Beacon (not just in range), so a wall between them still blocks it.
                    if (p.Role != null && !p.Role.IsImpostor && !p.IsDead && !p.Disconnected) {
                        var crew = Helpers.playerById(p.PlayerId);
                        if (crew != null) {
                            // TOR neutrals (Jackal, Jester, Arsonist, Vulture, ...) run on crewmate base
                            // roles too, so IsImpostor alone doesn't exclude them - mirrors Witness.cs's
                            // crew/neutral split via RoleInfo.isNeutral.
                            var info = RoleInfo.getRoleInfoForPlayer(crew, false).FirstOrDefault();
                            bool isNeutral = info != null && info.isNeutral;
                            if (!isNeutral && CanSeeBeacon(crew)) {
                                __result = Mathf.Max(__result, fullRadius);
                            }
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Beacon] LightPatch failed: {e}");
                }
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || beacon == null || p == null || p != beacon || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = BeaconInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, BeaconInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Beacon] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
