// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * WerewolfPawprints - in wolf form the beast leaves black paw prints for the Detective
 * (User 2026-10-02), instead of footprints in his colour.
 *
 * TOR's Detective footprints live in FootprintHolder (Objects/Footprint.cs): MakeFootprint takes a
 * pooled Footprint object (private nested class) and puts it on the player's spot, FootprintUpdate
 * recolours every active one every 0.1 s (player colour, grey when anonymous/camouflaged) and fades it.
 * Only the Detective's own client ever makes footprints, so this is the Detective's view only.
 *
 * Since audit 21.10. NO Harmony patch on TOR's footprint methods: both are hot managed methods (made per
 * player per interval, recoloured ten times a second), the pattern where .NET tiering silently drops a
 * detour. A lost MakeFootprint postfix showed the wolf's real colour, a lost bookkeeping left innocent
 * players with black paws. Instead a small component on the HUD reads _activeFootprints in LateUpdate,
 * which Unity runs after the Invoke that recolours, so the black always wins and nothing flickers:
 *  - a footprint object seen for the first time, or reused (its lifetime went UP again), is classified:
 *    paw when its owner is the werewolf in wolf form right now (MakeFootprint ran this very frame);
 *  - paws get the paw sprite and black (keeping TOR's fade), the others TOR's own sprite back.
 * Black stays black under camouflage and anonymous footprints: the beast is visible to everyone in
 * wolf form anyway, so the paws give nothing away that the eyes do not.
 * The paw sprite is werewolf_pawprint.png (100x100 at 600 ppu like TOR's Footprint.png); until it is
 * embedded TOR's own footprint shape is used, in black.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using TheOtherRoles.Objects;

namespace UnknownsCollection {
    public static class WerewolfPawprints {
        private static readonly Type footprintType = typeof(FootprintHolder).GetNestedType("Footprint", BindingFlags.NonPublic);
        // the private static backing field: the public Instance getter would CREATE a holder
        private static readonly FieldInfo instanceField = AccessTools.Field(typeof(FootprintHolder), "_instance");
        private static readonly FieldInfo activeField = AccessTools.Field(typeof(FootprintHolder), "_activeFootprints");
        private static readonly FieldInfo rendererField = footprintType != null ? AccessTools.Field(footprintType, "Renderer") : null;
        private static readonly FieldInfo ownerField = footprintType != null ? AccessTools.Field(footprintType, "Owner") : null;
        private static readonly FieldInfo lifetimeField = footprintType != null ? AccessTools.Field(footprintType, "Lifetime") : null;

        // every active footprint object (TOR's pooled managed instances): paw or not, lifetime last frame
        private static readonly Dictionary<object, (bool paw, float life)> seen = new Dictionary<object, (bool, float)>();
        private static readonly List<object> gone = new List<object>();
        private static readonly HashSet<object> alive = new HashSet<object>();
        private static Sprite torSprite;   // TOR's own footprint sprite, captured before the first swap
        private static bool warned;

        private static bool Ready => instanceField != null && activeField != null && rendererField != null
                                     && ownerField != null && lifetimeField != null;

        private static void Warn(string what) {
            if (warned) return;
            warned = true;
            UnknownsCollectionPlugin.Logger?.LogWarning($"[WerewolfPawprints] {what} - paws disabled, TOR's footprints stay as they are.");
        }

        private static void Tick() {
            if (!Ready) { if (seen.Count == 0) Warn("TOR's footprint internals not found"); return; }
            var holder = instanceField.GetValue(null) as FootprintHolder;
            if (holder == null) { seen.Clear(); return; }
            if (activeField.GetValue(holder) is not IList list) return;
            if (list.Count == 0 && seen.Count == 0) return;

            alive.Clear();
            foreach (object print in list) {
                if (print == null || rendererField.GetValue(print) is not SpriteRenderer r || r == null) continue;
                alive.Add(print);
                float life = (float)lifetimeField.GetValue(print);
                bool known = seen.TryGetValue(print, out var st);
                bool paw = st.paw;
                if (!known || life > st.life + 0.01f) {   // new, or a pool object put down again
                    if (torSprite == null && !(known && st.paw)) torSprite = r.sprite;
                    var owner = ownerField.GetValue(print) as PlayerControl;
                    paw = Werewolf.active && Werewolf.wolfForm && Werewolf.werewolf != null
                          && owner != null && owner.PlayerId == Werewolf.werewolf.PlayerId;
                    if (paw) r.sprite = UCAssets.WerewolfPawprint ?? torSprite ?? r.sprite;
                    else if (known && st.paw && torSprite != null) r.sprite = torSprite;   // TOR's shape again
                }
                seen[print] = (paw, life);
                if (paw) {
                    var c = r.color;
                    if (c.a > 0f) r.color = new Color(0.02f, 0.02f, 0.02f, c.a);
                }
            }
            // back in the pool: keep the paw flag so a reuse restores TOR's sprite, drop the rest
            gone.Clear();
            foreach (var kv in seen) if (!alive.Contains(kv.Key) && !kv.Value.paw) gone.Add(kv.Key);
            foreach (var k in gone) seen.Remove(k);
        }

        private class PawprintTicker : MonoBehaviour {
            static PawprintTicker() => ClassInjector.RegisterTypeInIl2Cpp<PawprintTicker>();
            public PawprintTicker(IntPtr ptr) : base(ptr) { }

            private void LateUpdate() {
                try { Tick(); } catch (Exception e) { Warn(e.Message); }
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    if (__instance != null && __instance.gameObject.GetComponent<PawprintTicker>() == null)
                        __instance.gameObject.AddComponent<PawprintTicker>();
                } catch (Exception e) { Warn(e.Message); }
            }
        }

        // The pool objects belong to the holder of this round; a new round starts with a new holder.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch {
            public static void Postfix() => seen.Clear();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyPatch {
            public static void Postfix() => seen.Clear();
        }
    }
}
