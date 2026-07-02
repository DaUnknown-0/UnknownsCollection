// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Poltergeist visual effects. Same reliable technique as TeslaParticles (pooled SpriteRenderers with
 * procedurally generated sprites - runtime ParticleSystems often fail to render in the IL2CPP build),
 * but layered: a soft radial dot AND an elongated streak sprite (both from the shared UCFx sprite
 * cache), combined into
 *
 *   - DoorBurst:      violet/cyan wisps imploding into the door plus radial streaks (the "slam"),
 *   - HexBurst:        stars spiraling up around the hexed player (cast),
 *   - HexEndBurst:     a quick, mode-tinted dissolve when a hex expires on its own,
 *   - HexIndicator:    a small orbiting halo that marks an ACTIVE hex - gated to the hexed player, the
 *                      local Poltergeist and dead players only (see TickHexIndicators).
 *   - HexVignette:     a faint pulsing screen-edge tint while the LOCAL player is Blind/Night-Vision
 *                      hexed (self-only, own HudManager.FullScreen-style overlay).
 *   - Poof:            an airy white puff that expands, drifts up and dissolves (manifest end/timeout),
 *   - ManifestReveal:  a sharper outward burst for the "that was a ghost!" kill-reveal moment.
 *   - Channel:         a pulsing cyan orb ring while the Ghost Hand holds a reactor console, fading out
 *                      (instead of a hard cut) when released.
 *   - Aura:            faint wisps orbiting the Poltergeist, shown ONLY to the Poltergeist itself.
 *   - Denied-flash:    a brief red flash on a ghost ability button whose click failed a gameplay
 *                      precondition (energy/target/etc, NOT simply cooldown) - see RegisterDeniedFlash.
 *
 * All effects are droven from Tick(), called every frame by Poltergeist's HudManager.Update patch.
 * One-shot effects own a small GameObject that is destroyed when their lifetime ends.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;

namespace UnknownsCollection {
    public static class PoltergeistFx {
        private static readonly Color Violet = new Color(0.62f, 0.42f, 1f);
        private static readonly Color Cyan = new Color(0.47f, 0.92f, 1f);
        private static readonly Color White = new Color(0.94f, 0.97f, 1f);
        private static readonly Color DeepViolet = new Color(0.30f, 0.10f, 0.46f);  // Hex: Blind
        private static readonly Color IcyWhite = new Color(0.78f, 0.96f, 1f);       // Hex: Night Vision

        // Idempotent: also invoked explicitly by Poltergeist.ResetAll() (resetVariables postfix) in
        // addition to this registration, so Clear() must tolerate being called twice per reset.
        static PoltergeistFx() {
            UCFx.RegisterReset(Clear);
        }

        // ---- One-shot effect bookkeeping ----
        private sealed class Effect {
            public GameObject go;
            public SpriteRenderer[] parts;
            public float start;
            public float life;
            public int kind; // 0 door, 1 hex, 2 poof, 3 manifest-reveal, 4 hex-end
            public Vector2 origin;
            public PlayerControl follow; // hex follows its target
            public int seed;
            public int extra; // kind 4: the hex mode that just expired (tint pick)
        }
        private static readonly List<Effect> effects = new();

        // ---- Continuous effects ----
        private static GameObject auraGo;
        private static SpriteRenderer[] auraParts;
        private static GameObject channelGo;
        private static SpriteRenderer[] channelParts;
        private static Vector2 channelPos;
        private static bool channelOn;
        private const float ChannelFadeDuration = 0.3f;
        private static float channelFadeStart = -1f; // -1 = not fading

        public static void SpawnDoorBurst(Vector2 at) => Spawn(0, at, 1.1f, 18, null);
        public static void SpawnHexBurst(PlayerControl target) {
            if (target != null) Spawn(1, target.GetTruePosition(), 1.2f, 14, target);
        }
        public static void SpawnPoof(Vector2 at) => Spawn(2, at, 0.95f, 20, null);
        // Manifest kill-reveal: sharper, punchier outward burst than the ordinary Poof (see Animate case 3).
        public static void SpawnManifestReveal(Vector2 at) => Spawn(3, at, 0.85f, 24, null);
        // Quick mode-tinted dissolve when a hex expires on its own (see Animate case 4).
        public static void SpawnHexEndBurst(Vector2 at, int hexMode) => Spawn(4, at, 0.5f, 10, null, hexMode);

        // Turning OFF while the channel was on starts a short fade-out instead of an instant cut
        // (TickChannel below keeps rendering, ring alpha ramping to 0 over ChannelFadeDuration, at the
        // LAST known channel position - so `at` is only meaningful while turning it ON).
        public static void SetChannel(Vector2 at, bool on) {
            if (on) {
                channelPos = at;
                channelOn = true;
                channelFadeStart = -1f; // re-engaged before a previous fade finished -> cancel it
            } else if (channelOn) {
                channelOn = false;
                channelFadeStart = Time.time;
            }
        }

        public static void Tick() {
            try {
                float now = Time.time;
                for (int i = effects.Count - 1; i >= 0; i--) {
                    var e = effects[i];
                    if (e.go == null || now - e.start >= e.life) {
                        if (e.go != null) UnityEngine.Object.Destroy(e.go);
                        effects.RemoveAt(i);
                        continue;
                    }
                    Animate(e, (now - e.start) / e.life);
                }
                TickAura();
                TickChannel();
                TickHexIndicators();
                TickHexVignette();
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Poltergeist] fx tick failed: {ex.Message}");
            }
        }

        public static void Clear() {
            foreach (var e in effects) if (e.go != null) UnityEngine.Object.Destroy(e.go);
            effects.Clear();
            if (auraGo != null) { UnityEngine.Object.Destroy(auraGo); auraGo = null; auraParts = null; }
            if (channelGo != null) { UnityEngine.Object.Destroy(channelGo); channelGo = null; channelParts = null; }
            channelOn = false;
            channelFadeStart = -1f;
            foreach (var go in hexIndicatorGo.Values) if (go != null) UnityEngine.Object.Destroy(go);
            hexIndicatorGo.Clear();
            hexIndicatorParts.Clear();
            if (hexVignette != null) { UnityEngine.Object.Destroy(hexVignette.gameObject); hexVignette = null; }
            deniedFlashUntil.Clear();
            // deniedButtons deliberately kept - see RegisterDeniedFlash (same resetVariables-after-
            // HudManager.Start timing trap as the CustomButton statics in Poltergeist.cs).
        }

        // ---- one-shots ----

        private static void Spawn(int kind, Vector2 at, float life, int count, PlayerControl follow, int extra = 0) {
            try {
                var e = new Effect {
                    // layer 11 like every TOR world object - the ship camera does not render Default
                    go = new GameObject("PoltergeistFx") { layer = 11 },
                    parts = new SpriteRenderer[count],
                    start = Time.time,
                    life = life,
                    kind = kind,
                    origin = at,
                    follow = follow,
                    extra = extra,
                    seed = UnityEngine.Random.Range(0, 10000)
                };
                e.go.transform.position = new Vector3(at.x, at.y, -1.5f);
                for (int i = 0; i < count; i++) {
                    var go = new GameObject($"p{i}") { layer = 11 };
                    go.transform.SetParent(e.go.transform);
                    var sr = go.AddComponent<SpriteRenderer>();
                    // Mix dots and streaks; streaks sell motion, dots sell volume. The manifest-reveal
                    // burst (kind 3) swaps dots for the harder Spark sprite + additive blending - a
                    // punchier, more electric read for the "that was a ghost!" moment.
                    bool isStreak = i % 3 == 0;
                    sr.sprite = isStreak ? UCFx.Streak : (kind == 3 ? UCFx.Spark : UCFx.Dot);
                    if (kind == 3 && !isStreak) UCFx.TryMakeAdditive(sr);
                    e.parts[i] = sr;
                }
                effects.Add(e);
                Animate(e, 0f);
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Poltergeist] fx spawn failed: {ex.Message}");
            }
        }

        private static void Animate(Effect e, float t) {
            if (e.follow != null && e.follow.gameObject != null)
                e.go.transform.position = new Vector3(e.follow.GetTruePosition().x, e.follow.GetTruePosition().y, -1.5f);

            for (int i = 0; i < e.parts.Length; i++) {
                var sr = e.parts[i];
                if (sr == null) continue;
                float u = Hash(e.seed + i);          // stable per-particle random 0..1
                float v = Hash(e.seed + i * 7 + 3);
                float ang = u * Mathf.PI * 2f;

                switch (e.kind) {
                    case 0: { // Door slam: wisps rush INWARD (1.4 -> 0.1), streaks flash outward at impact
                        bool isStreak = i % 3 == 0;
                        if (isStreak) {
                            float flash = Mathf.Clamp01(1f - Mathf.Abs(t - 0.25f) * 6f);
                            float r = 0.25f + t * 1.1f;
                            sr.transform.localPosition = Rot(ang, r);
                            sr.transform.localRotation = Quaternion.Euler(0, 0, ang * Mathf.Rad2Deg);
                            sr.transform.localScale = new Vector3(0.65f, 0.10f, 1f);
                            sr.color = Tint(Cyan, flash * 0.9f);
                        } else {
                            float ease = 1f - (1f - t) * (1f - t);   // ease-out toward the door
                            float r = Mathf.Lerp(1.4f * (0.5f + v), 0.08f, ease);
                            float swirl = ang + t * (2.5f + v * 2f);
                            sr.transform.localPosition = Rot(swirl, r);
                            sr.transform.localScale = Vector3.one * (0.24f + 0.12f * v) * (1f - t * 0.5f);
                            sr.color = Tint(Color.Lerp(Violet, Cyan, v), (1f - t) * (0.55f + 0.45f * Flicker(i, 11f)));
                        }
                        break;
                    }
                    case 1: { // Hex: sparkles spiral upward around the target
                        float rise = t * (0.9f + 0.5f * v);
                        float r = 0.45f * (1f - t * 0.35f);
                        float swirl = ang + t * (5f + v * 3f);
                        sr.transform.localPosition = new Vector3(Mathf.Cos(swirl) * r, rise - 0.3f + Mathf.Sin(swirl) * r * 0.35f, 0f);
                        sr.transform.localScale = Vector3.one * (0.16f + 0.10f * v) * (1f - t * t);
                        sr.color = Tint(Color.Lerp(Cyan, White, u), (1f - t) * (0.6f + 0.4f * Flicker(i, 14f)));
                        if (i % 3 == 0) { // streaks act as tiny star twinkles here
                            sr.transform.localScale = new Vector3(0.30f * (1f - t), 0.07f, 1f);
                            sr.transform.localRotation = Quaternion.Euler(0, 0, u * 360f + t * 90f);
                        }
                        break;
                    }
                    case 3: { // Manifest kill-reveal: sharp outward burst - lands harder than a soft poof
                        bool isStreak = i % 3 == 0;
                        float ease = 1f - (1f - t) * (1f - t);
                        if (isStreak) {
                            float r = 0.05f + ease * 1.35f;
                            sr.transform.localPosition = Rot(ang, r);
                            sr.transform.localRotation = Quaternion.Euler(0, 0, ang * Mathf.Rad2Deg);
                            sr.transform.localScale = new Vector3(0.55f * (1f - t * 0.4f), 0.09f, 1f);
                            sr.color = Tint(White, (1f - t) * 0.95f);
                        } else {
                            float r = 0.05f + ease * (0.85f + 0.4f * v);
                            sr.transform.localPosition = Rot(ang + v * 0.5f, r);
                            float coreFlash = Mathf.Clamp01(1f - t * 5f);
                            sr.transform.localScale = Vector3.one * (0.20f + 0.14f * v) * (1f - t * 0.3f);
                            sr.color = Tint(Color.Lerp(Violet, White, coreFlash), (1f - t) * (0.7f + 0.3f * Flicker(i, 16f)));
                        }
                        break;
                    }
                    case 4: { // Hex end: quick dissolve, tinted by the hex mode that just expired
                        Color tint = HexModeColor(e.extra);
                        bool isStreak = i % 3 == 0;
                        float r = 0.08f + t * (0.42f + 0.22f * v);
                        sr.transform.localPosition = Rot(ang, r);
                        if (isStreak) {
                            sr.transform.localRotation = Quaternion.Euler(0, 0, ang * Mathf.Rad2Deg);
                            sr.transform.localScale = new Vector3(0.28f * (1f - t), 0.06f, 1f);
                        } else {
                            sr.transform.localScale = Vector3.one * (0.16f + 0.08f * v) * (1f - t * 0.5f);
                        }
                        sr.color = Tint(Color.Lerp(tint, White, 0.3f), (1f - t) * (0.75f + 0.25f * Flicker(i, 12f)));
                        break;
                    }
                    default: { // Poof: puff expands, drifts up, dissolves
                        float ease = 1f - (1f - t) * (1f - t);
                        float r = 0.15f + ease * (0.7f + 0.5f * v);
                        var pos = Rot(ang, r);
                        pos.y += ease * 0.55f;                        // buoyant drift
                        sr.transform.localPosition = pos;
                        sr.transform.localScale = Vector3.one * (0.30f + 0.22f * v) * (0.6f + t * 0.8f);
                        sr.color = Tint(Color.Lerp(White, Cyan, v * 0.5f), (1f - ease) * 0.85f);
                        if (i % 3 == 0) sr.color = Tint(White, Mathf.Clamp01(1f - t * 4f)); // brief core flash
                        break;
                    }
                }
            }
        }

        // ---- continuous: aura around the local Poltergeist (only it sees this) ----

        private static void TickAura() {
            bool show = Poltergeist.IsLocalPoltergeist()
                        && PlayerControl.LocalPlayer != null
                        && PlayerControl.LocalPlayer.Data != null
                        && PlayerControl.LocalPlayer.Data.IsDead
                        && MeetingHud.Instance == null;
            if (!show) {
                if (auraGo != null) auraGo.SetActive(false);
                return;
            }
            if (auraGo == null) {
                auraGo = new GameObject("PoltergeistAura") { layer = 11 };
                auraParts = new SpriteRenderer[7];
                for (int i = 0; i < auraParts.Length; i++) {
                    var go = new GameObject($"a{i}") { layer = 11 };
                    go.transform.SetParent(auraGo.transform);
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = UCFx.Dot;
                    auraParts[i] = sr;
                }
            }
            auraGo.SetActive(true);
            var p = PlayerControl.LocalPlayer.GetTruePosition();
            auraGo.transform.position = new Vector3(p.x, p.y, -1.2f);
            float now = Time.time;
            for (int i = 0; i < auraParts.Length; i++) {
                float a = now * (0.8f + i * 0.13f) + i * 0.9f;
                float r = 0.42f + 0.10f * Mathf.Sin(now * 1.7f + i * 2.1f);
                auraParts[i].transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r * 0.75f + 0.05f, 0f);
                auraParts[i].transform.localScale = Vector3.one * (0.14f + 0.05f * Mathf.Sin(now * 2.3f + i));
                auraParts[i].color = Tint(Violet, 0.22f + 0.10f * Flicker(i, 5f));
            }
        }

        // ---- continuous: channel ring while the Ghost Hand holds a console ----

        private static void TickChannel() {
            bool fading = channelFadeStart >= 0f;
            if (!channelOn && !fading) {
                if (channelGo != null) channelGo.SetActive(false);
                return;
            }
            if (channelGo == null) {
                channelGo = new GameObject("PoltergeistChannel") { layer = 11 };
                channelParts = new SpriteRenderer[9];
                for (int i = 0; i < channelParts.Length; i++) {
                    var go = new GameObject($"c{i}") { layer = 11 };
                    go.transform.SetParent(channelGo.transform);
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = UCFx.Dot;
                    channelParts[i] = sr;
                }
            }
            channelGo.SetActive(true);
            channelGo.transform.position = new Vector3(channelPos.x, channelPos.y, -1.5f);
            float now = Time.time;

            // Released: ramp the ring's alpha down to 0 over ChannelFadeDuration instead of an instant
            // SetActive(false) cut, then finally hide it once the fade completes.
            float fadeMul = 1f;
            if (fading) {
                float ft = (now - channelFadeStart) / ChannelFadeDuration;
                if (ft >= 1f) {
                    channelFadeStart = -1f;
                    channelGo.SetActive(false);
                    return;
                }
                fadeMul = 1f - ft;
            }

            float pulse = 0.85f + 0.15f * Mathf.Sin(now * 5f);
            for (int i = 0; i < channelParts.Length; i++) {
                float a = now * 2.2f + i * Mathf.PI * 2f / channelParts.Length;
                float r = 0.34f * pulse;
                channelParts[i].transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                channelParts[i].transform.localScale = Vector3.one * 0.16f;
                channelParts[i].color = Tint(Cyan, (0.55f + 0.30f * Flicker(i, 9f)) * fadeMul);
            }
        }

        // ---- continuous: hex duration indicator - orbiting mode-tinted halo around a hexed player.
        // Gated (checked every frame, like TickAura) to only: the hexed target itself, the local
        // Poltergeist, and dead players (spectator-style visibility) - conservative by design decision,
        // so living onlookers get no extra tactical info beyond the already-public cast burst. ----

        private static readonly Dictionary<byte, GameObject> hexIndicatorGo = new();
        private static readonly Dictionary<byte, SpriteRenderer[]> hexIndicatorParts = new();

        private static Color HexModeColor(int mode) => mode switch {
            Poltergeist.HexBlind => DeepViolet,
            Poltergeist.HexNightVision => IcyWhite,
            _ => Cyan, // HexSpeed
        };

        private static void TickHexIndicators() {
            // Drop indicators for hexes that are no longer active.
            if (hexIndicatorGo.Count > 0) {
                List<byte> stale = null;
                foreach (var id in hexIndicatorGo.Keys)
                    if (!Poltergeist.hexes.ContainsKey(id)) (stale ??= new List<byte>()).Add(id);
                if (stale != null) {
                    foreach (var id in stale) {
                        if (hexIndicatorGo[id] != null) UnityEngine.Object.Destroy(hexIndicatorGo[id]);
                        hexIndicatorGo.Remove(id);
                        hexIndicatorParts.Remove(id);
                    }
                }
            }
            if (Poltergeist.hexes.Count == 0) return;

            var local = PlayerControl.LocalPlayer;
            bool localDead = local != null && local.Data != null && local.Data.IsDead;
            bool isGhost = Poltergeist.IsLocalPoltergeist();
            float now = Time.time;

            foreach (var kv in Poltergeist.hexes) {
                byte targetId = kv.Key;
                var target = Helpers.playerById(targetId);
                bool canSee = isGhost || localDead || (local != null && local.PlayerId == targetId);
                bool valid = canSee && target != null && target.Data != null && !target.Data.IsDead;

                if (!hexIndicatorGo.TryGetValue(targetId, out var go) || go == null) {
                    if (!valid) continue; // never seen by us and not visible right now - nothing to build
                    go = UCFx.NewFxRoot("PoltergeistHexIndicator", target.GetTruePosition(), -1.3f);
                    var parts = UCFx.MakeParts(go, 4, i => i == 0 ? UCFx.Ring : UCFx.Dot);
                    hexIndicatorGo[targetId] = go;
                    hexIndicatorParts[targetId] = parts;
                }

                if (!valid) { go.SetActive(false); continue; }

                go.SetActive(true);
                var pos = target.GetTruePosition();
                go.transform.position = new Vector3(pos.x, pos.y, -1.3f);
                Color tint = HexModeColor(kv.Value.mode);
                var parts2 = hexIndicatorParts[targetId];

                // Central pulsing halo.
                float pulse = 0.85f + 0.15f * Mathf.Sin(now * 2.4f);
                parts2[0].transform.localPosition = Vector3.zero;
                parts2[0].transform.localScale = Vector3.one * 0.62f * pulse;
                parts2[0].color = Tint(tint, 0.30f + 0.08f * Mathf.Sin(now * 2.4f));

                // Small accents orbiting the halo.
                for (int i = 1; i < parts2.Length; i++) {
                    float a = now * 1.1f + i * Mathf.PI * 2f / (parts2.Length - 1);
                    const float r = 0.50f;
                    parts2[i].transform.localPosition = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r * 0.7f + 0.05f, 0f);
                    parts2[i].transform.localScale = Vector3.one * 0.14f;
                    parts2[i].color = Tint(tint, 0.28f + 0.12f * Flicker(i, 4f));
                }
            }
        }

        // ---- continuous: screen-edge vignette while the LOCAL player is Blind/Night-Vision hexed.
        // Own overlay cloned from HudManager.FullScreen (same technique as UsefulTORStuff's
        // InvertVision) so this never fights TOR's own transient uses of the shared FullScreen renderer
        // (kill flash, sabotage flashes, ...). The vignette sprite is built to the SAME on-screen size
        // as the reference FullScreen sprite - derived from ITS world-unit size, not copied blindly -
        // so it always covers the visible camera area regardless of what texture TOR's FullScreen
        // happens to use. Self-only: gated every frame on the local player's OWN hexes entry. ----

        private static SpriteRenderer hexVignette;
        private static Sprite vignetteSprite;

        private static Sprite BuildVignetteSprite(Sprite reference) {
            if (vignetteSprite != null) return vignetteSprite;
            if (reference == null || reference.pixelsPerUnit <= 0f) return null;
            try {
                float worldW = reference.rect.width / reference.pixelsPerUnit;
                float worldH = reference.rect.height / reference.pixelsPerUnit;
                if (worldW <= 0f || worldH <= 0f) return null;

                const int texH = 96;
                int texW = Mathf.Clamp(Mathf.RoundToInt(texH * (worldW / worldH)), 2, 400);
                float ppu = texH / worldH;

                var tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false);
                for (int x = 0; x < texW; x++) {
                    for (int y = 0; y < texH; y++) {
                        float dx = (x - (texW - 1) / 2f) / (texW / 2f);
                        float dy = (y - (texH - 1) / 2f) / (texH / 2f);
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float alpha = Mathf.Clamp01((d - 0.58f) / 0.55f);
                        alpha *= alpha; // soft edge, eases in toward the corners
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                    }
                }
                tex.Apply();
                tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
                vignetteSprite = Sprite.Create(tex, new Rect(0, 0, texW, texH), new Vector2(0.5f, 0.5f), ppu);
                vignetteSprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Poltergeist] vignette sprite build failed: {e.Message}");
            }
            return vignetteSprite;
        }

        private static void TickHexVignette() {
            var hud = HudManager.Instance;
            if (hud == null || hud.FullScreen == null) return;

            // Stale overlay from a previous HudManager instance (round change) -> forget it, rebuild lazily.
            if (hexVignette != null && (hexVignette.gameObject == null || hexVignette.transform.parent != hud.transform))
                hexVignette = null;

            bool wantActive = false;
            var local = PlayerControl.LocalPlayer;
            if (local != null && local.Data != null && !local.Data.IsDead
                && Poltergeist.hexes.TryGetValue(local.PlayerId, out var hex)
                && (hex.mode == Poltergeist.HexBlind || hex.mode == Poltergeist.HexNightVision)) {
                wantActive = true;
            }

            if (!wantActive) {
                if (hexVignette != null && hexVignette.gameObject.activeSelf) hexVignette.gameObject.SetActive(false);
                return;
            }

            if (hexVignette == null) {
                var sprite = BuildVignetteSprite(hud.FullScreen.sprite);
                if (sprite == null) return; // no reference sprite to size against yet - degrade silently
                hexVignette = UnityEngine.Object.Instantiate(hud.FullScreen, hud.transform);
                hexVignette.name = "PoltergeistHexVignette";
                hexVignette.sprite = sprite;
                hexVignette.gameObject.SetActive(false);
            }

            if (!hexVignette.gameObject.activeSelf) hexVignette.gameObject.SetActive(true);
            hexVignette.enabled = true;
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 1.6f);
            float alpha = Mathf.Lerp(0.18f, 0.30f, pulse);
            hexVignette.color = new Color(Violet.r, Violet.g, Violet.b, alpha);
        }

        // ---- ability-denied feedback: a brief red flash on a ghost ability button whose click failed
        // a gameplay precondition (energy/target/etc) rather than simply being on cooldown. Buttons
        // register themselves once at creation; onClickEvent already gates the ENTIRE click (both
        // mouse and hotkey funnel through it) on Timer/HasButton/CouldUse, so postfixing it is a single
        // choke point to detect "an attempt happened but did nothing" without duplicating any button's
        // own precondition logic. A second postfix on Update() re-applies the flash color each frame
        // for its short duration, since Update() itself unconditionally repaints the button's color
        // from CouldUse() every frame. ----

        private static readonly HashSet<TheOtherRoles.Objects.CustomButton> deniedButtons = new();
        private static readonly Dictionary<TheOtherRoles.Objects.CustomButton, float> deniedFlashUntil = new();
        private const float DeniedFlashDuration = 0.22f;

        // Button statics in Poltergeist.cs/PoltergeistManifest.cs are deliberately kept across
        // resetVariables (it runs AFTER HudManager.Start at round start) - buttons re-register the
        // NEW instance from that same HudManager.Start call, so this set must not be cleared there.
        // Instead, stale instances from previous rounds (their Unity-side renderer is destroyed with
        // the old HUD) are pruned here so the set holds only the live ghost buttons instead of
        // growing by four dead references every round.
        public static void RegisterDeniedFlash(TheOtherRoles.Objects.CustomButton button) {
            if (button == null) return;
            try {
                deniedButtons.RemoveWhere(b => b == null || b.actionButtonRenderer == null);
                if (deniedFlashUntil.Count > 0) {
                    var stale = new List<TheOtherRoles.Objects.CustomButton>();
                    foreach (var kv in deniedFlashUntil)
                        if (kv.Key == null || kv.Key.actionButtonRenderer == null) stale.Add(kv.Key);
                    foreach (var b in stale) deniedFlashUntil.Remove(b);
                }
            } catch { }
            deniedButtons.Add(button);
        }

        [HarmonyPatch(typeof(TheOtherRoles.Objects.CustomButton), nameof(TheOtherRoles.Objects.CustomButton.onClickEvent))]
        static class DeniedClickPatch {
            public static void Postfix(TheOtherRoles.Objects.CustomButton __instance) {
                try {
                    if (__instance == null || !deniedButtons.Contains(__instance)) return;
                    // Mirrors onClickEvent's own gate: if all three held, OnClick() just ran normally.
                    if (__instance.Timer >= 0f || !__instance.HasButton() || __instance.CouldUse()) return;
                    deniedFlashUntil[__instance] = Time.time + DeniedFlashDuration;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Poltergeist] denied-click flash failed: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(TheOtherRoles.Objects.CustomButton), nameof(TheOtherRoles.Objects.CustomButton.Update))]
        static class DeniedFlashOverridePatch {
            public static void Postfix(TheOtherRoles.Objects.CustomButton __instance) {
                try {
                    if (__instance == null || !deniedFlashUntil.TryGetValue(__instance, out var until)) return;
                    if (Time.time >= until) { deniedFlashUntil.Remove(__instance); return; }
                    if (__instance.actionButtonRenderer != null)
                        __instance.actionButtonRenderer.color = new Color(1f, 0.25f, 0.25f, 1f);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Poltergeist] denied-flash override failed: {e.Message}");
                }
            }
        }

        // ---- helpers ----

        private static Vector3 Rot(float ang, float r) => new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, 0f);
        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
        private static float Flicker(int i, float speed) => Mathf.Abs(Mathf.Sin(Time.time * speed + i * 2.3f));
        private static float Hash(int n) { unchecked { n *= (int)2654435761u; n ^= n >> 13; return ((n & 0xFFFF) / 65535f); } }
    }
}
