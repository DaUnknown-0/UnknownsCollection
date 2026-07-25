// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * WerewolfFx - every VISIBLE part of the Werewolf (Paket W1) plus the Hunter's silver-death sequence
 * (Paket W2, item 7 - the kill itself belongs to Hunter.cs, this file only owns the pixels of the
 * VICTIM's animation). Werewolf.cs owns the rules, this file owns the pixels. Effects, all of them
 * deliberately PUBLIC (visible to every client):
 *
 *  1. WOLF SKIN (the beast itself). While the werewolf is transformed, its real cosmetics are hidden
 *     and a single child SpriteRenderer plays the hand-drawn wolf flipbook instead (6-frame idle /
 *     8-frame walk, chosen from the player's movement, flipX from the cosmetics' own facing flag).
 *     Chosen over a per-cosmetic renderer swap (IllusionistClone's approach) because the wolf is not
 *     a crewmate silhouette at all - there is nothing to map hat/visor/skin onto - and because a
 *     single renderer cannot desync from AU's body pivot/animator. The actual renderer-swap mechanic
 *     now lives in UCCharacterSkin (Paket W2 refactor: the Hunter's own skin, HunterFx.cs, needs the
 *     EXACT same mechanic, so it was pulled out into a shared class instead of being copy-pasted).
 *
 *  2. BLOOD RING. A wolf-form kill leaves a ring of blood + paw prints around the corpse, visible to
 *     everyone - the forensic counterweight to the reduced kill cooldown. Needs no RPC: the murder
 *     itself is already broadcast and `wolfForm` is synced, so every client draws its own ring from
 *     its own copy of the same event. Rings are cleared on meeting start (bodies are gone then) and
 *     on the shared UCFx round reset.
 *
 *  3. TRANSFORM FLARE. A short red/dark burst (werewolf_form.png) at the moment of the change, so the
 *     skin swap does not just pop in.
 *
 *  4. SILVER DEATH SEQUENCE (Paket W2). Fired by Werewolf.cs's SilverBulletPatch the instant the
 *     HUNTER lands a lethal silver hit on the wolf-form beast: a one-shot werewolf_death_f00-23
 *     flipbook (~12 fps, holds the last frame) at the victim's position, with werewolf_silver played
 *     back at ~frame 3 (0.25 s) for the "impact" beat. A free-floating world sprite, NOT parented to
 *     the player transform (unlike the wolf skin above) - the victim is dying/dead and its cosmetics
 *     may already be hidden/disabled by the time this plays, so the sequence must survive that.
 *
 *  5. VICTORY SCENE (Paket W4, plan 4.8b). The Impostors won and there was a beast: a dark panel,
 *     the full moon and the one-shot werewolf_victory flipbook take over the end screen for a few
 *     seconds, howl included, then fade out again. The only part of this file that does NOT run on
 *     the UCFx tick - the end-game scene has no HudManager (see its own section below).
 *
 * Driven by UCFx's shared per-frame tick + round-reset registries like every other UC FX class.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class WerewolfFx {
        // ---- wolf skin (UCCharacterSkin, shared mechanic - see UCCharacterSkin.cs) ----
        // 160 px frames. History: 180 ppu (~0.89 units) read as too small next to a crewmate (~0.7),
        // 145 was still not enough - the wolf crouches and never fills its frame vertically. 72.5 ppu
        // is exactly half of 145, i.e. DOUBLE the previous size (~2.2 units, roughly three crewmates
        // tall). The beast is meant to be unmistakable when it steps into the dark.
        private const float SkinPpu = 72.5f;
        private static readonly UCCharacterSkin skin =
            new UCCharacterSkin("Werewolf", "werewolf_skin_idle", 6, "werewolf_skin_walk", 8, SkinPpu);

        // ---- blood rings ----
        private sealed class Ring {
            public GameObject go;
            public SpriteRenderer sr;
            public float start;
        }
        private static readonly List<Ring> rings = new();
        private const float RingBloomSecs = 0.45f;
        private const float RingAlpha = 0.85f;

        // ---- transform flare ----
        private sealed class Flare {
            public GameObject go;
            public SpriteRenderer sr;
            public float start;
            public float life;
        }
        private static readonly List<Flare> flares = new();

        static WerewolfFx() {
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(Clear);
        }

        // Touched once from Werewolf.CreateOptions() so the static constructor (and therefore the
        // UCFx tick/reset registration) definitely runs at plugin start - same reason BeaconFx.Init()
        // exists: nothing else would reference this class before the first transformation.
        public static void Init() { }

        // ==================================================================================
        // Wolf skin (thin wrapper around the shared UCCharacterSkin - see that file for the mechanic)
        // ==================================================================================

        public static bool SkinAttached => skin.Attached;

        public static void AttachSkin(PlayerControl player) => skin.Attach(player);

        public static void DetachSkin() => skin.Detach();

        // ==================================================================================
        // Blood ring
        // ==================================================================================

        public static void SpawnBloodRing(Vector2 at) {
            try {
                var sprite = UCAssets.WerewolfBloodRing;
                if (sprite == null) return;
                // z just above the floor so the ring lies UNDER the corpse sprite but over the tiles.
                var go = new GameObject("WerewolfBloodRing") { layer = 11 };
                go.transform.position = new Vector3(at.x, at.y, at.y / 1000f + 0.002f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = sprite;
                sr.color = new Color(1f, 1f, 1f, 0f);
                rings.Add(new Ring { go = go, sr = sr, start = Time.time });
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Werewolf] blood ring failed: {e.Message}");
            }
        }

        public static void ClearBloodRings() {
            foreach (var r in rings) if (r.go != null) UnityEngine.Object.Destroy(r.go);
            rings.Clear();
        }

        private static void TickRings() {
            for (int i = rings.Count - 1; i >= 0; i--) {
                var r = rings[i];
                if (r.go == null || r.sr == null) { rings.RemoveAt(i); continue; }
                float t = Mathf.Clamp01((Time.time - r.start) / RingBloomSecs);
                float ease = 1f - (1f - t) * (1f - t);       // ease-out bloom
                r.sr.color = new Color(1f, 1f, 1f, RingAlpha * ease);
                r.go.transform.localScale = Vector3.one * (0.55f + 0.45f * ease);
            }
        }

        // ==================================================================================
        // Transform flare
        // ==================================================================================

        public static void SpawnFlare(Vector2 at, float life = 0.55f) {
            try {
                var sprite = UCAssets.WerewolfFormSprite;
                if (sprite == null) return;
                var go = new GameObject("WerewolfFlare") { layer = 11 };
                go.transform.position = new Vector3(at.x, at.y, -1.2f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = sprite;
                sr.color = new Color(1f, 1f, 1f, 0f);
                flares.Add(new Flare { go = go, sr = sr, start = Time.time, life = life });
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Werewolf] flare failed: {e.Message}");
            }
        }

        private static void TickFlares() {
            for (int i = flares.Count - 1; i >= 0; i--) {
                var f = flares[i];
                if (f.go == null || f.sr == null || Time.time - f.start >= f.life) {
                    if (f.go != null) UnityEngine.Object.Destroy(f.go);
                    flares.RemoveAt(i);
                    continue;
                }
                float t = (Time.time - f.start) / f.life;
                float alpha = t < 0.25f ? t / 0.25f : 1f - (t - 0.25f) / 0.75f;
                f.sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha) * 0.9f);
                f.go.transform.localScale = Vector3.one * (0.6f + 0.9f * t);
            }
        }

        // ==================================================================================
        // Silver death sequence (Paket W2, spec item 7) - fired by Werewolf.SilverBulletPatch the
        // instant the Hunter's shot is a LETHAL hit on the wolf-form beast (never on a mere wound, and
        // never on a human-form kill - "Menschform stirbt normal", no special sequence there).
        // ==================================================================================
        private const int DeathFrameCount = 24;
        private const float DeathFps = 12f;         // ~2.0 s for the 24-frame one-shot
        private const float DeathPpu = 200f;        // 224 px frames -> ~1.12 units (blood-ring scale)
        private const float DeathSoundDelay = 3f / DeathFps; // "~frame 3" per WEREWOLF_PLAN.md 4.11
        private const float DeathHoldSecs = 4f;     // how long the last frame is held before fading
        private const float DeathFadeSecs = 1f;

        private sealed class DeathSeq {
            public GameObject go;
            public SpriteRenderer sr;
            public float start;
            public bool soundPlayed;
        }
        private static readonly List<DeathSeq> deathSeqs = new();
        private static Sprite[] deathFrames;
        private static bool deathFramesTried;

        private static void LoadDeathFrames() {
            if (deathFramesTried) return;
            deathFramesTried = true;
            var frames = new Sprite[DeathFrameCount];
            for (int i = 0; i < DeathFrameCount; i++) {
                frames[i] = UCAssets.GetSprite($"UnknownsCollection.Resources.anim.werewolf_death_f{i:00}.png", DeathPpu);
                if (frames[i] == null) { deathFrames = null; return; }
            }
            deathFrames = frames;
        }

        // "at" is a snapshot of the beast's position taken by the CALLER before anything about the
        // kill runs (Prefix, before the murder itself) - the victim's cosmetics/GameObject state after
        // that point is irrelevant, this is a free-floating world sprite, not parented to the player.
        public static void PlaySilverDeath(Vector2 at) {
            try {
                LoadDeathFrames();
                if (deathFrames == null) {
                    // No frames -> at least keep the audible beat so the kill is not completely silent.
                    UCAssets.PlayWerewolfSilverAt(at);
                    return;
                }
                var go = new GameObject("WerewolfSilverDeath") { layer = 11 };
                go.transform.position = new Vector3(at.x, at.y, -1.1f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = deathFrames[0];
                deathSeqs.Add(new DeathSeq { go = go, sr = sr, start = Time.time });
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Werewolf] silver death sequence failed: {e.Message}");
            }
        }

        private static void TickDeathSeqs() {
            for (int i = deathSeqs.Count - 1; i >= 0; i--) {
                var d = deathSeqs[i];
                if (d.go == null || d.sr == null) { deathSeqs.RemoveAt(i); continue; }
                float t = Time.time - d.start;

                if (!d.soundPlayed && t >= DeathSoundDelay) {
                    d.soundPlayed = true;
                    UCAssets.PlayWerewolfSilverAt(d.go.transform.position);
                }

                float animSecs = DeathFrameCount / DeathFps;
                if (t < animSecs) {
                    d.sr.sprite = deathFrames[Mathf.Clamp((int)(t * DeathFps), 0, DeathFrameCount - 1)];
                } else {
                    d.sr.sprite = deathFrames[DeathFrameCount - 1]; // hold the last frame
                    float sinceHold = t - animSecs;
                    if (sinceHold > DeathHoldSecs) {
                        float fadeT = Mathf.Clamp01((sinceHold - DeathHoldSecs) / DeathFadeSecs);
                        var c = d.sr.color;
                        d.sr.color = new Color(c.r, c.g, c.b, 1f - fadeT);
                        if (fadeT >= 1f) {
                            UnityEngine.Object.Destroy(d.go);
                            deathSeqs.RemoveAt(i);
                        }
                    }
                }
            }
        }

        // ==================================================================================
        // VICTORY SCENE (Paket W4, WEREWOLF_PLAN.md 4.8b) - "howling at the moon".
        //
        // The Impostors won and there WAS a werewolf this round: a dark panel slides over the end
        // screen, the full moon rises behind it and the one-shot werewolf_victory flipbook plays in
        // front of it (~12 fps, last frame held), with the howl starting the moment the head is all
        // the way back (~frame 13, 1.08 s). Then the whole thing fades out again and hands the end
        // screen back - the podium and the win text are only ever COVERED, never destroyed.
        //
        // Not driven by UCFx: its tick hangs off HudManager.Update, and the HUD does not exist in
        // the end-game scene at all - hence a self-contained MonoBehaviour (the Bug's end-screen
        // effects use exactly the same construction).
        //
        // ---- WHY THE TRIGGER LOOKS LIKE IT DOES (Paket E!) ----
        // Bug.cs re-encodes a HIJACKED team win as BugHijackBase(20) + original reason, i.e. 20-26
        // and 31 (Bug.cs:67-79). A stolen IMPOSTOR win therefore arrives as 22-25, never as the raw
        // 2-5 this check tests for - so a Bug that took the round away from the Impostors can never
        // trigger the wolf's celebration. The same holds for the legacy flat Bug reason 18, for the
        // Collector's 19 and for the Pelican's 32: none of them is a vanilla impostor reason, so all
        // of them fall through. The check is deliberately written as an explicit whitelist of the
        // four vanilla impostor reasons instead of a "not one of ours" blacklist, so a future UC win
        // reason cannot accidentally opt itself IN.
        // ==================================================================================
        private const int VictoryFrameCount = 24;
        private const float VictoryFps = 12f;
        private const float VictoryPpu = 100f;                       // 224 px frames -> 2.24 units
        private const float VictoryHowlDelay = 13f / VictoryFps;     // "head is back" beat, ~1.08 s

        private static Sprite[] victoryFrames;
        private static bool victoryFramesTried;

        private static void LoadVictoryFrames() {
            if (victoryFramesTried) return;
            victoryFramesTried = true;
            var frames = new Sprite[VictoryFrameCount];
            for (int i = 0; i < VictoryFrameCount; i++) {
                frames[i] = UCAssets.GetSprite($"UnknownsCollection.Resources.anim.werewolf_victory_f{i:00}.png",
                                               VictoryPpu);
                if (frames[i] == null) { victoryFrames = null; return; }
            }
            victoryFrames = frames;
        }

        // The four VANILLA impostor game-over reasons. Anything a mod re-encoded (Bug 18/20-26/31,
        // Collector 19, Pelican 32, TOR's own 10-16) is by definition not in this list.
        private static bool ImpostorTeamWin(int reason) =>
            reason == (int)GameOverReason.ImpostorByVote
            || reason == (int)GameOverReason.ImpostorByKill
            || reason == (int)GameOverReason.ImpostorBySabotage
            || reason == (int)GameOverReason.ImpostorDisconnect;

        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.SetEverythingUp))]
        [HarmonyPriority(Priority.Last)]   // after TOR rebuilt the podium and its bonus line
        static class VictoryScenePatch {
            public static void Postfix(EndGameManager __instance) {
                try {
                    if (__instance == null) return;
                    if (!Werewolf.HadWerewolfThisRound) return;
                    int reason = (int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason;
                    if (!ImpostorTeamWin(reason)) return;

                    LoadVictoryFrames();
                    if (victoryFrames == null) {
                        UnknownsCollectionPlugin.Logger?.LogWarning(
                            "[Werewolf] victory frames missing - scene skipped.");
                        return;
                    }
                    var scene = __instance.gameObject.AddComponent<WerewolfVictoryScene>();
                    scene.mgr = __instance;
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[Werewolf] Victory scene armed (impostor win, reason {reason}).");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] victory scene failed: {e}");
                }
            }
        }

        private class WerewolfVictoryScene : MonoBehaviour {
            static WerewolfVictoryScene() =>
                Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<WerewolfVictoryScene>();

            public EndGameManager mgr;

            private const float PanelFadeIn = 0.4f;
            private const float AnimStart = 0.35f;
            private const float HoldSecs = 2.4f;     // how long the last frame stays up
            private const float FadeOut = 1.1f;
            private const float PanelAlpha = 0.86f;

            private float startTime;
            private bool howled;
            private GameObject root;
            private SpriteRenderer panel, glow, moon, wolf;

            private void Start() {
                try {
                    startTime = Time.time;
                    int layer = mgr != null ? mgr.gameObject.layer : gameObject.layer;

                    root = new GameObject("WerewolfVictory") { layer = layer };
                    root.transform.SetParent(mgr != null ? mgr.transform : transform, false);
                    // In FRONT of the podium beans (z = -8) so the cinematic really covers the screen;
                    // it fades out again at the end, so nothing is permanently hidden.
                    root.transform.localPosition = new Vector3(0f, 0f, -30f);

                    panel = Sr("panel", UCAssets.OverlayWhite, 0f, 0f, 1000, new Color(0.02f, 0.02f, 0.04f, 0f), 400f, layer);
                    glow = Sr("glow", UCFx.Dot, 0.45f, 0.95f, 1001, new Color(1f, 0.96f, 0.78f, 0f), 4.4f, layer);
                    if (glow != null) UCFx.TryMakeAdditive(glow);
                    moon = Sr("moon", UCAssets.WerewolfMoon, 0.45f, 0.95f, 1002, new Color(1f, 1f, 1f, 0f), 0.82f, layer);
                    wolf = Sr("wolf", victoryFrames[0], 0f, -0.5f, 1003, new Color(1f, 1f, 1f, 0f), 1.05f, layer);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] victory scene build failed: {e}");
                    Destroy(this);
                }
            }

            private SpriteRenderer Sr(string name, Sprite sprite, float x, float y, int order,
                                      Color color, float scale, int layer) {
                var go = new GameObject(name) { layer = layer };
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = new Vector3(x, y, 0f);
                go.transform.localScale = new Vector3(scale, scale, 1f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = sprite;
                sr.color = color;
                sr.sortingOrder = order;
                return sr;
            }

            private void Update() {
                try {
                    if (root == null) { Destroy(this); return; }
                    float t = Time.time - startTime;
                    float animSecs = VictoryFrameCount / VictoryFps;
                    float endAt = AnimStart + animSecs + HoldSecs;

                    float fadeIn = Mathf.Clamp01(t / PanelFadeIn);
                    float fadeOut = t > endAt ? Mathf.Clamp01((t - endAt) / FadeOut) : 0f;
                    float a = fadeIn * (1f - fadeOut);

                    SetA(panel, PanelAlpha * a);
                    SetA(glow, 0.30f * a * (0.85f + 0.15f * Mathf.Sin(t * 1.7f)));
                    SetA(moon, a);

                    float wt = t - AnimStart;
                    if (wt >= 0f) {
                        int frame = wt < animSecs
                            ? Mathf.Clamp((int)(wt * VictoryFps), 0, VictoryFrameCount - 1)
                            : VictoryFrameCount - 1;                    // hold the last frame
                        wolf.sprite = victoryFrames[frame];
                        SetA(wolf, Mathf.Clamp01(wt / 0.2f) * a);
                        if (!howled && wt >= VictoryHowlDelay) {
                            howled = true;
                            try { UCAssets.PlayWerewolfHowl(); } catch { }
                        }
                    }

                    if (fadeOut >= 1f) {
                        Destroy(root);
                        root = null;
                        Destroy(this);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Werewolf] victory scene tick: {e.Message}");
                    if (root != null) Destroy(root);
                    root = null;
                    Destroy(this);
                }
            }

            private static void SetA(SpriteRenderer sr, float alpha) {
                if (sr == null) return;
                var c = sr.color;
                sr.color = new Color(c.r, c.g, c.b, Mathf.Clamp01(alpha));
            }
        }

        // ==================================================================================
        // Drivers
        // ==================================================================================

        private static void Tick() {
            try { skin.Tick(); } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[Werewolf] skin tick: {e.Message}"); }
            try { TickRings(); } catch { }
            try { TickFlares(); } catch { }
            try { TickDeathSeqs(); } catch { }
        }

        private static void Clear() {
            try { DetachSkin(); } catch { }
            try { ClearBloodRings(); } catch { }
            try {
                foreach (var f in flares) if (f.go != null) UnityEngine.Object.Destroy(f.go);
                flares.Clear();
            } catch { }
            try {
                foreach (var d in deathSeqs) if (d.go != null) UnityEngine.Object.Destroy(d.go);
                deathSeqs.Clear();
            } catch { }
        }
    }
}
