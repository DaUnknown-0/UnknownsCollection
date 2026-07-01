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
 * Sounds are headerless 2-channel signed 32-bit PCM LE @ 48 kHz (the exact format TeslaSound/BugSound
 * load), synthesized offline (AssetGen tool). PlayAt() adds simple distance attenuation relative to
 * the local player so world-anchored cues (door slam, explosion) get quieter with distance.
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

        // ---- Sounds: Poltergeist ----

        public static void PlayManifest(float volume = 0.8f) => Play("poltergeist_manifest", volume);
        public static void PlayPoof(Vector2 at, float volume = 0.9f) => PlayAt("poltergeist_poof", at, volume);
        public static void PlayDoorSlam(Vector2 at, float volume = 1.0f) => PlayAt("poltergeist_door", at, volume);
        public static void PlayHex(float volume = 0.7f) => Play("poltergeist_hex", volume);
        public static void PlayGhostHand(float volume = 0.6f) => Play("poltergeist_hand", volume);

        // ---- Sounds: role cues ----

        public static void PlayZap(Vector2 at, float volume = 0.9f) => PlayAt("saboteur_zap", at, volume);
        public static void PlayTrapSnap(Vector2 at, float volume = 0.8f) => PlayAt("saboteur_trap", at, volume);
        public static void PlayFuse(Vector2 at, float volume = 0.8f) => PlayAt("maniac_fuse", at, volume);
        public static void PlayExplosion(Vector2 at, float volume = 1.0f) => PlayAt("maniac_explosion", at, volume);
        public static void PlayShh(float volume = 0.8f) => Play("silencer_silence", volume);
        public static void PlayCloneShimmer(Vector2 at, float volume = 0.7f) => PlayAt("illusionist_clone", at, volume);
        public static void PlayPoisonGurgle(float volume = 0.8f) => Play("poisoner_poison", volume);
        public static void PlayScoutWhoosh(Vector2 at, float volume = 0.7f) => PlayAt("scout_whoosh", at, volume);
        public static void PlaySiphonerDrain(float volume = 0.6f) => Play("siphoner_drain", volume);
        public static void PlayWitnessSting(float volume = 0.8f) => Play("witness_sting", volume);
        public static void PlayShadeVanish(float volume = 0.7f) => Play("shade_vanish", volume);
        public static void PlayFollowerShift(float volume = 0.8f) => Play("follower_shift", volume);
        public static void PlayCopycatLearn(float volume = 0.7f) => Play("copycat_learn", volume);

        // Burning-fuse loop (seamless clip): started for the bomb carrier, stopped on pass/explode.
        public static void PlayFuseLoop(float volume = 0.7f) {
            try {
                var clip = GetClip("maniac_fuse");
                if (clip == null || SoundManager.Instance == null) return;
                var source = SoundManager.Instance.PlaySound(clip, false, volume);
                if (source != null) source.loop = true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCAssets] fuse loop failed: {e.Message}");
            }
        }
        public static void StopFuseLoop() {
            try {
                var clip = GetClip("maniac_fuse");
                if (clip != null) SoundManager.Instance?.StopSound(clip);
            } catch { }
        }

        private static void Play(string name, float volume) {
            try {
                var clip = GetClip(name);
                if (clip == null || SoundManager.Instance == null) return;
                SoundManager.Instance.PlaySound(clip, false, volume);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCAssets] Play {name} failed: {e.Message}");
            }
        }

        // World-anchored cue: full volume within 4 units of the local player, fading to silent at 22.
        private static void PlayAt(string name, Vector2 at, float volume) {
            try {
                float vol = volume;
                var local = PlayerControl.LocalPlayer;
                if (local != null) {
                    float d = Vector2.Distance(local.GetTruePosition(), at);
                    vol *= Mathf.Clamp01(1f - (d - 4f) / 18f);
                }
                if (vol <= 0.02f) return;
                Play(name, vol);
            } catch { }
        }

        private static AudioClip GetClip(string name) {
            if (clips.TryGetValue(name, out var cached) && cached != null) return cached;
            var clip = LoadRawClip($"UnknownsCollection.Resources.{name}.raw", name);
            clips[name] = clip;
            return clip;
        }

        // Raw (headerless) 2-channel signed 32-bit PCM (LE), 48 kHz - same loader as TeslaSound.
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
