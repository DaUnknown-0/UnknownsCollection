// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using HarmonyLib;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace UnknownsCollection {
    [HarmonyPatch]
    public static class UCOptionsPatch {
        private static GameObject popUp;
        private static ToggleButtonBehaviour buttonPrefab;
        private static Vector3? origin;

        private static readonly SelectionBehaviour[] AllOptions = {
            new("Bug Win Glitch Effects",
                () => {
                    UnknownsCollectionPlugin.BugGlitchEnabled.Value = !UnknownsCollectionPlugin.BugGlitchEnabled.Value;
                    return UnknownsCollectionPlugin.BugGlitchEnabled.Value;
                },
                UnknownsCollectionPlugin.BugGlitchEnabled.Value)
        };

        private static TextMeshPro titleText;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        public static void MainMenuManager_StartPostfix(MainMenuManager __instance) {
            try {
                var go = new GameObject("UCTitleText");
                var tmp = go.AddComponent<TextMeshPro>();
                tmp.fontSize = 4;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.transform.localPosition += Vector3.left * 0.2f;
                titleText = Object.Instantiate(tmp);
                titleText.gameObject.SetActive(false);
                Object.DontDestroyOnLoad(titleText);
            } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
        public static void OptionsMenuBehaviour_StartPostfix(OptionsMenuBehaviour __instance) {
            try {
                if (__instance.CensorChatButton == null) return;

                if (!popUp) CreatePopUp(__instance);
                if (!buttonPrefab) {
                    buttonPrefab = Object.Instantiate(__instance.CensorChatButton);
                    Object.DontDestroyOnLoad(buttonPrefab);
                    buttonPrefab.name = "CensorChatPrefab";
                    buttonPrefab.gameObject.SetActive(false);
                }
                AddUCButton(__instance);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCOptions] StartPostfix failed: {e}");
            }
        }

        private static void CreatePopUp(OptionsMenuBehaviour prefab) {
            popUp = Object.Instantiate(prefab.gameObject);
            Object.DontDestroyOnLoad(popUp);
            var t = popUp.transform;
            var pos = t.localPosition;
            pos.z = -810f;
            t.localPosition = pos;

            Object.Destroy(popUp.GetComponent<OptionsMenuBehaviour>());
            var children = new List<GameObject>();
            for (int i = 0; i < popUp.transform.childCount; i++)
                children.Add(popUp.transform.GetChild(i).gameObject);
            foreach (var child in children) {
                if (child.name != "Background" && child.name != "CloseButton")
                    Object.Destroy(child);
            }
            popUp.SetActive(false);
        }

        private static void AddUCButton(OptionsMenuBehaviour __instance) {
            origin ??= __instance.CensorChatButton.transform.localPosition;

            var ucButton = Object.Instantiate(buttonPrefab, __instance.CensorChatButton.transform.parent);
            ucButton.transform.localPosition = origin.Value + Vector3.right * 2.667f;
            ucButton.transform.localScale = new Vector3(0.66f, 1, 1);
            ucButton.gameObject.SetActive(true);
            ucButton.Text.text = "UC Options...";
            ucButton.Text.transform.localScale = new Vector3(1 / 0.66f, 1, 1);

            var passiveButton = ucButton.GetComponent<PassiveButton>();
            passiveButton.OnClick = new ButtonClickedEvent();
            passiveButton.OnClick.AddListener((Action)(() => {
                if (popUp == null) return;

                bool closeUnderlying = false;
                if (__instance.transform.parent != null && HudManager.InstanceExists) {
                    popUp.transform.SetParent(HudManager.Instance.transform);
                    popUp.transform.localPosition = new Vector3(0, 0, -800f);
                    closeUnderlying = true;
                } else {
                    popUp.transform.SetParent(null);
                    Object.DontDestroyOnLoad(popUp);
                }

                CheckSetTitle();
                RefreshOpen();
                if (closeUnderlying)
                    __instance.Close();
            }));
        }

        private static void CheckSetTitle() {
            if (popUp == null || popUp.GetComponentInChildren<TextMeshPro>() || titleText == null) return;

            var title = Object.Instantiate(titleText, popUp.transform);
            title.GetComponent<RectTransform>().localPosition = Vector3.up * 2.3f;
            title.gameObject.SetActive(true);
            title.text = "UC Options";
            title.name = "UCTitle";
        }

        private static void RefreshOpen() {
            popUp.gameObject.SetActive(false);
            popUp.gameObject.SetActive(true);
            SetUpOptions();
        }

        private static void SetUpOptions() {
            if (popUp.transform.GetComponentInChildren<ToggleButtonBehaviour>()) return;

            for (int i = 0; i < AllOptions.Length; i++) {
                var info = AllOptions[i];
                var button = Object.Instantiate(buttonPrefab, popUp.transform);
                var pos = new Vector3(i % 2 == 0 ? -1.17f : 1.17f, 1.3f - i / 2 * 0.8f, -0.5f);
                button.transform.localPosition = pos;

                button.onState = info.DefaultValue;
                button.Background.color = button.onState ? Color.green : Palette.ImpostorRed;
                button.Text.text = info.Title;
                button.Text.fontSizeMin = button.Text.fontSizeMax = 1.8f;
                button.name = info.Title.Replace(" ", "") + "Toggle";
                button.gameObject.SetActive(true);

                var pb = button.GetComponent<PassiveButton>();
                var cb = button.GetComponent<BoxCollider2D>();
                cb.size = new Vector2(2.2f, 0.7f);

                pb.OnClick = new ButtonClickedEvent();
                pb.OnMouseOut = new UnityEvent();
                pb.OnMouseOver = new UnityEvent();

                pb.OnClick.AddListener((Action)(() => {
                    button.onState = info.OnClick();
                    button.Background.color = button.onState ? Color.green : Palette.ImpostorRed;
                }));

                pb.OnMouseOver.AddListener((Action)(() => button.Background.color = button.onState ? new Color32(34, 139, 34, byte.MaxValue) : new Color32(139, 34, 34, byte.MaxValue)));
                pb.OnMouseOut.AddListener((Action)(() => button.Background.color = button.onState ? Color.green : Palette.ImpostorRed));

                foreach (var spr in button.gameObject.GetComponentsInChildren<SpriteRenderer>())
                    spr.size = new Vector2(2.2f, 0.7f);
            }
        }

        public class SelectionBehaviour {
            public string Title;
            public Func<bool> OnClick;
            public bool DefaultValue;
            public SelectionBehaviour(string title, Func<bool> onClick, bool defaultValue) {
                Title = title;
                OnClick = onClick;
                DefaultValue = defaultValue;
            }
        }
    }
}
