// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * WerewolfCorpse - the beast leaves a body of its own.
 *
 * When the werewolf dies, its corpse is redrawn instead of the vanilla dead body. WHICH artwork
 * depends on the shape it died in:
 *   - killed in WOLF FORM by a silver bolt: the last frame of WerewolfFx's death sequence, handed
 *     over the moment that animation ends. The beast lies there as the beast, and it lies there
 *     until somebody reports it. While the animation is still running, the real body is kept hidden
 *     so the two are never on the floor at the same time.
 *   - any other death: a pixel-art "defeated" crewmate sprite in the player's colour (see
 *     Assets/WerewolfAssetGen/Corpse.cs for the artwork).
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

        // REPORTING IS UNTOUCHED, AND THAT IS THE WHOLE REASON FOR THIS DESIGN
        // The carcass is painted onto the game's own DeadBody rather than left lying around as a
        // free sprite. Reporting finds bodies by their collider (an overlap circle around the
        // player, filtered for DeadBody components), never by what is drawn - so switching the
        // game's renderers off hides the crewmate artwork and nothing else. The object, its
        // collider, its ParentId and its "already reported" flag are all still the vanilla ones,
        // which is also why the carcass survives until the meeting: the DeadBody is what the game
        // keeps until then. A free-floating sprite would have looked the same and been unreportable.
        //
        // The death frames are 224 px at 200 ppu (1.12 units) and drawn on a ground line at 90 % of
        // the frame height, i.e. 0.4 * 1.12 = 0.448 units below the sprite's centre. Lifting the
        // child by that much puts the beast's ground line where the corpse's own centre sits, which
        // is where the player died. Adjust here if a playtest shows it floating or sunk.
        private const float CarcassOffsetY = 0.448f;

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

                // NOT throttled: while the silver death flipbook plays, the beast IS the animation,
                // and the body the game spawns for the same kill would lie next to it as a second
                // victim. The body can appear in any frame, so it has to be caught in any frame -
                // a quarter second of a stray corpse is a quarter second too many.
                if (WerewolfFx.SilverDeathPlaying) { HideBody(wolf.PlayerId); return; }

                if (Time.time < nextTry) return;
                nextTry = Time.time + RetryInterval;
                TryStyle(wolf.PlayerId);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[WerewolfCorpse] tick failed: {e}");
            }
        }

        // Renderers off rather than sprites cleared: TryStyle has to be able to hand the body back
        // its artwork afterwards, and a cleared sprite cannot be told apart from one we never set.
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
            // The wolf-form carcass wins when there is one: a beast killed in its own shape lies
            // there as the beast, not as the crewmate it used to be.
            var carcass = WerewolfFx.DeathCarcass;
            var body = UCAssets.WerewolfCorpseBody;
            var detail = UCAssets.WerewolfCorpseDetail;
            if (carcass == null && (body == null || detail == null)) return;

            foreach (var dead in UnityEngine.Object.FindObjectsOfType<DeadBody>()) {
                if (dead == null || dead.ParentId != wolfId) continue;

                var renderers = dead.bodyRenderers;
                if (renderers == null || renderers.Length == 0 || renderers[0] == null) continue;
                var main = renderers[0];

                int id = dead.GetInstanceID();
                if (styled.Contains(id)) {
                    // The carcass is drawn by the child alone, so the game's own renderers have to
                    // STAY off. Re-asserted instead of trusted: this loop runs four times a second
                    // anyway, and anything that switched them back on would put a crewmate corpse
                    // back underneath the beast.
                    if (carcass != null)
                        for (int i = 0; i < renderers.Length; i++)
                            if (renderers[i] != null) renderers[i].enabled = false;
                    continue;
                }

                // The detail layer, on a child of the renderer so it inherits position, flipping and
                // the sorting layer, and sits exactly one step in front of the body.
                var child = new GameObject(DetailChildName);
                child.transform.SetParent(main.transform, false);
                child.layer = main.gameObject.layer;
                var detailRenderer = child.AddComponent<SpriteRenderer>();
                detailRenderer.sortingLayerID = main.sortingLayerID;
                detailRenderer.sortingOrder = main.sortingOrder + 1;

                if (carcass != null) {
                    // Killed in wolf form: the last frame of the death sequence IS the body from
                    // here on, and it stays until somebody reports it, because the DeadBody it hangs
                    // on is what the game keeps until a meeting.
                    //
                    // It brings its own colours (fur, silver, claws) and needs no player colour: the
                    // beast is pitch black while transformed, so there is nothing about the wearer
                    // left to show. That is why it goes on the child, whose default sprite material
                    // shows it as authored, while every renderer the game owns stays off.
                    detailRenderer.sprite = carcass;
                    child.transform.localPosition = new Vector3(0f, CarcassOffsetY, 0f);
                    for (int i = 0; i < renderers.Length; i++)
                        if (renderers[i] != null) renderers[i].enabled = false;
                    UnknownsCollectionPlugin.Logger?.LogInfo("[WerewolfCorpse] the beast's carcass is now the corpse.");
                } else {
                    // Any other death: the crewmate corpse in the player's colour, because a body
                    // still has to answer "who is lying there".
                    main.enabled = true;                       // may have been hidden mid-sequence
                    main.sprite = body;                        // same renderer, same PlayerMaterial
                    detailRenderer.sprite = detail;

                    // Any further renderers of the same body (vanilla splits body and "shadow")
                    // would still draw the old corpse underneath, so they are cleared.
                    for (int i = 1; i < renderers.Length; i++)
                        if (renderers[i] != null) renderers[i].sprite = null;
                    UnknownsCollectionPlugin.Logger?.LogInfo("[WerewolfCorpse] restyled the werewolf's corpse.");
                }

                styled.Add(id);
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
