// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCLateRoleIntro (audit 21.10.) - a TOR role handed out AFTER the intro still gets its intro set-up.
 *
 * TOR builds two role-specific HUD pieces in its IntroCutscene.OnDestroy prefix (IntroPatch.cs), for
 * whoever holds the role at that moment: the Arsonist's row of player icons (douse progress) and the
 * Bounty Hunter's countdown text. A role that arrives later (the Role Draft ends after the intro, a host
 * tool like Role Control sets roles at intro end or mid-round) never got them: no douse icons, no bounty
 * timer for that round. This runs on every client, for the local player only, after the intro:
 *  - Arsonist: if none of the other players' icons is showing, lay them out exactly like TOR does.
 *  - Bounty Hunter: if the countdown text is missing while he is alive, create it like TOR does.
 * A normal game is untouched: there the pieces already exist and both checks find them.
 */

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    internal static class UCLateRoleIntro {
        // TORMapOptions is internal in TOR
        private static readonly FieldInfo iconsField =
            AccessTools.Field(AccessTools.TypeByName("TheOtherRoles.TORMapOptions"), "playerIcons");
        private static readonly FieldInfo bottomLeftField =
            AccessTools.Field(AccessTools.TypeByName("TheOtherRoles.Patches.IntroCutsceneOnDestroyPatch"), "bottomLeft");

        private static bool introDone;
        private static float nextCheck;

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndPatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix() { introDone = true; nextCheck = Time.time + 1f; }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch {
            public static void Postfix() => introDone = false;
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyPatch {
            public static void Postfix() => introDone = false;
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    if (!introDone || Time.time < nextCheck || __instance == null) return;
                    nextCheck = Time.time + 0.5f;
                    var lp = PlayerControl.LocalPlayer;
                    if (lp == null || lp.Data == null || lp.Data.IsDead || MeetingHud.Instance != null) return;
                    if (bottomLeftField == null || bottomLeftField.GetValue(null) is not Vector3 bottomLeft) return;
                    if (Arsonist.arsonist != null && Arsonist.arsonist.PlayerId == lp.PlayerId) ArsonistIcons(lp, bottomLeft);
                    if (BountyHunter.bountyHunter != null && BountyHunter.bountyHunter.PlayerId == lp.PlayerId) BountyText(__instance, bottomLeft);
                } catch { }
            }
        }

        private static void ArsonistIcons(PlayerControl lp, Vector3 bottomLeft) {
            if (iconsField?.GetValue(null) is not System.Collections.Generic.Dictionary<byte, PoolablePlayer> icons) return;
            if (icons.Count == 0) return;
            foreach (var p in PlayerControl.AllPlayerControls)
                if (p != null && p.PlayerId != lp.PlayerId && icons.TryGetValue(p.PlayerId, out var pp) && pp != null && pp.gameObject.activeSelf)
                    return;   // TOR's own row is there
            int counter = 0;
            foreach (var p in PlayerControl.AllPlayerControls) {
                if (p == null || p.PlayerId == lp.PlayerId || !icons.TryGetValue(p.PlayerId, out var pp) || pp == null) continue;
                pp.transform.localPosition = bottomLeft + new Vector3(-0.25f, -0.25f, 0) + Vector3.right * counter++ * 0.35f;
                pp.transform.localScale = Vector3.one * 0.2f;
                bool doused = Arsonist.dousedPlayers.Exists(d => d != null && d.PlayerId == p.PlayerId);
                pp.setSemiTransparent(!doused);
                pp.gameObject.SetActive(p.Data != null && !p.Data.Disconnected && !p.Data.IsDead);
            }
            UnknownsCollectionPlugin.Logger?.LogInfo("[LateRoleIntro] Arsonist assigned after the intro: douse icons laid out.");
        }

        private static void BountyText(HudManager hud, Vector3 bottomLeft) {
            if (BountyHunter.cooldownText != null || hud.KillButton == null) return;
            var t = UnityEngine.Object.Instantiate(hud.KillButton.cooldownTimerText, hud.transform);
            t.alignment = TMPro.TextAlignmentOptions.Center;
            t.transform.localPosition = bottomLeft + new Vector3(0f, -0.35f, -62f);
            t.transform.localScale = Vector3.one * 0.4f;
            t.gameObject.SetActive(true);
            BountyHunter.cooldownText = t;
            UnknownsCollectionPlugin.Logger?.LogInfo("[LateRoleIntro] Bounty Hunter assigned after the intro: countdown created.");
        }
    }
}
