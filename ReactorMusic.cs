// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * ReactorMusic (Paket R) - a score for the one sabotage that can kill the whole lobby.
 *
 * The reactor meltdown (Skeld/Mira/Fungle/Airship "Reactor") and the seismic stabilizers (Polus
 * "Laboratory") are THE SAME game system - one ReactorSystemType, a different SystemTypes key per
 * map - so this is ONE implementation, not two. The detection is not rebuilt here either: it reuses
 * Poltergeist.ActiveReactorSystem(), the probe that role already needs for its ghost hand.
 *
 * WHY THIS FEATURE AT ALL, AND WHY IT IS SAFE
 * -------------------------------------------
 * Music leaks information - that is why the mod has no "an impostor is nearby" sting. The reactor is
 * the one exception: the sabotage announces itself with a full-screen vanilla alarm to every player
 * at the same moment, so a soundtrack cannot tell anybody anything they do not already know. And
 * because ICriticalSabotage.Countdown is publicly READABLE (TOR reads it itself,
 * EndGamePatch.cs:470-471; only the setter is non-public), the music can be written against the real
 * clock instead of guessing - the blast in reactor_boom LANDS on the explosion.
 *
 * THE TIMELINE (all four numbers are properties of the audio files, verified from their Ogg granule
 * positions: 3.2 s / 16.0 s / 9.5 s / 4.0 s)
 * -------------------------------------------------------------------------------------------------
 *   t = 0.0 s              reactor_intro (3.2 s file). Its musical body is 2.0 s long; the remaining
 *                          1.2 s are pure ring-out.
 *   t = 2.0 s              the loop starts - ALWAYS at exactly +2.0 s, never "when the intro file is
 *                          over". The intro body ends on the downbeat at 2.0 s, so starting the loop
 *                          there keeps one unbroken 120 BPM grid; waiting for the ring-out would put
 *                          the loop 1.2 s (2.4 beats) off the grid. UCMusic is a SINGLE channel, so
 *                          the intra-cue clip switch hard-cuts the ring-out - that is the intended
 *                          trade: a clean grid beats a decaying tail nobody consciously hears under
 *                          the vanilla klaxon.
 *   ... 16 s loop ...      one of six variants (reactor_music, reactor_music2..6). All six are
 *                          120 BPM and F minor, which is exactly why ONE intro and ONE finale fit
 *                          all of them.
 *   Countdown <= 8.0 s     the finale takes over: reactor_boom (9.5 s file). Its musical build is
 *                          8.0 s and the blast onset sits at 7.98 s, i.e. 0.02 s BEFORE the countdown
 *                          reaches zero - the blast is the explosion. The remaining ~1.5 s of the
 *                          file are the blast's decay.
 *   crew fixes it          reactor_fixed (4.0 s all-clear), then the cue is released.
 *
 * At the shortest configurable sabotage duration (UTS SabotageTuning: 10-90 s, default 30) the maths
 * closes exactly: 2.0 s intro body + 8.0 s finale = 10.0 s, no loop needed at all. That is why the
 * implementation must NEVER assume 30 s - it reads the live Countdown every frame.
 *
 * HOW EVERY CLIENT HEARS THE SAME VARIANT WITHOUT AN RPC
 * -----------------------------------------------------
 * The variant is not rolled with a random number generator, it is DERIVED from state that is already
 * identical on every client:
 *   round seed = AmongUsClient.Instance.GameId  XOR  a hash of the impostor PlayerIds
 *   variant    = mix(round seed, index of this reactor sabotage within the round) % 6
 * GameId is the lobby's game id (TOR reads it too, GameStartManagerPatch.cs:41), the vanilla impostor
 * assignment is server-authoritative and never changes during a round, and the sabotage INDEX is a
 * local counter over an event every client observes identically (the same argument that lets the
 * Pelican keep its swallow list without an RPC). The impostor hash is folded in with XOR so the
 * iteration order of AllPlayerControls cannot matter, and the seed is captured ONCE at the end of the
 * intro cutscene so a mid-round disconnect can never move it. Cost: zero bytes on the wire.
 *
 * WHY THE PATCHES LOOK LIKE THEY DO
 *  - The driver is a HudManager.Update POSTFIX with Priority.High so our Request lands BEFORE
 *    UCMusic's own Update postfix arbitrates in the same frame (UCMusic.cs:229) - otherwise every
 *    clip change would be one frame late, which is visible at the +2.0 s downbeat.
 *  - Nothing is played through UCAssets.Play*: the whole score runs on UCMusic (cue "reactor",
 *    priority 100 - the highest in the mod, see WEREWOLF_PLAN.md §11.2), so it displaces the
 *    werewolf and pelican beds (priority 50) and hands the channel back to them afterwards with
 *    their playback position preserved.
 *  - "It exploded" vs "the crew saved it" cannot be read off the system AFTER the fact (both paths
 *    leave Countdown at 10000 and IsActive false), so the last countdown value seen while the
 *    sabotage was still live is what decides which finale keeps playing.
 *
 * Option: 1483 (General tab, default OFF - user decision). No RPC of its own. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class ReactorMusic {
        // ---- Option (1483) ----
        public static CustomOption Enabled;

        // ---- Channel ----
        private const string Cue = "reactor";
        private const int Priority = 100;      // highest in the mod: a meltdown outranks everything
        private const float Volume = 0.6f;     // same bed level the pelican hunt uses

        // ---- Clip geometry (seconds; measured from the Ogg granule positions of the assets) ----
        private const float IntroFileSecs = 3.2f;   // 2.0 s body + 1.2 s ring-out
        private const float LoopStartSecs = 2.0f;   // the loop ALWAYS starts here (see the header)
        private const float BoomFileSecs = 9.5f;    // 8.0 s build, blast onset 7.98 s, ~1.5 s decay
        private const float BoomLeadSecs = 8.0f;    // switch to the finale at Countdown <= 8.0
        private const float BlastOnsetSecs = 7.98f; // the hit itself: 0.02 s before Countdown hits 0
        private const float FixedFileSecs = 4.0f;   // all-clear
        private const int Variants = 6;             // reactor_music + reactor_music2..6

        // A countdown this low when the sabotage went inactive means it BLEW UP (the host ends the
        // round at Countdown < 0). Anything above it is a crew fix. Deliberately tight: a fix landing
        // in the last third of a second is indistinguishable from the blast anyway.
        private const float ExplodedEpsilon = 0.35f;

        private enum Phase { Idle, Live, Tail }

        // ---- Runtime state (all derived from synced data -> identical on every client) ----
        private static Phase phase = Phase.Idle;
        private static float cueStart;        // Time.time when the sabotage was first seen
        private static bool boomArmed;        // the finale has taken over and never goes back
        private static float boomStart;       // Time.time when reactor_boom started
        private static float lastCountdown;   // last value read while the sabotage was still live
        private static bool cueDone;          // finale finished while the sabotage is STILL live
        private static string tailClip;       // finale that outlives the sabotage (boom decay / fixed)
        private static float tailUntil;
        private static int variant;
        private static int sabotageIndex;     // how many reactor sabotages this round has seen
        private static uint roundSeed;

        // One clip decode per frame right after the sabotage starts. A 16 s stereo 48 kHz asset
        // becomes a ~6 MB float buffer, and NVorbis decodes it synchronously on the main thread - so
        // the touch is deliberately moved into the first frames, where the vanilla alarm and the
        // full-screen flash mask a hitch, instead of onto the +2.0 s downbeat or the finale switch.
        private static readonly List<string> warmQueue = new();

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                // General tab ("TOR Settings"): this is not a role setting, it applies to every round
                // regardless of who is playing what. isHeader + an explicit heading so the mod's own
                // block is labelled instead of borrowing the option name as a headline.
                // The English name is deliberately specific ("Reactor Music"): UCLocalization matches
                // options by their pristine ENGLISH text across all uc.* keys, so a generic label
                // would silently re-translate unrelated strings elsewhere in the mod.
                Enabled = CustomOption.Create(1483, Types.General, "Reactor Music",
                    false, null, true, null, "Unknown's Collection");
                UnknownsCollectionPlugin.Logger?.LogInfo("[ReactorMusic] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[ReactorMusic] CreateOptions failed: {e}");
            }
        }

        // Every module gets exactly one TryPatch call from UnknownsCollectionPlugin.Load(). This one
        // has no reflection work and no RPC channel to register (see the header: the variant needs no
        // wire traffic), so it only exists to keep that single registration shape intact.
        public static void TryPatch(Harmony harmony) { }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static bool MusicEnabled() {
            try { return Enabled != null && Enabled.getBool(); } catch { return false; }
        }

        private static bool InRound() =>
            AmongUsClient.Instance != null && AmongUsClient.Instance.IsGameStarted
            && ShipStatus.Instance != null && PlayerControl.LocalPlayer != null;

        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;

        private static string LoopClipName() =>
            variant <= 0 ? "reactor_music" : $"reactor_music{variant + 1}";

        // The active reactor/seismic system as ICriticalSabotage, or null. The system LOOKUP is
        // Poltergeist's (Reactor + Laboratory, IsActive checked there); the cast mirrors TOR's own
        // EndGamePatch.cs:470 - ICriticalSabotage is where the readable Countdown lives.
        private static ICriticalSabotage ActiveCritical() {
            try {
                var sys = Poltergeist.ActiveReactorSystem();
                if (sys == 0 || ShipStatus.Instance == null) return null;
                if (!ShipStatus.Instance.Systems.ContainsKey(sys)) return null;
                return ShipStatus.Instance.Systems[sys].TryCast<ICriticalSabotage>();
            } catch {
                return null;
            }
        }

        // ====================================================================
        // Variant selection (no RPC - see the file header)
        // ====================================================================

        // Captured once per round, from data that cannot change afterwards. Folding the impostor ids
        // in with XOR makes the result independent of the iteration order of AllPlayerControls.
        private static void ComputeRoundSeed() {
            uint h = 2166136261u; // FNV offset basis, just a non-zero start
            try {
                if (AmongUsClient.Instance != null) h ^= (uint)AmongUsClient.Instance.GameId;
            } catch { }
            try {
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (p == null || p.Data == null || p.Data.Role == null) continue;
                    if (!p.Data.Role.IsImpostor) continue;
                    uint x = (uint)(p.PlayerId + 1) * 2654435761u;
                    x ^= x >> 15;
                    h ^= x;
                }
            } catch { }
            roundSeed = h == 0u ? 1u : h;
        }

        private static int PickVariant(int index) {
            if (roundSeed == 0u) ComputeRoundSeed(); // freeplay / intro patch never fired
            uint s = roundSeed ^ ((uint)index * 0x9E3779B1u);
            s ^= s >> 16; s *= 0x7feb352du;
            s ^= s >> 15; s *= 0x846ca68bu;
            s ^= s >> 16;
            return (int)(s % Variants);
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(HarmonyLib.Priority.Low)]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    // Same anchor the Pelican uses for its host-authoritative pick: by the time the
                    // intro cutscene is gone, every client has the full, final role assignment.
                    ComputeRoundSeed();
                    sabotageIndex = 0;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[ReactorMusic] seed failed: {e.Message}");
                }
            }
        }

        // ====================================================================
        // Per-frame driver
        // ====================================================================

        // Priority.High so this postfix runs BEFORE UCMusic's own HudManager.Update postfix in the
        // same frame - a Request that arrives after the arbitration tick would take effect one frame
        // late, which is audible exactly where it must not be (the +2.0 s downbeat, the finale cut).
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        [HarmonyPriority(HarmonyLib.Priority.High)]
        static class HudUpdatePatch {
            public static void Postfix() {
                try { Tick(); } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[ReactorMusic] tick failed: {e}");
                    StopCue();
                }
            }
        }

        private static void Tick() {
            // A meeting or an exile screen ends the score outright. UCMusic.StopAll already silences
            // the channel on MeetingHud.Start, but WE have to stop asking for it - otherwise the very
            // next frame would start it again. (A critical sabotage blocks meetings in vanilla, so
            // this is a guard, not an everyday path.)
            if (!MusicEnabled() || !InRound() || InMeeting()) { StopCue(); return; }

            WarmUpStep();

            var crit = ActiveCritical();
            if (crit != null) {
                if (cueDone) return;                       // this sabotage already played its finale
                if (phase != Phase.Live) StartCue();
                TickLive(crit);
                return;
            }

            // The sabotage is over. Which finale keeps playing was decided by the last countdown we
            // saw while it was still live (see the header - afterwards both outcomes look alike).
            if (phase == Phase.Live) EnterTail();
            cueDone = false;

            if (phase == Phase.Tail) {
                if (Time.time >= tailUntil) { StopCue(); return; }
                UCMusic.Request(Cue, tailClip, Priority, Volume,
                                Mathf.Max(0f, tailUntil - Time.time), false);
            }
        }

        private static void TickLive(ICriticalSabotage crit) {
            float countdown;
            try { countdown = crit.Countdown; } catch { countdown = float.MaxValue; }
            lastCountdown = countdown;

            // The finale arms ONCE and never disarms: from here the music is on rails towards the
            // blast at +7.98 s. A countdown that jumps back up (a partial fix, another mod retuning
            // the sabotage) must not rewind the finale mid-bar.
            if (!boomArmed && countdown <= BoomLeadSecs) {
                boomArmed = true;
                boomStart = Time.time;
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[ReactorMusic] Finale armed at {countdown:F2}s remaining - blast lands in {BlastOnsetSecs:F2}s "
                    + $"(i.e. {countdown - BlastOnsetSecs:F2}s before the meltdown).");
            }

            string clip;
            bool loop;
            float remaining;

            if (boomArmed) {
                float played = Time.time - boomStart;
                if (played > BoomFileSecs) {
                    // The whole 9.5 s file has run out and the sabotage is STILL live (only reachable
                    // on a client waiting for the host's end-game broadcast). Release instead of
                    // letting UCMusic restart the boom when the finished AudioSource is recycled.
                    // Order matters: StopCue() clears the latch, so it is set AFTERWARDS - otherwise
                    // the very next frame would start the whole score over on the same sabotage.
                    StopCue();
                    cueDone = true;
                    return;
                }
                clip = "reactor_boom";
                loop = false;
                remaining = Mathf.Max(0f, BoomFileSecs - played);
            } else if (Time.time - cueStart < LoopStartSecs) {
                // Intro. Not looped: it is a one-shot whose body ends on the downbeat at +2.0 s.
                clip = "reactor_intro";
                loop = false;
                remaining = Mathf.Max(0f, countdown);
            } else {
                clip = LoopClipName();
                loop = true;
                remaining = Mathf.Max(0f, countdown);
            }

            // secondsRemaining is what UCMusic arbitrates ties with - handing it the REAL countdown
            // (rather than the clip length) is what lets a reactor with a hard deadline win the
            // channel over an open-ended werewolf/pelican loop.
            UCMusic.Request(Cue, clip, Priority, Volume, remaining, loop);
        }

        private static void StartCue() {
            phase = Phase.Live;
            cueStart = Time.time;
            boomArmed = false;
            boomStart = 0f;
            cueDone = false;
            lastCountdown = float.MaxValue;
            variant = PickVariant(sabotageIndex);
            sabotageIndex++;

            // Warm the clips this sabotage can still need, one per frame (see warmQueue). The intro
            // is not in here: UCMusic decodes it in this very frame anyway.
            warmQueue.Clear();
            warmQueue.Add(LoopClipName());
            warmQueue.Add("reactor_boom");
            warmQueue.Add("reactor_fixed");

            UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[ReactorMusic] Reactor sabotage #{sabotageIndex} - music variant {variant + 1}/{Variants} (seed {roundSeed}).");
        }

        // The sabotage went inactive. Both outcomes leave the system looking identical, so the last
        // live countdown decides: practically zero -> it detonated, keep the boom's blast decay
        // running into the end screen (UCMusic.StopAll on AmongUsClient.OnGameEnd cuts it); anything
        // else -> the crew got there in time, play the 4 s all-clear.
        private static void EnterTail() {
            bool exploded = boomArmed && lastCountdown <= ExplodedEpsilon;
            if (exploded) {
                float played = Mathf.Max(0f, Time.time - boomStart);
                tailClip = "reactor_boom";
                tailUntil = Time.time + Mathf.Max(0f, BoomFileSecs - played);
            } else {
                tailClip = "reactor_fixed";
                tailUntil = Time.time + FixedFileSecs;
            }
            phase = Phase.Tail;
            UnknownsCollectionPlugin.Logger?.LogInfo(
                exploded ? "[ReactorMusic] The reactor blew - riding out the blast."
                         : $"[ReactorMusic] Sabotage cleared at {lastCountdown:F1}s - playing the all-clear.");
        }

        private static void WarmUpStep() {
            if (warmQueue.Count == 0) return;
            string name = warmQueue[0];
            warmQueue.RemoveAt(0);
            try { UCAssets.GetClipByName(name); } catch { }
        }

        // ====================================================================
        // Teardown
        // ====================================================================

        // Releases the channel and forgets everything about the CURRENT sabotage. The round-level
        // state (seed, sabotage index) survives - that is ResetRound's job.
        private static void StopCue() {
            if (phase == Phase.Idle && warmQueue.Count == 0 && !cueDone) return;
            try { UCMusic.Release(Cue); } catch { }
            phase = Phase.Idle;
            boomArmed = false;
            boomStart = 0f;
            cueDone = false;
            tailClip = null;
            tailUntil = 0f;
            lastCountdown = float.MaxValue;
            warmQueue.Clear();
        }

        private static void ResetRound() {
            StopCue();
            sabotageIndex = 0;
            variant = 0;
            roundSeed = 0u;
        }

        // Round start. UCMusic patches resetVariables itself (StopAll), but the module has to drop
        // its own bookkeeping too, or the sabotage counter (and with it the variant sequence) would
        // continue across rounds. Nothing here touches a button or a cached HUD object, so the
        // documented "resetVariables runs AFTER HudManager.Start" pitfall does not apply.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { ResetRound(); }
        }

        // Belt and suspenders, same rule the rest of the mod adopted after the "resetVariables lobby
        // leak": state must never travel into a FOREIGN lobby.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinPatch {
            public static void Postfix() { ResetRound(); }
        }

        // The end screen. UCMusic.StopAll already runs here (its own OnGameEnd prefix), this only
        // makes sure we stop ASKING for the cue.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch {
            public static void Prefix() { ResetRound(); }
        }
    }
}
