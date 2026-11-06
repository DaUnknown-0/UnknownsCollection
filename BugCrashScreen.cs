// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * BugCrashScreen - what every living player sees when the Bug's System Crash hits a meeting (BugCrash):
 *   1. GLITCH (GlitchSeconds): the real screen, captured at the end of the frame and shrunk to GlitchW pixels across,
 *      shown pixelated and corrupted more and more (User 06.11., reference "digital glitch effect on pixels"): rows
 *      smear sideways like pixel sorting, bands slip, streaks of orange/teal/pink, macroblocks with a grid pattern,
 *      short vertical dashes, and everything sinks into blue until only blue streaks are left and the blue screen
 *      takes over.
 *   2. BLUE SCREEN (BlueSeconds): a sad face, "collecting info", a percentage that jumps up.
 *   3. REBOOT: a BIOS screen with a six character recovery code that flickers (characters glitch and swap for a
 *      moment). Type it before the time runs out (letters and digits, Backspace deletes, a wrong code starts over).
 *   4. RESULT: rebooting, or "boot failed" -> BugCrash votes Skip for this player once the voting is open.
 * A screen-space canvas on top of everything; while it runs the game's buttons take no clicks and the chat is shut.
 * Only ASCII and Latin-1 text (TMP's default font).
 *
 * Music (User 2026-11-06: the meeting music shall go quiet for a moment as well): during the glitch and the blue screen
 * every source on the game's music channel is muted (Forgotten Fixes' meeting music and any other mod's music, all of
 * them play there) and comes back with the reboot screen. Only `mute` is touched, never the volume the owners set, and
 * only the sources muted here are switched back on.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UnknownsCollection {
    internal static class BugCrashScreen {
        private enum Phase { Off, Capture, Glitch, Blue, Reboot, Result }
        private static Phase phase = Phase.Off;

        private const float GlitchSeconds = 1.8f, BlueSeconds = 3f, ResultSeconds = 1.8f;
        private const int GlitchW = 480, CodeLength = 6;
        private const string CodeChars = "ACDEFGHJKLMNPQRTUVWXY34679";   // nothing that reads as 0/O or 1/I
        private const float RefW = 1920f, RefH = 1080f;

        private static readonly Color BsodBlue = new Color(0f, 0.47f, 0.84f, 1f);
        private static readonly Color BiosGrey = new Color(0.75f, 0.75f, 0.75f, 1f);
        private static readonly Color BiosAmber = new Color(1f, 0.78f, 0.25f, 1f);
        private static readonly Color Good = new Color(0.4f, 1f, 0.5f, 1f);
        private static readonly Color Bad = new Color(1f, 0.35f, 0.3f, 1f);

        private static GameObject root, glitchLayer, blueLayer, biosLayer;
        private static Texture2D glitchTex;      // GlitchW wide, point filtered, rewritten every tick
        private static Color32[] basePx, buf;    // the shrunk screenshot, and the corrupted frame
        private static int gw, gh;
        private static TextMeshProUGUI blueProgress, biosCode, biosInput, biosStatus, biosTime;

        private static float phaseStart, rebootSeconds, rebootLeft, nextJitter, closeAt;
        private static string code = "", typed = "";
        private static float flickerUntil, swapUntil;
        private static int flickerIdx = -1, swapIdx = -1;
        private static char flickerChar;

        private static readonly System.Random rng = new System.Random();

        internal static bool Active => phase != Phase.Off;
        private static bool Blocking => phase != Phase.Off && phase != Phase.Result;

        private static string Tr(string key, params object[] args) => UCLocalization.Tr("uc.ui.bug.crash." + key, args);

        internal static void Play(float seconds) {
            try {
                if (Active) return;
                var hud = HudManager.Instance;
                if (hud == null) return;
                rebootSeconds = Mathf.Max(3f, seconds);
                phase = Phase.Capture;
                CloseChat();
                hud.StartCoroutine(CaptureThenStart().WrapToIl2Cpp());
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Bug] crash screen start failed: {e}");
                Close();
            }
        }

        // The screenshot needs the finished frame.
        private static IEnumerator CaptureThenStart() {
            yield return new WaitForEndOfFrame();
            if (phase != Phase.Capture) yield break;
            try { CaptureBase(); }
            catch (Exception e) { basePx = null; UnknownsCollectionPlugin.Logger?.LogWarning($"[Bug] crash screenshot failed: {e.Message}"); }
            try {
                if (basePx == null) FallbackBase();
                Build();
                Render(0f);
                phase = Phase.Glitch;
                phaseStart = Time.time;
                nextJitter = 0f;
                HushMusic();
                UCAssets.PlayBugGlitch(UCAssets.VolStd);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Bug] crash screen build failed: {e}");
                Close();
            }
        }

        internal static void Close() {
            phase = Phase.Off;
            try { if (root != null) UnityEngine.Object.Destroy(root); } catch { }
            root = null;
            FreeGlitch();
            UnhushMusic();
        }

        // ---- music channel: muted during glitch and blue screen ----
        private static readonly List<AudioSource> hushed = new List<AudioSource>();
        private static float nextHush;

        // SoundManager.instance (the field), never .Instance: the property creates an empty SoundManager when none exists.
        private static void HushMusic() {
            nextHush = Time.time + 0.4f;   // again every 0.4 s: music that starts meanwhile is caught too
            try {
                var sm = SoundManager.instance;
                var music = sm != null ? sm.MusicChannel : null;
                if (music == null) return;
                foreach (var a in UnityEngine.Object.FindObjectsOfType<AudioSource>()) {
                    try {
                        if (a == null || a.mute || a.outputAudioMixerGroup != music) continue;
                        a.mute = true;
                        hushed.Add(a);
                    } catch { }
                }
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[Bug] muting the music failed: {e.Message}"); }
        }

        private static void UnhushMusic() {
            foreach (var a in hushed) {
                try { if (a != null) a.mute = false; } catch { }
            }
            hushed.Clear();
        }

        private static void FreeGlitch() {
            try { if (glitchTex != null) UnityEngine.Object.Destroy(glitchTex); } catch { }
            glitchTex = null;
            basePx = buf = null;
        }

        // The finished frame, shrunk on the GPU (Blit into a small render texture) and read back once.
        private static void CaptureBase() {
            var shot = ScreenCapture.CaptureScreenshotAsTexture();
            if (shot == null) return;
            RenderTexture rt = null, prev = RenderTexture.active;
            try {
                gw = GlitchW;
                gh = Mathf.Clamp(Mathf.RoundToInt(GlitchW * (float)shot.height / Mathf.Max(1, shot.width)), 120, 480);
                rt = RenderTexture.GetTemporary(gw, gh, 0);
                Graphics.Blit(shot, rt);
                RenderTexture.active = rt;
                var small = new Texture2D(gw, gh, TextureFormat.RGBA32, false);
                small.ReadPixels(new Rect(0, 0, gw, gh), 0, 0);
                small.Apply(false);
                var px = small.GetPixels32();
                basePx = new Color32[gw * gh];
                for (int i = 0; i < basePx.Length && i < px.Length; i++) basePx[i] = px[i];
                UnityEngine.Object.Destroy(small);
            } finally {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(shot);
            }
        }

        // No screenshot: a dark, noisy meeting-blue to corrupt instead.
        private static void FallbackBase() {
            gw = GlitchW;
            gh = 270;
            basePx = new Color32[gw * gh];
            for (int i = 0; i < basePx.Length; i++) {
                byte v = (byte)rng.Next(20, 60);
                basePx[i] = new Color32(v, (byte)(v + 10), (byte)(v + 40), 255);
            }
        }

        private static void CloseChat() {
            try {
                var chat = HudManager.Instance != null ? HudManager.Instance.Chat : null;
                if (chat != null && chat.IsOpenOrOpening) chat.ForceClosed();
            } catch { }
        }

        // ====================================================================
        // Per frame
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class UpdatePatch {
            public static void Postfix() {
                if (phase == Phase.Off || phase == Phase.Capture) return;
                try { Tick(); } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] crash screen tick failed: {e}");
                    Close();
                }
            }
        }

        private static void Tick() {
            var me = PlayerControl.LocalPlayer;
            if (root == null || MeetingHud.Instance == null || me == null || me.Data == null || me.Data.IsDead) { Close(); return; }
            if (Blocking) CloseChat();
            if ((phase == Phase.Glitch || phase == Phase.Blue) && Time.time >= nextHush) HushMusic();
            switch (phase) {
                case Phase.Glitch: TickGlitch(); break;
                case Phase.Blue: TickBlue(); break;
                case Phase.Reboot: TickReboot(); break;
                case Phase.Result: if (Time.time >= closeAt) Close(); break;
            }
        }

        private static void TickGlitch() {
            float p = Mathf.Clamp01((Time.time - phaseStart) / GlitchSeconds);
            if (p >= 1f) { StartBlue(); return; }
            if (Time.time < nextJitter) return;
            nextJitter = Time.time + Mathf.Lerp(0.09f, 0.03f, p);
            Render(p);
            if (rng.NextDouble() < 0.3) UCAssets.PlayBugGlitch(0.2f);
        }

        // Colours of the reference: the streaks are mostly blue of every brightness with some orange and red, the blocks
        // mostly teal and green. Repeated entries weight the pick.
        private static readonly Color32[] Accents = {
            new Color32(240, 110, 40, 255), new Color32(240, 110, 40, 255), new Color32(225, 70, 45, 255),
            new Color32(120, 190, 255, 255), new Color32(230, 110, 190, 255), new Color32(240, 220, 120, 255),
        };
        private static readonly Color32[] BlockColors = {
            new Color32(60, 210, 160, 255), new Color32(60, 210, 160, 255), new Color32(80, 230, 120, 255),
            new Color32(230, 110, 190, 255), new Color32(240, 110, 40, 255),
        };

        private static Color32 Shade(Color32 c, float k) =>
            new Color32((byte)Mathf.Min(255f, c.r * k), (byte)Mathf.Min(255f, c.g * k), (byte)Mathf.Min(255f, c.b * k), 255);

        private static Color32 Blue(Color32 c, float t) {
            // toward a blue of the same brightness: dark navy to light sky blue
            float l = (c.r * 0.3f + c.g * 0.59f + c.b * 0.11f) / 255f;
            byte r = (byte)Mathf.Lerp(c.r, 10f + 70f * l, t);
            byte g = (byte)Mathf.Lerp(c.g, 60f + 120f * l, t);
            byte b = (byte)Mathf.Lerp(c.b, 150f + 105f * l, t);
            return new Color32(r, g, b, 255);
        }

        private static int Rand(int min, int max) => max <= min ? min : rng.Next(min, max);

        // One corrupted frame for progress p (0..1): slipped rows, blue cast, smeared streaks, macroblocks, dashes.
        private static void Render(float p) {
            if (glitchTex == null || basePx == null) return;
            int n = gw * gh;
            if (buf == null || buf.Length != n) buf = new Color32[n];
            float blue = 0.1f + 0.85f * p * p;

            // rows slip sideways in bands of 1..14 rows
            int bandLeft = 0, shift = 0, maxShift = Mathf.Max(1, (int)(gw * 0.3f * p * p));
            for (int y = 0; y < gh; y++) {
                if (bandLeft <= 0) {
                    bandLeft = Rand(1, 15);
                    shift = rng.NextDouble() < 0.1 + 0.6 * p ? Rand(-maxShift, maxShift + 1) : 0;
                }
                bandLeft--;
                int row = y * gw;
                for (int x = 0; x < gw; x++) {
                    int sx = (x - shift) % gw;
                    if (sx < 0) sx += gw;
                    buf[row + x] = Blue(basePx[row + sx], blue);
                }
            }

            // pixel-sort streaks: a pixel's colour dragged along its row, sometimes an accent colour
            int streaks = (int)(20 + 1400 * p * p);
            int maxLen = (int)(12 + gw * 0.7f * p);
            for (int i = 0; i < streaks; i++) {
                int y = rng.Next(gh), x0 = rng.Next(gw), len = Rand(4, maxLen);
                Color32 c = rng.NextDouble() < 0.06 + 0.12 * p ? Accents[rng.Next(Accents.Length)]
                            : Shade(buf[y * gw + x0], 0.75f + 0.55f * (float)rng.NextDouble());
                int thick = rng.NextDouble() < 0.2 ? 2 : 1;
                for (int t = 0; t < thick && y + t < gh; t++) {
                    int row = (y + t) * gw;
                    for (int x = x0; x < x0 + len && x < gw; x++) buf[row + x] = c;
                }
            }

            // macroblocks: copied from elsewhere (datamosh) or filled with a grid pattern
            int blocks = (int)(2 + 30 * p);
            for (int i = 0; i < blocks; i++) {
                int bw = Rand(6, 28), bh = Rand(4, 18), bx = rng.Next(gw), by = rng.Next(gh);
                bool copy = rng.NextDouble() < 0.5;
                int sx0 = rng.Next(gw), sy0 = rng.Next(gh);
                Color32 c = BlockColors[rng.Next(BlockColors.Length)];
                Color32 dark = new Color32((byte)(c.r / 2), (byte)(c.g / 2), (byte)(c.b / 2), 255);
                for (int y = by; y < by + bh && y < gh; y++)
                    for (int x = bx; x < bx + bw && x < gw; x++) {
                        if (copy) buf[y * gw + x] = buf[((sy0 + y - by) % gh) * gw + (sx0 + x - bx) % gw];
                        else buf[y * gw + x] = ((x - bx) % 3 == 0 || (y - by) % 3 == 0) ? dark : c;
                    }
            }

            // short vertical dashes
            int dashes = (int)(25 * p);
            for (int i = 0; i < dashes; i++) {
                int x = rng.Next(gw), y0 = rng.Next(gh), len = Rand(2, 10);
                Color32 c = rng.NextDouble() < 0.5 ? new Color32(230, 240, 255, 255) : Accents[rng.Next(Accents.Length)];
                for (int y = y0; y < y0 + len && y < gh; y++) buf[y * gw + x] = c;
            }

            glitchTex.SetPixels32(buf);
            glitchTex.Apply(false);
        }

        private static void StartBlue() {
            phase = Phase.Blue;
            phaseStart = Time.time;
            glitchLayer.SetActive(false);
            blueLayer.SetActive(true);
            FreeGlitch();
            UCAssets.PlayBugGlitch(UCAssets.VolStd);
        }

        private static void TickBlue() {
            float p = Mathf.Clamp01((Time.time - phaseStart) / BlueSeconds);
            // jumps like the real thing: stuck, then a leap
            int pct = Mathf.Min(100, Mathf.FloorToInt(Mathf.Pow(p, 1.7f) * 10f) * 10 + (p > 0.35f ? 7 : 0));
            blueProgress.text = Tr("progress", Mathf.Min(100, pct));
            if (p >= 1f) StartReboot();
        }

        private static void StartReboot() {
            phase = Phase.Reboot;
            phaseStart = Time.time;
            blueLayer.SetActive(false);
            biosLayer.SetActive(true);
            UnhushMusic();
            var sb = new System.Text.StringBuilder(CodeLength);
            for (int i = 0; i < CodeLength; i++) sb.Append(CodeChars[rng.Next(CodeChars.Length)]);
            code = sb.ToString();
            typed = "";
            rebootLeft = rebootSeconds;
            flickerIdx = swapIdx = -1;
            biosStatus.text = "";
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Bug] crash reboot code {code}, {rebootSeconds:0} s.");
        }

        private static void TickReboot() {
            rebootLeft -= Time.deltaTime;
            string input = "";
            try { input = Input.inputString ?? ""; } catch { }
            foreach (char ch in input) {
                if (ch == '\b') { if (typed.Length > 0) typed = typed.Substring(0, typed.Length - 1); continue; }
                char u = char.ToUpperInvariant(ch);
                if (u > 127 || !char.IsLetterOrDigit(u) || typed.Length >= CodeLength) continue;
                typed += u;
                if (typed.Length < CodeLength) continue;
                if (typed == code) { Finish(true); return; }
                typed = "";
                biosStatus.text = Tr("wrong");
                biosStatus.color = Bad;
                UCAssets.PlaySaboteurWireWrong();
            }
            if (rebootLeft <= 0f) { Finish(false); return; }

            biosCode.text = ShownCode();
            var sb = new System.Text.StringBuilder("> ");
            for (int i = 0; i < CodeLength; i++) {
                sb.Append(i < typed.Length ? typed[i] : '_');
                sb.Append(i == 2 ? "  " : " ");
            }
            biosInput.text = sb.ToString();
            int secs = Mathf.CeilToInt(rebootLeft);
            biosTime.text = Tr("time", secs);
            biosTime.color = rebootLeft > 3f || Mathf.Repeat(Time.time, 0.5f) < 0.3f ? BiosGrey : Bad;
        }

        // The code as shown: now and then a character turns into another one, or two neighbours swap, for a moment.
        private static string ShownCode() {
            float now = Time.time;
            if (now >= flickerUntil && rng.NextDouble() < 0.06) {
                flickerIdx = rng.Next(CodeLength);
                flickerChar = CodeChars[rng.Next(CodeChars.Length)];
                flickerUntil = now + 0.12f;
            }
            if (now >= swapUntil && rng.NextDouble() < 0.025) {
                swapIdx = rng.Next(CodeLength - 1);
                swapUntil = now + 0.18f;
            }
            var c = code.ToCharArray();
            if (now < swapUntil && swapIdx >= 0) (c[swapIdx], c[swapIdx + 1]) = (c[swapIdx + 1], c[swapIdx]);
            if (now < flickerUntil && flickerIdx >= 0) c[flickerIdx] = flickerChar;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < CodeLength; i++) {
                sb.Append(c[i]);
                sb.Append(i == 2 ? "  -  " : i < CodeLength - 1 ? " " : "");
            }
            return sb.ToString();
        }

        private static void Finish(bool ok) {
            phase = Phase.Result;
            closeAt = Time.time + ResultSeconds;
            biosCode.text = "";
            biosInput.text = "";
            biosTime.text = "";
            biosStatus.text = Tr(ok ? "ok" : "fail");
            biosStatus.color = ok ? Good : Bad;
            if (ok) UCAssets.PlaySaboteurDefused();
            else {
                UCAssets.PlaySaboteurAlarm();
                BugCrash.RebootFailed();
            }
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Bug] crash reboot {(ok ? "done" : "failed")}.");
        }

        // The game's buttons (votes, skip, chat) take no clicks while the crash runs.
        [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickDown))]
        static class BlockClickDown { public static bool Prefix() => !Blocking; }
        [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickUp))]
        static class BlockClickUp { public static bool Prefix() => !Blocking; }
        [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveRepeatDown))]
        static class BlockRepeat { public static bool Prefix() => !Blocking; }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class JoinPatch { public static void Postfix() => Close(); }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch { public static void Prefix() => Close(); }

        // ====================================================================
        // Building
        // ====================================================================
        private static void Build() {
            root = new GameObject("UCBugCrash");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9100;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(RefW, RefH);
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();

            // 1. the corrupted screen: one pixelated picture over the whole screen
            glitchLayer = Layer("glitch", Color.black);
            glitchTex = new Texture2D(gw, gh, TextureFormat.RGBA32, false) {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            glitchTex.hideFlags |= HideFlags.HideAndDontSave;
            var pic = new GameObject("pixels");
            pic.transform.SetParent(glitchLayer.transform, false);
            var prt = pic.AddComponent<RectTransform>();
            prt.anchorMin = Vector2.zero;
            prt.anchorMax = Vector2.one;
            prt.offsetMin = prt.offsetMax = Vector2.zero;
            var img = pic.AddComponent<RawImage>();
            img.raycastTarget = false;
            img.texture = glitchTex;

            // 2. the blue screen
            blueLayer = Layer("blue", BsodBlue);
            Label(blueLayer, ":(", 190f, Color.white, new Vector2(200f, -150f), new Vector2(600f, 260f));
            Label(blueLayer, Tr("line1"), 44f, Color.white, new Vector2(200f, -430f), new Vector2(1500f, 120f));
            Label(blueLayer, Tr("line2"), 44f, Color.white, new Vector2(200f, -540f), new Vector2(1500f, 120f));
            blueProgress = Label(blueLayer, Tr("progress", 0), 44f, Color.white, new Vector2(200f, -680f), new Vector2(1000f, 70f));
            Label(blueLayer, Tr("stop"), 26f, Color.white, new Vector2(200f, -860f), new Vector2(1500f, 50f));
            blueLayer.SetActive(false);

            // 3. the reboot
            biosLayer = Layer("bios", new Color(0.02f, 0.02f, 0.03f, 1f));
            Label(biosLayer, Tr("bios"), 34f, BiosGrey, new Vector2(120f, -90f), new Vector2(1600f, 50f));
            Label(biosLayer, Tr("line_sector"), 34f, Bad, new Vector2(120f, -170f), new Vector2(1600f, 50f));
            Label(biosLayer, Tr("line_type"), 34f, BiosGrey, new Vector2(120f, -250f), new Vector2(1600f, 50f));
            biosCode = Label(biosLayer, "", 110f, Color.white, new Vector2(0f, -380f), new Vector2(RefW, 150f), TextAlignmentOptions.Center);
            biosInput = Label(biosLayer, "", 80f, BiosAmber, new Vector2(0f, -560f), new Vector2(RefW, 120f), TextAlignmentOptions.Center);
            biosStatus = Label(biosLayer, "", 46f, Bad, new Vector2(0f, -720f), new Vector2(RefW, 80f), TextAlignmentOptions.Center);
            biosTime = Label(biosLayer, "", 34f, BiosGrey, new Vector2(120f, -900f), new Vector2(800f, 50f));
            Label(biosLayer, Tr("hint"), 28f, BiosGrey, new Vector2(120f, -960f), new Vector2(1600f, 50f));
            biosLayer.SetActive(false);
        }

        private static GameObject Layer(string name, Color background) {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = background;
            img.raycastTarget = true;
            return go;
        }

        // Text placed by its top-left corner (reference pixels from the top-left of the screen).
        private static TextMeshProUGUI Label(GameObject parent, string text, float size, Color color, Vector2 topLeft, Vector2 box,
                                             TextAlignmentOptions align = TextAlignmentOptions.TopLeft) {
            var go = new GameObject("text");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = topLeft;
            rt.sizeDelta = box;
            var t = go.AddComponent<TextMeshProUGUI>();
            try { var f = TMP_Settings.defaultFontAsset; if (f != null) t.font = f; } catch { }
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.enableWordWrapping = true;
            t.raycastTarget = false;
            t.text = text;
            return t;
        }
    }
}
