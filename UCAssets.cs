// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCAssets - shared asset loaders for ALL Unknown's Collection roles.
 *
 * Button icons are OUR OWN embedded PNGs (drawn in the TOR comic-burst style, tinted with each role's
 * identity color: impostor roles red, Poltergeist violet, Scout teal, Siphoner cyan, the crew-side
 * Saboteur search button blue), so this brings its own sprite loader - TOR's
 * Helpers.loadSpriteFromResources only reads TOR's assembly.
 *
 * Sounds are headerless 2-channel signed 32-bit PCM LE @ 48 kHz (the historical raw-PCM format this mod
 * uses), synthesized offline (AssetGen tool). This is also the sole loader for tesla_warning and Glitch
 * now (previously duplicated in TeslaSound.cs/BugSound.cs; those are retired once their callers switch
 * to PlayTeslaWarning()/PlayBugGlitch()). PlayAt() adds distance attenuation (smoothstep falloff, full
 * volume within 4 units of the local player, silent by 22) plus simple stereo panning derived from the
 * world-space X difference, so world-anchored cues (door slam, explosion) get quieter AND move in the
 * stereo field with distance/direction.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace UnknownsCollection {
    public static class UCAssets {
        private static readonly Dictionary<string, Sprite> sprites = new();
        private static readonly Dictionary<string, AudioClip> clips = new();

        // ---- Button icons ----

        // Poltergeist (violet)
        public static Sprite ManifestIcon => GetSprite("UnknownsCollection.Resources.poltergeist_manifest.png", 115f);
        public static Sprite DoorIcon => GetSprite("UnknownsCollection.Resources.poltergeist_door.png", 115f);
        public static Sprite HandIcon => GetSprite("UnknownsCollection.Resources.poltergeist_hand.png", 115f);
        public static Sprite HexIcon => GetSprite("UnknownsCollection.Resources.poltergeist_hex.png", 115f);
        // Impostor roles (red)
        public static Sprite IllusionistRecordIcon => GetSprite("UnknownsCollection.Resources.illusionist_record.png", 115f);
        public static Sprite IllusionistPlaybackIcon => GetSprite("UnknownsCollection.Resources.illusionist_playback.png", 115f);
        public static Sprite ManiacBombIcon => GetSprite("UnknownsCollection.Resources.maniac_bomb.png", 115f);
        public static Sprite ManiacPassIcon => GetSprite("UnknownsCollection.Resources.maniac_pass.png", 115f);
        public static Sprite SaboteurSabotageIcon => GetSprite("UnknownsCollection.Resources.saboteur_sabotage.png", 115f);
        public static Sprite SaboteurTrapIcon => GetSprite("UnknownsCollection.Resources.saboteur_trap.png", 115f);
        public static Sprite SaboteurSelfLimpIcon => GetSprite("UnknownsCollection.Resources.saboteur_selflimp.png", 115f);
        public static Sprite SilencerIcon => GetSprite("UnknownsCollection.Resources.silencer_silence.png", 115f);
        // Crew (blue/teal/cyan)
        public static Sprite SaboteurSearchIcon => GetSprite("UnknownsCollection.Resources.saboteur_search.png", 100f);
        public static Sprite ScoutIcon => GetSprite("UnknownsCollection.Resources.scout_transparent.png", 115f);
        public static Sprite SiphonerIcon => GetSprite("UnknownsCollection.Resources.siphoner_drain.png", 115f);
        // Collector (gold) + Manipulator (red)
        public static Sprite CollectorIcon => GetSprite("UnknownsCollection.Resources.collector_collect.png", 115f);
        public static Sprite ManipulatorIcon => GetSprite("UnknownsCollection.Resources.manipulator_fake.png", 115f);
        // WORLD sprite (in-map object): 200 ppu -> the ~110 px crystal stands ~0.55 units tall.
        public static Sprite CollectorRelicSprite => GetSprite("UnknownsCollection.Resources.collector_relic.png", 200f);

        // ---- Kill-overlay sprites (UCKillOverlay; transparent parts, tinted/animated in code) ----
        public static Sprite OverlayCrewBody => GetSprite("UnknownsCollection.Resources.overlay_crew_body.png", 100f);
        public static Sprite OverlayCrewVisor => GetSprite("UnknownsCollection.Resources.overlay_crew_visor.png", 100f);
        public static Sprite OverlayBoltA => GetSprite("UnknownsCollection.Resources.overlay_bolt_a.png", 100f);
        public static Sprite OverlayBoltB => GetSprite("UnknownsCollection.Resources.overlay_bolt_b.png", 100f);
        public static Sprite OverlayConsole => GetSprite("UnknownsCollection.Resources.overlay_console.png", 100f);
        public static Sprite OverlayVial => GetSprite("UnknownsCollection.Resources.overlay_vial.png", 100f);
        public static Sprite OverlayShadow => GetSprite("UnknownsCollection.Resources.overlay_shadow.png", 100f);
        public static Sprite OverlayBomb => GetSprite("UnknownsCollection.Resources.overlay_bomb.png", 100f);
        public static Sprite OverlayBurst => GetSprite("UnknownsCollection.Resources.overlay_burst.png", 100f);
        public static Sprite OverlayWhite => GetSprite("UnknownsCollection.Resources.overlay_white.png", 100f);
        // TOR-role kill overlays (second wave)
        public static Sprite OverlayRevolver => GetSprite("UnknownsCollection.Resources.overlay_revolver.png", 100f);
        public static Sprite OverlayStar => GetSprite("UnknownsCollection.Resources.overlay_star.png", 100f);
        public static Sprite OverlayMuzzle => GetSprite("UnknownsCollection.Resources.overlay_muzzle.png", 100f);
        public static Sprite OverlayFangs => GetSprite("UnknownsCollection.Resources.overlay_fangs.png", 100f);
        public static Sprite OverlaySigil => GetSprite("UnknownsCollection.Resources.overlay_sigil.png", 100f);
        public static Sprite OverlayHat => GetSprite("UnknownsCollection.Resources.overlay_hat.png", 100f);
        public static Sprite OverlayKatana => GetSprite("UnknownsCollection.Resources.overlay_katana.png", 100f);
        public static Sprite OverlayReticle => GetSprite("UnknownsCollection.Resources.overlay_reticle.png", 100f);
        public static Sprite OverlayMask => GetSprite("UnknownsCollection.Resources.overlay_mask.png", 100f);
        public static Sprite OverlayRoleCard => GetSprite("UnknownsCollection.Resources.overlay_rolecard.png", 100f);
        public static Sprite OverlayClaw => GetSprite("UnknownsCollection.Resources.overlay_claw.png", 100f);
        public static Sprite OverlayWanted => GetSprite("UnknownsCollection.Resources.overlay_wanted.png", 100f);
        public static Sprite OverlayCoin => GetSprite("UnknownsCollection.Resources.overlay_coin.png", 100f);

        // ---- Animated button icon frames ----

        // 16-frame seamless loops generated by AssetGen (embedded under Resources/anim). Returns
        // null if ANY frame is missing, so callers (UCButtonAnim) cleanly fall back to the static
        // icon instead of playing a loop with holes. The ppu must match the static icon's.
        public static Sprite[] GetFrames(string baseName, float pixelsPerUnit = 115f) {
            var frames = new Sprite[16];
            for (int i = 0; i < frames.Length; i++) {
                frames[i] = GetSprite($"UnknownsCollection.Resources.anim.{baseName}_f{i:00}.png", pixelsPerUnit);
                if (frames[i] == null) return null;
            }
            return frames;
        }

        public static Sprite GetSprite(string path, float pixelsPerUnit) {
            string key = path + "_" + pixelsPerUnit;
            if (sprites.TryGetValue(key, out var cached) && cached != null) return cached;
            try {
                var tex = LoadTexture(path);
                if (tex == null) return null;
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), pixelsPerUnit);
                sprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
                sprites[key] = sprite;
                return sprite;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCAssets] sprite load failed ({path}): {e.Message}");
                return null;
            }
        }

        private static Texture2D LoadTexture(string path) {
            var asm = Assembly.GetExecutingAssembly();
            using Stream stream = asm.GetManifestResourceStream(path);
            if (stream == null) return null;
            var data = new byte[stream.Length];
            _ = stream.Read(data, 0, (int)stream.Length);
            var tex = new Texture2D(2, 2, TextureFormat.ARGB32, true);
            if (!ImageConversion.LoadImage(tex, data, false)) return null;
            tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            return tex;
        }

        // ---- Volume levels ----
        // Named steps so new cues can be assigned a sensible default without picking a fresh literal.
        // Existing literals that already matched one of these values were switched over 1:1 (no audible
        // change); values that don't cleanly map to a step (e.g. 0.7f/0.85f/0.9f outliers) were left as-is.
        public const float VolSoft = 0.6f;
        public const float VolStd = 0.8f;
        public const float VolLoud = 1.0f;

        // Stereo panning range in world units: at this distance from the local player on the X axis,
        // a PlayAt() cue pans fully to one ear. The AU camera never rotates, so world-X maps to screen-X.
        private const float PanRange = 6f;

        // ---- Sounds: Poltergeist ----

        // Tight falloff for ALL positional Poltergeist cues: with the default 4→22-unit curve they
        // were still at ~74 % volume 10 units away - effectively map-wide ("played globally / too
        // loud" in playtests). Haunt cues are meant to be heard around the haunt only, roughly within
        // vision range: full volume up to 2 units, silent by 9.
        private const float PolterFull = 2f;
        private const float PolterSilent = 9f;

        public static void PlayManifest(float volume = VolStd) => Play("poltergeist_manifest", volume);
        public static void PlayPoof(Vector2 at, float volume = 0.9f) => PlayAt("poltergeist_poof", at, volume, PolterFull, PolterSilent);
        public static void PlayDoorSlam(Vector2 at, float volume = VolLoud) => PlayAt("poltergeist_door", at, volume, PolterFull, PolterSilent);
        public static void PlayHex(float volume = 0.7f) => Play("poltergeist_hex", volume);
        public static void PlayGhostHand(float volume = VolSoft) => Play("poltergeist_hand", volume);

        // Position-bound variants of the cues above (distance-gated, per design decision: manifest-start,
        // hex-cast and ghost-hand-start are audible around the ghost/target position, not just locally).
        public static void PlayManifestAt(Vector2 at, float volume = VolStd) => PlayAt("poltergeist_manifest", at, volume, PolterFull, PolterSilent);
        public static void PlayHexAt(Vector2 at, float volume = 0.7f) => PlayAt("poltergeist_hex", at, volume, PolterFull, PolterSilent);
        public static void PlayGhostHandAt(Vector2 at, float volume = VolSoft) => PlayAt("poltergeist_hand", at, volume, PolterFull, PolterSilent);

        // New Poltergeist cues (hand release, hex expiring, manifest-kill reveal).
        public static void PlayPoltergeistHandStopAt(Vector2 at, float volume = VolSoft) => PlayAt("poltergeist_handstop", at, volume, PolterFull, PolterSilent);
        public static void PlayPoltergeistHexEndAt(Vector2 at, float volume = VolStd) => PlayAt("poltergeist_hexend", at, volume, PolterFull, PolterSilent);
        public static void PlayPoltergeistRevealAt(Vector2 at, float volume = VolLoud) => PlayAt("poltergeist_reveal", at, volume, PolterFull, PolterSilent);

        // ---- Sounds: role cues ----

        public static void PlayZap(Vector2 at, float volume = 0.9f) => PlayAt("saboteur_zap", at, volume);
        public static void PlayTrapSnap(Vector2 at, float volume = VolStd) => PlayAt("saboteur_trap", at, volume);
        public static void PlayFuse(Vector2 at, float volume = VolStd) => PlayAt("maniac_fuse", at, volume);
        public static void PlayExplosion(Vector2 at, float volume = VolLoud) => PlayAt("maniac_explosion", at, volume);
        public static void PlayShh(float volume = VolStd) => Play("silencer_silence", volume);
        public static void PlayCloneShimmer(Vector2 at, float volume = 0.7f) => PlayAt("illusionist_clone", at, volume);
        public static void PlayPoisonGurgle(float volume = VolStd) => Play("poisoner_poison", volume);
        public static void PlayScoutWhoosh(Vector2 at, float volume = 0.7f) => PlayAt("scout_whoosh", at, volume);
        public static void PlaySiphonerDrain(float volume = VolSoft) => Play("siphoner_drain", volume);
        public static void PlayWitnessSting(float volume = VolStd) => Play("witness_sting", volume);
        public static void PlayShadeVanish(float volume = 0.7f) => Play("shade_vanish", volume);
        public static void PlayFollowerShift(float volume = VolStd) => Play("follower_shift", volume);
        public static void PlayCopycatLearn(float volume = 0.7f) => Play("copycat_learn", volume);

        public static void PlayRelicPickup(Vector2 at, float volume = 0.7f) => PlayAt("collector_pickup", at, volume);
        public static void PlayCollectorWin(float volume = 0.85f) => Play("collector_win", volume);
        public static void PlayManipulatorWarp(float volume = 0.7f) => Play("manipulator_warp", volume);

        // Burning-fuse loop (seamless clip): started for the bomb carrier, stopped on pass/explode.
        // Returns the looping AudioSource so callers can escalate the loop (volume/pitch ramp toward
        // the explosion) without reaching into the loader; null if the clip/SoundManager is missing.
        public static AudioSource PlayFuseLoop(float volume = 0.7f) {
            try {
                var source = Play("maniac_fuse", volume);
                if (source != null) source.loop = true;
                return source;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCAssets] fuse loop failed: {e.Message}");
                return null;
            }
        }
        public static void StopFuseLoop() {
            try {
                var clip = GetClip("maniac_fuse");
                if (clip != null) SoundManager.Instance?.StopSound(clip);
            } catch { }
        }

        // ---- Sounds: Tesla ----
        // tesla_warning was previously a standalone loader (TeslaSound.cs); consolidated here since its
        // embedded LogicalName already matches this class's standard "UnknownsCollection.Resources.<name>.raw"
        // convention (verified against the csproj - no path deviation needed).
        public static void PlayTeslaWarning(float volume = VolStd) => Play("tesla_warning", volume);
        public static void PlayTeslaPromote(float volume = VolStd) => Play("tesla_promote", volume);
        public static void PlayTeslaPulse(float volume = VolSoft) => Play("tesla_pulse", volume);
        public static void PlayTeslaDischargeAt(Vector2 at, float volume = VolLoud) => PlayAt("tesla_discharge", at, volume);
        public static void PlayTeslaSelect(float volume = VolSoft) => Play("tesla_select", volume);

        // ---- Sounds: Illusionist / Copycat ----
        public static void PlayIllusionistUnravelAt(Vector2 at, float volume = VolStd) => PlayAt("illusionist_unravel", at, volume);
        public static void PlayIllusionistDenyAt(Vector2 at, float volume = VolStd) => PlayAt("illusionist_deny", at, volume);
        public static void PlayIllusionistRecord(float volume = VolSoft) => Play("illusionist_record", volume);
        public static void PlayCopycatWard(float volume = VolStd) => Play("copycat_ward", volume);
        public static void PlayCopycatMiss(float volume = VolSoft) => Play("copycat_miss", volume);

        // ---- Sounds: Maniac / Poisoner / Silencer ----
        public static void PlayManiacPassAt(Vector2 at, float volume = VolStd) => PlayAt("maniac_pass", at, volume);
        public static void PlayManiacPlant(float volume = VolSoft) => Play("maniac_plant", volume);
        public static void PlayPoisonerAntidote(float volume = VolStd) => Play("poisoner_antidote", volume);
        public static void PlaySilencerMark(float volume = VolSoft) => Play("silencer_mark", volume);

        // ---- Sounds: Saboteur (scan/defuse minigame + misc cues) ----
        public static void PlaySaboteurMark(float volume = VolSoft) => Play("saboteur_mark", volume);
        public static void PlaySaboteurScanHit(float volume = VolSoft) => Play("saboteur_scanhit", volume);
        public static void PlaySaboteurScanMiss(float volume = VolSoft) => Play("saboteur_scanmiss", volume);
        public static void PlaySaboteurSafe(float volume = VolStd) => Play("saboteur_safe", volume);
        public static void PlaySaboteurAlarm(float volume = VolStd) => Play("saboteur_alarm", volume);
        public static void PlaySaboteurWireWrong(float volume = VolSoft) => Play("saboteur_wirewrong", volume);
        public static void PlaySaboteurDefused(float volume = VolStd) => Play("saboteur_defused", volume);

        // ---- Sounds: Crew-side (Shade / Witness / Siphoner) ----
        public static void PlayShadeRevealAt(Vector2 at, float volume = VolStd) => PlayAt("shade_reveal", at, volume);
        public static void PlayWitnessNote(float volume = VolStd) => Play("witness_note", volume);
        public static void PlaySiphonerStop(float volume = VolSoft) => Play("siphoner_stop", volume);

        // ---- Sounds: Collector / Manipulator / Beacon ----
        public static void PlayCollectorReady(float volume = VolStd) => Play("collector_ready", volume);
        public static void PlayCollectorChannel(float volume = VolSoft) => Play("collector_channel", volume);
        public static void PlayManipulatorEnd(float volume = VolStd) => Play("manipulator_end", volume);
        public static void PlayBeaconShare(float volume = VolSoft) => Play("beacon_share", volume);

        // ---- Sounds: Bug ----
        // Glitch was previously a standalone loader (BugSound.cs) with no Play wrapper at all;
        // consolidated here (its LogicalName "UnknownsCollection.Resources.Glitch.raw" already matches
        // this class's standard convention - verified against the csproj, no path deviation needed).
        public static void PlayBugGlitch(float volume = VolSoft) => Play("Glitch", volume);

        // ---- Sounds: UI / meta ----
        public static void PlayUcReveal(float volume = VolStd) => Play("uc_reveal", volume);

        // Plays a clip locally (no distance/positioning) and returns the AudioSource so callers can
        // further configure it (e.g. loop, or feed it into PlayAt's panning below).
        private static AudioSource Play(string name, float volume) {
            try {
                var clip = GetClip(name);
                if (clip == null || SoundManager.Instance == null) return null;
                return SoundManager.Instance.PlaySound(clip, false, volume);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCAssets] Play {name} failed: {e.Message}");
                return null;
            }
        }

        // World-anchored cue: full volume within fullDist units of the local player, fading to silent
        // by silentDist units using a smoothstep curve (feels more present up close, more natural
        // falloff than a linear ramp). Defaults keep the historical 4→22 curve for public events
        // (explosion, zap, ...); cues that should only be heard around their source pass a tighter
        // pair (see the Poltergeist constants below). Also derives simple stereo panning from the
        // world-space X difference between the cue and the local player (the AU camera never rotates,
        // so world-X maps directly to screen-X). Defensive against a missing local player and a missing
        // AudioSource (e.g. SFX disabled).
        private static AudioSource PlayAt(string name, Vector2 at, float volume,
                                          float fullDist = 4f, float silentDist = 22f) {
            try {
                float vol = volume;
                Vector2? localPos = null;
                var local = PlayerControl.LocalPlayer;
                if (local != null) {
                    Vector2 lp = local.GetTruePosition();
                    localPos = lp;
                    float d = Vector2.Distance(lp, at);
                    float k = Mathf.Clamp01(1f - (d - fullDist) / Mathf.Max(0.01f, silentDist - fullDist));
                    vol *= k * k * (3f - 2f * k); // smoothstep
                }
                if (vol <= 0.02f) return null;
                var src = Play(name, vol);
                if (src != null && localPos.HasValue) {
                    src.panStereo = Mathf.Clamp((at.x - localPos.Value.x) / PanRange, -1f, 1f);
                }
                return src;
            } catch {
                return null;
            }
        }

        private static AudioClip GetClip(string name) {
            if (clips.TryGetValue(name, out var cached) && cached != null) return cached;
            var clip = LoadRawClip($"UnknownsCollection.Resources.{name}.raw", name);
            clips[name] = clip;
            return clip;
        }

        // Raw (headerless) 2-channel signed 32-bit PCM (LE), 48 kHz - the format all UC sound assets use,
        // including tesla_warning and Glitch now that they're loaded through this same central cache.
        private static AudioClip LoadRawClip(string path, string clipName) {
            try {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using Stream stream = assembly.GetManifestResourceStream(path);
                if (stream == null) return null;
                var bytes = new byte[stream.Length];
                _ = stream.Read(bytes, 0, (int)stream.Length);
                float[] samples = new float[bytes.Length / 4];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (float)BitConverter.ToInt32(bytes, i * 4) / int.MaxValue;
                AudioClip clip = AudioClip.Create(clipName, samples.Length / 2, 2, 48000, false);
                clip.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
                clip.SetData(samples, 0);
                return clip;
            } catch {
                return null;
            }
        }
    }
}
