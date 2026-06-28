// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Tesla (Impostor)
 *
 * A normal TOR Impostor is silently promoted to "The Tesla" at game start (host-authoritative pick,
 * broadcast via RPC 190). The Tesla charges exactly TWO players during a meeting - one POSITIVE,
 * one NEGATIVE (max two charged people at once, never the same person twice). While that +/- pair
 * stays too close together a hidden countdown drains; separating PAUSES it (it does not refill), and
 * it only resets to full in a meeting. If it hits zero, both charged players die.
 *
 * ARCHITECTURE (mirrors the Revenger in "Useful TOR Stuff"): this is a brand-new role built WITHOUT
 * touching TOR source - own RoleInfo (display tag over the real Impostor role), a meeting selection UI
 * (Swapper-style per-row checkboxes), a small custom RPC, and host-authoritative lethal logic. The
 * charge indicator + danger warning shown to the victims are purely client-side cosmetics computed
 * locally (every charged client knows the pair ids, so it needs no sync). Because the Tesla UI and the
 * victim warnings are client-side, the role is GATED on "everyone has the mod" (TeslaVersionHandshake),
 * exactly like the Revenger/Snitch features; otherwise it simply does not spawn (host gets a warning).
 *
 * Options:
 *   - Spawn rate (impostor role chance) + minimum LOBBY players to spawn.
 *   - Trigger distance + countdown seconds.
 *   - Minimum ALIVE players for charges to be lethal (below it the charge does nothing; combined with
 *     the spawn gate this is the "min alive player count").
 *   - Tesla may charge itself; and whether a self-charge also kills the Tesla.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Tesla {
        // ---- Theme ----
        public static readonly Color Color = Palette.ImpostorRed; // impostor role -> red role tag (matches UCRoleDraft)

        // ---- Options ----
        public static CustomOption SpawnRate;          // 1400 (header) - impostor role chance
        public static CustomOption SpawnMinPlayers;    // 1401 - minimum LOBBY players to spawn
        public static CustomOption TriggerDistance;    // 1402 - "too close" distance (world units)
        public static CustomOption CountdownSeconds;   // 1403 - drain time while close
        public static CustomOption LiveMinPlayers;     // 1404 - min ALIVE players for charges to kill
        public static CustomOption CanChargeSelf;      // 1405 - Tesla may charge itself
        public static CustomOption DiesIfSelfCharged;  // 1406 - self-charge also kills the Tesla
        public static CustomOption GraceAfterMeeting;  // 1407 - grace seconds after meeting / round start

        // ---- Runtime state (reset each round) ----
        public static PlayerControl tesla;
        public static byte plusId = byte.MaxValue;
        public static byte minusId = byte.MaxValue;
        public static bool active;                 // role spawned & usable this game
        public static float countdown;             // remaining seconds before the pair dies
        private static bool dangerLocal;           // local cosmetic danger latch (warning onset)
        private static float graceUntil;           // Time.time until which the countdown is frozen
        private static bool wasInMeeting;          // meeting-end edge detector (per client)
        // Everyone charged so far this game - excluded from future selections (no repeats).
        public static readonly System.Collections.Generic.HashSet<byte> chargedHistory = new();

        // ---- Custom RPC (190) subtypes ----
        private const byte RpcId = 190; // == UnknownsCollectionPlugin.TeslaRpcId
        private const byte SubSetTesla = 0;   // teslaId
        private const byte SubSetCharges = 1; // plusId, minusId
        private const byte SubClear = 2;      // (none)

        // TOR's UncheckedMurderPlayer RPC byte, resolved from the internal CustomRPC enum (fallback 108).
        private static byte uncheckedMurderRpc = 108;

        // ---- Role identity (own name/color over the real Impostor role) ----
        private static RoleInfo teslaInfo;
        public static RoleInfo TeslaInfo() => teslaInfo ??= new RoleInfo(
            "Tesla", Color,
            "Charge two players and bring them together",
            "Charge two players and bring them together",
            RoleId.Impostor);

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1400, Types.Impostor, "Tesla",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1401, Types.Impostor, "Tesla Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                TriggerDistance = CustomOption.Create(1402, Types.Impostor, "Tesla Charge Trigger Distance",
                    1.5f, 0.5f, 3f, 0.25f, SpawnRate);
                CountdownSeconds = CustomOption.Create(1403, Types.Impostor, "Tesla Charge Countdown (sec)",
                    5f, 1f, 15f, 0.5f, SpawnRate);
                LiveMinPlayers = CustomOption.Create(1404, Types.Impostor, "Tesla Minimum Alive Players For Charges",
                    4f, 2f, 10f, 1f, SpawnRate);
                CanChargeSelf = CustomOption.Create(1405, Types.Impostor, "Tesla Can Charge Itself",
                    false, SpawnRate);
                DiesIfSelfCharged = CustomOption.Create(1406, Types.Impostor, "Self-Charge Also Kills The Tesla",
                    true, CanChargeSelf);
                GraceAfterMeeting = CustomOption.Create(1407, Types.Impostor, "Tesla Grace Seconds After Meeting",
                    5f, 0f, 30f, 1f, SpawnRate);

                UnknownsCollectionPlugin.Logger?.LogInfo("[Tesla] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] CreateOptions failed: {e}");
            }
        }

        // ====================================================================
        // Reflection setup: resolve the UncheckedMurderPlayer RPC byte + patch resetVariables.
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
                        $"[Tesla] Could not resolve UncheckedMurderPlayer RPC id, using {uncheckedMurderRpc}: {ex.Message}");
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] TryPatch failed: {e}");
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        private static int AliveCount() {
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

        // A plain TOR Impostor (no special impostor role like Morphling/Bomber/...): its first
        // RoleInfo is exactly the Impostor entry. Those are the only eligible Tesla candidates.
        private static bool IsPlainImpostor(PlayerControl p) {
            if (!IsAlive(p) || p.Data.Role == null || !p.Data.Role.IsImpostor) return false;
            var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
            return info != null && info.roleId == RoleId.Impostor;
        }

        private static void PostChat(PlayerControl source, string text) {
            try {
                var hud = HudManager.Instance;
                if (hud != null && hud.Chat != null && source != null)
                    hud.Chat.AddChat(source, text);
            } catch { }
        }

        // ====================================================================
        // Custom RPC senders (each also applies locally; the sender never receives its own RPC)
        // ====================================================================
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetTesla(byte teslaPlayerId) {
            try {
                var w = BeginRpc(SubSetTesla);
                w.Write(teslaPlayerId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetTesla(teslaPlayerId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] SendSetTesla failed: {e}"); }
        }

        public static void SendSetCharges(byte newPlusId, byte newMinusId) {
            try {
                var w = BeginRpc(SubSetCharges);
                w.Write(newPlusId);
                w.Write(newMinusId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetCharges(newPlusId, newMinusId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] SendSetCharges failed: {e}"); }
        }

        public static void SendClear() {
            try {
                var w = BeginRpc(SubClear);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyClear();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] SendClear failed: {e}"); }
        }

        // ---- Appliers (run on every client) ----
        private static void ApplySetTesla(byte teslaPlayerId) {
            tesla = Helpers.playerById(teslaPlayerId);
            active = tesla != null;
            plusId = minusId = byte.MaxValue;
            countdown = CountdownSeconds != null ? CountdownSeconds.getFloat() : 5f;
            dangerLocal = false;
            // Round-start grace: everyone spawns together, so freeze the countdown briefly.
            graceUntil = Time.time + GraceSeconds();
            wasInMeeting = false;
            if (active)
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Tesla] The Tesla is {tesla.Data?.PlayerName}.");
        }

        private static float GraceSeconds() => GraceAfterMeeting != null ? GraceAfterMeeting.getFloat() : 0f;
        private static bool InGrace() => Time.time < graceUntil;

        // Drafted as Tesla in Role-Draft mode (see UCRoleDraft). setRole runs on every client, so
        // marking locally here is consistent everywhere - no extra role RPC needed.
        public static void MarkFromDraft(byte playerId) => ApplySetTesla(playerId);

        private static void ApplySetCharges(byte newPlusId, byte newMinusId) {
            plusId = newPlusId;
            minusId = newMinusId;
            countdown = CountdownSeconds != null ? CountdownSeconds.getFloat() : 5f;
            dangerLocal = false;
            // Remember the charged players so they can't be charged again in a later round.
            if (newPlusId != byte.MaxValue) chargedHistory.Add(newPlusId);
            if (newMinusId != byte.MaxValue) chargedHistory.Add(newMinusId);
        }

        private static void ApplyClear() {
            plusId = minusId = byte.MaxValue;
            countdown = CountdownSeconds != null ? CountdownSeconds.getFloat() : 5f;
            dangerLocal = false;
        }

        // Perform an unchecked murder on every client (local call + RPC), like the Sheriff/Revenger.
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
                UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] RpcUncheckedMurder failed: {e}");
            }
        }

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
                        case SubSetTesla: ApplySetTesla(reader.ReadByte()); break;
                        case SubSetCharges: {
                            byte p = reader.ReadByte();
                            byte m = reader.ReadByte();
                            ApplySetCharges(p, m);
                            break;
                        }
                        case SubClear: ApplyClear(); break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] HandleRpc failed: {e}");
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
                tesla = null;
                plusId = minusId = byte.MaxValue;
                active = false;
                countdown = 0f;
                dangerLocal = false;
                graceUntil = 0f;
                wasInMeeting = false;
                chargedHistory.Clear();
                TeslaMeetingUI.Reset();
            }
        }

        // Also clear the charged-history at game end (belt-and-suspenders; resetVariables already clears
        // it at the next game's start).
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch {
            public static void Postfix() { chargedHistory.Clear(); }
        }

        // ====================================================================
        // Game start: host picks the Tesla among plain Impostors and broadcasts it.
        // ====================================================================
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (UCRoleDraft.DraftWillRun()) return;                                   // draft assigns instead
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;          // role disabled
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;                      // client-side gate
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;      // spawn gate

                    int chance = SpawnRate.getSelection() * 10; // rates: 0..10 -> 0..100 %
                    if (rnd.Next(1, 101) > chance) return;                                     // spawn roll

                    var candidates = PlayerControl.AllPlayerControls.ToArray()
                        .Where(IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;

                    var pick = candidates[rnd.Next(candidates.Count)];
                    SendSetTesla(pick.PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Meeting: reset the countdown to full (the ONLY thing that refills it).
        // ====================================================================
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                countdown = CountdownSeconds != null ? CountdownSeconds.getFloat() : 5f;
                dangerLocal = false;
                // Charges are per-round: clear the previous round's pair at every meeting. The Tesla
                // re-charges a NEW pair during the meeting (already-charged players are excluded).
                plusId = minusId = byte.MaxValue;
            }
        }

        // ====================================================================
        // Host countdown + victim cosmetics (charge indicator + danger warning), per HUD frame.
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    // Meeting-end edge (runs on every client): (re)start the grace window so the countdown
                    // doesn't drain while everyone is still bunched up at the spawn point.
                    bool nowMeeting = InMeeting();
                    if (wasInMeeting && !nowMeeting) graceUntil = Time.time + GraceSeconds();
                    wasInMeeting = nowMeeting;

                    HostCountdown();
                    LocalCosmetics();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] HudUpdate failed: {e}");
                }
            }
        }

        // Host-authoritative: drain the countdown while the +/- pair is too close; kill at zero.
        private static void HostCountdown() {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            if (!active || plusId == byte.MaxValue || minusId == byte.MaxValue) return;
            if (InMeeting()) return;

            var plus = Helpers.playerById(plusId);
            var minus = Helpers.playerById(minusId);

            // A charged player who died/left ends the threat - clear the pair.
            if (!IsAlive(plus) || !IsAlive(minus)) { SendClear(); return; }

            // Live gate: below the minimum, charges are harmless (countdown frozen).
            if (AliveCount() < (LiveMinPlayers?.getFloat() ?? 4f)) return;

            // Grace window after a meeting / round start: don't drain while players are still bunched up.
            if (InGrace()) return;

            float dist = Vector2.Distance(plus.GetTruePosition(), minus.GetTruePosition());
            float trigger = TriggerDistance != null ? TriggerDistance.getFloat() : 1.5f;
            if (dist > trigger) return; // separated -> pause (no refill)

            countdown -= Time.deltaTime;
            if (countdown > 0f) return;

            TriggerDeath(plus, minus);
        }

        private static void TriggerDeath(PlayerControl plus, PlayerControl minus) {
            byte teslaId = tesla != null ? tesla.PlayerId : byte.MaxValue;
            bool teslaDies = DiesIfSelfCharged == null || DiesIfSelfCharged.getBool();

            bool killPlus = !(plusId == teslaId && !teslaDies);
            bool killMinus = !(minusId == teslaId && !teslaDies);

            byte killerId = IsAlive(tesla) ? tesla.PlayerId : byte.MaxValue;
            if (killPlus) RpcUncheckedMurder(killerId == byte.MaxValue ? plusId : killerId, plusId);
            if (killMinus) RpcUncheckedMurder(killerId == byte.MaxValue ? minusId : killerId, minusId);

            SendClear();
        }

        // Local cosmetics: show the charge indicator on the charged local player, and a pulsing red
        // (no-number) danger warning when they are within trigger distance of their partner.
        private static void LocalCosmetics() {
            var me = PlayerControl.LocalPlayer;
            bool charged = active && me != null && IsAlive(me)
                           && (me.PlayerId == plusId || me.PlayerId == minusId)
                           && plusId != byte.MaxValue && minusId != byte.MaxValue;

            if (!charged || InMeeting()) {
                TeslaIndicator.Hide();
                TeslaParticles.Hide();
                dangerLocal = false;
                return;
            }

            byte partnerId = me.PlayerId == plusId ? minusId : plusId;
            var partner = Helpers.playerById(partnerId);
            bool danger = false;
            if (IsAlive(partner) && !InGrace()) {
                float dist = Vector2.Distance(me.GetTruePosition(), partner.GetTruePosition());
                float trigger = TriggerDistance != null ? TriggerDistance.getFloat() : 1.5f;
                danger = dist <= trigger;
            }

            // Danger onset -> warning flash + sound (re-armed when leaving the danger zone).
            if (danger && !dangerLocal) {
                Helpers.showFlash(new Color(1f, 0.1f, 0.1f, 1f), 0.6f);
                TeslaSound.PlayWarning();
            }
            dangerLocal = danger;

            TeslaIndicator.Show(danger);
            TeslaParticles.SetActive(me, danger);
        }

        // ====================================================================
        // Role identity: show the Tesla as its own role (name/color) over the Impostor entry, in
        // name tags, the role tab and the end-game summary. Mirrors the Revenger's RoleInfo postfix.
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || tesla == null || p == null || p != tesla || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) {
                            __result[i] = TeslaInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, TeslaInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
