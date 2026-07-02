// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Collector relics - the world objects the Collector hunts.
 *
 * Rendering uses the proven pooled-SpriteRenderer technique (procedural sprites; runtime
 * ParticleSystems are unreliable in the IL2CPP build): each relic is a small faceted gold crystal
 * with a soft glow and orbiting sparkles, gently bobbing.
 *
 * Visibility per frame (Tick):
 *   - the Collector always sees relics at full strength (also while dead - it can still watch);
 *   - dead players see them (ghosts see everything);
 *   - Impostors see a FAINT shimmer within the configured sense radius (option) - they can camp them;
 *   - everyone else sees nothing.
 *
 * Positions come from the host (RPC), anchored near task consoles - available on EVERY map
 * (Skeld/Mira/Polus/Airship/Fungle/Submerged) - excluding critical-sabotage consoles and the
 * emergency button (SaboteurTrap.NearCriticalSpot, the same map-agnostic rule the traps use).
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {
    public static class CollectorRelics {
        private static readonly Color CrystalGold = new Color(1f, 0.84f, 0.32f);
        private static readonly Color GlowGold = new Color(1f, 0.94f, 0.63f);

        public sealed class Relic {
            public int id;
            public Vector2 pos;
            public GameObject go;
            public SpriteRenderer body;
            public SpriteRenderer glow;
            public SpriteRenderer[] sparkles;
        }

        private static readonly List<Relic> relics = new();
        private static Sprite crystalSprite;
        private static Sprite dotSprite;

        // Short-lived pickup bursts (gold sparkle explosion where a relic was collected).
        private sealed class Burst { public GameObject go; public SpriteRenderer[] parts; public float start; public int seed; }
        private static readonly List<Burst> bursts = new();

        public static int RemainingCount => relics.Count;

        public static void SpawnAll(List<Vector2> positions) {
            try {
                Clear();
                Ensure();
                for (int i = 0; i < positions.Count; i++) {
                    var r = new Relic { id = i, pos = positions[i] };
                    r.go = new GameObject($"CollectorRelic{i}");
                    r.go.transform.position = new Vector3(r.pos.x, r.pos.y, r.pos.y / 1000f);

                    var glowGo = new GameObject("glow");
                    glowGo.transform.SetParent(r.go.transform, false);
                    r.glow = glowGo.AddComponent<SpriteRenderer>();
                    r.glow.sprite = dotSprite;
                    glowGo.transform.localScale = Vector3.one * 1.5f;

                    var bodyGo = new GameObject("crystal");
                    bodyGo.transform.SetParent(r.go.transform, false);
                    r.body = bodyGo.AddComponent<SpriteRenderer>();
                    r.body.sprite = crystalSprite;

                    r.sparkles = new SpriteRenderer[3];
                    for (int s = 0; s < 3; s++) {
                        var sg = new GameObject($"spark{s}");
                        sg.transform.SetParent(r.go.transform, false);
                        r.sparkles[s] = sg.AddComponent<SpriteRenderer>();
                        r.sparkles[s].sprite = dotSprite;
                        sg.transform.localScale = Vector3.one * 0.12f;
                    }

                    SetAlpha(r, 0f); // invisible until Tick decides
                    relics.Add(r);
                }
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Collector] {relics.Count} relics spawned.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Collector] relic spawn failed: {e}");
            }
        }

        public static Relic NearestRelic(Vector2 pos, float maxDist) {
            Relic best = null;
            float bestD = maxDist;
            foreach (var r in relics) {
                float d = Vector2.Distance(pos, r.pos);
                if (d < bestD) { bestD = d; best = r; }
            }
            return best;
        }

        public static Relic ById(int id) => relics.Find(r => r.id == id);

        public static void Collect(int id) {
            var r = ById(id);
            if (r == null) return;
            SpawnPickupBurst(r.pos);
            if (r.go != null) UnityEngine.Object.Destroy(r.go);
            relics.Remove(r);
        }

        public static void Clear() {
            foreach (var r in relics) if (r.go != null) UnityEngine.Object.Destroy(r.go);
            relics.Clear();
            foreach (var b in bursts) if (b.go != null) UnityEngine.Object.Destroy(b.go);
            bursts.Clear();
        }

        // ---- per-frame: visibility + idle animation + pickup bursts ----

        public static void Tick() {
            try {
                float now = Time.time;
                bool anyVisible = relics.Count > 0 || bursts.Count > 0;
                if (!anyVisible) return;

                float viewerAlpha = ViewerAlpha();
                foreach (var r in relics) {
                    if (r.go == null) continue;
                    // Idle bob + glow pulse + orbiting sparkles.
                    float bob = Mathf.Sin(now * 1.6f + r.id * 1.3f) * 0.06f;
                    r.body.transform.localPosition = new Vector3(0f, 0.12f + bob, 0f);
                    r.glow.transform.localPosition = new Vector3(0f, 0.10f + bob * 0.6f, 0.01f);

                    float a = viewerAlpha;
                    if (a > 0f && viewerAlpha < 0.9f) {
                        // Impostor sense: fade with distance inside the radius.
                        float dist = PlayerControl.LocalPlayer != null
                            ? Vector2.Distance(PlayerControl.LocalPlayer.GetTruePosition(), r.pos) : float.MaxValue;
                        float radius = Collector.SenseRadius?.getFloat() ?? 5f;
                        a = dist > radius ? 0f : viewerAlpha * (1f - dist / radius);
                    }

                    r.body.color = Tint(CrystalGold, a);
                    r.glow.color = Tint(GlowGold, a * (0.28f + 0.12f * Mathf.Sin(now * 3f + r.id)));
                    for (int s = 0; s < r.sparkles.Length; s++) {
                        float ang = now * (1.2f + s * 0.4f) + s * 2.1f + r.id;
                        float rad = 0.30f + 0.05f * Mathf.Sin(now * 2f + s);
                        r.sparkles[s].transform.localPosition =
                            new Vector3(Mathf.Cos(ang) * rad, 0.15f + bob + Mathf.Sin(ang) * rad * 0.5f, -0.01f);
                        r.sparkles[s].color = Tint(Color.white, a * (0.4f + 0.6f * Mathf.Abs(Mathf.Sin(now * 6f + s * 2f))));
                    }
                }

                for (int i = bursts.Count - 1; i >= 0; i--) {
                    var b = bursts[i];
                    float t = (now - b.start) / 0.9f;
                    if (b.go == null || t >= 1f) {
                        if (b.go != null) UnityEngine.Object.Destroy(b.go);
                        bursts.RemoveAt(i);
                        continue;
                    }
                    for (int p = 0; p < b.parts.Length; p++) {
                        var sr = b.parts[p];
                        if (sr == null) continue;
                        float u = ((b.seed + p * 37) % 100) / 100f;
                        float ang = u * Mathf.PI * 2f;
                        float ease = 1f - (1f - t) * (1f - t);
                        float rad = 0.1f + ease * (0.6f + 0.5f * u);
                        sr.transform.localPosition = new Vector3(Mathf.Cos(ang) * rad, Mathf.Sin(ang) * rad + ease * 0.3f, 0f);
                        sr.transform.localScale = Vector3.one * (0.10f + 0.08f * u) * (1f - t * 0.5f);
                        sr.color = Tint(p % 2 == 0 ? CrystalGold : Color.white, (1f - t) * 0.9f);
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Collector] relic tick failed: {e.Message}");
            }
        }

        // Full visibility for the Collector and for dead viewers; faint base alpha for impostors when
        // the sense option is on (distance fade happens per relic); nothing for everyone else.
        private static float ViewerAlpha() {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.Data == null) return 0f;
            if (Collector.IsLocalCollector()) return 1f;
            if (me.Data.IsDead) return 0.85f;
            if (me.Data.Role != null && me.Data.Role.IsImpostor
                && (Collector.ImpostorsSense?.getBool() ?? false)) return 0.30f;
            return 0f;
        }

        private static void SpawnPickupBurst(Vector2 at) {
            try {
                Ensure();
                var b = new Burst {
                    go = new GameObject("RelicBurst"),
                    parts = new SpriteRenderer[12],
                    start = Time.time,
                    seed = (int)(at.x * 13 + at.y * 7)
                };
                b.go.transform.position = new Vector3(at.x, at.y, -1.2f);
                for (int i = 0; i < b.parts.Length; i++) {
                    var go = new GameObject($"g{i}");
                    go.transform.SetParent(b.go.transform, false);
                    b.parts[i] = go.AddComponent<SpriteRenderer>();
                    b.parts[i].sprite = dotSprite;
                }
                bursts.Add(b);
            } catch { }
        }

        private static void SetAlpha(Relic r, float a) {
            r.body.color = Tint(CrystalGold, a);
            r.glow.color = Tint(GlowGold, a * 0.3f);
            foreach (var s in r.sparkles) s.color = Tint(Color.white, a);
        }

        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));

        private static void Ensure() {
            if (dotSprite == null) dotSprite = BuildDot();
            if (crystalSprite == null) crystalSprite = BuildCrystal();
        }

        private static Sprite BuildDot() {
            const int n = 24;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            float c = (n - 1) / 2f;
            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++) {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (c + 1f);
                    float alpha = Mathf.Clamp01(1f - d);
                    alpha *= alpha;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            var s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            s.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            return s;
        }

        // Small faceted crystal: elongated hexagon, brighter left half, darker right half, white core line.
        private static Sprite BuildCrystal() {
            const int w = 22, h = 32;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            float cx = (w - 1) / 2f, cy = (h - 1) / 2f;
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++) {
                    // Hexagon test: |dx|/a + |dy|/b <= 1 with a slimmer top/bottom taper.
                    float dx = Mathf.Abs(x - cx) / (w * 0.45f);
                    float dy = Mathf.Abs(y - cy) / (h * 0.52f);
                    bool inside = dx + dy * 0.55f <= 1f && dy <= 1f;
                    if (!inside) { tex.SetPixel(x, y, Color.clear); continue; }
                    float bright = x < cx ? 1.0f : 0.78f;                       // left facet lighter
                    if (Mathf.Abs(x - cx) < 1.2f) bright = 1.15f;               // core line
                    var col = new Color(Mathf.Clamp01(1f * bright), Mathf.Clamp01(0.85f * bright),
                        Mathf.Clamp01(0.35f * bright), 1f);
                    tex.SetPixel(x, y, col);
                }
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            var s = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 34f);
            s.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            return s;
        }
    }
}
