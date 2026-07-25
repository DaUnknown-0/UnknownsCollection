// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    // ====================================================================================
    // UCMusic - the mod's single music channel.
    //
    // WHY: SoundManager.play simply layers clips on top of each other; there is no mixer.
    // As soon as more than one music track exists (werewolf form, pelican hunt, reactor),
    // two of them can be audible at once and the result is mush. This channel guarantees
    // that AT MOST ONE music cue plays at any time and arbitrates by priority.
    //
    // CONTRACT (see WEREWOLF_PLAN.md §11, decided with the user):
    //  - Callers call Request(cueId, clip, prio, ...) EVERY FRAME while their cue should be
    //    audible, and Release(cueId) when it ends. Request is idempotent: re-requesting the
    //    same clip only refreshes volume/remaining time and never restarts playback.
    //  - A clip change WITHIN a cue (reactor: loop -> finale) goes through the same Request
    //    with a different clip name and switches hard (sample-accurate starts matter there).
    //  - Priority: higher wins (reactor 100 > werewolf/pelican 50). At EQUAL priority the
    //    cue that ENDS EARLIER wins; a cue without a known end time never displaces one
    //    with an end time (an open-ended loop must not hog the channel).
    //  - A displaced cue is not cancelled: its playback position is remembered and restored
    //    when it wins the channel back (a reactor does not rewind the pelican hunt).
    //  - Crossfade ~0.35 s on channel switches; a hard cut reads as a bug, a long fade
    //    would soften the reactor's shock value.
    //
    // PITFALL guarded here: SoundManager.StopSound(clip) stops CLIP-based, not source-based.
    // The same clip must never run twice, and a still-fading source must be stopped HARD
    // before the same clip starts again (see StartClip).
    //
    // Hooks: HudManager.Update drives the tick; MeetingHud.Start and AmongUsClient.OnGameEnd
    // stop everything - no music survives a meeting or the end screen.
    // ====================================================================================
    public static class UCMusic {
        private const float CrossfadeSecs = 0.35f;
        // A cue whose owner stops calling Request without Release (death mid-frame, exception)
        // must not keep the channel forever - treat it as released after this grace window.
        private const float StaleSecs = 0.5f;

        private class Cue {
            public string id;
            public string clip;
            public int prio;
            public float volume;
            public bool loop;
            public bool hasEnd;
            public float endTime;       // Time.time when the cue expects to end (arbitration only)
            public float resumePos;     // seconds into the clip while displaced
            public float lastRequest;   // Time.time of the latest Request (stale detection)
        }

        private static readonly Dictionary<string, Cue> cues = new();

        private static Cue activeCue;
        private static AudioSource activeSource;
        private static string activeClipName;
        private static float activeFadeStart;   // fade-in start (Time.time)

        private static AudioSource fadingSource; // previous cue fading out
        private static string fadingClipName;
        private static float fadeOutStart;
        private static float fadeOutStartVolume;

        // ---- Public API ----------------------------------------------------------------

        // secondsRemaining: expected remaining play time of the cue (null = unknown/open loop).
        // Per-player mute switches (UC Options menu). Checked HERE rather than in each caller so a
        // muted cue can never slip through - and so a cue that is muted mid-play stops on the next
        // tick instead of running to its end.
        private static bool CueAudible(string cueId) {
            try {
                return cueId switch {
                    "werewolf_form" => UnknownsCollectionPlugin.MusicWerewolf?.Value ?? true,
                    "pelican_hunt"  => UnknownsCollectionPlugin.MusicPelican?.Value ?? true,
                    "reactor"       => UnknownsCollectionPlugin.MusicReactor?.Value ?? true,
                    _ => true
                };
            } catch { return true; }
        }

        public static void Request(string cueId, string clipName, int priority,
                                   float volume = 0.6f, float? secondsRemaining = null, bool loop = true) {
            try {
                if (string.IsNullOrEmpty(cueId) || string.IsNullOrEmpty(clipName)) return;
                if (!CueAudible(cueId)) {
                    // Muted: drop the cue entirely. Release() is safe on an unknown id and also stops
                    // playback if the switch was flipped while this cue was already running.
                    if (cues.ContainsKey(cueId)) Release(cueId);
                    return;
                }
                if (!cues.TryGetValue(cueId, out var cue)) {
                    cue = new Cue { id = cueId, resumePos = 0f };
                    cues[cueId] = cue;
                }
                if (cue.clip != null && cue.clip != clipName && cue == activeCue) {
                    // Intra-cue clip switch (reactor loop -> boom/fixed): hard cut, position resets -
                    // the new clip is a new timeline and its start is timing-critical.
                    StopActiveSource(hard: true);
                    cue.resumePos = 0f;
                }
                cue.clip = clipName;
                cue.prio = priority;
                cue.volume = volume;
                cue.loop = loop;
                cue.hasEnd = secondsRemaining.HasValue;
                cue.endTime = secondsRemaining.HasValue ? Time.time + secondsRemaining.Value : 0f;
                cue.lastRequest = Time.time;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCMusic] Request {cueId} failed: {e.Message}");
            }
        }

        public static void Release(string cueId) {
            try {
                if (cueId == null || !cues.TryGetValue(cueId, out var cue)) return;
                if (cue == activeCue) StopActiveSource(hard: false);
                cues.Remove(cueId);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCMusic] Release {cueId} failed: {e.Message}");
            }
        }

        public static void StopAll() {
            try {
                StopActiveSource(hard: true);
                if (fadingSource != null && fadingClipName != null) {
                    var clip = UCAssets.GetClipByName(fadingClipName);
                    if (clip != null) SoundManager.Instance?.StopSound(clip);
                }
                fadingSource = null;
                fadingClipName = null;
                cues.Clear();
            } catch { }
        }

        // ---- Arbitration tick ----------------------------------------------------------

        private static void Tick() {
            float now = Time.time;

            // Stale cues: owner stopped requesting without Release.
            foreach (var stale in cues.Values.Where(c => now - c.lastRequest > StaleSecs).ToList())
                Release(stale.id);

            // Fade-out bookkeeping for the displaced source.
            if (fadingSource != null) {
                float k = 1f - Mathf.Clamp01((now - fadeOutStart) / CrossfadeSecs);
                if (k <= 0f) {
                    var clip = fadingClipName != null ? UCAssets.GetClipByName(fadingClipName) : null;
                    if (clip != null) SoundManager.Instance?.StopSound(clip);
                    fadingSource = null;
                    fadingClipName = null;
                } else {
                    try { fadingSource.volume = fadeOutStartVolume * k * k; } catch { }
                }
            }

            // Pick the winner: highest priority; ties go to the cue that ends earlier, and a cue
            // without an end time sorts behind every cue that has one (float.MaxValue).
            Cue winner = null;
            foreach (var c in cues.Values) {
                if (winner == null) { winner = c; continue; }
                if (c.prio != winner.prio) { if (c.prio > winner.prio) winner = c; continue; }
                float cEnd = c.hasEnd ? c.endTime : float.MaxValue;
                float wEnd = winner.hasEnd ? winner.endTime : float.MaxValue;
                if (cEnd < wEnd) winner = c;
            }

            if (winner != activeCue) {
                if (activeCue != null) StopActiveSource(hard: false);
                if (winner != null) StartCue(winner);
            }

            if (activeCue != null && activeSource != null) {
                // Fade-in ramp + live volume updates from Request.
                float fin = Mathf.Clamp01((now - activeFadeStart) / CrossfadeSecs);
                try { activeSource.volume = activeCue.volume * (fin * fin * (3f - 2f * fin)); } catch { }
            } else if (activeCue != null && activeSource == null) {
                // Source died underneath us (SFX toggling, scene noise) - retry.
                StartCue(activeCue);
            }
        }

        private static void StartCue(Cue cue) {
            activeCue = cue;
            activeSource = StartClip(cue.clip, cue.loop, cue.resumePos);
            activeClipName = cue.clip;
            activeFadeStart = Time.time;
        }

        private static AudioSource StartClip(string clipName, bool loop, float resumePos) {
            try {
                var clip = UCAssets.GetClipByName(clipName);
                if (clip == null || SoundManager.Instance == null) return null;
                // StopSound is CLIP-based: if this very clip is still fading out from an earlier
                // displacement, stop it HARD first - the same clip must never run twice.
                if (fadingClipName == clipName) {
                    SoundManager.Instance.StopSound(clip);
                    fadingSource = null;
                    fadingClipName = null;
                }
                var src = SoundManager.Instance.PlaySound(clip, loop, 0f);
                if (src != null && resumePos > 0.01f && clip.length > 0.05f)
                    src.time = loop ? resumePos % clip.length : Mathf.Min(resumePos, clip.length - 0.01f);
                return src;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCMusic] StartClip {clipName} failed: {e.Message}");
                return null;
            }
        }

        // hard: stop immediately (StopAll / intra-cue switch); soft: remember the position and
        // crossfade out (displacement / Release of the active cue).
        private static void StopActiveSource(bool hard) {
            if (activeCue != null && activeSource != null) {
                try { activeCue.resumePos = activeSource.time; } catch { }
            }
            if (activeSource != null && activeClipName != null) {
                if (hard) {
                    var clip = UCAssets.GetClipByName(activeClipName);
                    if (clip != null) SoundManager.Instance?.StopSound(clip);
                } else {
                    // Only one source can be mid-fade; an older fade is cut off hard.
                    if (fadingSource != null && fadingClipName != null) {
                        var old = UCAssets.GetClipByName(fadingClipName);
                        if (old != null) SoundManager.Instance?.StopSound(old);
                    }
                    fadingSource = activeSource;
                    fadingClipName = activeClipName;
                    fadeOutStart = Time.time;
                    try { fadeOutStartVolume = activeSource.volume; } catch { fadeOutStartVolume = 0f; }
                }
            }
            activeCue = null;
            activeSource = null;
            activeClipName = null;
        }

        // ---- Hooks ---------------------------------------------------------------------

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                try { Tick(); } catch { }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() => StopAll();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch {
            public static void Prefix() => StopAll();
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => StopAll();
        }
    }
}
