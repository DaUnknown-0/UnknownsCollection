// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCWinners - identity comparison for entries on the end-game podium.
 *
 * WHY THIS EXISTS
 * ---------------
 * Several roles here append themselves to EndGameResult.CachedWinners and have to check first
 * whether that player is already on the list. The obvious key is missing: CachedPlayerData carries
 * PlayerName, Outfit, IsYou, IsImpostor, IsDead and RoleWhenAlive - and no PlayerId (verified
 * against Assembly-CSharp 2024.10.29). TOR itself compares PlayerName alone (EndGamePatch.cs:107),
 * and so did this mod.
 *
 * Among Us allows two players to pick the same name, so the name alone is ambiguous: a Copycat who
 * shares a name with an actual winner is silently treated as "already a winner" and loses her win.
 * The colour disambiguates it - the lobby hands out one colour per player - so both have to match.
 *
 * Morph/Camouflage do NOT interfere: TOR's setLook only rewrites the cosmetics and the name tag
 * (Helpers.cs:367-371), never Data.DefaultOutfit, which is what both sides of this comparison read.
 *
 * The colour is read defensively: if the interop property throws or an outfit is missing, the
 * comparison falls back to the name so behaviour is never worse than before.
 */

using System;

namespace UnknownsCollection {
    public static class UCWinners {
        // True if the cached podium entry describes the given player.
        public static bool IsSameWinner(CachedPlayerData cached, NetworkedPlayerInfo player) {
            if (cached == null || player == null) return false;
            if (cached.PlayerName != player.PlayerName) return false;
            try {
                var outfit = player.DefaultOutfit;
                if (outfit == null) return true;          // no colour to compare - name decides
                return cached.ColorId == outfit.ColorId;
            } catch {
                return true;                              // interop unavailable - name decides
            }
        }
    }
}
