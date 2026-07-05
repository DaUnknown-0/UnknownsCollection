// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * Adds an "Unknown's Collection" sub-page to TOR's "Mod Options..." popup (gear menu).
 *
 * Lifecycle lessons baked in (the first version silently broke on the SECOND open):
 *  - Cleanup must use GetComponentsInChildren(includeInactive: true) - the popup is inactive
 *    while closed, so the default overload finds nothing and stale titles/toggles pile up.
 *  - Never cache TMP templates/fonts across scenes. TOR re-creates its own titleText on every
 *    MainMenuManager.Start for exactly this reason; we now clone title/font from TOR's live
 *    objects on every open instead of a one-time template.
 *  - Every click listener is wrapped in try/catch WITH logging, and the TOR popup is only
 *    hidden AFTER our popup was built successfully - a throw used to leave both popups hidden,
 *    which looked like "the menu just doesn't open" with a clean log.
 */

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
        private static FieldInfo torTitleTextField;

        private static GameObject ucPopUp;

        private static readonly UCSelection[] AllOptions = {
            new(() => UCLocalization.Tr("uc.ui.options.bug_glitch_toggle"),
                () => {
                    UnknownsCollectionPlugin.BugGlitchEnabled.Value =
                        !UnknownsCollectionPlugin.BugGlitchEnabled.Value;
                    return UnknownsCollectionPlugin.BugGlitchEnabled.Value;
                },
                () => UnknownsCollectionPlugin.BugGlitchEnabled.Value),
            new(() => UCLocalization.Tr("uc.ui.options.button_pulse_toggle"),
                () => {
                    UnknownsCollectionPlugin.ButtonPulseEnabled.Value =
                        !UnknownsCollectionPlugin.ButtonPulseEnabled.Value;
                    return UnknownsCollectionPlugin.ButtonPulseEnabled.Value;
                },
                () => UnknownsCollectionPlugin.ButtonPulseEnabled.Value),
            new(() => UCLocalization.Tr("uc.ui.options.kill_anim_uc_toggle"),
                () => {
                    UnknownsCollectionPlugin.KillAnimationsUC.Value =
                        !UnknownsCollectionPlugin.KillAnimationsUC.Value;
                    return UnknownsCollectionPlugin.KillAnimationsUC.Value;
                },
                () => UnknownsCollectionPlugin.KillAnimationsUC.Value),
            new(() => UCLocalization.Tr("uc.ui.options.kill_anim_tor_toggle"),
                () => {
                    UnknownsCollectionPlugin.KillAnimationsTOR.Value =
                        !UnknownsCollectionPlugin.KillAnimationsTOR.Value;
                    return UnknownsCollectionPlugin.KillAnimationsTOR.Value;
                },
                () => UnknownsCollectionPlugin.KillAnimationsTOR.Value)
        };

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
        public static void OptionsMenuStartPostfix(OptionsMenuBehaviour __instance) {
            try {
                if (__instance.CensorChatButton == null) return;
                ResolveTORFields();

                var parent = __instance.CensorChatButton.transform.parent;
                if (parent == null) return;

                // Find TOR's "Mod Options..." button (TOR creates a fresh one per menu instance)
                for (int i = 0; i < parent.childCount; i++) {
                    var child = parent.GetChild(i);
                    var tb = child.GetComponent<ToggleButtonBehaviour>();
                    if (tb?.Text?.text?.Contains("Mod Options") == true) {
                        HookModOptionsButton(tb);
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
                if (type == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        "[UCOptions] ResolveTORFields: type TheOtherRoles.Patches.ClientOptionsPatch not found - UC menu entry will not be added.");
                    return;
                }
                torPopUpField = AccessTools.Field(type, "popUp");
                torButtonPrefabField = AccessTools.Field(type, "buttonPrefab");
                torTitleTextField = AccessTools.Field(type, "titleText");
                if (torPopUpField == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        "[UCOptions] ResolveTORFields: field 'popUp' not found on ClientOptionsPatch - UC menu entry will not be added.");
                }
                if (torButtonPrefabField == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        "[UCOptions] ResolveTORFields: field 'buttonPrefab' not found on ClientOptionsPatch - UC menu entry will not be added.");
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning(
                    $"[UCOptions] ResolveTORFields failed: {e}");
            }
        }

        private static void HookModOptionsButton(ToggleButtonBehaviour modBtn) {
            var pb = modBtn.GetComponent<PassiveButton>();
            if (pb == null) return;

            pb.OnClick.AddListener((Action)(() => {
                try {
                    var torPopUp = torPopUpField?.GetValue(null) as GameObject;
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[UCOptions] mod-options click: tor={(torPopUp == null ? "null" : torPopUp.activeSelf.ToString())} uc={(ucPopUp == null ? "null/destroyed" : ucPopUp.activeSelf.ToString())}");
                    if (torPopUp == null) return;

                    EnsureUCPopup(torPopUp);
                    AddNavButton(torPopUp);
                    // If our popup was left open (e.g. the settings were closed over it), it
                    // would now sit IN FRONT of TOR's freshly opened popup and swallow every
                    // click - the whole menu looks dead. Always start from TOR's page.
                    if (ucPopUp != null && ucPopUp.activeSelf) ucPopUp.SetActive(false);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError(
                        $"[UCOptions] HookModOptionsButton callback: {e}");
                }
            }));
        }

        private static void EnsureUCPopup(GameObject torPopUp) {
            // Unity's overloaded == also catches "destroyed" - e.g. after the popup got parented
            // under the HUD in-game and died with it on scene change. Then we simply rebuild.
            if (ucPopUp != null) return;

            // Create UC sub-popup from the same prefab as TOR's popup
            ucPopUp = Object.Instantiate(torPopUp);
            Object.DontDestroyOnLoad(ucPopUp);
            var t = ucPopUp.transform;
            var pos = t.localPosition;
            pos.z = -820f;
            t.localPosition = pos;

            // Destroy all children except Background and CloseButton (includeInactive so this
            // also works while the clone source was closed)
            var children = new List<GameObject>();
            for (int i = 0; i < ucPopUp.transform.childCount; i++)
                children.Add(ucPopUp.transform.GetChild(i).gameObject);
            foreach (var child in children) {
                if (child.name != "Background" && child.name != "CloseButton")
                    Object.Destroy(child);
            }

            // Wire the CloseButton to go back to TOR's popup instead of just closing. TOR's popup
            // is looked up fresh at click time (not captured) - it is a different object by then
            // if either popup was rebuilt in between.
            var closeBtn = ucPopUp.transform.Find("CloseButton");
            if (closeBtn != null) {
                var passive = closeBtn.GetComponent<PassiveButton>();
                if (passive != null) {
                    passive.OnClick = new ButtonClickedEvent();
                    passive.OnClick.AddListener((Action)(() => {
                        try {
                            ucPopUp.SetActive(false);
                            var tor = torPopUpField?.GetValue(null) as GameObject;
                            if (tor != null) tor.SetActive(true);
                            UnknownsCollectionPlugin.Logger?.LogInfo(
                                $"[UCOptions] close click: tor={(tor == null ? "null" : tor.activeSelf.ToString())}");
                        } catch (Exception e) {
                            UnknownsCollectionPlugin.Logger?.LogError(
                                $"[UCOptions] close click: {e}");
                        }
                    }));
                }
            }

            ucPopUp.SetActive(false);
        }

        private static void AddNavButton(GameObject torPopUp) {
            // Already added? (includeInactive: the popup may be closed at this point)
            foreach (var t in torPopUp.GetComponentsInChildren<ToggleButtonBehaviour>(true))
                if (t.name == "UCNavButton")
                    return;

            // Grab TOR's buttonPrefab to clone a consistent-looking button
            var prefab = torButtonPrefabField?.GetValue(null) as ToggleButtonBehaviour;
            if (prefab == null) {
                // Fallback: clone any existing toggle in TOR's popup
                foreach (var t in torPopUp.GetComponentsInChildren<ToggleButtonBehaviour>(true)) {
                    prefab = t; break;
                }
            }
            if (prefab == null) return;

            var nav = Object.Instantiate(prefab, torPopUp.transform);
            nav.name = "UCNavButton";
            nav.gameObject.SetActive(true);
            nav.Text.text = UCLocalization.Tr("uc.ui.options.nav_title");
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
                try {
                    var tor = torPopUpField?.GetValue(null) as GameObject;
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[UCOptions] nav click: tor={(tor == null ? "null" : tor.activeSelf.ToString())} uc={(ucPopUp == null ? "null" : ucPopUp.activeSelf.ToString())}");
                    if (tor == null) return;
                    EnsureUCPopup(tor);      // rebuild if it died with a scene change
                    ShowUCPopup(tor);        // build content FIRST...
                    tor.SetActive(false);    // ...only hide TOR's popup once that worked
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[UCOptions] nav click done: uc active={ucPopUp.activeSelf} activeInHierarchy={ucPopUp.activeInHierarchy} parent={(ucPopUp.transform.parent == null ? "null" : ucPopUp.transform.parent.name)} pos={ucPopUp.transform.position}");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCOptions] nav click: {e}");
                }
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

            // (Re)create title + toggles every time so state is fresh. includeInactive is
            // essential: the popup is inactive while closed, and the default overload would skip
            // its children entirely - stale UI then piled up on every reopen.
            foreach (var t in ucPopUp.GetComponentsInChildren<ToggleButtonBehaviour>(true))
                Object.Destroy(t.gameObject);
            foreach (var tmp in ucPopUp.GetComponentsInChildren<TextMeshPro>(true))
                if (tmp.name == "UCTitle")
                    Object.Destroy(tmp.gameObject);

            SetUpUCOptions();

            ucPopUp.SetActive(true);
        }

        private static void SetUpUCOptions() {
            // Grab a prefab from TOR's popup (recreated by TOR on every options-menu Start, so it
            // is alive - unlike a template cached once, whose font asset can die across scenes)
            var src = torButtonPrefabField?.GetValue(null) as ToggleButtonBehaviour;
            if (src == null || src.Text == null) {
                UnknownsCollectionPlugin.Logger?.LogWarning(
                    "[UCOptions] SetUpUCOptions: TOR buttonPrefab unavailable - popup stays empty.");
                return;
            }

            // TOR's title font if available (matches the "More Options..." look); the prefab's
            // own font as fallback. Both are live objects, re-created by TOR when scenes change.
            var torTitle = torTitleTextField?.GetValue(null) as TextMeshPro;
            var font = torTitle != null ? torTitle.font : src.Text.font;

            // Title, cloned from the live prefab text. The button text's rect is only ~2 units
            // wide - without widening it + disabling wrapping, the title breaks after every few
            // characters and floods the popup.
            var title = Object.Instantiate(src.Text, ucPopUp.transform);
            title.name = "UCTitle";
            title.text = UCLocalization.Tr("uc.ui.options.nav_title");
            if (font != null) title.font = font;
            title.enableAutoSizing = false;
            title.enableWordWrapping = false;
            title.fontSize = title.fontSizeMin = title.fontSizeMax = 4f;
            title.alignment = TextAlignmentOptions.Center;
            title.rectTransform.sizeDelta = new Vector2(6f, 1.2f);
            title.transform.localPosition = new Vector3(0f, 2.3f, -0.5f);
            title.transform.localScale = Vector3.one;
            title.gameObject.SetActive(true);

            for (int i = 0; i < AllOptions.Length; i++) {
                var info = AllOptions[i];
                var button = Object.Instantiate(src, ucPopUp.transform);
                var pos = new Vector3(i % 2 == 0 ? -1.17f : 1.17f, 1.3f - i / 2 * 0.8f, -0.5f);
                button.transform.localPosition = pos;

                button.onState = info.GetValue();
                button.Background.color = button.onState ? Color.green : Palette.ImpostorRed;
                button.Text.text = info.Title;
                button.Text.fontSizeMin = button.Text.fontSizeMax = 1.8f;
                if (font != null) button.Text.font = font;
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
                    try {
                        button.onState = info.OnClick();
                        button.Background.color = button.onState ? Color.green : Palette.ImpostorRed;
                    } catch (Exception e) {
                        UnknownsCollectionPlugin.Logger?.LogError($"[UCOptions] toggle click: {e}");
                    }
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
            private readonly Func<string> titleFn;
            public string Title => titleFn();
            public Func<bool> OnClick;
            public Func<bool> GetValue;
            public UCSelection(Func<string> title, Func<bool> onClick, Func<bool> getValue) {
                titleFn = title;
                OnClick = onClick;
                GetValue = getValue;
            }
        }
    }
}
