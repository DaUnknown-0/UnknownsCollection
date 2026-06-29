// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace UnknownsCollection {
    [HarmonyPatch]
    public static class UCOptionsPatch {
        private static FieldInfo torPopUpField;
        private static FieldInfo torButtonPrefabField;
        private static bool registered;

        private static GameObject ucPopUp;
        private static TextMeshPro ucTitleTemplate;

        private static readonly UCSelection[] AllOptions = {
            new("Bug Win Glitch Effects",
                () => {
                    UnknownsCollectionPlugin.BugGlitchEnabled.Value =
                        !UnknownsCollectionPlugin.BugGlitchEnabled.Value;
                    return UnknownsCollectionPlugin.BugGlitchEnabled.Value;
                },
                () => UnknownsCollectionPlugin.BugGlitchEnabled.Value)
        };

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
        public static void OptionsMenuStartPostfix(OptionsMenuBehaviour __instance) {
            try {
                if (registered) return;
                if (__instance.CensorChatButton == null) return;
                ResolveTORFields();

                var parent = __instance.CensorChatButton.transform.parent;
                if (parent == null) return;

                // Find TOR's "Mod Options..." button
                for (int i = 0; i < parent.childCount; i++) {
                    var child = parent.GetChild(i);
                    var tb = child.GetComponent<ToggleButtonBehaviour>();
                    if (tb?.Text?.text?.Contains("Mod Options") == true) {
                        HookModOptionsButton(tb, __instance);
                        registered = true;
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError(
                    $"[UCOptions] OptionsMenuStartPostfix: {e}");
            }
        }

        private static void ResolveTORFields() {
            if (torPopUpField != null) return;
            try {
                var type = Type.GetType(
                    "TheOtherRoles.Patches.ClientOptionsPatch, TheOtherRoles");
                if (type != null) {
                    torPopUpField = AccessTools.Field(type, "popUp");
                    torButtonPrefabField = AccessTools.Field(type, "buttonPrefab");
                }
            } catch { }
        }

        private static void HookModOptionsButton(
                ToggleButtonBehaviour modBtn, OptionsMenuBehaviour optionsMenu) {
            var pb = modBtn.GetComponent<PassiveButton>();
            if (pb == null) return;

            pb.OnClick.AddListener((Action)(() => {
                try {
                    var torPopUp = torPopUpField?.GetValue(null) as GameObject;
                    if (torPopUp == null) return;

                    EnsureUCPopup(torPopUp, optionsMenu);
                    AddNavButton(torPopUp);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError(
                        $"[UCOptions] HookModOptionsButton callback: {e}");
                }
            }));
        }

        private static void EnsureUCPopup(GameObject torPopUp, OptionsMenuBehaviour optionsMenu) {
            if (ucPopUp != null) return;

            // Create UC sub-popup from the same prefab as TOR's popup
            ucPopUp = Object.Instantiate(torPopUp);
            Object.DontDestroyOnLoad(ucPopUp);
            var t = ucPopUp.transform;
            var pos = t.localPosition;
            pos.z = -820f;
            t.localPosition = pos;

            // Destroy all children except Background and CloseButton
            var children = new List<GameObject>();
            for (int i = 0; i < ucPopUp.transform.childCount; i++)
                children.Add(ucPopUp.transform.GetChild(i).gameObject);
            foreach (var child in children) {
                if (child.name != "Background" && child.name != "CloseButton")
                    Object.Destroy(child);
            }

            // Wire the CloseButton to go back to TOR's popup instead of just closing
            var closeBtn = ucPopUp.transform.Find("CloseButton");
            if (closeBtn != null) {
                var passive = closeBtn.GetComponent<PassiveButton>();
                if (passive != null) {
                    passive.OnClick = new ButtonClickedEvent();
                    passive.OnClick.AddListener((Action)(() => {
                        ucPopUp.SetActive(false);
                        torPopUp.SetActive(true);
                    }));
                }
            }

            ucPopUp.SetActive(false);

            // Title template
            var go = new GameObject("UCTitleTemplate");
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.fontSize = 4;
            tmp.alignment = TextAlignmentOptions.Center;
            ucTitleTemplate = Object.Instantiate(tmp);
            ucTitleTemplate.gameObject.SetActive(false);
            Object.DontDestroyOnLoad(ucTitleTemplate);
        }

        private static void AddNavButton(GameObject torPopUp) {
            // Already added?
            foreach (var t in torPopUp.GetComponentsInChildren<ToggleButtonBehaviour>())
                if (t.name == "UCNavButton")
                    return;

            // Grab TOR's buttonPrefab to clone a consistent-looking button
            var prefab = torButtonPrefabField?.GetValue(null) as ToggleButtonBehaviour;
            if (prefab == null) {
                // Fallback: clone any existing toggle in TOR's popup
                foreach (var t in torPopUp.GetComponentsInChildren<ToggleButtonBehaviour>()) {
                    prefab = t; break;
                }
            }
            if (prefab == null) return;

            var nav = Object.Instantiate(prefab, torPopUp.transform);
            nav.name = "UCNavButton";
            nav.gameObject.SetActive(true);
            nav.Text.text = "Unknown's Collection";
            nav.Text.fontSizeMin = nav.Text.fontSizeMax = 1.8f;
            nav.Text.transform.localScale = Vector3.one;
            nav.onState = false;
            nav.Background.color = new Color32(30, 40, 80, byte.MaxValue); // dark blue

            nav.transform.localPosition = new Vector3(1.17f, -1.9f, -0.5f);

            var collider = nav.GetComponent<BoxCollider2D>();
            if (collider != null) collider.size = new Vector2(2.2f, 0.6f);
            foreach (var spr in nav.GetComponentsInChildren<SpriteRenderer>())
                spr.size = new Vector2(2.2f, 0.6f);

            var pb = nav.GetComponent<PassiveButton>();
            pb.OnClick = new ButtonClickedEvent();
            pb.OnMouseOut = new UnityEvent();
            pb.OnMouseOver = new UnityEvent();

            pb.OnClick.AddListener((Action)(() => {
                torPopUp.SetActive(false);
                ShowUCPopup(torPopUp);
            }));

            pb.OnMouseOver.AddListener((Action)(() =>
                nav.Background.color = new Color32(50, 70, 140, byte.MaxValue)));
            pb.OnMouseOut.AddListener((Action)(() =>
                nav.Background.color = new Color32(30, 40, 80, byte.MaxValue)));
        }

        private static void ShowUCPopup(GameObject torPopUp) {
            // Parent to same transform as TOR popup
            if (torPopUp.transform.parent != null)
                ucPopUp.transform.SetParent(torPopUp.transform.parent);
            else {
                ucPopUp.transform.SetParent(null);
                Object.DontDestroyOnLoad(ucPopUp);
            }
            ucPopUp.transform.localPosition = torPopUp.transform.localPosition;

            // Add / refresh title
            CheckSetTitle();

            // (Re)create toggles every time so state is fresh
            foreach (var t in ucPopUp.GetComponentsInChildren<ToggleButtonBehaviour>())
                Object.Destroy(t.gameObject);

            SetUpUCOptions();

            ucPopUp.SetActive(true);
        }

        private static void CheckSetTitle() {
            // Remove stale title if any
            foreach (var tmp in ucPopUp.GetComponentsInChildren<TextMeshPro>())
                if (tmp.name == "UCTitle")
                    Object.Destroy(tmp.gameObject);

            if (ucTitleTemplate == null) return;
            var title = Object.Instantiate(ucTitleTemplate, ucPopUp.transform);
            title.GetComponent<RectTransform>().localPosition = Vector3.up * 2.3f;
            title.gameObject.SetActive(true);
            title.text = "Unknown's Collection";
            title.name = "UCTitle";
        }

        private static void SetUpUCOptions() {
            // Grab a prefab from TOR's popup (any existing toggle)
            var src = torButtonPrefabField?.GetValue(null) as ToggleButtonBehaviour;
            if (src == null) return;

            for (int i = 0; i < AllOptions.Length; i++) {
                var info = AllOptions[i];
                var button = Object.Instantiate(src, ucPopUp.transform);
                var pos = new Vector3(i % 2 == 0 ? -1.17f : 1.17f, 1.3f - i / 2 * 0.8f, -0.5f);
                button.transform.localPosition = pos;

                button.onState = info.GetValue();
                button.Background.color = button.onState ? Color.green : Palette.ImpostorRed;
                button.Text.text = info.Title;
                button.Text.fontSizeMin = button.Text.fontSizeMax = 1.8f;
                button.Text.font = Object.Instantiate(ucTitleTemplate?.font);
                button.Text.GetComponent<RectTransform>().sizeDelta = new Vector2(2, 2);
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

        private class UCSelection {
            public string Title;
            public Func<bool> OnClick;
            public Func<bool> GetValue;
            public UCSelection(string title, Func<bool> onClick, Func<bool> getValue) {
                Title = title;
                OnClick = onClick;
                GetValue = getValue;
            }
        }
    }
}
