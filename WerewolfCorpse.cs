// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * WerewolfCorpse - the beast leaves a body of its own.
 *
 * When the werewolf dies, its corpse is redrawn as a pixel-art "defeated" sprite (see
 * Assets/WerewolfAssetGen/Corpse.cs for the artwork) instead of the vanilla dead body.
 *
 * TWO LAYERS, BECAUSE THE COLOUR HAS TO SURVIVE
 * A dead body in Among Us still answers "who is lying there", and it answers it through the player
 * colour. So the sprite is split:
 *   - the BODY layer replaces the sprite on the game's own dead-body renderer and keeps its
 *     PlayerMaterial, which is what paints the player colour onto it. The artwork is white where the
 *     colour should be full and grey where it should be shaded, so the shading falls out of the same
 *     multiplication rather than being painted on.
 *   - the DETAIL layer (outline, visor, X eyes, blood, claws) is drawn by a child renderer this file
 *     creates. A freshly created SpriteRenderer comes with Unity's default sprite material, so those
 *     colours are shown as authored - no shader lookup, no material juggling.
 *
 * WHY IT POLLS INSTEAD OF HOOKING THE SPAWN
 * The corpse object is not created by a single method this mod could patch cleanly: it appears a few
 * frames after the kill, and the werewolf can die in several ways (impostor kill, sheriff shot,
 * guess, its own charge). So the swap is attempted from the werewolf's own HUD tick, at most four
 * times a second and only while the wolf is dead and its corpse has not been restyled yet. Once done
 * for that body it never runs again, and a meeting (which removes every corpse) re-arms it.
 *
 * The corpse is the WEREWOLF'S OWN. It therefore reveals what the dead player was - that is the
 * point of it, and the reason it is a switch (option 1526) rather than always on.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {

    public static class WerewolfCorpse {

        public static CustomOption Enabled;   // 1526

        private const string DetailChildName = "UCWerewolfCorpseDetail";
        private const float RetryInterval = 0.25f;

        private static readonly HashSet<int> styled = new HashSet<int>();
        private static float nextTry = float.NegativeInfinity;

        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(1526, Types.Impostor, "Werewolf Leaves Its Own Corpse",
                    true, Werewolf.SpawnRate);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[WerewolfCorpse] CreateOptions failed: {e}");
            }
        }

        // Called every frame from the Werewolf's HUD tick; throttles itself.
        public static void Tick() {
            try {
                if (Enabled == null || !Enabled.getBool()) return;
                var wolf = Werewolf.werewolf;
                if (wolf == null || wolf.Data == null || !wolf.Data.IsDead) return;
                if (Time.time < nextTry) return;
                nextTry = Time.time + RetryInterval;
                TryStyle(wolf.PlayerId);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[WerewolfCorpse] tick failed: {e}");
            }
        }

        private static void TryStyle(byte wolfId) {
            var body = UCAssets.WerewolfCorpseBody;
            var detail = UCAssets.WerewolfCorpseDetail;
            if (body == null || detail == null) return;

            foreach (var dead in UnityEngine.Object.FindObjectsOfType<DeadBody>()) {
                if (dead == null || dead.ParentId != wolfId) continue;
                int id = dead.GetInstanceID();
                if (styled.Contains(id)) continue;

                var renderers = dead.bodyRenderers;
                if (renderers == null || renderers.Length == 0 || renderers[0] == null) continue;
                var main = renderers[0];

                // The body itself: same renderer, same PlayerMaterial, new artwork.
                main.sprite = body;

                // Everything else, on a child of the renderer so it inherits position, flipping and
                // the sorting layer, and sits exactly one step in front of the body.
                var child = new GameObject(DetailChildName);
                child.transform.SetParent(main.transform, false);
                child.layer = main.gameObject.layer;
                var detailRenderer = child.AddComponent<SpriteRenderer>();
                detailRenderer.sprite = detail;
                detailRenderer.sortingLayerID = main.sortingLayerID;
                detailRenderer.sortingOrder = main.sortingOrder + 1;

                // Any further renderers of the same body (vanilla splits body and "shadow") would
                // still draw the old corpse underneath, so they are cleared.
                for (int i = 1; i < renderers.Length; i++)
                    if (renderers[i] != null) renderers[i].sprite = null;

                styled.Add(id);
                UnknownsCollectionPlugin.Logger?.LogInfo("[WerewolfCorpse] restyled the werewolf's corpse.");
            }
        }

        // Corpses do not survive a meeting, so the ids do not either - and a new round must never
        // consider an old instance id as "already done".
        public static void Reset() {
            styled.Clear();
            nextTry = float.NegativeInfinity;
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        private static class MeetingResetPatch {
            public static void Postfix() => Reset();
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        private static class RoundResetPatch {
            public static void Postfix() => UCResetGuard.Run("WerewolfCorpse", Reset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class LobbyResetPatch {
            public static void Postfix() => Reset();
        }
    }
}
