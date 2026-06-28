// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Siphoner (Crewmate)
 *
 * A normal TOR Crewmate is silently promoted to "The Siphoner" at game start (host-authoritative pick,
 * broadcast via RPC 196). While the Siphoner stands close to an Impostor, it drains the Impostor's kill
 * power: every tick the nearby Impostor's KILL COOLDOWN is pushed back by a configurable amount.
 *
 * Why host-authoritative? A Crewmate client does NOT know who the Impostors are, so it cannot run the
 * proximity check itself. The HOST (which knows every role + every position) runs the detection and
 * broadcasts a drain pulse via RPC 196; the targeted Impostor's own client applies SetKillTimer to
 * itself (the only place a kill timer can be extended). This is fully host-side and host-verifiable.
 *
 * ARCHITECTURE mirrors Tesla/Saboteur: brand-new role over the real Crewmate role (first UC crew role)
 * - own RoleInfo tag, a small custom RPC (196), no buttons (passive). Gated by the mod-wide No-Start
 * handshake (everyone has the mod).
 *
 * Options live in the 1460-1466 block. See ID-Registry.md.
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
    public static class Siphoner {
        // ---- Theme ----
        public static readonly Color Color = new Color(0.30f, 0.80f, 0.90f); // siphon cyan (crew role tag)

        // ---- Options (IDs 1460-1466) ----
        public static CustomOption SpawnRate;        // 1460 (header) - crew role chance
        public static CustomOption SpawnMinPlayers;  // 1461 - minimum LOBBY players to spawn
        public static CustomOption DrainRange;        // 1462 - proximity range (world units)
        public static CustomOption PenaltyPerTick;    // 1463 - kill-cd seconds added per tick
        public static CustomOption TickInterval;      // 1464 - seconds between drain ticks
        public static CustomOption ScaleWithDistance; // 1465 - closer = stronger drain
        public static CustomOption WarnImpostor;      // 1466 - drained Impostor sees a warning flash

        // ---- Runtime state ----
        public static PlayerControl siphoner;
        public static bool active;
        private static float lastDrainTime;

        // ---- Custom RPC (196) subtypes ----
        private const byte RpcId = 196; // == UnknownsCollectionPlugin.SiphonerRpcId
        private const byte SubSetSiphoner = 0; // siphonerId
        private const byte SubDrain = 1;       // impostorId, penalty(float)

        // ---- Role identity ----
        private static RoleInfo siphonerInfo;
        public static RoleInfo SiphonerInfo() => siphonerInfo ??= new RoleInfo(
            "Siphoner", Color, "Drain the Impostor's kill power by standing near them",
            "Drain the Impostor's kill power by standing near them", RoleId.Crewmate);

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1460, Types.Crewmate, "Siphoner",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1461, Types.Crewmate, "Siphoner Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                DrainRange = CustomOption.Create(1462, Types.Crewmate, "Siphoner Drain Range",
                    2f, 0.5f, 5f, 0.25f, SpawnRate);
                PenaltyPerTick = CustomOption.Create(1463, Types.Crewmate, "Siphoner Kill-Cooldown Added Per Tick",
                    3f, 0.5f, 15f, 0.5f, SpawnRate);
                TickInterval = CustomOption.Create(1464, Types.Crewmate, "Siphoner Drain Tick Interval",
                    2f, 0.5f, 10f, 0.5f, SpawnRate);
                ScaleWithDistance = CustomOption.Create(1465, Types.Crewmate, "Siphoner Drain Stronger When Closer",
                    true, SpawnRate);
                WarnImpostor = CustomOption.Create(1466, Types.Crewmate, "Drained Impostor Sees A Warning",
                    true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Siphoner] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] CreateOptions failed: {e}");
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

        private static float Range() => DrainRange != null ? DrainRange.getFloat() : 2f;
        private static float Penalty() => PenaltyPerTick != null ? PenaltyPerTick.getFloat() : 3f;
        private static float Interval() => TickInterval != null ? TickInterval.getFloat() : 2f;

        // ====================================================================
        // Custom RPC senders (each applies locally too)
        // ====================================================================
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetSiphoner(byte id) {
            try {
                var w = BeginRpc(SubSetSiphoner);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetSiphoner(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] SendSetSiphoner failed: {e}"); }
        }

        private static void SendDrain(byte impostorId, float penalty) {
            try {
                var w = BeginRpc(SubDrain);
                w.Write(impostorId);
                w.Write(penalty);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyDrain(impostorId, penalty);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] SendDrain failed: {e}"); }
        }

        // ---- Appliers (run on every client) ----
        private static void ApplySetSiphoner(byte id) {
            siphoner = Helpers.playerById(id);
            active = siphoner != null;
            if (active) UCPromotion.Claim(id);
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Siphoner] The Siphoner is {siphoner.Data?.PlayerName}.");
        }

        // The targeted Impostor extends its OWN kill timer; everyone else ignores it.
        private static void ApplyDrain(byte impostorId, float penalty) {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.PlayerId != impostorId) return;
            try {
                me.SetKillTimer(Mathf.Max(me.killTimer, 0f) + penalty);
                if (WarnImpostor == null || WarnImpostor.getBool())
                    Helpers.showFlash(new Color(0.30f, 0.80f, 0.90f, 1f), 0.25f);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Siphoner] drain apply failed: {e.Message}");
            }
        }

        public static void MarkFromDraft(byte playerId) => ApplySetSiphoner(playerId);

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
                        case SubSetSiphoner: ApplySetSiphoner(reader.ReadByte()); break;
                        case SubDrain: {
                            byte impostorId = reader.ReadByte();
                            float penalty = reader.ReadSingle();
                            ApplyDrain(impostorId, penalty);
                            break;
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] HandleRpc failed: {e}");
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
                siphoner = null;
                active = false;
                lastDrainTime = 0f;
            }
        }

        // ====================================================================
        // Game start: host picks the Siphoner among plain Crewmates and broadcasts it.
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

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainCrewmate).ToList();
                    if (candidates.Count == 0) return;
                    SendSetSiphoner(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Host-authoritative proximity drain (runs only on the host, throttled by the tick interval).
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || InMeeting() || !IsAlive(siphoner)) return;
                    if (Time.time - lastDrainTime < Interval()) return;
                    lastDrainTime = Time.time;

                    Vector2 here = siphoner.GetTruePosition();
                    float range = Range();
                    foreach (var p in PlayerControl.AllPlayerControls) {
                        if (!IsAlive(p) || p.Data.Role == null || !p.Data.Role.IsImpostor) continue;
                        float dist = Vector2.Distance(here, p.GetTruePosition());
                        if (dist > range) continue;
                        float penalty = Penalty();
                        if (ScaleWithDistance == null || ScaleWithDistance.getBool())
                            penalty *= Mathf.Clamp(2f - dist / Mathf.Max(range, 0.01f), 1f, 2f);
                        SendDrain(p.PlayerId, penalty);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] drain tick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Role identity: show the Siphoner as its own role over the Crewmate entry.
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || siphoner == null || p == null || p != siphoner || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = SiphonerInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, SiphonerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
