// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

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
    public static class Follower {
        public static readonly Color Color = new Color(0.7f, 0.7f, 0.7f);

        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;

        public static PlayerControl follower;
        public static bool active;

        // The role the Follower copied from the first dead player
        public static byte copiedRoleId = byte.MaxValue; // RoleId byte of the copied role, or MaxValue = none
        public static PlayerControl copiedFrom;           // the dead player we copied
        private static bool firstDeathProcessed;          // host: has the first death been processed?

        private const byte RpcId = 200;
        private const byte SubSetFollower = 0; // followerId
        private const byte SubCopyRole = 1;    // roleId(byte), sourcePlayerId(byte)

        private static RoleInfo followerInfo;
        public static RoleInfo FollowerInfo() => followerInfo ??= new RoleInfo(
            "Follower", Color, "Copy the role of the first player to die",
            "Copy the role of the first player to die", RoleId.Crewmate)
        { isNeutral = true };

        // Cached copied RoleInfo (built from the received data)
        private static RoleInfo copiedDisplayInfo;

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1500, Types.Neutral, "Follower",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1501, Types.Neutral, "Follower Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Follower] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Follower] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { }

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalFollower() =>
            follower != null && PlayerControl.LocalPlayer != null && follower.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetFollower(byte id) {
            try {
                var w = BeginRpc(SubSetFollower);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetFollower(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Follower] SendSetFollower failed: {e}"); }
        }

        private static void SendCopyRole(byte roleId, byte sourcePlayerId) {
            try {
                var w = BeginRpc(SubCopyRole);
                w.Write(roleId);
                w.Write(sourcePlayerId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyCopyRole(roleId, sourcePlayerId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Follower] SendCopyRole failed: {e}"); }
        }

        private static void ApplySetFollower(byte id) {
            follower = Helpers.playerById(id);
            active = follower != null;
            if (active) UCPromotion.Claim(id);
            copiedRoleId = byte.MaxValue;
            copiedFrom = null;
            copiedDisplayInfo = null;
            firstDeathProcessed = false;
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Follower] The Follower is {follower.Data?.PlayerName}.");
        }

        // Build a display RoleInfo from the copied player's actual RoleInfo.
        private static void ApplyCopyRole(byte roleId, byte sourcePlayerId) {
            copiedRoleId = roleId;
            copiedFrom = Helpers.playerById(sourcePlayerId);
            if (copiedFrom != null) {
                // Get the dead player's actual role info for display
                var sourceRoles = RoleInfo.getRoleInfoForPlayer(copiedFrom, false);
                if (sourceRoles != null && sourceRoles.Count > 0) {
                    var src = sourceRoles[0];
                    copiedDisplayInfo = new RoleInfo(src.name, src.color, src.introDescription,
                        src.shortDescription, RoleId.Crewmate);
                }
            }
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Follower] Copied role of {copiedFrom?.Data?.PlayerName} (roleId={roleId}).");
        }

        public static void MarkFromDraft(byte playerId) => ApplySetFollower(playerId);

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubSetFollower: ApplySetFollower(reader.ReadByte()); break;
                        case SubCopyRole: {
                            byte roleId = reader.ReadByte();
                            byte sourceId = reader.ReadByte();
                            ApplyCopyRole(roleId, sourceId);
                            break;
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Follower] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                follower = null;
                active = false;
                copiedRoleId = byte.MaxValue;
                copiedFrom = null;
                copiedDisplayInfo = null;
                firstDeathProcessed = false;
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
                    SendSetFollower(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Follower] IntroEnd pick failed: {e}");
                }
            }
        }

        // Detect first death (host): when someone dies for the first time, tell the Follower
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || follower == null || firstDeathProcessed || target == null) return;

                    // Don't count if the follower is the target
                    if (target.PlayerId == follower.PlayerId) return;

                    firstDeathProcessed = true;

                    // Get the dead player's RoleId (use the first entry from getRoleInfoForPlayer)
                    var roles = RoleInfo.getRoleInfoForPlayer(target, false);
                    if (roles == null || roles.Count == 0) return;
                    byte rid = (byte)roles[0].roleId;

                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[Follower] First death: {target.Data?.PlayerName} with roleId={rid}, notifying Follower.");
                    SendCopyRole(rid, target.PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Follower] death detection failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || follower == null || p == null || p != follower || __result == null) return;

                    // If we have copied a role, show that instead of "Follower"
                    if (copiedDisplayInfo != null) {
                        // Replace the Crewmate entry with our copied display
                        bool replaced = false;
                        for (int i = 0; i < __result.Count; i++) {
                            if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                                __result[i] = copiedDisplayInfo;
                                replaced = true;
                            }
                        }
                        if (!replaced) __result.Insert(0, copiedDisplayInfo);
                        return;
                    }

                    // Before first death: show as Follower (grey)
                    bool replacedF = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = FollowerInfo();
                            replacedF = true;
                        }
                    }
                    if (!replacedF) __result.Insert(0, FollowerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Follower] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}