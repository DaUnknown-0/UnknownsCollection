// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * AuditorHud - the Auditor's readout plus the victim's "a task was undone" notice.
 *
 * Two independent labels, both plain TextMeshPro objects parented under HudManager (the lightweight
 * TeslaIndicator pattern - HUD space, no colliders, no canvas juggling):
 *
 *  - the AUDITOR panel (left edge): revert counter + the kill-cooldown multiplier it currently buys,
 *    then one line per queued task with its remaining lifetime, plus a red warning whenever a crew
 *    completion was just dropped because the queue was full. An entry the Auditor is actively working
 *    on shows "HOLD" instead of a countdown - its lifetime is frozen;
 *  - the VICTIM notice (bottom centre): a short, deliberately anonymous message. It never names the
 *    Auditor, and only the victim's own client ever creates it (info-leak rule).
 *
 * ASCII only. The HUD's TMP font has no glyphs for the usual bullet/arrow characters and renders them
 * as boxes (see the AU overlay UI rules).
 */

using System;
using System.Text;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class AuditorHud {
        private static readonly Color PanelColor = new Color(1f, 0.86f, 0.5f, 1f);
        private static readonly Color WarnColor = new Color(1f, 0.35f, 0.3f, 1f);
        private static readonly Color HoldColor = new Color(0.55f, 0.95f, 0.6f, 1f);

        private const float OverflowWarnSeconds = 3f;
        private const float VictimNoticeSeconds = 4f;

        private static TMPro.TextMeshPro panel;
        private static TMPro.TextMeshPro notice;
        private static float noticeUntil;

        // ---- victim notice ----
        public static void ShowVictimNotice() {
            EnsureNotice();
            if (notice == null) return;
            notice.text = UCLocalization.Tr("uc.ui.auditor.victim_notice");
            notice.gameObject.SetActive(true);
            noticeUntil = Time.time + VictimNoticeSeconds;
        }

        private static void EnsureNotice() {
            if (notice != null) return;
            var hud = HudManager.Instance;
            if (hud == null) return;
            var go = new GameObject("AuditorVictimNotice");
            go.transform.SetParent(hud.transform);
            go.transform.localPosition = new Vector3(0f, -2.2f, -50f);
            go.transform.localScale = Vector3.one;
            notice = go.AddComponent<TMPro.TextMeshPro>();
            notice.fontSize = 2.1f;
            notice.color = WarnColor;
            notice.alignment = TMPro.TextAlignmentOptions.Center;
            notice.enableWordWrapping = false;
            go.SetActive(false);
        }

        // ---- auditor panel ----
        private static void EnsurePanel() {
            if (panel != null) return;
            var hud = HudManager.Instance;
            if (hud == null) return;
            var go = new GameObject("AuditorPanel");
            go.transform.SetParent(hud.transform);
            go.transform.localPosition = new Vector3(-3.6f, 0.9f, -50f);
            go.transform.localScale = Vector3.one;
            panel = go.AddComponent<TMPro.TextMeshPro>();
            panel.fontSize = 1.55f;
            panel.color = PanelColor;
            panel.alignment = TMPro.TextAlignmentOptions.TopLeft;
            panel.enableWordWrapping = false;
            go.SetActive(false);
        }

        // Full localized task line ("Electrical: Fix Wiring (1/3)") straight from the task object -
        // vanilla builds exactly this string for the task list, so it is translated and includes the
        // room and the step counter for free. Falls back to the raw enum name if anything goes wrong.
        private static string TaskLine(NormalPlayerTask task) {
            try {
                if (task == null) return "?";
                var sb = new Il2CppSystem.Text.StringBuilder();
                task.AppendTaskText(sb);
                string s = sb.ToString();
                if (string.IsNullOrWhiteSpace(s)) return task.TaskType.ToString();
                return s.Replace("\r", "").Replace("\n", " ").Trim();
            } catch {
                try { return task.TaskType.ToString(); } catch { return "?"; }
            }
        }

        private static void BuildPanel(StringBuilder sb) {
            float mult = Auditor.CooldownMultiplier();
            int target = Mathf.RoundToInt(Auditor.RevertsForFull?.getFloat() ?? 8f);
            sb.Append(UCLocalization.Tr("uc.ui.auditor.header",
                Auditor.revertCount, target, mult.ToString("0.00")));

            var queue = Auditor.Queue;
            if (queue.Count == 0) {
                sb.Append('\n').Append(UCLocalization.Tr("uc.ui.auditor.queue_empty"));
            } else {
                for (int i = 0; i < queue.Count; i++) {
                    var e = queue[i];
                    sb.Append('\n').Append('[').Append(i + 1).Append("] ").Append(TaskLine(e.localTask));
                    if (Auditor.ShowsCompleterName()) {
                        var victim = Helpers.playerById(e.victim);
                        if (victim != null && victim.Data != null)
                            sb.Append("  ").Append(Helpers.cs(victim.Data.Color, victim.Data.PlayerName));
                    }
                    sb.Append("  ");
                    if (e.locked)
                        sb.Append(Helpers.cs(HoldColor, UCLocalization.Tr("uc.ui.auditor.hold")));
                    else
                        sb.Append(Mathf.CeilToInt(e.remaining)).Append('s');
                }
            }

            if (Time.time - Auditor.lastOverflow < OverflowWarnSeconds && Auditor.lastOverflow > 0f)
                sb.Append('\n').Append(Helpers.cs(WarnColor, UCLocalization.Tr("uc.ui.auditor.queue_full")));
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        [HarmonyPriority(Priority.Low)]
        static class HudUpdatePatch {
            private static readonly StringBuilder sb = new StringBuilder(256);

            public static void Postfix() {
                try {
                    // victim notice timeout
                    if (notice != null && notice.gameObject.activeSelf && Time.time > noticeUntil)
                        notice.gameObject.SetActive(false);

                    bool show = Auditor.IsLocalAuditor()
                                && PlayerControl.LocalPlayer.Data != null
                                && !PlayerControl.LocalPlayer.Data.IsDead
                                && MeetingHud.Instance == null;
                    if (!show) {
                        if (panel != null && panel.gameObject.activeSelf) panel.gameObject.SetActive(false);
                        return;
                    }
                    EnsurePanel();
                    if (panel == null) return;
                    if (!panel.gameObject.activeSelf) panel.gameObject.SetActive(true);

                    sb.Clear();
                    BuildPanel(sb);
                    panel.text = sb.ToString();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[AuditorHud] update failed: {e}");
                }
            }
        }

        // The labels live under HudManager and die with it; the references must not outlive the scene.
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.First)]
        static class HudStartPatch {
            public static void Prefix() { panel = null; notice = null; noticeUntil = 0f; }
        }
    }
}
