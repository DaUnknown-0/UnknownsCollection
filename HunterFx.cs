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
        // 160 px source, same canvas convention as the wolf skin. History: 180 ppu (~0.89 units) read
        // as smaller than the crewmate it replaces (the drawn figure does not fill its frame), 150 ppu
        // (~1.07 units) was still only "a head taller". 100 ppu is exactly 150/1.5, i.e. 1.5x the
        // previous size (~1.6 units): the hunter towers over the crew he protects, without coming
        // anywhere near the beast (~2.2 units).
        private const float SkinPpu = 100f;

        // Where the drawn figure ends inside its frame, measured from the BOTTOM (DrawHunterSkin puts
        // the ground line at 0.90 and the hat crown at ~0.25 of the canvas, counted from the top).
        // Only used to lift the name tag clear of the hat - see UCCharacterSkin.
        private const float SkinContentTop = 0.77f;

        private static readonly UCCharacterSkin skin =
            new UCCharacterSkin("Hunter", "hunter_skin_idle", 6, "hunter_skin_walk", 8, SkinPpu,
                                contentTop: SkinContentTop);

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

        // The beast must not be handed its target. On the LIVING Werewolf's own client the costume is
        // never put on: for him the Hunter keeps walking around as the ordinary crewmate he was, and the
        // wolf has to work out who is hunting him the same way the crew works out who the wolf is.
        // Everyone else sees the promotion instantly - that is still the point of the role.
        // Dead werewolves are exempt: a ghost is shown every role anyway, hiding it there buys nothing.
        private static bool HiddenFromLocalPlayer() {
            try {
                if (!Werewolf.active || !Werewolf.IsLocalWerewolf()) return false;
                var me = PlayerControl.LocalPlayer;
                return me != null && me.Data != null && !me.Data.IsDead;
            } catch { return false; }
        }

        public static void AttachSkin(PlayerControl player) {
            try {
                if (HiddenFromLocalPlayer()) return;
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
            try {
                // Belt-and-suspenders half of HiddenFromLocalPlayer: should the costume ever be on when
                // the local werewolf is alive (a promotion racing the wolf assignment), take it off
                // again. The reverse case needs no code - once the wolf dies, Hunter's per-frame driver
                // re-dresses him within a second.
                if (skin.Attached && HiddenFromLocalPlayer()) { skin.Detach(); return; }
                skin.Tick();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[Hunter] skin tick: {e.Message}"); }
        }

        private static void Clear() {
            try { skin.Detach(); } catch { }
        }
    }
}
