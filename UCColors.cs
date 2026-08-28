// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCColors - free colour slots, so the host can put a player into ANY colour, not just a palette
 * entry.
 *
 * HOW A FREE COLOUR IS POSSIBLE AT ALL
 * What travels over the network is an INDEX into Palette.PlayerColors, never an RGB value - that is
 * what RpcSetColor sends and what every client renders from. An arbitrary colour therefore cannot
 * be sent as a colour; it has to be a SLOT whose contents everybody agrees on. So this appends a
 * block of empty slots to the palette, one per possible player, and UCColorGrant fills a slot by
 * RPC before putting anybody in it. Every client with this mod writes the same RGB into the same
 * slot, so the index they all receive resolves to the same colour on all of them.
 *
 * THE SLOTS ARE DELIBERATELY NOT IN THE COLOUR PICKER
 * ----------------------------------------------------
 * They are host-assigned by design, so they have no business in a picker - and staying out of it
 * also steps around a live landmine in TOR:
 *
 *   TheOtherRoles/Modules/CustomColors.cs:224, inside PlayerTabEnablePatch.Postfix:
 *       if (pos < 0 || pos > chips.Length) continue;      // '>' where it must be '>='
 *       ColorChip chip = chips[pos];                      // chips[chips.Length] -> out of bounds
 *
 * That off-by-one cannot fire today, because TOR's ORDER list only ever holds 0..41 while
 * chips.Length is 42. Putting an appended colour index into ORDER is exactly what would arm it, and
 * an out-of-bounds read there is native - it would take the process, not throw. An earlier version
 * of this file did try to add its colour to ORDER; that code never actually ran (see Install below
 * for why), so this is a trap avoided rather than one already sprung. Do not add to ORDER.
 *
 * TOR's SECOND loop hides the slots for free - that one is written
 * `for (int j = ORDER.Count; j < chips.Length; j++)` and is correctly bounded.
 *
 * pickableColors is left alone for the same reason. TOR uses it to resolve a colour clash by
 * walking `(color + 1) % pickableColors`, and a free slot is not something a clash should hand out
 * by accident.
 *
 * EVERY CLIENT NEEDS THE MOD, AND THE HOST ENFORCES IT
 * A client without this mod has a shorter palette, so a slot index is past the end of its array. UC
 * blocks the game from STARTING in that case but not from JOINING, so the guard below runs in the
 * lobby and moves anybody off a custom slot the moment such a client is in the room.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TheOtherRoles.Modules;
using UnityEngine;

namespace UnknownsCollection {
    public static class UCColors {

        /// One slot per possible player, plus a little slack. A lobby holds 15.
        public const int SlotCount = 16;

        /// Name ids well clear of TOR's own block (50000 upwards, one per colour).
        private const int NameIdBase = 50900;

        /// First custom slot in Palette.PlayerColors, or -1 while none are installed.
        public static int Base { get; private set; } = -1;
        public static bool Installed => Base >= 0;

        /// True for a colour index that only exists because of this mod.
        public static bool IsCustom(int colour) => Installed && colour >= Base && colour < Base + SlotCount;

        /// What an unfilled slot looks like, and what the slots are reset to between lobbies.
        private static readonly Color32 Empty = new Color32(0x4A, 0x4A, 0x52, byte.MaxValue);

        /// Purpur, the colour this started as. Kept as a named preset for the host's list rather
        /// than a palette entry of its own - it is one hex value among all the others now.
        public static readonly Color32 Purpur = new Color32(0x9D, 0x00, 0xFF, byte.MaxValue);

        /// Where a player is moved when a custom slot becomes unusable.
        private static int fallbackIndex;

        // ================================================================================
        // Install
        // ================================================================================
        /*
         * CALLED DIRECTLY FROM THE PLUGIN, NOT AS A PATCH ON CustomColors.Load.
         *
         * That was the first attempt and it never ran once: TOR calls CustomColors.Load() inside
         * its OWN plugin Load (Main.cs:127), and this mod has a hard dependency on TOR, so TOR is
         * already fully loaded before PatchAll here can touch that method. A postfix on it is
         * registered for a call that happened minutes ago. The symptom was silent - no slots, no
         * log line, and the whole feature simply absent - which is exactly what a patch on an
         * already-past call looks like.
         *
         * The dependency that made the patch useless is the same one that makes a direct call
         * correct: by the time this runs, TOR's palette is built and ours goes on the end of it.
         * UC's own options rely on that ordering for the same reason.
         */
        public static void Install() {
                try {
                    if (Installed) return;                       // idempotent

                    // TOR's own idiom: pull the Il2Cpp arrays into managed lists, append, put back.
                    var names = Enumerable.ToList<StringNames>(Palette.ColorNames);
                    var colors = Enumerable.ToList<Color32>(Palette.PlayerColors);
                    var shadows = Enumerable.ToList<Color32>(Palette.ShadowColors);

                    fallbackIndex = NearestExisting(colors, Purpur);
                    Base = colors.Count;

                    for (int i = 0; i < SlotCount; i++) {
                        names.Add((StringNames)(NameIdBase + i));
                        colors.Add(Empty);
                        shadows.Add(Darker(Empty));
                    }

                    Palette.ColorNames = names.ToArray();
                    Palette.PlayerColors = colors.ToArray();
                    Palette.ShadowColors = shadows.ToArray();

                    // A name is required, not optional: TOR's ColorStringPatch answers GetString for
                    // every StringNames at or above 50000 out of its ColorStrings dictionary, and it
                    // does so with the INDEXER - an unregistered id in that range throws
                    // KeyNotFoundException instead of falling through.
                    var dict = AccessTools.Field(typeof(CustomColors), "ColorStrings")?.GetValue(null)
                               as Dictionary<int, string>;
                    if (dict == null) throw new Exception("CustomColors.ColorStrings not reachable");
                    for (int i = 0; i < SlotCount; i++) dict[NameIdBase + i] = "Custom";

                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[UCColors] {SlotCount} free colour slots installed at {Base}..{Base + SlotCount - 1}; "
                        + $"fallback is colour {fallbackIndex}. Deliberately NOT added to the colour picker.");
                } catch (Exception e) {
                    Base = -1;
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCColors] install failed: {e}");
                }
        }

        // ================================================================================
        // Filling a slot
        // ================================================================================
        /// Writes an RGB value into one slot. Called on EVERY client from UCColorGrant's RPC, so the
        /// index everybody receives resolves to the same colour everywhere.
        public static bool SetSlot(int slot, Color32 rgb) {
            if (!IsCustom(slot)) return false;
            try {
                var colors = Enumerable.ToList<Color32>(Palette.PlayerColors);
                var shadows = Enumerable.ToList<Color32>(Palette.ShadowColors);
                colors[slot] = rgb;
                shadows[slot] = Darker(rgb);
                Palette.PlayerColors = colors.ToArray();
                Palette.ShadowColors = shadows.ToArray();
                Refresh();
                return true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCColors] SetSlot({slot}) failed: {e}");
                return false;
            }
        }

        /// The first slot nobody is standing in. -1 when they are all taken.
        public static int FreeSlot() {
            if (!Installed) return -1;
            var used = new HashSet<int>();
            try {
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (p == null || p.Data == null || p.Data.Disconnected) continue;
                    used.Add(p.Data.DefaultOutfit.ColorId);
                }
            } catch { }
            for (int i = 0; i < SlotCount; i++) if (!used.Contains(Base + i)) return Base + i;
            return -1;
        }

        /// Among Us shades a crewmate with a second, darker tone per colour; TOR's own custom
        /// colours sit at roughly 60% of theirs, so a filled slot follows the same ratio instead of
        /// reading flatter than everything around it.
        public static Color32 Darker(Color32 c) =>
            new Color32((byte)(c.r * 0.60f), (byte)(c.g * 0.60f), (byte)(c.b * 0.60f), byte.MaxValue);

        /// Re-tints everyone already wearing a slot: a player's look is built from the palette when
        /// the colour is set, so a slot that changes afterwards needs its wearers rebuilt.
        private static void Refresh() {
            try {
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (p == null || p.Data == null || p.Data.Disconnected) continue;
                    if (!IsCustom(p.Data.DefaultOutfit.ColorId)) continue;
                    p.SetColor(p.Data.DefaultOutfit.ColorId);
                }
            } catch { }
        }

        /// The closest colour to `target` among the ones that exist without this mod, by plain
        /// squared RGB distance. Not a hand-picked constant: TOR adds colours between releases, and
        /// the right substitute is whatever is actually nearest in the palette we append to.
        private static int NearestExisting(List<Color32> existing, Color32 target) {
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < existing.Count; i++) {
                double dr = existing[i].r - target.r, dg = existing[i].g - target.g, db = existing[i].b - target.b;
                double d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // ================================================================================
        // Safety
        // ================================================================================
        /// True while every client in the room has this mod, and therefore a palette long enough to
        /// resolve a custom slot.
        public static bool Safe() {
            try { return TeslaVersionHandshake.EveryoneHasMod(); } catch { return false; }
        }

        /*
         * The guard. Runs on the host in the lobby: while anybody is missing the mod, nobody may be
         * left standing in a slot their client cannot resolve. Throttled to twice a second - this
         * walks the player list, and the situation it reacts to (somebody joining) does not need
         * frame precision.
         */
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        internal static class LobbyGuardPatch {
            private static float next;

            public static void Postfix() {
                try {
                    if (!Installed) return;
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (Time.time < next) return;
                    next = Time.time + 0.5f;
                    if (Safe()) return;

                    foreach (var p in PlayerControl.AllPlayerControls) {
                        if (p == null || p.Data == null || p.Data.Disconnected) continue;
                        if (!IsCustom(p.Data.DefaultOutfit.ColorId)) continue;

                        byte to = (byte)FreeColourFor(p);
                        p.RpcSetColor(to);
                        UnknownsCollectionPlugin.Logger?.LogInfo(
                            $"[UCColors] {p.Data.PlayerName} was moved off a custom colour to {to}: "
                            + "somebody in the lobby does not have this mod and could not render it.");
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCColors] lobby guard failed: {e}");
                }
            }
        }

        /*
         * Which colour the player gets instead. The nearest one by default, but this goes straight
         * to RpcSetColor and therefore past TOR's CheckColor prefix, which is what normally resolves
         * two players landing on the same colour - so the clash is resolved here instead.
         */
        private static int FreeColourFor(PlayerControl who) {
            int limit = Base > 0 ? Base : 1;                    // only colours everyone can resolve
            for (int step = 0; step < limit; step++) {
                int cand = (fallbackIndex + step) % limit;
                if (!IsTaken(cand, who)) return cand;
            }
            return fallbackIndex;
        }

        private static bool IsTaken(int colour, PlayerControl except) {
            try {
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (p == null || p.Data == null || p.Data.Disconnected) continue;
                    if (except != null && p.PlayerId == except.PlayerId) continue;
                    if (p.Data.DefaultOutfit.ColorId == colour) return true;
                }
            } catch { }
            return false;
        }

        /// Slots are per-lobby. Emptying them on join keeps a colour somebody was given in one lobby
        /// from turning up on a stranger in the next.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        internal static class ResetPatch {
            public static void Postfix() {
                try {
                    if (!Installed) return;
                    for (int i = 0; i < SlotCount; i++) SetSlot(Base + i, Empty);
                } catch { }
            }
        }
    }
}
