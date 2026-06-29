// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using HarmonyLib;
using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;

namespace UnknownsCollection {
    [HarmonyPatch]
    public static class UCOptionsPatch {
        private static FieldInfo popUpField;
        private static TextMeshPro headerTemplate;
        private static bool registered;

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
        public static void OptionsMenuStartPostfix(OptionsMenuBehaviour __instance) {
            try {
                if (registered) return;
                ResolveFields();

                // Find the "Mod Options..." button that TOR just created.
                var parent = __instance.CensorChatButton?.transform?.parent;
                if (parent == null) return;

                for (int i = 0; i < parent.childCount; i++) {
                    var child = parent.GetChild(i);
                    var tb = child.GetComponent<ToggleButtonBehaviour>();
                    if (tb?.Text?.text?.Contains("Mod Options") == true) {
                        RegisterOnModOptionsButton(tb, __instance);
                        registered = true;
                        break;
                    }
                }

                if (!registered) return;

                if (headerTemplate == null) {
                    var go = new GameObject("UCHeaderTemplate");
                    var tmp = go.AddComponent<TextMeshPro>();
                    tmp.fontSize = 2.5f;
                    tmp.alignment = TextAlignmentOptions.Center;
                    tmp.color = new Color(0.12f, 0.72f, 1f);
                    headerTemplate = UnityEngine.Object.Instantiate(tmp);
                    headerTemplate.gameObject.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(headerTemplate);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError(
                    $"[UCOptions] OptionsMenuStartPostfix failed: {e}");
            }
        }

        private static void ResolveFields() {
            if (popUpField != null) return;
            try {
                var type = Type.GetType(
                    "TheOtherRoles.Patches.ClientOptionsPatch, TheOtherRoles");
                if (type != null)
                    popUpField = AccessTools.Field(type, "popUp");
            } catch { }
        }

        private static void RegisterOnModOptionsButton(
                ToggleButtonBehaviour modOptionsBtn,
                OptionsMenuBehaviour optionsMenu) {
            var pb = modOptionsBtn.GetComponent<PassiveButton>();
            if (pb == null) return;

            pb.OnClick.AddListener((Action)(() => {
                try {
                    var popUp = popUpField?.GetValue(null) as GameObject;
                    if (popUp == null) return;

                    InjectUCToggles(popUp);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError(
                        $"[UCOptions] ModOptions click handler failed: {e}");
                }
            }));
        }

        private static void InjectUCToggles(GameObject popUp) {
            // Guard: already injected?
            foreach (var t in popUp.GetComponentsInChildren<ToggleButtonBehaviour>())
                if (t.name == "BugGlitchToggle")
                    return;

            // Find an existing toggle to clone (skip finding buttonPrefab)
            ToggleButtonBehaviour cloneSrc = null;
            foreach (var t in popUp.GetComponentsInChildren<ToggleButtonBehaviour>()) {
                if (t.name != "BugGlitchToggle") { cloneSrc = t; break; }
            }
            if (cloneSrc == null) return;

            int count = popUp.GetComponentsInChildren<ToggleButtonBehaviour>().Length;

            // Section header
            if (headerTemplate != null) {
                bool hasHeader = false;
                foreach (var tmp in popUp.GetComponentsInChildren<TextMeshPro>())
                    if (tmp.name == "UCSectionHeader") { hasHeader = true; break; }

                if (!hasHeader) {
                    int hi = count;
                    var hdr = UnityEngine.Object.Instantiate(headerTemplate, popUp.transform);
                    hdr.GetComponent<RectTransform>().localPosition = new Vector3(
                        0f,
                        1.3f - hi / 2 * 0.8f,
                        -0.5f);
                    hdr.gameObject.SetActive(true);
                    hdr.text = "--- Unknown's Collection ---";
                    hdr.name = "UCSectionHeader";
                    count++;
                }
            }

            int i = count;
            var button = UnityEngine.Object.Instantiate(cloneSrc, popUp.transform);
            button.transform.localPosition = new Vector3(
                i % 2 == 0 ? -1.17f : 1.17f,
                1.3f - i / 2 * 0.8f,
                -0.5f);

            button.onState = UnknownsCollectionPlugin.BugGlitchEnabled.Value;
            button.Background.color = button.onState ? Color.green : Palette.ImpostorRed;
            button.Text.text = "Bug Win Glitch Effects";
            button.Text.fontSizeMin = button.Text.fontSizeMax = 1.8f;
            button.name = "BugGlitchToggle";
            button.gameObject.SetActive(true);

            var pb = button.GetComponent<PassiveButton>();
            var cb = button.GetComponent<BoxCollider2D>();
            cb.size = new Vector2(2.2f, 0.7f);

            pb.OnClick = new ButtonClickedEvent();
            pb.OnMouseOut = new UnityEvent();
            pb.OnMouseOver = new UnityEvent();

            pb.OnClick.AddListener((Action)(() => {
                button.onState =
                    UnknownsCollectionPlugin.BugGlitchEnabled.Value =
                        !UnknownsCollectionPlugin.BugGlitchEnabled.Value;
                button.Background.color =
                    button.onState ? Color.green : Palette.ImpostorRed;
            }));

            pb.OnMouseOver.AddListener((Action)(() =>
                button.Background.color = button.onState
                    ? new Color32(34, 139, 34, byte.MaxValue)
                    : new Color32(139, 34, 34, byte.MaxValue)));

            pb.OnMouseOut.AddListener((Action)(() =>
                button.Background.color = button.onState
                    ? Color.green : Palette.ImpostorRed));

            foreach (var spr in button.GetComponentsInChildren<SpriteRenderer>())
                spr.size = new Vector2(2.2f, 0.7f);
        }
    }
}
