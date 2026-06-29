// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using HarmonyLib;
using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace UnknownsCollection {
    public static class UCOptionsPatch {
        private static FieldInfo popUpField;
        private static FieldInfo buttonPrefabField;
        private static TextMeshPro headerTemplate;

        public static void Initialize() {
            try {
                var type = Type.GetType(
                    "TheOtherRoles.Patches.ClientOptionsPatch, TheOtherRoles");
                if (type != null) {
                    popUpField = AccessTools.Field(type, "popUp");
                    buttonPrefabField = AccessTools.Field(type, "buttonPrefab");
                }

                var go = new GameObject("UCHeaderTemplate");
                var tmp = go.AddComponent<TextMeshPro>();
                tmp.fontSize = 2.5f;
                tmp.alignment = TextAlignmentOptions.Center;
                headerTemplate = Object.Instantiate(tmp);
                headerTemplate.gameObject.SetActive(false);
                Object.DontDestroyOnLoad(headerTemplate);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError(
                    $"[UCOptions] Init failed: {e}");
            }
        }

        public static void AddUCOptions() {
            try {
                var popUp = popUpField?.GetValue(null) as GameObject;
                var buttonPrefab = buttonPrefabField?.GetValue(null) as ToggleButtonBehaviour;
                if (popUp == null || buttonPrefab == null) return;

                // Already added?
                foreach (var t in popUp.GetComponentsInChildren<ToggleButtonBehaviour>())
                    if (t.name == "BugGlitchToggle")
                        return;

                int count = popUp.GetComponentsInChildren<ToggleButtonBehaviour>().Length;

                // Section header
                if (headerTemplate != null) {
                    bool exists = false;
                    foreach (var tmp in popUp.GetComponentsInChildren<TextMeshPro>())
                        if (tmp.name == "UCSectionHeader") { exists = true; break; }

                    if (!exists) {
                        int hi = count;
                        var hdr = Object.Instantiate(headerTemplate, popUp.transform);
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
                var button = Object.Instantiate(buttonPrefab, popUp.transform);
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
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError(
                    $"[UCOptions] AddUCOptions failed: {e}");
            }
        }
    }
}
