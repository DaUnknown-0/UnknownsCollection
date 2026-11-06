// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCEvents - meeting events a host tool may start by hand (User 2026-11-06: Role Control shall be able to trigger them).
 * Called by reflection (Role Control, BCL types only); host only, every call answers with one line of English text.
 *   BugCrashNow()           the Bug's System Crash for every living player, in this meeting at once, or else armed for the
 *                           next one. Works without a Bug in the game; a failed reboot votes Skip as usual.
 *   ManiacDefuse(playerId)  the bomb defuse minigame for this player in the current meeting. Failing it kills them at
 *                           the meeting's end, exactly as with a real bomb (also without a Maniac in the game).
 */

namespace UnknownsCollection {
    public static class UCEvents {
        public static string BugCrashNow() => BugCrash.ForceNow();
        public static string ManiacDefuse(byte playerId) => Maniac.ForceDefuse(playerId);
    }
}
