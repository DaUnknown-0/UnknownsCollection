// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Witness (Crewmate)
 *
 * A normal TOR Crewmate is silently promoted to "The Witness" at game start (host-authoritative pick,
 * broadcast via RPC 197). If the Witness is the SOLE living crewmate who SEES a kill happen (within
 * sight range AND with a clear line of sight - no wall between), the killer's identity is "noted on a
 * piece of paper":
 *
 *   - the killer's name glows red for the Witness (permanently through the game, or until the next
 *     meeting ends, depending on the option);
 *   - if the Witness DIES before the next meeting and their body is REPORTED, EVERYONE sees the note
 *     in chat: "I saw {killer} killing {victim}. I need to report this."
 *   - if the Witness SURVIVES to the first meeting after the sighting, they slip an ANONYMOUS note
 *     to a few random players: "I saw {killer} killing {victim}. Please do something."
 *     The recipients do NOT learn who the Witness is.
 *
 * Why host-authoritative? Deciding "sole crewmate to see it" needs every player's position + role +
 * line of sight, which only the host has. The host runs the sighting check on the kill, the report
 * reveal, and the random note hand-out, then broadcasts the results via RPC 197.
 *
 * Options live in the 1470-1474 block. See ID-Registry.md.
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
    public static class Witness {
        // ---- Theme ----
        public static readonly Color Color = new Color(0.85f, 0.75f, 0.45f); // parchment/amber (crew role tag)
        private static readonly Color RedName = new Color(1f, 0.18f, 0.18f);
        private const float BaseSight = 5f; // world units that "factor 1.0" maps to

        // ---- Options (IDs 1470-1475) ----
        public static CustomOption SpawnRate;        // 1470 (header) - crew role chance
        public static CustomOption SpawnMinPlayers;  // 1471 - minimum LOBBY players to spawn
        public static CustomOption SightFactor;       // 1472 - sight range factor (x BaseSight)
        public static CustomOption RedNamePermanent;  // 1473 - red name stays after the first meeting
        public static CustomOption NoteRecipients;    // 1474 - how many random players get the survive-note

        // ---- Runtime state ----
        public static PlayerControl witness;
        public static bool active;
        public static byte noteKillerId = byte.MaxValue; // the noted killer (none = MaxValue)
        public static byte noteVictimId = byte.MaxValue;
        private static bool revealed;        // killer publicly revealed via body report
        private static bool notesGiven;      // anonymous notes already handed out
        private static bool redNameExpired;  // (option) red name turned off after a meeting
        private static bool wasInMeeting;

        // Host-only: a pending body-report reveal (reporter, killer, victim) to flush at meeting start.
        private static byte pendingReporter = byte.MaxValue;

        // ---- Custom RPC (197) subtypes ----
        private const byte RpcId = 197; // == UnknownsCollectionPlugin.WitnessRpcId
        private const byte SubSetWitness = 0; // witnessId
        private const byte SubWitnessed = 1;  // killerId, victimId
        private const byte SubReveal = 2;     // reporterId, killerId, victimId  (public note)
        private const byte SubNote = 3;       // recipientId, killerId, victimId  (anonymous private note)

        // ---- Role identity ----
        private static RoleInfo witnessInfo;
        public static RoleInfo WitnessInfo() => witnessInfo ??= new RoleInfo(
            "Witness", Color, "Be the sole witness of a kill and expose the killer",
            "Be the sole witness of a kill and expose the killer", RoleId.Crewmate);

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1470, Types.Crewmate, "Witness",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1471, Types.Crewmate, "Witness Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                SightFactor = CustomOption.Create(1472, Types.Crewmate, "Witness Sight Range Factor",
                    1f, 0.5f, 2f, 0.25f, SpawnRate);
                RedNamePermanent = CustomOption.Create(1473, Types.Crewmate, "Killer Name Stays Red Permanently",
                    true, SpawnRate);
                NoteRecipients = CustomOption.Create(1474, Types.Crewmate, "Witness Note Recipients If Killer Survives",
                    3f, 1f, 8f, 1f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Witness] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Witness] CreateOptions failed: {e}");
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
        public static bool IsLocalWitness() =>
            witness != null && PlayerControl.LocalPlayer != null && witness.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static bool HasNote() => noteKillerId != byte.MaxValue;
        private static float Sight() => BaseSight * (SightFactor != null ? SightFactor.getFloat() : 1f);

        private static void AddLocalChat(PlayerControl source, string text) {
            try {
                var hud = HudManager.Instance;
                if (hud != null && hud.Chat != null && source != null) hud.Chat.AddChat(source, text);
            } catch { }
        }

        // Can `viewer` see world point `at`? (within sight range and a clear line of sight)
        private static bool CanSee(PlayerControl viewer, Vector2 at) {
            if (!IsAlive(viewer)) return false;
            Vector2 from = viewer.GetTruePosition();
            Vector2 dir = at - from;
            float mag = dir.magnitude;
            if (mag > Sight()) return false;
            if (mag < 0.05f) return true;
            return !PhysicsHelpers.AnyNonTriggersBetween(from, dir.normalized, mag, Constants.ShipAndObjectsMask);
        }

        // ====================================================================
        // Custom RPC senders (each applies locally too)
        // ====================================================================
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetWitness(byte id) {
            try {
                var w = BeginRpc(SubSetWitness);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetWitness(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Witness] SendSetWitness failed: {e}"); }
        }

        private static void SendWitnessed(byte killerId, byte victimId) {
            try {
                var w = BeginRpc(SubWitnessed);
                w.Write(killerId);
                w.Write(victimId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyWitnessed(killerId, victimId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Witness] SendWitnessed failed: {e}"); }
        }

        private static void SendReveal(byte reporterId, byte killerId, byte victimId) {
            try {
                var w = BeginRpc(SubReveal);
                w.Write(reporterId);
                w.Write(killerId);
                w.Write(victimId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyReveal(reporterId, killerId, victimId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Witness] SendReveal failed: {e}"); }
        }

        private static void SendNote(byte recipientId, byte killerId, byte victimId) {
            try {
                var w = BeginRpc(SubNote);
                w.Write(recipientId);
                w.Write(killerId);
                w.Write(victimId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyNote(recipientId, killerId, victimId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Witness] SendNote failed: {e}"); }
        }

        // ---- Appliers (run on every client) ----
        private static void ApplySetWitness(byte id) {
            witness = Helpers.playerById(id);
            active = witness != null;
            if (active) UCPromotion.Claim(id);
            noteKillerId = noteVictimId = byte.MaxValue;
            revealed = notesGiven = redNameExpired = false;
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Witness] The Witness is {witness.Data?.PlayerName}.");
        }

        private static void ApplyWitnessed(byte killerId, byte victimId) {
            if (HasNote()) return; // only the first witnessed kill sticks
            noteKillerId = killerId;
            noteVictimId = victimId;
            if (IsLocalWitness()) {
                var k = Helpers.playerById(killerId);
                AddLocalChat(witness, $"You witnessed {k?.Data?.PlayerName} kill {Helpers.playerById(victimId)?.Data?.PlayerName}. Their name is marked red.");
            }
        }

        private static void ApplyReveal(byte reporterId, byte killerId, byte victimId) {
            revealed = true;
            var reporter = Helpers.playerById(reporterId) ?? PlayerControl.LocalPlayer;
            string msg = $"I saw {Helpers.playerById(killerId)?.Data?.PlayerName} killing {Helpers.playerById(victimId)?.Data?.PlayerName}. I need to report this.";
            AddLocalChat(reporter, msg);
        }

        private static void ApplyNote(byte recipientId, byte killerId, byte victimId) {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.PlayerId != recipientId) return; // only the recipient sees their note
            string msg = $"(anonymous note) I saw {Helpers.playerById(killerId)?.Data?.PlayerName} killing {Helpers.playerById(victimId)?.Data?.PlayerName}. Please do something.";
            AddLocalChat(me, msg);
        }

        public static void MarkFromDraft(byte playerId) => ApplySetWitness(playerId);

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
                        case SubSetWitness: ApplySetWitness(reader.ReadByte()); break;
                        case SubWitnessed: { byte k = reader.ReadByte(); byte v = reader.ReadByte(); ApplyWitnessed(k, v); break; }
                        case SubReveal: { byte r = reader.ReadByte(); byte k = reader.ReadByte(); byte v = reader.ReadByte(); ApplyReveal(r, k, v); break; }
                        case SubNote: { byte rc = reader.ReadByte(); byte k = reader.ReadByte(); byte v = reader.ReadByte(); ApplyNote(rc, k, v); break; }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Witness] HandleRpc failed: {e}");
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
                witness = null;
                active = false;
                noteKillerId = noteVictimId = byte.MaxValue;
                revealed = notesGiven = redNameExpired = wasInMeeting = false;
                pendingReporter = byte.MaxValue;
            }
        }

        // ====================================================================
        // Game start: host picks the Witness among plain Crewmates and broadcasts it.
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
                    SendSetWitness(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Witness] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Sighting detection (host): on a kill, was the Witness the SOLE living crewmate who saw it?
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || HasNote() || !IsAlive(witness) || target == null) return;

                    Vector2 at = target.GetTruePosition();
                    var seers = new List<byte>();
                    foreach (var p in PlayerControl.AllPlayerControls) {
                        if (p == null || p.Data == null || p.Data.Role == null) continue;
                        if (p.Data.Role.IsImpostor) continue;       // killers/impostors don't count as witnesses
                        // Neutral roles (Jester, Jackal, Bug, Copycat, ...) aren't crewmates either - only
                        // a genuine crewmate may be the "sole crewmate witness" (mirrors UCPromotion's
                        // plain-crewmate check / RoleInfo.isNeutral, the project's standard crew/neutral split).
                        var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
                        if (info != null && info.isNeutral) continue;
                        if (!IsAlive(p)) continue;                  // the victim (now dead) is excluded
                        if (CanSee(p, at)) seers.Add(p.PlayerId);
                    }

                    // Sole crewmate witness == exactly the Witness, nobody else.
                    if (seers.Count == 1 && seers[0] == witness.PlayerId)
                        SendWitnessed(__instance.PlayerId, target.PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Witness] sighting check failed: {e}");
                }
            }
        }

        // ====================================================================
        // Body report (host): if the reported body is the noted killer, queue the public reveal.
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdReportDeadBody))]
        static class ReportPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] NetworkedPlayerInfo target) {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || !HasNote() || revealed || target == null) return;
                    if (target.PlayerId == witness.PlayerId) pendingReporter = __instance.PlayerId;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Witness] report check failed: {e}");
                }
            }
        }

        // ====================================================================
        // Meeting start (host): flush a pending reveal, or hand out anonymous notes if the killer lives.
        // ====================================================================
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || !HasNote()) return;

                    if (pendingReporter != byte.MaxValue && !revealed) {
                        SendReveal(pendingReporter, noteKillerId, noteVictimId);
                        pendingReporter = byte.MaxValue;
                        return;
                    }

                    // Witness survived to the meeting -> slip anonymous notes to random players.
                    if (!notesGiven && !revealed && IsAlive(witness)) {
                        notesGiven = true;
                        int n = NoteRecipients != null ? Mathf.RoundToInt(NoteRecipients.getFloat()) : 3;
                        var pool = PlayerControl.AllPlayerControls.ToArray()
                            .Where(p => IsAlive(p) && p.PlayerId != noteKillerId && p.PlayerId != witness.PlayerId)
                            .OrderBy(_ => rnd.Next()).Take(n).ToList();
                        foreach (var r in pool) SendNote(r.PlayerId, noteKillerId, noteVictimId);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Witness] meeting-start flush failed: {e}");
                }
            }
        }

        // ====================================================================
        // Red killer name for the Witness (in-game + meeting). Cleared after a meeting if not permanent.
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    bool nowMeeting = InMeeting();
                    if (wasInMeeting && !nowMeeting && (RedNamePermanent == null || !RedNamePermanent.getBool()))
                        redNameExpired = true;
                    wasInMeeting = nowMeeting;

                    if (!IsLocalWitness() || !HasNote() || redNameExpired || InMeeting()) return;
                    var killer = Helpers.playerById(noteKillerId);
                    if (killer != null && killer.cosmetics?.nameText != null)
                        killer.cosmetics.nameText.color = RedName;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Witness] HudUpdate failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
        static class MeetingUpdatePatch {
            public static void Postfix(MeetingHud __instance) {
                try {
                    if (!IsLocalWitness() || !HasNote() || redNameExpired || __instance == null) return;
                    foreach (var pva in __instance.playerStates) {
                        if (pva == null || pva.NameText == null) continue;
                        if ((byte)pva.TargetPlayerId == noteKillerId) pva.NameText.color = RedName;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Witness] MeetingUpdate failed: {e}");
                }
            }
        }

        // ====================================================================
        // Role identity: show the Witness as its own role over the Crewmate entry.
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || witness == null || p == null || p != witness || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = WitnessInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, WitnessInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Witness] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
