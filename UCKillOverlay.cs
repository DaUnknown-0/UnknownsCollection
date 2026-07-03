// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCKillOverlay - custom role-themed kill overlays (the fullscreen "cutscene" killer+victim see).
 *
 * Vanilla plays one of its OverlayKillAnimation prefabs from KillOverlay.ShowKillAnimation, which
 * only ever runs on the killer's and the victim's client. We intercept exactly that call: if the
 * kill belongs to one of our roles, the vanilla overlay is suppressed (bool Prefix -> false; TOR
 * patches KillAnimation.CoPerformKill but NOT this method, so no patch-order conflict) and our own
 * sequence plays instead - sprite parts animated by a code timeline, crew figures tinted with the
 * REAL player colors like the original.
 *
 * Role detection (who "owns" a kill) mirrors TOR's hideNextAnimation pattern, but network-safe:
 * every role that should get an overlay already broadcasts a role-specific FX RPC BEFORE its
 * murder RPCs (same sender => Hazel keeps the order). Those per-client handlers arm a context:
 *   - Tesla:        ArmVictim(Tesla, plusId/minusId)      (Tesla.ApplyKillFx)
 *   - SaboteurTask: ArmVictim(SaboteurTask, victimId)     (Saboteur.ApplyKillFx) - task kills ONLY,
 *                    the Saboteur's normal knife kills keep the vanilla overlay (design decision)
 *   - ManiacBomb:   ArmWindow(ManiacBomb)                 (Maniac.ApplyExplode; victims only known host-side)
 *   - Shade:        no arming - matched by killer identity, every Shade murder vanishes the body
 * The Poisoner never reaches ShowKillAnimation at all (poison deaths use Exiled(), no body): its
 * handler calls PlayFor() directly, and the queue below delays playback until the meeting/exile
 * UI is gone.
 *
 * Info-leak: audience is identical to vanilla (killer + victim only). The Saboteur/Poisoner
 * sequences additionally show NO killer figure - those kills stay anonymous even to the victim.
 * Everything here is pure cosmetics: no game state is touched, errors fall back to vanilla.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UnknownsCollection {
    public static class UCKillOverlay {
        public enum Kind : byte { None, Tesla, SaboteurTask, Poisoner, Shade, ManiacBomb }

        private const float DimMax = 0.82f;

        // ==================== context arming (called from role RPC handlers) ====================

        private static readonly Dictionary<byte, (Kind kind, float until)> armedVictims = new();
        private static Kind windowKind = Kind.None;
        private static float windowUntil;

        public static void ArmVictim(Kind kind, byte victimId, float ttl = 5f) {
            armedVictims[victimId] = (kind, Time.time + ttl);
        }
        public static void ArmWindow(Kind kind, float ttl = 3f) {
            windowKind = kind;
            windowUntil = Time.time + ttl;
        }

        static UCKillOverlay() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // Forces the static ctor (tick/reset registration) at plugin load - ManipulatorFx pattern.
        public static void Init() { }

        // ==================== vanilla overlay hook ====================

        [HarmonyPatch(typeof(KillOverlay), nameof(KillOverlay.ShowKillAnimation),
            typeof(NetworkedPlayerInfo), typeof(NetworkedPlayerInfo))]
        static class ShowKillAnimationPatch {
            public static bool Prefix([HarmonyArgument(0)] NetworkedPlayerInfo killer,
                                      [HarmonyArgument(1)] NetworkedPlayerInfo victim) {
                try {
                    Kind kind = Select(killer, victim);
                    if (kind == Kind.None) return true;
                    PlayFor(kind, killer, victim);
                    return false;                       // suppress the vanilla overlay
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCKillOverlay] hook: {e}");
                    return true;                        // any failure -> vanilla behavior
                }
            }
        }

        private static Kind Select(NetworkedPlayerInfo killer, NetworkedPlayerInfo victim) {
            float now = Time.time;
            if (victim != null && armedVictims.TryGetValue(victim.PlayerId, out var armed)) {
                armedVictims.Remove(victim.PlayerId);
                if (now <= armed.until) return armed.kind;
            }
            if (windowKind != Kind.None) {
                if (now <= windowUntil) return windowKind;
                windowKind = Kind.None;
            }
            // Shade: every murder BY the Shade gets the vanishing-body overlay.
            if (killer != null && victim != null && Shade.active && Shade.shade != null
                && killer.PlayerId == Shade.shade.PlayerId && killer.PlayerId != victim.PlayerId)
                return Kind.Shade;
            return Kind.None;
        }

        // ==================== queue (survives meetings; poison deaths arrive at MeetingHud.Close) ====================

        private sealed class Pending {
            public Kind kind;
            public int killerColor;
            public int victimColor;
            public float expires;
        }
        private static readonly List<Pending> pending = new();

        public static void PlayFor(Kind kind, NetworkedPlayerInfo killer, NetworkedPlayerInfo victim) {
            pending.Add(new Pending {
                kind = kind,
                killerColor = ColorIdOf(killer),
                victimColor = ColorIdOf(victim),
                expires = Time.time + 20f
            });
        }

        private static int ColorIdOf(NetworkedPlayerInfo info) {
            try {
                if (info?.DefaultOutfit != null) return info.DefaultOutfit.ColorId;
            } catch { }
            return 0;
        }

        private static Color PlayerColor(int colorId) {
            try {
                var colors = Palette.PlayerColors;
                if (colors != null && colorId >= 0 && colorId < colors.Length) return colors[colorId];
            } catch { }
            return Color.red;
        }

        // ==================== active sequence state ====================

        private sealed class Fig {
            public GameObject go;
            public SpriteRenderer body;
            public SpriteRenderer visor;
            public Color color;
            public float visorX;

            public void SetPos(float x, float y) => go.transform.localPosition = new Vector3(x, y, 0f);
            public void SetRot(float deg) => go.transform.localRotation = Quaternion.Euler(0, 0, deg);
            public void SetScale(float s) => go.transform.localScale = new Vector3(s, s, 1f);
            public void SetAlpha(float a) {
                var c = body.color; body.color = new Color(c.r, c.g, c.b, a);
                if (visor != null) { var v = visor.color; visor.color = new Color(v.r, v.g, v.b, a); }
            }
            public void SetTint(Color c, float a) {
                body.color = new Color(c.r, c.g, c.b, a);
                if (visor != null) { var v = visor.color; visor.color = new Color(v.r, v.g, v.b, a); }
            }
        }

        private static GameObject root;
        private static Kind activeKind = Kind.None;
        private static float startTime;
        private static float duration;
        private static System.Random rng;
        private static int hudLayer;

        private static SpriteRenderer dim, flash;
        private static Fig killerFig, victimFig;
        private static SpriteRenderer propA, propB, propC;      // role props (bolt/console/vial/shadow/bomb/burst)
        private static SpriteRenderer[] particles;              // small accents (sparks/smoke/bubbles)
        private static int soundPhase;                          // one-shot sound trigger per phase

        // ==================== tick ====================

        private static void Tick() {
            // Expire stale queue entries.
            for (int i = pending.Count - 1; i >= 0; i--)
                if (Time.time > pending[i].expires) pending.RemoveAt(i);

            bool uiBlocked = MeetingHud.Instance != null || ExileController.Instance != null;

            if (root != null) {
                if (uiBlocked) { Clear(); return; }     // a meeting interrupts the show
                float t = (Time.time - startTime) / duration;
                if (t >= 1f) { Clear(); return; }
                try { UpdateSeq(Mathf.Clamp01(t)); }
                catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[UCKillOverlay] update failed: {e.Message}");
                    Clear();
                }
                return;
            }

            if (pending.Count == 0 || uiBlocked) return;
            var hud = HudManager.Instance;
            if (hud == null) return;

            var next = pending[0];
            pending.RemoveAt(0);
            try { Build(hud, next); }
            catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCKillOverlay] build failed: {e}");
                Clear();
            }
        }

        private static void Clear() {
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            activeKind = Kind.None;
            killerFig = null; victimFig = null;
            dim = flash = propA = propB = propC = null;
            particles = null;
            armedVictims.Clear();
            windowKind = Kind.None;
            pending.Clear();
        }

        // ==================== construction ====================

        private static SpriteRenderer Make(string name, Sprite sprite, float x, float y, int order,
                                           Color color, float scale = 1f, bool additive = false, Transform parent = null) {
            var go = new GameObject(name) { layer = hudLayer };
            go.transform.SetParent(parent != null ? parent : root.transform, false);
            go.transform.localPosition = new Vector3(x, y, 0f);
            go.transform.localScale = new Vector3(scale, scale, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = order;
            if (additive) UCFx.TryMakeAdditive(sr);
            return sr;
        }

        private static Fig MakeFig(int colorId, bool faceLeft, float x, float y, int order) {
            var color = PlayerColor(colorId);
            var go = new GameObject(faceLeft ? "figL" : "figR") { layer = hudLayer };
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(x, y, 0f);
            var fig = new Fig { go = go, color = color, visorX = faceLeft ? -0.40f : 0.40f };

            var bodyGo = new GameObject("body") { layer = hudLayer };
            bodyGo.transform.SetParent(go.transform, false);
            fig.body = bodyGo.AddComponent<SpriteRenderer>();
            fig.body.sprite = UCAssets.OverlayCrewBody;
            fig.body.color = color;
            fig.body.flipX = faceLeft;
            fig.body.sortingOrder = order;

            var visorGo = new GameObject("visor") { layer = hudLayer };
            visorGo.transform.SetParent(go.transform, false);
            visorGo.transform.localPosition = new Vector3(fig.visorX, 0.44f, 0f);
            visorGo.transform.localScale = new Vector3(0.78f, 0.78f, 1f);
            fig.visor = visorGo.AddComponent<SpriteRenderer>();
            fig.visor.sprite = UCAssets.OverlayCrewVisor;
            fig.visor.color = Color.white;
            fig.visor.flipX = faceLeft;
            fig.visor.sortingOrder = order + 1;
            return fig;
        }

        private static SpriteRenderer[] MakeParticles(int count, Sprite sprite, Color color, int order, bool additive) {
            var arr = new SpriteRenderer[count];
            for (int i = 0; i < count; i++) {
                arr[i] = Make($"part{i}", sprite, 0f, 0f, order, new Color(color.r, color.g, color.b, 0f), 1f, additive);
            }
            return arr;
        }

        private static void Build(HudManager hud, Pending p) {
            hudLayer = hud.gameObject.layer;
            root = new GameObject("UCKillOverlay") { layer = hudLayer };
            root.transform.SetParent(hud.transform, false);
            root.transform.localPosition = new Vector3(0f, 0f, -500f);

            activeKind = p.kind;
            startTime = Time.time;
            soundPhase = 0;
            rng = new System.Random(Environment.TickCount);

            dim = Make("dim", UCAssets.OverlayWhite, 0f, 0f, 0, new Color(0f, 0f, 0f, 0f), 400f);
            flash = Make("flash", UCAssets.OverlayWhite, 0f, 0f, 90, new Color(1f, 1f, 1f, 0f), 400f);

            switch (p.kind) {
                case Kind.Tesla:
                    duration = 1.5f;
                    killerFig = MakeFig(p.killerColor, false, -4.2f, -0.35f, 10);
                    victimFig = MakeFig(p.victimColor, true, 4.2f, -0.35f, 10);
                    propA = Make("boltA", UCAssets.OverlayBoltA, 0f, 0.05f, 20, new Color(1f, 1f, 1f, 0f), 1f, true);
                    propB = Make("boltB", UCAssets.OverlayBoltB, 0f, 0.05f, 20, new Color(1f, 1f, 1f, 0f), 1f, true);
                    particles = MakeParticles(6, UCFx.Spark, new Color(0.55f, 0.92f, 1f), 30, true);
                    break;

                case Kind.SaboteurTask:
                    duration = 1.55f;
                    victimFig = MakeFig(p.victimColor, false, -0.9f, -0.4f, 10);   // works facing the console
                    propA = Make("console", UCAssets.OverlayConsole, 1.15f, -0.25f, 12, Color.white, 1.05f);
                    propB = Make("zap", UCAssets.OverlayBoltA, 0.2f, -0.1f, 20, new Color(1f, 1f, 1f, 0f), 0.45f, true);
                    particles = MakeParticles(7, UCFx.Spark, new Color(1f, 0.85f, 0.35f), 30, true);
                    break;

                case Kind.Poisoner:
                    duration = 1.7f;
                    victimFig = MakeFig(p.victimColor, false, 0f, -0.45f, 10);
                    propA = Make("vial", UCAssets.OverlayVial, 1.6f, 2.6f, 14, Color.white, 0.8f);
                    particles = MakeParticles(9, UCFx.Dot, new Color(0.45f, 0.95f, 0.45f), 30, true);
                    break;

                case Kind.Shade:
                    duration = 1.7f;
                    killerFig = MakeFig(p.killerColor, false, -1.35f, 0.25f, 8);   // looming silhouette behind
                    victimFig = MakeFig(p.victimColor, true, 0.6f, -0.4f, 10);
                    propA = Make("maw", UCAssets.OverlayShadow, 0.6f, -3.6f, 14, Color.white, 2.2f);
                    particles = MakeParticles(6, UCFx.Smoke, new Color(0.45f, 0.3f, 0.7f), 16, false);
                    break;

                case Kind.ManiacBomb:
                    duration = 1.5f;
                    victimFig = MakeFig(p.victimColor, true, 1.1f, -0.4f, 10);
                    propA = Make("bomb", UCAssets.OverlayBomb, -1.1f, 3.2f, 12, Color.white, 1.0f);
                    propB = Make("burst", UCAssets.OverlayBurst, -0.6f, -0.2f, 40, new Color(1f, 1f, 1f, 0f), 0.3f);
                    particles = MakeParticles(8, UCFx.Smoke, new Color(0.45f, 0.45f, 0.5f), 35, false);
                    break;
            }
        }

        // ==================== easing helpers ====================

        private static float Seg(float t, float a, float b) => Mathf.Clamp01((t - a) / (b - a));
        private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);
        private static float EaseIn(float t) => t * t;
        private static float Smooth(float t) => t * t * (3f - 2f * t);
        private static float Bounce(float t) {   // simple overshoot-settle for drops/landings
            if (t < 0.6f) return EaseIn(t / 0.6f);
            float u = (t - 0.6f) / 0.4f;
            return 1f + Mathf.Sin(u * Mathf.PI * 2f) * 0.08f * (1f - u);
        }
        private static float Jitter(float amp) => (float)(rng.NextDouble() * 2.0 - 1.0) * amp;

        private static void SetAlpha(SpriteRenderer sr, float a) {
            if (sr == null) return;
            var c = sr.color; sr.color = new Color(c.r, c.g, c.b, a);
        }

        private static void Sound(int phase, Action play) {
            if (soundPhase >= phase) return;
            soundPhase = phase;
            try { play(); } catch { }
        }

        // ==================== the five choreographies ====================

        private static void UpdateSeq(float t) {
            // Shared: dim in over the first 12%, out over the last 12%.
            float dimA = DimMax * Smooth(Seg(t, 0f, 0.12f)) * (1f - Smooth(Seg(t, 0.88f, 1f)));
            SetAlpha(dim, dimA);
            float exit = 1f - Smooth(Seg(t, 0.88f, 1f));   // global fade-out factor for actors

            switch (activeKind) {
                case Kind.Tesla: UpdateTesla(t, exit); break;
                case Kind.SaboteurTask: UpdateSaboteur(t, exit); break;
                case Kind.Poisoner: UpdatePoisoner(t, exit); break;
                case Kind.Shade: UpdateShade(t, exit); break;
                case Kind.ManiacBomb: UpdateManiac(t, exit); break;
            }
        }

        private static void UpdateTesla(float t, float exit) {
            // Entry: both slide in from the edges.
            float ein = EaseOut(Seg(t, 0f, 0.18f));
            killerFig.SetPos(Mathf.Lerp(-4.2f, -2.3f, ein), -0.35f);
            killerFig.SetAlpha(ein * exit);

            bool zapping = t >= 0.3f && t < 0.72f;
            if (t >= 0.28f) Sound(1, () => UCAssets.PlayTeslaDischargeAt(PlayerControl.LocalPlayer.GetTruePosition()));

            // Charge sparks around the killer's raised side before/while the arc burns.
            for (int i = 0; i < particles.Length; i++) {
                var sr = particles[i];
                float ph = Seg(t, 0.16f + i * 0.015f, 0.72f);
                sr.transform.localPosition = new Vector3(-1.75f + Jitter(0.12f), -0.05f + i * 0.11f - 0.3f + Jitter(0.1f), 0f);
                sr.transform.localScale = Vector3.one * (0.5f + 0.45f * Mathf.PingPong(t * 23f + i, 1f));
                SetAlpha(sr, (ph > 0f && ph < 1f ? 0.85f : 0f) * exit);
            }

            // The arc: alternate the two bolt sprites fast, slight scale shiver.
            if (zapping) {
                bool a = ((int)(Time.time * 18f)) % 2 == 0;
                SetAlpha(propA, a ? 0.95f : 0f);
                SetAlpha(propB, a ? 0f : 0.95f);
                float sy = 0.9f + Jitter(0.12f);
                propA.transform.localScale = new Vector3(0.86f, sy, 1f);
                propB.transform.localScale = new Vector3(0.86f, sy, 1f);
                SetAlpha(flash, 0.28f + 0.22f * Mathf.PingPong(Time.time * 14f, 1f));
                // The victim convulses while the current flows: white-hot flicker + shake.
                bool hot = ((int)(Time.time * 22f)) % 2 == 0;
                victimFig.SetTint(hot ? Color.white : victimFig.color, exit);
                victimFig.SetPos(2.3f + Jitter(0.07f), -0.35f + Jitter(0.06f));
            } else {
                SetAlpha(propA, 0f); SetAlpha(propB, 0f);
                if (t < 0.3f) {
                    victimFig.SetPos(Mathf.Lerp(4.2f, 2.3f, ein), -0.35f);
                    victimFig.SetAlpha(ein);
                    SetAlpha(flash, 0f);
                }
            }

            // Aftermath: charred victim keels over.
            if (t >= 0.72f) {
                float fall = Smooth(Seg(t, 0.72f, 0.95f));
                victimFig.SetTint(Color.Lerp(victimFig.color, new Color(0.16f, 0.15f, 0.17f), 0.85f), exit);
                victimFig.SetRot(76f * fall);
                victimFig.SetPos(2.3f + 0.35f * fall, -0.35f - 0.55f * fall);
                SetAlpha(flash, Mathf.Max(0f, 0.5f - 2.2f * (t - 0.72f)));
            }
        }

        private static void UpdateSaboteur(float t, float exit) {
            // No killer on screen: the trap is anonymous. Victim walks up and works the console.
            float ein = EaseOut(Seg(t, 0f, 0.2f));
            victimFig.SetAlpha(ein * exit);
            SetAlpha(propA, ein * exit);

            if (t < 0.5f) {
                // Working: gentle task bobbing.
                victimFig.SetPos(Mathf.Lerp(-2.6f, -0.9f, ein), -0.4f + 0.05f * Mathf.Sin(t * 26f));
            }

            // Alarm: the console blinks red twice.
            if (t >= 0.42f && t < 0.62f) {
                float blink = Mathf.PingPong(Seg(t, 0.42f, 0.62f) * 4f, 1f);
                propA.color = Color.Lerp(Color.white, new Color(1f, 0.35f, 0.3f), blink);
                propA.transform.localPosition = new Vector3(1.15f + Jitter(0.03f), -0.25f, 0f);
            }

            // Zap: short arc console -> victim, sparks fly.
            bool zap = t >= 0.62f && t < 0.78f;
            if (t >= 0.6f) Sound(1, () => UCAssets.PlayZap(PlayerControl.LocalPlayer.GetTruePosition()));
            SetAlpha(propB, zap ? (((int)(Time.time * 20f)) % 2 == 0 ? 0.95f : 0.4f) : 0f);
            if (zap) {
                SetAlpha(flash, 0.45f * (1f - Seg(t, 0.62f, 0.78f)));
                bool hot = ((int)(Time.time * 24f)) % 2 == 0;
                victimFig.SetTint(hot ? Color.white : victimFig.color, exit);
            }
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.63f + i * 0.008f, 0.98f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                double ang = i * (Math.PI * 2.0 / particles.Length) + 0.5;
                float r = 0.25f + EaseOut(ph) * 1.5f;
                sr.transform.localPosition = new Vector3(0.35f + (float)Math.Cos(ang) * r, -0.15f + (float)Math.Sin(ang) * r - 0.6f * ph * ph, 0f);
                sr.transform.localScale = Vector3.one * (0.8f - 0.5f * ph);
                SetAlpha(sr, (1f - ph) * exit);
            }

            // Blown back off its feet.
            if (t >= 0.78f) {
                float fly = EaseOut(Seg(t, 0.78f, 1f));
                victimFig.SetTint(victimFig.color, exit);
                victimFig.SetPos(-0.9f - 2.6f * fly, -0.4f + 0.7f * fly - 1.1f * fly * fly);
                victimFig.SetRot(-72f * fly);
            }
        }

        private static void UpdatePoisoner(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.18f));
            victimFig.SetAlpha(ein * exit);

            // The vial tips in from above and pours.
            float pour = Seg(t, 0.1f, 0.34f);
            if (t < 0.42f) {
                propA.transform.localPosition = new Vector3(Mathf.Lerp(1.6f, 0.55f, Smooth(pour)), Mathf.Lerp(2.6f, 1.35f, Smooth(pour)), 0f);
                propA.transform.localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(0f, 118f, Smooth(pour)));
                SetAlpha(propA, ein * exit);
            } else {
                // Vial exits stage right once emptied.
                float outp = Seg(t, 0.42f, 0.58f);
                propA.transform.localPosition = new Vector3(0.55f + 2.6f * EaseIn(outp), 1.35f + 1.4f * outp, 0f);
                SetAlpha(propA, (1f - outp) * exit);
            }
            if (t >= 0.3f) Sound(1, () => UCAssets.PlayPoisonGurgle());

            // Green soak: the victim's tint shifts toward sickly green, swaying weaker and weaker.
            float soak = Smooth(Seg(t, 0.32f, 0.8f));
            var sick = Color.Lerp(victimFig.color, new Color(0.4f, 0.85f, 0.42f), 0.72f * soak);
            victimFig.SetTint(sick, Mathf.Min(ein, exit));
            if (t < 0.86f)
                victimFig.SetRot(Mathf.Sin(t * 21f) * 7f * soak * (1f - Seg(t, 0.6f, 0.86f) * 0.6f));

            // Bubbles rise off the victim, staggered.
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.3f + i * 0.05f, 0.62f + i * 0.05f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                float x = -0.55f + (i % 3) * 0.5f + Jitter(0.02f);
                sr.transform.localPosition = new Vector3(x, -0.7f + ph * 2.1f, 0f);
                sr.transform.localScale = Vector3.one * (0.35f + 0.5f * ph);
                SetAlpha(sr, Mathf.Sin(ph * Mathf.PI) * 0.85f * exit);
            }

            // Collapse.
            if (t >= 0.8f) {
                float fall = Smooth(Seg(t, 0.8f, 0.97f));
                victimFig.SetRot(84f * fall);
                victimFig.SetPos(0f + 0.3f * fall, -0.45f - 0.6f * fall);
            }
        }

        private static void UpdateShade(float t, float exit) {
            // Extra darkness for the Shade.
            SetAlpha(dim, Mathf.Min(0.92f, dim.color.a + 0.1f * Smooth(Seg(t, 0f, 0.12f))));

            float ein = EaseOut(Seg(t, 0f, 0.18f));
            victimFig.SetAlpha(ein * exit);

            // The killer is only a black silhouette looming large behind - glowing dark visor.
            float loom = Smooth(Seg(t, 0.05f, 0.35f));
            killerFig.SetScale(1.25f + 0.15f * loom);
            killerFig.body.color = new Color(0.05f, 0.04f, 0.09f, 0.92f * loom * exit);
            killerFig.visor.color = new Color(0.55f, 0.35f, 0.95f, 0.9f * loom * exit);
            if (t >= 0.2f) Sound(1, () => UCAssets.PlayShadeVanish());

            // The maw rises from below the frame.
            float rise = Smooth(Seg(t, 0.2f, 0.5f));
            propA.transform.localPosition = new Vector3(0.6f, Mathf.Lerp(-3.6f, -1.35f, rise), 0f);
            propA.transform.localScale = new Vector3(2.2f + 0.06f * Mathf.Sin(t * 19f), 2.2f, 1f);
            SetAlpha(propA, exit);

            // The victim sinks behind the maw and dissolves - the vanishing body.
            float sink = Smooth(Seg(t, 0.5f, 0.88f));
            victimFig.SetPos(0.6f, -0.4f - 2.1f * sink);
            victimFig.SetRot(10f * Mathf.Sin(sink * Mathf.PI));
            victimFig.SetAlpha((1f - EaseIn(sink)) * exit);

            // Violet wisps curling up.
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.45f + i * 0.06f, 0.85f + i * 0.04f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                float x = 0.6f + Mathf.Sin(ph * 6f + i * 2.1f) * (0.4f + 0.12f * i);
                sr.transform.localPosition = new Vector3(x, -1.3f + ph * 2.3f, 0f);
                sr.transform.localScale = Vector3.one * (0.5f + 0.7f * ph);
                SetAlpha(sr, Mathf.Sin(ph * Mathf.PI) * 0.5f * exit);
            }

            // The maw slips away with the loot.
            if (t >= 0.85f) {
                float leave = Smooth(Seg(t, 0.85f, 1f));
                propA.transform.localPosition = new Vector3(0.6f, -1.35f - 2.4f * leave, 0f);
            }
        }

        private static void UpdateManiac(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.18f));
            victimFig.SetAlpha(Mathf.Min(ein, t < 0.62f ? 1f : 1f) * (t < 0.92f ? 1f : exit));

            bool exploded = t >= 0.58f;

            if (!exploded) {
                victimFig.SetPos(1.1f + Jitter(0.03f * Seg(t, 0.3f, 0.58f)), -0.4f);
                // The bomb drops in and settles with a bounce, pulsing angrier and angrier.
                float drop = Bounce(Seg(t, 0.08f, 0.34f));
                propA.transform.localPosition = new Vector3(-1.1f, Mathf.Lerp(3.2f, -0.75f, drop), 0f);
                float panic = Seg(t, 0.34f, 0.58f);
                float pulse = 1f + 0.08f * Mathf.Sin(panic * 26f) * panic;
                propA.transform.localScale = new Vector3(pulse, pulse, 1f);
                propA.color = Color.Lerp(Color.white, new Color(1f, 0.55f, 0.5f), Mathf.PingPong(panic * 6f, 1f) * panic);
                SetAlpha(propA, ein);
            } else {
                Sound(1, () => UCAssets.PlayExplosion(PlayerControl.LocalPlayer.GetTruePosition()));
                SetAlpha(propA, 0f);
                // Fireball: fast up-scale + spin, white flash decaying behind it.
                float boom = Seg(t, 0.58f, 0.9f);
                propB.transform.localScale = Vector3.one * Mathf.Lerp(0.3f, 3.4f, EaseOut(boom));
                propB.transform.localRotation = Quaternion.Euler(0, 0, 40f * boom);
                SetAlpha(propB, (boom < 0.75f ? 1f : (1f - Seg(boom, 0.75f, 1f))) * exit);
                SetAlpha(flash, Mathf.Max(0f, 0.85f - 2.4f * (t - 0.58f)));

                // The victim is flung off screen, spinning.
                float fly = EaseOut(Seg(t, 0.6f, 1f));
                victimFig.SetPos(1.1f + 3.6f * fly, -0.4f + 1.5f * fly - 2.2f * fly * fly);
                victimFig.SetRot(-660f * fly);

                // Smoke mushrooms out.
                for (int i = 0; i < particles.Length; i++) {
                    float ph = Seg(t, 0.6f + i * 0.02f, 1f);
                    var sr = particles[i];
                    if (ph <= 0f) { SetAlpha(sr, 0f); continue; }
                    double ang = i * (Math.PI * 2.0 / particles.Length);
                    float r = 0.3f + EaseOut(ph) * 1.9f;
                    sr.transform.localPosition = new Vector3(-0.6f + (float)Math.Cos(ang) * r, -0.2f + (float)Math.Sin(ang) * r * 0.75f + 0.35f * ph, 0f);
                    sr.transform.localScale = Vector3.one * (0.9f + 1.6f * ph);
                    SetAlpha(sr, (1f - ph) * 0.75f * exit);
                }
            }
        }
    }
}
