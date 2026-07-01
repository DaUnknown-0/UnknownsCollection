// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using AmongUs.GameOptions; // RoleTypes (vanilla team change for the takeover)
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

        // Role transfer state
        public static bool hasCopied;    // host: has the shift happened?

        // 207, NOT 200: 200/201/202 clash with other DaUnknown mods' reserved RPC ranges (see the
        // UnknownsCollectionPlugin RPC-id block). Reference the shared constant so it can never drift again.
        private const byte RpcId = UnknownsCollectionPlugin.FollowerRpcId;
        private const byte SubSetFollower = 0; // followerId
        private const byte SubShiftRole = 1;   // followerId, targetId

        private static RoleInfo followerInfo;
        public static RoleInfo FollowerInfo() => followerInfo ??= new RoleInfo(
            "Follower", Color, "Take the role of the first player to die",
            "Take the role of the first player to die", RoleId.Crewmate)
        { isNeutral = true };

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

        private static void SendShiftRole(byte followerId, byte targetId) {
            try {
                var w = BeginRpc(SubShiftRole);
                w.Write(followerId);
                w.Write(targetId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyShiftRole(followerId, targetId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Follower] SendShiftRole failed: {e}"); }
        }

        private static void ApplySetFollower(byte id) {
            follower = Helpers.playerById(id);
            active = follower != null;
            if (active) UCPromotion.Claim(id);
            hasCopied = false;
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Follower] The Follower is {follower.Data?.PlayerName}.");
        }

        // Full role takeover: the Follower BECOMES the dead player's role (team + ability + win con),
        // not the narrow, swap-only TOR Shifter (which handled ~22 crew roles and no-op'd on plain
        // crew/impostor/neutrals). Mirrors the Chance modifier's reassign (erasePlayerRoles -> setRole)
        // plus TOR's Thief team change (RoleManager.SetRole). Runs locally on EVERY client, because our
        // SubShiftRole RPC is broadcast to all and UC is gated on everyone-has-the-mod.
        private static void ApplyShiftRole(byte followerId, byte targetId) {
            var f = Helpers.playerById(followerId);
            var t = Helpers.playerById(targetId);
            if (f == null || t == null || f.Data == null || t.Data == null) return;

            // Dead player's primary role (modifiers excluded). For a plain crew/impostor or a UC custom
            // role (whose RoleInfo reports Crewmate/Impostor) this stays Crewmate/Impostor -> team-only copy.
            var info = RoleInfo.getRoleInfoForPlayer(t, false).FirstOrDefault();
            RoleId roleId = info != null ? info.roleId : RoleId.Crewmate;
            bool targetIsImpostor = t.Data.Role != null && t.Data.Role.IsImpostor;

            try {
                // 1. Clear the Follower's current TOR role (keeps vanilla team + modifiers).
                RPCProcedure.erasePlayerRoles(followerId);

                // 2. Only change the vanilla team for an impostor takeover (kill button + impostor win).
                //    For crew/neutral roles the Follower is already a vanilla Crewmate (it was picked from
                //    plain crewmates and erasePlayerRoles keeps the team), so we must NOT re-SetRole here:
                //    re-assigning the same vanilla role re-runs role init on remote clients for no gain
                //    (TOR's own thiefStealsRole likewise only SetRoles in the impostor branch).
                if (targetIsImpostor) {
                    RoleManager.Instance.SetRole(f, RoleTypes.Impostor);
                    if (f == PlayerControl.LocalPlayer && HudManager.Instance != null && HudManager.Instance.KillButton != null)
                        HudManager.Instance.KillButton.SetCoolDown(
                            f.killTimer, GameOptionsManager.Instance.currentNormalGameOptions.KillCooldown);
                }

                // 3. Copy the specific TOR role (plain Crewmate/Impostor have no role static to set).
                if (roleId != RoleId.Crewmate && roleId != RoleId.Impostor)
                    RPCProcedure.setRole((byte)roleId, followerId);

                // 4. Fresh cooldowns for the player who just changed role (local only).
                if (f == PlayerControl.LocalPlayer)
                    TheOtherRoles.Objects.CustomButton.ResetAllCooldowns();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Follower] role takeover failed: {e}");
            }

            hasCopied = true;
            // Takeover riser only for the Follower itself - the new role stays secret for everyone else.
            if (f == PlayerControl.LocalPlayer) UCAssets.PlayFollowerShift();
            UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[Follower] {f.Data?.PlayerName} took over the role of {t.Data?.PlayerName} ({roleId}).");
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
                        case SubShiftRole: {
                            byte fId = reader.ReadByte();
                            byte tId = reader.ReadByte();
                            ApplyShiftRole(fId, tId);
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
                hasCopied = false;
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

        // Shared "first death" handling for both detection paths below (kill and exile) so the same
        // player can never be counted twice as the first death.
        private static void HandleFirstDeath(PlayerControl target) {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            if (!active || follower == null || hasCopied || target == null) return;

            // Don't count if the follower is the target, or if the follower itself is already dead
            // (a dead Follower can't take over a role — otherwise we'd point a role static at a corpse).
            if (target.PlayerId == follower.PlayerId || !IsAlive(follower)) return;

            UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[Follower] First death: {target.Data?.PlayerName}, shifting role to Follower.");
            SendShiftRole(follower.PlayerId, target.PlayerId);
        }

        // Detect first death (host): when someone dies for the first time, tell the Follower
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    HandleFirstDeath(target);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Follower] death detection failed: {e}");
                }
            }
        }

        // Exile also counts as a death. Mirrors TOR's own exile hook (TheOtherRoles.Patches.
        // ExileControllerPatch.ExileControllerWrapUpPatch), which patches both ExileController.WrapUp
        // (regular maps) and AirshipExileController.WrapUpAndSpawn (Airship) and reads the exiled
        // player off __instance.initData.networkedPlayer.Object — the same fields used here.
        [HarmonyPatch(typeof(ExileController), nameof(ExileController.WrapUp))]
        static class ExileWrapUpPatch {
            public static void Postfix(ExileController __instance) {
                try {
                    var networkedPlayer = __instance?.initData.networkedPlayer;
                    HandleFirstDeath(networkedPlayer != null ? networkedPlayer.Object : null);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Follower] exile death detection failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(AirshipExileController), nameof(AirshipExileController.WrapUpAndSpawn))]
        static class AirshipExileWrapUpPatch {
            public static void Postfix(AirshipExileController __instance) {
                try {
                    var networkedPlayer = __instance?.initData.networkedPlayer;
                    HandleFirstDeath(networkedPlayer != null ? networkedPlayer.Object : null);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Follower] exile death detection failed: {e}");
                }
            }
        }

        // The Follower is neutral until it copies a role, so strip its tasks from the crew task-win total
        // (like Bug/Copycat). After the takeover (hasCopied) its tasks count per the new role again.
        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class TaskPatch {
            public static void Postfix(GameData __instance) {
                try {
                    if (!active || hasCopied || follower == null || follower.Data == null) return;
                    var (completed, total) = TasksHandler.taskInfo(follower.Data);
                    __instance.TotalTasks -= total;
                    __instance.CompletedTasks -= completed;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Follower] TaskPatch failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || follower == null || p == null || p != follower || __result == null) return;

                    // After the shift, the Follower has the real role (set by shiftRole), so let it show naturally.
                    if (hasCopied) return;

                    // Before first death: show as Follower (grey)
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = FollowerInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, FollowerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Follower] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}