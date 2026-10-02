// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCGuessTruth (review 2026-10-02) - what getRoleInfoForPlayer WOULD return with its detour intact.
 *
 * .NET tiering can silently drop the detour on TOR's getRoleInfoForPlayer (a hot managed method),
 * and with it every mod's postfix there. The postfix METHODS still work; only the jump into them is
 * gone. So:
 *   raw   = a reverse-patched copy of TOR's original body (never detoured, never patched)
 *   truth = raw, then every registered postfix that takes __result, replayed in Harmony's order
 * Used by UCGuessNames to judge a guess on the guesser's client. Any doubt (a postfix with a
 * parameter we cannot bind, one that throws, no reverse patch) returns false: no verdict.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TheOtherRoles;

namespace UnknownsCollection {
    internal static class UCGuessTruth {
        private static MethodInfo original;
        private static bool ready;

        internal static void Init(Harmony harmony) {
            try {
                original = AccessTools.Method(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer));
                if (original == null) return;
                Harmony.ReversePatch(original, new HarmonyMethod(AccessTools.Method(typeof(UCGuessTruth), nameof(Raw))));
                ready = true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCGuessTruth] reverse patch failed, guess safety net off: {e.Message}");
            }
        }

        // Body replaced by the reverse patch with TOR's original IL.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<RoleInfo> Raw(PlayerControl p, bool showModifier) =>
            throw new NotImplementedException("UCGuessTruth.Raw: reverse patch missing");

        /// <summary>First entry of TOR's raw result and of the replayed postfix chain.</summary>
        internal static bool TryMain(PlayerControl p, out RoleInfo rawMain, out RoleInfo trueMain) {
            rawMain = null; trueMain = null;
            if (!ready || p == null) return false;
            try {
                var raw = Raw(p, false);
                if (raw == null) return false;
                rawMain = raw.FirstOrDefault();

                var info = Harmony.GetPatchInfo(original);
                List<RoleInfo> result = new List<RoleInfo>(raw);
                if (info != null) {
                    var origParams = original.GetParameters();
                    foreach (var patch in info.Postfixes.OrderByDescending(x => x.priority).ThenBy(x => x.index)) {
                        var m = patch.PatchMethod;
                        if (m == null || !m.IsStatic) continue;
                        var ps = m.GetParameters();
                        int resultAt = Array.FindIndex(ps, x => x.Name == "__result");
                        if (resultAt < 0) continue;            // cannot change the list (e.g. UTS' canary)
                        var args = new object[ps.Length];
                        for (int i = 0; i < ps.Length; i++) {
                            if (i == resultAt) { args[i] = result; continue; }
                            int at = OriginalIndex(ps[i], origParams);
                            if (at == 0) args[i] = p;
                            else if (at == 1) args[i] = false;
                            else return false;               // a parameter we cannot supply: no verdict
                        }
                        m.Invoke(null, args);
                        if (args[resultAt] is List<RoleInfo> replaced) result = replaced;   // ref __result
                    }
                }
                trueMain = result.FirstOrDefault();
                return true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCGuessTruth] replay failed: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        private static int OriginalIndex(ParameterInfo param, ParameterInfo[] origParams) {
            var attr = param.GetCustomAttributes(typeof(HarmonyArgument), false).FirstOrDefault() as HarmonyArgument;
            if (attr != null) {
                if (attr.OriginalName != null) return Array.FindIndex(origParams, x => x.Name == attr.OriginalName);
                return attr.Index;
            }
            return Array.FindIndex(origParams, x => x.Name == param.Name);
        }
    }
}
