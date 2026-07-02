// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCPromotion - shared pick-arbiter for the Unknown's Collection "layered" roles.
 *
 * Several UC roles are display-tags promoted over a plain TOR Impostor (Tesla, Saboteur, Poisoner,
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
 *
 * Claim() is also the single choke point every UC role's Apply* runs through (Draft picks AND random
 * IntroCutscene promotion alike), so it doubles as the hook for the role-agnostic "you have been
 * promoted" reveal cue (UCRevealFx). Claim() runs on EVERY client for EVERY UC role assignment - the
 * reveal is therefore gated to playerId == PlayerControl.LocalPlayer.PlayerId here, exactly once, so
 * individual role files never need to remember the gate themselves. A role that already has its own
 * bespoke promotion feedback (e.g. a future Tesla-specific stinger in Tesla.ApplySetTesla) can pass
 * suppressFx: true to Claim() to avoid a double cue - see the suppressFx parameter below.
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

        // suppressFx: pass true when the caller already gives the promoted player its own bespoke
        // reveal feedback, so UCRevealFx's generic gold/white cue does not double up with it.
        public static void Claim(byte playerId, bool suppressFx = false) {
            if (playerId == byte.MaxValue) return;
            claimed.Add(playerId);
            // Info-Leak-Regel: Claim() fires on EVERY client for EVERY UC role assignment - only the
            // player who was actually promoted may ever see/hear this, never bystanders.
            if (!suppressFx && PlayerControl.LocalPlayer != null && playerId == PlayerControl.LocalPlayer.PlayerId)
                UCRevealFx.PlayReveal();
        }

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
