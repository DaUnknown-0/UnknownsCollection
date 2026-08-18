// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCShieldBridge - the two kill shields that live in TOR - Forgotten Fixes (UTS), reachable without
 * a compile-time reference to that mod.
 *
 * UTS owns two shields TOR knows nothing about: the NEWCOMER shield (somebody's first round) and the
 * ANTI START KILL spawn protection. Both are enforced inside UTS, and for every kill that passes
 * Helpers.checkMuderAttempt that is enough - this mod's roles inherit the protection for free.
 * Two things do NOT pass through there and therefore need this bridge:
 *
 *  1. THE MANIAC'S BLAST picks its victims itself (it already reads the Medic, Time Master, Mini and
 *     first-kill shields directly), so it has to ask about the UTS shields directly too.
 *  2. PEACEFUL TARGETING: UTS drops shield-protected players from TOR's targeting helper so no kill
 *     path can acquire them. The Silencer's mark is not a kill and announces itself as peaceful, the
 *     same way TOR's Medic/Shifter/Tracker targeting does inside UTS.
 *
 * The link is an AppDomain contract, the pattern this mod family already uses for the localization
 * engine and the lobby password gate: no assembly reference in either direction, and a missing key
 * simply means "that mod is not installed" - every call below then degrades to today's behaviour.
 *
 *   "UTS.Shield.IsKillProtected" -> Func<byte,bool>
 *   "UTS.Shield.SetPeaceful"     -> Action<bool>
 *
 * The delegates are resolved lazily and re-resolved while they are still missing (plugin load order
 * between two BepInEx plugins is not ours to dictate), then cached for good.
 */

using System;

namespace UnknownsCollection {

    public static class UCShieldBridge {

        private const string KeyIsProtected = "UTS.Shield.IsKillProtected";
        private const string KeySetPeaceful = "UTS.Shield.SetPeaceful";

        private static Func<byte, bool> isProtected;
        private static Action<bool> setPeaceful;
        private static bool logged;

        private static void Resolve() {
            // Re-reads while still null: UTS may well load after this mod.
            if (isProtected == null) {
                try { isProtected = AppDomain.CurrentDomain.GetData(KeyIsProtected) as Func<byte, bool>; }
                catch { }
            }
            if (setPeaceful == null) {
                try { setPeaceful = AppDomain.CurrentDomain.GetData(KeySetPeaceful) as Action<bool>; }
                catch { }
            }
            if (!logged && isProtected != null) {
                logged = true;
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    "[UCShieldBridge] Forgotten Fixes kill shields detected - newcomer and spawn protection are honoured.");
            }
        }

        // True while the player holds a UTS kill shield (newcomer or spawn protection).
        // False whenever that mod is absent, so nothing here can make a kill fail on its own.
        public static bool IsKillProtected(byte playerId) {
            try {
                Resolve();
                return isProtected != null && isProtected(playerId);
            } catch { return false; }
        }

        // Marks the enclosing targeting call as PEACEFUL, so the UTS shields let it through. Always
        // pair it in a try/finally - see Peaceful() below for the shape that does it for you.
        public static void SetPeaceful(bool on) {
            try {
                Resolve();
                setPeaceful?.Invoke(on);
            } catch { }
        }

        // using (UCShieldBridge.Peaceful()) { ... } - the window closes even if the body throws.
        public static IDisposable Peaceful() => new PeacefulScope();

        private sealed class PeacefulScope : IDisposable {
            public PeacefulScope() { SetPeaceful(true); }
            public void Dispose() { SetPeaceful(false); }
        }
    }
}
