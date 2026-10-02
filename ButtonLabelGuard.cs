// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ButtonLabelGuard - a TOR CustomButton shows the text it was given, not the "KILL" of the button
 * it was cloned from.
 *
 * TOR builds every CustomButton as a copy of the vanilla kill button and writes its own text into
 * the copied label each frame (ActionButton.OverrideText). In a real round on 2026-10-02 every
 * ability button still read "KILL": the Hypnotist's and even the Bypass test mod's END button,
 * so it is not one role's mistake. The copied label carries the kill button's own text source
 * (a TextTranslatorTMP), which can put the translated "KILL" back after TOR's write.
 *
 * After every CustomButton.Update: if the label is shown, has a text to show and reads something
 * else, the label's translator is switched off and the text is written again. The first mismatch
 * per button is logged with what was there, so the cause can be read from a real round's log.
 * Cost: one string compare per visible button per frame.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using TheOtherRoles.Objects;

namespace UnknownsCollection {
    public static class ButtonLabelGuard {
        private static readonly HashSet<IntPtr> logged = new HashSet<IntPtr>();

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
                    var translator = label.GetComponent<TextTranslatorTMP>();
                    bool hadTranslator = translator != null && translator.enabled;
                    if (hadTranslator) translator.enabled = false;
                    b.actionButton.OverrideText(b.buttonText);
                    if (label.text != b.buttonText) label.text = b.buttonText;

                    if (logged.Add(label.Pointer))
                        UnknownsCollectionPlugin.Logger?.LogInfo(
                            $"[ButtonLabelGuard] '{b.buttonText}' read '{was}' (translator {(hadTranslator ? "on, switched off" : "none/off")}); " +
                            $"after rewrite: '{label.text}'.");
                } catch { }
            }
        }
    }
}
