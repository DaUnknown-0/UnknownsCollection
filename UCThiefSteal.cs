// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCThiefSteal - the Thief steals UC roles, too.
 *
 * TOR's thiefStealsRole (RPC.cs) hands over TOR's own role statics and, for an Impostor victim,
 * makes the Thief an Impostor. UC roles are tags over a plain Impostor, so a Thief who killed or
 * guessed a Tesla, Saboteur, Hypnotist ... ended up a plain Impostor and the role stayed with the
 * corpse (User 2026-10-02).
 *
 * After TOR's steal, the HOST moves the victim's UC role to the Thief with the role's own SendSet,
 * which every client applies (the same way the Mixer swaps roles): first the role's leftovers are
 * cleared (charges, traps, silences ...), then the Thief is set as the new holder. thiefStealsRole
 * runs on every client (kill: local + RPC; guess: inside guesserShoot), so the host sees it as well.
 * UC roles without a setter (Werewolf, Pelican, Necromancer, Stalker ...) stay plain, as before;
 * TOR only lets the Thief steal from Impostors, the Jackal team and the Sheriff anyway.
 */

using System;
using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    public static class UCThiefSteal {

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.thiefStealsRole))]
        static class StealPatch {
            // the Thief is cleared inside the original (Thief.clearAndReload), so take him here
            public static void Prefix(out byte __state) {
                __state = Thief.thief != null ? Thief.thief.PlayerId : byte.MaxValue;
            }

            public static void Postfix([HarmonyArgument(0)] byte playerId, byte __state) {
                try {
                    if (__state == byte.MaxValue || AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    var thief = Helpers.playerById(__state);
                    if (thief == null || thief.Data == null || thief.Data.IsDead) return;
                    if (!Mixer.TryUcRole(playerId, out var name, out bool impostor, out var set, out var residue)) return;
                    // TOR made him an Impostor only for an Impostor victim; a UC crew/neutral role
                    // never reaches here (the Thief's kill on them is a misfire).
                    if (!impostor) return;
                    try { residue?.Invoke(); }
                    catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[UCThiefSteal] {name} residue: {e.Message}"); }
                    set(thief.PlayerId);
                    UnknownsCollectionPlugin.Logger?.LogInfo($"[UCThiefSteal] {thief.Data.PlayerName} stole {name} from player {playerId}.");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCThiefSteal] steal failed: {e}");
                }
            }
        }
    }
}
