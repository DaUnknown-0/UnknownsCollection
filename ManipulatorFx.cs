// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Manipulator visual/audio payoff - strictly self-only.
 *
 * The ability itself is silent-and-invisible to everyone BUT the Manipulator by design (the Admin/
 * Vitals lies must stay undetectable to crew), so before this file existed the activation had sound
 * for the Manipulator alone and NOTHING else - no particle, no screen cue, nothing marking the moment
 * the fake window opens or closes. This file adds exactly that, still strictly local:
 *
 *   - SpawnActivation(): a short (~0.6s) red/violet "glitch swirl" around the Manipulator's own
 *     player on activation - jittery, hard-edged sparks/streaks rather than a smooth swirl, to read
 *     as a digital glitch rather than a magic effect.
 *   - TickFakeWindowBookend(): a falling-edge detector on Manipulator.IsFaking() that plays a
 *     dedicated bookend cue (manipulator_end) the frame the fake window closes, so the Manipulator
 *     doesn't have to infer "the tools are telling the truth again" purely from the button cooldown.
 *
 * Same pooled-SpriteRenderer technique as PoltergeistFx (no runtime ParticleSystems in the IL2CPP
 * build). Every effect here is gated on IsLocalManipulator() BOTH at the call site (Manipulator.cs
 * only ever triggers this on the local Manipulator's own client) AND again every Tick frame (the
 * mandated defensive pattern, mirroring PoltergeistFx.TickAura's IsLocalPoltergeist() re-check) -
 * proximity or any other client can never trigger or see this, only the local player's own identity
 * matters.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {
    public static class ManipulatorFx {
        private static readonly Color Red = new Color(0.95f, 0.25f, 0.3f);
        private static readonly Color Violet = new Color(0.62f, 0.3f, 0.95f);

        static ManipulatorFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // Touched once from Manipulator.CreateOptions() (plugin load, guaranteed to run before any
        // round starts) purely to force this type's static constructor - and therefore the
        // RegisterTick/RegisterReset calls above - to run early. Without this, the constructor would
        // only fire on first use (e.g. the first SpawnActivation() call), which is too late to have
        // already registered the Tick/Reset delegates for that very same activation.
        public static void Init() { }

        private sealed class Swirl {
            public GameObject go;
            public SpriteRenderer[] parts;
            public float start;
            public float life;
            public int seed;
        }
        private static readonly List<Swirl> swirls = new();
        private static bool wasFaking;

        // Activation payoff. Called from Manipulator.ApplyManipulate() behind an IsLocalManipulator()
        // check already - re-checked here too (belt-and-suspenders, matches the mandated gate pattern).
        public static void SpawnActivation() {
            if (!Manipulator.IsLocalManipulator() || PlayerControl.LocalPlayer == null) return;
            try {
                Vector2 at = PlayerControl.LocalPlayer.GetTruePosition();
                var s = new Swirl {
                    go = new GameObject("ManipulatorGlitch") { layer = 11 },
                    parts = new SpriteRenderer[16],
                    start = Time.time,
                    life = 0.6f,
                    seed = UnityEngine.Random.Range(0, 10000)
                };
                s.go.transform.position = new Vector3(at.x, at.y, -1.5f);
                for (int i = 0; i < s.parts.Length; i++) {
                    var go = new GameObject($"g{i}") { layer = 11 };
                    go.transform.SetParent(s.go.transform);
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = (i % 3 == 0) ? UCFx.Streak : UCFx.Spark;
                    UCFx.TryMakeAdditive(sr);
                    s.parts[i] = sr;
                }
                swirls.Add(s);
                Animate(s, 0f);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Manipulator] fx spawn failed: {e.Message}");
            }
        }

        private static void Tick() {
            try {
                TickFakeWindowBookend();

                if (swirls.Count == 0) return;
                // Defensive per-frame gate (mandated pattern): if local identity ever changes mid-
                // animation, drop everything immediately instead of letting a stray frame render.
                if (!Manipulator.IsLocalManipulator()) { Clear(); return; }

                float now = Time.time;
                var local = PlayerControl.LocalPlayer;
                Vector2 at = local != null ? local.GetTruePosition() : Vector2.zero;
                for (int i = swirls.Count - 1; i >= 0; i--) {
                    var s = swirls[i];
                    if (s.go == null || now - s.start >= s.life) {
                        if (s.go != null) UnityEngine.Object.Destroy(s.go);
                        swirls.RemoveAt(i);
                        continue;
                    }
                    s.go.transform.position = new Vector3(at.x, at.y, -1.5f);
                    Animate(s, (now - s.start) / s.life);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Manipulator] fx tick failed: {e.Message}");
            }
        }

        // Falling-edge detector: fires manipulator_end the exact frame IsFaking() flips true -> false,
        // purely for the local Manipulator. Runs unconditionally every Tick (not gated on swirls.Count)
        // since the fake window can easily close long after the activation swirl already finished.
        private static void TickFakeWindowBookend() {
            bool faking = Manipulator.IsLocalManipulator() && Manipulator.IsFaking();
            if (wasFaking && !faking) UCAssets.PlayManipulatorEnd();
            wasFaking = faking;
        }

        private static void Animate(Swirl s, float t) {
            for (int i = 0; i < s.parts.Length; i++) {
                var sr = s.parts[i];
                if (sr == null) continue;
                float u = Hash(s.seed + i);
                float v = Hash(s.seed + i * 7 + 3);
                // Glitch jitter: discrete-feeling jumps (re-hashed every ~1/14th of the lifetime)
                // rather than a smooth swirl, so it reads as digital interference.
                int jitterStep = Mathf.FloorToInt(t * 14f) + i;
                float jitterU = Hash(s.seed + i * 13 + jitterStep * 91);
                float ang = u * Mathf.PI * 2f + t * (6f + v * 4f);
                float r = 0.26f + 0.20f * v + (jitterU - 0.5f) * 0.16f;
                var pos = new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r * 0.8f + 0.15f, 0f);
                sr.transform.localPosition = pos;

                bool isStreak = i % 3 == 0;
                if (isStreak) {
                    sr.transform.localScale = new Vector3(0.26f * (1f - t * 0.3f), 0.06f, 1f);
                    sr.transform.localRotation = Quaternion.Euler(0, 0, jitterU * 360f);
                } else {
                    sr.transform.localScale = Vector3.one * (0.13f + 0.09f * v) * (1f - t * 0.4f);
                }
                float fade = (1f - t) * (0.5f + 0.5f * Flicker(i, 20f));
                sr.color = Tint(Color.Lerp(Red, Violet, u), fade);
            }
        }

        public static void Clear() {
            foreach (var s in swirls) if (s.go != null) UnityEngine.Object.Destroy(s.go);
            swirls.Clear();
            wasFaking = false;
        }

        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
        private static float Flicker(int i, float speed) => Mathf.Abs(Mathf.Sin(Time.time * speed + i * 2.3f));
        private static float Hash(int n) { unchecked { n *= (int)2654435761u; n ^= n >> 13; return ((n & 0xFFFF) / 65535f); } }
    }
}
