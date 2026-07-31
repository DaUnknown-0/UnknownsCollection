// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * HunterFx - the Hunter's crew-visible transformation (Paket W2, Plan 4.11 + spec item 4).
 *
 * The Sheriff's promotion into the Hunter is meant to be SEEN by everyone the instant it happens -
 * unlike the Werewolf, this is not a toggled "form", it is a one-time, permanent costume change for
 * the rest of the round.
 *
 * ---------------------------------------------------------------------------------------------
 * WHY THIS IS A HAT AND NO LONGER A RENDERER SWAP (user decision 2026-07-31)
 * ---------------------------------------------------------------------------------------------
 * The first implementation hid the real cosmetics and played a hand-drawn idle/walk flipbook on a
 * child SpriteRenderer (UCCharacterSkin, deleted with this change - it had no callers left). That
 * mechanic has to re-implement by
 * hand everything a crewmate gets for free: light radius, walls, name tag height, morph/camo
 * interaction, every renderer AU re-enables behind its back. The Werewolf had already moved to the
 * other approach - a full-body CUSTOM HAT applied through TOR's own setLook pipeline - and the
 * Hunter now follows it:
 *
 *   - The costume is the "Monster Hunter" hat (UCHats). It is drawn to leave body, legs and visor
 *     UNCOVERED on purpose, so the crew still recognises "our sheriff" under the hat, coat, quiver
 *     and crossbow. That is the same design rule the flipbook followed - here it costs nothing,
 *     because the player's own colour simply stays visible instead of being faked with a tint.
 *   - setLook only touches the LOCAL cosmetics (Helpers.cs:367, RawSetColor/Visor/Hat/Pet), so
 *     "who may see the promotion" is a per-client decision with no RPC involved - which is exactly
 *     what the living-Werewolf carve-out below needs.
 *   - Everything vanilla (darkness, Chameleon, Morphling, camouflage, the name tag) keeps working,
 *     because the player IS still an ordinary crewmate wearing an ordinary hat.
 *
 * The look is re-applied from a per-frame guard (one string compare) rather than once: TOR's night
 * vision, a Camouflager ending and Morphling reverts all call setDefaultLook behind our back. While
 * a global camouflage or mushroom mixup is running the costume stays off entirely - overwriting it
 * would fight TOR's own restore, exactly as WerewolfFx does it.
 *
 * Option 1508 (Hunter.HatCostume) switches the whole costume off. Then the promotion is invisible,
 * the hat is an ordinary cosmetic anybody may wear (the UCHats hat lock reads the same option) and
 * the guess protection in Hunter.cs lapses with it - nobody can see who the Hunter is, so there is
 * nothing left to protect.
 */

using System;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class HunterFx {
        // Whom the costume is currently applied to ON THIS CLIENT. null = nobody is dressed up.
        private static PlayerControl lookOwner;
        private static bool lookWarned;

        static HunterFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // Touched once from Hunter.CreateOptions() so the static constructor (and therefore the UCFx
        // tick/reset registration) definitely runs before the first round - same idiom as
        // WerewolfFx.Init()/BeaconFx.Init().
        public static void Init() { }

        // Name kept from the flipbook era so Hunter.cs's driver reads the same: "is the costume on?"
        public static bool SkinAttached => lookOwner != null;

        // Host option 1508. Defaults to ON when the option does not exist yet (only ever hit by
        // callers that run before CreateOptions).
        public static bool CostumeEnabled() {
            try { return Hunter.HatCostume == null || Hunter.HatCostume.getBool(); }
            catch { return true; }
        }

        // ---- who gets to see it ----
        //
        // The beast must not be handed its target: on the LIVING Werewolf's own client the costume is
        // never put on, so for him the Hunter keeps walking around as the ordinary crewmate he was and
        // the wolf has to work out who is hunting it the same way the crew works out who the wolf is.
        // Dead werewolves are exempt - a ghost is shown every role anyway, hiding it there buys nothing.
        //
        // Deliberately written as a question about an ARBITRARY viewer, not just the local player: it
        // is computed from state every client has (Werewolf.werewolf plus the death flags), so every
        // client can answer it for every player. Hunter.cs's guess protection is built on exactly that -
        // it has to know whether somebody ELSE (a lover, a jackal) can see the Hunter.
        public static bool LookVisibleTo(PlayerControl viewer) {
            try {
                if (!CostumeEnabled()) return false;
                if (viewer == null || viewer.Data == null) return false;
                if (viewer.Data.IsDead) return true;                 // ghosts see everything
                if (!Werewolf.active || Werewolf.werewolf == null) return true;
                return Werewolf.werewolf.PlayerId != viewer.PlayerId;
            } catch {
                return true;   // a broken probe must never hide the promotion from the whole lobby
            }
        }

        private static bool HiddenFromLocalPlayer() => !LookVisibleTo(PlayerControl.LocalPlayer);

        // ---- applying the look ----

        public static void AttachSkin(PlayerControl player) {
            try {
                if (player == null || player.Data == null) return;
                if (!CostumeEnabled() || HiddenFromLocalPlayer()) return;
                lookOwner = player;
                ApplyHunterLook();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] AttachSkin failed: {e}");
                lookOwner = null;
            }
        }

        public static void DetachSkin() {
            var owner = lookOwner;
            lookOwner = null;
            if (owner == null) return;
            // While a global camouflage is running TOR owns every look - it restores the right one
            // when it ends, and writing here would only fight it.
            try { if (!GlobalCamoActive()) owner.setDefaultLook(); } catch { }
        }

        // Own name, OWN COLOUR (the whole point: the crew keeps recognising their sheriff) and the
        // Monster Hunter hat. Visor and skin are cleared the way the Werewolf clears its own: the hat
        // sprite is drawn over a bare bean, and a lobby visor or outfit would poke through the coat.
        // The PET is deliberately kept - it is a separate creature walking next to the player, not
        // part of the costume (the same call the flipbook mechanic made).
        private static void ApplyHunterLook() {
            try {
                if (lookOwner == null || lookOwner.Data == null || GlobalCamoActive()) return;
                var outfit = lookOwner.Data.DefaultOutfit;
                lookOwner.setLook(lookOwner.Data.PlayerName,
                                  outfit != null ? outfit.ColorId : 0,
                                  UCHats.HunterHatId, "", "",
                                  outfit != null ? outfit.PetId : "");
            } catch (Exception e) {
                if (lookWarned) return;
                lookWarned = true;
                UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] hunter look failed (logged once): {e}");
            }
        }

        private static bool WearsHunterHat() {
            try {
                var hp = lookOwner != null && lookOwner.cosmetics != null ? lookOwner.cosmetics.hat : null;
                return hp != null && hp.Hat != null && hp.Hat.ProductId == UCHats.HunterHatId;
            } catch { return true; }   // a broken probe must never turn into a per-frame setLook storm
        }

        private static bool GlobalCamoActive() {
            try { return Camouflager.camouflageTimer > 0f || Helpers.MushroomSabotageActive(); }
            catch { return false; }
        }

        private static void Tick() {
            try {
                if (lookOwner == null) return;

                // Owner gone/dead -> the costume comes off (Hunter.cs's death patches do the same;
                // this is the belt-and-suspenders half).
                if (lookOwner.Data == null || lookOwner.Data.Disconnected || lookOwner.Data.IsDead) {
                    DetachSkin();
                    return;
                }
                // The local player BECAME the living werewolf (or the host switched the costume off
                // mid-round): take it off again. The reverse case needs no code here - Hunter's
                // per-frame driver re-dresses him within a second.
                if (HiddenFromLocalPlayer()) { DetachSkin(); return; }

                // Re-apply guard: night vision, a camouflage ending or a morph revert rewrite the look
                // via setDefaultLook behind our back. One string compare per frame.
                if (!GlobalCamoActive() && !WearsHunterHat()) ApplyHunterLook();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Hunter] look tick: {e.Message}");
            }
        }

        private static void Clear() {
            try { DetachSkin(); } catch { }
            lookWarned = false;
        }
    }
}
