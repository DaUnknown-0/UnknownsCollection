// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UCGameSkin - the game's own menu sprites for UC's screen-space panels (User 2026-11-13: the menus have to really
 * look like Among Us). The same lookup as TOR - Forgotten Fixes' GameUI.cs, copied because UC must not depend on
 * it: the settings window's sprites are found by
 * name among the loaded ones and pinned (DontUnloadUnusedAsset) when a lobby is up. Where a sprite is missing the
 * panels draw their old shapes. The pieces:
 *   Window   the settings window picture (about 2:1) or the black field with the light frame (any size)
 *   Title    Brook, white, upper case, with the window's divider under it
 *   Plate    the button plate: teal = the window's own (dark teal label), any other colour tints the light plate
 *   Close    the white X of the settings menu
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace UnknownsCollection {

    internal static class UCGameSkin {
        internal static readonly Color TabText = new Color32(0x10, 0x58, 0x4B, 0xFF);
        internal static readonly Color Teal = new Color(0.16f, 0.70f, 0.66f);

        private static readonly string[] Wanted = {
            "IMAGE-PanelBG", "IMAGE-TitleDivider", "IMAGE-FieldCube", "IMAGE-LabelCube", "CTAInactive", "CTAActive",
            "BUTTON-ExitStandard", "BUTTON-ExitHover", "IMAGE-Subheader", "BUTTON-CheckboxInactive",
        };
        private static readonly Dictionary<string, Sprite> sprites = new();
        private static float searchedAt = -100f;

        internal static Sprite Sprite(string name) {
            if (sprites.TryGetValue(name, out var s) && s != null) return s;
            Search();
            return sprites.TryGetValue(name, out s) ? s : null;
        }

        internal static bool Ready => Sprite("IMAGE-FieldCube") != null && Sprite("CTAInactive") != null;

        [HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
        private static class LobbyWarm {
            public static void Postfix() { try { searchedAt = -100f; Search(); } catch { } }
        }

        private static void Search() {
            if (sprites.Count >= Wanted.Length || Time.realtimeSinceStartup - searchedAt < 2f) return;
            searchedAt = Time.realtimeSinceStartup;
            try {
                var want = new HashSet<string>(Wanted);
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>())) {
                    var sp = o.TryCast<Sprite>();
                    if (sp == null || !want.Contains(sp.name) || sprites.ContainsKey(sp.name)) continue;
                    sp.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    if (sp.texture != null) sp.texture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    sprites[sp.name] = sp;
                }
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[UCGameSkin] sprite search failed: {e.Message}"); }
        }

        // ---- fonts ----
        private static readonly Dictionary<string, TMPro.TMP_FontAsset> fonts = new();
        private static float fontsAt = -100f;

        internal static TMPro.TMP_FontAsset Font(string name) {
            if (fonts.TryGetValue(name, out var f) && f != null) return f;
            if (Time.realtimeSinceStartup - fontsAt < 2f) return null;
            fontsAt = Time.realtimeSinceStartup;
            try {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMPro.TMP_FontAsset>())) {
                    var fa = o.TryCast<TMPro.TMP_FontAsset>();
                    if (fa != null && !fonts.ContainsKey(fa.name)) fonts[fa.name] = fa;
                }
            } catch { }
            return fonts.TryGetValue(name, out f) ? f : null;
        }

        // ---- pieces ----
        /// <summary>Puts the game's window behind a panel object of that size (call instead of drawing body + frame).</summary>
        internal static void Window(GameObject panel, Vector2 size, Color frameTint) {
            var img = panel.GetComponent<Image>() ?? panel.AddComponent<Image>();
            float aspect = size.y > 0 ? size.x / size.y : 2f;
            var bg = Sprite("IMAGE-PanelBG");
            if (aspect >= 1.55f && aspect <= 2.6f && bg != null) { img.sprite = bg; img.type = Image.Type.Simple; img.color = Color.white; }
            else { img.sprite = Sprite("IMAGE-FieldCube"); img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 0.8f; img.color = frameTint; }
            img.raycastTarget = true;
        }

        /// <summary>The title in Brook at the top-left of a panel, the divider under it. Returns the y under the divider.</summary>
        internal static float Title(GameObject panel, string text, float inset, float height = 56f) {
            var go = new GameObject("Title");
            go.transform.SetParent(panel.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(inset + 26f, -inset - 8f); rt.sizeDelta = new Vector2(-2 * inset - 160f, height);
            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            t.text = text; t.fontSize = 50f; t.color = Color.white; t.alignment = TMPro.TextAlignmentOptions.Left;
            t.fontStyle = TMPro.FontStyles.UpperCase; t.enableWordWrapping = false; t.enableAutoSizing = true;
            t.fontSizeMin = 18f; t.fontSizeMax = 50f; t.raycastTarget = false;
            var brook = Font("Brook SDF");
            if (brook != null) t.font = brook;
            var ds = Sprite("IMAGE-TitleDivider");
            var d = new GameObject("TitleDivider");
            d.transform.SetParent(panel.transform, false);
            var drt = d.AddComponent<RectTransform>();
            drt.anchorMin = new Vector2(0, 1); drt.anchorMax = new Vector2(1, 1); drt.pivot = new Vector2(0.5f, 1);
            drt.anchoredPosition = new Vector2(0, -inset - 8f - height); drt.sizeDelta = new Vector2(-2 * inset - 52f, 3f);
            var di = d.AddComponent<Image>();
            di.sprite = ds; di.color = ds != null ? Color.white : new Color(0.35f, 0.35f, 0.35f); di.raycastTarget = false;
            return -inset - 8f - height - 10f;
        }

        private static bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.12f;

        /// <summary>A button plate filling its parent, flat (no gloss). Returns the label colour to use on it.</summary>
        internal static Color Plate(GameObject button, Color color) {
            bool teal = Near(color, Teal);
            var go = new GameObject("Fill");
            go.transform.SetParent(button.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.sizeDelta = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.sprite = Sprite(teal ? "CTAInactive" : "CTAActive");
            img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 0.9f;
            img.color = teal ? Color.white : color;
            img.raycastTarget = false;
            if (teal) return TabText;
            float lum = 0.299f * color.r + 0.587f * color.g + 0.114f * color.b;
            return lum < 0.8f ? Color.white : new Color(0.1f, 0.1f, 0.1f);
        }

        /// <summary>The white X of the settings menu on the panel's top-right corner.</summary>
        internal static GameObject Close(GameObject panel, Action onClose) {
            var go = new GameObject("Close");
            go.transform.SetParent(panel.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(-6f, -6f); rt.sizeDelta = new Vector2(78f, 78f);
            var img = go.AddComponent<Image>();
            img.sprite = Sprite("BUTTON-ExitStandard"); img.preserveAspect = true; img.raycastTarget = true;
            var b = go.AddComponent<Button>();
            b.targetGraphic = img;
            var hover = Sprite("BUTTON-ExitHover");
            if (hover != null) {
                b.transition = Selectable.Transition.SpriteSwap;
                var st = b.spriteState; st.highlightedSprite = hover; st.pressedSprite = hover; b.spriteState = st;
            }
            b.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClose()));
            return go;
        }
    }
}
