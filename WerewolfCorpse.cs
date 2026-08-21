// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * WerewolfCorpse - a beast shot down in wolf form lies there as the beast.
 *
 * WHAT IT DOES
 * When the Hunter's silver bolt kills the werewolf while it is transformed, WerewolfFx plays the
 * death flipbook and then hands its last frame over here. That frame is painted onto the REAL dead
 * body, blown up well past crewmate size, with blood pooled around it. It stays that way until
 * somebody reports it.
 *
 * ONLY THAT DEATH
 * Every other way the werewolf can die (voted out, killed in human form, guessed) leaves the
 * ordinary vanilla corpse, untouched. There is no crewmate-shaped "werewolf corpse" artwork any
 * more: the beast look is worth having exactly where the beast actually died as a beast.
 *
 * WHY IT GOES ON THE GAME'S OWN DEAD BODY
 * REPORTING. Bodies are found by their collider (an overlap circle around the player, filtered for
 * DeadBody components), never by what is drawn - so switching the game's renderers off hides the
 * crewmate artwork and changes nothing else. The object, its collider, its ParentId and its
 * "already reported" flag stay vanilla. That is also what makes the carcass last until the meeting:
 * the DeadBody is the object the game keeps until then. A free-floating sprite would have looked
 * identical and been unreportable.
 *
 * NO PLAYER COLOUR, ON PURPOSE
 * The carcass carries its own colours (fur, silver, claws) and is drawn on child renderers with
 * their default sprite material, so it shows as authored. A transformed werewolf is pitch black
 * anyway - there is no player colour left on it to reveal.
 *
 * WHY IT POLLS INSTEAD OF HOOKING THE SPAWN
 * The corpse object is not created by a single method this mod could patch cleanly: it appears a
 * few frames after the kill. So the swap is attempted from the werewolf's own HUD tick, at most
 * four times a second while the wolf is dead. While the flipbook is still running the tick is NOT
 * throttled - the body can appear in any frame, and a crewmate corpse next to the dying beast reads
 * as a second victim.
 *
 * EVERY CLIENT DRAWS ITS OWN, AND THEY MUST MATCH
 * Nothing here is synced. The layout of the blood pools is therefore fixed, never random: two
 * players standing over the same carcass have to see the same thing.
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

        private const string CarcassRootName = "UCWerewolfCarcass";
        private const float RetryInterval = 0.25f;

        // SIZE. The beast is 1.5x a crewmate on its feet (WerewolfFx's WolfSizePatch), and a body
        // sprawled on the floor covers more ground than one standing up. The death frames also carry
        // a lot of empty space above the ground line, so the drawn carcass is far smaller than the
        // sprite. 2.4 lands the beast itself at roughly two and a half crewmates across: unmistakably
        // bigger than a body, without swallowing the room it lies in.
        //
        // Every number below was set by compositing the real sprites at these values against a
        // crewmate for scale, not by guessing: at the first attempt the pools were wider than the
        // corridor.
        private const float CarcassScale = 2.4f;

        // The death frames are 224 px at 200 ppu (1.12 units) drawn on a ground line at 90 % of the
        // frame height, i.e. 0.448 units below the sprite's centre. Lifting the sprite by that much
        // (times the scale, or it sinks as it grows) puts the beast's ground line where the player
        // died. Adjust here if a playtest shows it floating or sunk.
        private const float CarcassGroundLift = 0.448f;

        // Blood underneath. The ring art is one pool with spatter, so several of them at different
        // sizes, offsets and turns read as a single large mess instead of as four identical stamps.
        // They sit low in the frame because that is where the beast actually lies - the artwork's
        // ground line is at 90 % of the frame height, not in the middle.
        private static readonly Vector3[] PoolOffsets = {
            new Vector3( 0.00f, -0.36f, 0f),
            new Vector3(-0.44f, -0.42f, 0f),
            new Vector3( 0.46f, -0.40f, 0f),
            new Vector3( 0.10f, -0.18f, 0f),
        };
        private static readonly float[] PoolScales = { 0.72f, 0.42f, 0.38f, 0.30f };
        private static readonly float[] PoolRotations = { 0f, 118f, -74f, 203f };
        private const float PoolAlpha = 0.9f;

        // NO SILVER BOLTS ON THE BODY. The overlay prop is a volley drawn in flight, complete with
        // motion streaks and an impact spark, and no amount of scaling or turning made it read as
        // "stuck in the carcass" - it stayed a picture of arrows flying, laid on top of a corpse.
        // The silver is told in the death sequence and the sound; the body just lies there.

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

                // NOT throttled: while the flipbook plays, the beast IS the animation, and the body
                // the game spawns for the same kill would lie next to it as a second victim. The
                // body can appear in any frame, so it has to be caught in any frame.
                if (WerewolfFx.SilverDeathPlaying) { HideBody(wolf.PlayerId); return; }

                // No carcass to hand over means the wolf died some other way: leave the vanilla
                // corpse exactly as it is.
                if (WerewolfFx.DeathCarcass == null) return;

                if (Time.time < nextTry) return;
                nextTry = Time.time + RetryInterval;
                TryStyle(wolf.PlayerId);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[WerewolfCorpse] tick failed: {e}");
            }
        }

        // Renderers off rather than sprites cleared: nothing here needs the artwork back, and a
        // cleared sprite cannot be told apart from one we never set.
        private static void HideBody(byte wolfId) {
            foreach (var dead in UnityEngine.Object.FindObjectsOfType<DeadBody>()) {
                if (dead == null || dead.ParentId != wolfId) continue;
                var renderers = dead.bodyRenderers;
                if (renderers == null) continue;
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i] != null) renderers[i].enabled = false;
            }
        }

        private static void TryStyle(byte wolfId) {
            var carcass = WerewolfFx.DeathCarcass;
            if (carcass == null) return;

            foreach (var dead in UnityEngine.Object.FindObjectsOfType<DeadBody>()) {
                if (dead == null || dead.ParentId != wolfId) continue;

                var renderers = dead.bodyRenderers;
                if (renderers == null || renderers.Length == 0 || renderers[0] == null) continue;
                var main = renderers[0];

                int id = dead.GetInstanceID();
                if (styled.Contains(id)) {
                    // The carcass is drawn by our children alone, so the game's own renderers have
                    // to STAY off. Re-asserted rather than trusted: this runs four times a second
                    // anyway, and anything switching them back on would put a crewmate corpse back
                    // underneath the beast.
                    for (int i = 0; i < renderers.Length; i++)
                        if (renderers[i] != null) renderers[i].enabled = false;
                    continue;
                }

                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i] != null) renderers[i].enabled = false;

                // One root under the body's renderer, so the whole scene inherits position, flipping
                // and the sorting layer, and dies with the body when the meeting clears it.
                var root = new GameObject(CarcassRootName);
                root.transform.SetParent(main.transform, false);
                root.layer = main.gameObject.layer;
                root.transform.localPosition = new Vector3(0f, CarcassGroundLift * CarcassScale, 0f);
                root.transform.localScale = Vector3.one * CarcassScale;

                var pool = UCAssets.WerewolfBloodRing;
                if (pool != null)
                    for (int i = 0; i < PoolOffsets.Length; i++)
                        AddLayer(root, main, "Pool" + i, pool, PoolOffsets[i], PoolScales[i], 1,
                                 PoolRotations[i], new Color(1f, 1f, 1f, PoolAlpha));

                // The carcass itself, above the blood.
                AddLayer(root, main, "Body", carcass, Vector3.zero, 1f, 2, 0f, Color.white);

                styled.Add(id);
                UnknownsCollectionPlugin.Logger?.LogInfo("[WerewolfCorpse] the beast's carcass is now the corpse.");
            }
        }

        // A child sprite on the carcass root. Fresh SpriteRenderers come with Unity's default sprite
        // material, so these show as authored - no PlayerMaterial, no tint, no shader lookup.
        private static void AddLayer(GameObject root, SpriteRenderer main, string name, Sprite sprite,
                                     Vector3 offset, float scale, int order, float rotation, Color colour) {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.layer = root.layer;
            go.transform.localPosition = offset;
            go.transform.localScale = Vector3.one * scale;
            if (rotation != 0f) go.transform.localRotation = Quaternion.Euler(0f, 0f, rotation);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = colour;
            sr.sortingLayerID = main.sortingLayerID;
            sr.sortingOrder = main.sortingOrder + order;
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
