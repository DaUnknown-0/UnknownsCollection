// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCPromotion - shared pick-arbiter for the Unknown's Collection "layered" roles.
 *
 * Several UC roles are display-tags promoted over a plain TOR Impostor (Tesla, Saboteur, Corrupter,
 * Silencer, Illusionist) or a plain Crewmate (Siphoner, Witness). Each role's host-authoritative pick
 * (IntroCutscene.OnDestroy) must avoid landing two UC roles on the SAME player. Instead of every role
 * knowing about every other, they all funnel through this tiny claim registry:
 *
 *   - candidates are filtered with !UCPromotion.IsClaimed(id);
 *   - when a role is assigned (its Apply* runs on every client) it calls Claim(id).
 *
 * Because picks only happen on the host, the host's claim set is what actually gates exclusion; the
 * clients just keep their copy in step (harmless). Cleared on a full game reset.
 *
 * IsPlainImpostor / IsPlainCrewmate are centralized here so every role uses the exact same eligibility
 * rule (its FIRST RoleInfo is the vanilla Impostor / Crewmate entry, i.e. no special TOR role on top).
 */

using System.Linq;
using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    public static class UCPromotion {
        // Player ids already claimed by a UC role this game (host: the authoritative exclusion set).
        private static readonly System.Collections.Generic.HashSet<byte> claimed = new();

        public static bool IsClaimed(byte playerId) => claimed.Contains(playerId);
        public static void Claim(byte playerId) { if (playerId != byte.MaxValue) claimed.Add(playerId); }
        public static void ClearClaims() => claimed.Clear();

        public static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        // A plain TOR Impostor (no special impostor role like Morphling/Bomber/...): its first RoleInfo
        // is exactly the Impostor entry. Excludes anyone already claimed by another UC role.
        public static bool IsPlainImpostor(PlayerControl p) {
            if (!IsAlive(p) || p.Data.Role == null || !p.Data.Role.IsImpostor) return false;
            if (IsClaimed(p.PlayerId)) return false;
            var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
            return info != null && info.roleId == RoleId.Impostor;
        }

        // A plain TOR Crewmate (no special crew/neutral role on top): its first RoleInfo is exactly the
        // Crewmate entry. Excludes anyone already claimed by another UC role.
        public static bool IsPlainCrewmate(PlayerControl p) {
            if (!IsAlive(p) || p.Data.Role == null || p.Data.Role.IsImpostor) return false;
            if (IsClaimed(p.PlayerId)) return false;
            var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
            return info != null && info.roleId == RoleId.Crewmate;
        }

        // Clear claims on a full game-state reset (next game's start).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { ClearClaims(); }
        }
    }
}
