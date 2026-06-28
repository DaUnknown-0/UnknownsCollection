// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Saboteur (Impostor)
 *
 * A normal TOR Impostor is silently promoted to "The Saboteur" at game start (host-authoritative pick,
 * broadcast via RPC 192). Once per round (= per meeting) he spends tokens on one of two abilities:
 *   - SABOTAGE A TASK: marks a concrete task console. The first non-Impostor who FINISHES that console
 *     dies instantly with an electric kill effect (max one such kill per round). Crew can counter by
 *     SEARCHING a console before using it (Scan-Sweep minigame) and DEFUSING it (Wire-Cut minigame).
 *   - PLACE A TRAP: an invisible ground trap (Trapper-style) that stuns whoever walks into it.
 *
 * ARCHITECTURE (mirrors the Tesla in this same mod): brand-new role built WITHOUT touching TOR source -
 * own RoleInfo (display tag over the real Impostor role), CustomButtons, a small custom RPC (192), and
 * host-authoritative logic. Everything player-facing (kill FX, invisible traps, the crew search/defuse
 * minigames) is client-side, so the role is implicitly gated by the mod-wide No-Start gate
 * (TeslaVersionHandshake.BeginGameGatePatch): the match cannot even start unless everyone runs the same
 * build, therefore "everyone has the mod" always holds in-game.
 *
 * Options live in the 1410-1427 block (Tesla holds 1400-1406). See ID-Registry.md.
 *
 * NOTE: This file currently contains the role scaffold (identity, options, pick, tokens, reset + RPC
 * core). The sabotage-task, kill FX, traps and the crew counterplay are layered on in their own files
 * and partial sections as the role is built out.
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
    public static class Saboteur {
        // ---- Theme ----
        public static readonly Color Color = new Color(0.62f, 0.10f, 0.80f, 1f); // toxic violet ("poison")

        // ---- Options (IDs 1410-1427) ----
        public static CustomOption SpawnRate;            // 1410 (header) - impostor role chance
        public static CustomOption SpawnMinPlayers;      // 1411 - minimum LOBBY players to spawn
        public static CustomOption MinAliveForKill;      // 1412 - min ALIVE players for the sabotage KILL
        public static CustomOption TokensPerRound;       // 1413 - tokens granted each round (per meeting)
        public static CustomOption SabotageTokenCost;    // 1414 - token cost to sabotage a task
        public static CustomOption TrapTokenCost;        // 1415 - token cost to place a trap
        public static CustomOption KillCooldownPenalty;  // 1416 - extra kill-cd seconds after a sabotage kill
        public static CustomOption MaxActiveTraps;       // 1417 - max simultaneously active traps
        public static CustomOption TrapStunDuration;     // 1418 - stun seconds
        public static CustomOption TrapsHitImpostors;    // 1419 - traps also affect other impostors
        public static CustomOption ImpostorsSeeTraps;    // 1420 - other impostors can see traps
        public static CustomOption TrappedLimp;          // 1421 - trapped players limp after the stun
        public static CustomOption SelfLimp;             // 1422 - saboteur can self-limp
        public static CustomOption LimpSpeedMultiplier;  // 1423 - limp speed multiplier
        public static CustomOption LimpDuration;         // 1424 - limp seconds after the stun
        public static CustomOption CrewCanSearch;        // 1425 - crew gets the SEARCH button
        public static CustomOption CrewCanDefuse;        // 1426 - crew can defuse a found sabotage
        public static CustomOption MinAliveForTraps;     // 1427 - min ALIVE players for traps to arm

        // ---- Runtime state (reset each round) ----
        public static PlayerControl saboteur;
        public static bool active;                 // role spawned & usable this game
        public static int tokens;                  // tokens left this round
        public static bool killUsedThisRound;      // at most one sabotage kill per round (host-arbitrated)

        // ---- Sabotage-task state ----
        public static bool sabotagedActive;        // a console is currently sabotaged
        public static float sabotagedX, sabotagedY;// its world position (cross-client stable map geometry)
        private static int lastProgress;           // local player's last task-progress sum (victim poll)
        private static bool progressInit;          // lastProgress has a valid baseline
        private static Console[] consoleCache;      // task consoles on the map (collected lazily per round)

        // ---- Custom RPC (192) subtypes ----
        private const byte RpcId = 192; // == UnknownsCollectionPlugin.SaboteurRpcId
        private const byte SubSetSaboteur = 0;        // saboteurId
        private const byte SubClear = 1;              // (none) - full ability reset (meeting/round)
        private const byte SubSetSabotagedConsole = 2;// x, y  (the marked console position)
        private const byte SubClearSabotage = 3;      // (none)
        private const byte SubRequestKill = 4;        // victimId, x, y  (host-validated request)
        private const byte SubKillFx = 5;             // victimId  (play electric death FX everywhere)
        private const byte SubPlaceTrap = 6;          // id, x, y
        private const byte SubTriggerTrap = 7;        // playerId, id
        private const byte SubSelfLimp = 8;           // on (0/1)
        // Subtypes 9+ (defuse) are added with their features.

        // TOR's UncheckedMurderPlayer RPC byte, resolved from the internal CustomRPC enum (fallback 108).
        internal static byte uncheckedMurderRpc = 108;

        // ---- Role identity (own name/color over the real Impostor role) ----
        private static RoleInfo saboteurInfo;
        private static RoleInfo SaboteurInfo() => saboteurInfo ??= new RoleInfo(
            "Saboteur", Color,
            "Sabotage a task or lay a trap",
            "Sabotage a task or lay a trap",
            RoleId.Impostor);

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1410, Types.Impostor, "Saboteur",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1411, Types.Impostor, "Saboteur Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                MinAliveForKill = CustomOption.Create(1412, Types.Impostor, "Minimum Alive Players For Sabotage Kill",
                    4f, 2f, 10f, 1f, SpawnRate);
                TokensPerRound = CustomOption.Create(1413, Types.Impostor, "Saboteur Tokens Per Round",
                    1f, 1f, 5f, 1f, SpawnRate);
                SabotageTokenCost = CustomOption.Create(1414, Types.Impostor, "Sabotage-Task Token Cost",
                    1f, 1f, 5f, 1f, SpawnRate);
                TrapTokenCost = CustomOption.Create(1415, Types.Impostor, "Trap Token Cost",
                    1f, 1f, 5f, 1f, SpawnRate);
                KillCooldownPenalty = CustomOption.Create(1416, Types.Impostor, "Extra Kill Cooldown After Sabotage Kill",
                    10f, 0f, 60f, 2.5f, SpawnRate);
                MaxActiveTraps = CustomOption.Create(1417, Types.Impostor, "Saboteur Max Active Traps",
                    1f, 1f, 5f, 1f, SpawnRate);
                TrapStunDuration = CustomOption.Create(1418, Types.Impostor, "Saboteur Trap Stun Duration",
                    5f, 1f, 15f, 1f, SpawnRate);
                TrapsHitImpostors = CustomOption.Create(1419, Types.Impostor, "Traps Also Affect Other Impostors",
                    false, SpawnRate);
                ImpostorsSeeTraps = CustomOption.Create(1420, Types.Impostor, "Other Impostors Can See Traps",
                    false, SpawnRate);
                TrappedLimp = CustomOption.Create(1421, Types.Impostor, "Trapped Players Limp After Stun",
                    false, SpawnRate);
                SelfLimp = CustomOption.Create(1422, Types.Impostor, "Saboteur Can Self-Limp",
                    false, TrappedLimp);
                LimpSpeedMultiplier = CustomOption.Create(1423, Types.Impostor, "Saboteur Limp Speed Multiplier",
                    0.5f, 0.25f, 0.9f, 0.05f, TrappedLimp);
                // CustomOption.Create accumulates floats with `+= step`, which drifts (0.7000000001).
                // Round each 0.05 entry so the menu + getFloat() stay clean (same fix as TrapperLimp).
                if (LimpSpeedMultiplier.selections != null)
                    for (int i = 0; i < LimpSpeedMultiplier.selections.Length; i++)
                        LimpSpeedMultiplier.selections[i] =
                            Mathf.Round((float)LimpSpeedMultiplier.selections[i] * 100f) / 100f;
                LimpDuration = CustomOption.Create(1424, Types.Impostor, "Saboteur Limp Duration After Stun",
                    5f, 1f, 20f, 1f, TrappedLimp);
                CrewCanSearch = CustomOption.Create(1425, Types.Impostor, "Crew Can Search Tasks",
                    true, SpawnRate);
                CrewCanDefuse = CustomOption.Create(1426, Types.Impostor, "Crew Can Defuse Sabotage",
                    true, CrewCanSearch);
                MinAliveForTraps = CustomOption.Create(1427, Types.Impostor, "Minimum Alive Players For Traps",
                    3f, 2f, 10f, 1f, SpawnRate);

                UnknownsCollectionPlugin.Logger?.LogInfo("[Saboteur] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] CreateOptions failed: {e}");
            }
        }

        // ====================================================================
        // Reflection setup: resolve the UncheckedMurderPlayer RPC byte.
        // (Everything else is attribute-based and picked up by PatchAll.)
        // ====================================================================
        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;
                try {
                    var rpcEnum = torAsm.GetType("TheOtherRoles.CustomRPC");
                    if (rpcEnum != null)
                        uncheckedMurderRpc = (byte)(int)Enum.Parse(rpcEnum, "UncheckedMurderPlayer");
                } catch (Exception ex) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        $"[Saboteur] Could not resolve UncheckedMurderPlayer RPC id, using {uncheckedMurderRpc}: {ex.Message}");
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] TryPatch failed: {e}");
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================
        internal static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;

        internal static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        internal static int AliveCount() {
            int n = 0;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
                if (IsAlive(p)) n++;
            return n;
        }

        private static int LobbyPlayerCount() {
            int n = 0;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
                if (p != null && p.Data != null && !p.Data.Disconnected) n++;
            return n;
        }

        internal static bool IsLocalSaboteur() =>
            saboteur != null && PlayerControl.LocalPlayer != null
            && saboteur.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        // A plain TOR Impostor (no special impostor role like Morphling/Bomber/...): its first RoleInfo
        // is exactly the Impostor entry. Those are the only eligible Saboteur candidates. The Tesla (if
        // it spawned first this game) is excluded so the two roles never land on the same player.
        private static bool IsPlainImpostor(PlayerControl p) {
            if (!IsAlive(p) || p.Data.Role == null || !p.Data.Role.IsImpostor) return false;
            if (Tesla.tesla != null && p.PlayerId == Tesla.tesla.PlayerId) return false;
            var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
            return info != null && info.roleId == RoleId.Impostor;
        }

        // ====================================================================
        // Custom RPC senders (each also applies locally; the sender never receives its own RPC)
        // ====================================================================
        internal static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetSaboteur(byte saboteurPlayerId) {
            try {
                var w = BeginRpc(SubSetSaboteur);
                w.Write(saboteurPlayerId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetSaboteur(saboteurPlayerId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] SendSetSaboteur failed: {e}"); }
        }

        public static void SendClear() {
            try {
                var w = BeginRpc(SubClear);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyClear();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] SendClear failed: {e}"); }
        }

        // ---- Appliers (run on every client) ----
        private static void ApplySetSaboteur(byte saboteurPlayerId) {
            saboteur = Helpers.playerById(saboteurPlayerId);
            active = saboteur != null;
            RefillTokens();
            killUsedThisRound = false;
            if (active)
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Saboteur] The Saboteur is {saboteur.Data?.PlayerName}.");
        }

        private static void ApplyClear() {
            // Round/meeting reset of the abilities (tokens refilled, kill re-armed). The picked saboteur
            // identity stays; full game reset happens in resetVariables.
            RefillTokens();
            killUsedThisRound = false;
        }

        private static void RefillTokens() {
            tokens = TokensPerRound != null ? Mathf.RoundToInt(TokensPerRound.getFloat()) : 1;
        }

        // Drafted as Saboteur in Role-Draft mode (see UCRoleDraft). setRole runs on every client, so
        // marking locally here is consistent everywhere - no extra role RPC needed.
        public static void MarkFromDraft(byte playerId) => ApplySetSaboteur(playerId);

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
                        case SubSetSaboteur: ApplySetSaboteur(reader.ReadByte()); break;
                        case SubClear: ApplyClear(); break;
                        case SubSetSabotagedConsole: {
                            float x = reader.ReadSingle();
                            float y = reader.ReadSingle();
                            ApplySetSabotagedConsole(x, y);
                            break;
                        }
                        case SubClearSabotage: ApplyClearSabotage(); break;
                        case SubRequestKill: {
                            byte victimId = reader.ReadByte();
                            float x = reader.ReadSingle();
                            float y = reader.ReadSingle();
                            HostHandleRequestKill(victimId, x, y); // no-op unless we are the host
                            break;
                        }
                        case SubKillFx: ApplyKillFx(reader.ReadByte()); break;
                        case SubPlaceTrap: {
                            int id = reader.ReadInt32();
                            float x = reader.ReadSingle();
                            float y = reader.ReadSingle();
                            SaboteurTrap.Place(id, x, y);
                            break;
                        }
                        case SubTriggerTrap: {
                            byte playerId = reader.ReadByte();
                            int id = reader.ReadInt32();
                            SaboteurTrap.Trigger(playerId, id);
                            break;
                        }
                        case SubSelfLimp: SaboteurTrap.SetSelfLimping(reader.ReadByte() != 0); break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        // ====================================================================
        // Round reset (full game-state reset)
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                saboteur = null;
                active = false;
                tokens = 0;
                killUsedThisRound = false;
                sabotagedActive = false;
                progressInit = false;
                lastProgress = 0;
                consoleCache = null;
                SaboteurTrap.Clear();
                SaboteurScanUI.Close();
            }
        }

        // ====================================================================
        // Game start: host picks the Saboteur among plain Impostors and broadcasts it.
        // Runs at LOW priority so the Tesla pick (normal priority) resolves first; we exclude it.
        // ====================================================================
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (UCRoleDraft.DraftWillRun()) return;                                   // draft assigns instead
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;          // role disabled
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;                      // mod gate
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;      // spawn gate

                    int chance = SpawnRate.getSelection() * 10; // rates: 0..10 -> 0..100 %
                    if (rnd.Next(1, 101) > chance) return;                                     // spawn roll

                    var candidates = PlayerControl.AllPlayerControls.ToArray()
                        .Where(IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;

                    var pick = candidates[rnd.Next(candidates.Count)];
                    SendSetSaboteur(pick.PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Meeting: refill tokens + re-arm the kill (the ONLY thing that refills them). Trap/sabotage
        // cleanup is added by their features (they hook the same MeetingHud.Start).
        // ====================================================================
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                if (!active) return;
                RefillTokens();
                killUsedThisRound = false;
                // Unused sabotage + traps are cleared every meeting.
                ApplyClearSabotage();
                SaboteurTrap.Clear();
                progressInit = false;
            }
        }

        // ====================================================================
        // Sabotage-task: senders + appliers
        // ====================================================================
        public static void SendSetSabotagedConsole(float x, float y) {
            try {
                var w = BeginRpc(SubSetSabotagedConsole);
                w.Write(x);
                w.Write(y);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetSabotagedConsole(x, y);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] SendSetSabotagedConsole failed: {e}"); }
        }

        public static void SendClearSabotage() {
            try {
                var w = BeginRpc(SubClearSabotage);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyClearSabotage();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] SendClearSabotage failed: {e}"); }
        }

        // Victim -> host: "I just finished the sabotaged console." Host validates and arbitrates.
        public static void SendRequestKill(byte victimId, float x, float y) {
            try {
                var w = BeginRpc(SubRequestKill);
                w.Write(victimId);
                w.Write(x);
                w.Write(y);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                HostHandleRequestKill(victimId, x, y); // host==sender path; no-op for non-host
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] SendRequestKill failed: {e}"); }
        }

        // Host -> everyone: play the electric death FX for the victim (the lethal murder follows).
        public static void SendKillFx(byte victimId) {
            try {
                var w = BeginRpc(SubKillFx);
                w.Write(victimId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyKillFx(victimId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] SendKillFx failed: {e}"); }
        }

        // ---- Trap senders (each applies locally too) ----
        public static void SendPlaceTrap(int id, float x, float y) {
            try {
                var w = BeginRpc(SubPlaceTrap);
                w.Write(id);
                w.Write(x);
                w.Write(y);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                SaboteurTrap.Place(id, x, y);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] SendPlaceTrap failed: {e}"); }
        }

        public static void SendTriggerTrap(byte playerId, int id) {
            try {
                var w = BeginRpc(SubTriggerTrap);
                w.Write(playerId);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                SaboteurTrap.Trigger(playerId, id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] SendTriggerTrap failed: {e}"); }
        }

        public static void SendSelfLimp(bool on) {
            try {
                var w = BeginRpc(SubSelfLimp);
                w.Write(on ? (byte)1 : (byte)0);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                SaboteurTrap.SetSelfLimping(on);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] SendSelfLimp failed: {e}"); }
        }

        private static void ApplySetSabotagedConsole(float x, float y) {
            sabotagedActive = true;
            sabotagedX = x;
            sabotagedY = y;
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Saboteur] console sabotaged at ({x:F2}, {y:F2}).");
        }

        private static void ApplyClearSabotage() {
            sabotagedActive = false;
        }

        private static void ApplyKillFx(byte victimId) {
            var victim = Helpers.playerById(victimId);
            SaboteurKillFx.Play(victim);
            // The Saboteur pays a kill-cooldown penalty for the sabotage kill.
            if (IsLocalSaboteur() && saboteur != null) {
                try {
                    float penalty = KillCooldownPenalty != null ? KillCooldownPenalty.getFloat() : 0f;
                    if (penalty > 0f)
                        saboteur.SetKillTimer(Mathf.Max(saboteur.killTimer, 0f) + penalty);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Saboteur] kill-cd penalty failed: {e.Message}");
                }
            }
        }

        // Host-authoritative: validate a completion request, then kill + clear (once per round).
        private static void HostHandleRequestKill(byte victimId, float x, float y) {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            if (!active || !sabotagedActive || killUsedThisRound) {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Saboteur] kill request rejected: active={active} sabotaged={sabotagedActive} killUsed={killUsedThisRound}");
                return;
            }
            if (AliveCount() < (MinAliveForKill?.getFloat() ?? 4f)) {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Saboteur] kill request rejected: alive {AliveCount()} < min {MinAliveForKill?.getFloat()}");
                return;
            }

            var victim = Helpers.playerById(victimId);
            if (!IsAlive(victim)) return;
            if (victim.Data.Role != null && victim.Data.Role.IsImpostor) return; // impostors don't trigger it
            // Sanity: the reported position must match the stored console.
            if (Vector2.Distance(new Vector2(x, y), new Vector2(sabotagedX, sabotagedY)) > 1.0f) return;

            UnknownsCollectionPlugin.Logger?.LogInfo($"[Saboteur] kill request ACCEPTED for victim {victim.Data?.PlayerName}.");
            killUsedThisRound = true;
            byte killerId = IsAlive(saboteur) ? saboteur.PlayerId : victimId;
            SendKillFx(victimId);                 // FX first (everywhere)
            RpcUncheckedMurder(killerId, victimId);
            SendClearSabotage();                  // consume the sabotage
        }

        // Perform an unchecked murder on every client (local call + RPC), like the Tesla/Sheriff.
        private static void RpcUncheckedMurder(byte sourceId, byte targetId) {
            try {
                MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, uncheckedMurderRpc, SendOption.Reliable, -1);
                w.Write(sourceId);
                w.Write(targetId);
                w.Write(byte.MaxValue); // showAnimation
                AmongUsClient.Instance.FinishRpcImmediately(w);
                RPCProcedure.uncheckedMurderPlayer(sourceId, targetId, byte.MaxValue);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] RpcUncheckedMurder failed: {e}");
            }
        }

        // ====================================================================
        // Sabotage-task: console discovery (saboteur) + victim completion poll (everyone)
        // ====================================================================
        private static Console[] GetConsoles() {
            // Re-scan while empty: a cache built before ShipStatus spawned its consoles would otherwise
            // stay permanently empty and the SABOTAGE button would never find a console.
            if (consoleCache == null || consoleCache.Length == 0)
                consoleCache = UnityEngine.Object.FindObjectsOfType<Console>();
            return consoleCache;
        }

        // A console the Saboteur may mark - just exclude the sabotage-repair consoles (lights/comms/
        // reactor/oxygen). We do NOT require TaskTypes to be non-empty: some task consoles leave it
        // empty, and a non-task console simply never triggers the kill (harmless).
        private static bool IsSabotageableConsole(Console c) {
            if (c == null) return false;
            if (c.TaskTypes != null) {
                foreach (var tt in c.TaskTypes) {
                    if (tt == TaskTypes.FixLights || tt == TaskTypes.FixComms || tt == TaskTypes.RestoreOxy
                        || tt == TaskTypes.ResetReactor || tt == TaskTypes.ResetSeismic || tt == TaskTypes.StopCharles)
                        return false;
                }
            }
            return true;
        }

        private static Console FindUsableConsoleInRange() {
            var me = PlayerControl.LocalPlayer;
            if (me == null) return null;
            Vector2 here = me.GetTruePosition();
            Console best = null;
            float bestDist = float.MaxValue;
            foreach (var c in GetConsoles()) {
                if (!IsSabotageableConsole(c)) continue;
                float ud = c.UsableDistance > 0f ? c.UsableDistance : 0.8f;
                float d = Vector2.Distance(here, (Vector2)c.transform.position);
                if (d <= ud && d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }

        // Sum of the local player's task progress (per-step), used to detect "a step just completed".
        private static int LocalTaskProgress() {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.myTasks == null) return 0;
            int sum = 0;
            foreach (PlayerTask t in me.myTasks.GetFastEnumerator()) {
                if (t == null) continue;
                var npt = t.TryCast<NormalPlayerTask>();
                if (npt != null) sum += npt.taskStep;
                else if (t.IsComplete) sum += 1;
            }
            return sum;
        }

        // Runs every HUD frame on every client. When the LOCAL player completes a task step while
        // standing on the sabotaged console, request the kill from the host. Impostors never trigger it.
        private static void VictimPoll() {
            var me = PlayerControl.LocalPlayer;
            if (!active || !sabotagedActive || !IsAlive(me) || InMeeting()) { progressInit = false; return; }
            if (me.Data.Role != null && me.Data.Role.IsImpostor) { progressInit = false; return; }

            int prog = LocalTaskProgress();
            if (progressInit && prog > lastProgress) {
                float d = Vector2.Distance(me.GetTruePosition(), new Vector2(sabotagedX, sabotagedY));
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Saboteur] task step completed (prog {lastProgress}->{prog}); dist to sabotaged console = {d:F2} (need <=1.4)");
                if (d <= 1.4f) SendRequestKill(me.PlayerId, sabotagedX, sabotagedY);
            }
            lastProgress = prog;
            progressInit = true;
        }

        // Throttled diagnostic for the local Saboteur - reveals which gate blocks an ability.
        private static float lastDiag;
        private static void Diag() {
            try {
                if (!IsLocalSaboteur() || InMeeting()) return;
                if (Time.time - lastDiag < 2f) return;
                lastDiag = Time.time;
                var room = HudManager.Instance?.roomTracker?.LastRoom?.RoomId;
                var c = FindUsableConsoleInRange();
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Saboteur][diag] active={active} tokens={tokens} trapCost={TrapCost()} sabCost={(SabotageTokenCost != null ? Mathf.RoundToInt(SabotageTokenCost.getFloat()) : 1)} " +
                    $"traps={SaboteurTrap.ActiveCount}/{MaxTraps()} canPlace={SaboteurTrap.CanPlaceHere()} room={room} " +
                    $"consoleInRange={(c != null)} consoles={(consoleCache == null ? -1 : consoleCache.Length)} sabotagedActive={sabotagedActive}");
            } catch { }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class SabotageHudUpdatePatch {
            public static void Postfix() {
                try { VictimPoll(); SaboteurTrap.Update(); SaboteurScanUI.Update(); Diag(); }
                catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] HUD poll failed: {e}"); }
            }
        }

        // ====================================================================
        // Buttons (created once per HUD): the Saboteur's SABOTAGE button.
        // ====================================================================
        private static TheOtherRoles.Objects.CustomButton sabotageButton;
        private static TheOtherRoles.Objects.CustomButton trapButton;
        private static TheOtherRoles.Objects.CustomButton selfLimpButton;
        private static TheOtherRoles.Objects.CustomButton searchButton;

        // The Saboteur COULD be in this game (option enabled). Used to show the crew SEARCH button
        // without leaking whether a Saboteur actually spawned (Garlic/Vampire anti-leak pattern).
        private static bool CouldSpawn() => SpawnRate != null && SpawnRate.getSelection() > 0;
        private static bool LocalIsImpostor() {
            var me = PlayerControl.LocalPlayer;
            return me != null && me.Data != null && me.Data.Role != null && me.Data.Role.IsImpostor;
        }

        private static int TrapCost() => TrapTokenCost != null ? Mathf.RoundToInt(TrapTokenCost.getFloat()) : 1;
        private static int MaxTraps() => MaxActiveTraps != null ? Mathf.RoundToInt(MaxActiveTraps.getFloat()) : 1;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    Sprite sprite = __instance.KillButton != null && __instance.KillButton.graphic != null
                        ? __instance.KillButton.graphic.sprite : null;
                    Sprite trapSprite = Trapper.getButtonSprite();

                    sabotageButton = new TheOtherRoles.Objects.CustomButton(
                        () => { // OnClick
                            var c = FindUsableConsoleInRange();
                            if (c == null) return;
                            int cost = SabotageTokenCost != null ? Mathf.RoundToInt(SabotageTokenCost.getFloat()) : 1;
                            if (tokens < cost) return;
                            Vector3 p = c.transform.position;
                            tokens -= cost;
                            SendSetSabotagedConsole(p.x, p.y);
                            sabotageButton.Timer = sabotageButton.MaxTimer;
                        },
                        () => active && IsLocalSaboteur() && !sabotagedActive
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead
                              && tokens >= (SabotageTokenCost != null ? Mathf.RoundToInt(SabotageTokenCost.getFloat()) : 1),
                        () => PlayerControl.LocalPlayer.CanMove && FindUsableConsoleInRange() != null,
                        () => { },
                        sprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter,
                        __instance,
                        KeyCode.F,
                        false,
                        "SABOTAGE"
                    );
                    sabotageButton.MaxTimer = 0f;
                    sabotageButton.Timer = 0f;

                    trapButton = new TheOtherRoles.Objects.CustomButton(
                        () => { // OnClick: place a trap at the saboteur's feet
                            if (tokens < TrapCost() || SaboteurTrap.ActiveCount >= MaxTraps()) return;
                            if (!SaboteurTrap.CanPlaceHere()) return;
                            Vector2 p = PlayerControl.LocalPlayer.GetTruePosition();
                            int id = SaboteurTrap.nextId++;
                            tokens -= TrapCost();
                            SendPlaceTrap(id, p.x, p.y);
                            trapButton.Timer = trapButton.MaxTimer;
                        },
                        () => active && IsLocalSaboteur()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead
                              && tokens >= TrapCost() && SaboteurTrap.ActiveCount < MaxTraps(),
                        () => PlayerControl.LocalPlayer.CanMove && SaboteurTrap.CanPlaceHere(),
                        () => { },
                        trapSprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowLeft,
                        __instance,
                        KeyCode.C,
                        false,
                        "TRAP"
                    );
                    trapButton.MaxTimer = 0f;
                    trapButton.Timer = 0f;

                    selfLimpButton = new TheOtherRoles.Objects.CustomButton(
                        () => SendSelfLimp(!SaboteurTrap.SelfLimping),
                        () => active && IsLocalSaboteur() && SelfLimp != null && SelfLimp.getBool()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove,
                        () => { },
                        trapSprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowLeft,
                        __instance,
                        KeyCode.H,
                        false,
                        "LIMP"
                    );
                    selfLimpButton.MaxTimer = 0f;
                    selfLimpButton.Timer = -1f;

                    // Crew SEARCH button: visible to every non-Impostor whenever the role COULD spawn.
                    searchButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            var c = FindUsableConsoleInRange();
                            if (c == null || SaboteurScanUI.IsOpen) return;
                            SaboteurScanUI.Open(c.transform.position);
                            searchButton.Timer = searchButton.MaxTimer;
                        },
                        () => CouldSpawn() && (CrewCanSearch == null || CrewCanSearch.getBool())
                              && !LocalIsImpostor()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead
                              && !SaboteurScanUI.IsOpen,
                        () => PlayerControl.LocalPlayer.CanMove && FindUsableConsoleInRange() != null,
                        () => { },
                        sprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowRight,
                        __instance,
                        KeyCode.F,
                        false,
                        "SEARCH"
                    );
                    searchButton.MaxTimer = 0f;
                    searchButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] Button creation failed: {e}");
                }
            }
        }

        // ====================================================================
        // Role identity: show the Saboteur as its own role (name/color) over the Impostor entry, in
        // name tags, the role tab and the end-game summary. Mirrors the Tesla's RoleInfo postfix.
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || saboteur == null || p == null || p != saboteur || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) {
                            __result[i] = SaboteurInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, SaboteurInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
