// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * WerewolfPawprints - in wolf form the beast leaves black paw prints for the Detective
 * (User 2026-10-02), instead of footprints in his colour.
 *
 * TOR's Detective footprints live in FootprintHolder (Objects/Footprint.cs): MakeFootprint takes a
 * pooled Footprint object (private nested class) and puts it on the player's spot, FootprintUpdate
 * recolours every active one every 0.1 s (player colour, grey when anonymous/camouflaged) and fades it.
 * Only the Detective's own client ever makes footprints, so this is the Detective's view only.
 *  - MakeFootprint postfix: the footprint just made (the last entry of _activeFootprints) is ours
 *    when its owner is the werewolf in wolf form; it gets the paw sprite. A pooled object reused for
 *    anybody else gets TOR's own sprite back.
 *  - FootprintUpdate postfix: ours are painted black, keeping TOR's fade (the alpha it just set).
 * Black stays black under camouflage and anonymous footprints: the beast is visible to everyone in
 * wolf form anyway, so the paws give nothing away that the eyes do not.
 * The paw sprite is werewolf_pawprint.png (100x100 at 600 ppu like TOR's Footprint.png); until it is
 * embedded TOR's own footprint shape is used, in black.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles.Objects;

namespace UnknownsCollection {
    public static class WerewolfPawprints {
        private static readonly Type footprintType = typeof(FootprintHolder).GetNestedType("Footprint", BindingFlags.NonPublic);
        private static readonly FieldInfo activeField = AccessTools.Field(typeof(FootprintHolder), "_activeFootprints");
        private static readonly FieldInfo rendererField = footprintType != null ? AccessTools.Field(footprintType, "Renderer") : null;
        private static readonly FieldInfo ownerField = footprintType != null ? AccessTools.Field(footprintType, "Owner") : null;

        // footprint objects (TOR's pooled managed instances) that currently show a paw
        private static readonly HashSet<object> paws = new HashSet<object>();
        private static Sprite torSprite;   // TOR's own footprint sprite, captured before the first swap
        private static bool warned;

        private static bool Ready => activeField != null && rendererField != null && ownerField != null;

        private static void Warn(string what) {
            if (warned) return;
            warned = true;
            UnknownsCollectionPlugin.Logger?.LogWarning($"[WerewolfPawprints] {what} - paws disabled, TOR's footprints stay as they are.");
        }

        [HarmonyPatch(typeof(FootprintHolder), nameof(FootprintHolder.MakeFootprint))]
        static class MakePatch {
            public static void Postfix(FootprintHolder __instance, PlayerControl player) {
                try {
                    if (!Ready) { Warn("TOR's footprint internals not found"); return; }
                    if (activeField.GetValue(__instance) is not IList list || list.Count == 0) return;
                    object print = list[list.Count - 1];
                    if (rendererField.GetValue(print) is not SpriteRenderer r || r == null) return;
                    if (torSprite == null && !paws.Contains(print)) torSprite = r.sprite;

                    bool wolf = Werewolf.active && Werewolf.wolfForm && Werewolf.werewolf != null
                                && player != null && player.PlayerId == Werewolf.werewolf.PlayerId;
                    if (wolf) {
                        paws.Add(print);
                        r.sprite = UCAssets.WerewolfPawprint ?? torSprite ?? r.sprite;
                    } else if (paws.Remove(print) && torSprite != null) {
                        r.sprite = torSprite;   // a reused pool object: TOR's shape again
                    }
                } catch (Exception e) { Warn(e.Message); }
            }
        }

        [HarmonyPatch(typeof(FootprintHolder), "FootprintUpdate")]
        static class UpdatePatch {
            public static void Postfix() {
                try {
                    if (paws.Count == 0 || !Ready) return;
                    foreach (var print in paws) {
                        if (rendererField.GetValue(print) is not SpriteRenderer r || r == null) continue;
                        var c = r.color;
                        if (c.a <= 0f) continue;   // faded or pooled
                        r.color = new Color(0.02f, 0.02f, 0.02f, c.a);
                    }
                } catch (Exception e) { Warn(e.Message); }
            }
        }

        // The pool objects belong to the holder of this round; a new round starts with a new holder.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch {
            public static void Postfix() => paws.Clear();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyPatch {
            public static void Postfix() => paws.Clear();
        }
    }
}
