// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * GamblerUI - the bet button, the two-step bet picker and the HUD strip of open bets.
 *
 * Built on the world-space HudManager overlay pattern that UCHelpMenu established and RoleControlUI
 * copied: a panel parented to the HudManager, re-fitted to the RENDERING camera every frame (which
 * is the "UI Camera", NOT Camera.main), hit tests resolved in panel-LOCAL space so they stay correct
 * under any scale. Only ASCII in the labels - the HUD font this clones from has no arrows, bullets
 * or check marks, and missing glyphs render as blanks.
 *
 * Two steps, because most bets need a target: pick the bet, then pick the player. Tier and target
 * requirement come from Gambler.Defs, so the picker never has to know the catalogue.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class GamblerUI {

        // ---- layout (design units, fitted to the camera every frame) ----
        private const float PanelW = 7.4f;
        private const float PanelH = 4.5f;
        private const float HeaderH = 0.5f;
        private const float RowH = 0.42f;
        private const float DesignOrtho = 3f;
        private const int SortBg = 500, SortMid = 510, SortText = 520;

        private static readonly Color PanelBg = new Color(0.05f, 0.13f, 0.09f, 0.96f);
        private static readonly Color HeaderBg = new Color(0.08f, 0.22f, 0.15f, 0.98f);
        private static readonly Color BorderCol = new Color(0.35f, 0.85f, 0.45f, 0.9f);
        private static readonly Color RowBg = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color Dim = new Color(0.62f, 0.68f, 0.64f, 1f);

        private static TheOtherRoles.Objects.CustomButton betButton;
        private static GameObject panel;
        private static BetKind? pendingKind;      // set once a bet is chosen, waiting for a target

        private sealed class HitBox {
            public Transform anchor;
            public float w, h;
            public Action onClick;
            public SpriteRenderer hover;
        }
        private static readonly List<HitBox> hits = new List<HitBox>();

        // ---- camera fit (see UCHelpMenu: the HUD renders through "UI Camera", not Camera.main) ----
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

        private static SpriteRenderer NewRect(Transform parent, Vector3 localPos, Vector2 size, Color color, int sort = SortBg) {
            var go = new GameObject("GamblerRect");
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = WhiteSprite();
            sr.color = color;
            sr.sortingOrder = sort;
            return sr;
        }

        private static void NewFrame(Transform parent, Vector3 center, Vector2 size, Color color, float thickness = 0.025f) {
            NewRect(parent, center + new Vector3(0, size.y / 2f, 0), new Vector2(size.x, thickness), color, SortMid);
            NewRect(parent, center + new Vector3(0, -size.y / 2f, 0), new Vector2(size.x, thickness), color, SortMid);
            NewRect(parent, center + new Vector3(-size.x / 2f, 0, 0), new Vector2(thickness, size.y), color, SortMid);
            NewRect(parent, center + new Vector3(size.x / 2f, 0, 0), new Vector2(thickness, size.y), color, SortMid);
        }

        private static TextMeshPro NewText(Transform parent, string text, float fontSize, Color color,
                                           TextAlignmentOptions alignment = TextAlignmentOptions.Left) {
            var template = HudManager.Instance.KillButton.cooldownTimerText;
            var tmp = UnityEngine.Object.Instantiate(template, parent);
            tmp.gameObject.SetActive(true);
            tmp.transform.localScale = Vector3.one;
            tmp.transform.localPosition = Vector3.zero;
            // Collapse the cloned rect to a point so the transform IS the alignment anchor
            // (the kill button's rect would otherwise offset every left/right aligned label).
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
            if (mr != null) mr.sortingOrder = SortText;
            return tmp;
        }

        // ---- open / close ----
        public static void Close() {
            try { if (panel != null) UnityEngine.Object.Destroy(panel); } catch { }
            panel = null;
            hits.Clear();
            pendingKind = null;
        }

        public static void Toggle() {
            if (panel != null) Close();
            else OpenBetList();
        }

        private static GameObject NewPanel(string title, string subtitle) {
            var hud = HudManager.Instance;
            if (hud == null) return null;
            Close();

            panel = new GameObject("GamblerPanel");
            panel.layer = hud.gameObject.layer;
            panel.transform.SetParent(hud.transform, false);
            panel.transform.localPosition = new Vector3(0f, 0f, -30f);
            ApplyCameraFit(panel);

            float topY = PanelH / 2f;
            NewRect(panel.transform, Vector3.zero, new Vector2(PanelW, PanelH), PanelBg);
            NewFrame(panel.transform, Vector3.zero, new Vector2(PanelW, PanelH), BorderCol);
            NewRect(panel.transform, new Vector3(0f, topY - HeaderH / 2f, -0.02f), new Vector2(PanelW, HeaderH), HeaderBg, SortMid);

            float headY = topY - HeaderH / 2f - 0.02f;
            var t = NewText(panel.transform, title, 1.4f, Color.white);
            t.transform.localPosition = new Vector3(-PanelW / 2f + 0.25f, headY, -0.1f);

            if (!string.IsNullOrEmpty(subtitle)) {
                var s = NewText(panel.transform, subtitle, 1.0f, Dim, TextAlignmentOptions.Right);
                s.transform.localPosition = new Vector3(PanelW / 2f - 0.9f, headY, -0.1f);
            }

            // Close X, top right.
            var x = NewText(panel.transform, "X", 1.4f, Color.white, TextAlignmentOptions.Center);
            x.transform.localPosition = new Vector3(PanelW / 2f - 0.3f, headY, -0.1f);
            AddHit(panel.transform, new Vector3(PanelW / 2f - 0.3f, headY, 0f), 0.45f, 0.45f, Close);
            return panel;
        }

        private static void AddHit(Transform parent, Vector3 localPos, float w, float h, Action onClick,
                                   bool highlight = false) {
            var go = new GameObject("hit");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            SpriteRenderer hover = null;
            if (highlight) {
                hover = NewRect(parent, localPos + new Vector3(0f, 0f, 0.01f), new Vector2(w, h),
                                new Color(1f, 1f, 1f, 0f), SortMid);
            }
            hits.Add(new HitBox { anchor = go.transform, w = w, h = h, onClick = onClick, hover = hover });
        }

        // ---- step 1: the catalogue ----
        private static void OpenBetList() {
            try {
                if (NewPanel(UCLocalization.Tr("uc.gambler.ui.title"),
                             UCLocalization.Tr("uc.gambler.ui.open_bets",
                                               Gambler.OpenBetCount(),
                                               Mathf.RoundToInt(Gambler.MaxActiveBets?.getFloat() ?? 2f))) == null) return;

                float topY = PanelH / 2f - HeaderH;
                // Two columns: 14 bets do not fit in one readable column at this panel height.
                int perColumn = Mathf.CeilToInt(Gambler.Defs.Count / 2f);
                for (int i = 0; i < Gambler.Defs.Count; i++) {
                    var def = Gambler.Defs[i];
                    int col = i / perColumn, row = i % perColumn;
                    float x = -PanelW / 2f + 0.3f + col * (PanelW / 2f - 0.15f);
                    float y = topY - 0.35f - row * RowH;

                    string label = $"[{def.Tier}] {BetLabel(def)}";
                    var text = NewText(panel.transform, label, 1.02f, Color.white);
                    text.transform.localPosition = new Vector3(x, y, -0.1f);

                    var captured = def;
                    AddHit(panel.transform, new Vector3(x + PanelW / 4f - 0.35f, y, 0f),
                           PanelW / 2f - 0.4f, RowH * 0.92f, () => OnBetChosen(captured), true);
                }

                float botY = -PanelH / 2f + 0.28f;
                var hint = NewText(panel.transform, UCLocalization.Tr("uc.gambler.ui.hint"), 0.92f, Dim);
                hint.transform.localPosition = new Vector3(-PanelW / 2f + 0.3f, botY, -0.1f);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] bet list failed: {e}");
                Close();
            }
        }

        // Tier + threshold values are baked into the label so the player can see what they are
        // actually signing up for without a second screen. Public because the settlement banner in
        // Gambler.cs names the bet with exactly the same wording the picker used.
        public static string BetLabel(BetDef def) {
            switch (def.Kind) {
                case BetKind.KillInWindow:
                    return UCLocalization.Tr("uc.gambler.bet.kill_in_window",
                                             Mathf.RoundToInt(Gambler.KillWindow?.getFloat() ?? 30f));
                case BetKind.TargetGetsNVotes:
                    return UCLocalization.Tr("uc.gambler.bet.n_votes",
                                             Mathf.RoundToInt(Gambler.VoteThreshold?.getFloat() ?? 3f));
                case BetKind.TargetDoesNTasks:
                    return UCLocalization.Tr("uc.gambler.bet.n_tasks",
                                             Mathf.RoundToInt(Gambler.TaskThreshold?.getFloat() ?? 4f));
                default:
                    return UCLocalization.Tr("uc.gambler.bet." + def.Key);
            }
        }

        private static void OnBetChosen(BetDef def) {
            if (def == null) return;
            if (!def.NeedsTarget) {
                Gambler.RequestBet(def.Kind, byte.MaxValue);
                Close();
                return;
            }
            pendingKind = def.Kind;
            OpenTargetList(def);
        }

        // ---- step 2: the target ----
        private static void OpenTargetList(BetDef def) {
            try {
                if (NewPanel(UCLocalization.Tr("uc.gambler.ui.pick_target"), BetLabel(def)) == null) return;

                var me = PlayerControl.LocalPlayer;
                var candidates = PlayerControl.AllPlayerControls.ToArray()
                    .Where(p => p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected
                                && (me == null || p.PlayerId != me.PlayerId))     // no self-bets, by design
                    .ToList();

                float topY = PanelH / 2f - HeaderH;
                int perColumn = Mathf.Max(1, Mathf.CeilToInt(candidates.Count / 2f));
                for (int i = 0; i < candidates.Count; i++) {
                    var p = candidates[i];
                    int col = i / perColumn, row = i % perColumn;
                    float x = -PanelW / 2f + 0.3f + col * (PanelW / 2f - 0.15f);
                    float y = topY - 0.35f - row * RowH;

                    var text = NewText(panel.transform, p.Data.PlayerName, 1.05f, Color.white);
                    text.transform.localPosition = new Vector3(x, y, -0.1f);

                    byte pid = p.PlayerId;
                    AddHit(panel.transform, new Vector3(x + PanelW / 4f - 0.35f, y, 0f),
                           PanelW / 2f - 0.4f, RowH * 0.92f, () => {
                               if (pendingKind.HasValue) Gambler.RequestBet(pendingKind.Value, pid);
                               Close();
                           }, true);
                }

                float botY = -PanelH / 2f + 0.28f;
                var back = NewText(panel.transform, UCLocalization.Tr("uc.gambler.ui.back"), 1.0f, BorderCol);
                back.transform.localPosition = new Vector3(-PanelW / 2f + 0.3f, botY, -0.1f);
                AddHit(panel.transform, new Vector3(-PanelW / 2f + 0.8f, botY, 0f), 1.4f, 0.4f, OpenBetList, true);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] target list failed: {e}");
                Close();
            }
        }

        // ---- HUD strip: open bets + last result ----
        private static TextMeshPro strip;

        // AUDIT-2026-08-16: UpdateStrip() used to run unconditionally every HudManager.Update - a
        // fresh StringBuilder, a UCLocalization.Tr() call and a Helpers.playerById() scan per open
        // bet, for a text that only actually changes when a bet is placed/settled or the result
        // banner appears/expires. The StringBuilder is now a reused static buffer (only Clear()'d,
        // never reallocated), and the text itself only gets rebuilt when the bet list (count/id/
        // settled-status) or the result banner (text or expiry) changed, or a short throttle elapses
        // - the throttle alone is what lets the banner's Time.time-based expiry disappear promptly
        // without a per-frame time comparison driving a full rebuild (see the file's general
        // "time throttle over change detection" guidance).
        private const float StripRebuildThrottle = 0.15f;
        private static float nextStripRebuildTime;
        private static bool stripDirty = true; // forces one unconditional rebuild after any reset
        private static readonly StringBuilder stripSb = new StringBuilder(256);
        private static readonly List<byte> cachedBetIds = new List<byte>();
        private static readonly List<bool> cachedBetSettled = new List<bool>();
        private static string cachedLastResultText;
        private static float cachedLastResultUntil = -1f;

        private static void ResetStripCache() {
            stripDirty = true;
            nextStripRebuildTime = 0f;
            cachedBetIds.Clear();
            cachedBetSettled.Clear();
            cachedLastResultText = null;
            cachedLastResultUntil = -1f;
        }

        // Cheap (id/settled only, no string work) comparison against the last rebuild's snapshot.
        private static bool BetsSignatureDiffers() {
            var bets = Gambler.Bets;
            if (bets.Count != cachedBetIds.Count) return true;
            for (int i = 0; i < bets.Count; i++) {
                var b = bets[i];
                byte id = b?.Id ?? byte.MaxValue;
                bool settled = b != null && b.Settled;
                if (id != cachedBetIds[i] || settled != cachedBetSettled[i]) return true;
            }
            return false;
        }

        private static void SyncBetsSnapshot() {
            var bets = Gambler.Bets;
            cachedBetIds.Clear();
            cachedBetSettled.Clear();
            for (int i = 0; i < bets.Count; i++) {
                var b = bets[i];
                cachedBetIds.Add(b?.Id ?? byte.MaxValue);
                cachedBetSettled.Add(b != null && b.Settled);
            }
        }

        private static void UpdateStrip() {
            try {
                if (!Gambler.IsLocalGambler()) {
                    if (strip != null) { UnityEngine.Object.Destroy(strip.gameObject); strip = null; ResetStripCache(); }
                    return;
                }
                var hud = HudManager.Instance;
                if (hud == null) return;
                if (strip == null) {
                    strip = NewText(hud.transform, "", 1.1f, Color.white);
                    strip.transform.localPosition = new Vector3(-3.6f, 2.05f, -20f);
                    stripDirty = true; // freshly (re)created label starts empty regardless of the cache
                }

                bool betsChanged = BetsSignatureDiffers();
                bool resultChanged = Gambler.lastResultText != cachedLastResultText
                                      || Gambler.lastResultUntil != cachedLastResultUntil;
                bool dirty = stripDirty || betsChanged || resultChanged || Time.time >= nextStripRebuildTime;
                if (!dirty) return;

                stripSb.Clear();
                if (!string.IsNullOrEmpty(Gambler.lastResultText) && Time.time < Gambler.lastResultUntil)
                    stripSb.Append(Gambler.lastResultText).Append('\n');

                var bets = Gambler.Bets;
                for (int i = 0; i < bets.Count; i++) {
                    var b = bets[i];
                    if (b == null || b.Settled) continue;
                    var def = Gambler.Def(b.Kind);
                    if (def == null) continue;
                    string target = b.Target == byte.MaxValue
                        ? "" : " " + (Helpers.playerById(b.Target)?.Data?.PlayerName ?? "?");
                    stripSb.Append(UCLocalization.Tr("uc.gambler.ui.open_row", BetLabel(def) + target))
                           .Append('\n');
                }
                strip.text = stripSb.ToString();

                SyncBetsSnapshot();
                cachedLastResultText = Gambler.lastResultText;
                cachedLastResultUntil = Gambler.lastResultUntil;
                nextStripRebuildTime = Time.time + StripRebuildThrottle;
                stripDirty = false;
            } catch { }
        }

        // ---- button ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    // Never gate core logic on button references, and never null statics here:
                    // resetVariables runs AFTER HudManager.Start at round start.
                    betButton = new TheOtherRoles.Objects.CustomButton(
                        () => Toggle(),
                        () => Gambler.IsLocalGambler()
                              && PlayerControl.LocalPlayer?.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => Gambler.CanPlaceBet(),
                        () => { Close(); },
                        UCAssets.GamblerIcon,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowRight,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.gambler.ui.button"));
                    betButton.MaxTimer = 0f;
                    betButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] button creation failed: {e}");
                }
            }
        }

        // ---- per-frame: fit, hover, clicks, HUD strip ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class UpdatePatch {
            public static void Postfix() {
                try {
                    if (!Gambler.active) { if (panel != null) Close(); return; }
                    UpdateStrip();

                    // The picker has no business being open during a meeting or after death.
                    if (panel != null &&
                        (MeetingHud.Instance != null || ExileController.Instance != null
                         || !Gambler.IsLocalGambler()
                         || PlayerControl.LocalPlayer?.Data == null || PlayerControl.LocalPlayer.Data.IsDead)) {
                        Close();
                        return;
                    }
                    if (panel == null) return;

                    ApplyCameraFit(panel);

                    var cam = FitCamera(panel.layer);
                    if (cam == null) return;
                    Vector3 world = cam.ScreenToWorldPoint(Input.mousePosition);
                    Vector3 local = panel.transform.InverseTransformPoint(world);

                    foreach (var h in hits) {
                        if (h?.hover == null || h.anchor == null) continue;
                        bool over = Mathf.Abs(local.x - h.anchor.localPosition.x) < h.w / 2f
                                    && Mathf.Abs(local.y - h.anchor.localPosition.y) < h.h / 2f;
                        h.hover.color = new Color(1f, 1f, 1f, over ? 0.10f : 0f);
                    }

                    if (!Input.GetMouseButtonDown(0)) return;
                    foreach (var h in new List<HitBox>(hits)) {
                        if (h?.anchor == null) continue;
                        Vector3 c = h.anchor.localPosition;
                        if (Mathf.Abs(local.x - c.x) < h.w / 2f && Mathf.Abs(local.y - c.y) < h.h / 2f) {
                            h.onClick?.Invoke();
                            return;
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Gambler] UI update failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() {
                Close();
                if (strip != null) { try { UnityEngine.Object.Destroy(strip.gameObject); } catch { } strip = null; }
                ResetStripCache();
            }
        }

        // Belt-and-suspenders round reset for the strip rebuild cache: Gambler.cs already clears
        // Bets/lastResultText on its own resetVariables patch, but the (Id, Settled) signature alone
        // could coincidentally match a stale cache across rounds if bet ids are reused from 0 - an
        // explicit reset here removes that possibility instead of relying on it never happening.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class StripCacheResetVariablesPatch {
            public static void Postfix() => UCResetGuard.Run("Gambler UI", ResetStripCache);
        }
    }
}
