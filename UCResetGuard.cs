// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCResetGuard - the try/catch every RPCProcedure.resetVariables postfix in this mod runs behind.
 *
 * WHY THIS EXISTS
 * ---------------
 * This mod declares 33 postfixes on RPCProcedure.resetVariables, and until 2026-08-17 not one of
 * them guarded its body - while all 23 of its postfixes on getRoleInfoForPlayer did. That asymmetry
 * mattered more than it looked, because of a measured fact: there are ZERO finalizers registered on
 * resetVariables. HarmonyX only wraps a patch chain in try/catch when a finalizer exists, so an
 * exception escaping one of these postfixes does not get swallowed - it propagates into TOR's own
 * caller. And one of those callers is RoleAssignmentPatch, i.e. the start of a round: a throw there
 * can abort role assignment. It would also skip every postfix queued after the throwing one, taking
 * the round resets of all the other roles and of the other mods down with it.
 *
 * There is no evidence any of them currently throws. This is a blast radius reduction, not a fix for
 * an observed crash: one role's reset failing should cost that one role, not the round.
 *
 * HOW TO USE IT
 * -------------
 * A reset postfix should read as one line:
 *
 *     [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
 *     static class ResetPatch {
 *         public static void Postfix() => UCResetGuard.Run("Pelican", ClearState);
 *     }
 *
 * The companion rule, which the guard cannot enforce for you: inside the reset itself, do the cheap
 * things that cannot throw FIRST (null the fields, clear the lists) and the risky things AFTER
 * (destroying Unity objects, touching cosmetics, writing MyPhysics.Speed, building RoleInfos). Give
 * each risky step its own try/catch where the steps are independent. Otherwise a throw in step one
 * still costs you every field you had not assigned yet - the guard only stops the damage from
 * spreading to other roles, it cannot un-skip your own cleanup.
 */

using System;

namespace UnknownsCollection {
    internal static class UCResetGuard {
        public static void Run(string owner, Action reset) {
            try {
                reset?.Invoke();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[{owner}] round reset failed: {e}");
            }
        }
    }
}
