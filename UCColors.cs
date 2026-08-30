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

        /*
         * WHAT THE SLOTS HOLD, AND WHO WAS PUT IN ONE - both for as long as this lobby lasts.
         *
         * Neither existed until 2026-08-30, and that was the bug: the palette was the only record
         * of a granted colour, and the palette is local to each client. A colour therefore
         * survived exactly as long as nobody cleared it, and something did clear it at every round
         * end (see ResetPatch). A player's colour INDEX lives in his player data and survives the
         * round; the RGB behind that index has to survive with it, or he comes back grey.
         */
        private static readonly Color32[] slotRgb = new Color32[SlotCount];
        private static readonly bool[] slotFilled = new bool[SlotCount];

        /*
         * PlayerId -> (slot, who), for every grant in this lobby. The host restores from this.
         *
         * WHY THE IDENTITY IS PART OF THE RECORD, and not just the id: Among Us REUSES PlayerIds.
         * A player who was granted a colour can leave and the next person to join takes his id, and
         * a record keyed on the id alone would then paint a stranger in a colour he never agreed to
         * - in a feature whose entire design is that a colour is only ever changed with consent.
         * So a record only counts while the person behind the id is still the same one: the friend
         * code where there is one, the name otherwise.
         */
        private struct Grant {
            public int Slot;
            public string Who;
        }

        private static readonly Dictionary<byte, Grant> grants = new Dictionary<byte, Grant>();

        /// Who a player is, for the record above. Friend code first: a name can be changed and can
        /// repeat, a friend code is the player.
        private static string Ident(PlayerControl p) {
            try {
                var d = p?.Data;
                if (d == null) return "";
                string fc = d.FriendCode;
                if (!string.IsNullOrEmpty(fc)) return "fc:" + fc;
                return "name:" + (d.PlayerName ?? "");
            } catch { return ""; }
        }

        /// The lobby the records above belong to. `AmongUsClient.GameId` is the lobby's own id.
        private static int lobbyId = int.MinValue;

        /// The RGB in a slot, if anything was ever written into it.
        public static bool TryGetSlot(int slot, out Color32 rgb) {
            rgb = Empty;
            if (!IsCustom(slot)) return false;
            int i = slot - Base;
            if (!slotFilled[i]) return false;
            rgb = slotRgb[i];
            return true;
        }

        /// Every slot that holds a colour, for the host's re-broadcast.
        public static IEnumerable<KeyValuePair<int, Color32>> FilledSlots() {
            if (!Installed) yield break;
            for (int i = 0; i < SlotCount; i++)
                if (slotFilled[i]) yield return new KeyValuePair<int, Color32>(Base + i, slotRgb[i]);
        }

        /// Called by UCColorGrant on the host the moment a grant goes through. The host also LEARNS
        /// the same mapping from whoever is wearing a slot (LobbyGuardPatch.Restore), which is what
        /// covers a host who took over the lobby after the colour was handed out.
        public static void RememberGrant(PlayerControl who, int slot) {
            if (who == null || !IsCustom(slot)) return;
            grants[who.PlayerId] = new Grant { Slot = slot, Who = Ident(who) };
        }

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
                slotRgb[slot - Base] = rgb;
                slotFilled[slot - Base] = true;
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
            private static int lastClients = -1;

            public static void Postfix() {
                try {
                    if (!Installed) return;
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (Time.time < next) return;
                    next = Time.time + 0.5f;
                    if (Safe()) { Restore(); return; }
                    lastClients = -1;                 // so the next safe tick re-broadcasts

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

            /*
             * PUTTING A GRANTED COLOUR BACK. Two things can take it away, and the host is the only
             * one who can undo either:
             *
             *  - A CLIENT THAT DOES NOT HAVE THE SLOT. He wrote his palette when he joined, and a
             *    grant older than his arrival never reached him: he renders the wearer in whatever
             *    that slot holds for him, which is the empty grey. Nobody could see this from the
             *    host's side, so it was simply broken for late joiners. The trigger is the client
             *    COUNT changing - the cheapest signal that says "somebody's palette is new".
             *  - A COLOUR INDEX THAT CAME BACK CHANGED. The index lives in the player's data, and
             *    if anything resets it (the game re-applying a saved colour, a clash resolution,
             *    another mod), the wearer is out of his slot. The host sets it again.
             *
             * Only in the lobby, only while everyone has the mod: the same two conditions under
             * which the colour could be handed out in the first place. Outside them the guard above
             * is in charge and moves people OFF custom slots, and the two must not fight.
             */
            private static void Restore() {
                if (!UCColorGrant.InLobby()) { lastClients = -1; return; }

                int clients = 0;
                try { clients = AmongUsClient.Instance.allClients?.Count ?? 0; } catch { }
                if (clients != lastClients) {
                    lastClients = clients;
                    foreach (var kv in FilledSlots()) UCColorGrant.BroadcastSlot(kv.Key, kv.Value);
                }

                /*
                 * LEARN BEFORE RESTORING. A grant is recorded when it happens, but the host may not
                 * have been in the room then - a host change hands the lobby to somebody whose
                 * record is empty. Whoever is standing in a filled slot right now is that same
                 * information, and it is available to everybody, so it is read off the players
                 * first. The order matters: read while things are still right, act when they break.
                 */
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (p == null || p.Data == null || p.Data.Disconnected) continue;
                    int worn = p.Data.DefaultOutfit.ColorId;
                    if (IsCustom(worn) && TryGetSlot(worn, out _)) RememberGrant(p, worn);
                }

                if (grants.Count == 0) return;
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (p == null || p.Data == null || p.Data.Disconnected) continue;
                    if (!grants.TryGetValue(p.PlayerId, out var g)) continue;
                    // The id is the same; is the PERSON? If not, the record belongs to somebody who
                    // has left and it dies here rather than colouring his successor.
                    if (g.Who != Ident(p)) { grants.Remove(p.PlayerId); continue; }
                    int slot = g.Slot;
                    if (p.Data.DefaultOutfit.ColorId == slot) continue;
                    if (!TryGetSlot(slot, out var rgb)) continue;
                    UCColorGrant.BroadcastSlot(slot, rgb);
                    p.RpcSetColor((byte)slot);
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[UCColors] restored {p.Data.PlayerName} to slot {slot} "
                        + $"(#{rgb.r:X2}{rgb.g:X2}{rgb.b:X2}); the colour index had been reset.");
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

        /*
         * Slots are per-LOBBY, and that is not the same thing as per-OnGameJoined.
         *
         * This used to empty every slot whenever OnGameJoined ran, which reads right and was
         * wrong: the callback also fires when a round ENDS and everybody returns to the same
         * lobby. The wearer keeps his colour index - that lives in his player data, not in the
         * palette - so after every round he stood in a slot that had just been wiped, and the
         * granted colour showed up as the empty grey. Reported 2026-08-30 ("die geaenderte Farbe
         * wird bei jedem Rundenende zurueckgesetzt").
         *
         * So the wipe is tied to the lobby's own id instead. Same lobby, same slots; a different
         * lobby empties them, which is what keeps a colour granted in one room from turning up on
         * a stranger in the next.
         */
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        internal static class ResetPatch {
            public static void Postfix() {
                try {
                    if (!Installed) return;
                    int id = 0;
                    try { id = AmongUsClient.Instance != null ? AmongUsClient.Instance.GameId : 0; } catch { }
                    if (id == lobbyId) return;                  // back in the same lobby: keep everything
                    lobbyId = id;
                    grants.Clear();
                    for (int i = 0; i < SlotCount; i++) {
                        SetSlot(Base + i, Empty);
                        slotFilled[i] = false;                  // SetSlot marks it filled; it is not
                    }
                } catch { }
            }
        }
    }
}
