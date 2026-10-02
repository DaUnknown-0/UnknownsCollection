// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * HypnotistVoteDiag - freeplay autotest for the Hypnotist's vote redirect (User 2026-10-02: "the
 * redirect of freeplay votes did not work"). Only active when the process was started with the
 * environment variable UC_DIAG_VOTE=1, so it can never run in a normal game.
 *
 * 10 s after the ship is up: local player -> Hypnotist (impostor faction), hypnotise the first
 * dummy, call an emergency meeting, pick another dummy as the redirect target, vote skip. Every
 * call of CheckForEndVoting and the final RpcVotingComplete are logged with each vote area's
 * VotedFor, so the log shows whether the redirect happened before the tally.
 */

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UnknownsCollection {
    public static class HypnotistVoteDiag {
        private static readonly bool On = Environment.GetEnvironmentVariable("UC_DIAG_VOTE") is "1" or "timeout" or "exileme";
        // "exileme": every dummy votes the local player, the local player votes himself (the case where
        // the meeting stayed in NotVoted after the count and was counted a second time).
        private static readonly bool ExileMe = Environment.GetEnvironmentVariable("UC_DIAG_VOTE") == "exileme";
        private static bool diagCasting, shotTaken;
        // "timeout": the victim dummy never votes and the local player does not vote either, so the
        // voting time has to run out (ForceSkipAll path of "Hypnotised Player Cannot Escape").
        private static readonly bool Timeout = Environment.GetEnvironmentVariable("UC_DIAG_VOTE") == "timeout";
        private static float at = -1f;
        private static int stage;
        private static byte victim = 255, target = 255;

        private static void Log(string s) => UnknownsCollectionPlugin.Logger?.LogInfo("[HypnotistVoteDiag] " + s);

        private static string Votes(MeetingHud m) {
            try {
                return string.Join(" ", m.playerStates.Select(ps => $"{ps.TargetPlayerId}:{(ps.DidVote ? ps.VotedFor.ToString() : "-")}{(ps.AmDead ? "d" : "")}"));
            } catch (Exception e) { return "?" + e.Message; }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                if (!On) return;
                try {
                    var lp = PlayerControl.LocalPlayer;
                    if (ShipStatus.Instance == null || lp == null) { at = -1f; stage = 0; return; }
                    if (at < 0f) { at = Time.realtimeSinceStartup + 10f; return; }
                    if (Time.realtimeSinceStartup < at) return;
                    var dummies = PlayerControl.AllPlayerControls.ToArray()
                        .Where(p => p != null && p.PlayerId != lp.PlayerId && p.Data != null && !p.Data.IsDead).ToList();
                    if (stage == 0) {
                        stage = 1;
                        RoleManager.Instance.SetRole(lp, AmongUs.GameOptions.RoleTypes.Impostor);
                        Hypnotist.SendSet(lp.PlayerId);
                        victim = dummies.Count > 0 ? dummies[0].PlayerId : (byte)255;
                        target = dummies.Count > 1 ? dummies[1].PlayerId : (byte)253;
                        Hypnotist.DiagHypnotize(victim);
                        Log($"Hypnotist set, victim {victim}, planned target {target}, {dummies.Count} dummies.");
                        try {
                            var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(MeetingHud), nameof(MeetingHud.CheckForEndVoting)));
                            if (info != null)
                                Log("CheckForEndVoting prefixes: " + string.Join(", ", info.Prefixes.Select(px => $"{px.owner}/{px.PatchMethod.DeclaringType?.Name} p{px.priority} i{px.index}")));
                        } catch (Exception e) { Log("patch info failed: " + e.Message); }
                        try {
                            int total = 0, nonDefault = 0;
                            var samples = new System.Collections.Generic.List<string>();
                            foreach (var m in Harmony.GetAllPatchedMethods()) {
                                var pi = Harmony.GetPatchInfo(m);
                                if (pi == null) continue;
                                foreach (var px in pi.Prefixes.Concat(pi.Postfixes).Concat(pi.Finalizers)) {
                                    total++;
                                    if (px.priority != Priority.Normal) { nonDefault++; if (samples.Count < 12) samples.Add($"{px.owner}/{px.PatchMethod.DeclaringType?.Name}.{px.PatchMethod.Name} p{px.priority}"); }
                                }
                            }
                            Log($"all patches: {total}, with non-default priority: {nonDefault}; e.g. {string.Join(", ", samples)}");
                            // order report: every method where at least two owners patch the same slot
                            var sb = new System.Text.StringBuilder();
                            foreach (var m in Harmony.GetAllPatchedMethods()) {
                                var pi = Harmony.GetPatchInfo(m);
                                if (pi == null) continue;
                                void Slot(string kind, System.Collections.ObjectModel.ReadOnlyCollection<Patch> list) {
                                    if (list.Select(x => x.owner).Distinct().Count() < 2) return;
                                    var ordered = list.OrderByDescending(x => x.priority).ThenBy(x => x.index);
                                    sb.AppendLine($"{m.DeclaringType?.Name}.{m.Name} [{kind}]: " +
                                        string.Join(" > ", ordered.Select(x => $"{x.owner.Split('.').Last()}/{x.PatchMethod.DeclaringType?.Name} p{x.priority}")));
                                }
                                Slot("prefix", pi.Prefixes); Slot("postfix", pi.Postfixes); Slot("finalizer", pi.Finalizers);
                            }
                            System.IO.File.WriteAllText(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "patch_order.txt"), sb.ToString());
                            Log("patch order report -> BepInEx/patch_order.txt");
                        } catch (Exception e) { Log("priority census failed: " + e.Message); }
                        at = Time.realtimeSinceStartup + 1f;
                    } else if (stage == 1) {
                        stage = 2;
                        lp.CmdReportDeadBody(null);
                        Log("emergency meeting requested.");
                        at = Time.realtimeSinceStartup + 6f;
                    } else if (stage == 2) {
                        if (MeetingHud.Instance == null) { Log("no meeting yet."); at = Time.realtimeSinceStartup + 2f; return; }
                        stage = 3;
                        var send = typeof(Hypnotist).GetMethod("SendPick", BindingFlags.NonPublic | BindingFlags.Static);
                        send?.Invoke(null, new object[] { target });
                        Log($"picked {target}; votes now {Votes(MeetingHud.Instance)}");
                        if (ExileMe) {
                            diagCasting = true;
                            foreach (var d in dummies) MeetingHud.Instance.CastVote(d.PlayerId, lp.PlayerId);
                            MeetingHud.Instance.CastVote(lp.PlayerId, lp.PlayerId);
                            diagCasting = false;
                            Log("exileme: everybody voted the local player.");
                        }
                        else if (!Timeout) { MeetingHud.Instance.CmdCastVote(lp.PlayerId, 253); Log("local vote: skip."); }
                        else Log("timeout mode: no local vote, victim blocked.");
                        at = Time.realtimeSinceStartup + 2f;
                    } else if (stage == 3) {
                        if (!shotTaken) { shotTaken = true; ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UC_vote_meeting.png")); }
                        // freeplay results wait for the Proceed button, so a finished count is enough
                        if (MeetingHud.Instance != null && MeetingHud.Instance.state != MeetingHud.VoteStates.Results
                            && MeetingHud.Instance.state != MeetingHud.VoteStates.Proceeding) { Log($"waiting, votes {Votes(MeetingHud.Instance)}, state {MeetingHud.Instance.state}"); at = Time.realtimeSinceStartup + 5f; return; }
                        stage = 4;
                        Log("count done.");
                        string shot = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UC_vote_diag.png");
                        ScreenCapture.CaptureScreenshot(shot);
                        at = Time.realtimeSinceStartup + 3f;
                    } else if (stage == 4) {
                        stage = 5;
                        Log("diag: done");
                    }
                } catch (Exception e) {
                    Log("failed: " + e);
                    stage = 5;
                }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CheckForEndVoting))]
        static class CheckPatch {
            private static string last;
            [HarmonyPriority(Priority.Last)]
            public static void Prefix(MeetingHud __instance) {
                if (!On) return;
                string v = Votes(__instance);
                if (v != last) { last = v; Log($"CheckForEndVoting (after all prefixes before it): {v}"); }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CastVote))]
        static class BlockVictimPatch {
            public static bool Prefix(byte srcPlayerId) {
                if (On && ExileMe && !diagCasting && srcPlayerId != PlayerControl.LocalPlayer?.PlayerId) return false;  // dummies wait for the diag
                if (!On || !Timeout || srcPlayerId != victim) return true;
                Log($"blocked the victim's own vote ({srcPlayerId}).");
                return false;
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.ForceSkipAll))]
        static class SkipAllPatch {
            public static void Prefix(MeetingHud __instance) { if (On) Log($"ForceSkipAll called; votes {Votes(__instance)}"); }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.RpcVotingComplete))]
        static class CompletePatch {
            public static void Prefix(MeetingHud __instance, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<MeetingHud.VoterState> states, NetworkedPlayerInfo exiled, bool tie) {
                if (!On) return;
                try {
                    string s = string.Join(" ", states.Select(st => $"{st.VoterId}->{st.VotedForId}"));
                    Log($"RpcVotingComplete: exiled {(exiled != null ? exiled.PlayerId.ToString() : "none")}, tie {tie}, states {s}; areas {Votes(__instance)}");
                    Log("RpcVotingComplete stack: " + Environment.StackTrace.Replace("\n", " | ").Substring(0, Math.Min(1500, Environment.StackTrace.Length)));
                } catch (Exception e) { Log("complete log failed: " + e.Message); }
            }
        }
    }
}
