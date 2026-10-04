// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UCButtonSlots - puts a button that rides ON TOP of another role (a modifier's button) on the first
 * HUD slot that is free right now.
 *
 * A modifier cannot know which role it sits on, and TOR's role buttons take the usual slots: the
 * Gambler's bet button sat on lowerRowRight with F, exactly where the Medic, Time Master, Deputy,
 * Tracker or Mayor have theirs (audit 2026-10-04). Same approach as the Saboteur's search button:
 * every visible CustomButton and the active VANILLA HUD buttons (vent, ability, pet, kill,
 * sabotage, which are not CustomButtons but share the grid) count as occupied.
 */

using System.Collections.Generic;
using TheOtherRoles.Objects;
using UnityEngine;

namespace UnknownsCollection {
    internal static class UCButtonSlots {
        internal static readonly Vector3[] DefaultSlots = {
            CustomButton.ButtonPositions.lowerRowRight,
            CustomButton.ButtonPositions.lowerRowCenter,
            CustomButton.ButtonPositions.lowerRowLeft,
            CustomButton.ButtonPositions.upperRowLeft,
            CustomButton.ButtonPositions.upperRowCenter,
            CustomButton.ButtonPositions.upperRowFarLeft,
        };
        private static readonly Vector3 Fallback = CustomButton.ButtonPositions.highRowRight + new Vector3(0f, 0.6f, 0f);
        private const float Eps = 0.25f;
        private static readonly List<Vector3> vanilla = new List<Vector3>();

        private static List<Vector3> VanillaOffsets(HudManager hud) {
            vanilla.Clear();
            if (hud == null || hud.UseButton == null) return vanilla;
            Vector3 anchor = hud.UseButton.transform.localPosition;
            void Add(ActionButton b) { if (b != null && b.isActiveAndEnabled) vanilla.Add(b.transform.localPosition - anchor); }
            Add(hud.ImpostorVentButton); Add(hud.SabotageButton); Add(hud.AbilityButton); Add(hud.PetButton); Add(hud.KillButton);
            return vanilla;
        }

        private static bool Same(Vector3 a, Vector3 b) => Mathf.Abs(a.x - b.x) < Eps && Mathf.Abs(a.y - b.y) < Eps;

        /// Moves `button` to the first free slot. Call it while the button is visible (per frame is
        /// fine: it only compares a few positions).
        internal static void Place(CustomButton button, Vector3[] slots = null) {
            if (button == null || button.actionButtonGameObject == null || !button.actionButtonGameObject.activeSelf) return;
            var occupied = VanillaOffsets(button.hudManager ?? HudManager.Instance);
            Vector3 chosen = Fallback;
            foreach (var slot in slots ?? DefaultSlots) {
                bool free = true;
                foreach (var b in CustomButton.buttons) {
                    if (b == null || b == button || b.mirror) continue;
                    if (b.actionButtonGameObject == null || !b.actionButtonGameObject.activeSelf) continue;
                    if (Same(b.PositionOffset, slot)) { free = false; break; }
                }
                if (free) foreach (var off in occupied) if (Same(off, slot)) { free = false; break; }
                if (free) { chosen = slot; break; }
            }
            button.PositionOffset = chosen;
        }
    }
}
