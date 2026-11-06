// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * ManiacDefuse - the bomb defuse minigame in a meeting (User 2026-11-06: while somebody carries a Maniac bomb and a
 * meeting is called, that player gets a minigame shortly after the meeting starts to defuse it; enough time, but not
 * too easy).
 *
 * Three steps against one clock (option 1692, default 30 s; the host shortens it to what is left of the meeting):
 *   1. WIRES: five coloured wires, a serial number on the bomb and a manual with one condition and two rules. Click the
 *      wire the manual points at. A wrong wire costs WrongWirePenalty seconds and stays cut.
 *   2./3. NEEDLE: a needle sweeps across a gauge; stop it in the green zone (Space, E, Enter or a click). The zone
 *      shrinks and the needle speeds up for step 3; a miss costs MissPenalty seconds and moves the zone.
 * Only the carrier sees it, nobody else learns anything. The result goes to the host (Maniac.SendDefuseResult):
 * defused = the bomb is gone; out of time, or the voting ends first = the carrier dies when the meeting ends.
 * While the steps run the panel is modal: the meeting's buttons (votes, skip, chat) take no clicks.
 *
 * Drawn like GamblerUI: world-space panel under the HudManager, fitted to the "UI Camera" every frame, hit tests in
 * panel-local space, only ASCII (plus umlauts) in the HUD font.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace UnknownsCollection {
    public static class ManiacDefuse {
        // ---- layout (design units, fitted to the camera every frame) ----
        private const float PanelW = 7.4f, PanelH = 4.5f, HeaderH = 0.55f, DesignOrtho = 3f;
        private const float BoxX = -1.75f, BoxY = -0.15f, BoxW = 3.6f, BoxH = 3.3f;
        private const float CardX = 1.95f, CardW = 3.1f;
        private const float WireLen = 3.0f, GaugeW = 3.0f;
        private const int SortBg = 600, SortMid = 610, SortTop = 615, SortText = 620;

        // ---- rules ----
        private const int WireCount = 5, ColorCount = 5;
        private const float WrongWirePenalty = 5f, MissPenalty = 3f, InputDeadTime = 0.35f;

        private static readonly Color PanelBg = new Color(0.06f, 0.06f, 0.07f, 0.97f);
        private static readonly Color HeaderBg = new Color(0.32f, 0.05f, 0.04f, 0.98f);
        private static readonly Color BorderCol = new Color(0.9f, 0.22f, 0.15f, 0.95f);
        private static readonly Color BoxCol = new Color(0.17f, 0.18f, 0.2f, 1f);
        private static readonly Color Paper = new Color(0.86f, 0.82f, 0.68f, 1f);
        private static readonly Color Ink = new Color(0.12f, 0.1f, 0.08f, 1f);
        private static readonly Color Dim = new Color(0.65f, 0.67f, 0.7f, 1f);
        private static readonly Color Good = new Color(0.4f, 1f, 0.5f, 1f);
        private static readonly Color Bad = new Color(1f, 0.32f, 0.25f, 1f);

        private static readonly string[] ColorKeys = { "red", "blue", "yellow", "white", "green" };
        private static readonly Color[] WireColors = {
            new Color(0.93f, 0.22f, 0.2f), new Color(0.25f, 0.5f, 0.98f), new Color(1f, 0.86f, 0.2f),
            new Color(0.94f, 0.94f, 0.94f), new Color(0.3f, 0.85f, 0.35f),
        };

        private enum Phase { Closed, Wires, Needle, Won, Lost }
        private static Phase phase = Phase.Closed;
        private static int needleStep;                 // 0 = step 2, 1 = step 3
        private static float timeLeft, armedAt, closeAt;
        private static bool resultSent, fuseOn;

        // wires
        private static readonly int[] wireColor = new int[WireCount];
        private static readonly bool[] wireCut = new bool[WireCount];
        private static int answer;
        private static string serial = "";
        private static string manual = "";

        // needle
        private static float zoneCenter, zoneHalf, needleSpeed, needleOffset;

        private static readonly System.Random rng = new System.Random();

        public static bool IsOpen => phase != Phase.Closed;
        private static bool Running => phase == Phase.Wires || phase == Phase.Needle;

        private static string Tr(string key, params object[] args) => UCLocalization.Tr("uc.ui.maniac.defuse." + key, args);
        private static string ColorName(int c) => Tr("color." + ColorKeys[c]);

        // ====================================================================
        // Open / close
        // ====================================================================
        public static void Open(float seconds) {
            try {
                if (IsOpen || HudManager.Instance == null || MeetingHud.Instance == null) return;
                NewPuzzle();
                Build();
                if (panel == null) return;
                timeLeft = Mathf.Max(5f, seconds);
                resultSent = false;
                phase = Phase.Wires;
                armedAt = Time.time + 0.6f;
                ShowWires(true);
                ShowGauge(false);
                stepText.text = Tr("step", 1);
                manualText.text = manual;
                hintText.text = Tr("hint_wires", (int)WrongWirePenalty);
                SetStatus(Tr("intro"), Bad);
                try { UCAssets.PlayFuseLoop(0.45f); fuseOn = true; } catch { }
                UCAssets.PlaySaboteurAlarm(UCAssets.VolSoft);
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Maniac] defuse minigame open: {seconds:0} s, serial {serial}, answer wire {answer + 1}.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] defuse open failed: {e}");
                Close();
            }
        }

        public static void Close() {
            if (fuseOn) { try { UCAssets.StopFuseLoop(); } catch { } fuseOn = false; }
            phase = Phase.Closed;
            try { if (panel != null) UnityEngine.Object.Destroy(panel); } catch { }
            panel = null;
        }

        private static void Win() {
            phase = Phase.Won;
            closeAt = Time.time + 1.6f;
            if (fuseOn) { try { UCAssets.StopFuseLoop(); } catch { } fuseOn = false; }
            UCAssets.PlaySaboteurDefused();
            title.text = Tr("won");
            title.color = Good;
            SetStatus(Tr("won_line"), Good);
            hintText.text = "";
            Send(true);
        }

        private static void Lose() {
            if (!Running) return;
            phase = Phase.Lost;
            closeAt = Time.time + 2.6f;
            if (fuseOn) { try { UCAssets.StopFuseLoop(); } catch { } fuseOn = false; }
            UCAssets.PlaySaboteurAlarm();
            try { TheOtherRoles.Helpers.showFlash(new Color(1f, 0.25f, 0.1f, 0.45f), 0.5f); } catch { }
            title.text = Tr("lost");
            title.color = Bad;
            timerText.text = "0:00";
            SetStatus(Tr("lost_line"), Bad);
            hintText.text = "";
            Send(false);
        }

        private static void Send(bool ok) {
            if (resultSent) return;
            resultSent = true;
            Maniac.SendDefuseResult(ok);
        }

        // ====================================================================
        // The puzzle
        // ====================================================================
        private static int Count(int c) { int n = 0; for (int i = 0; i < WireCount; i++) if (wireColor[i] == c) n++; return n; }

        private static int Nth(int c, int nth) {
            for (int i = 0, k = 0; i < WireCount; i++)
                if (wireColor[i] == c && k++ == nth) return i;
            return -1;
        }

        private static int LastOf(int c) {
            for (int i = WireCount - 1; i >= 0; i--) if (wireColor[i] == c) return i;
            return -1;
        }

        // One rule: its text, the wire it points at (-1 = impossible with these wires) and whether it is a plain position.
        private static (string text, int wire, bool plain) MakeRule() {
            int c = rng.Next(ColorCount);
            switch (rng.Next(7)) {
                case 0: return (Tr("rule.second", ColorName(c)), Count(c) >= 2 ? Nth(c, 1) : -1, false);
                case 1: return (Tr("rule.last", ColorName(c)), Count(c) >= 2 ? LastOf(c) : -1, false);
                case 2: {
                    int i = Count(c) == 1 ? Nth(c, 0) : -1;
                    return (Tr("rule.below", ColorName(c)), i >= 0 && i < WireCount - 1 ? i + 1 : -1, false);
                }
                case 3: return (Tr("rule.count", ColorName(c)), Count(c) >= 1 ? Count(c) - 1 : -1, false);
                case 4: { int k = rng.Next(1, WireCount + 1); return (Tr("rule.top", k), k - 1, true); }
                case 5: { int k = rng.Next(1, WireCount + 1); return (Tr("rule.bottom", k), WireCount - k, true); }
                default: {
                    int b = (c + 1 + rng.Next(ColorCount - 1)) % ColorCount;
                    for (int i = 0; i < WireCount; i++)
                        if (wireColor[i] != c && wireColor[i] != b) return (Tr("rule.neither", ColorName(c), ColorName(b)), i, false);
                    return ("", -1, false);
                }
            }
        }

        private static (string text, bool holds) MakeCondition() {
            int c = rng.Next(ColorCount);
            switch (rng.Next(3)) {
                case 0: return (Tr("cond.none", ColorName(c)), Count(c) == 0);
                case 1: return (Tr("cond.two", ColorName(c)), Count(c) >= 2);
                default: return (Tr("cond.odd"), (serial[serial.Length - 1] - '0') % 2 == 1);
            }
        }

        private static string MakeSerial() {
            const string L = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            char C() => L[rng.Next(L.Length)];
            char D() => (char)('0' + rng.Next(10));
            return $"{C()}{D()}{C()}-{D()}{C()}{D()}";
        }

        // A condition and two rules that both work on these wires and point at different wires, at least one of them
        // about colours: reading the manual is the puzzle, guessing costs time.
        private static void NewPuzzle() {
            for (int attempt = 0; attempt < 500; attempt++) {
                for (int i = 0; i < WireCount; i++) { wireColor[i] = rng.Next(ColorCount); wireCut[i] = false; }
                serial = MakeSerial();
                var cond = MakeCondition();
                var a = MakeRule();
                var b = MakeRule();
                if (a.wire < 0 || b.wire < 0 || a.wire == b.wire || (a.plain && b.plain)) continue;
                answer = cond.holds ? a.wire : b.wire;
                manual = Tr("if", cond.text) + "\n" + a.text + "\n\n" + Tr("otherwise") + "\n" + b.text;
                return;
            }
            answer = rng.Next(WireCount);
            manual = Tr("rule.top", answer + 1);
        }

        // ====================================================================
        // Per frame
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class UpdatePatch {
            public static void Postfix() {
                if (phase == Phase.Closed) return;
                try { Tick(); } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] defuse tick failed: {e}");
                    if (Running) Send(false);
                    Close();
                }
            }
        }

        private static void Tick() {
            var me = PlayerControl.LocalPlayer;
            var hud = MeetingHud.Instance;
            if (panel == null || me == null || me.Data == null || me.Data.IsDead) { Close(); return; }   // guessed: nothing left to defuse
            bool votingOver = hud == null || hud.state == MeetingHud.VoteStates.Results
                              || hud.state == MeetingHud.VoteStates.Proceeding;
            if (Running && votingOver) Lose();
            ApplyCameraFit(panel);
            if (!Running) {
                if (Time.time >= closeAt || hud == null) Close();
                return;
            }
            if (BugCrashScreen.Active) return;   // the Bug's crash covers the screen: the clock waits

            timeLeft -= Time.deltaTime;
            if (timeLeft <= 0f) { timeLeft = 0f; Lose(); return; }
            int secs = Mathf.CeilToInt(timeLeft);
            timerText.text = $"{secs / 60}:{secs % 60:00}";
            timerText.color = timeLeft > 10f || Mathf.Repeat(Time.time, 0.5f) < 0.3f ? Color.white : Bad;
            if (led != null) led.color = Mathf.Repeat(Time.time, timeLeft > 10f ? 1f : 0.35f) < 0.15f ? Bad : new Color(0.35f, 0.08f, 0.06f);

            var cam = FitCamera(panel.layer);
            Vector3 local = cam != null
                ? panel.transform.InverseTransformPoint(cam.ScreenToWorldPoint(Input.mousePosition))
                : new Vector3(99f, 99f, 0f);
            if (phase == Phase.Wires) TickWires(local);
            else TickNeedle();
        }

        private static void TickWires(Vector3 local) {
            int over = -1;
            for (int i = 0; i < WireCount; i++)
                if (Mathf.Abs(local.x - BoxX) < WireLen / 2f + 0.15f && Mathf.Abs(local.y - WireY(i)) < 0.2f) { over = i; break; }
            for (int i = 0; i < WireCount; i++)
                if (hover[i] != null) hover[i].color = new Color(1f, 1f, 1f, i == over && !wireCut[i] ? 0.13f : 0f);
            if (Time.time < armedAt || over < 0 || wireCut[over] || !Input.GetMouseButtonDown(0)) return;

            wireCut[over] = true;
            RefreshWire(over);
            try { UCAssets.PlayTrapSnap(PlayerControl.LocalPlayer.GetTruePosition(), UCAssets.VolSoft); } catch { }
            if (over == answer) {
                UCAssets.PlaySaboteurScanHit();
                StartNeedle(0);
                return;
            }
            timeLeft -= WrongWirePenalty;
            UCAssets.PlaySaboteurWireWrong();
            SetStatus(Tr("wrong", (int)WrongWirePenalty), Bad);
        }

        private static void StartNeedle(int step) {
            phase = Phase.Needle;
            needleStep = step;
            zoneHalf = step == 0 ? 0.11f : 0.07f;
            needleSpeed = step == 0 ? 0.9f : 1.15f;
            NewZone();
            needleOffset = (float)rng.NextDouble() * 2f;
            armedAt = Time.time + InputDeadTime;
            ShowWires(false);
            ShowGauge(true);
            stepText.text = Tr("step", step + 2);
            hintText.text = Tr("hint_needle", (int)MissPenalty);
            SetStatus(Tr("needle"), Color.white);
            if (manualText != null) manualText.text = Tr("needle");
        }

        private static void NewZone() {
            float c;
            do c = Mathf.Lerp(0.15f, 0.85f, (float)rng.NextDouble());
            while (Mathf.Abs(c - zoneCenter) < 0.2f);
            zoneCenter = c;
            if (zone != null) {
                zone.transform.localPosition = new Vector3(GaugeX(zoneCenter), BoxY, -0.03f);
                zone.transform.localScale = new Vector3(GaugeW * zoneHalf * 2f, 0.5f, 1f);
            }
        }

        private static void TickNeedle() {
            float t = Mathf.PingPong((Time.time + needleOffset) * needleSpeed, 1f);
            if (needle != null) needle.transform.localPosition = new Vector3(GaugeX(t), BoxY, -0.05f);
            if (Time.time < armedAt || !ActionDown()) return;
            if (Mathf.Abs(t - zoneCenter) <= zoneHalf) {
                UCAssets.PlaySaboteurScanHit();
                if (needleStep == 0) StartNeedle(1);
                else Win();
                return;
            }
            timeLeft -= MissPenalty;
            UCAssets.PlaySaboteurScanMiss();
            SetStatus(Tr("miss", (int)MissPenalty), Bad);
            NewZone();
            armedAt = Time.time + InputDeadTime;
        }

        // Click anywhere, or Space / E / Enter while the chat does not own the keyboard.
        private static bool ActionDown() {
            if (Input.GetMouseButtonDown(0)) return true;
            try { var chat = HudManager.Instance?.Chat; if (chat != null && chat.IsOpenOrOpening) return false; } catch { }
            return Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.E)
                   || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        }

        // The meeting's own buttons (vote, skip, chat, TOR's Guesser and Swapper) take no clicks while the steps run.
        [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickDown))]
        static class BlockClickDown { public static bool Prefix() => !Running; }
        [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickUp))]
        static class BlockClickUp { public static bool Prefix() => !Running; }
        [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveRepeatDown))]
        static class BlockRepeat { public static bool Prefix() => !Running; }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class JoinPatch { public static void Postfix() => Close(); }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch { public static void Prefix() => Close(); }

        // ====================================================================
        // Drawing
        // ====================================================================
        private static GameObject panel;
        private static int sortLayer;   // the meeting window's sorting layer: the panel must never end up behind it
        private static TextMeshPro title, stepText, timerText, statusText, hintText, manualText, serialText;
        private static SpriteRenderer led, gaugeBg, zone, needle;
        private static readonly SpriteRenderer[] wireL = new SpriteRenderer[WireCount], wireR = new SpriteRenderer[WireCount];
        private static readonly SpriteRenderer[] hover = new SpriteRenderer[WireCount];

        private static float WireY(int i) => BoxY + 0.9f - i * 0.45f;
        private static float GaugeX(float t) => BoxX + (t - 0.5f) * GaugeW;

        private static void Build() {
            var hud = HudManager.Instance;
            Close();
            sortLayer = 0;
            try {
                var mr = MeetingHud.Instance != null ? MeetingHud.Instance.GetComponentInChildren<SpriteRenderer>(true) : null;
                if (mr != null) sortLayer = mr.sortingLayerID;
            } catch { }
            panel = new GameObject("ManiacDefusePanel") { layer = hud.gameObject.layer };
            panel.transform.SetParent(hud.transform, false);
            panel.transform.localPosition = new Vector3(0f, 0f, -80f);
            ApplyCameraFit(panel);
            var t = panel.transform;

            float topY = PanelH / 2f;
            NewRect(t, Vector3.zero, new Vector2(PanelW, PanelH), PanelBg, SortBg);
            NewFrame(t, Vector3.zero, new Vector2(PanelW, PanelH), BorderCol, 0.035f);
            NewRect(t, new Vector3(0f, topY - HeaderH / 2f, -0.01f), new Vector2(PanelW, HeaderH), HeaderBg, SortMid);
            float headY = topY - HeaderH / 2f - 0.02f;
            title = NewText(t, Tr("title"), 1.5f, Color.white);
            title.transform.localPosition = new Vector3(-PanelW / 2f + 0.25f, headY, -0.1f);
            stepText = NewText(t, "", 1.1f, Dim, TextAlignmentOptions.Center);
            stepText.transform.localPosition = new Vector3(0.9f, headY, -0.1f);
            timerText = NewText(t, "", 1.9f, Color.white, TextAlignmentOptions.Right);
            timerText.transform.localPosition = new Vector3(PanelW / 2f - 0.25f, headY, -0.1f);

            // the bomb
            NewRect(t, new Vector3(BoxX, BoxY, -0.01f), new Vector2(BoxW, BoxH), BoxCol, SortMid);
            NewFrame(t, new Vector3(BoxX, BoxY, -0.01f), new Vector2(BoxW, BoxH), new Color(0.4f, 0.42f, 0.45f, 1f), 0.03f);
            float plateY = BoxY + BoxH / 2f - 0.3f;
            NewRect(t, new Vector3(BoxX - 0.6f, plateY, -0.02f), new Vector2(1.9f, 0.34f), new Color(0.82f, 0.82f, 0.78f, 1f), SortTop);
            serialText = NewText(t, Tr("serial", serial), 1.05f, Ink, TextAlignmentOptions.Center);
            serialText.transform.localPosition = new Vector3(BoxX - 0.6f, plateY - 0.01f, -0.1f);
            led = NewRect(t, new Vector3(BoxX + 1.35f, plateY, -0.02f), new Vector2(0.2f, 0.2f), Bad, SortTop);
            for (int i = 0; i < WireCount; i++) {
                float y = WireY(i);
                hover[i] = NewRect(t, new Vector3(BoxX, y, -0.015f), new Vector2(WireLen + 0.3f, 0.36f), new Color(1f, 1f, 1f, 0f), SortMid);
                NewRect(t, new Vector3(BoxX - WireLen / 2f - 0.05f, y, -0.03f), new Vector2(0.16f, 0.26f), new Color(0.55f, 0.56f, 0.58f, 1f), SortTop);
                NewRect(t, new Vector3(BoxX + WireLen / 2f + 0.05f, y, -0.03f), new Vector2(0.16f, 0.26f), new Color(0.55f, 0.56f, 0.58f, 1f), SortTop);
                wireL[i] = NewRect(t, Vector3.zero, Vector2.one, WireColors[wireColor[i]], SortTop);
                wireR[i] = NewRect(t, Vector3.zero, Vector2.one, WireColors[wireColor[i]], SortTop);
                RefreshWire(i);
            }
            gaugeBg = NewRect(t, new Vector3(BoxX, BoxY, -0.02f), new Vector2(GaugeW, 0.5f), new Color(0.05f, 0.05f, 0.06f, 1f), SortTop);
            zone = NewRect(t, new Vector3(BoxX, BoxY, -0.03f), new Vector2(0.6f, 0.5f), new Color(0.2f, 0.95f, 0.3f, 0.75f), SortTop + 1);
            needle = NewRect(t, new Vector3(BoxX, BoxY, -0.05f), new Vector2(0.07f, 0.8f), Color.white, SortTop + 2);
            statusText = NewText(t, "", 1.15f, Bad, TextAlignmentOptions.Center);
            statusText.transform.localPosition = new Vector3(BoxX, BoxY - BoxH / 2f + 0.3f, -0.1f);

            // the manual
            NewRect(t, new Vector3(CardX, BoxY, -0.01f), new Vector2(CardW, BoxH), Paper, SortMid);
            var head = NewText(t, Tr("manual"), 1.3f, Ink, TextAlignmentOptions.Center);
            head.transform.localPosition = new Vector3(CardX, BoxY + BoxH / 2f - 0.3f, -0.1f);
            NewRect(t, new Vector3(CardX, BoxY + BoxH / 2f - 0.55f, -0.02f), new Vector2(CardW - 0.4f, 0.025f), Ink, SortTop);
            manualText = NewText(t, "", 1.12f, Ink, TextAlignmentOptions.TopLeft);
            manualText.enableWordWrapping = true;
            manualText.rectTransform.sizeDelta = new Vector2(CardW - 0.4f, BoxH - 0.9f);
            manualText.transform.localPosition = new Vector3(CardX, BoxY - 0.3f, -0.1f);

            hintText = NewText(t, "", 0.95f, Dim, TextAlignmentOptions.Center);
            hintText.transform.localPosition = new Vector3(0f, -PanelH / 2f + 0.2f, -0.1f);
        }

        private static void RefreshWire(int i) {
            if (wireL[i] == null || wireR[i] == null) return;
            float y = WireY(i), half = WireLen / 2f;
            bool cut = wireCut[i];
            float len = cut ? half - 0.22f : half;
            Color c = WireColors[wireColor[i]];
            if (cut) c = new Color(c.r * 0.5f, c.g * 0.5f, c.b * 0.5f, 1f);
            wireL[i].transform.localPosition = new Vector3(BoxX - half + len / 2f, y, -0.04f);
            wireR[i].transform.localPosition = new Vector3(BoxX + half - len / 2f, y, -0.04f);
            wireL[i].transform.localScale = wireR[i].transform.localScale = new Vector3(len, 0.13f, 1f);
            wireL[i].color = wireR[i].color = c;
        }

        private static void ShowWires(bool on) {
            for (int i = 0; i < WireCount; i++) {
                if (wireL[i] != null) wireL[i].enabled = on;
                if (wireR[i] != null) wireR[i].enabled = on;
                if (hover[i] != null) hover[i].enabled = on;
            }
        }

        private static void ShowGauge(bool on) {
            if (gaugeBg != null) gaugeBg.enabled = on;
            if (zone != null) zone.enabled = on;
            if (needle != null) needle.enabled = on;
        }

        private static void SetStatus(string text, Color color) {
            if (statusText == null) return;
            statusText.text = text;
            statusText.color = color;
        }

        // ---- camera fit (see GamblerUI / UCHelpMenu: the HUD renders through "UI Camera", not Camera.main) ----
        private static Camera fitCam;
        private static Camera FitCamera(int layer) {
            if (fitCam != null && fitCam.isActiveAndEnabled && (fitCam.cullingMask & (1 << layer)) != 0)
                return fitCam;
            fitCam = null;
            foreach (var c in Camera.allCameras)
                if (c != null && c.gameObject.name == "UI Camera" && (c.cullingMask & (1 << layer)) != 0) { fitCam = c; break; }
            if (fitCam == null)
                foreach (var c in Camera.allCameras)
                    if (c != null && c != Camera.main && (c.cullingMask & (1 << layer)) != 0) { fitCam = c; break; }
            if (fitCam == null) fitCam = Camera.main;
            return fitCam;
        }

        private static void ApplyCameraFit(GameObject go) {
            if (go == null) return;
            var cam = FitCamera(go.layer);
            if (cam == null) return;
            Vector3 bl = cam.ScreenToWorldPoint(new Vector3(0f, 0f, 10f));
            Vector3 tr = cam.ScreenToWorldPoint(new Vector3(Screen.width, Screen.height, 10f));
            float visW = Mathf.Abs(tr.x - bl.x), visH = Mathf.Abs(tr.y - bl.y);
            if (visW < 0.01f || visH < 0.01f) return;
            float scale = Mathf.Min(visH / (DesignOrtho * 2f), visW * 0.98f / PanelW);
            float parentScale = go.transform.parent != null ? go.transform.parent.lossyScale.x : 1f;
            if (parentScale < 0.0001f) parentScale = 1f;
            go.transform.localScale = Vector3.one * (scale / parentScale);
            var p = go.transform.position;
            go.transform.position = new Vector3((bl.x + tr.x) / 2f, (bl.y + tr.y) / 2f, p.z);
        }

        // ---- primitives ----
        private static Sprite whiteSprite;
        private static Sprite WhiteSprite() {
            if (whiteSprite != null) return whiteSprite;
            var tex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave;
            whiteSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            whiteSprite.hideFlags |= HideFlags.HideAndDontSave;
            return whiteSprite;
        }

        private static SpriteRenderer NewRect(Transform parent, Vector3 localPos, Vector2 size, Color color, int sort) {
            var go = new GameObject("DefuseRect") { layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = WhiteSprite();
            sr.color = color;
            sr.sortingLayerID = sortLayer;
            sr.sortingOrder = sort;
            return sr;
        }

        private static void NewFrame(Transform parent, Vector3 center, Vector2 size, Color color, float thickness) {
            NewRect(parent, center + new Vector3(0, size.y / 2f, -0.005f), new Vector2(size.x, thickness), color, SortMid);
            NewRect(parent, center + new Vector3(0, -size.y / 2f, -0.005f), new Vector2(size.x, thickness), color, SortMid);
            NewRect(parent, center + new Vector3(-size.x / 2f, 0, -0.005f), new Vector2(thickness, size.y), color, SortMid);
            NewRect(parent, center + new Vector3(size.x / 2f, 0, -0.005f), new Vector2(thickness, size.y), color, SortMid);
        }

        private static TextMeshPro NewText(Transform parent, string text, float fontSize, Color color,
                                           TextAlignmentOptions alignment = TextAlignmentOptions.Left) {
            var template = HudManager.Instance.KillButton.cooldownTimerText;
            var tmp = UnityEngine.Object.Instantiate(template, parent);
            tmp.gameObject.SetActive(true);
            tmp.gameObject.layer = parent.gameObject.layer;
            tmp.transform.localScale = Vector3.one;
            tmp.transform.localPosition = Vector3.zero;
            // Collapse the cloned rect to a point so the transform IS the alignment anchor (see GamblerUI.NewText).
            var rt = tmp.rectTransform;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            tmp.margin = Vector4.zero;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.enableAutoSizing = false;
            tmp.enableWordWrapping = false;
            tmp.alignment = alignment;
            tmp.color = color;
            var mr = tmp.GetComponent<MeshRenderer>();
            if (mr != null) { mr.sortingLayerID = sortLayer; mr.sortingOrder = SortText; }
            return tmp;
        }
    }
}
