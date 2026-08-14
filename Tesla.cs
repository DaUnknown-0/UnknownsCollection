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
        public static float countdown;             // remaining seconds before the pair dies (host-authoritative;
                                                     // only HostCountdown() below ever decrements this)
        private static bool dangerLocal;           // local cosmetic danger latch (warning onset)
        // Local-only mirror of the drain, recomputed identically on EVERY client (host included) from
        // the same trigger/grace gating HostCountdown() uses - NOT authoritative (only `countdown` on
        // the host kills), purely so the indicator pulse/beep can escalate for victims on non-host
        // clients too (the real `countdown` field above is never updated there).
        private static float countdownLocal;
        private static float nextPulseTime;         // next tesla_pulse Geiger-counter beep (local-only)
        private static float graceUntil;           // Time.time until which the countdown is frozen
        private static bool wasInMeeting;          // meeting-end edge detector (per client)
        // Everyone charged so far this game - excluded from future selections (no repeats).
        public static readonly System.Collections.Generic.HashSet<byte> chargedHistory = new();

        // ---- Custom RPC: module byte 190 inside the shared UC channel (UCRpc.CallId = 230) ----
        // The value is unchanged from the days when 190 was its own callId, so logs/docs still match.
        private const byte RpcId = 190; // == UnknownsCollectionPlugin.TeslaRpcId
        private const byte SubSetTesla = 0;   // teslaId
        private const byte SubSetCharges = 1; // plusId, minusId
        private const byte SubClear = 2;      // (none)
        private const byte SubKillFx = 3;     // plusVictimId, minusVictimId (play kill FX everywhere,
                                               // sent BEFORE the murder RPCs - see TriggerDeath)

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
            UCRpc.Register(RpcId, HandleModuleRpc);
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

        // Shared live-player gate: below this, charges are harmless everywhere (lethal logic AND
        // cosmetics), so both HostCountdown and LocalCosmetics must check it the same way.
        private static bool LiveGateOk() => AliveCount() >= (LiveMinPlayers?.getFloat() ?? 4f);

        private static int LobbyPlayerCount() {
            int n = 0;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
                if (p != null && p.Data != null && !p.Data.Disconnected) n++;
            return n;
        }

        // A plain TOR Impostor not already claimed by another UC role (shared rule, see UCPromotion).
        private static bool IsPlainImpostor(PlayerControl p) => UCPromotion.IsPlainImpostor(p);

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
            MessageWriter w = UCRpc.Begin(RpcId); // shared UC channel; RpcId is the module byte
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

        // Host -> everyone: play the double-electrocution kill FX at both victim positions (the lethal
        // murder RPCs follow right after - see TriggerDeath). Mirrors Saboteur's SendKillFx.
        public static void SendKillFx(byte plusVictimId, byte minusVictimId) {
            try {
                var w = BeginRpc(SubKillFx);
                w.Write(plusVictimId);
                w.Write(minusVictimId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyKillFx(plusVictimId, minusVictimId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] SendKillFx failed: {e}"); }
        }

        // ---- Appliers (run on every client) ----
        private static void ApplySetTesla(byte teslaPlayerId) {
            tesla = Helpers.playerById(teslaPlayerId);
            active = tesla != null;
            if (active) UCPromotion.Claim(teslaPlayerId, suppressFx: true); // bespoke promote flash+sound below
            plusId = minusId = byte.MaxValue;
            countdown = CountdownSeconds != null ? CountdownSeconds.getFloat() : 5f;
            countdownLocal = countdown;
            dangerLocal = false;
            nextPulseTime = 0f;
            // Round-start grace: everyone spawns together, so freeze the countdown briefly.
            graceUntil = Time.time + GraceSeconds();
            wasInMeeting = false;
            if (active) {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Tesla] The Tesla is {tesla.Data?.PlayerName}.");
                // Local-only reveal: the generic Impostor intro card already played by this point
                // (ApplySetTesla runs from IntroCutscene.OnDestroy / MarkFromDraft), so this is the
                // player's only dedicated "you are THE Tesla" signal. Exact pattern of
                // Poltergeist.ApplySetPoltergeist's own local-only rise flash+sound.
                if (tesla == PlayerControl.LocalPlayer) {
                    Helpers.showFlash(Color, 2.5f, UCLocalization.Tr("uc.ui.tesla.promote_flash"));
                    UCAssets.PlayTeslaPromote();
                }
            }
        }

        private static void ApplyKillFx(byte plusVictimId, byte minusVictimId) {
            try {
                // Arm the custom kill overlay before the murder RPCs land (this FX RPC is sent
                // first by the same sender, so it runs first on every client too).
                if (plusVictimId != byte.MaxValue) UCKillOverlay.ArmVictim(UCKillOverlay.Kind.Tesla, plusVictimId);
                if (minusVictimId != byte.MaxValue) UCKillOverlay.ArmVictim(UCKillOverlay.Kind.Tesla, minusVictimId);
                // byte.MaxValue marks a spared pole (no death there -> no burst there).
                var p = plusVictimId != byte.MaxValue ? Helpers.playerById(plusVictimId) : null;
                var m = minusVictimId != byte.MaxValue ? Helpers.playerById(minusVictimId) : null;
                if (p == null && m == null) return;
                // Anchored at the victims' own current positions (never the Tesla's) - each client
                // reads its own locally known position, same approach SaboteurKillFx.Play() uses.
                TeslaKillFx.Play(p != null ? p.GetTruePosition() : (Vector2?)null,
                                 m != null ? m.GetTruePosition() : (Vector2?)null);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] ApplyKillFx failed: {e}");
            }
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
            countdownLocal = countdown;
            dangerLocal = false;
            nextPulseTime = 0f;
            // Remember the charged players so they can't be charged again in a later round.
            if (newPlusId != byte.MaxValue) chargedHistory.Add(newPlusId);
            if (newMinusId != byte.MaxValue) chargedHistory.Add(newMinusId);
        }

        private static void ApplyClear() {
            plusId = minusId = byte.MaxValue;
            countdown = CountdownSeconds != null ? CountdownSeconds.getFloat() : 5f;
            countdownLocal = countdown;
            dangerLocal = false;
            nextPulseTime = 0f;
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
        // RPC receiver (registered on the shared UC channel in TryPatch; the module byte is already
        // consumed by UCRpc's dispatcher, so this starts at the subtype byte exactly as before).
        // ====================================================================
        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSetTesla: { byte id = reader.ReadByte();
                        // Host-authoritative role assignment (host pick in IntroCutscene.OnDestroy / UCRoleDraft) - a
                    // forged one would let any client declare any player this role (AUDIT H-3).
                        if (UCRpc.RequireHost("Tesla.SetTesla")) ApplySetTesla(id); break; }
                    case SubSetCharges: {
                        byte p = reader.ReadByte();
                        byte m = reader.ReadByte();
                        ApplySetCharges(p, m);
                        break;
                    }
                    case SubClear: ApplyClear(); break;
                    case SubKillFx: {
                        byte p = reader.ReadByte();
                        byte m = reader.ReadByte();
                        ApplyKillFx(p, m);
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] HandleRpc failed: {e}");
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
                countdownLocal = 0f;
                nextPulseTime = 0f;
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
        // Guess refund: a charged player shot by a Guesser DURING a meeting gives the charge back.
        // RPCProcedure.guesserShoot runs identically on every client (RPC procedure), so mutating the
        // local state here is consistent everywhere - no extra RPC needed. The dead player leaves
        // chargedHistory; if they were part of the pair confirmed THIS meeting, the whole pair is
        // refunded (surviving partner freed too, pair dropped) and the Tesla's meeting UI reopens so
        // a fresh pair can be picked in the same meeting.
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.guesserShoot))]
        static class GuesserShootRefundPatch {
            public static void Postfix([HarmonyArgument(1)] byte dyingTargetId) {
                try {
                    if (!active) return;
                    bool inPair = dyingTargetId != byte.MaxValue
                                  && (dyingTargetId == plusId || dyingTargetId == minusId);
                    if (!inPair && !chargedHistory.Contains(dyingTargetId)) return;

                    chargedHistory.Remove(dyingTargetId);
                    if (inPair) {
                        byte partner = dyingTargetId == plusId ? minusId : plusId;
                        if (partner != byte.MaxValue) chargedHistory.Remove(partner);
                        plusId = minusId = byte.MaxValue;
                        dangerLocal = false;
                        TeslaMeetingUI.ReopenForRefund();
                    }
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[Tesla] Charge refunded - charged player {dyingTargetId} was guessed (pair refund: {inPair}).");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] guess refund failed: {e}");
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
                countdownLocal = countdown;
                dangerLocal = false;
                nextPulseTime = 0f;
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
            if (!LiveGateOk()) return;

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

            // Kill FX fires at the victim positions BEFORE the murder RPCs (same ordering as
            // Saboteur's SubKillFx), so the electrocution burst is on screen right as the death lands.
            // A spared pole (self-charged Tesla with DiesIfSelfCharged off) is sent as byte.MaxValue -
            // an electrocution burst on a player who visibly survives would be a false public tell.
            SendKillFx(killPlus ? plus.PlayerId : byte.MaxValue, killMinus ? minus.PlayerId : byte.MaxValue);

            // Source = the victim themselves (self-kill pattern, same as the Maniac's blast): vanilla
            // MurderPlayer snaps the SOURCE onto the target, so using the Tesla as source teleported
            // them across the map to the electrocution - a hard identity reveal. Self-source also
            // keeps killer-attribution info (Detective/Medic reports) from pointing at the Tesla.
            if (killPlus) RpcUncheckedMurder(plusId, plusId);
            if (killMinus) RpcUncheckedMurder(minusId, minusId);

            SendClear();
        }

        // Local cosmetics: show the charge indicator on the charged local player, and a pulsing red
        // (no-number) danger warning when they are within trigger distance of their partner.
        private static void LocalCosmetics() {
            var me = PlayerControl.LocalPlayer;

            // The Tesla's own (non-authoritative) pair-status readout - independent of whether the
            // local player is itself charged, so it must run before the `charged` early-return below.
            TeslaSelfStatus(me);

            bool charged = active && me != null && IsAlive(me)
                           && (me.PlayerId == plusId || me.PlayerId == minusId)
                           && plusId != byte.MaxValue && minusId != byte.MaxValue;

            // Below the live-player gate, charges can't kill (see HostCountdown) - keep the cosmetics
            // off too, and hide an already-shown indicator the moment the alive count drops under it.
            if (!charged || InMeeting() || !LiveGateOk()) {
                TeslaIndicator.Hide();
                TeslaParticles.Hide();
                dangerLocal = false;
                return;
            }

            bool isPlus = me.PlayerId == plusId;
            byte partnerId = isPlus ? minusId : plusId;
            var partner = Helpers.playerById(partnerId);
            bool grace = InGrace();
            bool danger = false;
            if (IsAlive(partner) && !grace) {
                float dist = Vector2.Distance(me.GetTruePosition(), partner.GetTruePosition());
                float trigger = TriggerDistance != null ? TriggerDistance.getFloat() : 1.5f;
                danger = dist <= trigger;
            }

            float totalSec = CountdownSeconds != null ? CountdownSeconds.getFloat() : 5f;

            if (danger) {
                // Local-only mirror of the drain (see countdownLocal's declaration above) - same
                // gating HostCountdown() uses (only drains while actually in danger), just recomputed
                // per client instead of relying on the host-only `countdown` field.
                countdownLocal = Mathf.Max(0f, countdownLocal - Time.deltaTime);

                // Danger onset -> warning flash + sound (re-armed when leaving the danger zone).
                if (!dangerLocal) {
                    Helpers.showFlash(new Color(1f, 0.1f, 0.1f, 1f), 0.6f);
                    UCAssets.PlayTeslaWarning();
                    // Stagger the first beep slightly after the warning cue instead of firing both on
                    // the exact same frame (the check right below would otherwise fire immediately).
                    nextPulseTime = Time.time + 0.15f;
                }
                // Recurring, accelerating "heartbeat" beep instead of a one-shot flash - interval
                // shrinks as the (locally mirrored) countdown drains.
                if (Time.time >= nextPulseTime) {
                    UCAssets.PlayTeslaPulse();
                    float urgency = totalSec > 0f ? Mathf.Clamp01(1f - countdownLocal / totalSec) : 1f;
                    nextPulseTime = Time.time + Mathf.Lerp(0.9f, 0.18f, urgency);
                }
            }
            dangerLocal = danger;

            float frac = totalSec > 0f ? Mathf.Clamp01(countdownLocal / totalSec) : 1f;
            TeslaIndicator.Show(isPlus, danger, grace, frac);
            TeslaParticles.SetActive(me, danger, isPlus);
        }

        // Tesla-only, purely cosmetic and explicitly NON-authoritative: a rough "is my current pair
        // closing in?" readout, computed client-side from the already-synced plusId/minusId positions
        // (no new RPC needed - GetTruePosition() is readable for everyone). Only HostCountdown() above
        // actually decides life or death.
        private static void TeslaSelfStatus(PlayerControl me) {
            bool isTesla = active && me != null && tesla == me && IsAlive(me);
            if (!isTesla || InMeeting() || plusId == byte.MaxValue || minusId == byte.MaxValue) {
                TeslaIndicator.HideSelfStatus();
                return;
            }
            var plus = Helpers.playerById(plusId);
            var minus = Helpers.playerById(minusId);
            if (!IsAlive(plus) || !IsAlive(minus)) { TeslaIndicator.HideSelfStatus(); return; }

            float dist = Vector2.Distance(plus.GetTruePosition(), minus.GetTruePosition());
            float trigger = TriggerDistance != null ? TriggerDistance.getFloat() : 1.5f;
            TeslaIndicator.ShowSelfStatus(dist <= trigger);
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
