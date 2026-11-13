// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UCOnlineHats - hats without a mod update, live (2026-11-05; the same scheme as the online costumes of Unknown's Halloween).
 *
 * The list extra/hats.json lives on the main branch of DaUnknown-0/UnknownsCollection-Assets (not at the catalog tag of the
 * DLL), so a new hat needs nothing but a push there:
 *   { "format": 1, "hats": [ { "key": "sweater", "name": "Ugly Sweater", "author": "DaUnknown-0",
 *       "files": { "main": { "file": "extra_sweater_main_1a2b3c4d.png", "size": 12345, "sha256": "..." },
 *                  "back": {...}, "climb": {...}, "flip": {...}, "backflip": {...} },
 *       "behind": false, "bounce": false, "adaptive": true, "minVersion": "1.4.1" } ] }
 * Only "main" is required. tools/build_online_hat.py writes the files and the list.
 *
 * Every file is checked (size, SHA-256) and kept in BepInEx/uc-assets/extra; UCAssetStore only cleans its top folder, so these
 * stay. A hat goes into TOR's hat folder as "UCX_<file>" and is registered exactly like UCHats does it (UnregisteredHats, by
 * reflection); hats that arrive after TOR's first hat pass get a second pass (HatManagerPatches.isLoaded back to false).
 * A changed hat (same key, other files) gets its pictures replaced in TOR's view-data cache on the spot, so whoever wears it
 * sees the new look at once. A hat that is no longer listed stays until the next start (TOR cannot unregister a hat).
 *
 * When: at start from the last list on disk (works offline), then the online list; again on every lobby join (at most once a
 * minute) and with "/loadhats". The command is for the mod's author only and also asks everyone in the lobby to reload
 * (UC module byte 245, every receiver checks the sender again). Nothing here is gameplay: no options, no host authority.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using Hazel;
using TheOtherRoles.Modules.CustomHats;
using UnityEngine;
using UnityEngine.Networking;

namespace UnknownsCollection {
    internal static class UCOnlineHats {
        private const string LogPrefix = "[UC/OnlineHats]";
        private const string ManifestName = "hats.json";
        internal const byte RpcModule = UnknownsCollectionPlugin.OnlineHatsRpcId;
        private const string DiskPrefix = "UCX_";
        private const string Package = "Unknown's Collection";
        private const int MaxHats = 128;
        private const long MaxFile = 8L * 1024 * 1024;
        internal const string Command = "/loadhats";

        // Same hashes as UTS's FeedbackOwner / Halloween's owner check (SHA-256 of PUID and friend code, no plain id in the source).
        private static readonly string[] OwnerHashes = {
            "d10f0e308524211c45d93ec3300efca28e35488ea35598196c67b1ababb7fc19",
            "cf2eab42fba217e7af4237f7a3bcd35402f2790602e07d92c6344096701480c6",
        };

        private static readonly string[] Roles = { "main", "back", "climb", "flip", "backflip" };

        private sealed class FileRef { public string File, Sha256; public long Size; }

        private sealed class Def {
            public string Key, Name, Author;
            public readonly Dictionary<string, FileRef> Files = new();
            public bool Behind, Bounce, Adaptive;
            public string Signature => string.Join("|", Roles.Select(r => Files.TryGetValue(r, out var f) ? f.Sha256 : "-"));
        }

        private static readonly Regex KeyRx = new("^[a-z][a-z0-9]{1,23}$");
        private static readonly Regex FileRx = new("^extra_[a-z0-9_]{1,60}\\.png$");
        private static readonly Regex ShaRx = new("^[0-9a-f]{64}$");

        // registered hats by key: name and file signature (to notice changed pictures)
        private static readonly Dictionary<string, (string Name, string Signature)> Registered = new();
        // every file we put into TOR's hat folder (UCHats' download guard strips these too)
        internal static readonly HashSet<string> DiskFiles = new(StringComparer.OrdinalIgnoreCase);

        private static MonoBehaviour _host;
        private static bool _loading;
        private static float _lastLoad = -999f;
        private static string _applied;
        private static string _hatsDir;

        internal static string Folder => Path.Combine(UCAssetStore.Folder, "extra");

        private static string SourceBase {
            get {
                string s = UCAssetStore.Source;
                if (s.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
                    return (s.EndsWith("files/", StringComparison.Ordinal) ? s.Substring(0, s.Length - "files/".Length) : s) + "extra/";
                return UCAssetStore.DefaultSourceBase + "main/extra/";
            }
        }

        private static void Log(string s) => UnknownsCollectionPlugin.Logger?.LogInfo($"{LogPrefix} {s}");
        private static void Warn(string s) => UnknownsCollectionPlugin.Logger?.LogWarning($"{LogPrefix} {s}");

        /// <summary>From the plugin Load, after UCHats.TryPatch: the last list on disk at once, then the online one.</summary>
        internal static void Start(MonoBehaviour host, string hatsDir) {
            _host = host;
            _hatsDir = hatsDir;
            if (_hatsDir == null) { Warn("TOR's hat folder unknown, online hats skipped"); return; }
            try { Directory.CreateDirectory(Folder); } catch (Exception e) { Warn($"folder: {e.Message}"); }
            UCRpc.Register(RpcModule, HandleRpc);
            try {
                string cache = Path.Combine(Folder, ManifestName);
                if (File.Exists(cache)) Apply(File.ReadAllText(cache, Encoding.UTF8), false);
            } catch (Exception e) { Warn($"cached list: {e.Message}"); }
            _loading = true;
            _lastLoad = Time.realtimeSinceStartup;
            BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(host, Load(true));
        }

        /// <summary>Check the online list again (lobby join at most once a minute, /loadhats at most every 5 s).</summary>
        internal static void Refresh(bool forced, string why) {
            if (_host == null || _loading) return;
            float now = Time.realtimeSinceStartup;
            if (now - _lastLoad < (forced ? 5f : 60f)) return;
            _loading = true;
            _lastLoad = now;
            Log($"checking the online hat list again ({why})");
            BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(_host, Load(false));
        }

        private static IEnumerator Load(bool startup) {
            string url = SourceBase + ManifestName + "?t=" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            var www = Get(url);
            var op = www.SendWebRequest();
            while (!op.isDone) yield return null;
            string text = null;
            long code = www.responseCode;
            if (!www.isNetworkError && !www.isHttpError) text = www.downloadHandler.text;
            else if (code == 404) Log("no online hat list in the asset repository (404)");
            else Warn($"online hat list not loaded ({(code > 0 ? "HTTP " + code : www.error)}), keeping the last one");
            www.downloadHandler.Dispose();
            www.Dispose();
            _loading = false;
            if (text == null || text == _applied) yield break;

            if (!TryParse(text, out var defs)) yield break;
            // the files first, then registration (a hat appears only when all its pictures are confirmed)
            foreach (var d in defs)
                foreach (var f in d.Files.Values) {
                    if (Confirmed(f)) continue;
                    var dl = Get(SourceBase + f.File);
                    var dop = dl.SendWebRequest();
                    while (!dop.isDone) yield return null;
                    if (!dl.isNetworkError && !dl.isHttpError) Store(f, dl.downloadHandler.data);
                    else Warn($"{f.File}: download failed ({(dl.responseCode > 0 ? "HTTP " + dl.responseCode : dl.error)})");
                    dl.downloadHandler.Dispose();
                    dl.Dispose();
                }
            try {
                File.WriteAllText(Path.Combine(Folder, ManifestName + ".part"), text, Encoding.UTF8);
                File.Move(Path.Combine(Folder, ManifestName + ".part"), Path.Combine(Folder, ManifestName), true);
            } catch { }
            Apply(text, startup);
        }

        private static UnityWebRequest Get(string url) {
            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl(url);
            www.SetRequestHeader("User-Agent", $"UnknownsCollection/{UnknownsCollectionPlugin.Version}");
            www.downloadHandler = new DownloadHandlerBuffer();
            www.timeout = 20;
            return www;
        }

        // ---------------------------------------------------------------- files

        private static string PathOf(FileRef f) => Path.Combine(Folder, f.File);

        private static bool Confirmed(FileRef f) {
            try {
                var fi = new FileInfo(PathOf(f));
                if (!fi.Exists || fi.Length != f.Size) return false;
                return Sha(File.ReadAllBytes(fi.FullName)) == f.Sha256;
            } catch { return false; }
        }

        private static void Store(FileRef f, byte[] data) {
            if (data == null || data.LongLength != f.Size || Sha(data) != f.Sha256) { Warn($"{f.File}: wrong size or checksum, dropped"); return; }
            try {
                string p = PathOf(f);
                File.WriteAllBytes(p + ".part", data);
                File.Move(p + ".part", p, true);
            } catch (Exception e) { Warn($"{f.File}: {e.Message}"); }
        }

        private static string Sha(byte[] data) {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        // ---------------------------------------------------------------- list -> TOR

        private static void Apply(string text, bool startup) {
            if (!TryParse(text, out var defs)) return;
            _applied = text;
            var fresh = new List<CustomHat>();
            foreach (var d in defs) {
                try {
                    if (!d.Files.Values.All(Confirmed)) { Log($"{d.Key}: pictures not here yet"); continue; }
                    foreach (var f in d.Files.Values) CopyToTor(f);
                    if (Registered.TryGetValue(d.Key, out var known)) {
                        if (known.Signature != d.Signature) { Replace(d, known.Name); Registered[d.Key] = (known.Name, d.Signature); }
                        if (known.Name != d.Name) Log($"{d.Key} renamed to '{d.Name}': takes effect after a restart");
                        continue;
                    }
                    var hat = new CustomHat {
                        Name = d.Name, Author = d.Author ?? "DaUnknown-0", Package = Package,
                        Resource = DiskName(d, "main"), BackResource = DiskName(d, "back"), ClimbResource = DiskName(d, "climb"),
                        FlipResource = DiskName(d, "flip"), BackFlipResource = DiskName(d, "backflip"),
                        Adaptive = d.Adaptive, Bounce = d.Bounce, Behind = d.Behind || d.Files.ContainsKey("back"),
                    };
                    fresh.Add(hat);
                    Registered[d.Key] = (d.Name, d.Signature);
                } catch (Exception e) { Warn($"{d.Key}: {e.Message}"); }
            }
            if (startup) CleanUp(defs);
            if (fresh.Count == 0) return;
            if (Register(fresh)) {
                KickTorHatLoader();
                Log($"in the hat shop: {string.Join(", ", fresh.Select(h => h.Name))}");
            }
        }

        private static string DiskName(Def d, string role) => d.Files.TryGetValue(role, out var f) ? DiskPrefix + f.File : null;

        private static void CopyToTor(FileRef f) {
            string target = Path.Combine(_hatsDir, DiskPrefix + f.File);
            DiskFiles.Add(DiskPrefix + f.File);
            if (File.Exists(target) && new FileInfo(target).Length == f.Size) return;
            Directory.CreateDirectory(_hatsDir);
            File.Copy(PathOf(f), target, true);
        }

        private static bool Register(List<CustomHat> hats) {
            try {
                var field = typeof(CustomHatManager).GetField("UnregisteredHats", BindingFlags.NonPublic | BindingFlags.Static);
                if (field?.GetValue(null) is not List<CustomHat> list) { Warn("CustomHatManager.UnregisteredHats not found"); return false; }
                var cache = ViewDataCache();
                foreach (var hat in hats.ToList()) {
                    if (list.Any(h => h?.Name == hat.Name) || (cache != null && cache.Contains(hat.Name))) {
                        Warn($"a hat named '{hat.Name}' already exists, the online one is skipped");
                        hats.Remove(hat);
                        continue;
                    }
                    list.Add(hat);
                }
                return hats.Count > 0;
            } catch (Exception e) {
                Warn($"registration failed: {e.Message}");
                return false;
            }
        }

        // TOR drains UnregisteredHats on GetHatById until the list is empty once (HatManagerPatches.isLoaded), then never
        // again; hats that arrive later need that pass once more.
        private static void KickTorHatLoader() {
            try {
                var t = typeof(CustomHatManager).Assembly.GetType("TheOtherRoles.Modules.CustomHats.Patches.HatManagerPatches");
                var f = t?.GetField("isLoaded", BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null && f.GetValue(null) is bool loaded && loaded) f.SetValue(null, false);
            } catch (Exception e) { Warn($"late hat registration: {e.Message}"); }
        }

        private static IDictionary ViewDataCache() =>
            typeof(CustomHatManager).GetField("ViewDataCache", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary;

        /// <summary>Same hat, new pictures: swap the sprites in TOR's caches (same pivot and scale as CreateHatSprite).</summary>
        private static void Replace(Def d, string name) {
            try {
                var views = ViewDataCache();
                var exts = typeof(CustomHatManager).GetField("ExtensionCache", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary;
                if (views == null || !views.Contains(name)) { Log($"{d.Key}: not built by TOR yet, the new pictures load with it"); return; }
                var view = views[name] as HatViewData;
                var main = SpriteOf(d, "main");
                if (view != null && main != null) {
                    view.MainImage = main;
                    view.FloorImage = main;
                    var back = SpriteOf(d, "back");
                    if (back != null) view.BackImage = back;
                    var climb = SpriteOf(d, "climb");
                    if (climb != null) { view.ClimbImage = climb; view.LeftClimbImage = climb; }
                }
                if (exts != null && exts.Contains(name) && exts[name] is HatExtension ext) {
                    var flip = SpriteOf(d, "flip");
                    if (flip != null) ext.FlipImage = flip;
                    var backFlip = SpriteOf(d, "backflip");
                    if (backFlip != null) ext.BackFlipImage = backFlip;
                }
                Log($"{d.Key}: pictures updated live");
            } catch (Exception e) { Warn($"{d.Key}: live update failed: {e.Message}"); }
        }

        private static Sprite SpriteOf(Def d, string role) {
            if (!d.Files.TryGetValue(role, out var f)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(PathOf(f)), false)) return null;
            var s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.53f, 0.575f), tex.width * 0.375f);
            tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            s.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            return s;
        }

        // Files no longer listed, and half downloads, from our own folder (only at start: later a replaced file may be in use).
        private static void CleanUp(List<Def> defs) {
            try {
                var keep = new HashSet<string>(defs.SelectMany(d => d.Files.Values.Select(f => f.File)), StringComparer.OrdinalIgnoreCase) { ManifestName };
                foreach (var p in Directory.GetFiles(Folder)) {
                    string n = Path.GetFileName(p);
                    if (keep.Contains(n)) continue;
                    try { File.Delete(p); Log($"removed {n} (no longer listed)"); } catch { }
                }
            } catch { }
        }

        // ---------------------------------------------------------------- parsing

        private static bool TryParse(string json, out List<Def> defs) {
            defs = new List<Def>();
            try {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                int format = root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.Number ? fmt.GetInt32() : 0;
                if (format != 1) { Warn($"list format {format} is not supported by this version"); return false; }
                if (!root.TryGetProperty("hats", out var arr) || arr.ValueKind != JsonValueKind.Array) return true;
                foreach (var e in arr.EnumerateArray()) {
                    if (defs.Count >= MaxHats) { Warn($"more than {MaxHats} online hats, the rest is skipped"); break; }
                    try {
                        var d = Read(e);
                        if (d == null) continue;
                        if (defs.Any(x => x.Key == d.Key || x.Name == d.Name)) { Warn($"{d.Key}: key or name listed twice, skipped"); continue; }
                        defs.Add(d);
                    } catch (Exception ex) { Warn($"entry skipped: {ex.Message}"); }
                }
                return true;
            } catch (Exception e) {
                Warn($"online hat list unreadable: {e.Message}");
                return false;
            }
        }

        private static Def Read(JsonElement e) {
            string Str(JsonElement o, string p) => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool Bool(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
            var d = new Def { Key = Str(e, "key"), Name = Str(e, "name"), Author = Str(e, "author") };
            if (d.Key == null || !KeyRx.IsMatch(d.Key)) throw new FormatException($"bad key '{d.Key}'");
            string min = Str(e, "minVersion");
            if (min != null && (!Version.TryParse(min, out var need) || UnknownsCollectionPlugin.Version < need)) {
                Log($"{d.Key} needs Unknown's Collection {min} or newer, skipped");
                return null;
            }
            if (d.Name == null || d.Name.Length < 3 || d.Name.Length > 40 || d.Name.Any(char.IsControl) || d.Name.Contains('<'))
                throw new FormatException($"{d.Key}: bad name");
            if (d.Author != null && (d.Author.Length > 40 || d.Author.Contains('<'))) d.Author = null;
            if (!e.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) throw new FormatException($"{d.Key}: no files");
            foreach (var role in Roles) {
                if (!files.TryGetProperty(role, out var fe) || fe.ValueKind != JsonValueKind.Object) continue;
                var f = new FileRef { File = Str(fe, "file"), Sha256 = Str(fe, "sha256")?.ToLowerInvariant() };
                if (f.File == null || !FileRx.IsMatch(f.File)) throw new FormatException($"{d.Key}: bad file name for {role}");
                if (f.Sha256 == null || !ShaRx.IsMatch(f.Sha256)) throw new FormatException($"{d.Key}: bad sha256 for {role}");
                if (!fe.TryGetProperty("size", out var sz) || !sz.TryGetInt64(out f.Size) || f.Size < 8 || f.Size > MaxFile)
                    throw new FormatException($"{d.Key}: bad size for {role}");
                d.Files[role] = f;
            }
            if (!d.Files.ContainsKey("main")) throw new FormatException($"{d.Key}: no main picture");
            d.Behind = Bool("behind");
            d.Bounce = Bool("bounce");
            d.Adaptive = Bool("adaptive");
            return d;
        }

        // ---------------------------------------------------------------- triggers

        private static bool IsOwner(PlayerControl p) {
            try {
                var data = p?.Data;
                if (data == null) return false;
                foreach (var id in new[] { data.Puid, data.FriendCode }) {
                    if (string.IsNullOrEmpty(id)) continue;
                    using var sha = SHA256.Create();
                    string h = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
                    if (OwnerHashes.Contains(h)) return true;
                }
            } catch { }
            return false;
        }

        // the author asked everybody to reload: only accepted from the author's client
        private static void HandleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                if (sub != 0 || !IsOwner(UCRpc.Sender)) return;
                _lastLoad = -999f;
                Refresh(true, "the author reloaded the hats");
            } catch { }
        }

        // Autotest (UC_DIAG_ONLINEHATS=1): check the list again after 25 s, so a test server can swap a hat in between
        // (live update without a restart); logs what TOR has in its caches.
        // UC_DIAG_ONLINEHATS=wear (2026-11-08): freeplay on the Skeld, every online hat put on the local player in turn and
        // photographed standing (UCShots/onlinehats/<key>.png), to check how an outfit sits on the real crewmate.
        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class DiagPatch {
            private static bool fired;
            private static void Postfix(MainMenuManager __instance) {
                string mode = Environment.GetEnvironmentVariable("UC_DIAG_ONLINEHATS");
                if (fired || (mode != "1" && mode != "wear")) return;
                fired = true;
                // "wear" leaves the main menu for freeplay: run on the plugin's own host, the menu object dies with the scene
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(mode == "wear" && _host != null ? _host : __instance,
                    mode == "wear" ? DiagWear() : Diag());
            }
            private static IEnumerator DiagWear() {
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 25f;) yield return null;
                try { DestroyableSingleton<HatManager>.Instance?.GetHatById("hat_none"); } catch { }
                try {
                    var popover = UnityEngine.Object.FindObjectOfType<FreeplayPopover>(true);
                    for (var t = popover.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                    popover.Show();
                    popover.PlayMap(MapNames.Skeld);
                    Log("diag: freeplay started");
                } catch (Exception e) { Log($"diag: freeplay failed: {e.Message}"); yield break; }
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 60f && PlayerControl.LocalPlayer == null;) yield return null;
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 6f;) yield return null;
                string dir = Path.Combine(BepInEx.Paths.GameRootPath, "UCShots", "onlinehats");
                Directory.CreateDirectory(dir);
                // pet, visor and skin off (a pet stands in front of the visor), then the bare crewmate as reference picture
                try {
                    var lp = PlayerControl.LocalPlayer;
                    lp.RpcSetPet("pet_EmptyPet");
                    lp.RpcSetVisor("visor_EmptyVisor");
                    lp.RpcSetSkin("skin_None");
                    lp.RpcSetHat("hat_NoHat");
                } catch (Exception e) { Log($"diag: unequip failed: {e.Message}"); }
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 2f;) yield return null;
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "bare.png"));
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 1f;) yield return null;
                foreach (var r in Registered.ToList()) {
                    string id = "hat_" + r.Value.Name.Replace(' ', '_');
                    try { PlayerControl.LocalPlayer.SetHat(id, PlayerControl.LocalPlayer.Data.DefaultOutfit.ColorId); }
                    catch (Exception e) { Log($"diag: {r.Key} not worn: {e.Message}"); continue; }
                    for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 2f;) yield return null;
                    ScreenCapture.CaptureScreenshot(Path.Combine(dir, r.Key + ".png"));
                    Log($"diag: wearing {r.Key} ({id})");
                    for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 1f;) yield return null;
                }
                Log("diag: all done");
            }
            private static IEnumerator Diag() {
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 25f;) yield return null;
                try { DestroyableSingleton<HatManager>.Instance?.GetHatById("hat_none"); } catch { }
                var views = ViewDataCache();
                Log($"diag: registered {string.Join(", ", Registered.Select(r => r.Key + "=" + r.Value.Name))}; TOR view data {(views == null ? -1 : views.Count)}");
                _lastLoad = -999f;
                Refresh(true, "diag");
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 10f;) yield return null;
                Log("diag: all done");
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class JoinedPatch {
            private static void Postfix() { try { Refresh(false, "lobby joined"); } catch { } }
        }

        [HarmonyPatch(typeof(ChatController), nameof(ChatController.SendChat))]
        private static class CommandPatch {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(ChatController __instance) {
                try {
                    if (__instance == null || __instance.freeChatField == null) return true;
                    string text = __instance.freeChatField.Text;
                    if (string.IsNullOrEmpty(text) || text.Trim().ToLowerInvariant() != Command) return true;
                    __instance.freeChatField.Clear();
                    __instance.quickChatMenu?.Clear();
                    var lp = PlayerControl.LocalPlayer;
                    if (!IsOwner(lp)) { __instance.AddChat(lp, "Unknown's Collection: this command is only for the mod author."); return false; }
                    _lastLoad = -999f;
                    Refresh(true, Command);
                    bool sent = false;
                    try {
                        if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmConnected && lp != null) {
                            var w = UCRpc.Begin(RpcModule);
                            w.Write((byte)0);
                            AmongUsClient.Instance.FinishRpcImmediately(w);
                            sent = true;
                        }
                    } catch { }
                    __instance.AddChat(lp, sent ? "Unknown's Collection: the hat list is being reloaded for everyone in the lobby."
                                                : "Unknown's Collection: the hat list is being reloaded.");
                    return false;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"{LogPrefix} {Command}: {e}");
                    return true;
                }
            }
        }
    }
}
