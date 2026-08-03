// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCLocalization - Unknown's Collection side of the shared localization system.
 *
 * The engine lives in UsefulTORStuff (UTSLocalization); this file is the small per-mod
 * counterpart the cross-mod contract expects (same duplicate-the-helper convention as
 * VersionDisplay): UC ships its OWN string tables (uc.* keys, embedded as
 * "UnknownsCollection.Resources.Localization.<code>.json") plus a copy of the flat-map
 * JSON loader, and follows the language UTS publishes via AppDomain:
 *   "UTS.Loc.ActiveCode" -> string   active language code (e.g. "german", "tr")
 *   "UTS.Loc.Epoch"      -> int      bumped on every UTS re-apply
 * Without UTS installed the mod still localizes itself: it falls back to resolving the
 * vanilla language (DataManager.Settings.Language.CurrentLanguage) directly. Change
 * detection is a throttled poll in HudManager.Update plus a MainMenuManager.Start apply
 * (no own TranslationController.SetLanguage patch - UTS already has one, and in the
 * no-UTS case the poll catches the switch within half a second).
 *
 * UC's roles/options are TEXT-KEYED rather than field-mapped: the English reference table
 * knows every original string, so Apply() matches RoleInfo entries (via the sentinel
 * RoleIds 200+) and CustomOptions (id range 1400-1699) by their pristine English text and
 * swaps in the translation. Originals are captured on first sight so switching languages
 * (including back to English) always re-derives from the pristine text. Dynamic strings
 * (chat, HUD, buttons) go through Tr() at their call sites. Community overrides: uc.* keys
 * in BepInEx/config/UTSLocalization/<code>.json (same file UTS reads) win over embedded.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class UCLocalization {
        private const string ActiveCodeKey = "UTS.Loc.ActiveCode";
        private const string EpochKey = "UTS.Loc.Epoch";

        public static readonly string[] KnownCodes = {
            "en", "latam", "brazilian", "portuguese", "korean", "russian", "dutch",
            "filipino", "french", "german", "italian", "japanese", "spanish",
            "schinese", "tchinese", "irish",
            "tr", "pl", "cs", "hu", "ro", "sv", "fi", "uk", "id", "vi"
        };

        private static readonly Dictionary<string, string> english = new();
        private static readonly Dictionary<string, string> active = new();
        // pristine originals, captured on first apply (keyed by object reference)
        private static readonly Dictionary<RoleInfo, (string name, string intro, string shortDesc)> roleOriginals = new();
        private static readonly Dictionary<CustomOption, (string name, object[] selections)> optionOriginals = new();

        public static string ActiveCode { get; private set; } = "en";
        public static event Action LanguageApplied;
        private static int lastEpoch = -1;
        private static float nextPoll;

        public static void Initialize() {
            LoadTable("en", english);
            Apply("initial load");
        }

        // ---------- lookup ----------

        public static string Tr(string key) {
            if (active.TryGetValue(key, out var t) && t.Length > 0) return t;
            if (english.TryGetValue(key, out var e) && e.Length > 0) return e;
            return key;
        }

        public static string Tr(string key, params object[] args) {
            var t = Tr(key);
            try { return string.Format(t, args); }
            catch (FormatException) { return t; }
        }

        // Lookup in an EXPLICIT language, independent of the active one (the help menu's
        // session-only language picker needs arbitrary tables). Tables load lazily, once
        // per code; fallback chain: requested language -> en -> key.
        private static readonly Dictionary<string, Dictionary<string, string>> tablesByCode = new();
        public static string TrIn(string code, string key) {
            if (string.IsNullOrEmpty(code) || code == ActiveCode) return Tr(key);
            if (Array.IndexOf(KnownCodes, code) < 0) return Tr(key);
            if (!tablesByCode.TryGetValue(code, out var table)) {
                table = new Dictionary<string, string>();
                LoadTable(code, table);
                tablesByCode[code] = table;
            }
            if (table.TryGetValue(key, out var t) && t.Length > 0) return t;
            if (english.TryGetValue(key, out var e) && e.Length > 0) return e;
            return key;
        }

        // ---------- applying ----------

        public static void Apply(string reason) {
            var code = ResolveCode();
            LoadTable(code, active);
            ActiveCode = code;
            try { ApplyRoles(); } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[UCLoc] role apply failed: {e.Message}"); }
            try { ApplyOptions(); } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[UCLoc] option apply failed: {e.Message}"); }
            try { LanguageApplied?.Invoke(); } catch { }
            UnknownsCollectionPlugin.Logger?.LogInfo($"[UCLoc] language \"{code}\" applied ({active.Count} keys, {reason})");
        }

        private static string ResolveCode() {
            try {
                if (AppDomain.CurrentDomain.GetData(ActiveCodeKey) is string s
                    && Array.IndexOf(KnownCodes, s) >= 0) return s;
            } catch { }
            // no UTS: follow the vanilla language directly
            try {
                var code = AmongUs.Data.DataManager.Settings.Language.CurrentLanguage
                    .ToString().ToLowerInvariant();
                if (code == "english") return "en";
                return Array.IndexOf(KnownCodes, code) >= 0 ? code : "en";
            } catch { return "en"; }
        }

        // English text -> key maps for one prefix + suffix (e.g. all "uc.role.*.name" entries).
        private static Dictionary<string, string> EnToKey(string prefix, string suffix) {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in english)
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)
                    && kv.Key.EndsWith(suffix, StringComparison.Ordinal))
                    map[kv.Value] = kv.Key;
            return map;
        }

        private static string TrOrNull(string key) {
            if (active.TryGetValue(key, out var t) && t.Length > 0) return t;
            if (english.TryGetValue(key, out var e) && e.Length > 0) return e;
            return null;
        }

        private static void ApplyRoles() {
            var nameToKey = EnToKey("uc.role.", ".name");
            foreach (var ri in RoleInfo.roleInfoById.Values) {
                if (ri == null) continue;
                if (!roleOriginals.TryGetValue(ri, out var orig)) {
                    // only capture/translate UC's own roles: match by pristine English name
                    if (!nameToKey.ContainsKey(ri.name)) continue;
                    orig = (ri.name, ri.introDescription, ri.shortDescription);
                    roleOriginals[ri] = orig;
                }
                if (!nameToKey.TryGetValue(orig.name, out var nameKey)) continue;
                var baseKey = nameKey.Substring(0, nameKey.Length - ".name".Length);
                ri.name = TrOrNull(nameKey) ?? orig.name;
                var desc = TrOrNull(baseKey + ".desc");
                var intro = TrOrNull(baseKey + ".intro") ?? desc;
                ri.introDescription = intro ?? orig.intro;
                ri.shortDescription = desc ?? orig.shortDesc;
            }
        }

        private static void ApplyOptions() {
            var optToKey = EnToKey("uc.option.", "");
            var valueToKey = EnToKey("uc.", ""); // per-element selection match (any uc.* text)
            foreach (var opt in CustomOption.options.ToArray()) {
                if (opt == null || opt.id < 1400 || opt.id > 1699) continue;
                if (!optionOriginals.TryGetValue(opt, out var orig)) {
                    orig = (opt.name, opt.selections);
                    optionOriginals[opt] = orig;
                }
                bool child = orig.name.StartsWith("- ", StringComparison.Ordinal);
                string raw = child ? orig.name.Substring(2) : orig.name;
                if (optToKey.TryGetValue(raw, out var key)) {
                    var tr = TrOrNull(key);
                    if (tr != null) opt.name = child ? "- " + tr : tr;
                } else {
                    opt.name = orig.name;
                }
                opt.selections = TranslateSelections(orig.selections, valueToKey);
            }
        }

        private static object[] TranslateSelections(object[] originals, Dictionary<string, string> valueToKey) {
            if (originals == null || originals.Length == 0) return originals;
            if (!originals.All(o => o is string)) return originals; // float lists: hands off
            var arr = new object[originals.Length];
            bool any = false;
            for (int i = 0; i < originals.Length; i++) {
                var s = (string)originals[i];
                if (valueToKey.TryGetValue(s, out var key) && TrOrNull(key) is string tr) {
                    arr[i] = tr; any = true;
                } else arr[i] = s;
            }
            return any ? arr : originals;
        }

        // ---------- change detection ----------

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class PollPatch {
            public static void Postfix() {
                if (UnityEngine.Time.unscaledTime < nextPoll) return;
                nextPoll = UnityEngine.Time.unscaledTime + 0.5f;
                CheckForChange();
            }
        }

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class MenuPatch {
            public static void Postfix() => CheckForChange();
        }

        private static void CheckForChange() {
            try {
                int epoch = AppDomain.CurrentDomain.GetData(EpochKey) is int i ? i : -1;
                var code = ResolveCode();
                if (epoch == lastEpoch && code == ActiveCode) return;
                string reason = epoch != lastEpoch ? "epoch change" : "language change";
                lastEpoch = epoch;
                Apply(reason);
            } catch { }
        }

        // ---------- table loading (same minimal flat-map JSON as UTSLocalization) ----------

        private static void LoadTable(string code, Dictionary<string, string> into) {
            into.Clear();
            try {
                var asm = Assembly.GetExecutingAssembly();
                using var s = asm.GetManifestResourceStream(
                    $"UnknownsCollection.Resources.Localization.{code}.json");
                if (s != null) {
                    using var r = new StreamReader(s, Encoding.UTF8);
                    ParseFlatJson(r.ReadToEnd(), into);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCLoc] embedded table {code} failed: {e.Message}");
            }
            try {
                var path = Path.Combine(Paths.ConfigPath, "UTSLocalization", code + ".json");
                if (File.Exists(path)) {
                    var all = new Dictionary<string, string>();
                    ParseFlatJson(File.ReadAllText(path, Encoding.UTF8), all);
                    foreach (var kv in all)
                        if (kv.Key.StartsWith("uc.", StringComparison.Ordinal)) into[kv.Key] = kv.Value;
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCLoc] override table {code} failed: {e.Message}");
            }
        }

        private static void ParseFlatJson(string json, Dictionary<string, string> into) {
            int i = 0, n = json.Length;
            void SkipWs() { while (i < n && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++; }
            string ParseString() {
                var sb = new StringBuilder();
                i++;
                while (i < n) {
                    char c = json[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= n) break;
                    char e = json[i++];
                    switch (e) {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= n && ushort.TryParse(json.Substring(i, 4),
                                    System.Globalization.NumberStyles.HexNumber, null, out var cp)) {
                                sb.Append((char)cp);
                                i += 4;
                            }
                            break;
                    }
                }
                return sb.ToString();
            }
            if (n > 0 && json[0] == '﻿') i = 1;
            SkipWs();
            if (i >= n || json[i] != '{') return;
            i++;
            while (true) {
                SkipWs();
                if (i >= n) return;
                if (json[i] == '}') return;
                if (json[i] == ',') { i++; continue; }
                if (json[i] != '"') return;
                var key = ParseString();
                SkipWs();
                if (i >= n || json[i] != ':') return;
                i++;
                SkipWs();
                if (i < n && json[i] == '"') {
                    into[key] = ParseString();
                } else {
                    int depth = 0;
                    while (i < n) {
                        char c = json[i];
                        if (c == '{' || c == '[') depth++;
                        else if (c == '}' || c == ']') { if (depth == 0) break; depth--; }
                        else if (c == ',' && depth == 0) break;
                        i++;
                    }
                }
            }
        }
    }
}
