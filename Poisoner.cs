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
 * Options live in the 1430-1436 block. See ID-Registry.md.
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

        // ---- Options (IDs 1430-1436) ----
        public static CustomOption SpawnRate;             // 1430 (header)
        public static CustomOption SpawnMinPlayers;       // 1431
        public static CustomOption PoisonDeathMeetings;   // 1432 - meetings before poisoned reporter dies
        public static CustomOption AntidoteCharges;       // 1433 - how many times Medic can cure
        public static CustomOption NeedsHealer;           // 1437 - only spawns with a Medic or Paramedic in play
        public static CustomOption MaxPoisonedPerRound;   // 1434 - max poisoned bodies per round
        public static CustomOption BaitSelfPoison;        // 1435 - TOR's Bait auto-report poisons the Poisoner too
        public static CustomOption SelfReportPoison;      // 1436 - reporting his own poisoned body poisons him

        // ---- Runtime state ----
        public static PlayerControl poisoner;
        public static bool active;

        // Poisoned bodies (victim PlayerId)
        private static readonly HashSet<byte> poisonedBodies = new();
        // Poisoned reporters: reporterId -> meetings REMAINING until death (counts down each meeting).
        // A countdown avoids the ordering race an absolute meeting counter had (report stamp vs. the
        // meeting-start increment could land in either order on the host).
        private static readonly Dictionary<byte, int> poisonedReporters = new();
        // Bodies poisoned this round (reset each meeting)
        private static readonly HashSet<byte> bodiesPoisonedThisRound = new();

        // Antidote button
        private static int antidoteUsesLeft;
        private static TheOtherRoles.Objects.CustomButton antidoteButton;
        private static PlayerControl antidoteTarget;

        // ---- Custom RPC subtypes: module byte 193 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = 193;
        private const byte SubSetPoisoner = 0;
        private const byte SubMarkBody = 1;      // victimId
        private const byte SubPoisonReporter = 2; // reporterId
        private const byte SubAntidote = 3;       // targetId
        private const byte SubPoisonDeath = 4;    // targetId

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
                // Minimum is 2: with a 1-meeting delay the reporter would die at the close of the very
                // meeting their report triggered, leaving the Medic no free-roam round to cure them.
                PoisonDeathMeetings = CustomOption.Create(1432, Types.Impostor, "Poison Death After Meetings",
                    2f, 2f, 5f, 1f, SpawnRate);
                AntidoteCharges = CustomOption.Create(1433, Types.Impostor, "Medic Antidote Uses Per Round",
                    1f, 0f, 5f, 1f, SpawnRate);
                MaxPoisonedPerRound = CustomOption.Create(1434, Types.Impostor, "Max Poisoned Bodies Per Round",
                    3f, 1f, 5f, 1f, SpawnRate);
                // Both off by default: the Poisoner is spared his own poison (since 1.2.14.2).
                BaitSelfPoison = CustomOption.Create(1435, Types.Impostor, "Poisoner Poisons Himself Via Bait",
                    false, SpawnRate);
                SelfReportPoison = CustomOption.Create(1436, Types.Impostor, "Poisoner Poisons Himself On Self-Report",
                    false, SpawnRate);
                // Poison is only fair when somebody can cure it (User 2026-10-04, Fable review): on by
                // default, the Medic AND the Paramedic count as the healer. Named so the host sees why
                // the role does not come in a round without one.
                NeedsHealer = CustomOption.Create(1437, Types.Impostor, "Poisoner Needs A Healer In Play",
                    true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Poisoner] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] CreateOptions failed: {e}");
            }
        }

        // Poison deaths are applied locally on each client via RPCProcedure.uncheckedMurderPlayer
        // (distributed by our own SubPoisonDeath RPC), so no reflection/RPC-byte resolution is needed.
        public static void TryPatch(Harmony harmony) {
            // Receiver registration for the shared UC channel (UCRpc.CallId = 230). Every module
            // registers here even when it has no Harmony work left to do - TryPatch is the single
            // place UnknownsCollectionPlugin.Load() calls for every module.
            UCRpc.Register(RpcId, HandleModuleRpc);
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
            MessageWriter w = UCRpc.Begin(RpcId); // shared UC channel; RpcId is the module byte
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
            // Antidote charges are a PER-GAME budget for the Medic. Set them exactly once, here - they
            // used to refill every meeting (the line sat in MeetingStartPatch), which made the option a no-op.
            antidoteUsesLeft = AntidoteChargesValue();
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Poisoner] The Poisoner is {poisoner.Data?.PlayerName}.");
        }

        private static void ApplyMarkBody(byte victimId) {
            if (active) poisonedBodies.Add(victimId);
            bodiesPoisonedThisRound.Add(victimId);
            // The Poisoner learns that the poison took (audit 04.10.: he got no word at all). Only on
            // his own client.
            if (IsLocalPoisoner()) Tell(UCLocalization.Tr("uc.ui.poisoner.marked", bodiesPoisonedThisRound.Count, MaxPoisonedValue()));
        }

        private static void ApplyPoisonReporter(byte reporterId) {
            if (!active || reporterId == byte.MaxValue) return;
            // Start the countdown: the reporter dies after this many meetings (decremented each meeting).
            if (!poisonedReporters.ContainsKey(reporterId)) {
                poisonedReporters[reporterId] = PoisonDeathValue();
                if (IsLocalPoisoner()) {
                    var r = Helpers.playerById(reporterId);
                    Tell(UCLocalization.Tr("uc.ui.poisoner.caught", r?.Data?.PlayerName ?? "?", PoisonDeathValue()));
                }
            }
        }

        private static bool IsLocalPoisoner() =>
            active && poisoner != null && PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.PlayerId == poisoner.PlayerId;

        private static void Tell(string text) {
            try { HudManager.Instance?.Chat?.AddChat(PlayerControl.LocalPlayer, text); } catch { }
        }

        /// A revived victim is a new person: a later body of his is not the poisoned one (audit 04.10.).
        /// Called from Pelican.ForgetDeath, i.e. from every UC revive.
        internal static void ForgetBody(byte id) => poisonedBodies.Remove(id);

        private static void ApplyAntidote(byte targetId) {
            poisonedReporters.Remove(targetId);
            antidoteUsesLeft--;

            // Cleanse feedback (poof + chime) is gated to the medic and the cured target only.
            // ApplyAntidote runs on EVERY client (SendAntidote broadcasts SubAntidote) - an ungated
            // world effect here would tell every bystander "this player was poisoned", the exact leak
            // the fuse-loop PlayerId gate already avoids for the Maniac's bomb carrier (Maniac.cs).
            var me = PlayerControl.LocalPlayer;
            var healer = Healer();
            bool localIsMedic = me != null && healer != null && me.PlayerId == healer.PlayerId;
            bool localIsTarget = me != null && me.PlayerId == targetId;
            if (localIsMedic || localIsTarget) {
                var target = Helpers.playerById(targetId);
                if (target != null) PoisonerFx.SpawnCleanse(target.GetTruePosition());
                UCAssets.PlayPoisonerAntidote();
            }

            UnknownsCollectionPlugin.Logger?.LogInfo($"[Poisoner] Antidote used on player {targetId}.");
        }

        private static void ApplyPoisonDeath(byte targetId) {
            // No poisoner ever spawned (or the round already reset) -> nothing to exile. Without this,
            // a forged SubPoisonDeath could Exiled() anyone even with no Poisoner in play (AUDIT-2026-08-15).
            if (!active) return;
            var target = Helpers.playerById(targetId);
            if (target != null && target.Data != null) {
                // Apply the death LOCALLY only. SendPoisonDeath already broadcast SubPoisonDeath to every
                // client, so each client runs this exactly once. We use Exiled() (not uncheckedMurderPlayer)
                // so the poison death leaves NO body to report — exactly like a guesser shot or a vote-out.
                // But Exiled() is what TOR treats as a vote-out: its ExilePlayerPatch takes the Lawyer of
                // an exiled client down with him. Poison is a kill, and a killed client turns his Lawyer
                // into a Pursuer, so promote him first (the Witch's exile and UTS' LoverRevenger do the
                // same; Opus review 2026-10-02). Every client runs this locally, like the exile itself.
                try {
                    if (!target.Data.IsDead && Lawyer.lawyer != null && target == Lawyer.target)
                        RPCProcedure.lawyerPromotesToPursuer();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Poisoner] lawyer promotion failed: {e.Message}");
                }
                target.Exiled();
                // TOR booked that as an exile without a killer (ExilePlayerPatch): the end screen said
                // "exiled" and the Poisoner's kill count missed it (audit 04.10.). Every client fixes
                // its own ledger entry, like the exile itself.
                try {
                    var ledger = Pelican.DeadPlayersLedger();
                    var entry = ledger?.LastOrDefault(d => d != null && d.player != null && d.player.PlayerId == targetId);
                    if (entry != null && poisoner != null) {
                        entry.deathReason = DeadPlayer.CustomDeathReason.Kill;
                        entry.killerIfExisting = poisoner;
                    }
                } catch { }
                // Custom kill overlay: the Exiled() path never reaches KillOverlay.ShowKillAnimation,
                // so trigger it directly - with the vanilla audience (victim + killer only). The
                // overlay queue holds it until the meeting/exile UI is gone.
                var lp = PlayerControl.LocalPlayer;
                bool poisonerHere = poisoner != null;   // Unity null after a disconnect
                if (lp != null && (lp.PlayerId == targetId || (poisonerHere && lp.PlayerId == poisoner.PlayerId)))
                    UCKillOverlay.PlayFor(UCKillOverlay.Kind.Poisoner, poisonerHere ? poisoner.Data : null, target.Data);
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Poisoner] Player {targetId} died from poison (no body).");
            }
            poisonedReporters.Remove(targetId);
        }

        public static void MarkFromDraft(byte playerId) => ApplySetPoisoner(playerId);

        // ---- RPC handler ----
        // RPC receiver, registered on the shared UC channel in TryPatch. UCRpc's dispatcher
        // already consumed the module byte, so this starts at the subtype byte - the wire
        // format behind the module byte is byte-for-byte what the old per-callId RPC used.
        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSetPoisoner: { byte id = reader.ReadByte();
                        // Host-authoritative role assignment (host pick in IntroCutscene.OnDestroy / UCRoleDraft) - a
                    // forged one would let any client declare any player this role (AUDIT H-3).
                        if (UCRpc.RequireHost("Poisoner.SetPoisoner")) ApplySetPoisoner(id); break; }
                    // MarkBody / PoisonReporter / PoisonDeath are host-only broadcasts (Send* gated behind
                    // AmHost in MurderPatch/ReportPatch/MeetingClosePatch) - a forged sender could otherwise
                    // mark bodies, arm reporters or exile players at will (AUDIT-2026-08-15).
                    //
                    // Antidote is NOT one of them. It is the MEDIC's ability button (HudStartPatch below), so it
                    // legitimately originates from the Medic's own client, which is usually not the host - the
                    // same category UCRpc.RequireOwnerOrHost exists for. Gating it with RequireHost turned every
                    // non-host Medic's cure into a local-only no-op: the Medic saw the cleanse and lost a charge,
                    // while the host kept the reporter in poisonedReporters and killed them at MeetingHud.Close
                    // anyway. The owner here is the Medic, not the Poisoner.
                    case SubMarkBody: { byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("Poisoner.MarkBody")) ApplyMarkBody(id); break; }
                    case SubPoisonReporter: { byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("Poisoner.PoisonReporter")) ApplyPoisonReporter(id); break; }
                    case SubAntidote: { byte id = reader.ReadByte();
                        if (UCRpc.RequireOwnerOrHost(Healer(), "Poisoner.Antidote")) ApplyAntidote(id); break; }
                    case SubPoisonDeath: { byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("Poisoner.PoisonDeath")) ApplyPoisonDeath(id); break; }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] HandleRpc failed: {e}");
            }
        }

        // ---- Round reset ----
        // PlayerId-keyed state is cleared on OnGameJoined as well as on resetVariables
        // (AUDIT M-12). PlayerIds are handed out per LOBBY, and resetVariables only ever
        // arrives from a host that has this mod - so joining a vanilla host, or leaving a
        // lobby abnormally, used to carry the previous game's ids into the next one and let
        // them act on whoever happens to reuse them. Same belt-and-suspenders rule the
        // Silencer and the Shade already followed; the body is shared so the two entry
        // points can never drift apart.
        private static void ClearState() {
            poisoner = null;
            active = false;
            poisonedBodies.Clear();
            poisonedReporters.Clear();
            bodiesPoisonedThisRound.Clear();
            _pendingPoisonDeaths.Clear();
            antidoteUsesLeft = 0;
            // antidoteButton deliberately kept (resetVariables runs after HudManager.Start).
            antidoteTarget = null;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Poisoner", ClearState);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinPatch {
            public static void Postfix() => UCResetGuard.Run("Poisoner", ClearState);
        }

        // ---- Game start ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    // Runs after every other intro pick (UCPromotion.LatePickers), so a Paramedic of
                    // this round is already known here.
                    if ((NeedsHealer == null || NeedsHealer.getBool()) && Healer() == null) {
                        UnknownsCollectionPlugin.Logger?.LogInfo("[Poisoner] not rolled: no Medic or Paramedic in play.");
                        return;
                    }
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
                    if (IsLocalPoisoner() && __instance != null && target != null && __instance.PlayerId == poisoner.PlayerId
                        && target.PlayerId != poisoner.PlayerId && bodiesPoisonedThisRound.Count >= MaxPoisonedValue())
                        Tell(UCLocalization.Tr("uc.ui.poisoner.limit", MaxPoisonedValue()));
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || poisoner == null || target == null) return;
                    if (__instance.PlayerId != poisoner.PlayerId) return;
                    // His own death (Lover suicide, Tesla charge: source == target) leaves no poisoned
                    // body; otherwise whoever reports the dead Poisoner was poisoned (Opus audit round 2).
                    if (target.PlayerId == poisoner.PlayerId) return;
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
                    // Host-only: ReportDeadBody runs on the host for a normal report, but the Bait/Mayor
                    // bypass paths call it on EVERY client — without this gate each client would broadcast
                    // its own SendPoisonReporter (redundant RPC storm). SendPoisonReporter is a single-
                    // broadcaster like the rest of the role (mirrors Witness's ReportPatch).
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || target == null) return;
                    if (!poisonedBodies.Contains(target.PlayerId)) return;
                    // The Poisoner reporting his own poisoned body. Two cases, one option each (both
                    // off by default): TOR's Bait auto-report, which reports a Bait's body on the
                    // killer's behalf (1435), and a voluntary self-report (1436). Off = he is spared.
                    if (poisoner != null && __instance.PlayerId == poisoner.PlayerId) {
                        bool viaBait = Bait.bait != null && Bait.bait.Any(b => b != null && b.PlayerId == target.PlayerId);
                        var opt = viaBait ? BaitSelfPoison : SelfReportPoison;
                        if (opt == null || !opt.getBool()) { poisonedBodies.Remove(target.PlayerId); return; }
                    }
                    if (!IsAlive(__instance)) return;
                    SendPoisonReporter(__instance.PlayerId);
                    poisonedBodies.Remove(target.PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] ReportPatch failed: {e}");
                }
            }
        }

        // ---- Meeting start: increment meeting counter, check for poison deaths ----
        // Random "you feel unwell" flavour shown locally to a poisoned reporter during a meeting.
        private static readonly string[] SickMessageKeys = {
            "uc.chat.poisoner.sick1",
            "uc.chat.poisoner.sick2",
            "uc.chat.poisoner.sick3",
            "uc.chat.poisoner.sick4",
            "uc.chat.poisoner.sick5",
            "uc.chat.poisoner.sick6",
            "uc.chat.poisoner.sick7",
            "uc.chat.poisoner.sick8",
        };

        private static void ShowSickMessageIfPoisoned() {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null || me.Data == null || me.Data.IsDead) return;
                if (!poisonedReporters.ContainsKey(me.PlayerId)) return;
                string msg = UCLocalization.Tr(SickMessageKeys[UnityEngine.Random.Range(0, SickMessageKeys.Length)]);
                HudManager.Instance?.Chat?.AddChat(me, msg); // local-only display, only the victim sees it
                UCAssets.PlayPoisonGurgle();                  // sickly cue, victim-only like the message
            } catch { }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    // Gated on active only: a Poisoner who disconnected (destroyed PlayerControl) must
                    // not freeze the countdown, his poison keeps working just as after his death.
                    if (!active) return;

                    // Reset round tracking. The marks go too: every body leaves the map with the
                    // meeting, and a mark kept past it hit a revived victim's NEXT body (audit 04.10.).
                    // The report check runs in ReportDeadBody, before this Start.
                    bodiesPoisonedThisRound.Clear();
                    poisonedBodies.Clear();

                    // (Antidote charges are set once per game in ApplySetPoisoner - refilling them here made
                    // the charges option meaningless, the Medic got a fresh stock every single meeting.)

                    // Count down every poisoned reporter by one meeting; schedule deaths at zero. Runs on
                    // every client so the countdown stays in sync; only the host acts on the list (in
                    // MeetingClosePatch). Dead reporters (voted/killed meanwhile) are simply dropped.
                    var toKill = new List<byte>();
                    foreach (byte id in poisonedReporters.Keys.ToList()) {
                        var p = Helpers.playerById(id);
                        if (p == null || !IsAlive(p)) { poisonedReporters.Remove(id); continue; }
                        int remaining = poisonedReporters[id] - 1;
                        poisonedReporters[id] = remaining;
                        if (remaining <= 0) toKill.Add(id);
                    }

                    // Executed after the meeting closes (see MeetingClosePatch).
                    _pendingPoisonDeaths = toKill;

                    // Tell the local player if they are the poisoned one.
                    ShowSickMessageIfPoisoned();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] MeetingStartPatch failed: {e}");
                }
            }
        }

        private static List<byte> _pendingPoisonDeaths = new();

        private static byte exiledThisMeeting = byte.MaxValue;

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.VotingComplete))]
        static class VotingCompletePatch {
            public static void Postfix([HarmonyArgument(1)] NetworkedPlayerInfo exiled) {
                exiledThisMeeting = exiled != null ? exiled.PlayerId : byte.MaxValue;
            }
        }

        // ---- Meeting end: execute pending poison deaths ----
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        static class MeetingClosePatch {
            public static void Postfix() {
                try {
                    if (!active || _pendingPoisonDeaths.Count == 0) return;
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

                    foreach (byte id in _pendingPoisonDeaths) {
                        // Don't kill if they were cured during the meeting...
                        if (!poisonedReporters.ContainsKey(id)) continue;
                        // ...or if they already died some other way (avoids a duplicate corpse).
                        var p = Helpers.playerById(id);
                        if (p == null || !IsAlive(p)) { poisonedReporters.Remove(id); continue; }
                        // Voted out in this very meeting: the exile kills him a moment later; a poison death
                        // on top ran Exiled() twice (double GameHistory entry, Lover/Lawyer logic twice).
                        if (id == exiledThisMeeting) { poisonedReporters.Remove(id); continue; }
                        SendPoisonDeath(id);
                    }
                    _pendingPoisonDeaths.Clear();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poisoner] MeetingClosePatch failed: {e}");
                }
            }
        }

        // ---- Antidote button for the healer ----
        /// Who holds the Antidote: the Medic, or the Paramedic in a round without one (User 2026-10-04,
        /// Fable review: both count as the healer that makes poison curable). Same answer on every
        /// client, both roles are synced.
        internal static PlayerControl Healer() {
            if (Medic.medic != null && Medic.medic.Data != null && !Medic.medic.Data.Disconnected) return Medic.medic;
            var pm = Paramedic.paramedic;
            if (pm != null && pm.Data != null && !pm.Data.Disconnected) return pm;
            return null;
        }

        private static PlayerControl FindMedic() => Healer();

        // Pulsing green outline for the Medic's Antidote target: setPlayerOutline forces the alpha
        // channel itself (SetAlpha(Chameleon.visibility(...))), so a pulse riding on alpha (as
        // PoltergeistFx.Flicker does for its own particles) would just get overwritten every call -
        // the pulse instead rides on brightness, so a "you can heal this player" outline reads
        // differently at a glance from a flat, static aggressive kill-target outline.
        private static Color HealPulseColor() {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 4f);
            return Color.Lerp(new Color(0.10f, 0.5f, 0.20f), Color.green, pulse);
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartPatch {
            [HarmonyPriority(Priority.Low)]
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
                              && PlayerControl.LocalPlayer.PlayerId == Healer()?.PlayerId
                              && !PlayerControl.LocalPlayer.Data.IsDead
                              && antidoteUsesLeft > 0,
                        () => PlayerControl.LocalPlayer.CanMove && antidoteTarget != null && !InMeeting(),
                        () => { },
                        sprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowRight,
                        __instance, KeyCode.G, false, UCLocalization.Tr("uc.ui.poisoner.button_antidote"));
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
                    if (PlayerControl.LocalPlayer.PlayerId != Healer()?.PlayerId) return;
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
                        PlayerControlFixedUpdatePatch.setPlayerOutline(antidoteTarget, HealPulseColor());
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
