// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * HunterFx - the Hunter's crew-visible transformation (Paket W2, Plan 4.11 + spec item 4).
 *
 * The Sheriff's promotion into the Hunter is meant to be SEEN by everyone the instant it happens -
 * unlike the Werewolf, this is not a toggled "form", it is a one-time, permanent costume change for
 * the rest of the round. Mechanically it is the exact same renderer-swap idea WerewolfFx pioneered in
 * Paket W1 (hide the real cosmetics, show a hand-drawn idle/walk flipbook on a child SpriteRenderer),
 * so instead of re-implementing it this file just instantiates the shared UCCharacterSkin the two
 * classes now both sit on top of (see UCCharacterSkin.cs).
 *
 * The one deliberate difference: the Hunter's body is TINTED with the player's own colour (multiplied
 * onto the sprite), so the crew still recognises "our sheriff" under the hunter's coat - the Werewolf
 * has none (there is only ever one beast, nothing to tell it apart from itself).
 */

using System;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class HunterFx {
        private const float SkinPpu = 180f; // same canvas/scale convention as the wolf skin (160 px source)

        private static readonly UCCharacterSkin skin =
            new UCCharacterSkin("Hunter", "hunter_skin_idle", 6, "hunter_skin_walk", 8, SkinPpu);

        static HunterFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // Touched once from Hunter.CreateOptions() so the static constructor (and therefore the UCFx
        // tick/reset registration) definitely runs before the first round - same idiom as
        // WerewolfFx.Init()/BeaconFx.Init().
        public static void Init() { }

        public static bool SkinAttached => skin.Attached;

        // How far the player's colour is pushed into the sprite. A single SpriteRenderer can only
        // multiply the WHOLE frame, hat/coat/crossbow included, so a full-strength tint would drown the
        // dark leather and the silver bolt in the player's colour. Lerping the multiplier from white
        // keeps the props readable while the light-grey crewmate body still clearly reads as "yours".
        private const float TintStrength = 0.6f;

        public static void AttachSkin(PlayerControl player) {
            try {
                Color tint = Color.white;
                var colorId = player?.Data?.DefaultOutfit?.ColorId ?? -1;
                var colors = Palette.PlayerColors;
                if (colorId >= 0 && colors != null && colorId < colors.Length)
                    tint = Color.Lerp(Color.white, colors[colorId], TintStrength);
                skin.Attach(player, tint);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] AttachSkin failed: {e}");
            }
        }

        public static void DetachSkin() => skin.Detach();

        private static void Tick() {
            try { skin.Tick(); } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[Hunter] skin tick: {e.Message}"); }
        }

        private static void Clear() {
            try { skin.Detach(); } catch { }
        }
    }
}
