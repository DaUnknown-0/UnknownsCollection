// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UCHelp3D - small pre-rendered 3D loops for the demo strip of the "?" help menu (2026-11-08).
 *
 * The loops are rendered in Blender (TOR Mod/Assets/HelpDemos3D, see STYLE.md there) and live in the asset repository
 * (DaUnknown-0/UnknownsCollection-Assets, main/help3d/), not in the DLL. Nothing is downloaded before a player opens a
 * role in the help menu:
 *   - the first opened role fetches help3d/manifest.json (role name -> four files with size + SHA-256), once per session;
 *   - the opened role's four files are downloaded, checked (size, SHA-256) and decoded IN MEMORY ONLY: nothing is written
 *     to disk;
 *   - while that runs, or when there is no server / no entry for the role, the existing 2D demo keeps playing. When the
 *     loop is ready and the card still shows that role, the 2D demo is hidden and the 3D loop fades in;
 *   - a loop not looked at for 10 minutes is unloaded (textures, meshes, material destroyed); at most four stay loaded.
 *     Runtime textures carry HideFlags.HideAndDontSave (includes DontUnloadUnusedAsset), so scene changes keep them.
 *
 * File format (written by Assets/HelpDemos3D/library/h3d_pack.py, "format": 2): a background plate (JPG, the stage
 * without actors), an atlas (JPG) of 30 x 30 px tiles in 32 x 32 px slots (1 px border from the neighbour pixels, so
 * bilinear filtering never bleeds), the matching mask (PNG) and anim.json: per frame the list of tiles to draw over the
 * plate (tile number in the frame grid, slot in the atlas) plus a per-frame fade for the seamless loop. RGB and mask are
 * merged into one RGBA32 texture once; every frame becomes one small mesh (one quad per tile), so playback is a mesh swap
 * per frame on a Sprites/Default material.
 *
 * Source: UC_HELP3D_SOURCE (environment, only a local test server http://127.0.0.1:<port>/... or the asset repository),
 * else a local UCAssetStore source (http://127.0.0.1:<port>/files/ -> .../help3d/), else the repository's main branch.
 * Self test: UC_DIAG_HELP3D=1 (or "offline"): enters freeplay, opens the help menu, shows Tesla, Sheriff and Pelican (or
 * the roles listed in UC_DIAG_HELP3D_ROLES, comma separated) one after the other, logs load and frame timing and saves
 * screenshots to <game>/UCShots/help3d/. Ends with
 * "[UC/Help3D] diag: all done". Nothing of the self test runs without the variable.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace UnknownsCollection {
    internal static class UCHelp3D {
        private const string LogPrefix = "[UC/Help3D]";
        private const string ManifestName = "manifest.json";
        private const long MaxFile = 4L * 1024 * 1024;
        private const float UnloadAfter = 600f;       // seconds a loop may stay unwatched
        private const int MaxLoaded = 4;
        private const float RetryAfter = 60f;         // after a failed manifest / role download
        private const float FadeTime = 0.25f;
        private const int SortPlate = 504, SortFrame = 505;
        private static readonly string[] Kinds = { "anim", "plate", "atlas", "alpha" };
        private static readonly Regex FileRx = new("^[a-z0-9_]{1,80}\\.(jpg|png|json)$");
        private static readonly Regex ShaRx = new("^[0-9a-f]{64}$");

        private static void Log(string s) => UnknownsCollectionPlugin.Logger?.LogInfo($"{LogPrefix} {s}");
        private static void Warn(string s) => UnknownsCollectionPlugin.Logger?.LogWarning($"{LogPrefix} {s}");

        internal static string SourceBase {
            get {
                string env = Environment.GetEnvironmentVariable("UC_HELP3D_SOURCE");
                if (!string.IsNullOrWhiteSpace(env)) {
                    env = env.Trim();
                    if (!env.EndsWith("/", StringComparison.Ordinal)) env += "/";
                    if (env.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
                        || env.StartsWith(UCAssetStore.DefaultSourceBase, StringComparison.Ordinal)) return env;
                }
                string s = UCAssetStore.Source;
                if (s.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
                    return (s.EndsWith("files/", StringComparison.Ordinal) ? s.Substring(0, s.Length - "files/".Length) : s) + "help3d/";
                return UCAssetStore.DefaultSourceBase + "main/help3d/";
            }
        }

        // ------------------------------------------------------------------ state
        private sealed class FileRef { public string File, Sha256; public long Size; }

        private sealed class Anim {
            public string Role;
            public int W, H, Fps, Frames;
            public Texture2D Plate, Atlas;
            public Sprite PlateSprite;
            public Material Mat;
            public Mesh[] Meshes;
            public float LastSeen;
        }

        private static Dictionary<string, Dictionary<string, FileRef>> _manifest;
        private static bool _manifestLoading;
        private static float _manifestFailedAt = -999f;
        private static readonly Dictionary<string, Anim> Loaded = new();
        private static readonly HashSet<string> Loading = new();
        private static readonly Dictionary<string, float> FailedAt = new();
        private static bool _janitor;
        private static float _nextPrune;

        // the card currently on screen
        private static GameObject _stage, _view;
        private static string _role;
        private static Vector2 _stageSize;
        private static Anim _viewAnim;
        private static MeshFilter _mf;
        private static SpriteRenderer _plate;
        private static float _viewStart, _fadeStart;
        private static int _lastFrame = -1;
        internal static int FramesShown;                 // self test: distinct loop frames put on screen

        internal static bool IsShowing(string role) => _view != null && _viewAnim != null && _role == role;

        // ------------------------------------------------------------------ hooks from UCHelpMenu

        /// <summary>BuildStage built a new demo stage for this role: show the 3D loop if it is in memory, else start loading
        /// it (the 2D demo keeps running meanwhile).</summary>
        internal static void Attach(GameObject stage, string role, Vector2 stageSize) {
            try {
                _stage = stage; _role = role; _stageSize = stageSize;
                _view = null; _viewAnim = null; _mf = null; _plate = null; _lastFrame = -1;
                if (stage == null || string.IsNullOrEmpty(role)) return;
                if (Loaded.TryGetValue(role, out var a)) { Show(a, 0.12f); return; }
                Request(role);
            } catch (Exception e) { Warn($"attach {role}: {e.Message}"); }
        }

        /// <summary>Every frame while the stage exists. True = the 3D loop is on screen (the 2D demo must not animate).</summary>
        internal static bool Tick(GameObject stage) {
            try {
                if (Time.realtimeSinceStartup >= _nextPrune) { _nextPrune = Time.realtimeSinceStartup + 10f; Prune(); }
                if (stage == null || stage != _stage) return false;
                if (_view == null) {
                    if (_role == null || !Loaded.TryGetValue(_role, out var ready)) return false;
                    Show(ready, FadeTime);
                    if (_view == null) return false;
                }
                var a = _viewAnim;
                if (a == null || a.Meshes == null || _mf == null) return false;
                a.LastSeen = Time.realtimeSinceStartup;
                int f = (int)((Time.unscaledTime - _viewStart) * a.Fps) % a.Frames;
                if (f < 0) f = 0;
                if (f != _lastFrame) {
                    _mf.sharedMesh = a.Meshes[f];
                    _lastFrame = f;
                    FramesShown++;
                }
                float k = _fadeStart < 0f ? 1f : Mathf.Clamp01((Time.unscaledTime - _fadeStart) / FadeTime);
                if (k < 1f || _fadeStart >= 0f) {
                    var c = new Color(1f, 1f, 1f, k);
                    if (_plate != null) _plate.color = c;
                    if (a.Mat != null) a.Mat.color = c;
                    if (k >= 1f) _fadeStart = -1f;
                }
                return true;
            } catch (Exception e) {
                Warn($"tick: {e.Message}");
                return false;
            }
        }

        private static void Show(Anim a, float fade) {
            if (_stage == null || a == null || a.Meshes == null) return;
            var st = _stage.transform;
            // hide the 2D demo (everything the stage holds so far: inset, floor line, tag, actors)
            for (int i = 0; i < st.childCount; i++) st.GetChild(i).gameObject.SetActive(false);

            _view = new GameObject("UCHelp3D");
            _view.layer = _stage.layer;
            _view.transform.SetParent(st, false);
            _view.transform.localPosition = new Vector3(0f, 0f, -0.02f);
            float qh = _stageSize.y, qw = qh * a.W / a.H;
            if (qw > _stageSize.x) { qw = _stageSize.x; qh = qw * a.H / a.W; }
            _view.transform.localScale = Vector3.one * (qw / a.W);       // meshes and plate are in pixel units

            var pg = new GameObject("plate") { layer = _stage.layer };
            pg.transform.SetParent(_view.transform, false);
            _plate = pg.AddComponent<SpriteRenderer>();
            _plate.sprite = a.PlateSprite;
            _plate.sortingOrder = SortPlate;

            var fg = new GameObject("frame") { layer = _stage.layer };
            fg.transform.SetParent(_view.transform, false);
            fg.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            _mf = fg.AddComponent<MeshFilter>();
            var mr = fg.AddComponent<MeshRenderer>();
            mr.sharedMaterial = a.Mat;
            mr.sortingOrder = SortFrame;

            _viewAnim = a;
            _viewStart = Time.unscaledTime;
            _fadeStart = fade > 0f ? Time.unscaledTime - (FadeTime - fade) : -1f;
            var c = new Color(1f, 1f, 1f, fade > 0f ? 0f : 1f);
            _plate.color = c;
            a.Mat.color = c;
            _lastFrame = -1;
            a.LastSeen = Time.realtimeSinceStartup;
        }

        // ------------------------------------------------------------------ loading

        private static MonoBehaviour Host => UnknownsCollectionUpdater.Instance;

        private static void Request(string role) {
            if (Loading.Contains(role)) return;
            if (FailedAt.TryGetValue(role, out var at) && Time.realtimeSinceStartup - at < RetryAfter) return;
            if (_manifest == null && !_manifestLoading && Time.realtimeSinceStartup - _manifestFailedAt < RetryAfter) return;
            var host = Host;
            if (host == null) return;
            Loading.Add(role);
            BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(host, LoadRole(role));
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

        private static string Sha(byte[] data) {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        private static IEnumerator LoadManifest() {
            _manifestLoading = true;
            string url = SourceBase + ManifestName + "?t=" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            var www = Get(url);
            var op = www.SendWebRequest();
            while (!op.isDone) yield return null;
            string text = null;
            long code = www.responseCode;
            if (!www.isNetworkError && !www.isHttpError) text = www.downloadHandler.text;
            else Warn($"no 3D demo list from {SourceBase} ({(code > 0 ? "HTTP " + code : www.error)}), the 2D demos stay");
            www.downloadHandler.Dispose();
            www.Dispose();
            _manifestLoading = false;
            if (text == null || !TryParseManifest(text, out var m)) { _manifestFailedAt = Time.realtimeSinceStartup; yield break; }
            _manifest = m;
            Log($"3D demos listed for {m.Count} roles ({string.Join(", ", m.Keys)})");
        }

        private static bool TryParseManifest(string text, out Dictionary<string, Dictionary<string, FileRef>> result) {
            result = new Dictionary<string, Dictionary<string, FileRef>>();
            try {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("format", out var fmt) || fmt.GetInt32() != 1) { Warn("unknown 3D demo list format"); return false; }
                foreach (var role in root.GetProperty("roles").EnumerateObject()) {
                    var files = new Dictionary<string, FileRef>();
                    foreach (var kind in Kinds) {
                        if (!role.Value.TryGetProperty(kind, out var fe)) break;
                        var f = new FileRef {
                            File = fe.GetProperty("file").GetString(),
                            Size = fe.GetProperty("size").GetInt64(),
                            Sha256 = fe.GetProperty("sha256").GetString(),
                        };
                        if (f.File == null || !FileRx.IsMatch(f.File) || f.Sha256 == null || !ShaRx.IsMatch(f.Sha256)
                            || f.Size <= 0 || f.Size > MaxFile) break;
                        files[kind] = f;
                    }
                    if (files.Count == Kinds.Length) result[role.Name] = files;
                    else Warn($"3D demo entry '{role.Name}' incomplete, skipped");
                }
                return true;
            } catch (Exception e) {
                Warn($"3D demo list unreadable: {e.Message}");
                return false;
            }
        }

        private static IEnumerator LoadRole(string role) {
            float t0 = Time.realtimeSinceStartup;
            if (_manifest == null) {
                if (_manifestLoading) { while (_manifestLoading) yield return null; }
                else {
                    var it = LoadManifest();
                    while (it.MoveNext()) yield return it.Current;
                }
            }
            if (_manifest == null || !_manifest.TryGetValue(role, out var files)) {
                Loading.Remove(role);
                FailedAt[role] = Time.realtimeSinceStartup;
                yield break;
            }
            var data = new Dictionary<string, byte[]>();
            foreach (var kind in Kinds) {
                var f = files[kind];
                var www = Get(SourceBase + f.File);
                var op = www.SendWebRequest();
                while (!op.isDone) yield return null;
                byte[] bytes = null;
                if (!www.isNetworkError && !www.isHttpError) bytes = www.downloadHandler.data;
                else Warn($"{role}: {f.File} not loaded ({(www.responseCode > 0 ? "HTTP " + www.responseCode : www.error)})");
                www.downloadHandler.Dispose();
                www.Dispose();
                if (bytes == null || bytes.LongLength != f.Size || Sha(bytes) != f.Sha256) {
                    if (bytes != null) Warn($"{role}: {f.File} has the wrong size or checksum, dropped");
                    Loading.Remove(role);
                    FailedAt[role] = Time.realtimeSinceStartup;
                    yield break;
                }
                data[kind] = bytes;
            }
            float tDown = Time.realtimeSinceStartup;
            Anim anim = null;
            var build = Build(role, data, a => anim = a);
            while (true) {
                bool more;
                try { more = build.MoveNext(); } catch (Exception e) { Warn($"{role}: decoding failed: {e.Message}"); more = false; anim = null; }
                if (!more) break;
                yield return build.Current;
            }
            Loading.Remove(role);
            if (anim == null) { FailedAt[role] = Time.realtimeSinceStartup; yield break; }
            anim.LastSeen = Time.realtimeSinceStartup;
            Loaded[role] = anim;
            long bytesTotal = data.Values.Sum(d => (long)d.Length);
            Log($"{role}: 3D demo ready ({bytesTotal / 1024} KB, {anim.Frames} frames, atlas {anim.Atlas.width}x{anim.Atlas.height}; " +
                $"download {(tDown - t0) * 1000f:F0} ms, decode {(Time.realtimeSinceStartup - tDown) * 1000f:F0} ms)");
            EnforceCap();
            EnsureJanitor();
        }

        // Decode one loop. Spread over a few frames so opening a card never stalls the game noticeably.
        private static IEnumerator Build(string role, Dictionary<string, byte[]> data, Action<Anim> done) {
            int w, h, fps, frames, tile, slot, cols, aw, ah;
            float[] fade;
            byte[] seq;
            using (var doc = JsonDocument.Parse(data["anim"])) {
                var r = doc.RootElement;
                if (r.GetProperty("format").GetInt32() != 2) throw new InvalidDataException("anim format");
                w = r.GetProperty("w").GetInt32(); h = r.GetProperty("h").GetInt32();
                fps = r.GetProperty("fps").GetInt32(); frames = r.GetProperty("frames").GetInt32();
                tile = r.GetProperty("tile").GetInt32(); slot = r.GetProperty("slot").GetInt32();
                cols = r.GetProperty("cols").GetInt32();
                var atl = r.GetProperty("atlas");
                aw = atl[0].GetInt32(); ah = atl[1].GetInt32();
                fade = r.GetProperty("fade").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                seq = Convert.FromBase64String(r.GetProperty("tiles").GetString() ?? "");
            }
            if (w < 16 || w > 2048 || h < 16 || h > 1024 || fps < 1 || fps > 60 || frames < 1 || frames > 600
                || tile < 4 || slot < tile + 2 || cols < 1 || aw < slot || ah < slot || aw > 4096 || ah > 4096 || fade.Length != frames)
                throw new InvalidDataException("anim header");

            var plate = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!ImageConversion.LoadImage(plate, data["plate"], true) || plate.width != w || plate.height != h) {
                UnityEngine.Object.Destroy(plate);
                throw new InvalidDataException("plate");
            }
            Keep(plate);
            yield return null;

            var rgb = new Texture2D(2, 2, TextureFormat.RGB24, false);
            var msk = new Texture2D(2, 2, TextureFormat.RGB24, false);
            Texture2D atlas = null;
            try {
                if (!ImageConversion.LoadImage(rgb, data["atlas"], false) || rgb.width != aw || rgb.height != ah)
                    throw new InvalidDataException("atlas");
                if (!ImageConversion.LoadImage(msk, data["alpha"], false) || msk.width != aw || msk.height != ah)
                    throw new InvalidDataException("mask");
                atlas = Merge(rgb, msk, aw, ah);
            } finally {
                UnityEngine.Object.Destroy(rgb);
                UnityEngine.Object.Destroy(msk);
            }
            if (atlas == null) { UnityEngine.Object.Destroy(plate); throw new InvalidDataException("atlas merge"); }
            yield return null;

            var shader = Shader.Find("Sprites/Default");
            if (shader == null) { UnityEngine.Object.Destroy(plate); UnityEngine.Object.Destroy(atlas); throw new InvalidDataException("shader"); }
            var mat = new Material(shader) { mainTexture = atlas };
            mat.hideFlags |= HideFlags.HideAndDontSave;
            var sprite = Sprite.Create(plate, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 1f);
            sprite.hideFlags |= HideFlags.HideAndDontSave;

            var meshes = new Mesh[frames];
            int perRow = aw / slot, pos = 0;
            float fw = aw, fh = ah;
            for (int i = 0; i < frames; i++) {
                int n = seq[pos] | (seq[pos + 1] << 8);
                pos += 2;
                var verts = new Vector3[n * 4];
                var uvs = new Vector2[n * 4];
                var cols32 = new Color32[n * 4];
                var tris = new int[n * 6];
                byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(fade[i]) * 255f);
                for (int j = 0; j < n; j++) {
                    int k = seq[pos];
                    int s = seq[pos + 1] | (seq[pos + 2] << 8);
                    pos += 3;
                    int x0 = (k % cols) * tile, y0 = (k / cols) * tile;
                    int tw = Math.Min(tile, w - x0), th = Math.Min(tile, h - y0);
                    int ax = (s % perRow) * slot + 1, ay = (s / perRow) * slot + 1;
                    float l = x0 - w * 0.5f, r = l + tw, t = h * 0.5f - y0, b = t - th;
                    float u0 = ax / fw, u1 = (ax + tw) / fw, v0 = 1f - ay / fh, v1 = 1f - (ay + th) / fh;
                    int q = j * 4;
                    verts[q] = new Vector3(l, b, 0f); verts[q + 1] = new Vector3(l, t, 0f);
                    verts[q + 2] = new Vector3(r, t, 0f); verts[q + 3] = new Vector3(r, b, 0f);
                    uvs[q] = new Vector2(u0, v1); uvs[q + 1] = new Vector2(u0, v0);
                    uvs[q + 2] = new Vector2(u1, v0); uvs[q + 3] = new Vector2(u1, v1);
                    for (int c = 0; c < 4; c++) cols32[q + c] = new Color32(255, 255, 255, a);
                    int ti = j * 6;
                    tris[ti] = q; tris[ti + 1] = q + 1; tris[ti + 2] = q + 2;
                    tris[ti + 3] = q; tris[ti + 4] = q + 2; tris[ti + 5] = q + 3;
                }
                var mesh = new Mesh { name = $"help3d_{role}_{i}" };
                mesh.hideFlags |= HideFlags.HideAndDontSave;
                mesh.vertices = verts;
                mesh.uv = uvs;
                mesh.colors32 = cols32;
                mesh.triangles = tris;
                mesh.RecalculateBounds();
                meshes[i] = mesh;
                if (i % 32 == 31) yield return null;
            }
            done(new Anim {
                Role = role, W = w, H = h, Fps = fps, Frames = frames, Plate = plate, Atlas = atlas,
                PlateSprite = sprite, Mat = mat, Meshes = meshes,
            });
        }

        private static void Keep(Texture2D t) {
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.hideFlags |= HideFlags.HideAndDontSave;   // includes DontUnloadUnusedAsset: survives scene changes
        }

        // Bytes per pixel and the red/green/blue offsets of the formats LoadImage can hand back.
        private static bool Layout(TextureFormat f, out int bpp, out int r, out int g, out int b) {
            switch (f) {
                case TextureFormat.RGB24: bpp = 3; r = 0; g = 1; b = 2; return true;
                case TextureFormat.RGBA32: bpp = 4; r = 0; g = 1; b = 2; return true;
                case TextureFormat.ARGB32: bpp = 4; r = 1; g = 2; b = 3; return true;
                case TextureFormat.BGRA32: bpp = 4; r = 2; g = 1; b = 0; return true;
                case TextureFormat.R8:
                case TextureFormat.Alpha8: bpp = 1; r = g = b = 0; return true;
                default: bpp = r = g = b = 0; return false;
            }
        }

        // Raw pixel bytes of a readable texture as a managed array (one memory copy instead of millions of interop reads).
        private static byte[] Raw(Texture2D t) {
            var arr = t.GetRawTextureData();
            int n = arr.Length;
            var managed = new byte[n];
            Marshal.Copy(IntPtr.Add(arr.Pointer, 4 * IntPtr.Size), managed, 0, n);
            // sanity check of the il2cpp array layout; fall back to element reads if it ever differs
            if (n > 0 && (managed[0] != arr[0] || managed[n / 2] != arr[n / 2] || managed[n - 1] != arr[n - 1]))
                for (int i = 0; i < n; i++) managed[i] = arr[i];
            return managed;
        }

        private static Texture2D Merge(Texture2D rgb, Texture2D msk, int aw, int ah) {
            if (!Layout(rgb.format, out int cb, out int cr, out int cg, out int cbb) || !Layout(msk.format, out int mb, out int mr, out _, out _)) {
                Warn($"unexpected texture formats {rgb.format} / {msk.format}");
                return null;
            }
            byte[] c = Raw(rgb), m = Raw(msk);
            int px = aw * ah;
            if (c.Length < px * cb || m.Length < px * mb) { Warn("texture data shorter than expected"); return null; }
            var outp = new byte[px * 4];
            for (int i = 0, ci = 0, mi = 0, o = 0; i < px; i++, ci += cb, mi += mb, o += 4) {
                outp[o] = c[ci + cr];
                outp[o + 1] = c[ci + cg];
                outp[o + 2] = c[ci + cbb];
                outp[o + 3] = m[mi + mr];
            }
            var tex = new Texture2D(aw, ah, TextureFormat.RGBA32, false);
            var pin = GCHandle.Alloc(outp, GCHandleType.Pinned);
            try { tex.LoadRawTextureData(pin.AddrOfPinnedObject(), outp.Length); } finally { pin.Free(); }
            Keep(tex);
            tex.Apply(false, true);    // upload and drop the CPU copy
            return tex;
        }

        // ------------------------------------------------------------------ unloading

        private static void EnsureJanitor() {
            if (_janitor || Host == null) return;
            _janitor = true;
            BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(Host, Janitor());
        }

        // Runs while anything is loaded, also with the help menu closed or in the main menu.
        private static IEnumerator Janitor() {
            while (Loaded.Count > 0) {
                if (Time.realtimeSinceStartup >= _nextPrune) { _nextPrune = Time.realtimeSinceStartup + 10f; Prune(); }
                yield return null;
            }
            _janitor = false;
        }

        private static bool OnScreen(Anim a) => a != null && a == _viewAnim && _view != null;

        private static void Prune() {
            float now = Time.realtimeSinceStartup;
            foreach (var kv in Loaded.ToList())
                if (!OnScreen(kv.Value) && now - kv.Value.LastSeen > UnloadAfter) Unload(kv.Key, "not viewed for 10 minutes");
        }

        private static void EnforceCap() {
            while (Loaded.Count > MaxLoaded) {
                var oldest = Loaded.Values.Where(a => !OnScreen(a)).OrderBy(a => a.LastSeen).FirstOrDefault();
                if (oldest == null) return;
                Unload(oldest.Role, "too many loaded");
            }
        }

        private static void Unload(string role, string why) {
            if (!Loaded.TryGetValue(role, out var a)) return;
            Loaded.Remove(role);
            if (_viewAnim == a) { _viewAnim = null; _view = null; }
            try {
                if (a.Meshes != null) foreach (var m in a.Meshes) if (m != null) UnityEngine.Object.Destroy(m);
                if (a.Mat != null) UnityEngine.Object.Destroy(a.Mat);
                if (a.PlateSprite != null) UnityEngine.Object.Destroy(a.PlateSprite);
                if (a.Plate != null) UnityEngine.Object.Destroy(a.Plate);
                if (a.Atlas != null) UnityEngine.Object.Destroy(a.Atlas);
            } catch { }
            a.Meshes = null;
            Log($"{role}: 3D demo unloaded ({why})");
        }

        // ------------------------------------------------------------------ self test (UC_DIAG_HELP3D)

        private static readonly string DiagMode = Environment.GetEnvironmentVariable("UC_DIAG_HELP3D") ?? "";
        // UC_DIAG_HELP3D_ROLES=Tesla,Mayor,... (names as in the help menu); default: the three Lauf-1 samples
        private static readonly string[] DiagRoles = ParseDiagRoles(Environment.GetEnvironmentVariable("UC_DIAG_HELP3D_ROLES"));

        private static string[] ParseDiagRoles(string s) {
            var list = (s ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            return list.Length > 0 ? list : new[] { "Tesla", "Sheriff", "Pelican" };
        }
        private static int _diagStep = -1, _diagRole;
        private static float _diagAt = -1f, _diagT0;
        private static int _diagFrames, _diagShown0;
        private static float _diagDtSum, _diagDtMax;
        private static bool _diagDone;

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class DiagFreeplayPatch {
            private static bool fired;
            private static bool Prepare() => DiagMode.Length > 0;
            private static void Postfix(MainMenuManager __instance) {
                if (fired) return;
                fired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, EnterFreeplay());
            }
        }

        private static IEnumerator EnterFreeplay() {
            Log("diag: waiting for the login ...");
            float t0 = Time.time;
            while (true) {
                bool ok = false;
                try { ok = EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow(); } catch { }
                if (ok) break;
                if (Time.time - t0 > 90f) { Log("diag: login timeout"); yield break; }
                yield return null;
            }
            try {
                var popover = UnityEngine.Object.FindObjectOfType<FreeplayPopover>(true);
                for (var t = popover.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                popover.Show();
                popover.PlayMap(MapNames.Skeld);
                Log($"diag: freeplay on the Skeld started (mode '{DiagMode}', source {SourceBase})");
            } catch (Exception e) { Log($"diag: freeplay failed: {e.Message}"); }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class DiagTickPatch {
            private static bool Prepare() => DiagMode.Length > 0;
            private static void Postfix(HudManager __instance) {
                if (_diagDone) return;
                try { DiagTick(__instance); } catch (Exception e) { Log($"diag failed: {e}"); _diagDone = true; }
            }
        }

        private static void Shot(string tag) {
            string dir = Path.Combine(BepInEx.Paths.GameRootPath, "UCShots", "help3d");
            Directory.CreateDirectory(dir);
            string name = new string(DiagRoles[_diagRole].ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            string file = Path.Combine(dir, $"{(DiagMode == "offline" ? "offline" : "online")}_{name}_{tag}.png");
            ScreenCapture.CaptureScreenshot(file);
            Log($"diag: shot {file}");
        }

        private static void DiagTick(HudManager hud) {
            float now = Time.realtimeSinceStartup;
            if (ShipStatus.Instance == null || PlayerControl.LocalPlayer == null) return;
            bool gate = (hud.UseButton != null && hud.UseButton.isActiveAndEnabled) || (hud.PetButton != null && hud.PetButton.isActiveAndEnabled);
            if (_diagStep < 0) {
                if (!gate) return;
                if (_diagAt < 0f) { _diagAt = now + 6f; return; }
                if (now < _diagAt) return;
                _diagStep = 0;
                _diagRole = 0;
            }
            if (now < _diagAt) {
                if (_diagStep == 2 || _diagStep == 3) { _diagFrames++; _diagDtSum += Time.unscaledDeltaTime; _diagDtMax = Mathf.Max(_diagDtMax, Time.unscaledDeltaTime); }
                return;
            }
            string role = DiagRoles[_diagRole];
            switch (_diagStep) {
                case 0: // open the panel, pick the role
                    if (!UCHelpMenu.DiagPanelOpen) UCHelpMenu.DiagToggle();
                    bool sel = UCHelpMenu.DiagSelect(role);
                    Log($"diag: {role} selected ({sel}); 3D in memory: {Loaded.ContainsKey(role)}");
                    _diagT0 = now;
                    _diagStep = 1;
                    break;
                case 1: // wait for the 3D loop (or the fallback timeout)
                    float limit = DiagMode == "offline" ? 8f : 25f;
                    if (!IsShowing(role) && now - _diagT0 < limit) return;
                    Log($"diag: {role} {(IsShowing(role) ? "3D shown" : "still 2D")} after {(now - _diagT0) * 1000f:F0} ms" +
                        (IsShowing(role) ? "" : $" (loading: {Loading.Contains(role)}, list: {(_manifest != null ? _manifest.Count.ToString() : "none")})"));
                    _diagStep = 2;
                    _diagAt = now + 1.2f;
                    _diagFrames = 0; _diagDtSum = 0f; _diagDtMax = 0f; _diagShown0 = FramesShown;
                    break;
                case 2:
                    Shot("a");
                    _diagStep = 3;
                    _diagAt = now + 1.45f;
                    break;
                case 3:
                    Shot("b");
                    float avg = _diagFrames > 0 ? _diagDtSum / _diagFrames : 0f;
                    Log($"diag: {role} timing over {_diagFrames} game frames: {(avg > 0 ? 1f / avg : 0f):F0} fps average, " +
                        $"worst frame {_diagDtMax * 1000f:F0} ms, {FramesShown - _diagShown0} loop frames shown (24 fps loop)");
                    _diagRole++;
                    if (_diagRole >= DiagRoles.Length) {
                        Log($"diag: loaded now: {string.Join(", ", Loaded.Keys)}");
                        Log("diag: all done");
                        _diagDone = true;
                        return;
                    }
                    _diagStep = 0;
                    _diagAt = now + 0.5f;
                    break;
            }
        }
    }
}
