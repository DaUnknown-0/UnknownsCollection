// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCButtonAnim - purely cosmetic animation driver for Unknown's Collection ability buttons.
 *
 * One effect, riding on the fact that TOR's CustomButton.Update() re-applies
 * `actionButtonRenderer.sprite = Sprite` every frame:
 *
 *  Flipbook: 16-frame seamless icon loops (AssetGen `anim/` output). Each tick simply sets
 *     `btn.Sprite` to the current frame; TOR pushes it to the renderer itself. Buttons are matched
 *     by sprite instance-id (the static icon AND every frame map to the same set), so no role file
 *     needs to register anything - and buttons of OTHER mods/TOR are never touched.
 *     The loop only plays while the ability is USABLE (design decision): on cooldown / mid-effect
 *     the icon holds frame 0, so motion itself signals "ready". Playback starts at frame 0 on the
 *     not-ready -> ready transition instead of jumping into a global clock.
 *
 *  The size pulse that used to ride along is gone for good (User 2026-10-04), with its client
 *  option. Buttons without frames simply stay static.
 *
 * Round-start safety (see the resetVariables lesson): this class keeps NO references to buttons
 * across rounds - per-button state is keyed by ActionButton instance-id and cleared on reset;
 * every tick null-checks before touching anything. Nothing gameplay-relevant is gated on it.
 */

using System.Collections.Generic;
using TheOtherRoles.Objects;
using UnityEngine;

namespace UnknownsCollection {
    public static class UCButtonAnim {
        private const float Fps = 14f;            // flipbook playback speed (16 frames -> ~1.14s loop)

        private static readonly int DesatId = Shader.PropertyToID("_Desat");

        private sealed class AnimSet { public Sprite[] frames; }

        // sprite instance-id -> animation set (static icon and all 16 frames map to the same set)
        private static Dictionary<int, AnimSet> frameMap;
        private static bool buildTried;

        // Per-ActionButton cosmetic state, keyed by instance-id. Buttons are re-instantiated every
        // round (HudManager.Start), so stale ids are simply dropped on reset.
        private static readonly Dictionary<int, float> readySince = new();  // Time.time of the not-ready -> ready flip

        static UCButtonAnim() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(() => readySince.Clear());
        }

        // Touched once from the plugin's Load() purely to force the static constructor (and thus
        // the RegisterTick/RegisterReset calls) to run before the first round - same pattern as
        // ManipulatorFx.Init().
        public static void Init() { }

        private static void Tick() {
            if (frameMap == null) {
                if (buildTried) return;
                buildTried = true;
                BuildMap();
                if (frameMap == null) return;
            }

            var buttons = CustomButton.buttons;
            if (buttons == null) return;

            for (int i = 0; i < buttons.Count; i++) {
                var btn = buttons[i];
                if (btn == null || btn.actionButton == null || btn.Sprite == null) continue;
                if (!frameMap.TryGetValue(btn.Sprite.GetInstanceID(), out var set)) continue;
                var go = btn.actionButtonGameObject;
                if (go == null || !go.activeSelf) continue; // hidden (meeting/dead/no role): skip

                int id = btn.actionButton.GetInstanceID();

                // "Usable" = cooldown elapsed, no active effect, and TOR did not desaturate it
                // (reusing TOR's own CouldUse verdict from its material instead of re-running the
                // role lambdas; worst case this lags one frame behind).
                bool ready = btn.Timer <= 0f && !btn.isEffectActive;
                if (ready && btn.actionButtonMat != null && btn.actionButtonMat.HasProperty(DesatId))
                    ready = btn.actionButtonMat.GetFloat(DesatId) < 0.5f;

                // 1. Flipbook - only while usable; otherwise hold the static frame 0. Playback is
                // clocked from the moment this button became ready, so it always starts at frame 0.
                if (ready) {
                    if (!readySince.TryGetValue(id, out float since)) { since = Time.time; readySince[id] = since; }
                    btn.Sprite = set.frames[(int)((Time.time - since) * Fps) % set.frames.Length];
                } else {
                    readySince.Remove(id);
                    btn.Sprite = set.frames[0];
                }
            }
        }

        private static void BuildMap() {
            // Every UC button icon with its AssetGen anim base name + pixels-per-unit (must match
            // the static icon's ppu in UCAssets, or the animated frames would change button size).
            var sources = new (Sprite statik, string baseName, float ppu)[] {
                (UCAssets.ManifestIcon,             "poltergeist_manifest", 115f),
                (UCAssets.DoorIcon,                 "poltergeist_door",     115f),
                (UCAssets.HandIcon,                 "poltergeist_hand",     115f),
                (UCAssets.HexIcon,                  "poltergeist_hex",      115f),
                (UCAssets.IllusionistRecordIcon,    "illusionist_record",   115f),
                (UCAssets.IllusionistPlaybackIcon,  "illusionist_playback", 115f),
                (UCAssets.ManiacBombIcon,           "maniac_bomb",          115f),
                (UCAssets.ManiacPassIcon,           "maniac_pass",          115f),
                (UCAssets.SaboteurSabotageIcon,     "saboteur_sabotage",    115f),
                (UCAssets.SaboteurTrapIcon,         "saboteur_trap",        115f),
                // saboteur_selflimp deliberately NOT animated.
                (UCAssets.SilencerIcon,             "silencer_silence",     115f),
                (UCAssets.SaboteurSearchIcon,       "saboteur_search",      100f),
                (UCAssets.ScoutIcon,                "scout_transparent",    115f),
                (UCAssets.SiphonerIcon,             "siphoner_drain",       115f),
                (UCAssets.CollectorIcon,            "collector_collect",    115f),
                (UCAssets.ManipulatorIcon,          "manipulator_fake",     115f),
                (UCAssets.WerewolfTransformIcon,    "werewolf_transform",   115f),
                (UCAssets.WerewolfRevertIcon,       "werewolf_revert",      115f),
                (UCAssets.HunterShootIcon,          "hunter_shoot",         115f),
                (UCAssets.PelicanSwallowIcon,       "pelican_swallow",      115f),
                (UCAssets.StalkerIcon,              "stalker_strike",       115f),
                (UCAssets.FakerIcon,                "faker_fakebody",       115f),
                (UCAssets.FakerSwapIcon,            "faker_swap",           115f),
            };

            var map = new Dictionary<int, AnimSet>();
            int sets = 0;
            foreach (var (statik, baseName, ppu) in sources) {
                if (statik == null) continue;                 // static icon missing -> nothing to match
                var frames = UCAssets.GetFrames(baseName, ppu);
                if (frames == null) continue;                 // frames missing -> button keeps static icon
                var set = new AnimSet { frames = frames };
                map[statik.GetInstanceID()] = set;
                foreach (var f in frames) map[f.GetInstanceID()] = set;
                sets++;
            }
            if (sets > 0) frameMap = map;
            UnknownsCollectionPlugin.Logger?.LogInfo($"[UCButtonAnim] animated icon sets loaded: {sets}/{sources.Length}");
        }
    }
}
