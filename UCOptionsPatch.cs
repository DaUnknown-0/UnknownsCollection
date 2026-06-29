// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace UnknownsCollection {
    [HarmonyPatch]
    public static class UCOptionsPatch {
        private static Type clientOptionsPatchType;
        private static FieldInfo popUpField;
        private static FieldInfo buttonPrefabField;

        public static bool Prepare() {
            clientOptionsPatchType = Type.GetType(
                "TheOtherRoles.Patches.ClientOptionsPatch, TheOtherRoles");
            if (clientOptionsPatchType != null) {
                popUpField = AccessTools.Field(clientOptionsPatchType, "popUp");
                buttonPrefabField = AccessTools.Field(clientOptionsPatchType, "buttonPrefab");
            }
            return clientOptionsPatchType != null;
        }

        public static MethodBase TargetMethod() {
            return AccessTools.Method(clientOptionsPatchType, "SetUpOptions");
        }

        [HarmonyPostfix]
        public static void SetUpOptionsPostfix() {
            try {
                var popUp = popUpField?.GetValue(null) as GameObject;
                var buttonPrefab = buttonPrefabField?.GetValue(null) as ToggleButtonBehaviour;
                if (popUp == null || buttonPrefab == null) return;

                // Already added?
                foreach (var t in popUp.GetComponentsInChildren<ToggleButtonBehaviour>())
                    if (t.name == "BugGlitchToggle")
                        return;

                // Count existing toggles to compute position
                int count = popUp.GetComponentsInChildren<ToggleButtonBehaviour>().Length;
                int i = count; // this toggle's index

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
                    button.onState = UnknownsCollectionPlugin.BugGlitchEnabled.Value =
                        !UnknownsCollectionPlugin.BugGlitchEnabled.Value;
                    button.Background.color = button.onState
                        ? Color.green : Palette.ImpostorRed;
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
                    $"[UCOptions] SetUpOptionsPostfix failed: {e}");
            }
        }
    }
}
