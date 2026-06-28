// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Self-updater for Unknown's Collection. Mirrors the other DaUnknown mods' updaters (own GitHub
 * release DTOs, no compile-time TOR reference needed). CHANNEL-AWARE: the update target follows the
 * shared "show test versions" toggle - when test versions are ON the newest release of ANY channel is
 * offered (so prereleases / vX.Y.Z.W test builds update), when OFF only the newest stable (vX.Y.Z).
 * Also exposes the LatestInChannel/HasChannelRelease/TriggerChannelSwitch API used by the Mod Manager
 * toggle to force a stable/test reinstall.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using AmongUs.Data;
using Assets.InnerNet;
using Twitch;

namespace UnknownsCollection {
    public class UnknownsCollectionUpdater : MonoBehaviour {
        public const string RepositoryOwner = "DaUnknown-0";
        public const string RepositoryName = "UnknownsCollection";
        public const string PluginAssetName = "UnknownsCollection.dll";

        public static UnknownsCollectionUpdater Instance { get; private set; }

        public UnknownsCollectionUpdater(IntPtr ptr) : base(ptr) { }

        private bool _busy;
        private bool _showPopUp = true;
        public List<GithubRelease> Releases;

        private int _updateState;     // 0 idle, 1 downloading, 2 success (restart), 3 error
        private float _updateProgress;
        private bool _checkCompleted;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
            foreach (var file in Directory.GetFiles(Paths.PluginPath, PluginAssetName + ".old"))
                File.Delete(file);
        }

        private void Start() {
            if (_busy) return;
            this.StartCoroutine(CoCheckForUpdate());
            SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded);
        }

        [HideFromIl2Cpp]
        public void StartDownloadRelease(GithubRelease release, bool managerMode = false) {
            if (_busy) return;
            this.StartCoroutine(CoDownloadRelease(release, managerMode));
        }

        [HideFromIl2Cpp]
        public void TriggerCheckFromManager() {
            if (_busy) return;
            _checkCompleted = false;
            this.StartCoroutine(CoCheckForUpdate());
        }

        [HideFromIl2Cpp] public int GetUpdateState() => _updateState;
        [HideFromIl2Cpp] public float GetUpdateProgress() => _updateProgress;
        [HideFromIl2Cpp] public bool GetCheckCompleted() => _checkCompleted;
        // True when the release list was successfully fetched (Mod Manager shows "check unavailable"
        // instead of a misleading "up to date" when the GitHub call failed/rate-limited).
        [HideFromIl2Cpp] public bool ReleasesLoaded() => Releases != null && Releases.Count > 0;

        [HideFromIl2Cpp]
        private IEnumerator CoCheckForUpdate() {
            _busy = true;
            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases");
            www.SetRequestHeader("User-Agent", $"UnknownsCollection/{UnknownsCollectionPlugin.PluginVersion}");
            www.downloadHandler = new DownloadHandlerBuffer();
            var operation = www.SendWebRequest();
            while (!operation.isDone) yield return new WaitForEndOfFrame();

            if (www.isNetworkError || www.isHttpError) {
                www.downloadHandler.Dispose(); www.Dispose();
                _checkCompleted = true; _busy = false; yield break;
            }
            try {
                Releases = JsonSerializer.Deserialize<List<GithubRelease>>(www.downloadHandler.text);
                if (Releases != null) Releases.Sort(SortReleases);
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"Update check: failed to parse GitHub releases ({ex.Message}). Treating as 'no update'.");
            } finally {
                www.downloadHandler.Dispose(); www.Dispose();
                _checkCompleted = true; _busy = false;
            }
        }

        [HideFromIl2Cpp]
        private IEnumerator CoDownloadRelease(GithubRelease release, bool managerMode) {
            _busy = true; _updateState = 1; _updateProgress = 0f;

            GenericPopup popup = null;
            GameObject button = null;
            if (!managerMode) {
                popup = Instantiate(TwitchManager.Instance.TwitchPopup);
                popup.TextAreaTMP.fontSize *= 0.7f;
                popup.TextAreaTMP.enableAutoSizing = false;
                popup.Show();
                button = popup.transform.GetChild(2).gameObject;
                button.SetActive(false);
                popup.TextAreaTMP.text = "Updating Unknown's Collection\nPlease wait...";
            }

            var asset = release.Assets.Find(FilterPluginAsset);
            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl(asset.DownloadUrl);
            www.downloadHandler = new DownloadHandlerBuffer();
            var operation = www.SendWebRequest();
            while (!operation.isDone) {
                _updateProgress = www.downloadProgress;
                if (!managerMode) {
                    int stars = Mathf.CeilToInt(www.downloadProgress * 10);
                    popup.TextAreaTMP.text = $"Updating Unknown's Collection\nPlease wait...\nDownloading...\n{new String((char)0x25A0, stars) + new String((char)0x25A1, 10 - stars)}";
                }
                yield return new WaitForEndOfFrame();
            }

            if (www.isNetworkError || www.isHttpError) {
                _updateState = 3;
                if (!managerMode) { popup.TextAreaTMP.text = "Update wasn't successful\nTry again later,\nor update manually."; button.SetActive(true); }
                _busy = false; yield break;
            }
            if (!managerMode) popup.TextAreaTMP.text = "Updating Unknown's Collection\nPlease wait...\n\nDownload complete\ncopying file...";

            var filePath = Path.Combine(Paths.PluginPath, asset.Name);
            if (File.Exists(filePath + ".old")) File.Delete(filePath + ".old");
            if (File.Exists(filePath)) File.Move(filePath, filePath + ".old");

            var persistTask = File.WriteAllBytesAsync(filePath, www.downloadHandler.data);
            var hasError = false;
            while (!persistTask.IsCompleted) {
                if (persistTask.Exception != null) { hasError = true; break; }
                yield return new WaitForEndOfFrame();
            }
            www.downloadHandler.Dispose(); www.Dispose();

            if (!hasError) {
                _updateState = 2;
                if (!managerMode) popup.TextAreaTMP.text = "Unknown's Collection\nupdated successfully\nPlease restart the game.";
            } else _updateState = 3;
            if (!managerMode) button.SetActive(true);
            _busy = false;
        }

        [HideFromIl2Cpp]
        private static bool FilterPluginAsset(GithubAsset asset) => asset.Name == PluginAssetName;

        [HideFromIl2Cpp]
        private static int SortReleases(GithubRelease a, GithubRelease b) {
            if (a.IsNewer(b.Version)) return -1;
            if (b.IsNewer(a.Version)) return 1;
            return 0;
        }

        // ---- Channel awareness ----
        // Semantic version comparison where a STABLE vX.Y.Z SUPERSEDES its prereleases vX.Y.Z.W
        // (unlike System.Version, which wrongly orders 1.0.0.4 > 1.0.0). Returns >0 if a is newer than b.
        // Rule: compare the X.Y.Z base first; on a tie, the finalized stable (no 4th part) beats any
        // prerelease, and among prereleases the higher 4th part wins.
        [HideFromIl2Cpp]
        public static int SemCompare(Version a, Version b) {
            int c = new Version(a.Major, System.Math.Max(0, a.Minor), System.Math.Max(0, a.Build)).CompareTo(new Version(b.Major, System.Math.Max(0, b.Minor), System.Math.Max(0, b.Build)));
            if (c != 0) return c;
            bool aPre = a.Revision > 0, bPre = b.Revision > 0;
            if (aPre && bPre) return a.Revision.CompareTo(b.Revision);
            if (aPre == bPre) return 0;
            return aPre ? -1 : 1; // prerelease is older than the finalized stable of the same base
        }

        // The update target follows the shared "show test versions" toggle. OFF -> newest STABLE only.
        // ON -> the newest prerelease ONLY if it is semantically AHEAD of the newest stable (i.e. a
        // prerelease of a FUTURE version); an old prerelease (base <= newest stable) is ignored and the
        // stable is used instead. So the latest prerelease downloads only when it leads the latest release.
        [HideFromIl2Cpp]
        public GithubRelease UpdateTarget() {
            if (Releases == null) return null;
            var stable = LatestInChannel(true);
            if (!VersionDisplay.ShowTestVersions()) return stable;
            var pre = LatestInChannel(false);
            if (pre != null && (stable == null || SemCompare(pre.Version, stable.Version) > 0)) return pre;
            return stable;
        }

        // True when `target` is a version the user should actually install (not just "semantically newer").
        // On the test channel, stable vX.Y.Z for a user already on prerelease vX.Y.Z.W is a channel switch,
        // not an update — the base version did not advance. Channel switches go through TriggerChannelSwitch.
        [HideFromIl2Cpp]
        private static bool IsActualUpdate(Version target, Version current) {
            if (SemCompare(target, current) <= 0) return false;
            if (VersionDisplay.ShowTestVersions() && current.Revision > 0 && target.Revision <= 0) {
                var tBase = new Version(target.Major, System.Math.Max(0, target.Minor), System.Math.Max(0, target.Build));
                var cBase = new Version(current.Major, System.Math.Max(0, current.Minor), System.Math.Max(0, current.Build));
                if (tBase.CompareTo(cBase) <= 0) return false;
            }
            return true;
        }

        // stable = vX.Y.Z (Version.Revision <= 0), test = vX.Y.Z.W (Revision > 0).
        [HideFromIl2Cpp]
        public GithubRelease LatestInChannel(bool stable) {
            if (Releases == null) return null;
            foreach (var r in Releases) {
                if (r == null || r.Draft) continue;
                int rev;
                try { rev = r.Version.Revision; } catch { continue; }
                bool isTest = rev > 0;
                if (stable == isTest) continue;
                if (r.Assets != null && r.Assets.Any(FilterPluginAsset)) return r;
            }
            return null;
        }

        [HideFromIl2Cpp]
        public bool HasChannelRelease(bool stable) => LatestInChannel(stable) != null;

        // Only downloads if it is REALLY a different version than the running build (channel switch may
        // be an up- or downgrade; skip if we're already on it).
        [HideFromIl2Cpp]
        public void TriggerChannelSwitch(bool stable) {
            var r = LatestInChannel(stable);
            if (r != null && SemCompare(r.Version, UnknownsCollectionPlugin.Version) != 0)
                StartDownloadRelease(r, managerMode: true);
        }

        // ---- Mod Manager callbacks ----
        [HideFromIl2Cpp]
        public bool HasUpdate() {
            var t = UpdateTarget();
            return t != null && t.Assets.Any(FilterPluginAsset)
                && IsActualUpdate(t.Version, UnknownsCollectionPlugin.Version);
        }

        [HideFromIl2Cpp]
        public void TriggerUpdateFromManager() {
            var t = UpdateTarget();
            if (t != null && t.Assets.Any(FilterPluginAsset)
                && IsActualUpdate(t.Version, UnknownsCollectionPlugin.Version))
                StartDownloadRelease(t, managerMode: true);
        }

        [HideFromIl2Cpp]
        public string GetReleaseNotes() => UpdateTarget()?.Description ?? "";

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            if (_busy || scene.name != "MainMenu" || Releases == null) return;
            if (IsModManagerEnabled()) return; // Mod Manager owns the update UI

            var target = UpdateTarget();
            if (target == null || !target.Assets.Any(FilterPluginAsset)
                || !IsActualUpdate(target.Version, UnknownsCollectionPlugin.Version))
                return;

            var template = GameObject.Find("ExitGameButton");
            if (!template) return;
            var button = Instantiate(template, null);
            button.GetComponent<AspectPosition>().anchorPoint = new Vector2(0.458f, 0.48f);

            PassiveButton passiveButton = button.GetComponent<PassiveButton>();
            passiveButton.OnClick = new Button.ButtonClickedEvent();
            passiveButton.OnClick.AddListener((Action)(() => { StartDownloadRelease(target); button.SetActive(false); }));

            var text = button.transform.GetComponentInChildren<TMPro.TMP_Text>();
            StartCoroutine(Effects.Lerp(0.1f, (Action<float>)(p => text.SetText("Update Unknown's Collection"))));
            passiveButton.OnMouseOut.AddListener((Action)(() => text.color = new Color(0.12f, 0.72f, 1f)));
            passiveButton.OnMouseOver.AddListener((Action)(() => text.color = Color.white));
            text.color = new Color(0.12f, 0.72f, 1f);

            if (_showPopUp) {
                var announcement = $"<size=150%>A new UNKNOWN'S COLLECTION update to {target.Tag} is available</size>\n{target.Description}";
                var mgr = FindObjectOfType<MainMenuManager>(true);
                if (mgr != null)
                    mgr.StartCoroutine(CoShowAnnouncement(announcement, shortTitle: "Unknown's Collection Update", date: target.PublishedAt));
            }
            _showPopUp = false;
        }

        [HideFromIl2Cpp]
        public IEnumerator CoShowAnnouncement(string announcement, bool show = true, string shortTitle = "Unknown's Collection Update", string title = "", string date = "") {
            for (float t = 30f; t > 0f; t -= 0.25f) {
                if (UnityEngine.Object.FindObjectOfType<AnnouncementPopUp>() == null) break;
                yield return new WaitForSeconds(0.25f);
            }
            yield return new WaitForSeconds(0.2f);

            var mgr = FindObjectOfType<MainMenuManager>(true);
            var popUpTemplate = UnityEngine.Object.FindObjectOfType<AnnouncementPopUp>(true);
            if (popUpTemplate == null || mgr == null) yield break;
            var popUp = UnityEngine.Object.Instantiate(popUpTemplate);
            popUp.gameObject.SetActive(true);

            Announcement optimized = new() {
                Id = "unknownsCollectionAnnouncement",
                Language = 0,
                Number = 6974,
                Title = title == "" ? "Unknown's Collection Announcement" : title,
                ShortTitle = shortTitle,
                SubTitle = "",
                PinState = false,
                Date = date == "" ? DateTime.Now.Date.ToString() : date,
                Text = announcement,
            };
            mgr.StartCoroutine(Effects.Lerp(0.1f, new Action<float>((p) => {
                if (p == 1) {
                    var backup = DataManager.Player.Announcements.allAnnouncements;
                    DataManager.Player.Announcements.allAnnouncements = new();
                    popUp.Init(false);
                    DataManager.Player.Announcements.SetAnnouncements(new Announcement[] { optimized });
                    popUp.CreateAnnouncementList();
                    popUp.UpdateAnnouncementText(optimized.Number);
                    popUp.visibleAnnouncements[0].PassiveButton.OnClick.RemoveAllListeners();
                    DataManager.Player.Announcements.allAnnouncements = backup;
                }
            })));
        }

        private static bool IsModManagerEnabled() {
            try { return AppDomain.CurrentDomain.GetData("ModManager.IsEnabled") is bool b && b; }
            catch { return false; }
        }
    }

    // Minimal DTOs matching the GitHub Releases API JSON (local, so no TOR reference is needed).
    public class GithubRelease {
        [JsonPropertyName("tag_name")] public string Tag { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("published_at")] public string PublishedAt { get; set; }
        [JsonPropertyName("body")] public string Description { get; set; }
        [JsonPropertyName("assets")] public List<GithubAsset> Assets { get; set; }

        public Version Version => Version.Parse(Tag.Replace("v", string.Empty));
        public bool IsNewer(Version version) => Version > version;
    }

    public class GithubAsset {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; }
    }
}
