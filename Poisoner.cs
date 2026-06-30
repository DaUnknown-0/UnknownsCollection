// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Poisoner (Impostor)
 *
 * A normal TOR Impostor is silently promoted to "The Poisoner" at game start (host-authoritative pick,
 * broadcast via RPC 193). When the Poisoner kills, the victim's body becomes poisoned. The next player
 * who reports that body becomes poisoned themselves. After X meetings, the poisoned reporter dies unless
 * saved by the Medic's Antidote ability.
 *
 * The Medic gets an Antidote button when a reporter is poisoned, and can cure them once per round
 * (configurable).
 *
 * Options live in the 1430-1434 block. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Poisoner {
        // ---- Theme ----
        public static readonly Color Color = Palette.ImpostorRed;

        // ---- Options (IDs 1430-1434) ----
        public static CustomOption SpawnRate;             // 1430 (header)
        public static CustomOption SpawnMinPlayers;       // 1431
        public static CustomOption PoisonDeathMeetings;   // 1432 - meetings before poisoned reporter dies
        public static CustomOption AntidoteCharges;       // 1433 - how many times Medic can cure
        public static CustomOption MaxPoisonedPerRound;   // 1434 - max poisoned bodies per round

        // ---- Runtime state ----
        public static PlayerControl poisoner;
        public static bool active;

        // Poisoned bodies (victim PlayerId)
        private static readonly HashSet<byte> poisonedBodies = new();
        // Poisoned reporters: reporterId -> meetings since poisoned
        private static readonly Dictionary<byte, int> poisonedReporters = new();
        // Bodies poisoned this round (reset each meeting)
        private static readonly HashSet<byte> bodiesPoisonedThisRound = new();
        private static int meetingCount; // tracks meetings since game start

        // Antidote button
        private static int antidoteUsesLeft;
        private static TheOtherRoles.Objects.CustomButton antidoteButton;
        private static PlayerControl antidoteTarget;

        // ---- Custom RPC (193) subtypes ----
        private const byte RpcId = 193;
        private const byte SubSetPoisoner = 0;
        private const byte SubMarkBody = 1;      // victimId
        private const byte SubPoisonReporter = 2; // reporterId
        private const byte SubAntidote = 3;       // targetId
        private const byte SubPoisonDeath = 4;    // targetId

        // Unchecked murder RPC byte (from TOR's enum)
        internal static byte uncheckedMurderRpc = 108;

        // ---- Role identity ----
        private static RoleInfo poisonerInfo;
        public static RoleInfo PoisonerInfo() => poisonerInfo ??= new RoleInfo(
            "Poisoner", Color, "Your kills poison the reporter; the Medic can save them",
            "Your kills poison the reporter; the Medic can save them", RoleId.Impostor);

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1430, Types.Impostor, "Poisoner",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1431, Types.Impostor, "Poisoner Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                PoisonDeathMeetings = CustomOption.Create(1432, Types.Impostor, "Poison Death After Meetings",
                    2f, 1f, 5f, 1f, SpawnRate);
                AntidoteCharges = CustomOption.Create(1433, Types.Impostor, "Medic Antidote Uses Per Round",
                    1f, 0f, 5f, 1f, SpawnRate);
                MaxPoisonedPerRound = CustomOption.Create(1434, Types.Impostor, "Max Poisoned Bodies Per Round",
                    3f, 1f, 5f, 1f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Poisoner] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;
                try {
                    var rpcEnum = torAsm.GetType("TheOtherRoles.CustomRPC");
                    if (rpcEnum != null)
                        uncheckedMurderRpc = (byte)(int)Enum.Parse(rpcEnum, "UncheckedMurderPlayer");
                } catch (Exception ex) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        $"[Poisoner] Could not resolve UncheckedMurderPlayer RPC id, using {uncheckedMurderRpc}: {ex.Message}");
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] TryPatch failed: {e}");
            }
        }

        // ---- Helpers ----
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);

        private static int AntidoteChargesValue() => AntidoteCharges != null ? Mathf.RoundToInt(AntidoteCharges.getFloat()) : 1;
        private static int MaxPoisonedValue() => MaxPoisonedPerRound != null ? Mathf.RoundToInt(MaxPoisonedPerRound.getFloat()) : 3;
        private static int PoisonDeathValue() => PoisonDeathMeetings != null ? Mathf.RoundToInt(PoisonDeathMeetings.getFloat()) : 2;

        // ---- RPC ----
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetPoisoner(byte id) {
            try {
                var w = BeginRpc(SubSetPoisoner);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetPoisoner(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] SendSetPoisoner failed: {e}"); }
        }

        private static void SendMarkBody(byte victimId) {
            try {
                var w = BeginRpc(SubMarkBody);
                w.Write(victimId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyMarkBody(victimId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] SendMarkBody failed: {e}"); }
        }

        private static void SendPoisonReporter(byte reporterId) {
            try {
                var w = BeginRpc(SubPoisonReporter);
                w.Write(reporterId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyPoisonReporter(reporterId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] SendPoisonReporter failed: {e}"); }
        }

        private static void SendAntidote(byte targetId) {
            try {
                var w = BeginRpc(SubAntidote);
                w.Write(targetId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyAntidote(targetId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] SendAntidote failed: {e}"); }
        }

        private static void SendPoisonDeath(byte targetId) {
            try {
                var w = BeginRpc(SubPoisonDeath);
                w.Write(targetId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyPoisonDeath(targetId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] SendPoisonDeath failed: {e}"); }
        }

        // ---- Appliers ----
        private static void ApplySetPoisoner(byte id) {
            poisoner = Helpers.playerById(id);
            active = poisoner != null;
            if (active) UCPromotion.Claim(id);
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Poisoner] The Poisoner is {poisoner.Data?.PlayerName}.");
        }

        private static void ApplyMarkBody(byte victimId) {
            if (active) poisonedBodies.Add(victimId);
            bodiesPoisonedThisRound.Add(victimId);
        }

        private static void ApplyPoisonReporter(byte reporterId) {
            if (!active || reporterId == byte.MaxValue) return;
            if (!poisonedReporters.ContainsKey(reporterId))
                poisonedReporters[reporterId] = 0;
        }

        private static void ApplyAntidote(byte targetId) {
            poisonedReporters.Remove(targetId);
            antidoteUsesLeft--;
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Poisoner] Antidote used on player {targetId}.");
        }

        private static void ApplyPoisonDeath(byte targetId) {
            var target = Helpers.playerById(targetId);
            if (target == null || target.Data == null) return;
            RpcUncheckedMurder(target.PlayerId, target.PlayerId);
            poisonedReporters.Remove(targetId);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Poisoner] Player {targetId} died from poison.");
        }

        private static void RpcUncheckedMurder(byte sourceId, byte targetId) {
            try {
                MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, uncheckedMurderRpc, SendOption.Reliable, -1);
                w.Write(sourceId);
                w.Write(targetId);
                w.Write((byte)0);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                RPCProcedure.uncheckedMurderPlayer(sourceId, targetId, 0);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] RpcUncheckedMurder failed: {e}");
            }
        }

        public static void MarkFromDraft(byte playerId) => ApplySetPoisoner(playerId);

        // ---- RPC handler ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubSetPoisoner: ApplySetPoisoner(reader.ReadByte()); break;
                        case SubMarkBody: ApplyMarkBody(reader.ReadByte()); break;
                        case SubPoisonReporter: ApplyPoisonReporter(reader.ReadByte()); break;
                        case SubAntidote: ApplyAntidote(reader.ReadByte()); break;
                        case SubPoisonDeath: ApplyPoisonDeath(reader.ReadByte()); break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        // ---- Round reset ----
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                poisoner = null;
                active = false;
                poisonedBodies.Clear();
                poisonedReporters.Clear();
                bodiesPoisonedThisRound.Clear();
                meetingCount = 0;
                antidoteUsesLeft = 0;
                antidoteButton = null;
            }
        }

        // ---- Game start ----
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
                    SendSetPoisoner(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Murder: mark body as poisoned ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || poisoner == null || target == null) return;
                    if (__instance.PlayerId != poisoner.PlayerId) return;
                    if (bodiesPoisonedThisRound.Count >= MaxPoisonedValue()) return;
                    SendMarkBody(target.PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] MurderPatch failed: {e}");
                }
            }
        }

        // ---- Report detection: if the body is poisoned, poison the reporter ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ReportDeadBody))]
        static class ReportPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] NetworkedPlayerInfo target) {
                try {
                    if (AmongUsClient.Instance == null) return;
                    if (!active || poisoner == null || target == null) return;
                    if (!poisonedBodies.Contains(target.PlayerId)) return;
                    if (!IsAlive(__instance)) return;
                    SendPoisonReporter(__instance.PlayerId);
                    poisonedBodies.Remove(target.PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] ReportPatch failed: {e}");
                }
            }
        }

        // ---- Meeting start: increment meeting counter, check for poison deaths ----
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    if (!active || poisoner == null) return;

                    // Reset round tracking
                    bodiesPoisonedThisRound.Clear();

                    // Refill antidote charges
                    antidoteUsesLeft = AntidoteChargesValue();

                    // Increment meeting counter for poisoned reporters
                    meetingCount++;
                    int delay = PoisonDeathValue();
                    var toKill = new List<byte>();
                    foreach (var kvp in poisonedReporters) {
                        int meetingsSince = meetingCount - kvp.Value;
                        if (meetingsSince >= delay) {
                            toKill.Add(kvp.Key);
                        }
                    }

                    // Schedule deaths for after the meeting
                    // We use the meeting end patch to actually kill them
                    _pendingPoisonDeaths = toKill;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] MeetingStartPatch failed: {e}");
                }
            }
        }

        private static List<byte> _pendingPoisonDeaths = new();

        // ---- Meeting end: execute pending poison deaths ----
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        static class MeetingClosePatch {
            public static void Postfix() {
                try {
                    if (!active || poisoner == null || _pendingPoisonDeaths.Count == 0) return;
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

                    foreach (byte id in _pendingPoisonDeaths) {
                        // Don't kill if they were cured during the meeting
                        if (!poisonedReporters.ContainsKey(id)) continue;
                        SendPoisonDeath(id);
                    }
                    _pendingPoisonDeaths.Clear();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] MeetingClosePatch failed: {e}");
                }
            }
        }

        // ---- Antidote button for Medic ----
        private static PlayerControl FindMedic() {
            foreach (var p in PlayerControl.AllPlayerControls) {
                if (p == null) continue;
                if (p.PlayerId == Medic.medic?.PlayerId) return p;
            }
            return null;
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = __instance.KillButton != null && __instance.KillButton.graphic != null
                        ? __instance.KillButton.graphic.sprite : null;
                    antidoteButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            if (antidoteTarget == null || antidoteUsesLeft <= 0) return;
                            SendAntidote(antidoteTarget.PlayerId);
                            antidoteButton.Timer = 2f;
                        },
                        () => active && poisonedReporters.Count > 0 && FindMedic() != null
                              && PlayerControl.LocalPlayer != null
                              && PlayerControl.LocalPlayer.PlayerId == Medic.medic?.PlayerId
                              && !PlayerControl.LocalPlayer.Data.IsDead
                              && antidoteUsesLeft > 0,
                        () => PlayerControl.LocalPlayer.CanMove && antidoteTarget != null && !InMeeting(),
                        () => { },
                        sprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowRight,
                        __instance, KeyCode.G, false, "ANTIDOTE");
                    antidoteButton.MaxTimer = 0f;
                    antidoteButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] Antidote button failed: {e}");
                }
            }
        }

        // ---- Medics's antidote targeting (update every frame) ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (!active || poisonedReporters.Count == 0) return;
                    if (PlayerControl.LocalPlayer == null) return;
                    if (PlayerControl.LocalPlayer.PlayerId != Medic.medic?.PlayerId) return;
                    if (PlayerControl.LocalPlayer.Data == null || PlayerControl.LocalPlayer.Data.IsDead) return;

                    // Find nearest poisoned reporter
                    antidoteTarget = null;
                    float closest = 2f;
                    foreach (var kvp in poisonedReporters) {
                        var p = Helpers.playerById(kvp.Key);
                        if (p == null || !IsAlive(p)) continue;
                        float d = Vector2.Distance(PlayerControl.LocalPlayer.GetTruePosition(), p.GetTruePosition());
                        if (d < closest) { closest = d; antidoteTarget = p; }
                    }

                    if (antidoteTarget != null && antidoteButton != null) {
                        PlayerControlFixedUpdatePatch.setPlayerOutline(antidoteTarget, Color.green);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] HudUpdate failed: {e}");
                }
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || poisoner == null || p == null || p != poisoner || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) {
                            __result[i] = PoisonerInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, PoisonerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
