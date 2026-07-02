// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Tesla kill effect - the double electrocution when a charged +/- pair's countdown runs out. Built
 * after the SaboteurKillFx pattern (its own sprite pool + coroutine, sourced from the shared UCFx
 * sprite cache), but doubled up for the pair: a spark cluster plays at BOTH victim positions (cyan for
 * the positive pole, orange for the negative one, matching TeslaMeetingUI's own +/- color coding) plus
 * a jagged "chain lightning" of streak segments strung along the line between them (the two victims are,
 * by definition, within TriggerDistance of each other for this to fire at all). A brief cyan/orange
 * blended screen flash and the tesla_discharge cue (played at both positions, distance-attenuated via
 * UCAssets.PlayTeslaDischargeAt) complete the moment.
 *
 * Self-contained one-shot effect - anchored at the VICTIM positions (passed in by the caller), never at
 * the (possibly distant) Tesla's own position, so it cannot leak the Tesla's location. Triggered via
 * Tesla's own RPC (SubKillFx), sent BEFORE the murder RPCs so every client applies it locally the moment
 * the pair's fate is decided, exactly like Saboteur.ApplyKillFx -> SaboteurKillFx.Play.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class TeslaKillFx {
        private const int SparkCountPerVictim = 12;
        private const int ChainLinks = 7;
        private const float Duration = 0.7f;
        private const float FlashRange = 12f;   // screen flash only near a victim (no map-wide tell)
        private static readonly Color CyanTint = new Color(0.12f, 0.72f, 1f, 1f);   // positive pole
        private static readonly Color OrangeTint = new Color(1f, 0.55f, 0f, 1f);    // negative pole
        private static readonly Color ChainTint = Color.Lerp(CyanTint, OrangeTint, 0.5f);

        // Running one-shot effect hosts, tracked so a round reset can hard-destroy any burst that is
        // still mid-flight (the Lerp coroutine normally cleans itself up, but resetVariables can land
        // mid-burst if a round somehow ends right after a kill).
        private static readonly List<GameObject> activeEffects = new();

        static TeslaKillFx() {
            UCFx.RegisterReset(Clear);
        }

        public static void Clear() {
            try {
                foreach (var go in activeEffects) if (go != null) UnityEngine.Object.Destroy(go);
                activeEffects.Clear();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Tesla] kill FX clear failed: {e.Message}");
            }
        }

        // plusPos/minusPos: the charged pair's own current positions (read locally by the caller via
        // GetTruePosition() - never transmitted over the RPC), so the burst spawns exactly where each
        // client already sees the pair standing. A null position marks a spared pole (self-charged
        // Tesla that survives): no burst, no discharge sound there, and no chain without both ends.
        public static void Play(Vector2? plusPos, Vector2? minusPos) {
            try {
                if (plusPos == null && minusPos == null) return;

                // Screen flash: a cyan/orange blend reads as "electric", not a single pole's color.
                // Distance-gated like the Maniac explosion flash - the kill itself is public via the
                // death animation, but a map-wide full-screen flash would be a free "Tesla just
                // killed" timestamp for every uninvolved player.
                var local = PlayerControl.LocalPlayer;
                if (local != null) {
                    var lp = local.GetTruePosition();
                    float d = float.MaxValue;
                    if (plusPos != null) d = Mathf.Min(d, Vector2.Distance(lp, plusPos.Value));
                    if (minusPos != null) d = Mathf.Min(d, Vector2.Distance(lp, minusPos.Value));
                    if (d <= FlashRange) Helpers.showFlash(ChainTint, 0.55f);
                }
                if (plusPos != null) UCAssets.PlayTeslaDischargeAt(plusPos.Value);
                if (minusPos != null) UCAssets.PlayTeslaDischargeAt(minusPos.Value);

                Vector2 mid = plusPos != null && minusPos != null
                    ? (plusPos.Value + minusPos.Value) * 0.5f
                    : (plusPos ?? minusPos).Value;

                // layer 11 like every TOR world object - the ship camera does not render Default
                var host = new GameObject("TeslaDischarge") { layer = 11 };
                host.transform.position = new Vector3(mid.x, mid.y, -1.0f);

                var plusSparks = plusPos != null ? BuildSparkCluster(host, CyanTint) : null;
                var minusSparks = minusPos != null ? BuildSparkCluster(host, OrangeTint) : null;
                var chain = plusPos != null && minusPos != null ? BuildChain(host) : null;

                var hud = HudManager.Instance;
                if (hud == null) { UnityEngine.Object.Destroy(host); return; }

                activeEffects.Add(host);
                hud.StartCoroutine(Effects.Lerp(Duration, new Action<float>((t) => {
                    if (host == null) return;
                    float time = Time.time;
                    if (plusSparks != null) AnimateCluster(plusSparks, plusPos.Value, CyanTint, time, t);
                    if (minusSparks != null) AnimateCluster(minusSparks, minusPos.Value, OrangeTint, time, t);
                    if (chain != null) AnimateChain(chain, plusPos.Value, minusPos.Value, time, t);
                    if (t >= 1f) {
                        activeEffects.Remove(host);
                        UnityEngine.Object.Destroy(host);
                    }
                })));
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Tesla] kill FX failed: {e.Message}");
            }
        }

        private static SpriteRenderer[] BuildSparkCluster(GameObject host, Color tint) {
            var parts = new SpriteRenderer[SparkCountPerVictim];
            for (int i = 0; i < SparkCountPerVictim; i++) {
                var go = new GameObject($"spark{i}") { layer = 11 };
                go.transform.SetParent(host.transform);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = (i % 3 == 0) ? UCFx.Streak : UCFx.Spark;
                sr.color = tint;
                go.transform.localScale = Vector3.one * (0.16f + 0.08f * (i % 3));
                UCFx.TryMakeAdditive(sr); // electric look; safely falls back to the default material
                parts[i] = sr;
            }
            return parts;
        }

        // Jagged lightning-chain segments strung along the plus->minus line - each a UCFx.Streak
        // rotated to the line's angle, offset by a small stable per-link jitter perpendicular to it so
        // it reads as a bolt rather than a straight ruled line.
        private static SpriteRenderer[] BuildChain(GameObject host) {
            var parts = new SpriteRenderer[ChainLinks];
            for (int i = 0; i < ChainLinks; i++) {
                var go = new GameObject($"link{i}") { layer = 11 };
                go.transform.SetParent(host.transform);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = UCFx.Streak;
                sr.color = ChainTint;
                UCFx.TryMakeAdditive(sr);
                parts[i] = sr;
            }
            return parts;
        }

        private static void AnimateCluster(SpriteRenderer[] parts, Vector2 at, Color tint, float time, float t) {
            if (parts == null) return;
            float fade = 1f - t;
            for (int i = 0; i < parts.Length; i++) {
                var s = parts[i];
                if (s == null) continue;
                float a = time * (14f + i * 1.3f) + i * 1.7f;       // fast electric orbit
                float r = 0.15f + 0.45f * Mathf.Abs(Mathf.Sin(time * 22f + i));
                s.transform.position = new Vector3(at.x + Mathf.Cos(a) * r, at.y + Mathf.Sin(a) * r, -1.0f);
                float flicker = 0.4f + 0.6f * Mathf.Abs(Mathf.Sin(time * 30f + i * 2f));
                s.color = new Color(tint.r, tint.g, tint.b, flicker * fade);
            }
        }

        private static void AnimateChain(SpriteRenderer[] parts, Vector2 a, Vector2 b, float time, float t) {
            if (parts == null) return;
            Vector2 dir = b - a;
            float len = dir.magnitude;
            float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            Vector2 perp = len > 0.001f ? new Vector2(-dir.y, dir.x).normalized : Vector2.zero;
            float fade = 1f - t;
            float segLen = Mathf.Clamp(parts.Length > 0 ? len / parts.Length * 1.6f : 0.1f, 0.1f, 1.2f);
            for (int i = 0; i < parts.Length; i++) {
                var s = parts[i];
                if (s == null) continue;
                float u = (i + 0.5f) / parts.Length; // position along the line, 0..1
                Vector2 pos = Vector2.Lerp(a, b, u);
                // Stable per-link jitter (deterministic hash, not runtime RNG) plus a fast per-frame
                // crackle so the bolt reads as electricity rather than a static jagged line.
                float jitterBase = Hash(i) - 0.5f;
                float crackle = Mathf.Sin(time * 26f + i * 3.1f) * 0.4f;
                pos += perp * (jitterBase * 0.22f + crackle * 0.05f);
                s.transform.position = new Vector3(pos.x, pos.y, -1.0f);
                s.transform.localRotation = Quaternion.Euler(0f, 0f, ang);
                s.transform.localScale = new Vector3(segLen, 0.10f, 1f);
                float flicker = 0.5f + 0.5f * Mathf.Abs(Mathf.Sin(time * 34f + i * 2.7f));
                s.color = new Color(ChainTint.r, ChainTint.g, ChainTint.b, flicker * fade);
            }
        }

        private static float Hash(int n) {
            unchecked { n *= (int)2654435761u; n ^= n >> 13; return (n & 0xFFFF) / 65535f; }
        }
    }
}
