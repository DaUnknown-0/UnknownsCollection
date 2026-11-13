// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * BugCrash - the Bug's "System Crash" (User 2026-11-06: in a meeting the screen glitches, a blue screen for a few
 * seconds, then a minigame; decisions by question: triggered by the Bug, hits every living player, whoever fails the
 * reboot votes Skip automatically, the minigame is typing a flickering code. Discord is NOT touched).
 *
 *   - The Bug gets a CRASH button, once per game (option 1484 "Bug System Crash", default on). Using it arms the
 *     NEXT meeting; nobody is told who did it, the Bug gets a private confirmation.
 *   - Host: CrashDelayMin..Max seconds into that meeting it sends SubCrashStart with the reboot time (option 1485).
 *     Too little voting time left for the whole show: it waits for the next meeting instead.
 *   - Every living client plays BugCrashScreen: the real screen tears apart, a blue screen, then the reboot code.
 *   - Whoever does not type the code in time votes Skip (vanilla CmdCastVote with the skip id 253) as soon as the
 *     voting is open and as long as they have not voted yet. Talking stays allowed.
 */

using System;
using Hazel;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    internal static class BugCrash {
        public static CustomOption SystemCrash;   // 1484
        public static CustomOption RebootTime;    // 1485

        internal const byte SubCrashArm = 1;      // (no payload) Bug -> host
        internal const byte SubCrashStart = 2;    // reboot seconds, host -> everyone

        private const float CrashDelayMin = 6f, CrashDelayMax = 10f;
        private const float ShowSeconds = 1.6f + 3f + 2f;   // glitch + blue screen + result, on top of the reboot time
        private const byte SkipVote = 253;

        private static bool usedLocal;            // the local Bug pressed it this game
        private static bool armUsedHost;          // host: the Bug's one arm this game was spent (usedLocal is only local)
        private static bool armed;                // host: the next meeting crashes
        private static bool pending;              // host: this meeting crashes at crashAt
        private static float crashAt;
        private static float showUntil = -1f;     // host: the crash show (glitch, blue screen, reboot) runs until then

        /// Host: a system crash is on screen in this meeting right now.
        internal static bool ShowRunning => Time.time < showUntil && MeetingHud.Instance != null;
        private static bool skipOwed;             // local: failed the reboot, vote Skip once voting is open

        private static TheOtherRoles.Objects.CustomButton button;

        internal static void CreateOptions(CustomOption parent) {
            SystemCrash = CustomOption.Create(1484, Types.Neutral, "Bug System Crash", true, parent);
            RebootTime = CustomOption.Create(1485, Types.Neutral, "Bug Reboot Time", 10f, 6f, 20f, 1f, SystemCrash);
        }

        private static bool On() => SystemCrash == null || SystemCrash.getBool();
        private static float Reboot() => RebootTime != null ? RebootTime.getFloat() : 10f;
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;

        internal static void Reset() {
            usedLocal = armed = pending = skipOwed = armUsedHost = false;
            BugCrashScreen.Close();
        }

        // ---- RPC (Bug module 198, subtypes 1 and 2) ----
        internal static void Handle(byte subtype, MessageReader reader) {
            if (subtype == SubCrashArm) {
                if (UCRpc.RequireOwnerOrHost(Bug.bug, "Bug.CrashArm")) ApplyArm();
            } else if (subtype == SubCrashStart) {
                byte secs = reader.ReadByte();
                if (UCRpc.RequireHost("Bug.CrashStart")) ApplyStart(secs);
            }
        }

        private static void SendArm() {
            try {
                var w = UCRpc.Begin(Bug.ModuleRpcId);
                w.Write(SubCrashArm);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyArm();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Bug] SendCrashArm failed: {e}"); }
        }

        private static void SendStart(byte seconds) {
            try {
                showUntil = Time.time + seconds + ShowSeconds + 1f;
                var w = UCRpc.Begin(Bug.ModuleRpcId);
                w.Write(SubCrashStart);
                w.Write(seconds);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyStart(seconds);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Bug] SendCrashStart failed: {e}"); }
        }

        private static void ApplyArm() {
            if (!AmHost() || !On()) return;
            // Once per game and only from a living Bug, checked here too: the button enforces it only
            // on the Bug's own client.
            var bug = Bug.bug;
            if (armUsedHost || bug == null || bug.Data == null || bug.Data.IsDead) {
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] system crash arm ignored (already used or the Bug is dead).");
                return;
            }
            armUsedHost = true;
            armed = true;
            UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] system crash armed for the next meeting.");
        }

        private static void ApplyStart(byte seconds) {
            var me = PlayerControl.LocalPlayer;
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Bug] system crash: reboot {seconds} s.");
            if (me == null || me.Data == null || me.Data.IsDead || MeetingHud.Instance == null) return;
            BugCrashScreen.Play(seconds);
        }

        // Called by BugCrashScreen when the reboot fails or runs out.
        internal static void RebootFailed() => skipOwed = true;

        // By hand (UCEvents, Role Control): in a meeting at once, otherwise the next meeting. No Bug needed.
        internal static string ForceNow() {
            if (!AmHost()) return "Only the host can start the system crash.";
            var hud = MeetingHud.Instance;
            if (hud == null || ExileController.Instance != null) {
                armed = true;
                return "System crash armed: it hits the next meeting.";
            }
            if (hud.state == MeetingHud.VoteStates.Results || hud.state == MeetingHud.VoteStates.Proceeding)
                return "The voting is over, too late for this meeting.";
            if (BugCrashScreen.Active || ShowRunning) return "A system crash is already running.";
            if (Maniac.DefuseThisMeeting) {
                armed = true;
                return "A bomb defuse runs in this meeting: the system crash hits the next meeting.";
            }
            pending = false;
            SendStart((byte)Mathf.Clamp(Mathf.RoundToInt(Reboot()), 1, 60));
            return $"System crash started for every living player (reboot {Reboot():0} s).";
        }

        // ---- host: arm at the meeting start, fire a few seconds in ----
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                skipOwed = false;
                if (!AmHost() || !armed) return;
                armed = false;
                pending = true;
                crashAt = Time.time + UnityEngine.Random.Range(CrashDelayMin, CrashDelayMax);
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        static class MeetingClosePatch {
            public static void Postfix() {
                skipOwed = false;
                BugCrashScreen.Close();
                if (pending) { pending = false; armed = true; }   // the meeting ended before it fired: next one
            }
        }

        private static float MeetingSecondsLeft(MeetingHud hud) {
            try {
                var o = GameOptionsManager.Instance?.currentNormalGameOptions;
                if (o == null || o.VotingTime <= 0) return float.MaxValue;
                return o.DiscussionTime + o.VotingTime - hud.discussionTimer;
            } catch { return float.MaxValue; }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                // The button placement has its own guard: a failure there must not disarm a pending crash.
                try {
                    if (button != null && Bug.IsLocalBug()) UCButtonSlots.Place(button);   // a modifier may hold a slot
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] crash button placement failed: {e}");
                }
                try {
                    HostTick();
                    PayOwedSkip();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] crash tick failed: {e}");
                    pending = false;
                }
            }
        }

        private static void HostTick() {
            if (!pending || !AmHost() || Time.time < crashAt) return;
            var hud = MeetingHud.Instance;
            if (hud == null) return;
            if (hud.state == MeetingHud.VoteStates.Results || hud.state == MeetingHud.VoteStates.Proceeding) return;
            pending = false;
            // The bomb defuse goes first: it decides about a life and belongs to this meeting, the crash can wait for
            // the next one (User 2026-11-13: the two must not run at the same time).
            if (Maniac.DefuseThisMeeting) {
                armed = true;
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] system crash postponed: a bomb defuse runs in this meeting.");
                return;
            }
            float reboot = Reboot();
            if (MeetingSecondsLeft(hud) < reboot + ShowSeconds + 3f) {
                armed = true;   // not enough voting time for the whole show: the next meeting
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] system crash postponed: too little voting time left.");
                return;
            }
            SendStart((byte)Mathf.Clamp(Mathf.RoundToInt(reboot), 1, 60));
        }

        // TORMapOptions and MeetingHudPatch are internal to TOR: read the two values by reflection.
        private static System.Reflection.FieldInfo blockSkipField, meetingTargetField;
        private static bool skipFieldsResolved;
        private static bool SkipBlocked() {
            try {
                if (!skipFieldsResolved) {
                    skipFieldsResolved = true;
                    var asm = typeof(CustomOption).Assembly;
                    blockSkipField = asm.GetType("TheOtherRoles.TORMapOptions")?.GetField("blockSkippingInEmergencyMeetings",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    meetingTargetField = asm.GetType("TheOtherRoles.Patches.MeetingHudPatch")?.GetField("target",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                }
                if (!(blockSkipField?.GetValue(null) is bool block) || !block) return false;
                // TOR's rule applies to emergency meetings only (no reported body).
                return meetingTargetField == null || meetingTargetField.GetValue(null) == null;
            } catch { return false; }
        }

        // A failed reboot votes Skip once the voting is open, unless this player has voted already.
        private static void PayOwedSkip() {
            if (!skipOwed) return;
            var hud = MeetingHud.Instance;
            var me = PlayerControl.LocalPlayer;
            if (hud == null || me == null || me.Data == null || me.Data.IsDead) { skipOwed = false; return; }
            if (hud.state != MeetingHud.VoteStates.NotVoted && hud.state != MeetingHud.VoteStates.Voted) {
                if (hud.state == MeetingHud.VoteStates.Results || hud.state == MeetingHud.VoteStates.Proceeding) skipOwed = false;
                return;   // still in the discussion: wait
            }
            skipOwed = false;
            // TOR hides the Skip button in emergency meetings when "Block Skipping In Emergency Meetings"
            // is on; the forced vote must not get around that, so the player simply stays unvoted.
            if (SkipBlocked()) {
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] reboot failed, but skipping is blocked in this meeting: no vote cast.");
                return;
            }
            foreach (var pva in hud.playerStates) {
                if (pva == null || pva.TargetPlayerId != me.PlayerId) continue;
                if (pva.DidVote || pva.AmDead) return;
                break;
            }
            hud.CmdCastVote(me.PlayerId, SkipVote);
            UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] reboot failed: local vote Skip.");
        }

        // ---- the Bug's button ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix(HudManager __instance) {
                try {
                    var icon = UCAssets.BugCrashIcon
                               ?? Helpers.loadSpriteFromResources("TheOtherRoles.Resources.HackerButton.png", 115f);
                    button = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            if (usedLocal || !On()) return;
                            usedLocal = true;
                            SendArm();
                            try { Helpers.showFlash(Bug.Color, 1.2f, UCLocalization.Tr("uc.ui.bug.crash_armed")); } catch { }
                            UCAssets.PlayBugGlitch(UCAssets.VolSoft);
                        },
                        () => Bug.active && Bug.IsLocalBug() && On() && !usedLocal
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove && !InMeeting(),
                        () => { },
                        icon,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowRight,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.bug.button_crash"));
                    button.MaxTimer = 10f;
                    button.Timer = 10f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] crash button creation failed: {e}");
                }
            }
        }
    }
}
