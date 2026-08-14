// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Gambler (Crew MODIFIER)
 *
 * He predicts what the round will do: who gets voted out, whether anyone dies in the next X seconds,
 * whether a target really finishes tasks. Winning pays, losing hurts, and the stake grows with the
 * tier of the bet.
 *
 * THE RULE THE WHOLE DESIGN HANGS ON: EVERY bet is settled INSIDE A MEETING, never during the round.
 * Resolving a bet means telling the bettor an outcome, and an outcome learned at 34 seconds into the
 * round is information nobody else has. Settled at the meeting, the answer arrives at the moment
 * everybody is talking anyway, so the modifier produces conversation instead of a private sensor.
 * Two settlement points exist:
 *   AtMeetingStart - round facts (kills, tasks, sabotage, who died)
 *   AtVoteEnd      - vote facts (who was ejected, vote counts, ties)
 *
 * WHY THE TASK BET NEEDS A HIGH THRESHOLD
 * A bet on "does X finish A task" would be a perfect impostor detector: fake tasks never send the
 * real task RPC (the Auditor relies on exactly that, Auditor.cs:533), so losing that bet would mean
 * "X is an impostor". At a threshold of 4 the signal inverts and weakens: WINNING proves the target
 * is crew, losing says almost nothing because a slow crewmate misses it too. Confirming a crewmate
 * is far less powerful than exposing an impostor, and it costs a whole round for a single name.
 *
 * WHY HIS OWN VOTE IS EXCLUDED
 * For every bet that COUNTS VOTES the Gambler's own vote is skipped, otherwise he could simply vote
 * his own bet home. Bets on the RESULT of the vote (who was ejected, tie, was it the reporter) use
 * the real result - there his single vote is one among many, and re-deriving the result without him
 * would answer a different question than the one the lobby actually decided.
 *
 * Options 1610-1624, module byte 215 on the shared UC channel. No draft entry: this is a modifier,
 * it rides along with whatever crew role the player already has. See ID-Registry.md.
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

    // The 14 bets. The byte value travels over the wire, so entries must never be renumbered.
    public enum BetKind : byte {
        SomeoneEjected      = 0,   // tier 1  - is anyone voted out at all (vs. skip)
        KillInWindow        = 1,   // tier 2  - does a kill happen within X seconds
        NoKillThisRound     = 2,   // tier 2  - does the round stay clean
        TargetGetsNoVote    = 3,   // tier 2  - does X receive zero votes
        TargetGetsNVotes    = 4,   // tier 3  - does X receive at least N votes
        TieVote             = 5,   // tier 3  - does the vote end in a tie
        DeadlySabotage      = 6,   // tier 3  - is reactor/oxygen called this round
        TargetDoesNTasks    = 7,   // tier 4  - does X finish at least N tasks
        TargetSurvives      = 8,   // tier 4  - is X still alive at the meeting
        ReporterEjected     = 9,   // tier 4  - is the body reporter voted out
        TargetEjected       = 10,  // tier 5  - is exactly X voted out
        UnanimousVote       = 11,  // tier 5  - do all eligible voters agree
        MoreThanTwoKills    = 12,  // tier 5  - three or more kills this round
        WhoDiesNext         = 13   // tier 6  - X is the next player to die
    }

    public enum BetSettle : byte { AtMeetingStart = 0, AtVoteEnd = 1 }

    public sealed class BetDef {
        public BetKind Kind;
        public byte Tier;          // 1..6, drives the payout
        public bool NeedsTarget;
        public BetSettle Settle;
        public string Key;         // localization key stem: uc.gambler.bet.<key>
        public BetDef(BetKind k, byte tier, bool target, BetSettle settle, string key) {
            Kind = k; Tier = tier; NeedsTarget = target; Settle = settle; Key = key;
        }
    }

    public static class Gambler {
        // ---- Theme ----
        // Crew modifier -> not a faction colour. Casino green, close enough to TOR's modifier look
        // to read as "extra trait" rather than "role".
        public static readonly Color Color = new Color(0.35f, 0.85f, 0.45f);

        // ---- Options (IDs 1610-1624) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption BetCooldown;
        public static CustomOption MaxActiveBets;
        public static CustomOption KillWindow;
        public static CustomOption TaskThreshold;
        public static CustomOption VoteThreshold;
        public static CustomOption SpeedDelta;
        public static CustomOption EffectDuration;
        public static CustomOption CooldownDelta;
        public static CustomOption AnnounceToImpostors;

        // ---- The catalogue ----
        private static readonly BetDef[] defs = {
            new BetDef(BetKind.SomeoneEjected,   1, false, BetSettle.AtVoteEnd,      "someone_ejected"),
            new BetDef(BetKind.KillInWindow,     2, false, BetSettle.AtMeetingStart, "kill_in_window"),
            new BetDef(BetKind.NoKillThisRound,  2, false, BetSettle.AtMeetingStart, "no_kill"),
            new BetDef(BetKind.TargetGetsNoVote, 2, true,  BetSettle.AtVoteEnd,      "no_vote"),
            new BetDef(BetKind.TargetGetsNVotes, 3, true,  BetSettle.AtVoteEnd,      "n_votes"),
            new BetDef(BetKind.TieVote,          3, false, BetSettle.AtVoteEnd,      "tie"),
            new BetDef(BetKind.DeadlySabotage,   3, false, BetSettle.AtMeetingStart, "deadly_sabotage"),
            new BetDef(BetKind.TargetDoesNTasks, 4, true,  BetSettle.AtMeetingStart, "n_tasks"),
            new BetDef(BetKind.TargetSurvives,   4, true,  BetSettle.AtMeetingStart, "survives"),
            new BetDef(BetKind.ReporterEjected,  4, false, BetSettle.AtVoteEnd,      "reporter_ejected"),
            new BetDef(BetKind.TargetEjected,    5, true,  BetSettle.AtVoteEnd,      "target_ejected"),
            new BetDef(BetKind.UnanimousVote,    5, false, BetSettle.AtVoteEnd,      "unanimous"),
            new BetDef(BetKind.MoreThanTwoKills, 5, false, BetSettle.AtMeetingStart, "many_kills"),
            new BetDef(BetKind.WhoDiesNext,      6, true,  BetSettle.AtMeetingStart, "who_dies_next"),
        };

        public static IReadOnlyList<BetDef> Defs => defs;
        public static BetDef Def(BetKind k) {
            foreach (var d in defs) if (d.Kind == k) return d;
            return null;
        }

        // ---- Runtime state ----
        public static PlayerControl gambler;
        public static bool active;

        public sealed class Bet {
            public byte Id;
            public BetKind Kind;
            public byte Target;        // 255 = none
            public float Placed;       // Time.time when it was placed (window bets need it)
            public bool Settled;
            public bool Won;
            public bool Push;          // undecided: no payout, no penalty
        }

        private static readonly List<Bet> bets = new List<Bet>();
        public static IReadOnlyList<Bet> Bets => bets;

        // Round observations (host keeps them, they drive the settlement).
        private static readonly List<float> killTimes = new List<float>();     // Time.time of each kill this round
        private static readonly Dictionary<byte, int> tasksThisRound = new Dictionary<byte, int>();
        private static bool deadlySabotageThisRound;
        private static byte lastReporter = byte.MaxValue;                       // 255 = emergency button
        private static readonly List<byte> deathOrderThisRound = new List<byte>();
        private static bool sabotageWasActive;

        // Local: cooldown until the next bet may be placed.
        public static float betCooldownLeft;
        // Local: the last settlement result, for the HUD banner.
        public static string lastResultText;
        public static float lastResultUntil;

        // Host: pending effect that has to be lifted again (tuning is a permanent multiplier).
        private static readonly Dictionary<byte, float> tuningUntil = new Dictionary<byte, float>();
        private static byte nextBetId;

        // Host-only: task completions we ordered ourselves, so a penalty reset does not look like
        // fresh crew progress to the counting patch (same trick the Auditor uses).
        private static readonly Dictionary<byte, HashSet<uint>> pendingRecomplete =
            new Dictionary<byte, HashSet<uint>>();

        // ---- RPC: module byte 215 on UCRpc.CallId = 230 ----
        private const byte RpcId = UnknownsCollectionPlugin.GamblerRpcId;
        private const byte SubSetGambler = 0;  // playerId
        private const byte SubPlaceBet   = 1;  // betId, kind, target      (host -> everyone)
        private const byte SubRequestBet = 2;  // kind, target             (gambler -> host)
        private const byte SubSettle     = 3;  // betId, won, push         (host -> everyone)
        private const byte SubGrantTasks = 4;  // count                    (host -> everyone, gambler acts)
        private const byte SubRevertTask = 5;  // count, ids...            (host -> everyone, gambler re-completes)
        private const byte SubAnnounce   = 6;  // won(bool), seconds       (host -> everyone, impostors show it)

        private static readonly System.Random rnd = new System.Random();

        // ---- Identity ----
        // TOR has no generic "Modifier" RoleId (its enum ends at Shifter = 58) and every real modifier
        // id belongs to a TOR modifier with its own logic, so borrowing one would invite false
        // positives. 230 is a sentinel in the same spirit as UCRoleDraft's 200-218 draft ids, chosen
        // above that block so it can never be mistaken for a draft entry. The value is display-only.
        private const RoleId GamblerRoleId = (RoleId)230;
        private static RoleInfo gamblerInfo;
        public static RoleInfo GamblerInfo() => gamblerInfo ??= new RoleInfo(
            "Gambler", Color, "Bet on what the round will do",
            "Bet on the round", GamblerRoleId, false, true);

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1610, Types.Modifier, "Gambler",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1611, Types.Modifier, "Gambler Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                BetCooldown = CustomOption.Create(1612, Types.Modifier, "Gambler Bet Cooldown",
                    45f, 10f, 180f, 5f, SpawnRate);
                MaxActiveBets = CustomOption.Create(1613, Types.Modifier, "Gambler Open Bets At Once",
                    2f, 1f, 5f, 1f, SpawnRate);
                KillWindow = CustomOption.Create(1614, Types.Modifier, "Gambler Kill Bet Window",
                    30f, 10f, 120f, 5f, SpawnRate);
                TaskThreshold = CustomOption.Create(1615, Types.Modifier, "Gambler Task Bet Threshold",
                    4f, 2f, 10f, 1f, SpawnRate);
                VoteThreshold = CustomOption.Create(1616, Types.Modifier, "Gambler Vote Bet Threshold",
                    3f, 2f, 8f, 1f, SpawnRate);
                SpeedDelta = CustomOption.Create(1617, Types.Modifier, "Gambler Speed Change",
                    15f, 5f, 50f, 5f, SpawnRate);
                EffectDuration = CustomOption.Create(1618, Types.Modifier, "Gambler Speed Effect Duration",
                    30f, 10f, 120f, 5f, SpawnRate);
                CooldownDelta = CustomOption.Create(1619, Types.Modifier, "Gambler Kill Cooldown Change",
                    5f, 1f, 20f, 1f, SpawnRate);
                AnnounceToImpostors = CustomOption.Create(1620, Types.Modifier,
                    "Impostors Are Told About Cooldown Changes", true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Gambler] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            UCRpc.Register(RpcId, HandleModuleRpc);
        }

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        public static bool IsLocalGambler() =>
            active && gambler != null && PlayerControl.LocalPlayer != null
            && gambler.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);

        public static int OpenBetCount() {
            int n = 0;
            foreach (var b in bets) if (!b.Settled) n++;
            return n;
        }

        public static bool CanPlaceBet() =>
            IsLocalGambler() && IsAlive(gambler) && !InMeeting()
            && betCooldownLeft <= 0f
            && OpenBetCount() < Mathf.RoundToInt(MaxActiveBets?.getFloat() ?? 2f);

        // ---- send ----
        private static MessageWriter BeginRpc(byte subtype) {
            var w = UCRpc.Begin(RpcId);
            w.Write(subtype);
            return w;
        }

        public static void SendSetGambler(byte id) {
            try {
                var w = BeginRpc(SubSetGambler);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetGambler(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] SendSet failed: {e}"); }
        }

        // Gambler -> host: "I want this bet". The host owns the bet list so nobody can fabricate one.
        public static void RequestBet(BetKind kind, byte target) {
            try {
                var w = BeginRpc(SubRequestBet);
                w.Write((byte)kind); w.Write(target);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                if (AmHost()) HostPlaceBet(kind, target, PlayerControl.LocalPlayer);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] RequestBet failed: {e}"); }
        }

        private static void SendPlaceBet(byte betId, BetKind kind, byte target) {
            try {
                var w = BeginRpc(SubPlaceBet);
                w.Write(betId); w.Write((byte)kind); w.Write(target);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyPlaceBet(betId, kind, target);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] SendPlaceBet failed: {e}"); }
        }

        private static void SendSettle(byte betId, bool won, bool push) {
            try {
                var w = BeginRpc(SubSettle);
                w.Write(betId); w.Write(won); w.Write(push);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySettle(betId, won, push);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] SendSettle failed: {e}"); }
        }

        private static void SendGrantTasks(byte count) {
            try {
                var w = BeginRpc(SubGrantTasks);
                w.Write(count);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyGrantTasks(count);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] SendGrant failed: {e}"); }
        }

        private static void SendRevertTasks(List<uint> keepComplete) {
            try {
                var w = BeginRpc(SubRevertTask);
                w.Write((byte)Mathf.Min(keepComplete.Count, 255));
                for (int i = 0; i < keepComplete.Count && i < 255; i++) w.Write(keepComplete[i]);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyRevertTasks(keepComplete);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] SendRevert failed: {e}"); }
        }

        private static void SendAnnounce(bool won, float seconds) {
            try {
                var w = BeginRpc(SubAnnounce);
                w.Write(won); w.Write(seconds);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyAnnounce(won, seconds);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] SendAnnounce failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSetGambler: {
                        byte id = reader.ReadByte();
                        // Host-authoritative: a forged one would let any client hand out the modifier.
                        if (UCRpc.RequireHost("Gambler.SetGambler")) ApplySetGambler(id);
                        break;
                    }
                    case SubRequestBet: {
                        byte kind = reader.ReadByte();
                        byte target = reader.ReadByte();
                        if (AmHost()) HostPlaceBet((BetKind)kind, target, UCRpc.Sender);
                        break;
                    }
                    case SubPlaceBet: {
                        byte betId = reader.ReadByte();
                        byte kind = reader.ReadByte();
                        byte target = reader.ReadByte();
                        if (UCRpc.RequireHost("Gambler.PlaceBet")) ApplyPlaceBet(betId, (BetKind)kind, target);
                        break;
                    }
                    case SubSettle: {
                        byte betId = reader.ReadByte();
                        bool won = reader.ReadBoolean();
                        bool push = reader.ReadBoolean();
                        if (UCRpc.RequireHost("Gambler.Settle")) ApplySettle(betId, won, push);
                        break;
                    }
                    case SubGrantTasks: {
                        byte count = reader.ReadByte();
                        if (UCRpc.RequireHost("Gambler.GrantTasks")) ApplyGrantTasks(count);
                        break;
                    }
                    case SubRevertTask: {
                        int n = reader.ReadByte();
                        var ids = new List<uint>(n);
                        for (int i = 0; i < n; i++) ids.Add(reader.ReadUInt32());
                        if (UCRpc.RequireHost("Gambler.RevertTasks")) ApplyRevertTasks(ids);
                        break;
                    }
                    case SubAnnounce: {
                        bool won = reader.ReadBoolean();
                        float seconds = reader.ReadSingle();
                        if (UCRpc.RequireHost("Gambler.Announce")) ApplyAnnounce(won, seconds);
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] HandleRpc failed: {e}");
            }
        }

        // ---- apply (every client) ----
        private static void ApplySetGambler(byte id) {
            var p = Helpers.playerById(id);
            if (p == null) return;
            gambler = p;
            active = true;
            // The modifier rides on top of an existing role, so the generic promotion cue is the only
            // feedback - and only the player themselves may ever see it (info-leak rule).
            UCPromotion.Claim(id);
        }

        private static void ApplyPlaceBet(byte betId, BetKind kind, byte target) {
            if (FindBet(betId) != null) return;
            bets.Add(new Bet { Id = betId, Kind = kind, Target = target, Placed = Time.time });
            if (IsLocalGambler()) betCooldownLeft = BetCooldown?.getFloat() ?? 45f;
        }

        private static void ApplySettle(byte betId, bool won, bool push) {
            var b = FindBet(betId);
            if (b == null) return;
            b.Settled = true; b.Won = won; b.Push = push;
            if (!IsLocalGambler()) return;
            var def = Def(b.Kind);
            string label = def != null ? GamblerUI.BetLabel(def) : "?";
            lastResultText = push
                ? UCLocalization.Tr("uc.gambler.result_push", label)
                : won ? UCLocalization.Tr("uc.gambler.result_won", label)
                      : UCLocalization.Tr("uc.gambler.result_lost", label);
            lastResultUntil = Time.time + 8f;
        }

        // Reward: complete open tasks of the Gambler. RpcCompleteTask has to come from the OWNER, so
        // the work happens on his own client (the host only orders it).
        private static void ApplyGrantTasks(byte count) {
            try {
                if (!IsLocalGambler()) return;
                var me = PlayerControl.LocalPlayer;
                if (me == null || me.Data == null || me.Data.Tasks == null) return;
                int done = 0;
                foreach (var t in me.Data.Tasks) {
                    if (done >= count) break;
                    if (t == null || t.Complete) continue;
                    try { me.RpcCompleteTask(t.Id); done++; } catch { }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] GrantTasks failed: {e}");
            }
        }

        // Penalty: the host has already sent the vanilla RpcSetTasks that wipes the whole list; the
        // Gambler's own client now re-completes everything that must stay done. Net effect: exactly
        // the intended number of tasks is open again, and the SERVER agrees (Auditor.cs header).
        private static void ApplyRevertTasks(List<uint> keepComplete) {
            try {
                if (!IsLocalGambler()) return;
                var me = PlayerControl.LocalPlayer;
                if (me == null) return;
                foreach (uint id in keepComplete) {
                    try { me.RpcCompleteTask(id); } catch { }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] RevertTasks failed: {e}");
            }
        }

        // Announcement to the impostors. Deliberately anonymous: they learn that a Gambler exists and
        // what it did to their cooldown, never who it is.
        private static void ApplyAnnounce(bool won, float seconds) {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null || me.Data == null || me.Data.Role == null || !me.Data.Role.IsImpostor) return;
                var hud = HudManager.Instance;
                if (hud == null || hud.Chat == null) return;
                hud.Chat.AddChat(me, won
                    ? UCLocalization.Tr("uc.gambler.announce_worse", seconds)
                    : UCLocalization.Tr("uc.gambler.announce_better", seconds));
            } catch { }
        }

        public static Bet FindBet(byte id) {
            foreach (var b in bets) if (b.Id == id) return b;
            return null;
        }

        // ====================================================================
        // Host: placing a bet
        // ====================================================================
        private static void HostPlaceBet(BetKind kind, byte target, PlayerControl sender) {
            try {
                if (!AmHost() || !active || gambler == null) return;
                // Only the Gambler may place bets, and only while alive and outside a meeting.
                if (sender == null || sender.PlayerId != gambler.PlayerId) return;
                if (!IsAlive(gambler) || InMeeting()) return;
                if (OpenBetCount() >= Mathf.RoundToInt(MaxActiveBets?.getFloat() ?? 2f)) return;

                var def = Def(kind);
                if (def == null) return;
                if (def.NeedsTarget) {
                    var t = Helpers.playerById(target);
                    // Betting on yourself is out (the user's decision: no self-bets), and a dead or
                    // missing target has no story left to tell.
                    if (t == null || !IsAlive(t) || t.PlayerId == gambler.PlayerId) return;
                } else {
                    target = byte.MaxValue;
                }

                SendPlaceBet(nextBetId++, kind, target);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] HostPlaceBet failed: {e}");
            }
        }

        // ====================================================================
        // Host: round observation
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (!AmHost() || !active || target == null) return;
                    killTimes.Add(Time.time);
                    if (!deathOrderThisRound.Contains(target.PlayerId)) deathOrderThisRound.Add(target.PlayerId);
                } catch { }
            }
        }

        // Task counting, same entry point the Auditor uses: CompleteTask is the RECEIVING side of the
        // vanilla task RPC and runs on the host for the whole lobby. Impostor fake tasks never get
        // here, which is exactly why the threshold has to be high (see the file header).
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CompleteTask))]
        static class TaskCountPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] uint idx) {
                try {
                    if (!AmHost() || !active || __instance == null || __instance.Data == null) return;
                    // Re-completions we ordered ourselves are bookkeeping, not crew progress.
                    if (pendingRecomplete.TryGetValue(__instance.PlayerId, out var expected)
                        && expected.Remove(idx)) {
                        if (expected.Count == 0) pendingRecomplete.Remove(__instance.PlayerId);
                        return;
                    }
                    if (__instance.hasFakeTasks()) return;
                    byte pid = __instance.PlayerId;
                    tasksThisRound[pid] = (tasksThisRound.TryGetValue(pid, out int n) ? n : 0) + 1;
                } catch { }
            }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ReportDeadBody))]
        static class ReportPatch {
            public static void Postfix(PlayerControl __instance,
                                       [HarmonyArgument(0)] NetworkedPlayerInfo target) {
                try {
                    if (!AmHost() || !active || __instance == null) return;
                    // target == null is the emergency button: there is no reporter to bet on.
                    lastReporter = target == null ? byte.MaxValue : __instance.PlayerId;
                } catch { }
            }
        }

        // Deadly sabotage probe. Polled rather than hooked: the activation paths differ per map and
        // per mod, but the SYSTEM state is synced and identical on every client (the same probe
        // UsefulTORStuff' SabotageTuning uses).
        private static bool DeadlySabotageActive() {
            try {
                var ship = ShipStatus.Instance;
                if (ship == null || ship.Systems == null) return false;
                ISystemType raw;
                if (ship.Systems.TryGetValue(SystemTypes.Reactor, out raw) || ship.Systems.TryGetValue(SystemTypes.Laboratory, out raw)) {
                    var reactor = raw != null ? raw.TryCast<ReactorSystemType>() : null;
                    if (reactor != null && reactor.IsActive) return true;
                }
                if (ship.Systems.TryGetValue(SystemTypes.LifeSupp, out raw)) {
                    var o2 = raw != null ? raw.TryCast<LifeSuppSystemType>() : null;
                    if (o2 != null && o2.IsActive) return true;
                }
            } catch { }
            return false;
        }

        // ====================================================================
        // Per-frame: bet cooldown (local) + sabotage probe / tuning expiry (host)
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class UpdatePatch {
            public static void Postfix() {
                try {
                    if (!active) return;

                    if (IsLocalGambler() && betCooldownLeft > 0f && !InMeeting())
                        betCooldownLeft = Mathf.Max(0f, betCooldownLeft - Time.deltaTime);

                    if (!AmHost()) return;

                    if (!InMeeting()) {
                        bool now = DeadlySabotageActive();
                        if (now && !sabotageWasActive) deadlySabotageThisRound = true;
                        sabotageWasActive = now;
                    }

                    // Speed effects are a permanent multiplier in PlayerTuning, so their lifetime is
                    // ours to manage. Expired -> hand the player back to normal.
                    if (tuningUntil.Count > 0) {
                        var done = new List<byte>();
                        foreach (var kv in tuningUntil) if (Time.time >= kv.Value) done.Add(kv.Key);
                        foreach (var pid in done) {
                            tuningUntil.Remove(pid);
                            try { PlayerTuning.SendClear(pid); } catch { }
                        }
                    }
                } catch { }
            }
        }

        // ====================================================================
        // Settlement
        // ====================================================================

        // Round facts, settled the moment the meeting opens.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    if (!AmHost() || !active) return;
                    foreach (var b in bets.ToArray()) {
                        if (b.Settled) continue;
                        var def = Def(b.Kind);
                        if (def == null || def.Settle != BetSettle.AtMeetingStart) continue;
                        bool push;
                        bool won = ResolveRoundBet(b, out push);
                        SettleAndPay(b, won, push);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] meeting settle failed: {e}");
                }
            }
        }

        private static bool ResolveRoundBet(Bet b, out bool push) {
            push = false;
            float window = KillWindow?.getFloat() ?? 30f;
            switch (b.Kind) {
                case BetKind.KillInWindow: {
                    // A meeting inside the window is nobody's fault -> undecided, no payout either way.
                    float elapsed = Time.time - b.Placed;
                    foreach (float t in killTimes)
                        if (t >= b.Placed && t <= b.Placed + window) return true;
                    if (elapsed < window) { push = true; return false; }
                    return false;
                }
                case BetKind.NoKillThisRound:
                    return killTimes.Count == 0;
                case BetKind.DeadlySabotage:
                    return deadlySabotageThisRound;
                case BetKind.MoreThanTwoKills:
                    return killTimes.Count > 2;
                case BetKind.TargetDoesNTasks: {
                    int need = Mathf.RoundToInt(TaskThreshold?.getFloat() ?? 4f);
                    return tasksThisRound.TryGetValue(b.Target, out int n) && n >= need;
                }
                case BetKind.TargetSurvives: {
                    var t = Helpers.playerById(b.Target);
                    return t != null && t.Data != null && !t.Data.IsDead && !t.Data.Disconnected;
                }
                case BetKind.WhoDiesNext:
                    // Nobody died at all -> the question was never answered.
                    if (deathOrderThisRound.Count == 0) { push = true; return false; }
                    return deathOrderThisRound[0] == b.Target;
            }
            push = true;
            return false;
        }

        // Vote facts, settled once the vote is in.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.VotingComplete))]
        static class VotingCompletePatch {
            public static void Postfix(MeetingHud __instance,
                                       [HarmonyArgument(1)] NetworkedPlayerInfo exiled,
                                       [HarmonyArgument(2)] bool tie) {
                try {
                    if (!AmHost() || !active) return;
                    var counts = CountVotes(__instance, out int voters, out bool anyVoteCast);
                    foreach (var b in bets.ToArray()) {
                        if (b.Settled) continue;
                        var def = Def(b.Kind);
                        if (def == null || def.Settle != BetSettle.AtVoteEnd) continue;
                        bool push;
                        bool won = ResolveVoteBet(b, exiled, tie, counts, voters, anyVoteCast, out push);
                        SettleAndPay(b, won, push);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] vote settle failed: {e}");
                }
            }
        }

        // Votes per target id, EXCLUDING the Gambler's own vote (see the file header). 253 is skip.
        private const byte SkipVote = 253;
        private static Dictionary<byte, int> CountVotes(MeetingHud hud, out int voters, out bool anyVoteCast) {
            var counts = new Dictionary<byte, int>();
            voters = 0; anyVoteCast = false;
            try {
                if (hud == null || hud.playerStates == null) return counts;
                foreach (var state in hud.playerStates) {
                    if (state == null) continue;
                    if (gambler != null && state.TargetPlayerId == gambler.PlayerId) continue; // his own vote never counts
                    byte votedFor = state.VotedFor;
                    if (votedFor == byte.MaxValue || votedFor == 254) continue;                // no vote cast
                    anyVoteCast = true;
                    voters++;
                    if (votedFor == SkipVote) continue;                                        // skip is not a target
                    counts[votedFor] = (counts.TryGetValue(votedFor, out int n) ? n : 0) + 1;
                }
            } catch { }
            return counts;
        }

        private static bool ResolveVoteBet(Bet b, NetworkedPlayerInfo exiled, bool tie,
                                           Dictionary<byte, int> counts, int voters, bool anyVoteCast,
                                           out bool push) {
            push = false;
            switch (b.Kind) {
                case BetKind.SomeoneEjected:
                    return exiled != null;
                case BetKind.TieVote:
                    return tie;
                case BetKind.TargetEjected:
                    return exiled != null && exiled.PlayerId == b.Target;
                case BetKind.TargetGetsNoVote:
                    return !counts.ContainsKey(b.Target);
                case BetKind.TargetGetsNVotes: {
                    int need = Mathf.RoundToInt(VoteThreshold?.getFloat() ?? 3f);
                    return counts.TryGetValue(b.Target, out int n) && n >= need;
                }
                case BetKind.ReporterEjected:
                    // Emergency button: there was no reporter, so the bet cannot be decided.
                    if (lastReporter == byte.MaxValue) { push = true; return false; }
                    return exiled != null && exiled.PlayerId == lastReporter;
                case BetKind.UnanimousVote: {
                    // Everyone who voted (minus the Gambler, minus the accused themselves) agreed on
                    // the same player. Nobody voting at all is not unanimity.
                    if (!anyVoteCast) { push = true; return false; }
                    byte candidate = byte.MaxValue; int candidateVotes = 0;
                    foreach (var kv in counts) { if (kv.Value > candidateVotes) { candidate = kv.Key; candidateVotes = kv.Value; } }
                    if (candidate == byte.MaxValue) return false;   // everyone skipped
                    int eligible = voters;
                    // The accused does not have to vote for themselves.
                    foreach (var state in MeetingHud.Instance.playerStates) {
                        if (state == null) continue;
                        if (state.TargetPlayerId != candidate) continue;
                        if (state.VotedFor != candidate && state.VotedFor != byte.MaxValue && state.VotedFor != 254)
                            eligible--;
                        break;
                    }
                    return candidateVotes >= eligible && candidateVotes > 0;
                }
            }
            push = true;
            return false;
        }

        // ====================================================================
        // Host: payout
        // ====================================================================
        private static void SettleAndPay(Bet b, bool won, bool push) {
            SendSettle(b.Id, won, push);
            if (push) return;
            var def = Def(b.Kind);
            if (def == null) return;

            // Tier decides the currency: small tiers move only him, the top tiers reach the impostors.
            switch (def.Tier) {
                case 1:
                case 2:
                    ApplySpeedEffect(won);
                    break;
                case 3:
                case 4:
                    if (!ApplyTaskEffect(won, 1)) ApplySpeedEffect(won);
                    break;
                case 5:
                    ApplyCooldownEffect(won);
                    break;
                default:
                    // Tier 6: both, exactly as designed - two tasks AND the cooldown.
                    if (!ApplyTaskEffect(won, 2)) ApplySpeedEffect(won);
                    ApplyCooldownEffect(won);
                    break;
            }
        }

        private static void ApplySpeedEffect(bool won) {
            try {
                if (gambler == null) return;
                float pct = (SpeedDelta?.getFloat() ?? 15f) / 100f;
                float mult = won ? 1f + pct : 1f - pct;
                PlayerTuning.SendSetTuning(gambler.PlayerId, mult, 1f, false);
                tuningUntil[gambler.PlayerId] = Time.time + (EffectDuration?.getFloat() ?? 30f);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] speed effect failed: {e}");
            }
        }

        // Returns false when there was nothing to give or take - the caller then falls back to the
        // speed effect, so a Gambler with all tasks done is neither immune to his losses nor cheated
        // out of his wins.
        private static bool ApplyTaskEffect(bool won, int count) {
            try {
                if (gambler == null || gambler.Data == null || gambler.Data.Tasks == null) return false;
                if (won) {
                    int open = 0;
                    foreach (var t in gambler.Data.Tasks) if (t != null && !t.Complete) open++;
                    if (open == 0) return false;
                    SendGrantTasks((byte)Mathf.Min(count, open));
                    return true;
                }
                return HostRevertTasks(count);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] task effect failed: {e}");
                return false;
            }
        }

        // Takes `count` finished tasks away from the Gambler, server-authoritatively. Same two-step as
        // the Auditor: rebuild the whole list through the host-owned RpcSetTasks (the only path the
        // SERVER also sees), then have his own client re-complete everything that should stay done.
        private static bool HostRevertTasks(int count) {
            try {
                if (gambler?.Data?.Tasks == null) return false;
                int total = gambler.Data.Tasks.Count;
                if (total == 0) return false;

                var completed = new List<uint>();
                for (int i = 0; i < total; i++) {
                    var t = gambler.Data.Tasks[i];
                    if (t != null && t.Complete) completed.Add(t.Id);
                }
                if (completed.Count == 0) return false;      // nothing done yet -> nothing to take

                // Take the most recently finished ones back.
                int take = Mathf.Min(count, completed.Count);
                var revoked = new HashSet<uint>();
                for (int i = 0; i < take; i++) revoked.Add(completed[completed.Count - 1 - i]);

                var typeIds = new Il2CppStructArray<byte>(total);
                var keepComplete = new List<uint>();
                for (int i = 0; i < total; i++) {
                    var t = gambler.Data.Tasks[i];
                    if (t == null) return false;
                    typeIds[i] = t.TypeId;
                    if (t.Complete && !revoked.Contains(t.Id)) keepComplete.Add(t.Id);
                }

                if (!pendingRecomplete.TryGetValue(gambler.PlayerId, out var expected)) {
                    expected = new HashSet<uint>();
                    pendingRecomplete[gambler.PlayerId] = expected;
                }
                foreach (uint id in keepComplete) expected.Add(id);

                gambler.Data.RpcSetTasks(typeIds);
                SendRevertTasks(keepComplete);
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Gambler] revoked {take} task(s) ({keepComplete.Count} re-completed).");
                return true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] revert failed: {e}");
                return false;
            }
        }

        // Kill cooldown for every impostor, until the next meeting. PlayerTuning speaks in
        // MULTIPLIERS, so the configured second-delta is converted against the lobby's kill cooldown.
        private static void ApplyCooldownEffect(bool won) {
            try {
                float delta = CooldownDelta?.getFloat() ?? 5f;
                float baseCd = 25f;
                try { baseCd = Mathf.Max(1f, GameOptionsManager.Instance.currentNormalGameOptions.KillCooldown); } catch { }
                // won -> the impostors get SLOWER (longer cooldown), lost -> faster.
                float mult = Mathf.Clamp(won ? (baseCd + delta) / baseCd : (baseCd - delta) / baseCd, 0.1f, 5f);

                foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                    if (p == null || p.Data == null || p.Data.Role == null) continue;
                    if (!p.Data.Role.IsImpostor || p.Data.Disconnected) continue;
                    PlayerTuning.SendSetTuning(p.PlayerId, 1f, mult, false);
                    // Until the next meeting: MeetingClosePatch clears it. A long fallback keeps a
                    // dropped meeting from making the effect permanent.
                    tuningUntil[p.PlayerId] = Time.time + 600f;
                }

                if (AnnounceToImpostors?.getBool() ?? true) SendAnnounce(won, delta);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] cooldown effect failed: {e}");
            }
        }

        // ====================================================================
        // Round boundaries
        // ====================================================================
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        static class MeetingClosePatch {
            public static void Postfix() {
                try {
                    // A new round: the observations start over. Bets that were still open at the
                    // meeting are gone with it - every bet is a bet on THIS round.
                    killTimes.Clear();
                    tasksThisRound.Clear();
                    deathOrderThisRound.Clear();
                    deadlySabotageThisRound = false;
                    sabotageWasActive = false;
                    lastReporter = byte.MaxValue;
                    bets.RemoveAll(b => b.Settled);

                    // He may bet again right away (user decision), cooldown reset at every meeting.
                    betCooldownLeft = 0f;

                    if (!AmHost()) return;
                    // Impostor cooldown effects last exactly one round.
                    foreach (var kv in new List<KeyValuePair<byte, float>>(tuningUntil)) {
                        var p = Helpers.playerById(kv.Key);
                        if (p == null || p.Data == null || p.Data.Role == null) continue;
                        if (!p.Data.Role.IsImpostor) continue;
                        tuningUntil.Remove(kv.Key);
                        try { PlayerTuning.SendClear(kv.Key); } catch { }
                    }
                } catch { }
            }
        }

        // Open bets die with him - settling them afterwards would pay a dead man and, worse, tell the
        // living nothing they could act on.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        [HarmonyPriority(Priority.Low)]
        static class GamblerDeathPatch {
            public static void Postfix([HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (!active || gambler == null || target == null) return;
                    if (target.PlayerId != gambler.PlayerId) return;
                    bets.RemoveAll(b => !b.Settled);
                } catch { }
            }
        }

        // ====================================================================
        // Spawn pick (host) + resets
        // ====================================================================
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    if (!AmHost()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    // A MODIFIER, so unlike the UC roles this does not need a plain crewmate: any
                    // living crew member qualifies, whatever role they already have. Neutrals and
                    // impostors are out - the payouts are built around crew tasks and crew interests.
                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(IsModifierCandidate).ToList();
                    if (candidates.Count == 0) return;
                    SendSetGambler(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] IntroEnd pick failed: {e}");
                }
            }
        }

        private static bool IsModifierCandidate(PlayerControl p) {
            try {
                if (!UCPromotion.IsAlive(p) || p.Data.Role == null || p.Data.Role.IsImpostor) return false;
                var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
                if (info != null && info.isNeutral) return false;
                return true;
            } catch { return false; }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { FullReset(); }
        }

        // PlayerId-keyed state must ALSO be cleared when joining another lobby - resetVariables alone
        // leaks it into the next lobby (see the resetVariables-Lobby-Leak rule).
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() { FullReset(); }
        }

        private static void FullReset() {
            gambler = null;
            active = false;
            bets.Clear();
            killTimes.Clear();
            tasksThisRound.Clear();
            deathOrderThisRound.Clear();
            pendingRecomplete.Clear();
            tuningUntil.Clear();
            deadlySabotageThisRound = false;
            sabotageWasActive = false;
            lastReporter = byte.MaxValue;
            betCooldownLeft = 0f;
            nextBetId = 0;
            lastResultText = null;
            lastResultUntil = 0f;
        }

        // ---- Role identity: APPEND, never replace - a modifier rides on top of the real role ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, [HarmonyArgument(1)] bool showModifier,
                                        ref List<RoleInfo> __result) {
                try {
                    if (!active || gambler == null || p == null || p != gambler || __result == null) return;
                    if (!showModifier) return;
                    if (!__result.Contains(GamblerInfo())) __result.Add(GamblerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
