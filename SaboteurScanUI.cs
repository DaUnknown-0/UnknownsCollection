// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Crew counterplay for the Saboteur - two self-contained minigames drawn from procedural sprites +
 * TextMeshPro under HudManager (no embedded assets):
 *
 *   1. SEARCH (Scan-Sweep): a marker sweeps across a bar; the crewmate presses the action when it is
 *      inside the green window to reveal whether the console is SAFE or SABOTAGED. The action is
 *      Left-Click / E / Space / Enter (all equivalent).
 *   2. DEFUSE (Wire-Cut): if SABOTAGED (and defusing is allowed), four numbered wires must be cut in
 *      order 1->4. A cursor auto-cycles the wires; press the action to cut the highlighted one. A wrong
 *      cut resets. Completing it clears the sabotage.
 *
 * Drunk (the renamed Invert modifier): a searcher who is currently inverted gets a harder, unreliable
 * minigame - a narrower/faster, jittery scan whose result may LIE, and flickering wire numbers.
 *
 * Whether a console is actually sabotaged is known locally (Saboteur.sabotagedActive + position), so no
 * extra sync is needed to start; a successful defuse broadcasts a clear via Saboteur.SendClearSabotage.
 */

using System;
using UnityEngine;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    public static class SaboteurScanUI {
        private enum Phase { Closed, Scan, Result, Wire, Done }
        private static Phase phase = Phase.Closed;

        public static bool IsOpen => phase != Phase.Closed;

        // Target console + truth
        private static Vector2 target;
        private static bool sabotaged;
        private static bool drunk;

        // Scan state
        private static float markerPos;       // 0..1 sweep
        private static float windowCenter;    // 0..1
        private static float windowHalf;      // half-width in 0..1
        private static float sweepSpeed;
        private static float resultTimer;
        private static bool shownSabotaged;

        // Wire state
        private const int WireCount = 4;
        private static readonly int[] wireOrder = new int[WireCount]; // wireOrder[i] = required cut number (1..N)
        private static readonly bool[] wireCut = new bool[WireCount];
        private static int nextExpected;
        private static float cursor;          // floating index over wires
        private static float cursorSpeed;

        // ---- visuals ----
        private static GameObject root;
        private static SpriteRenderer barBg, window, marker, cursorBox;
        private static readonly SpriteRenderer[] wires = new SpriteRenderer[WireCount];
        private static readonly TMPro.TextMeshPro[] wireLabels = new TMPro.TextMeshPro[WireCount];
        private static TMPro.TextMeshPro title, hint;
        private static Sprite rect;

        // ====================================================================
        // Public entry
        // ====================================================================
        public static void Open(Vector2 consolePos) {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null) return;
                target = consolePos;
                sabotaged = Saboteur.sabotagedActive
                            && Vector2.Distance(consolePos, new Vector2(Saboteur.sabotagedX, Saboteur.sabotagedY)) <= 1.0f;
                drunk = IsDrunk(me.PlayerId);

                Ensure();
                if (root == null) return;
                root.SetActive(true);
                StartScan();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] scan Open failed: {e}");
            }
        }

        public static void Close() {
            phase = Phase.Closed;
            if (root != null) root.SetActive(false);
        }

        public static bool IsDrunk(byte id) {
            try { return Invert.invert.FindAll(x => x.PlayerId == id).Count > 0 && Invert.meetings > 0; }
            catch { return false; }
        }

        // The action input: Left-Click / E / Space / Enter (all equivalent).
        private static bool ActionDown() =>
            Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.E)
            || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return)
            || Input.GetKeyDown(KeyCode.KeypadEnter);

        // ====================================================================
        // Per-frame driver (called from HudManager.Update)
        // ====================================================================
        public static void Update() {
            if (phase == Phase.Closed) return;
            try {
                var me = PlayerControl.LocalPlayer;
                // Abort if a meeting starts, the player dies, or walks away from the console.
                if (me == null || me.Data == null || me.Data.IsDead
                    || MeetingHud.Instance != null || ExileController.Instance != null
                    || Vector2.Distance(me.GetTruePosition(), target) > 1.8f
                    || Input.GetKeyDown(KeyCode.Escape)) {
                    Close();
                    return;
                }

                switch (phase) {
                    case Phase.Scan: UpdateScan(); break;
                    case Phase.Result: UpdateResult(); break;
                    case Phase.Wire: UpdateWire(); break;
                    case Phase.Done:
                        resultTimer -= Time.deltaTime;
                        if (resultTimer <= 0f) Close();
                        break;
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Saboteur] scan Update failed: {e}");
                Close();
            }
        }

        // ---- Scan ----------------------------------------------------------
        private static void StartScan() {
            phase = Phase.Scan;
            markerPos = 0f;
            windowHalf = drunk ? 0.06f : 0.12f;                 // drunk: narrower window
            windowCenter = UnityEngine.Random.Range(0.2f, 0.8f);
            sweepSpeed = drunk ? 1.7f : 1.0f;                    // drunk: faster
            SetScanActive(true);
            SetWireActive(false);
            if (title != null) { title.text = "SEARCH"; title.color = new Color(0.7f, 0.9f, 1f); }
            if (hint != null) hint.text = "Klicke im grünen Feld  (LMB / E / Leer / Enter)";
        }

        private static void UpdateScan() {
            float jitter = drunk ? Mathf.Sin(Time.time * 40f) * 0.02f : 0f;
            markerPos = Mathf.PingPong(Time.time * sweepSpeed, 1f);
            float shown = Mathf.Clamp01(markerPos + jitter);
            if (marker != null) marker.transform.localPosition = new Vector3(BarX(shown), 0f, -1f);

            if (window != null) {
                window.transform.localPosition = new Vector3(BarX(windowCenter), 0f, -0.5f);
                window.transform.localScale = new Vector3(4f * (windowHalf * 2f), 0.5f, 1f);
            }

            if (ActionDown()) {
                bool hitWindow = Mathf.Abs(shown - windowCenter) <= windowHalf;
                if (hitWindow) {
                    // Reveal. Drunk result may lie.
                    shownSabotaged = sabotaged;
                    if (drunk && UnityEngine.Random.value < 0.5f) shownSabotaged = !sabotaged;
                    StartResult();
                } else if (hint != null) {
                    hint.text = "Daneben - weiter scannen";
                }
            }
        }

        private static void StartResult() {
            phase = Phase.Result;
            resultTimer = 1.2f;
            SetScanActive(false);
            if (title != null) {
                title.text = shownSabotaged ? "⚠ SABOTAGED" : "✓ SAFE";
                title.color = shownSabotaged ? new Color(1f, 0.35f, 0.2f) : new Color(0.4f, 1f, 0.5f);
            }
            if (hint != null) hint.text = drunk ? "(du bist Drunk - unsicher)" : "";
        }

        private static void UpdateResult() {
            resultTimer -= Time.deltaTime;
            if (resultTimer > 0f) return;
            bool canDefuse = Saboteur.CrewCanDefuse == null || Saboteur.CrewCanDefuse.getBool();
            if (shownSabotaged && canDefuse) StartWire();
            else Close();
        }

        // ---- Wire-Cut (defuse) --------------------------------------------
        private static void StartWire() {
            phase = Phase.Wire;
            // Assign cut-order numbers 1..N to the wires in a scrambled visual layout.
            for (int i = 0; i < WireCount; i++) { wireOrder[i] = i + 1; wireCut[i] = false; }
            for (int i = WireCount - 1; i > 0; i--) {
                int j = UnityEngine.Random.Range(0, i + 1);
                (wireOrder[i], wireOrder[j]) = (wireOrder[j], wireOrder[i]);
            }
            nextExpected = 1;
            cursor = 0f;
            cursorSpeed = drunk ? 4.5f : 2.2f;
            SetScanActive(false);
            SetWireActive(true);
            if (title != null) { title.text = "DEFUSE"; title.color = new Color(1f, 0.8f, 0.3f); }
            if (hint != null) hint.text = "Schneide in Reihenfolge 1→" + WireCount + "  (LMB / E / Leer / Enter)";
            RefreshWires();
        }

        private static void UpdateWire() {
            cursor += Time.deltaTime * cursorSpeed;
            int idx = ((int)cursor) % WireCount;
            if (cursorBox != null)
                cursorBox.transform.localPosition = new Vector3(-1.6f, WireY(idx), -1f);

            // Drunk: flicker the numbers so the order is hard to read.
            if (drunk)
                for (int i = 0; i < WireCount; i++)
                    if (wireLabels[i] != null)
                        wireLabels[i].enabled = !wireCut[i] && Mathf.Sin(Time.time * 18f + i) > -0.2f;

            if (ActionDown()) {
                if (!wireCut[idx] && wireOrder[idx] == nextExpected) {
                    wireCut[idx] = true;
                    nextExpected++;
                    if (nextExpected > WireCount) { // defused!
                        Saboteur.SendClearSabotage();
                        if (title != null) { title.text = "✓ DEFUSED"; title.color = new Color(0.4f, 1f, 0.5f); }
                        if (hint != null) hint.text = "";
                        RefreshWires();
                        phase = Phase.Done;
                        resultTimer = 0.8f;
                        return;
                    }
                } else if (!wireCut[idx]) {
                    // wrong order -> reset
                    for (int i = 0; i < WireCount; i++) wireCut[i] = false;
                    nextExpected = 1;
                    Helpers.showFlash(new Color(1f, 0.2f, 0.2f, 0.4f), 0.25f);
                }
                RefreshWires();
            }
        }

        private static void RefreshWires() {
            for (int i = 0; i < WireCount; i++) {
                if (wires[i] != null)
                    wires[i].color = wireCut[i] ? new Color(0.25f, 0.25f, 0.25f, 0.6f) : WireColor(i);
                if (wireLabels[i] != null) {
                    wireLabels[i].enabled = !wireCut[i];
                    wireLabels[i].text = wireOrder[i].ToString();
                }
            }
        }

        // ====================================================================
        // Visual construction
        // ====================================================================
        private static float BarX(float t) => Mathf.Lerp(-2f, 2f, t);
        private static float WireY(int i) => 0.7f - i * 0.45f;
        private static Color WireColor(int i) {
            switch (i % 4) {
                case 0: return new Color(1f, 0.3f, 0.3f);
                case 1: return new Color(0.4f, 0.7f, 1f);
                case 2: return new Color(1f, 0.9f, 0.3f);
                default: return new Color(0.5f, 1f, 0.5f);
            }
        }

        private static void Ensure() {
            if (root != null) return;
            var hud = HudManager.Instance;
            if (hud == null) return;
            if (rect == null) rect = BuildRectSprite();

            root = new GameObject("SaboteurScanUI");
            root.transform.SetParent(hud.transform);
            root.transform.localPosition = new Vector3(0f, 0.3f, -50f);
            root.transform.localScale = Vector3.one;

            title = MakeText("title", new Vector3(0f, 1.5f, -1f), 2.4f);
            hint = MakeText("hint", new Vector3(0f, -1.6f, -1f), 1.1f);

            // Scan widgets
            barBg = MakeRect("bar", new Vector3(0f, 0f, 0f), new Vector3(4f, 0.5f, 1f),
                new Color(0.08f, 0.08f, 0.1f, 0.95f));
            window = MakeRect("window", new Vector3(0f, 0f, -0.5f), new Vector3(0.8f, 0.5f, 1f),
                new Color(0.2f, 1f, 0.3f, 0.6f));
            marker = MakeRect("marker", new Vector3(0f, 0f, -1f), new Vector3(0.08f, 0.7f, 1f),
                new Color(1f, 1f, 1f, 1f));

            // Wire widgets
            cursorBox = MakeRect("cursor", new Vector3(-1.6f, 0.7f, -1f), new Vector3(0.5f, 0.36f, 1f),
                new Color(1f, 1f, 1f, 0.35f));
            for (int i = 0; i < WireCount; i++) {
                wires[i] = MakeRect($"wire{i}", new Vector3(0.2f, WireY(i), 0f), new Vector3(3f, 0.28f, 1f), WireColor(i));
                wireLabels[i] = MakeText($"wlabel{i}", new Vector3(-1.6f, WireY(i), -1f), 1.3f);
            }

            root.SetActive(false);
        }

        private static TMPro.TextMeshPro MakeText(string name, Vector3 pos, float size) {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform);
            go.transform.localPosition = pos;
            go.transform.localScale = Vector3.one;
            var t = go.AddComponent<TMPro.TextMeshPro>();
            t.fontSize = size;
            t.alignment = TMPro.TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.text = "";
            return t;
        }

        private static SpriteRenderer MakeRect(string name, Vector3 pos, Vector3 scale, Color color) {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = rect;
            sr.color = color;
            return sr;
        }

        private static void SetScanActive(bool on) {
            if (barBg != null) barBg.gameObject.SetActive(on);
            if (window != null) window.gameObject.SetActive(on);
            if (marker != null) marker.gameObject.SetActive(on);
        }

        private static void SetWireActive(bool on) {
            if (cursorBox != null) cursorBox.gameObject.SetActive(on);
            for (int i = 0; i < WireCount; i++) {
                if (wires[i] != null) wires[i].gameObject.SetActive(on);
                if (wireLabels[i] != null) wireLabels[i].gameObject.SetActive(on);
            }
        }

        // 1x1 white sprite (1 px = 1 world unit) stretched via transform.localScale.
        private static Sprite BuildRectSprite() {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        }
    }
}
