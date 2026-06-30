// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Copycat (Neutral)
 *
 * A normal TOR Crewmate is silently promoted to "The Copycat" at game start (host-authoritative pick,
 * broadcast via RPC 206). The Copycat learns abilities by witnessing other players use them (tracked
 * via TOR CustomRPC IDs on PlayerControl.HandleRpc). Up to MaxAbilitiesStored abilities can be kept
 * simultaneously (oldest is dropped when full). Each learned ability appears as a CustomButton on the
 * Copycat's HUD. The Copycat wins if alive when the game ends (wins with the winning team).
 *
 * Tracked TOR abilities:
 *   Camouflage  (RPC 131) - become grey
 *   Morphling   (RPC 130) - sample a player, then morph into them
 *   Vent        (RPC 107) - use vents
 *   TimeMaster  (RPC 126) - temporary shield
 *   Sheriff     (RPC 108) - shoot attempt (kills Impostors, dies if target is Crew)
 *
 * Options live in the 1520-1524 block. See ID-Registry.md.
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
    public static class Copycat {
        // ---- Ability enum ----
        public enum Ability : byte {
            Camouflage = 0,
            Morphling = 1,
            Vent = 2,
            TimeMaster = 3,
            Sheriff = 4
        }

        private static readonly string[] AbilityNames = { "CAMO", "MORPH", "VENT", "SHIELD", "SHOOT" };

        // ---- Theme ----
        public static readonly Color Color = new Color(0.85f, 0.45f, 0.85f); // purple

        // ---- Options (IDs 1520-1524) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption MaxAbilitiesStored;
        public static CustomOption CopycatHasTasks;
        public static CustomOption AbilitiesNeededToWin;

        // ---- Runtime state ----
        public static PlayerControl copycat;
        public static bool active;

        // Learned abilities (ordered by learning time)
        private static readonly List<Ability> learnedAbilities = new();
        private static readonly HashSet<Ability> usedAbilities = new();

        // Shield state (TimeMaster copy)
        public static bool shielded;
        private static float shieldEndTime;

        // Camouflage state
        private static bool camouflaged;
        private static float camoEndTime;

        // Morphling state
        private static byte morphTargetId = byte.MaxValue;
        private static bool isMorphed;
        private static float morphEndTime;

        // ---- TOR CustomRPC byte values (enum is internal) ----
        private const byte TorCamouflageRpc = 131;
        private const byte TorMorphlingRpc = 130;
        private const byte TorVentRpc = 107;
        private const byte TorTimeMasterRpc = 126;
        private const byte TorSheriffRpc = 108;

        // ---- Custom RPC (202) subtypes ----
        private const byte RpcId = 206;
        private const byte SubSetCopycat = 0;
        private const byte SubUseAbility = 1;   // abilityId
        private const byte SubEndCamouflage = 2;
        private const byte SubEndMorph = 3;

        // ---- Role identity ----
        private static RoleInfo copycatInfo;
        public static RoleInfo CopycatInfo() => copycatInfo ??= new RoleInfo(
            "Copycat", Color, "Copy abilities you witness and win with the winners",
            "Copy abilities you witness and win with the winners", RoleId.Crewmate)
        { isNeutral = true };

        // Buttons
        private static readonly Dictionary<Ability, TheOtherRoles.Objects.CustomButton> abilityButtons = new();
        private static Sprite cachedButtonSprite;

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1520, Types.Neutral, "Copycat",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1521, Types.Neutral, "Copycat Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                MaxAbilitiesStored = CustomOption.Create(1522, Types.Neutral, "Copycat Max Stored Abilities",
                    3f, 1f, 6f, 1f, SpawnRate);
                CopycatHasTasks = CustomOption.Create(1523, Types.Neutral, "Copycat Has Tasks",
                    true, SpawnRate);
                AbilitiesNeededToWin = CustomOption.Create(1524, Types.Neutral, "Copycat Abilities Needed To Win",
                    1f, 0f, 6f, 1f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Copycat] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;
                var checkType = torAsm.GetType("TheOtherRoles.Patches.CheckEndCriteriaPatch");
                if (checkType == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning("[Copycat] CheckEndCriteriaPatch not found — win intercept disabled.");
                    return;
                }
                var methodNames = new[] {
                    "CheckAndEndGameForCrewmateWin",
                    "CheckAndEndGameForImpostorWin",
                    "CheckAndEndGameForJackalWin",
                    "CheckAndEndGameForTaskWin",
                    "CheckAndEndGameForSabotageWin"
                };
                var prefix = new HarmonyMethod(typeof(Copycat), nameof(CopycatWinPrefix));
                foreach (var name in methodNames) {
                    var m = checkType.GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (m != null) {
                        harmony.Patch(m, prefix: prefix);
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[Copycat] Patched {name} for Copycat survival check.");
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] TryPatch failed: {e}");
            }
        }

        // Intercept endgame checks to prevent game from ending too early if Copycat is alive
        // (so the Copycat always survives to see the winner declared)
        public static bool CopycatWinPrefix(ref bool __result) {
            try {
                if (CopycatIsAlive()) {
                    // Check if this would be the last player standing scenario
                    int aliveCount = 0;
                    foreach (var p in PlayerControl.AllPlayerControls) {
                        if (p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected)
                            aliveCount++;
                    }
                    // If only 2 players alive (Copycat + 1 other), let the game end
                    if (aliveCount <= 2) return true;
                    // Otherwise, prevent game from ending (Copycat must survive more)
                    __result = false;
                    return false; // skip the original end check
                }
            } catch { }
            return true; // let original check run
        }

        private static bool CopycatIsAlive() =>
            active && copycat != null && copycat.Data != null && !copycat.Data.IsDead && !copycat.Data.Disconnected;

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalCopycat() =>
            copycat != null && PlayerControl.LocalPlayer != null && copycat.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        // ---- Helper: Map TOR RPC callId to Copycat ability ----
        private static Ability? RpcToAbility(byte callId) {
            return callId switch {
                TorCamouflageRpc => Ability.Camouflage,
                TorMorphlingRpc => Ability.Morphling,
                TorVentRpc => Ability.Vent,
                TorTimeMasterRpc => Ability.TimeMaster,
                TorSheriffRpc => Ability.Sheriff,
                _ => null
            };
        }

        private static int MaxAbilities() =>
            MaxAbilitiesStored != null ? Mathf.RoundToInt(MaxAbilitiesStored.getFloat()) : 3;

        private static int NeededToWin() =>
            AbilitiesNeededToWin != null ? Mathf.RoundToInt(AbilitiesNeededToWin.getFloat()) : 1;

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetCopycat(byte id) {
            try {
                var w = BeginRpc(SubSetCopycat);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetCopycat(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] SendSetCopycat failed: {e}"); }
        }

        private static void SendUseAbility(Ability ability) {
            try {
                var w = BeginRpc(SubUseAbility);
                w.Write((byte)ability);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyUseAbility(ability);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] SendUseAbility failed: {e}"); }
        }

        private static void ApplySetCopycat(byte id) {
            copycat = Helpers.playerById(id);
            active = copycat != null;
            if (active) UCPromotion.Claim(id);
            learnedAbilities.Clear();
            usedAbilities.Clear();
            shielded = false;
            camouflaged = false;
            isMorphed = false;
            morphTargetId = byte.MaxValue;
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Copycat] The Copycat is {copycat.Data?.PlayerName}.");
        }

        private static void ApplyUseAbility(Ability ability) {
            if (!active || copycat == null) return;
            if (!IsAlive(copycat)) return;

            switch (ability) {
                case Ability.Camouflage: StartCamouflage(); break;
                case Ability.Morphling: StartMorph(); break;
                case Ability.Vent: DoVent(); break;
                case Ability.TimeMaster: StartShield(); break;
                case Ability.Sheriff: DoShoot(); break;
            }

            if (!usedAbilities.Contains(ability)) {
                usedAbilities.Add(ability);
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Copycat] Used ability {ability} (total used: {usedAbilities.Count}).");
            }
        }

        // ---- Ability implementations (run on every client via RPC) ----
        private static byte originalColorId;

        private static void StartCamouflage() {
            camouflaged = true;
            camoEndTime = Time.time + 10f;
            originalColorId = (byte)copycat.Data.DefaultOutfit.ColorId;
            copycat.RpcSetColor(6); // grey
        }

        private static void EndCamouflage() {
            camouflaged = false;
            if (copycat != null) {
                copycat.RpcSetColor(originalColorId);
            }
        }

        private static void StartMorph() {
            var targets = PlayerControl.AllPlayerControls.ToArray()
                .Where(p => IsAlive(p) && p.PlayerId != copycat.PlayerId).ToList();
            if (targets.Count == 0) return;
            var target = targets[rnd.Next(targets.Count)];
            morphTargetId = target.PlayerId;
            isMorphed = true;
            morphEndTime = Time.time + 15f;
            originalColorId = (byte)copycat.Data.DefaultOutfit.ColorId;
            byte targetColor = (byte)target.Data.DefaultOutfit.ColorId;
            copycat.RpcSetColor(targetColor);
        }

        private static void EndMorph() {
            isMorphed = false;
            morphTargetId = byte.MaxValue;
            if (copycat != null) {
                copycat.RpcSetColor(originalColorId);
            }
        }

        private static void DoVent() {
            // Allow the Copycat to use vents - handled by the vent system
            // Send the unchecked vent RPC
            if (AmongUsClient.Instance != null) {
                var w = AmongUsClient.Instance.StartRpcImmediately(
                    copycat.NetId, TorVentRpc, SendOption.Reliable, -1);
                AmongUsClient.Instance.FinishRpcImmediately(w);
            }
        }

        private static void StartShield() {
            shielded = true;
            shieldEndTime = Time.time + 5f;
        }

        private static void DoShoot() {
            // Sheriff shoot: the copycat clicks the button while targeting a player
            // This is handled in the button onClick via targeting
        }

        public static void MarkFromDraft(byte playerId) => ApplySetCopycat(playerId);

        // ---- Learn ability (called when a tracked RPC is seen) ----
        private static void LearnAbility(Ability ability) {
            if (!active || copycat == null) return;
            if (learnedAbilities.Contains(ability)) return; // already known

            int maxAbilities = MaxAbilities();
            if (learnedAbilities.Count >= maxAbilities && maxAbilities > 0) {
                // Drop the oldest learned ability
                var oldest = learnedAbilities[0];
                learnedAbilities.RemoveAt(0);
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Copycat] Dropped oldest ability {oldest} to make room for {ability}.");
            }

            if (learnedAbilities.Count < maxAbilities) {
                learnedAbilities.Add(ability);
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Copycat] Learned ability {ability} (total: {learnedAbilities.Count}).");
            }
        }

        // ---- RPC handlers ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                try {
                    if (callId == RpcId) {
                        // Our own RPC — handle and suppress original to prevent anti-cheat kick
                        byte subtype = reader.ReadByte();
                        switch (subtype) {
                            case SubSetCopycat: ApplySetCopycat(reader.ReadByte()); break;
                            case SubUseAbility: ApplyUseAbility((Ability)reader.ReadByte()); break;
                            case SubEndCamouflage: EndCamouflage(); break;
                            case SubEndMorph: EndMorph(); break;
                        }
                        return false;
                    }

                    // Track ability usage for learning (Prefix: can check callId without consuming reader)
                    if (active && copycat != null) {
                        var ability = RpcToAbility(callId);
                        if (ability != null) {
                            LearnAbility(ability.Value);
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] HandleRpc failed: {e}");
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                copycat = null;
                active = false;
                learnedAbilities.Clear();
                usedAbilities.Clear();
                shielded = false;
                camouflaged = false;
                isMorphed = false;
                morphTargetId = byte.MaxValue;
                shieldEndTime = 0;
                camoEndTime = 0;
                morphEndTime = 0;
                abilityButtons.Clear();
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
                    SendSetCopycat(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Target tracking for abilities that need it (Sheriff, Morphling) ----
        private static PlayerControl currentTarget;
        private static PlayerControl currentMorphTarget;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (!active || copycat == null) return;
                    bool local = IsLocalCopycat();
                    if (!local) return;

                    // Update targeting for Sheriff and Morphling
                    currentTarget = null;
                    currentMorphTarget = null;
                    float closestDist = 2f; // max targeting distance
                    float closestMorphDist = 5f; // morph can target from farther

                    foreach (var p in PlayerControl.AllPlayerControls) {
                        if (p == null || !IsAlive(p) || p.PlayerId == copycat.PlayerId) continue;
                        float d = Vector2.Distance(copycat.GetTruePosition(), p.GetTruePosition());

                        if (d < closestDist && learnedAbilities.Contains(Ability.Sheriff)) {
                            closestDist = d;
                            currentTarget = p;
                        }
                        if (d < closestMorphDist && learnedAbilities.Contains(Ability.Morphling)) {
                            closestMorphDist = d;
                            currentMorphTarget = p;
                        }
                    }

                    // Time out effects
                    if (shielded && Time.time >= shieldEndTime) shielded = false;
                    if (camouflaged && Time.time >= camoEndTime) EndCamouflage();
                    if (isMorphed && Time.time >= morphEndTime) EndMorph();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] HudUpdate failed: {e}");
                }
            }
        }

        // ---- Button creation (one per ability) ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    cachedButtonSprite = __instance.KillButton != null && __instance.KillButton.graphic != null
                        ? __instance.KillButton.graphic.sprite : null;

                    // Create a button for each ability
                    CreateAbilityButton(Ability.Camouflage,  __instance, 0);
                    CreateAbilityButton(Ability.Morphling,   __instance, 1);
                    CreateAbilityButton(Ability.Vent,        __instance, 2);
                    CreateAbilityButton(Ability.TimeMaster,  __instance, 3);
                    CreateAbilityButton(Ability.Sheriff,     __instance, 4);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] Button creation failed: {e}");
                }
            }
        }

        private static void CreateAbilityButton(Ability ability, HudManager __instance, int index) {
            var pos = TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter;
            if (index == 0) pos = TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowLeft;
            else if (index == 1) pos = TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter;
            else if (index == 2) pos = TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowRight;
            else if (index == 3) pos = TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowLeft;
            else if (index == 4) pos = TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowCenter;

            var button = new TheOtherRoles.Objects.CustomButton(
                () => OnAbilityClick(ability),
                () => IsAbilityAvailable(ability),
                () => IsAbilityVisible(ability),
                () => { /* on meeting - nothing special */ },
                GetAbilitySprite(ability),
                pos,
                __instance, KeyCode.None, false, AbilityNames[(int)ability]);
            abilityButtons[ability] = button;
        }

        private static readonly Dictionary<Ability, Sprite> abilitySpriteCache = new();
        private static Sprite GetAbilitySprite(Ability ability) {
            if (abilitySpriteCache.TryGetValue(ability, out var cached)) return cached;
            string resource = null;
            switch (ability) {
                case Ability.Camouflage: resource = "TheOtherRoles.Resources.CamoButton.png"; break;
                case Ability.Morphling:  resource = "TheOtherRoles.Resources.MorphButton.png"; break;
                case Ability.Vent:       resource = "TheOtherRoles.Resources.Vent.png"; break;
                case Ability.TimeMaster: resource = "TheOtherRoles.Resources.TimeShieldButton.png"; break;
            }
            if (resource != null) {
                var spr = Helpers.loadSpriteFromResources(resource, 115f);
                if (spr != null) {
                    abilitySpriteCache[ability] = spr;
                    return spr;
                }
            }
            return cachedButtonSprite;
        }

        private static bool IsAbilityAvailable(Ability ability) {
            if (!active || copycat == null) return false;
            if (!IsLocalCopycat()) return false;
            if (copycat.Data == null || copycat.Data.IsDead) return false;
            if (!learnedAbilities.Contains(ability)) return false;

            switch (ability) {
                case Ability.Sheriff: return currentTarget != null;
                case Ability.Morphling: return currentMorphTarget != null;
                case Ability.Camouflage: return !camouflaged;
                case Ability.TimeMaster: return !shielded;
                default: return true;
            }
        }

        private static bool IsAbilityVisible(Ability ability) {
            if (!active || copycat == null) return false;
            if (!IsLocalCopycat()) return false;
            if (copycat.Data == null || copycat.Data.IsDead) return false;
            return learnedAbilities.Contains(ability);
        }

        private static void OnAbilityClick(Ability ability) {
            if (!IsLocalCopycat()) return;
            switch (ability) {
                case Ability.Sheriff:
                    if (currentTarget == null) return;
                    // Perform sheriff check
                    PerformSheriffKill(currentTarget);
                    break;
                case Ability.Morphling:
                    if (currentMorphTarget == null) return;
                    morphTargetId = currentMorphTarget.PlayerId;
                    SendUseAbility(ability);
                    break;
                default:
                    SendUseAbility(ability);
                    break;
            }
        }

        private static void PerformSheriffKill(PlayerControl target) {
            if (target == null || target.Data == null) return;
            bool targetIsImpostor = target.Data.Role != null && target.Data.Role.IsImpostor;
            if (targetIsImpostor) {
                // Kill the target
                if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) {
                    target.Data.IsDead = true;
                    AmongUsClient.Instance.FinishRpcImmediately(
                        AmongUsClient.Instance.StartRpcImmediately(
                            target.NetId, TorSheriffRpc, SendOption.Reliable, -1));
                }
            } else {
                // Kill the Copycat instead
                if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) {
                    copycat.Data.IsDead = true;
                }
            }
            usedAbilities.Add(Ability.Sheriff);
        }

        // ---- Task management ----
        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class TaskPatch {
            public static void Postfix(GameData __instance) {
                try {
                    if (!active || copycat == null || copycat.Data == null) return;

                    // Remove Copycat's tasks from totals (neutral role)
                    var (completed, total) = TasksHandler.taskInfo(copycat.Data);
                    __instance.TotalTasks -= total;
                    __instance.CompletedTasks -= completed;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] TaskPatch failed: {e}");
                }
            }
        }

        // ---- Win with winning team if alive ----
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)]
        static class OnGameEndPatch {
            public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ref EndGameResult endGameResult) {
                try {
                    if (!active || copycat == null || copycat.Data == null) return;
                    if (copycat.Data.IsDead || copycat.Data.Disconnected) return;

                    int needed = NeededToWin();
                    int used = usedAbilities.Count;
                    if (used < needed) {
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[Copycat] Copycat survived but only used {used}/{needed} abilities — did NOT win with winners.");
                        return;
                    }

                    // Add Copycat to winners
                    bool alreadyWinner = false;
                    foreach (var w in EndGameResult.CachedWinners) {
                        if (w != null && w.PlayerName == copycat.Data.PlayerName) {
                            alreadyWinner = true;
                            break;
                        }
                    }
                    if (!alreadyWinner) {
                        EndGameResult.CachedWinners.Add(new CachedPlayerData(copycat.Data));
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[Copycat] Copycat wins with the winners! (used {used}/{needed} abilities)");
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] OnGameEnd failed: {e}");
                }
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || copycat == null || p == null || p != copycat || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = CopycatInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, CopycatInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
