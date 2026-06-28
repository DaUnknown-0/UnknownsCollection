// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Tesla meeting selection UI - Swapper-style per-row checkboxes. Only the (alive) Tesla sees them.
 * First tapped player becomes POSITIVE (cyan), the next becomes NEGATIVE (orange); the second pick
 * auto-confirms (sends the pair via RPC, locks the buttons green). Tapping a selected row clears it.
 * The Tesla's own row is only offered when "Tesla Can Charge Itself" is on. The two poles must be
 * different players (never the same person twice). Re-opens fresh each meeting so the pair can be
 * reassigned; the previously active pair stays live until a new one is confirmed.
 */

using System;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;

namespace UnknownsCollection {
    public static class TeslaMeetingUI {
        private static MeetingHud builtFor;
        private static PassiveButton[] buttons;
        private static SpriteRenderer[] renderers;
        private static int plusSel = -1;
        private static int minusSel = -1;
        private static bool locked;

        private static readonly Color Cyan = new Color(0.12f, 0.72f, 1f, 1f);
        private static readonly Color Orange = new Color(1f, 0.55f, 0f, 1f);

        public static void Reset() {
            builtFor = null;
            buttons = null;
            renderers = null;
            plusSel = minusSel = -1;
            locked = false;
        }

        private static bool LocalIsTesla() =>
            Tesla.active && Tesla.tesla != null && Tesla.tesla == PlayerControl.LocalPlayer
            && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead;

        public static void Build(MeetingHud hud) {
            try {
                if (hud == null || !LocalIsTesla()) return;
                if (builtFor == hud) return; // ServerStart + Deserialize both fire - build once
                builtFor = hud;
                plusSel = minusSel = -1;
                locked = false;

                bool canSelf = Tesla.CanChargeSelf != null && Tesla.CanChargeSelf.getBool();
                byte selfId = PlayerControl.LocalPlayer.PlayerId;

                int n = hud.playerStates.Length;
                buttons = new PassiveButton[n];
                renderers = new SpriteRenderer[n];

                for (int i = 0; i < n; i++) {
                    PlayerVoteArea pva = hud.playerStates[i];
                    if (pva.AmDead) continue;
                    if (pva.TargetPlayerId == selfId && !canSelf) continue;
                    if (Tesla.chargedHistory.Contains(pva.TargetPlayerId)) continue; // no repeats

                    GameObject template = pva.Buttons.transform.Find("CancelButton").gameObject;
                    GameObject checkbox = UnityEngine.Object.Instantiate(template);
                    checkbox.transform.SetParent(pva.transform);
                    checkbox.transform.position = template.transform.position;
                    // Default spot is (-0.95) - the SAME spot TOR's Guesser shoot button uses. If this
                    // Tesla is also a Guesser, shift our checkbox left so the two don't overlap.
                    float x = HandleGuesser.isGuesser(PlayerControl.LocalPlayer.PlayerId) ? -1.43f : -0.95f;
                    checkbox.transform.localPosition = new Vector3(x, 0.03f, -1.3f);

                    SpriteRenderer renderer = checkbox.GetComponent<SpriteRenderer>();
                    renderer.color = Color.white;

                    PassiveButton button = checkbox.GetComponent<PassiveButton>();
                    button.OnClick.RemoveAllListeners();
                    int idx = i;
                    button.OnClick.AddListener((System.Action)(() => OnClick(idx, hud)));

                    buttons[i] = button;
                    renderers[i] = renderer;
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] meeting UI build failed: {e}");
            }
        }

        private static void OnClick(int i, MeetingHud hud) {
            try {
                if (locked || hud.state == MeetingHud.VoteStates.Results) return;
                if (renderers == null || renderers[i] == null) return;

                if (i == plusSel) { plusSel = -1; renderers[i].color = Color.white; return; }
                if (i == minusSel) { minusSel = -1; renderers[i].color = Color.white; return; }

                if (plusSel == -1) {
                    plusSel = i;
                    renderers[i].color = Cyan;
                } else if (minusSel == -1) {
                    minusSel = i;
                    renderers[i].color = Orange;
                    Confirm(hud);
                }
                // both already chosen -> ignore until one is deselected
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Tesla] meeting UI click failed: {e}");
            }
        }

        private static void Confirm(MeetingHud hud) {
            byte plusTarget = hud.playerStates[plusSel].TargetPlayerId;
            byte minusTarget = hud.playerStates[minusSel].TargetPlayerId;
            if (plusTarget == minusTarget) return; // never the same person twice

            Tesla.SendSetCharges(plusTarget, minusTarget);
            locked = true;

            // Lock all rows: confirmed pair stays coloured, the rest greys out, no further clicks.
            for (int k = 0; k < buttons.Length; k++) {
                if (buttons[k] != null) buttons[k].OnClick.RemoveAllListeners();
                if (renderers[k] == null) continue;
                if (k == plusSel) renderers[k].color = Cyan;
                else if (k == minusSel) renderers[k].color = Orange;
                else renderers[k].color = Color.gray;
            }

            var plusName = Helpers.playerById(plusTarget)?.Data?.PlayerName ?? "?";
            var minusName = Helpers.playerById(minusTarget)?.Data?.PlayerName ?? "?";
            try {
                HudManager.Instance?.Chat?.AddChat(PlayerControl.LocalPlayer,
                    $"Charged: <color=#1FB8FFFF>+ {plusName}</color>, <color=#FF8C00FF>- {minusName}</color>");
            } catch { }
        }

        // Build the UI when the meeting opens (host path + client path, like TOR's Swapper buttons).
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.ServerStart))]
        static class MeetingServerStartPatch {
            public static void Postfix(MeetingHud __instance) => Build(__instance);
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Deserialize))]
        static class MeetingDeserializePatch {
            public static void Postfix(MeetingHud __instance, [HarmonyArgument(1)] bool initialState) {
                if (initialState) Build(__instance);
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        static class MeetingClosePatch {
            public static void Postfix() => Reset();
        }
    }
}
