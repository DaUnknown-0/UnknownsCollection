// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * BugCrashScreen - what every living player sees when the Bug's System Crash hits a meeting (BugCrash):
 *   1. GLITCH (GlitchSeconds): the real screen, captured at the end of the frame, is cut into horizontal bands that
 *      tear sideways, swap places and split into red/cyan, growing worse until it breaks.
 *   2. BLUE SCREEN (BlueSeconds): a sad face, "collecting info", a percentage that jumps up.
 *   3. REBOOT: a BIOS screen with a six character recovery code that flickers (characters glitch and swap for a
 *      moment). Type it before the time runs out (letters and digits, Backspace deletes, a wrong code starts over).
 *   4. RESULT: rebooting, or "boot failed" -> BugCrash votes Skip for this player once the voting is open.
 * A screen-space canvas on top of everything; while it runs the game's buttons take no clicks and the chat is shut.
 * Only ASCII and Latin-1 text (TMP's default font).
 */

using System;
using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UnknownsCollection {
    internal static class BugCrashScreen {
        private enum Phase { Off, Capture, Glitch, Blue, Reboot, Result }
        private static Phase phase = Phase.Off;

        private const float GlitchSeconds = 1.6f, BlueSeconds = 3f, ResultSeconds = 1.8f;
        private const int Bands = 24, NoiseBlocks = 10, CodeLength = 6;
        private const string CodeChars = "ACDEFGHJKLMNPQRTUVWXY34679";   // nothing that reads as 0/O or 1/I
        private const float RefW = 1920f, RefH = 1080f;

        private static readonly Color BsodBlue = new Color(0f, 0.47f, 0.84f, 1f);
        private static readonly Color BiosGrey = new Color(0.75f, 0.75f, 0.75f, 1f);
        private static readonly Color BiosAmber = new Color(1f, 0.78f, 0.25f, 1f);
        private static readonly Color Good = new Color(0.4f, 1f, 0.5f, 1f);
        private static readonly Color Bad = new Color(1f, 0.35f, 0.3f, 1f);

        private static GameObject root, glitchLayer, blueLayer, biosLayer;
        private static Texture2D shot;
        private static RawImage[] bands;
        private static Image[] noise;
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
            try { shot = ScreenCapture.CaptureScreenshotAsTexture(); }
            catch (Exception e) { shot = null; UnknownsCollectionPlugin.Logger?.LogWarning($"[Bug] crash screenshot failed: {e.Message}"); }
            if (shot != null) {
                shot.wrapMode = TextureWrapMode.Clamp;
                shot.hideFlags |= HideFlags.HideAndDontSave;
            }
            try {
                Build();
                phase = Phase.Glitch;
                phaseStart = Time.time;
                nextJitter = 0f;
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
            FreeShot();
        }

        private static void FreeShot() {
            try { if (shot != null) UnityEngine.Object.Destroy(shot); } catch { }
            shot = null;
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
            float amp = Mathf.Lerp(0.01f, 0.16f, p * p) * RefW;
            for (int i = 0; i < Bands; i++) {
                var b = bands[i];
                if (b == null) continue;
                bool hit = rng.NextDouble() < Mathf.Lerp(0.15f, 0.8f, p);
                b.rectTransform.anchoredPosition = new Vector2(hit ? ((float)rng.NextDouble() * 2f - 1f) * amp : 0f, 0f);
                if (shot != null) {
                    int src = hit && rng.NextDouble() < p * 0.5f ? rng.Next(Bands) : i;
                    b.uvRect = new Rect(0f, src / (float)Bands, 1f, 1f / Bands);
                    double r = rng.NextDouble();
                    b.color = !hit ? Color.white
                              : r < 0.25 ? new Color(1f, 0.35f, 0.35f) : r < 0.5 ? new Color(0.35f, 1f, 1f)
                              : r < 0.6 ? new Color(0.45f, 1f, 0.5f) : Color.white;
                } else {
                    float g = (float)rng.NextDouble() * 0.35f;
                    b.color = new Color(g, g + (hit ? 0.2f : 0f), g, 1f);
                }
            }
            for (int i = 0; i < NoiseBlocks; i++) {
                var n = noise[i];
                if (n == null) continue;
                bool on = rng.NextDouble() < p * 0.6f;
                n.enabled = on;
                if (!on) continue;
                var rt = n.rectTransform;
                rt.anchoredPosition = new Vector2((float)rng.NextDouble() * RefW, -(float)rng.NextDouble() * RefH);
                rt.sizeDelta = new Vector2(40f + (float)rng.NextDouble() * 420f, 6f + (float)rng.NextDouble() * 60f);
                double c = rng.NextDouble();
                n.color = c < 0.3 ? Color.black : c < 0.55 ? Color.white : c < 0.8 ? new Color(0.2f, 1f, 0.35f) : new Color(1f, 0.2f, 0.9f);
            }
            if (rng.NextDouble() < 0.3) UCAssets.PlayBugGlitch(0.2f);
        }

        private static void StartBlue() {
            phase = Phase.Blue;
            phaseStart = Time.time;
            glitchLayer.SetActive(false);
            blueLayer.SetActive(true);
            FreeShot();
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

            // 1. the torn screen
            glitchLayer = Layer("glitch", Color.black);
            bands = new RawImage[Bands];
            for (int i = 0; i < Bands; i++) {
                var go = new GameObject("band");
                go.transform.SetParent(glitchLayer.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, i / (float)Bands);
                rt.anchorMax = new Vector2(1f, (i + 1) / (float)Bands);
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                var img = go.AddComponent<RawImage>();
                img.raycastTarget = false;
                if (shot != null) {
                    img.texture = shot;
                    img.uvRect = new Rect(0f, i / (float)Bands, 1f, 1f / Bands);
                }
                bands[i] = img;
            }
            noise = new Image[NoiseBlocks];
            for (int i = 0; i < NoiseBlocks; i++) {
                var go = new GameObject("noise");
                go.transform.SetParent(glitchLayer.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                var img = go.AddComponent<Image>();
                img.raycastTarget = false;
                img.enabled = false;
                noise[i] = img;
            }

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
