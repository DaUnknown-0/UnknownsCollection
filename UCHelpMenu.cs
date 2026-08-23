// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHelpMenu - the "?" help button (top right, lobby AND in-game) plus the role overview panel.
 *
 * The panel lists every Unknown's Collection role that COULD be active this game (spawn rate > 0 -
 * option-based, so it never leaks which roles actually spawned; same anti-leak rule as the crew
 * SEARCH button). Clicking a role shows a detail card: team, a detailed explanation and a small
 * looping DEMO animation acting out the role's mechanic (stateless per-frame vignettes built from
 * the shared UCFx sprites, driven by the same HudManager.Update patch that does the camera fit).
 * A language row toggles between Deutsch and English (persisted via BepInEx config).
 *
 * UI mechanics: plain SpriteRenderer/TextMeshPro objects parented to the HudManager (world-space,
 * sortingOrder 500+ - above world/HUD, below Helpers.showFlash's 999 flashes, see BeaconFx).
 * Clicks AND hover are resolved MANUALLY each frame (Camera.main.ScreenToWorldPoint vs. stored hit
 * boxes) instead of PassiveButtons - no collider/layer wrangling, works identically in the lobby
 * and in-game. The "?" button is anchored to the top-right screen edge via AspectPosition (the same
 * component TOR's draft uses), below the vanilla settings/mod buttons.
 *
 * Visibility follows hud.UseButton.isActiveAndEnabled - exactly the gate CustomButton uses - so the
 * button shows in the lobby and during rounds but not in meetings/intro; an open panel force-closes
 * when the gate drops.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using TMPro;

namespace UnknownsCollection {
    public static class UCHelpMenu {
        // ---- role table ----
        private enum Faction { Impostor, Crew, Neutral, Ghost, Modifier }

        private sealed class Entry {
            public string name;
            public Faction faction;
            public Func<Color> color;
            public Func<CustomOption> rate;
            public string key;          // localization key (uc.help.* / tor.help.*)
        }

        private static List<Entry> entries;
        private static List<Entry> Entries() {
            if (entries != null) return entries;
            entries = new List<Entry> {
                E("Tesla", Faction.Impostor, () => Tesla.Color, () => Tesla.SpawnRate, "uc.help.tesla"),
                E("Saboteur", Faction.Impostor, () => Saboteur.Color, () => Saboteur.SpawnRate, "uc.help.saboteur"),
                E("Poisoner", Faction.Impostor, () => Palette.ImpostorRed, () => Poisoner.SpawnRate, "uc.help.poisoner"),
                E("Silencer", Faction.Impostor, () => Palette.ImpostorRed, () => Silencer.SpawnRate, "uc.help.silencer"),
                E("Illusionist", Faction.Impostor, () => Palette.ImpostorRed, () => Illusionist.SpawnRate, "uc.help.illusionist"),
                E("Maniac", Faction.Impostor, () => Maniac.Color, () => Maniac.SpawnRate, "uc.help.maniac"),
                E("Shade", Faction.Impostor, () => Palette.ImpostorRed, () => Shade.SpawnRate, "uc.help.shade"),
                E("Manipulator", Faction.Impostor, () => Palette.ImpostorRed, () => Manipulator.SpawnRate, "uc.help.manipulator"),
                E("Auditor", Faction.Impostor, () => Palette.ImpostorRed, () => Auditor.SpawnRate, "uc.help.auditor"),
                E("Werewolf", Faction.Impostor, () => Werewolf.Color, () => Werewolf.SpawnRate, "uc.help.werewolf"),

                E("Gambler", Faction.Modifier, () => Gambler.Color, () => Gambler.SpawnRate, "uc.help.gambler"),

                E("Siphoner", Faction.Crew, () => Siphoner.Color, () => Siphoner.SpawnRate, "uc.help.siphoner"),
                E("Witness", Faction.Crew, () => Witness.Color, () => Witness.SpawnRate, "uc.help.witness"),
                E("Scout", Faction.Crew, () => Scout.Color, () => Scout.SpawnRate, "uc.help.scout"),
                E("Beacon", Faction.Crew, () => Beacon.Color, () => Beacon.SpawnRate, "uc.help.beacon"),
                // The Hunter has no spawn rate of his own (he is the Sheriff's endgame inside a
                // Werewolf round), so his visibility gate is borrowed: the WEREWOLF rate decides
                // whether he can exist at all, and returning null while option 1502 is off makes the
                // list filter drop the row entirely. Same anti-leak rule as every other entry - it is
                // option state only, never "did he actually rise this round".
                E("Hunter", Faction.Crew, () => Hunter.Color,
                  () => (Hunter.Enabled != null && Hunter.Enabled.getBool()) ? Werewolf.SpawnRate : null,
                  "uc.help.hunter"),

                E("Bug", Faction.Neutral, () => Bug.Color, () => Bug.SpawnRate, "uc.help.bug"),
                E("Follower", Faction.Neutral, () => Follower.Color, () => Follower.SpawnRate, "uc.help.follower"),
                E("Copycat", Faction.Neutral, () => Copycat.Color, () => Copycat.SpawnRate, "uc.help.copycat"),
                E("Collector", Faction.Neutral, () => Collector.Color, () => Collector.SpawnRate, "uc.help.collector"),
                E("Pelican", Faction.Neutral, () => Pelican.Color, () => Pelican.SpawnRate, "uc.help.pelican"),
                // AUDIT-2026-08-15: Necromancer was missing from the guide entirely despite being a
                // spawnable Neutral role. Fixed AUDIT-2026-08-23 (L-19): "uc.help.necromancer" now has
                // an English reference entry (Resources/Localization/en.json); every other language
                // table falls back to it automatically via Tr()/TrIn(), so no per-language work is
                // needed here.
                E("Necromancer", Faction.Neutral, () => Necromancer.Color, () => Necromancer.SpawnRate, "uc.help.necromancer"),

                E("Poltergeist", Faction.Ghost, () => Poltergeist.Color, () => Poltergeist.SpawnRate, "uc.help.poltergeist"),
            };

            // Merge in every TOR role/modifier (UCHelpTORData) so the guide covers ALL
            // roles that can appear, then group by faction and sort alphabetically.
            foreach (var t in UCHelpTORData.Entries()) {
                var f = t.IsModifier ? Faction.Modifier
                    : t.Faction == 0 ? Faction.Impostor
                    : t.Faction == 2 ? Faction.Neutral
                    : Faction.Crew;
                var tc = t.Color;
                var fCopy = f;
                entries.Add(new Entry {
                    name = t.Name, faction = f, key = t.Key, rate = t.Rate,
                    color = () => { try { return tc != null ? tc() : FactionColor(fCopy); } catch { return FactionColor(fCopy); } },
                });
            }
            entries.Sort((a, b) => {
                int fa = FactionOrder(a.faction), fb = FactionOrder(b.faction);
                return fa != fb ? fa.CompareTo(fb) : string.CompareOrdinal(a.name, b.name);
            });
            return entries;
        }

        private static int FactionOrder(Faction f) => f switch {
            Faction.Impostor => 0, Faction.Neutral => 1, Faction.Ghost => 2,
            Faction.Crew => 3, _ => 4,
        };

        private static Entry E(string name, Faction f, Func<Color> color, Func<CustomOption> rate,
                               string key)
            => new Entry { name = name, faction = f, color = color, rate = rate, key = key };

        // ---- session language ----
        // The guide follows the active mod/game language (UCLocalization) but can be
        // overridden PER SESSION: a quick "EN | <language>" toggle when the game runs in a
        // non-English language, plus a full dropdown grid when the base language is
        // English (there is nothing to toggle to then). Never persisted, never touching
        // the mod language - exactly the "only for this UI" behavior asked for.
        private static string sessionLang;          // null = follow the active language
        private static bool langDropdownOpen;
        private static string searchQuery = "";
        private static bool searchFocused;          // typing only goes to the search after clicking it
        private static int scrollLines;
        private static int maxScroll;

        private static readonly string[] LangNames = {
            "English", "Español (LA)", "Português (BR)", "Português", "Korean", "Русский",
            "Nederlands", "Filipino", "Français", "Deutsch", "Italiano", "Japanese",
            "Español", "Chinese (S)", "Chinese (T)", "Gaeilge",
            "Türkçe", "Polski", "Czech", "Magyar", "Română", "Svenska", "Suomi",
            "Ukrainian", "Indonesia", "Tieng Viet"
        }; // parallel to UCLocalization.KnownCodes (glyph-safe for the HUD TMP font)

        private static string BaseLang() {
            var c = UCLocalization.ActiveCode;
            return string.IsNullOrEmpty(c) ? "en" : c;
        }
        private static string HelpLang() => sessionLang ?? BaseLang();
        private static string T(string key) => GlyphSafe(UCLocalization.TrIn(HelpLang(), key));

        // ---- glyph safety ----
        // Every help text clones the HUD kill-timer TMP, whose atlas only covers the VANILLA
        // languages. The Tier-B mod languages (uk/tr/pl/cs/hu/ro/vi/...) use letters that are
        // simply not in it and render as boxes (Ukrainian і/ї/є/ґ etc.). Fold every character
        // the font cannot display onto the closest one it CAN: explicit map first, then a
        // generic strip-the-diacritics pass; anything still unresolved is left untouched (a
        // box), so the fold can never make things worse. Decisions are cached per character.
        private static TMP_FontAsset glyphFont;
        private static readonly Dictionary<char, string> glyphFold = new Dictionary<char, string>();
        private static readonly Dictionary<char, string[]> GlyphMap = new Dictionary<char, string[]> {
            // Ukrainian (base Cyrillic exists via Russian, these four don't)
            { 'і', new[] { "i" } }, { 'І', new[] { "I" } },
            { 'ї', new[] { "ï", "i" } }, { 'Ї', new[] { "Ï", "I" } },
            { 'є', new[] { "е", "e" } }, { 'Є', new[] { "Е", "E" } },
            { 'ґ', new[] { "г", "r" } }, { 'Ґ', new[] { "Г", "R" } },
            // Turkish
            { 'ı', new[] { "i" } }, { 'İ', new[] { "I" } },
            { 'ğ', new[] { "g" } }, { 'Ğ', new[] { "G" } },
            { 'ş', new[] { "s" } }, { 'Ş', new[] { "S" } },
            // Polish / Vietnamese letters that do NOT decompose to a base letter
            { 'ł', new[] { "l" } }, { 'Ł', new[] { "L" } },
            { 'đ', new[] { "d" } }, { 'Đ', new[] { "D" } },
            // Hungarian double-acute -> umlaut first, bare vowel second
            { 'ő', new[] { "ö", "o" } }, { 'Ő', new[] { "Ö", "O" } },
            { 'ű', new[] { "ü", "u" } }, { 'Ű', new[] { "Ü", "U" } },
            // Romanian comma-below -> cedilla first, bare letter second
            { 'ș', new[] { "ş", "s" } }, { 'Ș', new[] { "Ş", "S" } },
            { 'ț', new[] { "ţ", "t" } }, { 'Ț', new[] { "Ţ", "T" } },
            // Typography that may be missing from a HUD font
            { '’', new[] { "'" } }, { '‘', new[] { "'" } },
            { '“', new[] { "\"" } }, { '”', new[] { "\"" } },
            { '–', new[] { "-" } }, { '—', new[] { "-" } },
            { '…', new[] { "..." } },
        };

        private static bool FontHas(char c) {
            if (c <= 0x7F) return true; // ASCII always present in the HUD font
            try { return glyphFont != null && glyphFont.HasCharacter(c, true, true); } catch { return true; }
        }
        private static bool FontHasAll(string s) {
            foreach (var ch in s) if (!FontHas(ch)) return false;
            return true;
        }

        // Replacement for c, or null = keep the character as-is.
        private static string FoldChar(char c) {
            if (glyphFold.TryGetValue(c, out var cached)) return cached;
            string rep = null;
            if (!FontHas(c)) {
                if (GlyphMap.TryGetValue(c, out var cands))
                    foreach (var cand in cands) { if (FontHasAll(cand)) { rep = cand; break; } }
                if (rep == null) {
                    try { // generic: decompose and drop the combining marks (á->a, ą->a, ř->r ...)
                        var norm = c.ToString().Normalize(System.Text.NormalizationForm.FormD);
                        var sb = new System.Text.StringBuilder();
                        foreach (var ch in norm)
                            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                                != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
                        string basic = sb.ToString();
                        if (basic.Length > 0 && basic != c.ToString() && FontHasAll(basic)) rep = basic;
                    } catch { }
                }
            }
            glyphFold[c] = rep;
            return rep;
        }

        internal static string GlyphSafe(string s) {
            if (string.IsNullOrEmpty(s) || glyphFont == null) return s;
            System.Text.StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                string rep = c > 0x7F ? FoldChar(c) : null;
                if (rep == null) { sb?.Append(c); continue; }
                if (sb == null) { sb = new System.Text.StringBuilder(s.Length + 8); sb.Append(s, 0, i); }
                sb.Append(rep);
            }
            return sb == null ? s : sb.ToString();
        }
        private static string LangName(string code) {
            int i = Array.IndexOf(UCLocalization.KnownCodes, code);
            return i >= 0 && i < LangNames.Length ? LangNames[i] : code;
        }

        private static string FactionHeader(Faction f) => f switch {
            Faction.Impostor => T("uc.helpui.header_impostor"),
            Faction.Crew => T("uc.helpui.header_crew"),
            Faction.Neutral => T("uc.helpui.header_neutral"),
            Faction.Ghost => T("uc.helpui.header_ghost"),
            _ => T("uc.helpui.header_modifier"),
        };

        private static string FactionTeamLine(Faction f) => f switch {
            Faction.Impostor => T("uc.helpui.team_impostor"),
            Faction.Crew => T("uc.helpui.team_crew"),
            Faction.Neutral => T("uc.helpui.team_neutral"),
            Faction.Ghost => T("uc.helpui.team_ghost"),
            _ => T("uc.helpui.team_modifier"),
        };

        private static Color FactionColor(Faction f) => f switch {
            Faction.Impostor => Palette.ImpostorRed,
            Faction.Crew => new Color(0.55f, 0.85f, 1f),
            Faction.Neutral => new Color(0.78f, 0.78f, 0.82f),
            Faction.Ghost => new Color(0.72f, 0.55f, 1f),
            _ => new Color(1f, 0.78f, 0.35f),   // modifiers: warm gold
        };

        // ---- theme ----
        internal static readonly Color Accent = new Color(1f, 0.82f, 0.35f);          // UC gold
        private static readonly Color PanelBg = new Color(0.055f, 0.07f, 0.115f, 0.97f);
        private static readonly Color HeaderBg = new Color(0.10f, 0.125f, 0.20f, 1f);
        private static readonly Color CardBg = new Color(0.085f, 0.105f, 0.165f, 1f);
        private static readonly Color BorderCol = new Color(1f, 0.82f, 0.35f, 0.55f);
        private const int SortBg = 500;
        private const int SortMid = 501;
        private const int SortText = 502;

        // ---- geometry (design units; designed for orthographic size 3) ----
        private const float PanelW = 8.6f, PanelH = 4.9f;
        private const float HeaderH = 0.52f;
        private const float DesignOrtho = 3f;

        // Among Us renders the HUD layer through a SEPARATE "UI Camera", not Camera.main (TOR's
        // Helpers.toggleZoom adjusts both individually). Measuring/centring through Camera.main
        // therefore kept landing the panel off-centre - the maths must run through the camera that
        // ACTUALLY renders our layer. Resolve it by name + culling mask, cache it, fall back to main.
        private static Camera fitCam;
        private static Camera FitCamera(int layer) {
            if (fitCam != null && fitCam.isActiveAndEnabled && (fitCam.cullingMask & (1 << layer)) != 0)
                return fitCam;
            fitCam = null;
            var all = Camera.allCameras;
            foreach (var c in all)
                if (c != null && c.gameObject.name == "UI Camera" && (c.cullingMask & (1 << layer)) != 0) { fitCam = c; break; }
            if (fitCam == null)
                foreach (var c in all)
                    if (c != null && c != Camera.main && (c.cullingMask & (1 << layer)) != 0) { fitCam = c; break; }
            if (fitCam == null) fitCam = Camera.main;
            if (fitCam != null)
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[UCHelpMenu] fit camera resolved: {fitCam.gameObject.name} ortho={fitCam.orthographicSize} " +
                    $"pos={fitCam.transform.position} hasLayer={(fitCam.cullingMask & (1 << layer)) != 0}");
            return fitCam;
        }

        // The visible world rect is measured through ScreenToWorldPoint on the screen corners of the
        // RENDERING camera (see FitCamera), the panel is placed at that rect's centre and scaled so
        // the design (8.6 x 4.9 on a 6-unit-tall screen) keeps its intended proportion - clamped to
        // never exceed the visible width. Applied every frame; the hit tests run in panel-LOCAL
        // space through the same camera, so they stay correct under any scale.
        private static void ApplyCameraFit(GameObject go) {
            if (go == null) return;
            var cam = FitCamera(go.layer);
            if (cam == null) return;
            Vector3 bl = cam.ScreenToWorldPoint(new Vector3(0f, 0f, 10f));
            Vector3 tr = cam.ScreenToWorldPoint(new Vector3(Screen.width, Screen.height, 10f));
            float visW = Mathf.Abs(tr.x - bl.x), visH = Mathf.Abs(tr.y - bl.y);
            if (visW < 0.01f || visH < 0.01f) return;
            float scale = Mathf.Min(visH / (DesignOrtho * 2f), visW * 0.98f / PanelW);
            float parentScale = go.transform.parent != null ? go.transform.parent.lossyScale.x : 1f;
            if (parentScale < 0.0001f) parentScale = 1f;
            go.transform.localScale = Vector3.one * (scale / parentScale);
            var p = go.transform.position;
            go.transform.position = new Vector3((bl.x + tr.x) / 2f, (bl.y + tr.y) / 2f, p.z);
        }

        private sealed class HitBox {
            public Transform anchor;         // world-space centre
            public float w, h;
            public Action onClick;
            public SpriteRenderer hover;     // optional row highlight
            public Entry entry;              // row entries (for selected state)
            public bool isRow;               // true = rebuilt by BuildList (scroll/search)
        }

        private static GameObject button;
        private static GameObject panel;
        private static TextMeshPro detailTitle, detailTeam, detailBody;
        private static readonly List<HitBox> hits = new List<HitBox>();
        private static Entry selected;

        private static Sprite whiteSprite;
        private static Sprite WhiteSprite() {
            if (whiteSprite != null) return whiteSprite;
            var tex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave;
            whiteSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            whiteSprite.hideFlags |= HideFlags.HideAndDontSave;
            return whiteSprite;
        }

        private static TextMeshPro NewText(Transform parent, string text, float fontSize, Color color,
                                           TextAlignmentOptions alignment = TextAlignmentOptions.Left) {
            var template = HudManager.Instance.KillButton.cooldownTimerText;
            if (glyphFont == null && template != null) glyphFont = template.font; // for GlyphSafe
            var tmp = UnityEngine.Object.Instantiate(template, parent);
            tmp.gameObject.SetActive(true);
            tmp.transform.localScale = Vector3.one;
            tmp.transform.localPosition = Vector3.zero;
            // The clone inherits the kill button's RectTransform; TMP aligns text INSIDE that rect,
            // so Left/Right-aligned labels land half a rect away from the intended anchor. Collapse
            // the rect to a point: the transform position becomes the exact alignment anchor
            // (Left = text starts there, Center = centred there, Right = text ends there).
            // detailBody/detailTip re-widen the rect (sizeDelta) after this for word wrapping.
            var rt = tmp.rectTransform;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            tmp.margin = Vector4.zero;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.enableAutoSizing = false;
            tmp.enableWordWrapping = false;
            tmp.alignment = alignment;
            tmp.color = color;
            var mr = tmp.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = SortText;
            return tmp;
        }

        private static SpriteRenderer NewRect(Transform parent, Vector3 localPos, Vector2 size, Color color, int sort = SortBg) {
            var go = new GameObject("UCHelpRect");
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = WhiteSprite();
            sr.color = color;
            sr.sortingOrder = sort;
            return sr;
        }

        // Thin border frame around a rect (4 lines).
        private static void NewFrame(Transform parent, Vector3 center, Vector2 size, Color color, float thickness = 0.025f, int sort = SortMid) {
            NewRect(parent, center + new Vector3(0, size.y / 2f, 0), new Vector2(size.x, thickness), color, sort);
            NewRect(parent, center + new Vector3(0, -size.y / 2f, 0), new Vector2(size.x, thickness), color, sort);
            NewRect(parent, center + new Vector3(-size.x / 2f, 0, 0), new Vector2(thickness, size.y), color, sort);
            NewRect(parent, center + new Vector3(size.x / 2f, 0, 0), new Vector2(thickness, size.y), color, sort);
        }

        // ====================================================================
        // "?" button (created once per HUD)
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    panel = null; // stale references from the previous HUD (its objects died with it)
                    hits.Clear();
                    selected = null;
                    stage = null;
                    stageRole = null;
                    cast.Clear();
                    castTexts.Clear();
                    figs.Clear();
                    btns.Clear();

                    button = new GameObject("UCHelpButton");
                    button.layer = __instance.gameObject.layer;
                    button.transform.SetParent(__instance.transform, false);

                    var ring = new GameObject("ring");
                    ring.layer = button.layer;
                    ring.transform.SetParent(button.transform, false);
                    var rr = ring.AddComponent<SpriteRenderer>();
                    rr.sprite = UCFx.Ring;
                    rr.color = new Color(Accent.r, Accent.g, Accent.b, 0.75f);
                    rr.sortingOrder = SortBg;
                    ring.transform.localScale = Vector3.one * 0.5f;

                    var q = NewText(button.transform, "?", 3.0f, new Color(1f, 1f, 1f, 0.9f), TextAlignmentOptions.Center);
                    q.transform.localPosition = new Vector3(0f, 0f, -0.1f);

                    var ap = button.AddComponent<AspectPosition>();
                    ap.Alignment = AspectPosition.EdgeAlignments.RightTop;
                    ap.DistanceFromEdge = new Vector3(0.5f, 2.3f, -10f);
                    ap.AdjustPosition();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCHelpMenu] button creation failed: {e}");
                }
            }
        }

        // ====================================================================
        // Panel build / teardown
        // ====================================================================
        private static void TogglePanel() {
            if (panel != null) ClosePanel();
            else OpenPanel();
        }

        private static void ClosePanel() {
            try { if (panel != null) UnityEngine.Object.Destroy(panel); } catch { }
            panel = null;
            hits.Clear();
            selected = null;
            listRoot = null;
            searchLabel = null;
            langDropdownOpen = false;
            searchQuery = "";
            searchFocused = false;
            scrollLines = 0;
            genericKind = -1;
            stage = null; // died with the panel
            stageRole = null;
            cast.Clear();
            castTexts.Clear();
            figs.Clear();
            btns.Clear();
        }

        // withDropdown: ClosePanel() (run first for the rebuild) always resets langDropdownOpen,
        // so the desired state must travel INTO the rebuild as a parameter - toggling the flag
        // before calling OpenPanel() gets silently swallowed by that reset.
        private static void OpenPanel(bool withDropdown = false) {
            try {
                var hud = HudManager.Instance;
                if (hud == null) return;
                ClosePanel();
                langDropdownOpen = withDropdown;
                // resolve the glyph-fold font BEFORE the first T() call of this build
                try {
                    if (glyphFont == null && hud.KillButton != null && hud.KillButton.cooldownTimerText != null)
                        glyphFont = hud.KillButton.cooldownTimerText.font;
                } catch { }

                panel = new GameObject("UCHelpPanel");
                panel.layer = hud.gameObject.layer;
                panel.transform.SetParent(hud.transform, false);
                panel.transform.localPosition = new Vector3(0f, 0f, -30f);
                ApplyCameraFit(panel); // centre + scale immediately (also re-applied every frame)

                float topY = PanelH / 2f;

                // Backdrop + gold frame + header bar
                NewRect(panel.transform, Vector3.zero, new Vector2(PanelW, PanelH), PanelBg);
                NewFrame(panel.transform, Vector3.zero, new Vector2(PanelW, PanelH), BorderCol);
                NewRect(panel.transform, new Vector3(0f, topY - HeaderH / 2f, -0.02f), new Vector2(PanelW, HeaderH), HeaderBg, SortMid);
                NewRect(panel.transform, new Vector3(0f, topY - HeaderH, -0.03f), new Vector2(PanelW, 0.03f), BorderCol, SortMid);

                float headY = topY - HeaderH / 2f - 0.02f;
                var title = NewText(panel.transform, T("uc.helpui.title"), 1.45f, Color.white);
                title.transform.localPosition = new Vector3(-PanelW / 2f + 0.25f, headY, -0.1f);

                // Language control: quick EN|<base> toggle when the game language is not
                // English; a full session dropdown (3-column grid) when it is.
                string baseLang = BaseLang();
                string langLabel = baseLang != "en"
                    ? (HelpLang() == "en"
                        ? $"EN <color=#777777>| {LangName(baseLang)}</color>"
                        : $"<color=#777777>EN |</color> {LangName(baseLang)}")
                    : $"{LangName(HelpLang())} {(langDropdownOpen ? "^" : "v")}";
                var lang = NewText(panel.transform, langLabel, 1.15f, Accent, TextAlignmentOptions.Right);
                lang.transform.localPosition = new Vector3(PanelW / 2f - 0.6f, headY, -0.1f);
                // Right-aligned text ENDS at its anchor, so the clickable area sits to the LEFT
                // of it - a hitbox centred on the anchor itself would cover the close X instead
                // (and swallow its clicks, since earlier hitboxes win).
                var langHit = new GameObject("langHit");
                langHit.transform.SetParent(panel.transform, false);
                langHit.transform.localPosition = new Vector3(PanelW / 2f - 1.6f, headY, 0f);
                hits.Add(new HitBox { anchor = langHit.transform, w = 2.0f, h = 0.45f, onClick = () => {
                    bool wantDrop = BaseLang() == "en" && !langDropdownOpen;
                    if (BaseLang() != "en")
                        sessionLang = HelpLang() == "en" ? null : "en";
                    var reopen = selected;
                    OpenPanel(wantDrop);
                    Select(reopen);
                } });

                var close = NewText(panel.transform, "X", 1.8f, new Color(1f, 0.5f, 0.5f), TextAlignmentOptions.Center);
                close.transform.localPosition = new Vector3(PanelW / 2f - 0.28f, headY, -0.1f);
                hits.Add(new HitBox { anchor = close.transform, w = 0.45f, h = 0.45f, onClick = ClosePanel });

                // Search row (live filter; typed input is captured in HudUpdatePatch)
                float searchY = topY - HeaderH - 0.30f;
                float searchW = 3.6f;
                float searchX = -PanelW / 2f + 0.25f + searchW / 2f;
                NewRect(panel.transform, new Vector3(searchX, searchY, -0.02f), new Vector2(searchW, 0.34f),
                    new Color(0f, 0f, 0f, 0.35f), SortMid);
                NewFrame(panel.transform, new Vector3(searchX, searchY, 0f), new Vector2(searchW, 0.34f),
                    new Color(1f, 1f, 1f, 0.18f), 0.02f, SortMid);
                searchLabel = NewText(panel.transform, "", 1.05f, new Color(1f, 1f, 1f, 0.9f));
                searchLabel.transform.localPosition = new Vector3(searchX - searchW / 2f + 0.12f, searchY, -0.1f);
                var clear = NewText(panel.transform, "x", 1.2f, new Color(1f, 1f, 1f, 0.5f), TextAlignmentOptions.Center);
                clear.transform.localPosition = new Vector3(searchX + searchW / 2f - 0.16f, searchY, -0.1f);
                hits.Add(new HitBox { anchor = clear.transform, w = 0.3f, h = 0.34f, onClick = () => {
                    searchQuery = ""; searchFocused = true; scrollLines = 0; BuildList();
                } });
                // Clicking the field FOCUSES it; only then does typed input go to the search
                // (added after the clear-x so the x keeps winning inside its own little area).
                var searchHit = new GameObject("searchHit");
                searchHit.transform.SetParent(panel.transform, false);
                searchHit.transform.localPosition = new Vector3(searchX, searchY, 0f);
                hits.Add(new HitBox { anchor = searchHit.transform, w = searchW, h = 0.34f, onClick = () => {
                    searchFocused = true;
                } });

                // ---- role list (left): scrollable, filterable; built by BuildList ----
                BuildList();

                // Session language dropdown overlay (only when the base language is English)
                if (langDropdownOpen) BuildLangDropdown();

                // ---- detail card (right) ----
                // Title -> team -> detailed explanation, with a small looping DEMO animation of the
                // role's mechanic filling the bottom strip of the card (built in BuildStage below).
                float cardW = 4.15f, cardH = 3.7f;
                float cardX = PanelW / 2f - cardW / 2f - 0.3f;
                float cardY = (topY - HeaderH - 0.35f) - cardH / 2f + 0.05f;
                NewRect(panel.transform, new Vector3(cardX, cardY, -0.02f), new Vector2(cardW, cardH), CardBg, SortMid);
                NewFrame(panel.transform, new Vector3(cardX, cardY, 0f), new Vector2(cardW, cardH),
                    new Color(1f, 1f, 1f, 0.14f), 0.02f, SortMid);

                float cardLeft = cardX - cardW / 2f + 0.22f;
                float cardTop = cardY + cardH / 2f;
                float cy = cardTop - 0.34f;

                detailTitle = NewText(panel.transform, T("uc.helpui.pick_role"), 1.7f, Color.white);
                detailTitle.transform.localPosition = new Vector3(cardLeft, cy, -0.1f);
                cy -= 0.40f;

                detailTeam = NewText(panel.transform, "", 1.05f, new Color(1f, 1f, 1f, 0.65f));
                detailTeam.transform.localPosition = new Vector3(cardLeft, cy, -0.1f);
                cy -= 0.34f;

                float bodyH = 1.6f; // sized for ~6-7 wrapped lines at fontSize 1.1
                detailBody = NewText(panel.transform, T("uc.helpui.click_hint"),
                    1.1f, new Color(1f, 1f, 1f, 0.88f));
                detailBody.enableWordWrapping = true;
                detailBody.overflowMode = TextOverflowModes.Truncate; // never bleed into the demo below
                detailBody.alignment = TextAlignmentOptions.TopLeft;
                detailBody.rectTransform.sizeDelta = new Vector2(cardW - 0.45f, bodyH);
                detailBody.transform.localPosition = new Vector3(cardX, cy - bodyH / 2f, -0.1f);
                cy -= bodyH + 0.06f;

                // Demo stage strip: everything from here down to just above the card's bottom edge.
                float stageBottom = cardY - cardH / 2f + 0.10f;
                stageSize = new Vector2(cardW - 0.3f, cy - stageBottom);
                stageCenter = new Vector3(cardX, (cy + stageBottom) / 2f, -0.05f);
                BuildStage(selected); // usually null here (fresh panel); rebuilt on role click

                // footer hint
                var footer = NewText(panel.transform, T("uc.helpui.footer"),
                    0.95f, new Color(1f, 1f, 1f, 0.45f));
                footer.transform.localPosition = new Vector3(-PanelW / 2f + 0.25f, -PanelH / 2f + 0.2f, -0.1f);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCHelpMenu] panel build failed: {e}");
                ClosePanel();
            }
        }

        private static GameObject listRoot;
        private static TextMeshPro searchLabel;
        private const float RowH = 0.335f;

        // Rebuilds ONLY the role list (both columns + scrollbar). Cheap enough to run on
        // every scroll tick / search keystroke; the rest of the panel stays untouched.
        private static void BuildList() {
            if (panel == null) return;
            try { if (listRoot != null) UnityEngine.Object.Destroy(listRoot); } catch { }
            hits.RemoveAll(h => h.isRow);
            listRoot = new GameObject("UCHelpList");
            listRoot.layer = panel.layer;
            listRoot.transform.SetParent(panel.transform, false);

            float topY = PanelH / 2f;
            float listTop = topY - HeaderH - 0.72f;      // below the search row
            float listBottom = -PanelH / 2f + 0.42f;     // above the footer
            int visible = Mathf.Max(3, (int)((listTop - listBottom) / RowH) + 1);
            float colLeft = -PanelW / 2f + 0.35f;
            float colRight = colLeft + 1.95f;
            float rowW = 1.8f;

            string q = searchQuery.Trim().ToLowerInvariant();
            var left = new List<Entry>();
            var right = new List<Entry>();
            foreach (var e in Entries()) {
                CustomOption rate = null;
                try { rate = e.rate?.Invoke(); } catch { }
                if (e.rate != null && (rate == null || rate.getSelection() <= 0)) continue;
                if (q.Length > 0 && !e.name.ToLowerInvariant().Contains(q)) continue;
                if (e.faction == Faction.Impostor || e.faction == Faction.Neutral || e.faction == Faction.Ghost)
                    left.Add(e);
                else
                    right.Add(e);
            }

            var leftLines = ColumnLines(left);
            var rightLines = ColumnLines(right);
            maxScroll = Mathf.Max(0, Mathf.Max(leftLines.Count, rightLines.Count) - visible);
            scrollLines = Mathf.Clamp(scrollLines, 0, maxScroll);
            RenderColumn(leftLines, colLeft, listTop, rowW, visible);
            RenderColumn(rightLines, colRight, listTop, rowW, visible);

            if (left.Count == 0 && right.Count == 0) {
                var none = NewText(listRoot.transform,
                    q.Length > 0 ? T("uc.helpui.no_matches") : T("uc.helpui.none_enabled"),
                    1.25f, new Color(1f, 1f, 1f, 0.7f));
                none.transform.localPosition = new Vector3(colLeft, listTop, -0.1f);
            }

            // slim scrollbar between the columns and the detail card
            if (maxScroll > 0) {
                float trackX = colRight + rowW + 0.14f;
                float trackH = listTop - listBottom;
                float trackMid = (listTop + listBottom) / 2f;
                NewRect(listRoot.transform, new Vector3(trackX, trackMid, -0.05f),
                    new Vector2(0.03f, trackH), new Color(1f, 1f, 1f, 0.08f), SortMid);
                int total = maxScroll + visible;
                float thumbH = Mathf.Max(0.15f, trackH * visible / total);
                float t = maxScroll == 0 ? 0f : (float)scrollLines / maxScroll;
                float thumbY = trackMid + (trackH - thumbH) / 2f - t * (trackH - thumbH);
                NewRect(listRoot.transform, new Vector3(trackX, thumbY, -0.06f),
                    new Vector2(0.05f, thumbH), new Color(1f, 0.82f, 0.35f, 0.5f), SortMid);
            }
        }

        // A column's virtual lines: a null entry marks a section header for `header`.
        private static List<(Entry e, Faction header)> ColumnLines(List<Entry> list) {
            var lines = new List<(Entry, Faction)>();
            Faction? last = null;
            foreach (var e in list) {
                if (last != e.faction) {
                    lines.Add((null, e.faction));
                    last = e.faction;
                }
                lines.Add((e, e.faction));
            }
            return lines;
        }

        private static void RenderColumn(List<(Entry e, Faction header)> lines, float x, float listTop,
                                         float rowW, int visible) {
            for (int i = scrollLines; i < lines.Count && i < scrollLines + visible; i++) {
                float y = listTop - (i - scrollLines) * RowH;
                var (e, f) = lines[i];
                if (e == null) {
                    var fc = FactionColor(f);
                    var header = NewText(listRoot.transform, FactionHeader(f), 1.0f,
                        new Color(fc.r, fc.g, fc.b, 0.8f));
                    header.transform.localPosition = new Vector3(x, y, -0.1f);
                    NewRect(listRoot.transform, new Vector3(x + rowW / 2f - 0.1f, y - 0.15f, -0.05f),
                        new Vector2(rowW - 0.2f, 0.018f), new Color(fc.r, fc.g, fc.b, 0.35f), SortMid);
                    continue;
                }
                var hover = NewRect(listRoot.transform, new Vector3(x + rowW / 2f - 0.1f, y, -0.05f),
                    new Vector2(rowW, RowH * 0.92f), new Color(1f, 1f, 1f, 0f), SortMid);
                Color rowCol;
                try { rowCol = e.color(); } catch { rowCol = Color.white; }
                var row = NewText(listRoot.transform, e.name, 1.35f, rowCol);
                row.transform.localPosition = new Vector3(x + 0.12f, y, -0.1f);
                var entry = e;
                hits.Add(new HitBox {
                    anchor = hover.transform, w = rowW, h = RowH * 0.95f,
                    hover = hover, entry = entry, isRow = true,
                    onClick = () => Select(entry),
                });
            }
        }

        // Full-language session picker (3-column grid over the list, panel-topmost).
        private static void BuildLangDropdown() {
            const int cols = 3;
            const float cellW = 1.55f, cellH = 0.3f;
            int count = UCLocalization.KnownCodes.Length;      // 26
            int rows = (count + cols - 1) / cols;
            float w = cols * cellW + 0.2f, h = rows * cellH + 0.2f;
            float cx = PanelW / 2f - w / 2f - 0.35f;
            float cy = PanelH / 2f - HeaderH - h / 2f - 0.12f;
            var drop = new GameObject("UCHelpLangDrop");
            drop.layer = panel.layer;
            drop.transform.SetParent(panel.transform, false);
            drop.transform.localPosition = new Vector3(0f, 0f, -0.5f);
            var bg = NewRect(drop.transform, new Vector3(cx, cy, 0f), new Vector2(w, h),
                new Color(0.05f, 0.06f, 0.1f, 0.97f), 520);
            NewFrame(drop.transform, new Vector3(cx, cy, -0.01f), new Vector2(w, h), BorderCol, 0.02f, 521);
            for (int i = 0; i < count; i++) {
                string code = UCLocalization.KnownCodes[i];
                int col = i % cols, r = i / cols;
                float x = cx + (col - (cols - 1) / 2f) * cellW;
                float y = cy + ((rows - 1) / 2f - r) * cellH;
                bool current = HelpLang() == code;
                var cell = NewText(drop.transform, LangNames[i], 0.95f,
                    current ? Accent : new Color(1f, 1f, 1f, 0.92f), TextAlignmentOptions.Center);
                cell.transform.localPosition = new Vector3(x, y, -0.05f);
                var mr = cell.GetComponent<MeshRenderer>();
                if (mr != null) mr.sortingOrder = 522;
                string codeCopy = code;
                // inserted FIRST so dropdown cells win over any overlapping list rows
                hits.Insert(0, new HitBox { anchor = cell.transform, w = cellW, h = cellH, onClick = () => {
                    sessionLang = codeCopy == BaseLang() ? null : codeCopy;
                    langDropdownOpen = false;
                    var reopen = selected;
                    OpenPanel();
                    Select(reopen);
                } });
            }
        }

        private static void Select(Entry e) {
            try {
                selected = e;
                if (e == null || detailTitle == null) return;
                detailTitle.text = e.name;
                detailTitle.color = e.color();
                detailTeam.text = FactionTeamLine(e.faction);
                detailTeam.color = FactionColor(e.faction);
                detailBody.text = T(e.key);
                BuildStage(e);
            } catch { }
        }

        // ====================================================================
        // Demo stage: a small looping vignette in the bottom strip of the detail card, acting out
        // the selected role's mechanic with the shared UCFx sprites (soft dots as "players"). The
        // animation is STATELESS: AnimateStage() recomputes every position/color/alpha purely from
        // the loop phase each frame, so nothing can drift and rebuilds need no bookkeeping.
        // Stage-local design units; the panel's camera-fit scale applies automatically (child).
        // ====================================================================
        private static GameObject stage;
        private static string stageRole;
        internal static float stageT;
        private static Vector3 stageCenter;   // computed during OpenPanel's card layout
        internal static Vector2 stageSize;
        private static readonly Dictionary<string, SpriteRenderer> cast = new Dictionary<string, SpriteRenderer>();
        private static readonly Dictionary<string, TextMeshPro> castTexts = new Dictionary<string, TextMeshPro>();
        private static readonly Dictionary<string, Fig> figs = new Dictionary<string, Fig>();
        private static readonly Dictionary<string, Btn> btns = new Dictionary<string, Btn>();

        // Mini crewmate built from the kill-overlay assets (tinted body + white visor + soft ground
        // shadow). Root origin = the FEET, so vignettes place figures on the floor line directly.
        internal sealed class Fig {
            public GameObject root;
            public Transform bodyGroup;   // walk bob / death fall applied here
            public SpriteRenderer body, visor, shadow;
            public float scale;
            public float phase;           // per-figure walk-cycle offset (no robot sync)
        }

        // Ability-button pop-up (the role's actual CustomButton icon on a soft dark plate) shown
        // the moment the demo actor "presses" that button.
        internal sealed class Btn {
            public GameObject root;
            public SpriteRenderer plate, icon;
        }

        // Fixed demo palette (independent of player colors; red = killer/impostor by convention).
        internal static readonly Color DemoRed = new Color(0.93f, 0.28f, 0.28f);
        internal static readonly Color DemoBlue = new Color(0.38f, 0.66f, 1f);
        internal static readonly Color DemoGreen = new Color(0.45f, 0.88f, 0.5f);
        internal static readonly Color DemoGray = new Color(0.62f, 0.62f, 0.66f);
        internal static readonly Color DemoCyan = new Color(0.55f, 0.9f, 1f);
        internal static readonly Color DemoOrange = new Color(1f, 0.62f, 0.2f);
        internal static readonly Color DemoPurple = new Color(0.72f, 0.55f, 1f);
        internal static readonly Color DemoDark = new Color(0.22f, 0.22f, 0.26f);
        internal static readonly Color DemoWhite = new Color(0.88f, 0.88f, 0.92f);

        internal const float FloorY = -0.36f;   // feet baseline the crewmates stand on
        internal const float BtnY = 0.22f;      // height of the ability-button pop-ups

        private static void BuildStage(Entry e) {
            try { if (stage != null) UnityEngine.Object.Destroy(stage); } catch { }
            stage = null;
            stageRole = null;
            stageT = 0f;
            cast.Clear();
            castTexts.Clear();
            figs.Clear();
            btns.Clear();
            if (e == null || panel == null) return;
            try {
                stage = new GameObject("UCHelpStage");
                stage.layer = panel.layer;
                stage.transform.SetParent(panel.transform, false);
                stage.transform.localPosition = stageCenter;

                // dark inset so the vignette reads as its own little screen, a faint floor line the
                // crewmates stand on, plus a subtle tag (bottom-left: the top strip is used by the
                // vignettes' countdown/energy bars and button pop-ups)
                NewRect(stage.transform, new Vector3(0f, 0f, 0.01f), stageSize, new Color(0f, 0f, 0f, 0.4f), 503);
                NewRect(stage.transform, new Vector3(0f, FloorY, 0f), new Vector2(stageSize.x - 0.1f, 0.014f), new Color(1f, 1f, 1f, 0.08f), 504);
                var tag = NewText(stage.transform, "DEMO", 0.7f, new Color(1f, 1f, 1f, 0.28f));
                tag.transform.localPosition = new Vector3(-stageSize.x / 2f + 0.1f, -stageSize.y / 2f + 0.1f, -0.1f);
                var tagMr = tag.GetComponent<MeshRenderer>();
                if (tagMr != null) tagMr.sortingOrder = 510;

                stageRole = e.name;
                genericKind = -1;
                CreateActors(e.name);
                // Bespoke demo packs (UCHelpDemos*.cs) cover the TOR roles; the generic
                // per-faction vignette is only the last-resort safety net.
                if (figs.Count == 0 && cast.Count == 0 && btns.Count == 0) {
                    EnsureExtraDemos();
                    if (ExtraDemos.TryGetValue(e.name, out var demo) && demo.create != null) {
                        try { demo.create(); } catch (Exception dex) {
                            UnknownsCollectionPlugin.Logger?.LogWarning($"[UCHelpMenu] demo '{e.name}' failed: {dex.Message}");
                        }
                    }
                }
                if (figs.Count == 0 && cast.Count == 0 && btns.Count == 0)
                    CreateGenericActors(e);
                AnimateStage(); // first-frame pose (no visible pop-in)
            } catch (Exception ex) {
                stageRole = null;
                UnknownsCollectionPlugin.Logger?.LogError($"[UCHelpMenu] stage build failed: {ex}");
            }
        }

        // ---- actor/prop factories (registered by key, driven by AnimateStage) ----
        internal static SpriteRenderer StageSprite(string key, Sprite sprite, Color c, float size, int sort) {
            var go = new GameObject("UCStage_" + key);
            go.layer = stage.layer;
            go.transform.SetParent(stage.transform, false);
            go.transform.localScale = Vector3.one * size;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = new Color(c.r, c.g, c.b, 0f);
            sr.sortingOrder = sort;
            cast[key] = sr;
            return sr;
        }

        internal static SpriteRenderer StageDot(string key, Color c, float size = 0.22f)
            => StageSprite(key, UCFx.Dot, c, size, 506);

        internal static SpriteRenderer StageRect(string key, Color c, float w, float h, int sort = 505) {
            var sr = NewRect(stage.transform, Vector3.zero, new Vector2(w, h), c, sort);
            sr.gameObject.name = "UCStage_" + key;
            cast[key] = sr;
            return sr;
        }

        internal static TextMeshPro StageCap(string key, string txt, float size, Color c) {
            var t = NewText(stage.transform, txt, size, c, TextAlignmentOptions.Center);
            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 510;
            castTexts[key] = t;
            return t;
        }

        // Prop from an existing asset sprite, normalized to a target HEIGHT in stage units (the
        // embedded PNGs have varying pixel sizes, so bounds-based scaling keeps layouts stable).
        internal static SpriteRenderer StagePic(string key, Sprite sprite, float targetH, int sort, Color? tint = null) {
            float h = sprite != null ? sprite.bounds.size.y : 1f;
            float scale = targetH / Mathf.Max(0.01f, h);
            var sr = StageSprite(key, sprite, tint ?? Color.white, scale, sort);
            return sr;
        }

        // Mini crewmate from the kill-overlay assets: tinted body + white visor + soft ground
        // shadow. Root origin = the FEET (visor anchor +-0.40/+0.54 mirrors UCKillOverlay.MakeFig).
        internal static Fig Crew(string key, Color c, float scale = 0.19f) {
            var root = new GameObject("UCStageFig_" + key);
            root.layer = stage.layer;
            root.transform.SetParent(stage.transform, false);
            var fig = new Fig { root = root, scale = scale, phase = (key.Length * 1.31f + key[0] * 0.37f) % 6.28f };

            var shadowGo = new GameObject("shadow") { layer = stage.layer };
            shadowGo.transform.SetParent(root.transform, false);
            shadowGo.transform.localScale = new Vector3(scale * 2.6f, scale * 0.7f, 1f);
            fig.shadow = shadowGo.AddComponent<SpriteRenderer>();
            fig.shadow.sprite = UCFx.Dot;
            fig.shadow.color = new Color(0f, 0f, 0f, 0.35f);
            fig.shadow.sortingOrder = 505;

            var grp = new GameObject("bodyGroup") { layer = stage.layer };
            grp.transform.SetParent(root.transform, false);
            fig.bodyGroup = grp.transform;

            var bodyGo = new GameObject("body") { layer = stage.layer };
            bodyGo.transform.SetParent(grp.transform, false);
            float bodyH = UCAssets.OverlayCrewBody != null ? UCAssets.OverlayCrewBody.bounds.size.y : 2.56f;
            bodyGo.transform.localPosition = new Vector3(0f, bodyH * scale / 2f, 0f); // feet at origin
            bodyGo.transform.localScale = Vector3.one * scale;
            fig.body = bodyGo.AddComponent<SpriteRenderer>();
            fig.body.sprite = UCAssets.OverlayCrewBody;
            fig.body.color = c;
            fig.body.sortingOrder = 506;

            var visorGo = new GameObject("visor") { layer = stage.layer };
            visorGo.transform.SetParent(bodyGo.transform, false);
            visorGo.transform.localPosition = new Vector3(0.40f, 0.54f, 0f);
            fig.visor = visorGo.AddComponent<SpriteRenderer>();
            fig.visor.sprite = UCAssets.OverlayCrewVisor;
            fig.visor.color = Color.white;
            fig.visor.sortingOrder = 507;

            figs[key] = fig;
            return fig;
        }

        // Place + animate a crewmate: walk = 0..1 walk-cycle intensity (bob + waddle), y is the
        // FOOT height relative to the floor line (0 = standing on it).
        internal static void FigPut(string key, float x, float y, bool faceLeft, float walk) {
            if (!figs.TryGetValue(key, out var f) || f == null || f.root == null) return;
            f.root.transform.localPosition = new Vector3(x, FloorY + y, -0.1f);
            float cycle = stageT * 11f + f.phase;
            float bounce = Mathf.Abs(Mathf.Sin(cycle)) * 0.03f * walk;
            f.bodyGroup.localPosition = new Vector3(0f, bounce, 0f);
            f.bodyGroup.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(cycle) * 5f * walk);
            f.body.flipX = faceLeft;
            f.visor.flipX = faceLeft;
            f.visor.transform.localPosition = new Vector3(faceLeft ? -0.40f : 0.40f, 0.54f, 0f);
        }

        internal static void FigCol(string key, Color c, float alpha, float shadowAlpha = -1f) {
            if (!figs.TryGetValue(key, out var f) || f == null || f.root == null) return;
            f.body.color = new Color(c.r, c.g, c.b, alpha);
            f.visor.color = new Color(1f, 1f, 1f, alpha);
            f.shadow.color = new Color(0f, 0f, 0f, shadowAlpha >= 0f ? shadowAlpha : 0.35f * alpha);
        }

        // Death fall: 0 = standing, 1 = flat on the ground (overrides the walk pose - call last).
        internal static void FigDead(string key, float fall) {
            if (!figs.TryGetValue(key, out var f) || f == null || f.root == null || fall <= 0f) return;
            float e = Ease(fall);
            f.bodyGroup.localRotation = Quaternion.Euler(0f, 0f, -82f * e);
            f.bodyGroup.localPosition = new Vector3(0.06f * e, -0.02f * e, 0f);
        }

        // Ability-button pop-up: the role's real CustomButton icon on a soft dark plate.
        internal static Btn MakeBtn(string key, Sprite icon) {
            var root = new GameObject("UCStageBtn_" + key);
            root.layer = stage.layer;
            root.transform.SetParent(stage.transform, false);
            var b = new Btn { root = root };

            var plateGo = new GameObject("plate") { layer = stage.layer };
            plateGo.transform.SetParent(root.transform, false);
            plateGo.transform.localScale = Vector3.one * 0.46f;
            b.plate = plateGo.AddComponent<SpriteRenderer>();
            b.plate.sprite = UCFx.Dot;
            b.plate.color = new Color(0.04f, 0.05f, 0.09f, 0f);
            b.plate.sortingOrder = 508;

            var iconGo = new GameObject("icon") { layer = stage.layer };
            iconGo.transform.SetParent(root.transform, false);
            float h = icon != null ? icon.bounds.size.y : 1f;
            iconGo.transform.localScale = Vector3.one * (0.3f / Mathf.Max(0.01f, h));
            b.icon = iconGo.AddComponent<SpriteRenderer>();
            b.icon.sprite = icon;
            b.icon.color = new Color(1f, 1f, 1f, 0f);
            b.icon.sortingOrder = 509;

            btns[key] = b;
            return b;
        }

        // prog 0..1: pop in with a little overshoot, hover, fade out. Outside the window: hidden.
        internal static void BtnPop(string key, float x, float y, float prog) {
            if (!btns.TryGetValue(key, out var b) || b == null || b.root == null) return;
            if (prog <= 0f || prog >= 1f) {
                b.plate.color = new Color(b.plate.color.r, b.plate.color.g, b.plate.color.b, 0f);
                b.icon.color = new Color(1f, 1f, 1f, 0f);
                return;
            }
            float aIn = Ease(Seg(prog, 0f, 0.16f));
            float aOut = 1f - Ease(Seg(prog, 0.78f, 1f));
            float a = aIn * aOut;
            float scale = Mathf.Lerp(0.55f, 1f, aIn) + 0.16f * Mathf.Sin(aIn * Mathf.PI);
            b.root.transform.localPosition = new Vector3(x, y + 0.015f * Mathf.Sin(stageT * 3.2f), -0.15f);
            b.root.transform.localScale = Vector3.one * (scale * aOut);
            b.plate.color = new Color(0.04f, 0.05f, 0.09f, 0.85f * a);
            b.icon.color = new Color(1f, 1f, 1f, a);
        }

        // ---- per-frame state helpers ----
        internal static void Put(string key, float x, float y) {
            if (cast.TryGetValue(key, out var s) && s != null) s.transform.localPosition = new Vector3(x, y, -0.1f);
        }
        internal static void ColA(string key, Color c, float a) {
            if (cast.TryGetValue(key, out var s) && s != null) s.color = new Color(c.r, c.g, c.b, a);
        }
        internal static void Size2(string key, float sx, float sy) {
            if (cast.TryGetValue(key, out var s) && s != null) s.transform.localScale = new Vector3(sx, sy, 1f);
        }
        internal static void BarLeft(string key, float left, float y, float w, float h) {
            if (!cast.TryGetValue(key, out var s) || s == null) return;
            s.transform.localScale = new Vector3(Mathf.Max(w, 0.001f), h, 1f);
            s.transform.localPosition = new Vector3(left + w / 2f, y, -0.1f);
        }
        // Re-scale an asset prop (StagePic) to a new target height, e.g. a growing explosion.
        internal static void PicScale(string key, float targetH) {
            if (cast.TryGetValue(key, out var s) && s != null && s.sprite != null)
                s.transform.localScale = Vector3.one * (targetH / Mathf.Max(0.01f, s.sprite.bounds.size.y));
        }
        // One-shot expanding ring: prog 0..1 grows + fades, outside that range it is hidden.
        internal static void Burst(string key, float x, float y, float prog, float maxD, Color c) {
            if (!cast.TryGetValue(key, out var s) || s == null) return;
            if (prog <= 0f || prog >= 1f) { s.color = new Color(c.r, c.g, c.b, 0f); return; }
            s.transform.localPosition = new Vector3(x, y, -0.12f);
            s.transform.localScale = Vector3.one * (0.12f + maxD * prog);
            s.color = new Color(c.r, c.g, c.b, 0.85f * (1f - prog));
        }
        internal static void PutCap(string key, float x, float y) {
            if (castTexts.TryGetValue(key, out var t) && t != null) t.transform.localPosition = new Vector3(x, y, -0.15f);
        }
        internal static void CapA(string key, float a) {
            if (castTexts.TryGetValue(key, out var t) && t != null) { var c = t.color; t.color = new Color(c.r, c.g, c.b, a); }
        }
        internal static void CapText(string key, string s) {
            if (castTexts.TryGetValue(key, out var t) && t != null && t.text != s) t.text = s;
        }

        // ---- timing helpers: p = loop phase 0..1, Seg picks a sub-window, Move eases a path ----
        internal static float P(float period) => Mathf.Repeat(stageT, period) / period;
        internal static float Seg(float p, float a, float b) => Mathf.Clamp01((p - a) / (b - a));
        internal static float Ease(float x) => x * x * (3f - 2f * x);
        internal static float Move(float from, float to, float x) => Mathf.Lerp(from, to, Ease(x));
        internal static float PathY(float x) => -0.16f + 0.1f * Mathf.Sin(x * 2.4f);

        // The KillButton's own sprite (the vanilla red-knife button) for demos that show a kill
        // cooldown; falls back to a UC icon if the HUD button is not available.
        internal static Sprite KillButtonSprite(Sprite fallback) {
            try {
                var hud = HudManager.Instance;
                if (hud != null && hud.KillButton != null && hud.KillButton.graphic != null && hud.KillButton.graphic.sprite != null)
                    return hud.KillButton.graphic.sprite;
            } catch { }
            return fallback;
        }

        // ---- bespoke demo registry (UCHelpDemos*.cs packs) -------------------------------
        // Every TOR role gets its OWN hand-crafted vignette, contributed by the demo pack
        // files via RegisterDemo. Packs are discovered once via reflection (any type named
        // UCHelpDemos* with a public static Register()). The per-faction generic vignette
        // below stays as a last-resort safety net only.
        internal static readonly Dictionary<string, (Action create, Action animate)> ExtraDemos
            = new Dictionary<string, (Action, Action)>();
        private static bool extraDemosLoaded;

        internal static void RegisterDemo(string roleName, Action create, Action animate)
            => ExtraDemos[roleName] = (create, animate);

        private static void EnsureExtraDemos() {
            if (extraDemosLoaded) return;
            extraDemosLoaded = true;
            try {
                foreach (var t in typeof(UCHelpMenu).Assembly.GetTypes()) {
                    if (!t.Name.StartsWith("UCHelpDemos", StringComparison.Ordinal)) continue;
                    try {
                        t.GetMethod("Register", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                            ?.Invoke(null, null);
                    } catch (Exception ex) {
                        UnknownsCollectionPlugin.Logger?.LogWarning($"[UCHelpMenu] demo pack {t.Name} failed: {ex.Message}");
                    }
                }
                UnknownsCollectionPlugin.Logger?.LogInfo($"[UCHelpMenu] {ExtraDemos.Count} bespoke demos registered");
            } catch { }
        }

        // ---- generic per-faction fallback vignettes ------------------------------------
        // Every role without a bespoke scene still gets a fully animated, high-quality demo:
        // a small acted loop matching its faction, tinted in the role's own color.
        private static int genericKind = -1;    // -1 = none, else (int)Faction
        private static Color genericColor;

        private static void CreateGenericActors(Entry e) {
            genericKind = (int)e.faction;
            try { genericColor = e.color(); } catch { genericColor = FactionColor(e.faction); }
            switch (e.faction) {
                case Faction.Impostor:
                    Crew("gVic", DemoBlue);
                    Crew("gImp", genericColor);
                    MakeBtn("gKill", KillButtonSprite(null));
                    StageSprite("gFx", UCFx.Ring, DemoRed, 0.1f, 508);
                    break;
                case Faction.Crew:
                    Crew("gC", genericColor);
                    StageRect("gPanel", new Color(1f, 1f, 1f, 0.07f), 0.95f, 0.55f, 504);
                    StageRect("gBarBg", new Color(1f, 1f, 1f, 0.14f), 0.7f, 0.06f);
                    StageRect("gBar", DemoGreen, 0.01f, 0.045f);
                    StageSprite("gFx", UCFx.Ring, DemoGreen, 0.1f, 508);
                    break;
                case Faction.Modifier:
                    Crew("gM", DemoBlue);
                    StageSprite("gFx", UCFx.Ring, genericColor, 0.1f, 508);
                    for (int i = 0; i < 4; i++) StageDot("gS" + i, genericColor, 0.07f);
                    StageCap("gPlus", "+", 1.2f, genericColor);
                    break;
                default: // Neutral / Ghost
                    Crew("gN", genericColor);
                    for (int i = 0; i < 3; i++) StageDot("gO" + i, Accent, 0.09f);
                    StageSprite("gFx", UCFx.Ring, Accent, 0.1f, 508);
                    StageCap("gWin", "WIN", 1.0f, Accent);
                    break;
            }
        }

        private static void AnimateGeneric() {
            switch ((Faction)genericKind) {
                case Faction.Impostor: {
                    // victim strolls right, the role's figure closes in and strikes
                    float p = P(6f);
                    float fade = Ease(Seg(p, 0.02f, 0.1f)) * (1f - Ease(Seg(p, 0.92f, 1f)));
                    bool strike = p >= 0.5f;
                    float vx = Move(-1.05f, 0.85f, Seg(p, 0f, 0.52f));
                    float ix = Mathf.Min(Move(-1.65f, vx - 0.34f, Seg(p, 0.04f, 0.5f)), vx - 0.32f);
                    FigPut("gVic", vx, 0f, false, strike ? 0f : 1f);
                    FigPut("gImp", ix, 0f, false, strike ? 0f : 1f);
                    FigCol("gImp", genericColor, fade);
                    FigCol("gVic", DemoBlue, fade);
                    FigDead("gVic", Ease(Seg(p, 0.5f, 0.62f)));
                    BtnPop("gKill", ix, BtnY, Seg(p, 0.4f, 0.62f));
                    Burst("gFx", vx, 0.12f, Seg(p, 0.5f, 0.72f), 0.85f, DemoRed);
                    break;
                }
                case Faction.Crew: {
                    // walk to a task panel, fill the bar, celebrate with a hop + green burst
                    float p = P(6f);
                    float fade = Ease(Seg(p, 0.02f, 0.1f)) * (1f - Ease(Seg(p, 0.92f, 1f)));
                    float cx = Move(-1.3f, 0.02f, Seg(p, 0f, 0.3f));
                    float hop = Mathf.Sin(Mathf.Clamp01(Seg(p, 0.78f, 0.9f)) * Mathf.PI) * 0.12f;
                    FigPut("gC", cx, hop, false, p < 0.3f ? 1f : 0f);
                    FigCol("gC", genericColor, fade);
                    Put("gPanel", 0.75f, 0.05f); ColA("gPanel", new Color(1f, 1f, 1f, 1f), 0.07f * fade);
                    Put("gBarBg", 0.75f, 0.05f); ColA("gBarBg", new Color(1f, 1f, 1f, 1f), 0.14f * fade);
                    float fill = 0.7f * Ease(Seg(p, 0.32f, 0.74f));
                    BarLeft("gBar", 0.4f, 0.05f, fill, 0.045f);
                    ColA("gBar", DemoGreen, fade);
                    Burst("gFx", 0.75f, 0.05f, Seg(p, 0.76f, 0.95f), 0.7f, DemoGreen);
                    break;
                }
                case Faction.Modifier: {
                    // a normal crewmate gains "something extra": orbiting sparks attach and
                    // the body shimmers toward the modifier's color
                    float p = P(5f);
                    float fade = Ease(Seg(p, 0.02f, 0.1f)) * (1f - Ease(Seg(p, 0.92f, 1f)));
                    FigPut("gM", 0f, 0f, false, 0.35f);
                    float blend = 0.5f + 0.5f * Mathf.Sin(stageT * 2.6f);
                    FigCol("gM", Color.Lerp(DemoBlue, genericColor, 0.55f * blend * Ease(Seg(p, 0.2f, 0.5f))), fade);
                    for (int i = 0; i < 4; i++) {
                        float ph = stageT * 2.2f + i * 1.57f;
                        float r = Move(0.85f, 0.3f, Ease(Seg(p, 0.1f, 0.5f)));
                        Put("gS" + i, Mathf.Cos(ph) * r, 0.28f + Mathf.Sin(ph) * r * 0.45f);
                        ColA("gS" + i, genericColor, fade * (0.5f + 0.5f * Mathf.Sin(ph * 2f)));
                    }
                    PutCap("gPlus", 0f, 0.78f + 0.04f * Mathf.Sin(stageT * 3f));
                    CapA("gPlus", fade * Ease(Seg(p, 0.45f, 0.6f)) * (1f - Ease(Seg(p, 0.85f, 1f))));
                    Burst("gFx", 0f, 0.25f, Seg(p, 0.45f, 0.7f), 0.8f, genericColor);
                    break;
                }
                default: {
                    // Neutral/Ghost: lone figure, converging gold orbs, own little victory
                    float p = P(6f);
                    float fade = Ease(Seg(p, 0.02f, 0.1f)) * (1f - Ease(Seg(p, 0.92f, 1f)));
                    float hop = Mathf.Sin(Mathf.Clamp01(Seg(p, 0.72f, 0.86f)) * Mathf.PI) * 0.14f;
                    FigPut("gN", 0f, hop, false, 0.25f);
                    FigCol("gN", genericColor, fade);
                    for (int i = 0; i < 3; i++) {
                        float ph = stageT * 1.9f + i * 2.094f;
                        float r = Move(0.95f, 0.18f, Ease(Seg(p, 0.08f, 0.62f)));
                        Put("gO" + i, Mathf.Cos(ph) * r, 0.3f + Mathf.Sin(ph) * r * 0.4f);
                        ColA("gO" + i, Accent, fade * (1f - Ease(Seg(p, 0.6f, 0.72f))));
                    }
                    Burst("gFx", 0f, 0.28f, Seg(p, 0.66f, 0.88f), 0.9f, Accent);
                    PutCap("gWin", 0f, 0.72f + 0.05f * Mathf.Sin(stageT * 3.4f));
                    CapA("gWin", fade * Ease(Seg(p, 0.7f, 0.82f)) * (1f - Ease(Seg(p, 0.94f, 1f))));
                    break;
                }
            }
        }

        private static void CreateActors(string role) {
            switch (role) {
                case "Tesla":
                    Crew("a", DemoBlue); Crew("b", DemoGreen);
                    StageCap("plus", "+", 1.1f, Accent); StageCap("minus", "-", 1.3f, Accent);
                    StageRect("barBg", new Color(1f, 1f, 1f, 0.12f), 0.9f, 0.06f);
                    StageRect("bar", DemoRed, 0.9f, 0.045f);
                    StagePic("boltA", UCAssets.OverlayBoltA, 0.3f, 507);
                    StagePic("boltB", UCAssets.OverlayBoltB, 0.3f, 507);
                    StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
                    break;
                case "Saboteur":
                    StagePic("console", UCAssets.OverlayConsole, 0.46f, 505);
                    Crew("sab", DemoRed); Crew("crew", DemoBlue);
                    MakeBtn("sabBtn", UCAssets.SaboteurSabotageIcon);
                    StagePic("bolt", UCAssets.OverlayBoltA, 0.3f, 507);
                    StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
                    break;
                case "Poisoner":
                    Crew("poi", DemoRed); Crew("vic", DemoWhite); Crew("rep", DemoBlue);
                    StagePic("vial", UCAssets.OverlayVial, 0.24f, 508);
                    StageCap("mark", "!", 1.0f, Accent);
                    StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
                    break;
                case "Silencer":
                    Crew("sil", DemoRed); Crew("vic", DemoBlue);
                    MakeBtn("silBtn", UCAssets.SilencerIcon);
                    StageDot("wave", DemoRed, 0.1f);
                    StageCap("chat", "...", 0.9f, Color.white);
                    StageCap("mute", "X", 0.9f, DemoRed);
                    break;
                case "Illusionist":
                    Crew("real", DemoRed); Crew("clone", DemoRed);
                    MakeBtn("recBtn", UCAssets.IllusionistRecordIcon);
                    MakeBtn("playBtn", UCAssets.IllusionistPlaybackIcon);
                    for (int i = 0; i < 7; i++) StageDot("t" + i, Color.white, 0.06f);
                    break;
                case "Maniac":
                    Crew("man", DemoRed); Crew("v1", DemoBlue); Crew("v2", DemoGreen);
                    MakeBtn("bombBtn", UCAssets.ManiacBombIcon);
                    MakeBtn("passBtn", UCAssets.ManiacPassIcon);
                    StagePic("bomb", UCAssets.OverlayBomb, 0.2f, 508);
                    StagePic("burst", UCAssets.OverlayBurst, 0.3f, 509);
                    break;
                case "Shade":
                    Crew("shade", DemoRed); Crew("vic", DemoWhite); Crew("walker", DemoBlue);
                    for (int i = 0; i < 3; i++) StageSprite("s" + i, UCFx.Smoke, new Color(0.4f, 0.3f, 0.55f), 0.22f, 507);
                    StageCap("mark", "!", 1.0f, Accent);
                    StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
                    break;
                case "Manipulator":
                    Crew("manip", DemoRed);
                    MakeBtn("fakeBtn", UCAssets.ManipulatorIcon);
                    StageRect("map", new Color(1f, 1f, 1f, 0.06f), 1.7f, 0.62f, 504);
                    StageCap("admin", "ADMIN", 0.55f, Color.white);
                    StageDot("d0", DemoBlue, 0.12f); StageDot("d1", DemoGreen, 0.12f); StageDot("d2", DemoRed, 0.12f);
                    StageDot("g0", DemoBlue, 0.12f); StageDot("g1", DemoGreen, 0.12f); StageDot("g2", DemoRed, 0.12f);
                    break;
                case "Auditor":
                    Crew("crew", DemoBlue); Crew("aud", DemoRed);
                    StageRect("console", new Color(0.55f, 0.6f, 0.68f), 0.22f, 0.34f, 504);
                    StageRect("barBg", new Color(1f, 1f, 1f, 0.12f), 1.7f, 0.08f);
                    StageRect("bar", DemoGreen, 1.7f, 0.06f);
                    StageCap("undo", "UNDO", 0.8f, Accent);
                    StageSprite("fx", UCFx.Ring, Accent, 0.1f, 508);
                    break;
                case "Siphoner":
                    Crew("sip", DemoCyan); Crew("imp", DemoRed);
                    MakeBtn("killBtn", KillButtonSprite(UCAssets.SiphonerIcon));
                    StageRect("barBg", new Color(1f, 1f, 1f, 0.12f), 0.9f, 0.06f);
                    StageRect("bar", DemoGreen, 0.9f, 0.045f);
                    StageDot("flow", DemoRed, 0.09f);
                    break;
                case "Witness":
                    Crew("wit", DemoCyan); Crew("killer", DemoRed); Crew("vic", DemoWhite);
                    StageSprite("markRing", UCFx.Ring, DemoRed, 0.5f, 507);
                    StageCap("mark", "!", 1.0f, Accent);
                    StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
                    break;
                case "Scout":
                    Crew("scout", DemoCyan);
                    MakeBtn("scoutBtn", UCAssets.ScoutIcon);
                    StageSprite("st0", UCFx.Streak, DemoCyan, 0.3f, 505);
                    StageSprite("st1", UCFx.Streak, DemoCyan, 0.22f, 505);
                    break;
                case "Beacon":
                    StageRect("dark", Color.black, stageSize.x - 0.06f, stageSize.y - 0.06f, 504);
                    StageSprite("light", UCFx.Dot, Accent, 1.4f, 505);
                    Crew("beacon", Accent); Crew("crew", DemoBlue);
                    break;
                case "Bug":
                    Crew("blue", DemoBlue); Crew("red", DemoRed); Crew("bug", DemoGray);
                    StageSprite("fx", UCFx.Ring, Accent, 0.1f, 508);
                    StageCap("win", "WIN", 0.7f, Accent);
                    break;
                case "Follower":
                    Crew("red", DemoRed); Crew("blue", DemoBlue); Crew("fol", DemoGray);
                    StageDot("soul", DemoBlue, 0.1f);
                    StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
                    StageSprite("fx2", UCFx.Ring, DemoBlue, 0.1f, 508);
                    break;
                case "Copycat":
                    Crew("cat", new Color(0.82f, 0.74f, 0.95f));
                    Crew("red", DemoRed); Crew("cyan", DemoCyan);
                    MakeBtn("i1", KillButtonSprite(UCAssets.SilencerIcon));
                    MakeBtn("i2", UCAssets.ScoutIcon);
                    StageSprite("fx1", UCFx.Ring, DemoRed, 0.1f, 508);
                    StageSprite("fx2", UCFx.Ring, DemoCyan, 0.1f, 508);
                    StageSprite("fx3", UCFx.Ring, Accent, 0.1f, 508);
                    break;
                case "Collector":
                    Crew("col", new Color(1f, 0.8f, 0.35f));
                    MakeBtn("colBtn", UCAssets.CollectorIcon);
                    StagePic("r1", UCAssets.CollectorRelicSprite, 0.26f, 505);
                    StagePic("r2", UCAssets.CollectorRelicSprite, 0.26f, 505);
                    StageRect("bar", Accent, 0.5f, 0.04f);
                    StageCap("count", "0/2", 0.75f, Color.white);
                    StageSprite("fx", UCFx.Ring, Accent, 0.1f, 508);
                    break;
                case "Poltergeist":
                    Crew("vic", DemoWhite); Crew("ghost", DemoPurple); Crew("blue", DemoBlue);
                    MakeBtn("doorBtn", UCAssets.DoorIcon);
                    MakeBtn("hexBtn", UCAssets.HexIcon);
                    StageRect("door", new Color(0.62f, 0.45f, 0.28f), 0.1f, 0.5f);
                    StageRect("ebarBg", new Color(1f, 1f, 1f, 0.12f), 0.85f, 0.06f);
                    StageRect("ebar", DemoPurple, 0.85f, 0.045f);
                    StageSprite("fx1", UCFx.Ring, DemoRed, 0.1f, 508);
                    StageSprite("fx2", UCFx.Ring, DemoPurple, 0.1f, 508);
                    break;
            }
        }

        // True while x sits strictly inside a Seg window - used for "is this actor walking" checks.
        internal static bool Mid(float x) => x > 0f && x < 1f;

        private static void AnimateStage() {
            if (genericKind >= 0) { AnimateGeneric(); return; }
            if (stage == null || stageRole == null) return;
            if (ExtraDemos.TryGetValue(stageRole, out var extra) && extra.animate != null) {
                try { extra.animate(); } catch { }
                return;
            }
            float t = stageT;
            float FigMidY = FloorY + 0.25f; // body-centre height for kill bursts / marker rings
            switch (stageRole) {
                case "Tesla": {
                    // two crewmates get charged, drift together, the hidden countdown drains,
                    // electricity arcs between them, both drop dead
                    float p = P(6.5f);
                    float approach = Seg(p, 0.02f, 0.26f);
                    float close = Seg(p, 0.3f, 0.7f);
                    float dead = Seg(p, 0.74f, 0.86f);
                    float ax = Move(-1.35f, -0.34f, approach);
                    float bx = Move(1.35f, 0.34f, approach);
                    FigPut("a", ax, 0f, false, Mid(approach) ? 1f : 0f);
                    FigPut("b", bx, 0f, true, Mid(approach) ? 1f : 0f);
                    float danger = Mid(close) ? 0.5f + 0.5f * Mathf.Sin(t * 10f) : 0f;
                    FigCol("a", Color.Lerp(DemoBlue, DemoRed, danger * 0.5f), 1f);
                    FigCol("b", Color.Lerp(DemoGreen, DemoRed, danger * 0.5f), 1f);
                    FigDead("a", dead); FigDead("b", dead);
                    PutCap("plus", ax, 0.2f); CapA("plus", 1f - dead);
                    PutCap("minus", bx, 0.22f); CapA("minus", 1f - dead);
                    BarLeft("barBg", -0.45f, 0.31f, 0.9f, 0.06f);
                    BarLeft("bar", -0.45f, 0.31f, 0.9f * (1f - close), 0.045f);
                    ColA("bar", DemoRed, 0.9f * (1f - Seg(p, 0.74f, 0.9f)));
                    // electric arcs while the pair is too close (and one last flash as they die)
                    bool arc = (Mid(close) || Mid(dead)) && ((int)(t * 14f) % 3 != 0);
                    bool alt = ((int)(t * 14f)) % 2 == 0;
                    Put("boltA", 0f, FigMidY + 0.02f * Mathf.Sin(t * 31f));
                    ColA("boltA", new Color(0.75f, 0.9f, 1f), arc && alt ? 0.95f : 0f);
                    Put("boltB", 0f, FigMidY - 0.02f * Mathf.Sin(t * 27f));
                    ColA("boltB", new Color(0.75f, 0.9f, 1f), arc && !alt ? 0.95f : 0f);
                    Burst("fx", 0f, FigMidY, Seg(p, 0.74f, 0.88f), 0.9f, DemoRed);
                    break;
                }
                case "Saboteur": {
                    // the Saboteur presses SABOTAGE at a console and leaves; a crewmate finishes
                    // the task and is electrocuted
                    float p = P(6.5f);
                    Put("console", 0.95f, FloorY + 0.23f);
                    float sabbed = Seg(p, 0.26f, 0.3f) * (1f - Seg(p, 0.92f, 0.98f));
                    float use = Seg(p, 0.72f, 0.75f) * (1f - Seg(p, 0.78f, 0.82f));
                    Color baseCol = Color.Lerp(Color.white, new Color(1f, 0.45f, 0.4f), sabbed * 0.85f);
                    ColA("console", Color.Lerp(baseCol, Color.white, use), 1f);
                    float sIn = Seg(p, 0.02f, 0.2f), sOut = Seg(p, 0.38f, 0.56f);
                    float sx = p < 0.38f ? Move(-1.45f, 0.4f, sIn) : Move(0.4f, -1.55f, sOut);
                    FigPut("sab", sx, 0f, p >= 0.38f, (Mid(sIn) || Mid(sOut)) ? 1f : 0f);
                    FigCol("sab", DemoRed, 1f);
                    BtnPop("sabBtn", 0.4f, BtnY, Seg(p, 0.2f, 0.36f));
                    float cIn = Seg(p, 0.5f, 0.68f);
                    FigPut("crew", Move(-1.45f, 0.4f, cIn), 0f, false, Mid(cIn) ? 1f : 0f);
                    FigCol("crew", DemoBlue, 1f);
                    FigDead("crew", Seg(p, 0.8f, 0.9f));
                    bool zap = p > 0.76f && p < 0.86f && ((int)(t * 16f)) % 2 == 0;
                    Put("bolt", 0.4f, FigMidY);
                    ColA("bolt", new Color(0.75f, 0.9f, 1f), zap ? 0.95f : 0f);
                    Burst("fx", 0.4f, FigMidY, Seg(p, 0.78f, 0.92f), 0.8f, DemoRed);
                    break;
                }
                case "Poisoner": {
                    // a kill leaves a poisoned body (vial); the reporter gets infected, turns
                    // sickly green and staggers off, slowly dying
                    float p = P(7f);
                    float pIn = Seg(p, 0.02f, 0.18f), pOut = Seg(p, 0.3f, 0.48f);
                    float px = p < 0.3f ? Move(-1.45f, -0.2f, pIn) : Move(-0.2f, -1.55f, pOut);
                    FigPut("poi", px, 0f, p >= 0.3f, (Mid(pIn) || Mid(pOut)) ? 1f : 0f);
                    FigCol("poi", DemoRed, 1f);
                    FigPut("vic", 0.25f, 0f, true, 0f);
                    FigCol("vic", Color.Lerp(DemoWhite, new Color(0.5f, 0.5f, 0.55f), Seg(p, 0.18f, 0.26f)), 1f);
                    FigDead("vic", Seg(p, 0.18f, 0.26f));
                    Burst("fx", 0.25f, FigMidY, Seg(p, 0.17f, 0.3f), 0.6f, DemoRed);
                    // pulsing poison vial over the body
                    Put("vial", 0.25f, FloorY + 0.24f + 0.02f * Mathf.Sin(t * 3f));
                    Color vialCol = Color.Lerp(Color.white, new Color(0.5f, 1f, 0.5f), 0.5f + 0.5f * Mathf.Sin(t * 4f));
                    ColA("vial", vialCol, Seg(p, 0.28f, 0.34f) * (1f - Seg(p, 0.7f, 0.78f)));
                    // reporter walks in, gets infected, staggers off fading
                    float rIn = Seg(p, 0.46f, 0.62f), rOut = Seg(p, 0.8f, 0.95f);
                    float rx = p < 0.8f ? Move(1.45f, 0.6f, rIn) : Move(0.6f, 1.5f, rOut);
                    float sick = Seg(p, 0.66f, 0.76f);
                    FigPut("rep", rx, 0f, p < 0.8f, (Mid(rIn) || Mid(rOut)) ? 1f : 0f);
                    FigCol("rep", Color.Lerp(DemoBlue, new Color(0.5f, 0.85f, 0.35f), sick), 1f - 0.6f * Seg(p, 0.82f, 0.96f));
                    PutCap("mark", rx, 0.2f);
                    CapA("mark", Seg(p, 0.62f, 0.68f) * (1f - Seg(p, 0.76f, 0.86f)));
                    break;
                }
                case "Silencer": {
                    // the Silencer presses SILENCE; the wave hits the victim, whose speech bubble
                    // gets crossed out for the next meeting
                    float p = P(6f);
                    FigPut("sil", -1.0f, 0f, false, 0f);
                    FigCol("sil", DemoRed, 1f);
                    float mutedK = Seg(p, 0.46f, 0.54f);
                    FigPut("vic", 0.9f, 0f, true, 0f);
                    FigCol("vic", Color.Lerp(DemoBlue, new Color(0.32f, 0.42f, 0.55f), mutedK), 1f);
                    BtnPop("silBtn", -1.0f, BtnY, Seg(p, 0.08f, 0.24f));
                    float wave = Seg(p, 0.28f, 0.46f);
                    Put("wave", Move(-0.8f, 0.68f, wave), FigMidY);
                    ColA("wave", DemoRed, Mid(wave) ? 0.85f : 0f);
                    PutCap("chat", 0.9f, 0.24f);
                    CapA("chat", p >= 0.46f ? 0.2f : 0.85f);
                    PutCap("mute", 0.9f, 0.24f);
                    CapA("mute", Seg(p, 0.48f, 0.58f) * (1f - Seg(p, 0.9f, 0.98f)));
                    break;
                }
                case "Illusionist": {
                    // RECORD: walk a path leaving footprints; step aside; PLAYBACK: a translucent
                    // clone re-walks the exact same route
                    float p = P(7.5f);
                    BtnPop("recBtn", -1.35f, BtnY, Seg(p, 0f, 0.1f));
                    float rec = Seg(p, 0.06f, 0.32f);
                    float rx = Move(-1.35f, 1.25f, rec);
                    float back = Seg(p, 0.36f, 0.52f);
                    float realX = p < 0.36f ? rx : Move(1.25f, -1.2f, back);
                    FigPut("real", realX, 0f, p >= 0.36f && p < 0.56f, (Mid(rec) || Mid(back)) ? 1f : 0f);
                    FigCol("real", DemoRed, 1f);
                    for (int i = 0; i < 7; i++) {
                        float xi = Mathf.Lerp(-1.35f, 1.25f, (i + 0.5f) / 7f);
                        Put("t" + i, xi, FloorY + 0.02f);
                        float on = p < 0.36f ? (rx >= xi ? 0.35f : 0f) : 0.35f;
                        ColA("t" + i, Color.white, on * (1f - Seg(p, 0.92f, 1f)));
                    }
                    BtnPop("playBtn", -1.2f, BtnY, Seg(p, 0.52f, 0.64f));
                    float rep = Seg(p, 0.58f, 0.9f);
                    float cAlpha = 0.5f * Seg(p, 0.56f, 0.6f) * (1f - Seg(p, 0.92f, 0.98f));
                    FigPut("clone", Move(-1.35f, 1.25f, rep), 0f, false, Mid(rep) ? 1f : 0f);
                    FigCol("clone", DemoRed, cAlpha, 0.12f * (cAlpha > 0f ? 1f : 0f));
                    break;
                }
                case "Maniac": {
                    // bomb planted (BOMB button), noticed, passed on by touch (PASS button - the
                    // fuse keeps running and blinks faster), then it explodes on the last carrier
                    float p = P(7f);
                    float mIn = Seg(p, 0.02f, 0.16f), mOut = Seg(p, 0.3f, 0.46f);
                    float mx = p < 0.3f ? Move(-1.45f, -0.68f, mIn) : Move(-0.68f, -1.55f, mOut);
                    FigPut("man", mx, 0f, p >= 0.3f, (Mid(mIn) || Mid(mOut)) ? 1f : 0f);
                    FigCol("man", DemoRed, 1f);
                    BtnPop("bombBtn", -0.68f, BtnY, Seg(p, 0.16f, 0.3f));
                    float walk1 = Seg(p, 0.4f, 0.58f), flee = Seg(p, 0.62f, 0.8f);
                    float v1x = p < 0.62f ? Move(-0.35f, 0.62f, walk1) : Move(0.62f, -0.75f, flee);
                    FigPut("v1", v1x, 0f, p >= 0.62f, (Mid(walk1) || Mid(flee)) ? 1f : 0f);
                    FigCol("v1", DemoBlue, 1f);
                    BtnPop("passBtn", 0.62f, BtnY, Seg(p, 0.58f, 0.7f));
                    FigPut("v2", 0.95f, 0f, true, 0f);
                    FigCol("v2", DemoGreen, 1f);
                    FigDead("v2", Seg(p, 0.9f, 0.98f));
                    // the bomb rides its current carrier, blinking ever faster
                    bool bombOn = p >= 0.2f && p < 0.9f;
                    float blink = 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(t * (3f + 14f * p)));
                    Put("bomb", p < 0.6f ? v1x : 0.95f, FloorY + 0.52f + 0.015f * Mathf.Sin(t * 5f));
                    ColA("bomb", Color.white, bombOn ? blink : 0f);
                    float boom = Seg(p, 0.88f, 0.98f);
                    Put("burst", 0.95f, FigMidY);
                    PicScale("burst", 0.2f + 1.0f * boom);
                    ColA("burst", Color.white, Mid(boom) ? 1f - boom : 0f);
                    break;
                }
                case "Shade": {
                    // the body sinks away in smoke right after the kill; a passer-by walking close
                    // enough makes it reappear
                    float p = P(7f);
                    float shIn = Seg(p, 0.02f, 0.16f), shOut = Seg(p, 0.28f, 0.46f);
                    float shx = p < 0.28f ? Move(-1.45f, -0.6f, shIn) : Move(-0.6f, -1.55f, shOut);
                    FigPut("shade", shx, 0f, p >= 0.28f, (Mid(shIn) || Mid(shOut)) ? 1f : 0f);
                    FigCol("shade", DemoRed, 1f - Seg(p, 0.42f, 0.52f));
                    float killed = Seg(p, 0.18f, 0.26f);
                    float vanish = Seg(p, 0.3f, 0.42f);
                    float wIn = Seg(p, 0.5f, 0.84f);
                    float wx = Move(1.45f, -1.25f, wIn);
                    float near = Mathf.Clamp01(1f - Mathf.Abs(wx + 0.3f) / 0.45f) * (p > 0.5f ? 1f : 0f);
                    float revealed = Mathf.Max(near, Seg(p, 0.68f, 0.72f)); // stays found once seen
                    float bodyA = p < 0.3f ? 1f : Mathf.Max(1f - vanish, 0.92f * revealed);
                    FigPut("vic", -0.3f, 0f, true, 0f);
                    FigCol("vic", Color.Lerp(DemoWhite, new Color(0.5f, 0.5f, 0.55f), killed), bodyA);
                    FigDead("vic", killed);
                    // smoke puffs while the body sinks away
                    for (int i = 0; i < 3; i++) {
                        float sp = Seg(p, 0.29f + i * 0.03f, 0.46f + i * 0.03f);
                        Put("s" + i, -0.3f + (i - 1) * 0.14f, FloorY + 0.08f + 0.22f * sp);
                        ColA("s" + i, new Color(0.4f, 0.3f, 0.55f), Mid(sp) ? 0.5f * (1f - sp) : 0f);
                    }
                    FigPut("walker", wx, 0f, true, Mid(wIn) ? 1f : 0f);
                    FigCol("walker", DemoBlue, 1f);
                    PutCap("mark", wx, 0.2f);
                    CapA("mark", Seg(p, 0.68f, 0.74f) * (1f - Seg(p, 0.84f, 0.92f)));
                    Burst("fx", -0.3f, FigMidY, Seg(p, 0.17f, 0.29f), 0.6f, DemoRed);
                    break;
                }
                case "Manipulator": {
                    // the Manipulator presses FAKE: the admin table flickers, then shows wrong
                    // positions - the faint ghosts are where everyone REALLY is
                    float p = P(6f);
                    FigPut("manip", -1.3f, 0f, false, 0f);
                    FigCol("manip", DemoRed, 1f);
                    BtnPop("fakeBtn", -1.3f, BtnY, Seg(p, 0.18f, 0.34f));
                    Put("map", 0.55f, 0.05f);
                    PutCap("admin", -0.12f, 0.28f);
                    CapA("admin", 0.4f);
                    float lie = Seg(p, 0.34f, 0.42f) * (1f - Seg(p, 0.86f, 0.93f));
                    bool transition = (p > 0.32f && p < 0.44f) || (p > 0.84f && p < 0.95f);
                    float flick = transition ? 0.55f + 0.45f * Mathf.Sin(t * 40f) : 1f;
                    float[] tx = { 0.05f, 0.6f, 1.0f };
                    float[] ty = { 0.15f, -0.07f, 0.13f };
                    float[] fx2 = { 1.05f, 0.1f, 0.5f };
                    float[] fy = { -0.09f, 0.11f, -0.11f };
                    Color[] cols = { DemoBlue, DemoGreen, DemoRed };
                    for (int i = 0; i < 3; i++) {
                        float e2 = Ease(lie);
                        Put("d" + i, Mathf.Lerp(tx[i], fx2[i], e2), Mathf.Lerp(ty[i], fy[i], e2));
                        ColA("d" + i, cols[i], flick);
                        Put("g" + i, tx[i], ty[i]);
                        ColA("g" + i, cols[i], 0.15f * lie);
                    }
                    break;
                }
                case "Auditor": {
                    // a crewmate finishes a task at the console (the bar ticks up), the Auditor comes
                    // to the SAME console and does it again - the bar falls right back
                    float p = P(8f);
                    const float ConsoleX = -0.15f, WorkX = -0.55f;
                    Put("console", ConsoleX, FigMidY);

                    float cIn = Seg(p, 0.02f, 0.16f), cOut = Seg(p, 0.30f, 0.46f);
                    float cx = p < 0.30f ? Move(-1.55f, WorkX, cIn) : Move(WorkX, 1.6f, cOut);
                    bool cWorking = p > 0.16f && p < 0.30f;
                    FigPut("crew", cx + (cWorking ? 0.015f * Mathf.Sin(t * 22f) : 0f), 0f,
                           false, (Mid(cIn) || Mid(cOut)) ? 1f : 0f); // in from the left, out to the right
                    FigCol("crew", DemoBlue, 1f);

                    float aIn = Seg(p, 0.48f, 0.64f), aOut = Seg(p, 0.82f, 0.98f);
                    float ax = p < 0.82f ? Move(-1.55f, WorkX, aIn) : Move(WorkX, -1.6f, aOut);
                    bool aWorking = p > 0.64f && p < 0.82f;
                    FigPut("aud", ax + (aWorking ? 0.015f * Mathf.Sin(t * 22f) : 0f), 0f,
                           p >= 0.82f, (Mid(aIn) || Mid(aOut)) ? 1f : 0f);
                    FigCol("aud", DemoRed, 1f);

                    // one notch up while the crewmate works, the same notch back down when he undoes it
                    float fill = 0.35f + 0.22f * Seg(p, 0.18f, 0.28f) - 0.22f * Seg(p, 0.68f, 0.78f);
                    BarLeft("barBg", -0.85f, 0.36f, 1.7f, 0.08f);
                    BarLeft("bar", -0.85f, 0.36f, 1.7f * fill, 0.06f);
                    ColA("bar", Color.Lerp(DemoGreen, Accent, Seg(p, 0.68f, 0.78f) * (1f - Seg(p, 0.9f, 1f))), 0.95f);

                    PutCap("undo", ConsoleX, 0.26f);
                    CapA("undo", Seg(p, 0.7f, 0.76f) * (1f - Seg(p, 0.88f, 0.96f)));
                    Burst("fx", ConsoleX, FigMidY, Seg(p, 0.68f, 0.8f), 0.55f, Accent);
                    break;
                }
                case "Siphoner": {
                    // standing near the impostor: their kill button hangs overhead while the
                    // cooldown gauge only ever fills UP - it never gets to kill
                    float p = P(6f);
                    float sIn = Seg(p, 0.02f, 0.24f);
                    FigPut("sip", Move(-1.45f, -0.28f, sIn), 0f, false, Mid(sIn) ? 1f : 0f);
                    FigCol("sip", DemoCyan, 1f);
                    float draining = Seg(p, 0.28f, 0.32f);
                    FigPut("imp", 0.45f + 0.02f * Mathf.Sin(t * 25f) * draining, 0f, true, 0f);
                    FigCol("imp", DemoRed, 1f);
                    BtnPop("killBtn", 0.45f, BtnY, Seg(p, 0.26f, 0.98f));
                    float fill = 0.25f + 0.75f * Seg(p, 0.3f, 0.92f);
                    BarLeft("barBg", -1.6f, 0.31f, 0.9f, 0.06f);
                    BarLeft("bar", -1.6f, 0.31f, 0.9f * fill, 0.045f);
                    ColA("bar", Color.Lerp(DemoGreen, DemoRed, fill), 0.9f);
                    float fp = Mathf.Repeat(t * 0.9f, 1f);
                    Put("flow", Mathf.Lerp(0.45f, -0.28f, fp), FigMidY + 0.06f * Mathf.Sin(fp * 6.28f));
                    ColA("flow", DemoRed, draining * 0.7f * (1f - fp));
                    break;
                }
                case "Witness": {
                    // sole witness of a kill: the killer stays marked red while walking away
                    float p = P(6.5f);
                    FigPut("wit", -1.15f, 0f, false, 0f);
                    FigCol("wit", DemoCyan, 1f);
                    float kIn = Seg(p, 0.02f, 0.16f), kOut = Seg(p, 0.34f, 0.6f);
                    float kx = p < 0.34f ? Move(1.45f, 0.5f, kIn) : Move(0.5f, 1.45f, kOut);
                    FigPut("killer", kx, 0f, p < 0.34f, (Mid(kIn) || Mid(kOut)) ? 1f : 0f);
                    FigCol("killer", DemoRed, 1f);
                    float killed = Seg(p, 0.18f, 0.26f);
                    FigPut("vic", 0.9f, 0f, true, 0f);
                    FigCol("vic", Color.Lerp(DemoWhite, new Color(0.5f, 0.5f, 0.55f), killed), 1f);
                    FigDead("vic", killed);
                    float markA = Seg(p, 0.26f, 0.34f) * (1f - Seg(p, 0.9f, 0.98f));
                    Put("markRing", kx, FigMidY);
                    float ringS = 0.5f + 0.05f * Mathf.Sin(t * 6f);
                    Size2("markRing", ringS, ringS);
                    ColA("markRing", DemoRed, markA);
                    PutCap("mark", -1.15f, 0.2f);
                    CapA("mark", Seg(p, 0.22f, 0.28f) * (1f - Seg(p, 0.5f, 0.6f)));
                    Burst("fx", 0.9f, FigMidY, Seg(p, 0.17f, 0.29f), 0.6f, DemoRed);
                    break;
                }
                case "Scout": {
                    // SCOUT button: turn near-invisible and dash across the stage with speed
                    // streaks, then fade back in
                    float p = P(6f);
                    BtnPop("scoutBtn", -1.3f, BtnY, Seg(p, 0.1f, 0.24f));
                    float act = Seg(p, 0.2f, 0.26f) * (1f - Seg(p, 0.8f, 0.88f));
                    float dash1 = Seg(p, 0.24f, 0.46f);
                    float dash2 = Seg(p, 0.52f, 0.76f);
                    float x = p < 0.5f ? Move(-1.3f, 1.25f, dash1) : Move(1.25f, -0.3f, dash2);
                    bool dashing = Mid(dash1) || Mid(dash2);
                    float dir = p < 0.5f ? 1f : -1f;
                    FigPut("scout", x, 0f, dir < 0f, dashing ? 1f : 0f);
                    FigCol("scout", DemoCyan, Mathf.Lerp(1f, 0.3f, act));
                    Put("st0", x - 0.42f * dir, FigMidY - 0.04f);
                    ColA("st0", DemoCyan, dashing ? 0.3f * act : 0f);
                    Put("st1", x - 0.68f * dir, FigMidY - 0.09f);
                    ColA("st1", DemoCyan, dashing ? 0.18f * act : 0f);
                    break;
                }
                case "Beacon": {
                    // lights are out; a crewmate only sees (= is bright) inside the Beacon's
                    // shared vision circle
                    float p = P(6f);
                    Put("dark", 0f, 0f); ColA("dark", Color.black, 0.55f);
                    Put("light", 0f, FigMidY);
                    ColA("light", Accent, 0.2f + 0.03f * Mathf.Sin(t * 2.5f));
                    FigPut("beacon", 0f, 0f, false, 0f);
                    FigCol("beacon", Accent, 1f);
                    float pp = Mathf.PingPong(p * 2f, 1f);
                    float cx = Mathf.Lerp(-1.4f, 1.4f, pp);
                    bool leftward = (p * 2f) % 2f >= 1f;
                    float inside = Mathf.Clamp01(1f - (Mathf.Abs(cx) - 0.45f) / 0.3f);
                    FigPut("crew", cx, 0f, leftward, 1f);
                    FigCol("crew", DemoBlue, Mathf.Lerp(0.32f, 1f, inside));
                    break;
                }
                case "Bug": {
                    // chaos elsewhere, the Bug just stands still - and steals the team win at the end
                    float p = P(6.5f);
                    float tl = Mathf.Repeat(t, 6.5f);            // loop-local clock for the chase
                    float tb = Mathf.Min(tl, 0.66f * 6.5f);      // blue's clock freezes when it dies
                    float bx = -0.5f + 0.5f * Mathf.Sin(tb * 1.6f);
                    bool blueLeft = Mathf.Cos(tb * 1.6f) < 0f;
                    FigPut("blue", bx, 0f, blueLeft, p < 0.64f ? 1f : 0f);
                    FigCol("blue", DemoBlue, 1f);
                    FigDead("blue", Seg(p, 0.66f, 0.76f));
                    float rxx = -0.5f + 0.5f * Mathf.Sin(tl * 1.6f - 0.85f);
                    FigPut("red", rxx, 0f, Mathf.Cos(tl * 1.6f - 0.85f) < 0f, 1f);
                    FigCol("red", DemoRed, 1f);
                    float win = Seg(p, 0.78f, 0.88f);
                    FigPut("bug", 1.25f, 0f, true, 0f);
                    FigCol("bug", Color.Lerp(DemoGray, Accent, win), 1f);
                    Burst("fx", 1.25f, FigMidY, Seg(p, 0.78f, 0.94f), 0.9f, Accent);
                    PutCap("win", 1.25f, 0.26f);
                    CapA("win", win * (1f - Seg(p, 0.96f, 1f)));
                    break;
                }
                case "Follower": {
                    // the first death transfers its role: the gray Follower takes the victim's
                    // color (and with it team, abilities and win condition)
                    float p = P(6.5f);
                    float rIn = Seg(p, 0.02f, 0.18f), rOut = Seg(p, 0.32f, 0.5f);
                    float rx = p < 0.32f ? Move(-1.5f, -0.85f, rIn) : Move(-0.85f, -1.55f, rOut);
                    FigPut("red", rx, 0f, p >= 0.32f, (Mid(rIn) || Mid(rOut)) ? 1f : 0f);
                    FigCol("red", DemoRed, 1f);
                    float dead = Seg(p, 0.2f, 0.28f);
                    FigPut("blue", -0.5f, 0f, true, 0f);
                    FigCol("blue", Color.Lerp(DemoBlue, new Color(0.3f, 0.35f, 0.45f), dead), 1f);
                    FigDead("blue", dead);
                    Burst("fx", -0.5f, FigMidY, Seg(p, 0.19f, 0.31f), 0.6f, DemoRed);
                    float soul = Seg(p, 0.38f, 0.58f);
                    Put("soul", Mathf.Lerp(-0.5f, 0.7f, Ease(soul)), FigMidY + 0.28f * Mathf.Sin(Ease(soul) * Mathf.PI));
                    ColA("soul", DemoBlue, Mid(soul) ? 0.85f : 0f);
                    float take = Seg(p, 0.58f, 0.68f);
                    FigPut("fol", 0.7f, 0.05f * Mathf.Sin(Mathf.PI * Seg(p, 0.58f, 0.7f)), true, 0f);
                    FigCol("fol", Color.Lerp(DemoGray, DemoBlue, take), 1f);
                    Burst("fx2", 0.7f, FigMidY, Seg(p, 0.58f, 0.74f), 0.7f, DemoBlue);
                    break;
                }
                case "Copycat": {
                    // abilities used elsewhere appear as REAL buttons above the Copycat; enough
                    // used + surviving = winning alongside the winners
                    float p = P(7f);
                    float win = Seg(p, 0.78f, 0.88f);
                    FigPut("cat", 0.85f, 0f, true, 0f);
                    FigCol("cat", Color.Lerp(new Color(0.82f, 0.74f, 0.95f), Accent, win), 1f);
                    FigPut("red", -1.05f, 0f, false, 0f);
                    FigCol("red", DemoRed, 1f);
                    FigPut("cyan", -0.2f, 0f, false, 0f);
                    FigCol("cyan", DemoCyan, 1f);
                    Burst("fx1", -1.05f, FigMidY, Seg(p, 0.1f, 0.24f), 0.55f, DemoRed);
                    Burst("fx2", -0.2f, FigMidY, Seg(p, 0.4f, 0.54f), 0.55f, DemoCyan);
                    BtnPop("i1", 0.62f, BtnY + 0.04f, Seg(p, 0.24f, 0.97f));
                    BtnPop("i2", 1.05f, BtnY + 0.04f, Seg(p, 0.54f, 0.97f));
                    Burst("fx3", 0.85f, FigMidY, Seg(p, 0.78f, 0.94f), 0.85f, Accent);
                    break;
                }
                case "Collector": {
                    // walk to a glowing relic and CHANNEL it away (button + progress), repeat -
                    // enough relics = winning alone
                    float p = P(7f);
                    float w1 = Seg(p, 0.02f, 0.16f), w2 = Seg(p, 0.44f, 0.62f);
                    float colx = p < 0.44f ? Move(-1.45f, -0.78f, w1) : Move(-0.78f, 0.78f, w2);
                    FigPut("col", colx, 0f, false, (Mid(w1) || Mid(w2)) ? 1f : 0f);
                    FigCol("col", new Color(1f, 0.8f, 0.35f), 1f);
                    float sparkle = 0.65f + 0.35f * Mathf.Sin(t * 5f);
                    Put("r1", -0.5f, FloorY + 0.13f); ColA("r1", Color.white, p < 0.42f ? sparkle : 0f);
                    Put("r2", 1.05f, FloorY + 0.13f); ColA("r2", Color.white, p < 0.86f ? sparkle : 0f);
                    float b1 = Seg(p, 0.18f, 0.42f), b2 = Seg(p, 0.64f, 0.88f);
                    BtnPop("colBtn", colx + 0.48f, 0.14f, Mid(b1) ? b1 : b2); // beside, not above (stage top is tight)
                    float ch = Mid(b1) ? b1 : (Mid(b2) ? b2 : 0f);
                    BarLeft("bar", colx - 0.25f, 0.16f, 0.5f * ch, 0.04f);
                    ColA("bar", Accent, ch > 0f ? 0.9f : 0f);
                    CapText("count", p < 0.42f ? "0/2" : p < 0.86f ? "1/2" : "2/2");
                    PutCap("count", 1.55f, 0.3f);
                    CapA("count", 0.75f);
                    Burst("fx", 0.78f, FigMidY, Seg(p, 0.9f, 0.99f), 0.9f, Accent);
                    break;
                }
                case "Poltergeist": {
                    // the first death rises as a translucent ghost; DOOR slam and HEX each drain
                    // the energy bar, which then slowly refills
                    float p = P(7f);
                    float killed = Seg(p, 0.06f, 0.14f);
                    FigPut("vic", -1.0f, 0f, false, 0f);
                    FigCol("vic", Color.Lerp(DemoWhite, new Color(0.5f, 0.5f, 0.55f), killed), 1f);
                    FigDead("vic", killed);
                    Burst("fx1", -1.0f, FigMidY, Seg(p, 0.05f, 0.17f), 0.5f, DemoRed);
                    float rise = Seg(p, 0.14f, 0.26f);
                    FigPut("ghost", -1.0f, 0.14f * rise + 0.02f * Mathf.Sin(t * 2.2f), false, 0f);
                    FigCol("ghost", DemoPurple, 0.55f * rise, 0f); // floats - no ground shadow
                    BtnPop("doorBtn", -0.52f, 0.12f, Seg(p, 0.3f, 0.44f)); // beside the floating ghost
                    float doorK = Seg(p, 0.38f, 0.42f);
                    Size2("door", 0.1f, 0.5f * Mathf.Max(doorK, 0.001f));
                    Put("door", 0.2f, FloorY + 0.25f * doorK);
                    ColA("door", new Color(0.62f, 0.45f, 0.28f), doorK > 0f ? 1f - Seg(p, 0.68f, 0.76f) : 0f);
                    BtnPop("hexBtn", -0.52f, 0.12f, Seg(p, 0.52f, 0.66f));
                    FigPut("blue", 1.05f, 0f, true, 0f);
                    float hex = Seg(p, 0.58f, 0.7f);
                    float hexPulse = Mid(hex) ? 0.6f * Mathf.Sin(hex * Mathf.PI) : 0f;
                    FigCol("blue", Color.Lerp(DemoBlue, DemoPurple, hexPulse), 1f);
                    Burst("fx2", 1.05f, FigMidY, hex, 0.55f, DemoPurple);
                    float e = 1f;
                    if (p >= 0.4f) e = 0.62f;
                    if (p >= 0.6f) e = 0.3f;
                    if (p >= 0.72f) e = Mathf.Min(1f, 0.3f + 0.6f * Seg(p, 0.72f, 1f));
                    BarLeft("ebarBg", -1.65f, 0.31f, 0.85f, 0.06f);
                    BarLeft("ebar", -1.65f, 0.31f, 0.85f * e, 0.045f);
                    break;
                }
            }
        }

        // ====================================================================
        // While the search field is FOCUSED, typed letters must not walk the player around -
        // mirror the vanilla chat: CanMove reports false for the local player for as long as
        // the focus lasts. With the field unfocused the panel leaves movement alone entirely.
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CanMove), MethodType.Getter)]
        static class BlockMoveWhileTypingPatch {
            public static void Postfix(PlayerControl __instance, ref bool __result) {
                try {
                    if (__result && searchFocused && panel != null && __instance.AmOwner)
                        __result = false;
                } catch { }
            }
        }

        // ====================================================================
        // Per-frame: visibility gate + manual hover/click resolution.
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        [HarmonyPriority(Priority.Low)]
        static class HudUpdatePatch {
            public static void Postfix(HudManager __instance) {
                try {
                    if (button == null) return;

                    bool visible = __instance.UseButton != null && __instance.UseButton.isActiveAndEnabled
                                   && !MeetingHud.Instance && !ExileController.Instance;
                    if (button.activeSelf != visible) button.SetActive(visible);
                    if (!visible) { if (panel != null) ClosePanel(); return; }

                    if (panel != null && Input.GetKeyDown(KeyCode.Escape)) {
                        if (searchFocused) searchFocused = false; // first Escape only leaves the field
                        else ClosePanel();
                        return;
                    }

                    // Mouse mapping through the SAME camera that renders (and fits) the UI layer.
                    var cam = FitCamera(button.layer);
                    if (cam == null) return;
                    Vector3 world = cam.ScreenToWorldPoint(Input.mousePosition);

                    // Hover highlight for role rows (and keep the selected row lit). All hit tests
                    // run in panel-LOCAL space: InverseTransformPoint divides the panel's camera-fit
                    // scale out, so the design-unit extents (h.w/h.h) stay valid at any zoom.
                    Vector3 local = Vector3.zero;
                    if (panel != null) {
                        ApplyCameraFit(panel); // keep centred + scaled even if the camera changes
                        if (stage != null) { stageT += Time.deltaTime; AnimateStage(); }

                        // live search: type to filter, backspace to delete - but ONLY while the
                        // field is focused (clicked); otherwise the keys stay with the game so
                        // the player can keep walking with the guide open. Enter leaves the field.
                        string typed = searchFocused ? Input.inputString : null;
                        if (!string.IsNullOrEmpty(typed)) {
                            bool changed = false;
                            foreach (char ch in typed) {
                                if (ch == '\r' || ch == '\n') {
                                    searchFocused = false;
                                } else if (ch == '\b') {
                                    if (searchQuery.Length > 0) { searchQuery = searchQuery.Substring(0, searchQuery.Length - 1); changed = true; }
                                } else if (ch >= ' ' && ch != '\u007f' && searchQuery.Length < 24) {
                                    searchQuery += ch; changed = true;
                                }
                            }
                            if (changed) { scrollLines = 0; BuildList(); }
                        }
                        float wheel = Input.mouseScrollDelta.y;
                        if (Mathf.Abs(wheel) > 0.01f && !langDropdownOpen) {
                            int next = Mathf.Clamp(scrollLines - (int)Mathf.Sign(wheel) * 2, 0, maxScroll);
                            if (next != scrollLines) { scrollLines = next; BuildList(); }
                        }
                        if (searchLabel != null) {
                            bool blink = searchFocused && Mathf.Repeat(Time.unscaledTime, 1f) < 0.55f;
                            searchLabel.text = T("uc.helpui.search") + ": " + searchQuery + (blink ? "_" : " ");
                            // dimmed while unfocused so "click first, then type" is readable at a glance
                            searchLabel.color = new Color(1f, 1f, 1f, searchFocused ? 0.95f : 0.55f);
                        }
                        local = panel.transform.InverseTransformPoint(world);
                        foreach (var h in hits) {
                            if (h?.hover == null || h.anchor == null) continue;
                            bool over = Mathf.Abs(local.x - h.anchor.localPosition.x) < h.w / 2f
                                        && Mathf.Abs(local.y - h.anchor.localPosition.y) < h.h / 2f;
                            bool isSelected = h.entry != null && h.entry == selected;
                            float alpha = isSelected ? 0.16f : over ? 0.09f : 0f;
                            Color c = isSelected ? Accent : Color.white;
                            h.hover.color = new Color(c.r, c.g, c.b, alpha);
                        }
                    }

                    if (!Input.GetMouseButtonDown(0)) return;

                    // any click drops the search focus; the search-field hitbox re-sets it
                    searchFocused = false;

                    // The "?" button is NOT scaled (AspectPosition-anchored), so its test stays in
                    // world space.
                    Vector3 bp = button.transform.position;
                    if (Mathf.Abs(world.x - bp.x) < 0.35f && Mathf.Abs(world.y - bp.y) < 0.35f) {
                        TogglePanel();
                        return;
                    }

                    if (panel == null) return;
                    foreach (var h in new List<HitBox>(hits)) {
                        if (h?.anchor == null) continue;
                        Vector3 c = h.anchor.localPosition;
                        if (Mathf.Abs(local.x - c.x) < h.w / 2f && Mathf.Abs(local.y - c.y) < h.h / 2f) {
                            h.onClick?.Invoke();
                            return;
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCHelpMenu] update failed: {e}");
                }
            }
        }
    }
}
