// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UCAssetStore - large pictures and sounds come from the asset repository instead of the DLL
 * (same scheme as Unknown's Atlas' AtlasAssetStore).
 *
 * The files listed in UCAssetCatalog.g.cs (made by tools/build_assets_repo.py: button animation frames,
 * hats, eject scene pictures, music and the longer sound effects) are not embedded any more. They live in
 * BepInEx/uc-assets and are downloaded from DaUnknown-0/UnknownsCollection-Assets at the catalog's tag:
 *   - at EVERY start: all files are checked in the background (size, SHA-256); whatever is missing or
 *     different is downloaded again. Files the catalog does not know (older versions, renamed files,
 *     left-over .part files) are deleted from that folder, never anywhere else.
 *   - while running: a FileSystemWatcher reports changed, deleted or renamed files; those are checked again.
 *     Every read also compares size and time stamp with the last confirmed state.
 *   - a read of a file that the background check has not reached yet hashes that one file on the spot, so
 *     the hats (read during plugin load) find a full cache immediately.
 * Only confirmed files are read. A missing file gives null; callers show their fallback (static icon, no
 * hat this session, plain eject screen, no sound) and ask Waiting() whether it may still arrive.
 * Errors stay quiet: one warning for the first failed download, one summary when the queue is empty.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace UnknownsCollection {
    internal static class UCAssetStore {
        private const string LogPrefix = "[UC/Assets]";
        internal const string DefaultSourceBase = "https://raw.githubusercontent.com/DaUnknown-0/UnknownsCollection-Assets/";
        private const string FolderName = "uc-assets";
        private const int Parallel = 4;
        private const int MaxTries = 4;
        private const int GiveUpStreak = 12;                                   // failures in a row without any success
        private static readonly float[] Backoff = { 0f, 10f, 30f, 60f };       // wait before try 1..4 (s)

        internal static string Folder => Path.Combine(BepInEx.Paths.BepInExRootPath, FolderName);
        internal static string Source { get; private set; } = DefaultSourceBase + UCAssetCatalog.Tag + "/files/";

        private sealed class Entry {
            public string Name, Res, Sha256;
            public long Size;
            public long OkLength, OkTicks;        // confirmed state (size + time stamp when last hashed); 0 = not confirmed
            public bool Checked;                  // hashed at least once this session
            public int Tries;
            public float RetryAt;
        }

        private static readonly Dictionary<string, Entry> ByName = UCAssetCatalog.Files.ToDictionary(
            f => f.Name, f => new Entry { Name = f.Name, Res = f.Res, Size = f.Size, Sha256 = f.Sha256 },
            StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Entry> ByRes = ByName.Values.ToDictionary(e => e.Res, StringComparer.Ordinal);
        private static readonly object Gate = new();
        private static readonly ConcurrentQueue<string> Dirty = new();
        private static readonly List<string> Queue = new();
        private static readonly ConcurrentQueue<string> Arrived = new();
        private static FileSystemWatcher _watcher;
        private static MonoBehaviour _host;
        private static volatile bool _verifying;
        private static int _active, _loadedThisRun, _failStreak;
        private static bool _failWarned, _summaryLogged;

        /// <summary>Main thread: a catalog file was downloaded and confirmed (resource name).</summary>
        internal static event Action<string> FileArrived;

        internal static int Total => ByName.Count;
        internal static int ReadyCount { get { lock (Gate) return ByName.Values.Count(e => e.OkLength > 0); } }

        private static void Log(string s) => UnknownsCollectionPlugin.Logger?.LogInfo($"{LogPrefix} {s}");
        private static void Warn(string s) => UnknownsCollectionPlugin.Logger?.LogWarning($"{LogPrefix} {s}");

        /// <summary>Is this resource (e.g. "UnknownsCollection.Resources.hats.virus_1.png") served by the store?</summary>
        internal static bool Owns(string res) => res != null && ByRes.ContainsKey(res);

        /// <summary>Stream of a resource: catalog files from the asset folder (confirmed only), everything else embedded.</summary>
        internal static Stream Open(string res) {
            if (res == null) return null;
            if (!ByRes.TryGetValue(res, out var e)) return typeof(UCAssetStore).Assembly.GetManifestResourceStream(res);
            if (!Confirmed(e)) return null;
            try { return new FileStream(PathOf(e.Name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch { return null; }
        }

        /// <summary>Embedded, or confirmed in the asset folder?</summary>
        internal static bool Exists(string res) {
            if (res == null) return false;
            if (ByRes.TryGetValue(res, out var e)) return Confirmed(e);
            try { return typeof(UCAssetStore).Assembly.GetManifestResourceInfo(res) != null; } catch { return false; }
        }

        /// <summary>A catalog file that is not there yet but may still arrive this session (callers then wait quietly
        /// instead of giving up on it for good).</summary>
        internal static bool Waiting(string res) {
            if (res == null || !ByRes.TryGetValue(res, out var e) || Confirmed(e)) return false;
            if (_host == null) return false;                                   // store not started: nothing will come
            int tries;
            lock (Gate) tries = e.Tries;
            if (tries < MaxTries || _verifying) return true;
            lock (Queue) return Queue.Contains(e.Name);
        }

        /// <summary>All catalog files whose resource name starts with this prefix confirmed?</summary>
        internal static bool AllReady(string resPrefix) {
            foreach (var e in ByRes.Values)
                if (e.Res.StartsWith(resPrefix, StringComparison.Ordinal) && !Confirmed(e)) return false;
            return true;
        }

        private static string PathOf(string file) => Path.Combine(Folder, file);

        /// <summary>Confirmed = right when last hashed AND neither size nor time stamp changed since. A file the
        /// background check has not reached yet is hashed here, once.</summary>
        private static bool Confirmed(Entry e) {
            bool ok, checkedOnce;
            lock (Gate) { ok = e.OkLength > 0; checkedOnce = e.Checked; }
            if (!ok) {
                if (checkedOnce) return false;
                return Check(e.Name);
            }
            try {
                var fi = new FileInfo(PathOf(e.Name));
                if (fi.Exists && fi.Length == e.OkLength && fi.LastWriteTimeUtc.Ticks == e.OkTicks) return true;
            } catch { }
            lock (Gate) e.OkLength = 0;                                        // changed in place: check and load again
            Dirty.Enqueue(e.Name);
            return false;
        }

        // ------------------------------------------------------------------ start, check, watch

        /// <summary>Plugin start: pick the source, watch the folder, check everything, load what is missing.</summary>
        internal static void Start(MonoBehaviour host, string sourceOverride) {
            _host = host;
            string s = sourceOverride?.Trim();
            if (!string.IsNullOrEmpty(s)) {
                if (!s.EndsWith("/")) s += "/";
                if (s.StartsWith(DefaultSourceBase, StringComparison.Ordinal) || s.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
                    Source = s;
                else Warn($"download source '{s}' ignored (only the asset repository or a local test server)");
            }
            try { Directory.CreateDirectory(Folder); } catch (Exception e) { Warn($"folder: {e.Message}"); }
            Watch();
            VerifyAll();
        }

        private static void Watch() {
            try {
                _watcher = new FileSystemWatcher(Folder) {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                FileSystemEventHandler on = (_, a) => Touched(a.Name);
                _watcher.Changed += on;
                _watcher.Deleted += on;
                _watcher.Created += on;
                _watcher.Renamed += (_, a) => { Touched(a.OldName); Touched(a.Name); };
                _watcher.EnableRaisingEvents = true;
            } catch (Exception e) { Warn($"watching the folder failed ({e.Message}); changes are found on the next read"); }
        }

        private static void Touched(string name) {
            if (string.IsNullOrEmpty(name) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return;
            if (ByName.ContainsKey(name)) Dirty.Enqueue(name);
        }

        private static void VerifyAll() {
            if (_verifying) return;
            _verifying = true;
            var names = ByName.Keys.ToList();
            Task.Run(() => {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Cleanup();
                var bad = new List<string>();
                foreach (var n in names) if (!Check(n)) bad.Add(n);
                return (bad, sw.ElapsedMilliseconds);
            }).ContinueWith(t => {
                if (t.IsFaulted) { _verifying = false; Warn($"check failed: {t.Exception?.GetBaseException().Message}"); return; }
                var (bad, ms) = t.Result;
                if (bad.Count == 0) Log($"start: all {Total} files present and verified ({ms} ms)");
                else {
                    Log($"start: {Total - bad.Count}/{Total} verified, {bad.Count} missing or changed ({ms} ms), loading from {Source}");
                    lock (Queue) foreach (var n in bad) if (!Queue.Contains(n)) Queue.Add(n);
                }
                _verifying = false;
            });
        }

        /// <summary>Background, at start: delete every file in the own folder that the catalog does not list.</summary>
        private static void Cleanup() {
            try {
                var dir = new DirectoryInfo(Folder);
                if (!dir.Exists || !string.Equals(dir.Name, FolderName, StringComparison.OrdinalIgnoreCase)) return;
                int n = 0;
                long bytes = 0;
                foreach (var f in dir.GetFiles()) {
                    if (ByName.ContainsKey(f.Name)) continue;
                    try { long len = f.Length; f.Delete(); n++; bytes += len; }
                    catch (Exception e) { Warn($"cleanup: {f.Name}: {e.Message}"); }
                }
                if (n > 0) Log($"cleanup: removed {n} file(s) not in catalog {UCAssetCatalog.Tag} ({bytes / 1024} KB)");
            } catch (Exception e) { Warn($"cleanup failed: {e.Message}"); }
        }

        /// <summary>One file against the catalog (size, then SHA-256). A wrong file is deleted (the download replaces it).</summary>
        private static bool Check(string name) {
            var e = ByName[name];
            try {
                var fi = new FileInfo(PathOf(name));
                if (!fi.Exists) { lock (Gate) { e.OkLength = 0; e.Checked = true; } return false; }
                string hash = null;
                if (fi.Length == e.Size) {
                    using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sha = SHA256.Create();
                    hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
                }
                if (hash != e.Sha256) {
                    lock (Gate) { e.OkLength = 0; e.Checked = true; }
                    try { fi.Delete(); } catch { }
                    return false;
                }
                fi.Refresh();
                lock (Gate) { e.OkLength = fi.Length; e.OkTicks = fi.LastWriteTimeUtc.Ticks; e.Checked = true; }
                return true;
            } catch {
                lock (Gate) { e.OkLength = 0; e.Checked = true; }
                return false;
            }
        }

        // ------------------------------------------------------------------ tick: changes, downloads

        /// <summary>Main thread (UnknownsCollectionUpdater.Update): re-check changed files, start downloads, report arrivals.</summary>
        internal static void Tick() {
            if (_host == null) return;
            while (Arrived.TryDequeue(out var res)) {
                try { FileArrived?.Invoke(res); } catch (Exception ex) { Warn($"arrival handler ({res}): {ex.Message}"); }
            }
            if (!Dirty.IsEmpty) {
                var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (Dirty.TryDequeue(out var n)) batch.Add(n);
                lock (Queue) batch.RemoveWhere(n => Queue.Contains(n));
                if (batch.Count > 0)
                    Task.Run(() => batch.Where(n => !Check(n)).ToList()).ContinueWith(t => {
                        if (t.IsFaulted) return;
                        lock (Queue) foreach (var n in t.Result) if (!Queue.Contains(n)) Queue.Add(n);
                    });
            }
            lock (Queue) {
                int scanned = 0;
                while (_active < Parallel && Queue.Count > 0 && scanned < Queue.Count) {
                    string n = Queue[0];
                    var e = ByName[n];
                    if (Time.realtimeSinceStartup < e.RetryAt) { Queue.RemoveAt(0); Queue.Add(n); scanned++; continue; }
                    Queue.RemoveAt(0);
                    if (e.Tries >= MaxTries) continue;
                    e.Tries++;
                    _active++;
                    BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(_host, Fetch(e));
                }
            }
        }

        private static IEnumerator Fetch(Entry e) {
            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl(Source + Uri.EscapeDataString(e.Name));
            www.SetRequestHeader("User-Agent", $"UnknownsCollection/{UnknownsCollectionPlugin.PluginVersion}");
            www.downloadHandler = new DownloadHandlerBuffer();
            www.timeout = 60;
            var op = www.SendWebRequest();
            while (!op.isDone) yield return null;
            byte[] data = null;
            string error = null;
            if (www.isNetworkError || www.isHttpError) error = $"HTTP {www.responseCode} {www.error}";
            else data = www.downloadHandler.data;
            www.downloadHandler.Dispose();
            www.Dispose();
            if (error == null) {
                var write = Task.Run(() => Store(e, data));
                while (!write.IsCompleted) yield return null;
                error = write.IsFaulted ? write.Exception?.GetBaseException().Message : write.Result;
            }
            _active--;
            if (error == null) {
                _loadedThisRun++;
                _failStreak = 0;
                Arrived.Enqueue(e.Res);
                if (_active == 0 && ReadyCount == Total) Log($"all {Total} files ready ({_loadedThisRun} downloaded since start)");
            } else {
                _failStreak++;
                if (!_failWarned) {
                    _failWarned = true;
                    Warn($"download failed ({e.Name}: {error}); retrying in the background, further failures are only counted");
                }
                if (_failStreak >= GiveUpStreak && _loadedThisRun == 0) {
                    // source unreachable: stop for this session instead of asking four times per file
                    lock (Queue) {
                        lock (Gate) foreach (var n in Queue) ByName[n].Tries = MaxTries;
                        Queue.Clear();
                    }
                    lock (Gate) e.Tries = MaxTries;
                }
                if (e.Tries < MaxTries) {
                    e.RetryAt = Time.realtimeSinceStartup + Backoff[Math.Min(e.Tries, Backoff.Length - 1)];
                    lock (Queue) Queue.Add(e.Name);
                }
            }
            bool idle;
            lock (Queue) idle = _active == 0 && Queue.Count == 0;
            if (idle && !_verifying && !_summaryLogged) {
                int missing = Total - ReadyCount;
                if (missing > 0) {
                    _summaryLogged = true;
                    Warn($"{missing}/{Total} files could not be downloaded from {Source}; the pictures and sounds using them " +
                         "are left out until the next start");
                }
            }
        }

        /// <summary>Background: size and SHA-256 against the catalog, write next to it, then move into place.</summary>
        private static string Store(Entry e, byte[] data) {
            if (data == null || data.Length != e.Size) return $"size {data?.Length ?? 0} instead of {e.Size}";
            string hash;
            using (var sha = SHA256.Create()) hash = Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
            if (hash != e.Sha256) return "checksum does not match";
            Directory.CreateDirectory(Folder);
            string path = PathOf(e.Name), tmp = path + ".part";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            var fi = new FileInfo(path);
            lock (Gate) { e.OkLength = fi.Length; e.OkTicks = fi.LastWriteTimeUtc.Ticks; e.Checked = true; }
            return null;
        }
    }
}
