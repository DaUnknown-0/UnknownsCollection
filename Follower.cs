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

        // AUDIT-2026-08-23, M-2 (FRAGEN Nr. 10, answered 2026-08-23: "end screen and export only").
        //
        // The takeover moves a TOR role STATIC, and those are single references: setRole writes
        // Sheriff.sheriff = <follower>, so the dead player stops being the Sheriff retroactively.
        // Everything that asks getRoleInfoForPlayer then reports them as a plain Crewmate, including
        // the end-of-game summary and TrackerExport's snapshot: a player who spent the round as the
        // Sheriff is recorded as having had no role at all.
        //
        // The role they held is remembered here so the summary can put it back. Deliberately NOT
        // restored during the round: a Medium or a Seer would then read the same role twice, on the
        // corpse and on the Follower, which is exactly the tell the takeover is supposed to hide.
        // The choice of "end screen and export only" is the answer recorded in FRAGEN.md Nr. 10.
        private static readonly Dictionary<byte, RoleId> rolesTakenOver = new Dictionary<byte, RoleId>();

        // True from the moment the game ends. Set from a Priority.First prefix on OnGameEnd so it is
        // already true for every other consumer of that event: TrackerExport reads its snapshot in an
        // OnGameEnd PREFIX of its own (TOR's postfix wipes the statics), so a flag set any later
        // would miss the very reader this fix exists for.
        private static bool endScreenActive;

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

        public static void TryPatch(Harmony harmony) {
            // Receiver registration for the shared UC channel (UCRpc.CallId = 230). Every module
            // registers here even when it has no Harmony work left to do - TryPatch is the single
            // place UnknownsCollectionPlugin.Load() calls for every module.
            UCRpc.Register(RpcId, HandleModuleRpc);
        }

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalFollower() =>
            follower != null && PlayerControl.LocalPlayer != null && follower.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = UCRpc.Begin(RpcId); // shared UC channel; RpcId is the module byte
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
            // Remember what the dead player was, for the end-of-game summary only (see the field's
            // comment). Plain Crewmate/Impostor is not worth recording: there is no role to lose.
            if (roleId != RoleId.Crewmate && roleId != RoleId.Impostor)
                rolesTakenOver[targetId] = roleId;

            // Takeover riser + energy-burst only for the Follower itself - the new role stays secret for
            // everyone else, so both cues share the exact same local-only gate.
            if (f == PlayerControl.LocalPlayer) {
                UCAssets.PlayFollowerShift();
                FollowerFx.SpawnShift(f.GetTruePosition());
            }
            UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[Follower] {f.Data?.PlayerName} took over the role of {t.Data?.PlayerName} ({roleId}).");
        }

        public static void MarkFromDraft(byte playerId) => ApplySetFollower(playerId);

        // RPC receiver, registered on the shared UC channel in TryPatch. UCRpc's dispatcher
        // already consumed the module byte, so this starts at the subtype byte - the wire
        // format behind the module byte is byte-for-byte what the old per-callId RPC used.
        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                // HOST-ONLY, both subtypes: SubSetFollower declares who the Follower is (host pick in
                // IntroCutscene.OnDestroy), SubShiftRole hands a player an ARBITRARY TOR role via
                // erasePlayerRoles + setRole (host-side first-death detection). Neither has a legitimate
                // non-host sender (AUDIT-2026-08-11.md, H-3).
                if (!UCRpc.RequireHost($"Follower.subtype{subtype}")) return;
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
        }

        // PlayerId-keyed state is cleared on OnGameJoined as well as on resetVariables
        // (AUDIT M-12). PlayerIds are handed out per LOBBY, and resetVariables only ever
        // arrives from a host that has this mod - so joining a vanilla host, or leaving a
        // lobby abnormally, used to carry the previous game's ids into the next one and let
        // them act on whoever happens to reuse them. Same belt-and-suspenders rule the
        // Silencer and the Shade already followed; the body is shared so the two entry
        // points can never drift apart.
        private static void ClearState() {
            follower = null;
            active = false;
            hasCopied = false;
            rolesTakenOver.Clear();
            endScreenActive = false;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Follower", ClearState);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinPatch {
            public static void Postfix() => UCResetGuard.Run("Follower", ClearState);
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

        // AUDIT-2026-08-11 M-1, fixed 2026-08-23: the three hooks below cover murders and the two
        // exile controllers, but SIX ways to die reach neither. They all call target.Exiled()
        // directly: the UC Poisoner (Poisoner.cs), TOR's Witch spell during the exile phase, a
        // Guesser hit (RPC.cs:1015), the Lover following their partner, the Lawyer/Pursuer suicide
        // and the Shifter losing their old role. A Follower whose lobby saw one of those as the
        // first death simply kept waiting for a murder that had already been overtaken.
        //
        // Gated on "no exile controller is running" on purpose. A regular vote-out ALSO passes
        // through Exiled(), and that case is already handled by the WrapUp hooks below, which fire
        // after the cutscene. Without the gate the takeover would move to the start of the
        // cutscene instead: harmless thanks to the hasCopied guard, but a visible timing change to
        // the one path that works today. So the vote keeps its existing route and this hook only
        // catches the deaths that have none.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Exiled))]
        static class ExiledDirectPatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    if (ExileController.Instance != null) return;   // the vote path owns this one
                    HandleFirstDeath(__instance);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Follower] Exiled detection failed: {e}");
                }
            }
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

        // Arms the end-screen restore. Priority.First so this runs before every other prefix on
        // OnGameEnd, including TrackerExport's snapshot in another assembly.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.First)]
        static class EndScreenArmPatch {
            public static void Prefix() => endScreenActive = true;
        }

        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (p == null || __result == null) return;

                    // The player whose role was taken gets it back, but only once the round is over
                    // (see rolesTakenOver). Checked before the Follower branch below because this is
                    // about a DIFFERENT player: the corpse, not the Follower.
                    if (endScreenActive && rolesTakenOver.TryGetValue(p.PlayerId, out RoleId stolen)) {
                        bool alreadyThere = false;
                        for (int i = 0; i < __result.Count; i++)
                            if (__result[i] != null && __result[i].roleId == stolen) { alreadyThere = true; break; }
                        if (!alreadyThere) {
                            var info = RoleInfo.allRoleInfos.FirstOrDefault(x => x != null && x.roleId == stolen);
                            if (info != null) {
                                // Replace the bare "Crewmate" the moved static left behind; if the list
                                // says something else entirely, prepend instead of throwing it away.
                                bool replacedStolen = false;
                                for (int i = 0; i < __result.Count; i++) {
                                    if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                                        __result[i] = info;
                                        replacedStolen = true;
                                        break;
                                    }
                                }
                                if (!replacedStolen) __result.Insert(0, info);
                            }
                        }
                    }

                    if (!active || follower == null || p != follower) return;

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