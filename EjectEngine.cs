// Unknown's Atlas / Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
//
// EjectEngine - spielt Rauswurf-Szenen aus JSON ab (Atlas: eject_scenes.json, UC: eject_void.json).
//
// GETEILTE DATEI: liegt wortgleich in UnknownsAtlas/src und UnknownsCollection; nur die namespace-Zeile
// unterscheidet sich. Aenderungen immer in beiden Kopien. Die Regie entsteht in
// UnknownsAtlas/tools/eject_scenes.py, das Vorschau-Kino (tools/eject_theater) spielt dieselben Dateien
// mit derselben Logik ab (Keyframes je Eigenschaft, Wobble, Flicker, Parent, Parallax, Kamera mit
// Grenzen, Partikel, Ereignisse, Kinobalken, Synth-Klaenge).
//
// Einheiten: Szenen-Einheiten, 1 = 180 px der Grafik; sichtbar 11 x 6,2 bei Zoom 1 (im Spiel
// bildschirmfuellend skaliert). Grafik und JSON kommen aus den Ressourcen DIESER Assembly
// (<namespace>.Resources.<datei>); Texturen leben nur fuer die laufende Szene (32-Bit-Prozess).
//
// Ablauf im Spiel: der Aufrufer stoppt die Animate-Koroutine des ExileController und startet
// Play(run). Text samt Tipp-Geraeusch (HandleText), Impostor-Zeile und WrapUp bzw. auf dem Airship
// WrapUpAndSpawn bleiben die des Spiels, damit TOR/UC-Patches unveraendert greifen.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnknownsCollection {

internal static class EjectEngine {
    public static Action<string> Log = _ => { };
    public static Action<string> Warn = _ => { };
    private const float Ppu = 180f, ViewW = 11f, ViewH = 6.2f;
    private static string Prefix => typeof(EjectEngine).Namespace + ".Resources.";

    // ================================================================== Daten

    internal sealed class Key { public float T, V, Arc; public string E = "io"; }
    internal sealed class Wob { public string P; public float Amp, F, Ph, T0 = float.NaN, T1; }
    internal sealed class Flick { public float Amp, F, T0 = float.NaN, T1; }

    internal sealed class ObjDef {
        public string Id, Sprite, Parent;
        public int Z;
        public bool Screen;
        public Vector2 Pivot = new(0.5f, 0.5f);
        public float Depth = 1f, Hide = float.NaN;
        public Color Tint = Color.white;
        public readonly Dictionary<string, float> Static = new();
        public readonly Dictionary<string, List<Key>> Tracks = new();
        public readonly List<Wob> Wobble = new();
        public Flick Flicker;
    }

    internal sealed class EmitDef {
        public string Sprite; public int Z; public bool Stretch;
        public float T0, T1, Rate, Burst = float.NaN; public int N;
        public float[] Area = new float[4], Vel = new float[4], Acc = new float[2], Life = { 1, 1 }, Size = { 0.2f, 0.2f }, Spin = { 0, 0 }, Rot = { 0, 0 }, Fade = { 0.15f, 0.35f };
        public float Drag, Grow, A = 1f; public Color Tint = Color.white;
    }

    internal sealed class EventDef { public float T, Vol = 0.7f, ShakeDur, ShakeAmp, FlashA, FlashDur; public string Sound; public Color FlashCol; public bool Shake, Flash; }

    internal sealed class SceneDef {
        public string Id, Map, Title, TextStyle;
        public bool Skip;
        public float Length, TextAt = 1.2f, TextDur = 2.6f, BoundsW = 12f, BoundsH = 6.8f;
        public readonly Dictionary<string, List<Key>> Camera = new();
        public readonly List<ObjDef> Objects = new();
        public ObjDef Player;
        public readonly List<EmitDef> Emitters = new();
        public readonly List<EventDef> Events = new();
    }

    internal sealed class Doc {
        public float Top = 0.8f, Bottom = 0.55f, In = 0.45f, Vignette = 0.5f;
        public readonly List<SceneDef> Scenes = new();
    }

    // ================================================================== Laden

    public static Doc LoadDoc(string file) {
        try {
            using var s = typeof(EjectEngine).Assembly.GetManifestResourceStream(Prefix + file);
            if (s == null) { Warn($"eject: resource {file} missing"); return null; }
            using var r = new StreamReader(s);
            return Parse(r.ReadToEnd());
        } catch (Exception e) { Warn($"eject: {file} unreadable: {e.Message}"); return null; }
    }

    private static float F(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetSingle() : 0f;
    private static float[] FA(JsonElement e) => e.EnumerateArray().Select(F).ToArray();
    private static Color Hex(string h) {
        if (string.IsNullOrEmpty(h) || h[0] != '#') return Color.white;
        int n = Convert.ToInt32(h.Substring(1), 16);
        return new Color((n >> 16 & 255) / 255f, (n >> 8 & 255) / 255f, (n & 255) / 255f, 1f);
    }

    private static void ReadKeys(JsonElement arr, Dictionary<string, List<Key>> tracks) {
        foreach (var k in arr.EnumerateArray()) {
            float t = F(k[0]);
            string ease = k.GetArrayLength() > 2 ? k[2].GetString() : "io";
            float arc = k[1].TryGetProperty("arc", out var a) ? F(a) : 0f;
            foreach (var p in k[1].EnumerateObject()) {
                if (p.Name == "arc") continue;
                if (!tracks.TryGetValue(p.Name, out var list)) tracks[p.Name] = list = new List<Key>();
                list.Add(new Key { T = t, V = F(p.Value), E = ease, Arc = p.Name == "y" ? arc : 0f });
            }
        }
        foreach (var name in tracks.Keys.ToList()) tracks[name] = tracks[name].OrderBy(x => x.T).ToList();   // stabil
    }

    private static ObjDef ReadObj(JsonElement o) {
        var d = new ObjDef();
        foreach (var p in o.EnumerateObject()) {
            switch (p.Name) {
                case "id": d.Id = p.Value.GetString(); break;
                case "sprite": d.Sprite = p.Value.GetString(); break;
                case "parent": d.Parent = p.Value.GetString(); break;
                case "z": d.Z = (int)F(p.Value); break;
                case "screen": d.Screen = p.Value.ValueKind == JsonValueKind.True; break;
                case "pivot": { var a = FA(p.Value); d.Pivot = new Vector2(a[0], a[1]); break; }
                case "depth": d.Depth = F(p.Value); break;
                case "hide": d.Hide = F(p.Value); break;
                case "tint": d.Tint = Hex(p.Value.GetString()); break;
                case "keys": ReadKeys(p.Value, d.Tracks); break;
                case "wobble":
                    foreach (var w in p.Value.EnumerateArray()) {
                        var wb = new Wob { P = w.GetProperty("p").GetString(), Amp = F(w.GetProperty("amp")), F = F(w.GetProperty("f")) };
                        if (w.TryGetProperty("ph", out var ph)) wb.Ph = F(ph);
                        if (w.TryGetProperty("t0", out var a0)) { wb.T0 = F(a0); wb.T1 = F(w.GetProperty("t1")); }
                        d.Wobble.Add(wb);
                    }
                    break;
                case "flicker": {
                    var f = new Flick { Amp = F(p.Value.GetProperty("amp")), F = F(p.Value.GetProperty("f")) };
                    if (p.Value.TryGetProperty("t0", out var a0)) { f.T0 = F(a0); f.T1 = F(p.Value.GetProperty("t1")); }
                    d.Flicker = f; break;
                }
                default:
                    if (p.Value.ValueKind == JsonValueKind.Number) d.Static[p.Name] = F(p.Value);
                    break;
            }
        }
        return d;
    }

    public static Doc Parse(string json) {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var d = new Doc();
        if (root.TryGetProperty("cinema", out var c)) {
            d.Top = F(c.GetProperty("top")); d.Bottom = F(c.GetProperty("bottom")); d.In = F(c.GetProperty("in"));
            d.Vignette = F(c.GetProperty("vignette"));
        }
        foreach (var s in root.GetProperty("scenes").EnumerateArray()) {
            var sd = new SceneDef {
                Id = s.GetProperty("id").GetString(), Map = s.GetProperty("map").GetString(),
                Title = s.TryGetProperty("title", out var ti) ? ti.GetString() : "",
                Skip = s.TryGetProperty("skip", out var sk) && sk.ValueKind == JsonValueKind.True,
                TextStyle = s.TryGetProperty("textStyle", out var ts) ? ts.GetString() : null,
                Length = F(s.GetProperty("length")),
            };
            if (s.TryGetProperty("text", out var tx)) { sd.TextAt = F(tx[0]); sd.TextDur = F(tx[1]); }
            if (s.TryGetProperty("bounds", out var b)) { sd.BoundsW = F(b[0]); sd.BoundsH = F(b[1]); }
            if (s.TryGetProperty("camera", out var cam)) ReadKeys(cam, sd.Camera);
            foreach (var o in s.GetProperty("objects").EnumerateArray()) sd.Objects.Add(ReadObj(o));
            sd.Player = s.TryGetProperty("player", out var pl) ? ReadObj(pl) : new ObjDef();
            sd.Player.Id = "player";
            foreach (var e in s.GetProperty("emitters").EnumerateArray()) {
                var em = new EmitDef();
                foreach (var p in e.EnumerateObject()) {
                    switch (p.Name) {
                        case "sprite": em.Sprite = p.Value.GetString(); break;
                        case "z": em.Z = (int)F(p.Value); break;
                        case "t0": em.T0 = F(p.Value); break;
                        case "t1": em.T1 = F(p.Value); break;
                        case "rate": em.Rate = F(p.Value); break;
                        case "burst": em.Burst = F(p.Value); break;
                        case "n": em.N = (int)F(p.Value); break;
                        case "area": em.Area = FA(p.Value); break;
                        case "vel": em.Vel = FA(p.Value); break;
                        case "acc": em.Acc = FA(p.Value); break;
                        case "drag": em.Drag = F(p.Value); break;
                        case "life": em.Life = FA(p.Value); break;
                        case "size": em.Size = FA(p.Value); break;
                        case "grow": em.Grow = F(p.Value); break;
                        case "spin": em.Spin = FA(p.Value); break;
                        case "rot": em.Rot = FA(p.Value); break;
                        case "tint": em.Tint = Hex(p.Value.GetString()); break;
                        case "a": em.A = F(p.Value); break;
                        case "fade": em.Fade = FA(p.Value); break;
                        case "stretch": em.Stretch = p.Value.ValueKind == JsonValueKind.True; break;
                    }
                }
                sd.Emitters.Add(em);
            }
            foreach (var e in s.GetProperty("events").EnumerateArray()) {
                var ev = new EventDef { T = F(e.GetProperty("t")) };
                if (e.TryGetProperty("sound", out var so)) { ev.Sound = so.GetString(); if (e.TryGetProperty("vol", out var v)) ev.Vol = F(v); }
                if (e.TryGetProperty("shake", out var sh)) { ev.Shake = true; ev.ShakeDur = F(sh[0]); ev.ShakeAmp = F(sh[1]); }
                if (e.TryGetProperty("flash", out var fl)) { ev.Flash = true; ev.FlashCol = Hex(fl[0].GetString()); ev.FlashA = F(fl[1]); ev.FlashDur = F(fl[2]); }
                sd.Events.Add(ev);
            }
            d.Scenes.Add(sd);
        }
        return d;
    }

    // ================================================================== Auswertung (wie im Kino)

    private static float Ease(string e, float u) {
        switch (e) {
            case "lin": return u;
            case "in": return u * u;
            case "out": return 1f - (1f - u) * (1f - u);
            case "back": { const float c = 1.70158f; float v = u - 1f; return 1f + v * v * ((c + 1f) * v + c); }
            case "inback": { const float c = 1.70158f; return u * u * ((c + 1f) * u - c); }
            case "hold": return u < 1f ? 0f : 1f;
            default: return u * u * (3f - 2f * u);
        }
    }

    private static float Sample(List<Key> tr, float t, float def) {
        if (tr == null || tr.Count == 0) return def;
        if (t <= tr[0].T) return tr[0].V;
        for (int i = 1; i < tr.Count; i++) {
            var n = tr[i];
            if (t < n.T) {
                var p = tr[i - 1];
                float u = (t - p.T) / Mathf.Max(1e-6f, n.T - p.T);
                if (n.E == "hold") return p.V;
                float v = p.V + (n.V - p.V) * Ease(n.E, u);
                if (n.Arc != 0f) v += n.Arc * 4f * u * (1f - u);
                return v;
            }
        }
        return tr[tr.Count - 1].V;
    }

    private static float Env(float t, float t0, float t1) {
        if (float.IsNaN(t0)) return 1f;
        if (t < t0 || t > t1) return 0f;
        return Mathf.Min(1f, (t - t0) / 0.12f) * Mathf.Min(1f, (t1 - t) / 0.12f);
    }

    private static float FlickerMul(Flick f, float t) {
        if (f == null) return 1f;
        float env = Env(t, f.T0, f.T1);
        if (env <= 0f) return 1f;
        float w = 2f * Mathf.PI * f.F;
        float n = 0.5f + 0.5f * (0.5f * Mathf.Sin(w * t) + 0.3f * Mathf.Sin(w * 1.73f * t + 1.3f) + 0.2f * Mathf.Sin(w * 2.71f * t + 2.1f));
        return 1f - f.Amp * n * env;
    }

    internal struct Xf { public float X, Y, Rot, Sx, Sy, A, B; }

    private static float Get(ObjDef o, string p, float t, float def) {
        o.Tracks.TryGetValue(p, out var tr);
        return Sample(tr, t, o.Static.TryGetValue(p, out var v) ? v : def);
    }

    private static Xf Local(ObjDef o, float t) {
        var L = new Dictionary<string, float> {
            ["x"] = Get(o, "x", t, 0f), ["y"] = Get(o, "y", t, 0f), ["rot"] = Get(o, "rot", t, 0f),
            ["sx"] = Get(o, "sx", t, 1f), ["sy"] = Get(o, "sy", t, 1f), ["s"] = Get(o, "s", t, 1f),
            ["a"] = Get(o, "a", t, 1f), ["b"] = Get(o, "b", t, 1f), ["flip"] = Get(o, "flip", t, 1f),
        };
        foreach (var w in o.Wobble) {
            float env = Env(t, w.T0, w.T1);
            if (env > 0f && L.ContainsKey(w.P)) L[w.P] += w.Amp * Mathf.Sin(2f * Mathf.PI * w.F * t + w.Ph) * env;
        }
        float flip = L["flip"] < 0f ? -1f : 1f;
        return new Xf { X = L["x"], Y = L["y"], Rot = L["rot"], Sx = L["s"] * L["sx"] * flip, Sy = L["s"] * L["sy"],
                        A = L["a"] * FlickerMul(o.Flicker, t), B = L["b"] };
    }

    private static Xf Compose(Xf W, Xf? parent) {
        if (parent == null) return W;
        var P = parent.Value;
        float r = P.Rot * Mathf.Deg2Rad, lx = W.X * P.Sx, ly = W.Y * P.Sy;
        float sign = Mathf.Sign(P.Sx * P.Sy);
        return new Xf { X = P.X + lx * Mathf.Cos(r) - ly * Mathf.Sin(r), Y = P.Y + lx * Mathf.Sin(r) + ly * Mathf.Cos(r),
                        Rot = P.Rot + W.Rot * (sign == 0f ? 1f : sign), Sx = W.Sx * P.Sx, Sy = W.Sy * P.Sy, A = W.A * P.A, B = W.B };
    }

    // ================================================================== Laufende Szene

    internal sealed class Part {
        public EmitDef Em; public SpriteRenderer R; public Vector2 Size;
        public float X, Y, Vx, Vy, Life, Age, Sz, Scale = 1f, Rot, Spin;
    }

    internal sealed class Run {
        public SceneDef Def; public Doc Doc; public ExileController Ec;
        public float T;
        public Transform World, Screen, P;
        private float _base, _visH;
        private int _layer, _sortLayer, _lo, _hi;
        private Vector3 _pBase; private Vector2 _pOff; private bool _pHidden;
        private SpriteRenderer[] _pRenderers = Array.Empty<SpriteRenderer>();
        private readonly List<(ObjDef O, Transform Tr, SpriteRenderer R, int Idx)> _objs = new();
        private readonly List<Part> _parts = new(), _pool = new();
        private readonly HashSet<int> _fired = new();
        private float[] _acc;
        private float _shakeUntil, _shakeDur, _shakeAmp, _flashAt = -9f, _flashDur, _flashA;
        private Color _flashCol;
        private SpriteRenderer _flash, _top, _bot;
        private readonly Dictionary<string, Sprite> _sprites = new();
        private readonly List<Texture2D> _textures = new();
        private readonly System.Random _rng;

        public Run(SceneDef def, Doc doc) { Def = def; Doc = doc; _rng = new System.Random(def.Id.GetHashCode()); }

        // ---- Grafik (nur fuer diese Szene)
        private Sprite Spr(string file, Vector2 pivot) {
            string key = file + "|" + pivot.x.ToString("F3") + "," + pivot.y.ToString("F3");
            if (_sprites.TryGetValue(key, out var s) && s != null) return s;
            Texture2D tex = null;
            foreach (var kv in _sprites) if (kv.Key.StartsWith(file + "|") && kv.Value != null) { tex = kv.Value.texture; break; }
            if (tex == null) {
                using var st = typeof(EjectEngine).Assembly.GetManifestResourceStream(Prefix + file);
                if (st == null) { Warn($"eject: sprite {file} missing"); return null; }
                var bytes = new byte[st.Length];
                int got = 0;
                while (got < bytes.Length) { int n = st.Read(bytes, got, bytes.Length - got); if (n <= 0) break; got += n; }
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                if (!ImageConversion.LoadImage(tex, bytes, false)) { Warn($"eject: sprite {file} undecodable"); return null; }
                tex.Apply(false, true);
                tex.hideFlags |= HideFlags.HideAndDontSave;
                _textures.Add(tex);
            }
            s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), pivot, Ppu, 0, SpriteMeshType.FullRect);
            s.hideFlags |= HideFlags.HideAndDontSave;
            _sprites[key] = s;
            return s;
        }

        private int Order(int z) => z < 0 ? _lo + z : _hi + 1 + z;

        private SpriteRenderer MakeRenderer(Transform parent, string file, Vector2 pivot, int order, string name) {
            var go = new GameObject(name) { layer = _layer };
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Spr(file, pivot);
            sr.sortingLayerID = _sortLayer;
            sr.sortingOrder = order;
            return sr;
        }

        public void Setup(ExileController ec, NetworkedPlayerInfo showAs) {
            Ec = ec;
            _layer = ec.gameObject.layer;
            // Spieler: der Ausgeworfene, oder (Void) ein nachgereichter Spieler, bei Skip-Szenen niemand
            P = null;
            if (!Def.Skip) {
                try {
                    bool has = ec.initData != null && ec.initData.networkedPlayer != null && ec.Player != null;
                    if (!has && showAs != null && ec.Player != null) {
                        ec.Player.gameObject.SetActive(true);
                        ec.Player.UpdateFromPlayerData(showAs, PlayerOutfitType.Default, PlayerMaterial.MaskType.Exile, false);
                        try { ec.Player.cosmetics.nameText.gameObject.SetActive(false); } catch { }
                        has = true;
                    }
                    if (has && ec.Player.gameObject.activeSelf) P = ec.Player.transform;
                } catch (Exception e) { Warn($"eject: player setup: {e.Message}"); P = null; }
            }
            _lo = int.MaxValue; _hi = int.MinValue;
            if (P != null)
                foreach (var r in P.GetComponentsInChildren<Renderer>(true)) { _sortLayer = r.sortingLayerID; _lo = Math.Min(_lo, r.sortingOrder); _hi = Math.Max(_hi, r.sortingOrder); }
            if (_lo == int.MaxValue) {
                var tr = ec.Text != null ? ec.Text.GetComponent<Renderer>() : null;
                _sortLayer = tr != null ? tr.sortingLayerID : 0;
                _lo = _hi = tr != null ? tr.sortingOrder - 5 : 0;
            }
            // Kulisse des Spiels ausblenden (Sterne, Lava, Wolken ...); Spieler und Texte bleiben
            foreach (var r in ec.GetComponentsInChildren<Renderer>(true)) {
                if (r == null) continue;
                var tf = r.transform;
                if ((ec.Player != null && tf.IsChildOf(ec.Player.transform)) || (ec.Text != null && tf.IsChildOf(ec.Text.transform)) ||
                    (ec.ImpostorText != null && tf.IsChildOf(ec.ImpostorText.transform))) continue;
                r.enabled = false;
            }
            foreach (var ps in ec.GetComponentsInChildren<ParticleSystem>(true)) if (ps != null) ps.Stop();
            if (ec.Player != null && P == null) ec.Player.gameObject.SetActive(false);

            var cam = Camera.main;
            float halfH = (cam != null ? cam.orthographicSize : 3f) / Mathf.Max(0.001f, ec.transform.lossyScale.y);
            float halfW = halfH * (cam != null ? cam.aspect : 16f / 9f);
            _base = Mathf.Max(2f * halfW / ViewW, 2f * halfH / ViewH);          // fuellend (je nach Seitenverhaeltnis leicht beschnitten)
            _visH = halfH / _base;

            World = new GameObject("Eject_World") { layer = _layer }.transform;
            World.SetParent(ec.transform, false);
            World.localPosition = new Vector3(0f, 0f, 1f);
            Screen = new GameObject("Eject_Screen") { layer = _layer }.transform;
            Screen.SetParent(ec.transform, false);
            Screen.localPosition = new Vector3(0f, 0f, 0.5f);
            Screen.localScale = new Vector3(_base, _base, 1f);

            if (P != null) {
                P.SetParent(World, true);
                P.localRotation = Quaternion.identity;
                P.localScale = new Vector3(Mathf.Abs(P.localScale.x), Mathf.Abs(P.localScale.y), P.localScale.z);
                World.localScale = new Vector3(_base, _base, 1f);
                // Koerperhoehe auf 1 Szenen-Einheit normieren (das Kino zeichnet die Figur genau so)
                SpriteRenderer body = null; float best = 0f;
                _pRenderers = P.GetComponentsInChildren<SpriteRenderer>(true);
                foreach (var r in _pRenderers)
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy && r.sprite != null) {
                        float area = r.bounds.size.x * r.bounds.size.y;
                        if (area > best) { best = area; body = r; }
                    }
                if (body != null) {
                    float h = body.bounds.size.y / World.lossyScale.y;
                    float k = h > 0.01f ? 1f / h : 1f;
                    _pBase = P.localScale * k;
                    var c = World.InverseTransformPoint(body.bounds.center);
                    _pOff = new Vector2(c.x - P.localPosition.x, c.y - P.localPosition.y) * k;
                } else { _pBase = P.localScale; _pOff = Vector2.zero; }
            }

            int idx = 0;
            foreach (var o in Def.Objects) {
                Transform tr;
                SpriteRenderer sr = null;
                if (!string.IsNullOrEmpty(o.Sprite)) {
                    sr = MakeRenderer(o.Screen ? Screen : World, o.Sprite, o.Pivot, o.Screen ? _hi + 60 + o.Z : Order(o.Z), o.Id);
                    tr = sr.transform;
                } else {
                    tr = new GameObject(o.Id) { layer = _layer }.transform;
                    tr.SetParent(World, false);
                }
                _objs.Add((o, tr, sr, idx++));
            }
            _acc = new float[Def.Emitters.Count];

            // Kino: Vignette, Blitz, Balken (Bildschirmebene)
            var vig = MakeRenderer(Screen, "task_eject_vignette.png", new Vector2(0.5f, 0.5f), _hi + 70, "Eject_Vignette");
            if (vig.sprite != null) {
                float vw = vig.sprite.bounds.size.x, vh = vig.sprite.bounds.size.y;
                float hw = _visH * (cam != null ? cam.aspect : 16f / 9f);
                vig.transform.localScale = new Vector3(hw * 2.08f / vw, _visH * 2.08f / vh, 1f);
                vig.color = new Color(1f, 1f, 1f, Doc.Vignette);
            }
            _flash = MakeRenderer(Screen, "task_eject_px.png", new Vector2(0.5f, 0.5f), _hi + 72, "Eject_Flash");
            _flash.transform.localScale = new Vector3(40f / 0.16f, 20f / 0.16f, 1f);
            _flash.color = Color.clear;
            _top = MakeRenderer(Screen, "task_eject_px.png", new Vector2(0.5f, 1f), _hi + 75, "Eject_Top");
            _bot = MakeRenderer(Screen, "task_eject_px.png", new Vector2(0.5f, 0f), _hi + 75, "Eject_Bottom");
            _top.color = _bot.color = Color.black;
            _top.transform.localPosition = new Vector3(0f, _visH, -0.1f);
            _bot.transform.localPosition = new Vector3(0f, -_visH, -0.1f);

            // Texte in die Balken
            if (ec.Text != null) {
                ec.Text.transform.localPosition = new Vector3(0f, (_visH - Doc.Top * 0.55f) * _base, ec.Text.transform.localPosition.z);
                var r = ec.Text.GetComponent<Renderer>(); if (r != null) { r.sortingLayerID = _sortLayer; r.sortingOrder = _hi + 80; }
            }
            if (ec.ImpostorText != null) {
                ec.ImpostorText.transform.localPosition = new Vector3(0f, (-_visH + Doc.Bottom * 0.5f) * _base, ec.ImpostorText.transform.localPosition.z);
                var r = ec.ImpostorText.GetComponent<Renderer>(); if (r != null) { r.sortingLayerID = _sortLayer; r.sortingOrder = _hi + 80; }
            }
            Apply();
        }

        // ---- Zeitschritt
        public void Step(float dt) {
            float t0 = T, t1 = T + dt;
            for (int i = 0; i < Def.Emitters.Count; i++) {
                var em = Def.Emitters[i];
                if (!float.IsNaN(em.Burst)) {
                    if (em.Burst >= t0 && em.Burst < t1 && _fired.Add(1000 + i)) for (int k = 0; k < em.N; k++) Spawn(em);
                } else if (t1 > em.T0 && t0 < em.T1) {
                    _acc[i] += em.Rate * (Mathf.Min(t1, em.T1) - Mathf.Max(t0, em.T0));
                    while (_acc[i] >= 1f) { _acc[i] -= 1f; Spawn(em); }
                }
            }
            for (int i = _parts.Count - 1; i >= 0; i--) {
                var p = _parts[i];
                p.Vx += p.Em.Acc[0] * dt; p.Vy += p.Em.Acc[1] * dt;
                float k = Mathf.Max(0f, 1f - p.Em.Drag * dt); p.Vx *= k; p.Vy *= k;
                p.X += p.Vx * dt; p.Y += p.Vy * dt; p.Rot += p.Spin * dt; p.Scale *= Mathf.Max(0f, 1f + p.Em.Grow * dt); p.Age += dt;
                if (p.Age >= p.Life) { p.R.enabled = false; _parts.RemoveAt(i); _pool.Add(p); }
            }
            for (int i = 0; i < Def.Events.Count; i++) {
                var ev = Def.Events[i];
                if (ev.T >= t0 && ev.T < t1 && _fired.Add(i)) {
                    if (ev.Sound != null) EjectSynth.Play(ev.Sound, ev.Vol);
                    if (ev.Shake) { _shakeUntil = ev.T + ev.ShakeDur; _shakeDur = ev.ShakeDur; _shakeAmp = ev.ShakeAmp; }
                    if (ev.Flash) { _flashAt = ev.T; _flashDur = ev.FlashDur; _flashA = ev.FlashA; _flashCol = ev.FlashCol; }
                }
            }
            T = t1;
            Apply();
        }

        private float R(float a, float b) => a + (b - a) * (float)_rng.NextDouble();

        private void Spawn(EmitDef em) {
            Part p;
            if (_pool.Count > 0) { p = _pool[_pool.Count - 1]; _pool.RemoveAt(_pool.Count - 1); }
            else p = new Part();
            if (p.R == null || p.Em == null || p.Em.Sprite != em.Sprite || p.Em.Z != em.Z) {
                if (p.R != null) Object.Destroy(p.R.gameObject);
                p.R = MakeRenderer(World, em.Sprite, new Vector2(0.5f, 0.5f), Order(em.Z), "Eject_Part");
                p.Size = p.R.sprite != null ? (Vector2)p.R.sprite.bounds.size : Vector2.one * 0.2f;
            }
            p.Em = em;
            p.X = R(em.Area[0], em.Area[2]); p.Y = R(em.Area[1], em.Area[3]);
            p.Vx = R(em.Vel[0], em.Vel[2]); p.Vy = R(em.Vel[1], em.Vel[3]);
            p.Life = R(em.Life[0], em.Life[1]); p.Age = 0f; p.Sz = R(em.Size[0], em.Size[1]); p.Scale = 1f;
            p.Rot = R(em.Rot[0], em.Rot[1]); p.Spin = R(em.Spin[0], em.Spin[1]);
            p.R.enabled = true;
            _parts.Add(p);
        }

        private void Apply() {
            float t = T;
            float z = Sample(Def.Camera.TryGetValue("z", out var zt) ? zt : null, t, 1f);
            float cx = Sample(Def.Camera.TryGetValue("x", out var xt) ? xt : null, t, 0f);
            float cy = Sample(Def.Camera.TryGetValue("y", out var yt) ? yt : null, t, 0f);
            float hx = Mathf.Max(0f, Def.BoundsW / 2f - ViewW / 2f / z), hy = Mathf.Max(0f, Def.BoundsH / 2f - ViewH / 2f / z);
            cx = Mathf.Clamp(cx, -hx, hx); cy = Mathf.Clamp(cy, -hy, hy);
            float sx = 0f, sy = 0f;
            if (t < _shakeUntil && _shakeDur > 0f) {
                float k = (_shakeUntil - t) / _shakeDur;
                sx = ((float)_rng.NextDouble() * 2f - 1f) * _shakeAmp * k; sy = ((float)_rng.NextDouble() * 2f - 1f) * _shakeAmp * k;
            }
            float s = _base * z;
            World.localScale = new Vector3(s, s, 1f);
            World.localPosition = new Vector3((-cx + sx) * s, (-cy + sy) * s, 1f);

            var map = new Dictionary<string, Xf>();
            void Put(ObjDef o, Transform tr, SpriteRenderer sr, int i, Xf W) {
                tr.localPosition = new Vector3(W.X, W.Y, -i * 0.0005f);
                tr.localEulerAngles = new Vector3(0f, 0f, W.Rot);
                tr.localScale = new Vector3(W.Sx, W.Sy, 1f);
                if (sr != null) {
                    float a = Mathf.Clamp01(W.A);
                    sr.enabled = a > 0.003f;
                    sr.color = new Color(o.Tint.r * W.B, o.Tint.g * W.B, o.Tint.b * W.B, a);
                }
            }
            foreach (var (o, tr, sr, i) in _objs) {
                if (o.Parent == "player") continue;
                var W = Compose(Local(o, t), o.Parent != null && map.TryGetValue(o.Parent, out var pw) ? pw : (Xf?)null);
                if (!o.Screen && o.Parent == null && o.Depth != 1f) { W.X += cx * (1f - o.Depth); W.Y += cy * (1f - o.Depth); }
                map[o.Id] = W;
                Put(o, tr, sr, i, W);
            }
            var pl = Def.Player;
            var PW = Compose(Local(pl, t), pl.Parent != null && map.TryGetValue(pl.Parent, out var ppw) ? ppw : (Xf?)null);
            bool hidden = Def.Skip || P == null || (!float.IsNaN(pl.Hide) && t >= pl.Hide);
            if (hidden) PW.A = 0f;
            map["player"] = PW;
            if (P != null) {
                if (hidden && !_pHidden) { _pHidden = true; P.gameObject.SetActive(false); }
                if (!hidden) {
                    P.localScale = new Vector3(_pBase.x * PW.Sx, _pBase.y * PW.Sy, _pBase.z);
                    P.localEulerAngles = new Vector3(0f, 0f, PW.Rot);
                    float r = PW.Rot * Mathf.Deg2Rad, ox = _pOff.x * PW.Sx, oy = _pOff.y * PW.Sy;
                    P.localPosition = new Vector3(PW.X - (ox * Mathf.Cos(r) - oy * Mathf.Sin(r)), PW.Y - (ox * Mathf.Sin(r) + oy * Mathf.Cos(r)), P.localPosition.z);
                    float a = Mathf.Clamp01(PW.A);
                    foreach (var pr in _pRenderers) if (pr != null) { var c = pr.color; if (Mathf.Abs(c.a - a) > 0.004f) pr.color = new Color(c.r, c.g, c.b, a); }
                }
            }
            foreach (var (o, tr, sr, i) in _objs) {
                if (o.Parent != "player") continue;
                var W = Compose(Local(o, t), PW);
                map[o.Id] = W;
                Put(o, tr, sr, i, W);
            }
            foreach (var p in _parts) {
                var em = p.Em;
                float u = p.Age / p.Life;
                float a = em.A * Mathf.Min(1f, Mathf.Min(u / Mathf.Max(1e-3f, em.Fade[0]), (1f - u) / Mathf.Max(1e-3f, em.Fade[1])));
                float k = p.Sz / Mathf.Max(0.001f, Mathf.Max(p.Size.x, p.Size.y)) * p.Scale;
                float rot = p.Rot;
                if (em.Stretch && (p.Vx != 0f || p.Vy != 0f)) rot = Mathf.Atan2(p.Vx, -p.Vy) * Mathf.Rad2Deg;
                var tr = p.R.transform;
                tr.localPosition = new Vector3(p.X, p.Y, -0.3f);
                tr.localEulerAngles = new Vector3(0f, 0f, rot);
                tr.localScale = new Vector3(k, k, 1f);
                p.R.color = new Color(em.Tint.r, em.Tint.g, em.Tint.b, Mathf.Clamp01(a));
            }
            // Blitz und Balken
            float fk = 1f - (t - _flashAt) / Mathf.Max(0.001f, _flashDur);
            _flash.color = fk > 0f && fk <= 1f ? new Color(_flashCol.r, _flashCol.g, _flashCol.b, _flashA * fk) : Color.clear;
            float bi = Ease("out", Mathf.Clamp01(t / Mathf.Max(0.01f, Doc.In)));
            _top.transform.localScale = new Vector3(40f / 0.16f, Doc.Top * bi / 0.16f, 1f);
            _bot.transform.localScale = new Vector3(40f / 0.16f, Doc.Bottom * bi / 0.16f, 1f);
        }

        public void Release() {
            try { if (World != null) Object.Destroy(World.gameObject); } catch { }
            try { if (Screen != null) Object.Destroy(Screen.gameObject); } catch { }
            foreach (var tex in _textures) if (tex != null) Object.Destroy(tex);
            _textures.Clear();
            _sprites.Clear();
        }
    }

    // ================================================================== Ablauf im ExileController

    /// <summary>Koroutine: Einblenden, Szene, Impostor-Zeile, Ausblenden, WrapUp (Airship: WrapUpAndSpawn).</summary>
    public static IEnumerator Play(Run run) {
        var ec = run.Ec;
        var hud = HudManager.Instance;
        if (hud != null) hud.StartCoroutine(hud.CoFadeFullScreen(Color.black, Color.clear, 0.2f, false));
        ec.StartCoroutine(ec.HandleText(run.Def.TextAt, run.Def.TextDur));
        float len = run.Def.Length;
        while (run.T < len) {
            try { run.Step(Mathf.Min(0.05f, Time.deltaTime)); }
            catch (Exception e) { Warn($"eject: step failed: {e.Message}"); break; }
            yield return null;
        }
        bool imp = false;
        try { imp = ec.initData != null && ec.initData.confirmImpostor && ec.ImpostorText != null && ec.initData.networkedPlayer != null; } catch { }
        if (imp) ec.ImpostorText.gameObject.SetActive(true);
        float extra = imp ? 1.6f : 0.6f;
        for (float u = 0f; u < extra; u += Time.deltaTime) {
            if (imp) {
                float k = Mathf.Clamp01(u / 0.3f);
                float sc = k < 1f ? Mathf.Lerp(0.2f, 1.1f, k) : Mathf.Lerp(1.1f, 1f, Mathf.Clamp01((u - 0.3f) / 0.2f));
                ec.ImpostorText.transform.localScale = Vector3.one * sc;
            }
            try { run.Step(Mathf.Min(0.05f, Time.deltaTime)); } catch { }
            yield return null;
        }
        if (hud != null) hud.StartCoroutine(hud.CoFadeFullScreen(Color.clear, Color.black, 0.2f, false));
        for (float u = 0f; u < 0.25f; u += Time.deltaTime) yield return null;
        run.Release();
        var air = ec.TryCast<AirshipExileController>();
        if (air != null) ec.StartCoroutine(air.WrapUpAndSpawn());
        else ec.WrapUp();
    }
}

// ====================================================================== Klang (wie im Kino)

internal static class EjectSynth {
    private const int Sr = 22050;
    private static readonly Dictionary<string, AudioClip> Clips = new();
    private static int _seed;
    private static float N() { _seed = (int)((_seed * 1103515245L + 12345L) & 0x7fffffff); return _seed / (float)0x7fffffff * 2f - 1f; }
    private static float[] Brown(int n, float a, float gain) { var s = new float[n]; float y = 0f; for (int i = 0; i < n; i++) { y += a * (N() - y); s[i] = y * gain; } return s; }

    public static void Play(string name, float vol) {
        try {
            if (SoundManager.Instance == null) return;
            if (!Clips.TryGetValue(name, out var c) || c == null) {
                var s = Make(name);
                if (s == null || s.Length < 2) return;
                c = AudioClip.Create("eject_" + name, s.Length, 1, Sr, false);
                c.hideFlags |= HideFlags.HideAndDontSave;
                c.SetData(s, 0);
                Clips[name] = c;
            }
            SoundManager.Instance.PlaySound(c, false, vol * 0.8f);
        } catch (Exception e) { EjectEngine.Warn($"eject: sound {name}: {e.Message}"); }
    }

    private static float[] Make(string name) {
        _seed = 1234;
        const float PI = Mathf.PI;
        float[] s;
        switch (name) {
            case "whoosh": { int n = (int)(Sr * 0.7f); s = new float[n]; float y = 0f; for (int i = 0; i < n; i++) { float t = (float)i / n, a = 0.02f + 0.25f * Mathf.Sin(PI * t); y += a * (N() - y); s[i] = y * 2.2f * Mathf.Sin(PI * t); } return s; }
            case "thud": { int n = (int)(Sr * 0.6f); s = Brown(n, 0.05f, 0.8f); for (int i = 0; i < n; i++) { float t = (float)i / Sr; s[i] = (s[i] * 0.4f + Mathf.Sin(2f * PI * (70f - 40f * t) * t)) * Mathf.Exp(-t * 9f) * 0.9f; } return s; }
            case "splash": { int n = Sr; s = Brown(n, 0.35f, 0.9f); for (int i = 0; i < n; i++) { float t = (float)i / Sr; s[i] *= Mathf.Exp(-t * 5f); if ((N() + 1f) / 2f < 0.0012f) { float f = 600f + (N() + 1f) / 2f * 900f; for (int j = 0; j < 500 && i + j < n; j++) s[i + j] += Mathf.Sin(2f * PI * f * (1f + j / 900f) * j / Sr) * 0.12f * (1f - j / 500f); } } return s; }
            case "slam": { s = Make("thud"); _seed = 99; for (int j = 0; j < 260; j++) s[j] += N() * 0.7f * (1f - j / 260f); return s; }
            case "roar": { int n = (int)(Sr * 1.6f); s = Brown(n, 0.08f, 1.4f); for (int i = 0; i < n; i++) { float t = (float)i / Sr, f = 95f + 25f * Mathf.Sin(t * 3f) + 8f * Mathf.Sin(t * 37f), saw = (t * f) % 1f * 2f - 1f, env = Mathf.Clamp01(t / 0.15f) * Mathf.Clamp01((1.6f - t) / 0.6f); s[i] = Mathf.Clamp((saw * 0.55f + s[i] * 0.6f) * env, -1f, 1f); } return s; }
            case "grind": { int n = (int)(Sr * 0.9f); s = Brown(n, 0.12f, 1.2f); for (int i = 0; i < n; i++) { float t = (float)i / Sr; s[i] *= (0.6f + 0.4f * Mathf.Sin(t * 60f)) * Mathf.Sin(PI * t / 0.9f); } return s; }
            case "growl": { int n = (int)(Sr * 1.2f); s = new float[n]; for (int i = 0; i < n; i++) { float t = (float)i / Sr, f = 60f + 10f * Mathf.Sin(t * 5f); s[i] = (Mathf.Sin(2f * PI * f * t) + 0.5f * Mathf.Sin(2f * PI * f * 2.02f * t)) * 0.35f * (0.6f + 0.4f * Mathf.Sin(t * 23f)) * Mathf.Sin(PI * t / 1.2f); } return s; }
            case "rush": { s = Brown(Sr * 4, 0.25f, 0.9f); for (int i = 0; i < s.Length; i++) s[i] *= Mathf.Clamp01((float)i / Sr) * Mathf.Clamp01((float)(s.Length - i) / Sr); return s; }
            case "thunder": { int n = Sr * 4; s = Brown(n, 0.015f, 6f); for (int i = 0; i < n; i++) { float t = (float)i / Sr, env = t < 0.05f ? t / 0.05f : Mathf.Exp(-(t - 0.05f) * 1.1f) * (0.7f + 0.3f * Mathf.Sin(t * 9f)); s[i] = Mathf.Clamp(s[i] * env, -1f, 1f); } return s; }
            case "owl": {
                int n = (int)(Sr * 1.6f); s = new float[n];
                void Hoot(float at, float len, float f) { for (int i = (int)(at * Sr); i < Mathf.Min(n, (int)((at + len) * Sr)); i++) { float t = (float)i / Sr - at, k = t / len; s[i] += Mathf.Sin(2f * PI * f * (1f - 0.08f * k) * t) * Mathf.Sin(PI * k) * 0.5f; } }
                Hoot(0f, 0.35f, 420f); Hoot(0.55f, 0.12f, 440f); Hoot(0.75f, 0.6f, 410f); return s;
            }
            case "twig": { int n = Sr / 3; s = new float[n]; for (int c = 0; c < 3; c++) { int at = c * Sr / 14; for (int j = 0; j < 400 && at + j < n; j++) s[at + j] += N() * (1f - j / 400f) * 0.6f; } return s; }
            case "creak": { int n = Sr; s = new float[n]; for (int i = 0; i < n; i++) { float t = (float)i / Sr, f = 90f + 40f * Mathf.Sin(t * 3f), saw = (t * f) % 1f * 2f - 1f; s[i] = saw * 0.25f * Mathf.Sin(PI * t); } return s; }
            case "fire": { int n = Sr * 5; s = Brown(n, 0.06f, 1.2f); for (int i = 0; i < n; i++) if ((N() + 1f) / 2f < 0.0015f) { int len = 60 + (int)((N() + 1f) / 2f * 200f); for (int j = 0; j < len && i + j < n; j++) s[i + j] += N() * 0.6f * (1f - (float)j / len); } return s; }
            case "step": { int n = (int)(Sr * 0.18f); s = new float[n]; for (int i = 0; i < n; i++) { float t = (float)i / Sr; s[i] = (Mathf.Sin(2f * PI * (90f - 60f * t) * t) * Mathf.Exp(-t * 28f) + N() * 0.25f * Mathf.Exp(-t * 60f)) * 0.8f; } return s; }
            case "zap": { int n = (int)(Sr * 0.22f); s = new float[n]; float f = 300f; for (int i = 0; i < n; i++) { float t = (float)i / Sr; if (i % 441 == 0) f = 180f + (N() + 1f) / 2f * 900f; float sq = Mathf.Sin(2f * PI * f * t) > 0f ? 1f : -1f; s[i] = (sq * 0.28f + N() * 0.18f) * Mathf.Exp(-t * 9f) * ((i >> 6) % 3 == 0 ? 0.2f : 1f); } return s; }
            case "click": { int n = (int)(Sr * 0.12f); s = new float[n]; for (int i = 0; i < n; i++) { float t = (float)i / Sr; s[i] = (i < 90 ? N() * 0.8f * (1f - i / 90f) : 0f) + Mathf.Sin(2f * PI * 1800f * t) * 0.35f * Mathf.Exp(-t * 60f); } return s; }
            default: return null;
        }
    }
}

}
