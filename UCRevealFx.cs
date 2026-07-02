// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCRevealFx - the role-agnostic "you have just been assigned a UC role" cue.
 *
 * UCPromotion.Claim() is the single choke point every UC role's Apply* runs through (Draft picks AND
 * random IntroCutscene promotion alike), but it never gave the freshly-promoted player any feedback -
 * the moment was completely silent. This class is that feedback: a brief warm screen flash
 * (Helpers.showFlash), the shared "uc_reveal" stinger (UCAssets.PlayUcReveal), and a small gold/white
 * particle burst anchored on the local player, built from the shared UCFx sprite cache and driven by
 * UCFx's per-frame tick/reset registries (same pooled-SpriteRenderer technique as PoltergeistFx - no
 * runtime ParticleSystems, IL2CPP does not render those reliably).
 *
 * Self-only by construction: PlayReveal() is only ever called by UCPromotion.Claim() after it has
 * already verified playerId == PlayerControl.LocalPlayer.PlayerId, so this class does not need its own
 * per-frame visibility gate (there is nothing continuous here to gate - it is a single one-shot burst).
 * Because it is role-agnostic it deliberately carries no role name/color: a neutral gold/white palette
 * covers every UC role without leaking which specific role was just granted to anyone glancing at a
 * recording, and without requiring Claim()'s signature to grow a faction/color parameter.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class UCRevealFx {
        private static readonly Color Gold = new Color(1f, 0.82f, 0.35f);
        private static readonly Color White = new Color(1f, 0.97f, 0.88f);

        static UCRevealFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // ---- one-shot effect bookkeeping (same shape as PoltergeistFx.Effect) ----
        private sealed class Effect {
            public GameObject go;
            public SpriteRenderer[] parts;
            public float start;
            public float life;
            public int seed;
        }
        private static readonly List<Effect> effects = new();

        // Plays the reveal cue for the local player only. Callers (currently just UCPromotion.Claim)
        // are responsible for the playerId == LocalPlayer.PlayerId gate - this method assumes it is
        // always the local player's own reveal and reads the local player's position directly.
        public static void PlayReveal() {
            try {
                var self = PlayerControl.LocalPlayer;
                if (self == null) return;
                Helpers.showFlash(Gold, 0.6f);
                UCAssets.PlayUcReveal();
                Spawn(self.GetTruePosition());
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCRevealFx] PlayReveal failed: {e.Message}");
            }
        }

        private static void Spawn(Vector2 at) {
            try {
                const int count = 14;
                var go = UCFx.NewFxRoot("UCRevealFx", at);
                var parts = UCFx.MakeParts(go, count, i => (i % 3 == 0) ? UCFx.Streak : UCFx.Dot);
                var e = new Effect {
                    go = go,
                    parts = parts,
                    start = Time.time,
                    life = 0.6f,
                    seed = UnityEngine.Random.Range(0, 10000),
                };
                effects.Add(e);
                Animate(e, 0f);
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCRevealFx] spawn failed: {ex.Message}");
            }
        }

        private static void Tick() {
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
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCRevealFx] tick failed: {ex.Message}");
            }
        }

        public static void Clear() {
            try {
                foreach (var e in effects) if (e.go != null) UnityEngine.Object.Destroy(e.go);
                effects.Clear();
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCRevealFx] clear failed: {ex.Message}");
            }
        }

        // Burst expands outward, drifts gently upward and fades - same shape language as
        // PoltergeistFx's Poof, but shorter-lived and tinted warm gold/white instead of violet/cyan.
        private static void Animate(Effect e, float t) {
            for (int i = 0; i < e.parts.Length; i++) {
                var sr = e.parts[i];
                if (sr == null) continue;
                float u = Hash(e.seed + i);
                float v = Hash(e.seed + i * 7 + 3);
                float ang = u * Mathf.PI * 2f;
                bool isStreak = i % 3 == 0;

                float ease = 1f - (1f - t) * (1f - t); // ease-out
                float r = 0.10f + ease * (0.55f + 0.35f * v);
                var pos = new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, 0f);
                pos.y += ease * 0.35f; // gentle buoyant drift
                sr.transform.localPosition = pos;

                if (isStreak) {
                    sr.transform.localRotation = Quaternion.Euler(0, 0, ang * Mathf.Rad2Deg);
                    sr.transform.localScale = new Vector3(0.30f * (1f - t * 0.5f), 0.08f, 1f);
                } else {
                    sr.transform.localScale = Vector3.one * (0.22f + 0.16f * v) * (1f - t * 0.4f);
                }

                float alpha = (1f - ease) * 0.9f;
                sr.color = Tint(Color.Lerp(White, Gold, v), alpha);
                if (!isStreak && i % 5 == 0) sr.color = Tint(White, Mathf.Clamp01(1f - t * 3f)); // brief core flash
            }
        }

        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
        private static float Hash(int n) { unchecked { n *= (int)2654435761u; n ^= n >> 13; return ((n & 0xFFFF) / 65535f); } }
    }
}
