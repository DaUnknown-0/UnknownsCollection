// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ButtonLabelGuard - TOR CustomButtons keep counting down and show their own text after a mid-round
 * faction change.
 *
 * Seen 2026-10-02 in a solo test round: after Role Control switched the local player's faction
 * (PlayerTuning -> RoleManager.SetRole), every ability button read "KILL" and its cooldown stood
 * still. The log shows a NullReferenceException inside GameObject.SetActive at the top of TOR's
 * CustomButton.Update, every frame. That throw aborts Update before the countdown and before
 * OverrideText, which explains both symptoms. A throw inside SetActive comes from a component's
 * OnEnable/OnDisable; the prime suspect is the TextTranslatorTMP every button copies from the
 * vanilla kill button's label (it writes the translated "KILL" when enabled).
 *
 *  1. Prefix, once per button: switch the label's TextTranslatorTMP off. TOR owns these labels and
 *     writes their text every frame, the translator has nothing to do there.
 *  2. Postfix: a button with its own text that still reads something else gets it written again.
 *  3. Finalizer: the first throw per button is logged with the button's state and every component
 *     on it, so a real round's log names the culprit. Swallowed like UTS does, so one broken
 *     button never stops the others.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TheOtherRoles.Objects;

namespace UnknownsCollection {
    public static class ButtonLabelGuard {
        private static readonly HashSet<IntPtr> prepared = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> labelLogged = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> throwLogged = new HashSet<IntPtr>();
        private static int translatorsOff;
        // IdeasPackDiag A/B run: UC_LABELGUARD_OFF=1 leaves the translators alone.
        private static readonly bool DiagOff = Environment.GetEnvironmentVariable("UC_LABELGUARD_OFF") == "1";

        private static IntPtr Key(CustomButton b) {
            try { return b.actionButton != null ? b.actionButton.Pointer : IntPtr.Zero; } catch { return IntPtr.Zero; }
        }

        [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.Update))]
        [HarmonyPriority(Priority.First)]
        static class PreparePatch {
            public static void Prefix(CustomButton __instance) {
                try {
                    if (DiagOff) return;
                    var key = Key(__instance);
                    if (key == IntPtr.Zero || !prepared.Add(key)) return;
                    var label = __instance.actionButtonLabelText;
                    var translator = label != null ? label.GetComponent<TextTranslatorTMP>() : null;
                    if (translator == null || !translator.enabled) return;
                    translator.enabled = false;
                    translatorsOff++;
                    if (translatorsOff == 1 || translatorsOff % 20 == 0)
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[ButtonLabelGuard] label translator switched off on {translatorsOff} button(s).");
                } catch { }
            }
        }

        [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.Update))]
        [HarmonyPriority(Priority.Last)]
        static class UpdatePatch {
            public static void Postfix(CustomButton __instance) {
                try {
                    var b = __instance;
                    if (b == null || !b.showButtonText || string.IsNullOrEmpty(b.buttonText)) return;
                    var label = b.actionButtonLabelText;
                    if (label == null || !label.isActiveAndEnabled || label.text == b.buttonText) return;
                    string was = label.text;
                    b.actionButton.OverrideText(b.buttonText);
                    if (label.text != b.buttonText) label.text = b.buttonText;
                    if (labelLogged.Add(Key(b)))
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[ButtonLabelGuard] '{b.buttonText}' read '{was}'; after rewrite: '{label.text}'.");
                } catch { }
            }

            public static Exception Finalizer(CustomButton __instance, Exception __exception) {
                if (__exception == null) return null;
                try {
                    if (throwLogged.Add(Key(__instance))) {
                        var go = __instance.actionButtonGameObject;
                        string comps = "?";
                        try {
                            comps = go == null ? "no GameObject" : string.Join(", ",
                                go.GetComponentsInChildren<UnityEngine.Component>(true)
                                  .Select(c => c == null ? "null" : $"{c.gameObject.name}:{c.GetIl2CppType().Name}{(c.TryCast<UnityEngine.Behaviour>() is UnityEngine.Behaviour bh && !bh.enabled ? "(off)" : "")}"));
                        } catch (Exception e) { comps = "listing failed: " + e.Message; }
                        UnknownsCollectionPlugin.Logger?.LogWarning(
                            $"[ButtonLabelGuard] Update threw for '{__instance.buttonText}' (sprite {__instance.Sprite?.name}, timer {__instance.Timer:F1}, " +
                            $"active {(go != null ? go.activeSelf.ToString() : "-")}): {__exception.GetType().Name}: {__exception.Message}. Components: {comps}");
                    }
                } catch { }
                return null;
            }
        }
    }
}
