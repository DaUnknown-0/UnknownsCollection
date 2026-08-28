// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCColors - one more player colour, Purpur (#9D00FF), and the fallback that keeps it from
 * breaking anybody who does not have this mod.
 *
 * WHY THE FALLBACK IS NOT OPTIONAL
 * --------------------------------
 * A player colour is an INDEX into Palette.PlayerColors, and that array is only as long as the
 * mods present made it: vanilla ends at 17, TOR's CustomColors appends its own up to 41, and this
 * file appends one more at 42. The index is what travels over the network - RpcSetColor sends a
 * byte - so a client whose array stops at 41 and is told "colour 42" indexes past the end of it in
 * a render path.
 *
 * UC's version handshake blocks the game from STARTING while somebody is missing the mod
 * (TeslaVersionHandshake.BeginGameGatePatch), but it does not stop them JOINING, and the colour is
 * already being rendered in the lobby. So the handshake alone does not cover this, and two things
 * have to happen the moment a client without the mod is in the room:
 *
 *   1. NOBODY MAY KEEP THE COLOUR. The host moves anyone sitting on it to the nearest colour that
 *      exists without this mod. The host is the right actor because colour assignment is already
 *      host-authoritative - TOR's own CheckColor prefix resolves clashes by calling RpcSetColor
 *      from there - so one decision reaches everyone and no two clients disagree.
 *   2. NOBODY MAY PICK IT. The chip disappears from the colour tab, by the mechanism TOR's own tab
 *      builder already has: PlayerTabEnablePatch positions the chips named in its private ORDER
 *      list and switches off every chip beyond it (scale 0, button disabled, listeners removed).
 *      Leaving our index out of ORDER therefore hides it, and putting it back shows it again.
 *
 * In the main menu there is no lobby to be unsafe in - AmongUsClient.Instance is null and
 * EveryoneHasMod() answers true - so the colour is pickable there as normal.
 *
 * WHY THE NAME HAS TO BE REGISTERED, NOT JUST THE COLOUR
 * TOR's ColorStringPatch answers TranslationController.GetString for every StringNames at or above
 * 50000 out of its ColorStrings dictionary, and it does so with the INDEXER: an id in that range
 * that nobody registered throws KeyNotFoundException instead of falling through. So the name goes
 * into TOR's own dictionary (it is protected, hence the reflection) rather than into a second
 * GetString patch of ours.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles.Modules;
using UnityEngine;

namespace UnknownsCollection {
    public static class UCColors {

        /// Purpur. The name id sits well clear of TOR's own block (50000 upwards, one per colour).
        private const int NameId = 50900;
        // Measured against the palette it joins: the nearest existing colour is TOR's Fuchsia
        // (#A41181) at a distance of 127 in RGB, with Lavender (#AD7EC9) next at 138 - far enough
        // apart to tell them on a crewmate. The shadow is the usual ~60% of the colour, which is the
        // ratio TOR's own custom colours keep.
        public static readonly Color32 Purpur = new Color32(0x9D, 0x00, 0xFF, byte.MaxValue);
        public static readonly Color32 PurpurShadow = new Color32(0x5E, 0x00, 0x99, byte.MaxValue);

        /// Index into Palette.PlayerColors once installed, or -1 while it is not.
        public static int Index { get; private set; } = -1;

        /// Where a player is moved when the colour becomes unsafe. Worked out once, from the
        /// palette as it exists WITHOUT this mod, so it stays right if TOR ever adds colours.
        private static int fallbackIndex = -1;

        // ================================================================================
        // Install
        // ================================================================================
        [HarmonyPatch(typeof(CustomColors), nameof(CustomColors.Load))]
        internal static class InstallPatch {
            public static void Postfix() {
                try {
                    if (Index >= 0) return;                        // Load can run more than once

                    // TOR's own idiom: pull the Il2Cpp arrays into managed lists, append, put back.
                    var names = Enumerable.ToList<StringNames>(Palette.ColorNames);
                    var colors = Enumerable.ToList<Color32>(Palette.PlayerColors);
                    var shadows = Enumerable.ToList<Color32>(Palette.ShadowColors);

                    fallbackIndex = NearestExisting(colors);

                    names.Add((StringNames)NameId);
                    colors.Add(Purpur);
                    shadows.Add(PurpurShadow);
                    Index = colors.Count - 1;

                    Palette.ColorNames = names.ToArray();
                    Palette.PlayerColors = colors.ToArray();
                    Palette.ShadowColors = shadows.ToArray();

                    // The name, into TOR's dictionary - see the header for why it must be there.
                    var f = AccessTools.Field(typeof(CustomColors), "ColorStrings");
                    var dict = f?.GetValue(null) as Dictionary<int, string>;
                    if (dict == null) throw new Exception("CustomColors.ColorStrings not reachable");
                    dict[NameId] = "Purpur";

                    // Count it among the pickable ones. TOR uses this both for the tab and for
                    // resolving colour clashes, so it has to grow with the palette.
                    CustomColors.pickableColors += 1;

                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[UCColors] Purpur installed as colour {Index}, fallback is colour {fallbackIndex}.");
                } catch (Exception e) {
                    Index = -1;
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCColors] install failed: {e}");
                }
            }
        }

        /*
         * Which colour the player actually gets. The nearest one by default, but this goes straight
         * to RpcSetColor and therefore past TOR's CheckColor prefix, which is what normally resolves
         * two players landing on the same colour. So the clash is resolved here instead: if the
         * nearest one is taken, walk forward through the colours that exist without this mod until
         * a free one turns up. Falling back to the nearest one anyway (rather than looping forever)
         * if the lobby is somehow full.
         */
        private static int FreeColourFor(PlayerControl who) {
            int limit = Index > 0 ? Index : 1;                  // only colours everyone can resolve
            for (int step = 0; step < limit; step++) {
                int cand = (fallbackIndex + step) % limit;
                if (!IsTaken(cand, who)) return cand;
            }
            return fallbackIndex;
        }

        private static bool IsTaken(int colour, PlayerControl except) {
            foreach (var p in PlayerControl.AllPlayerControls) {
                if (p == null || p.Data == null || p.Data.Disconnected) continue;
                if (except != null && p.PlayerId == except.PlayerId) continue;
                if (p.Data.DefaultOutfit.ColorId == colour) return true;
            }
            return false;
        }

        /// The closest colour to Purpur among the ones that exist without this mod, by plain squared
        /// RGB distance. Not a hand-picked constant: TOR adds colours between releases, and the right
        /// substitute is whatever is actually nearest in the palette we are appending to.
        private static int NearestExisting(List<Color32> existing) {
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < existing.Count; i++) {
                double dr = existing[i].r - Purpur.r, dg = existing[i].g - Purpur.g, db = existing[i].b - Purpur.b;
                double d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // ================================================================================
        // Safety
        // ================================================================================
        /// True while every client in the room has this mod - and therefore an array long enough to
        /// resolve the colour. Outside a lobby this is true, which is what makes the colour
        /// selectable from the main menu.
        private static bool Safe() {
            try { return TeslaVersionHandshake.EveryoneHasMod(); } catch { return false; }
        }

        /*
         * Hide or show the chip, by adding our index to TOR's ORDER list or taking it out again.
         *
         * A PREFIX, and deliberately at Priority.First: TOR lays the chips out in its own POSTFIX on
         * the same method, reading ORDER as it finds it. Editing ORDER afterwards would change
         * nothing until the tab is next opened.
         */
        [HarmonyPatch(typeof(PlayerTab), nameof(PlayerTab.OnEnable))]
        [HarmonyPriority(Priority.First)]
        internal static class ColorTabPatch {
            public static void Prefix() {
                try {
                    if (Index < 0) return;
                    var order = AccessTools.Field(typeof(CustomColors), "ORDER")?.GetValue(null) as List<int>;
                    if (order == null) return;

                    bool shouldShow = Safe();
                    bool shown = order.Contains(Index);
                    if (shouldShow && !shown) order.Add(Index);
                    else if (!shouldShow && shown) order.Remove(Index);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCColors] colour tab update failed: {e}");
                }
            }
        }

        /*
         * The fallback itself. Runs on the host in the lobby: while anybody is missing the mod,
         * nobody may be left sitting on a colour their client cannot resolve.
         *
         * Throttled to twice a second rather than run per frame - this walks the player list, and
         * the situation it reacts to (somebody joining) does not need frame precision.
         */
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        internal static class LobbyGuardPatch {
            private static float next;

            public static void Postfix() {
                try {
                    if (Index < 0) return;
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (Time.time < next) return;
                    next = Time.time + 0.5f;
                    if (Safe()) return;

                    foreach (var p in PlayerControl.AllPlayerControls) {
                        if (p == null || p.Data == null || p.Data.Disconnected) continue;
                        if (p.Data.DefaultOutfit.ColorId != Index) continue;

                        byte to = (byte)FreeColourFor(p);
                        p.RpcSetColor(to);
                        UnknownsCollectionPlugin.Logger?.LogInfo(
                            $"[UCColors] {p.Data.PlayerName} was moved off Purpur to colour {to}: "
                            + "somebody in the lobby does not have this mod and could not render it.");
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCColors] lobby guard failed: {e}");
                }
            }
        }
    }
}
