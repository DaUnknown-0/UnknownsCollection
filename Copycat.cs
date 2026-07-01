// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Copycat (Neutral)
 *
 * A normal TOR Crewmate is silently promoted to "The Copycat" at game start (host-authoritative pick,
 * broadcast via RPC 206). The Copycat LEARNS abilities by sensing role-specific TOR ability RPCs while
 * alive; up to MaxAbilitiesStored are kept at once (oldest dropped when full). Each learned ability is a
 * CustomButton on the Copycat's HUD. The Copycat wins WITH the winning team if it is alive at game end
 * and has used at least AbilitiesNeededToWin abilities.
 *
 * Copyable abilities (each learned from a role-specific RPC, so learning is reliable and identical on
 * every client - not proximity based):
 *   Camouflage (RPC 131, Camouflager) - turn grey for a while.
 *   Morphling  (RPC 130, Morphling)   - morph into a chosen player for a while.
 *   Shield     (RPC 126, TimeMaster)  - become unkillable (normal kills suppressed) for a while.
 *   Shoot      (RPC 108, any kill)    - a Sheriff-style shot: kills an Impostor, backfires on Crew.
 *   Vent       (Vent.EnterVent, anyone vents) - gain vent access; the host promotes the Copycat's AU role
 *              to Engineer so the native vent button appears (roleCanUseVents alone gives no button).
 *
 * All ability effects are driven by our own RPC (206 / SubUseAbility), applied locally on every client
 * and timed out independently per client, so cosmetics and the shield stay consistent for everyone.
 *
 * Options live in the 1520-1524 block. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using AmongUs.GameOptions; // RoleTypes (promote to Engineer so the native vent button shows)
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Copycat {
        // ---- Ability enum ----
        public enum Ability : byte {
            Camouflage = 0,
            Morphling = 1,
            Shield = 2,
            Shoot = 3,
            Vent = 4
        }

        private static readonly string[] AbilityNames = { "CAMO", "MORPH", "SHIELD", "SHOOT", "VENT" };

        // Effect durations (seconds).
        private const float CamoDuration = 10f;
        private const float MorphDuration = 15f;
        private const float ShieldDuration = 5f;

        // Per-ability cooldowns (seconds). Our ability buttons use CustomButton's HasEffect=false overload,
        // which never auto-resets its Timer — so without an explicit MaxTimer + a Timer reset on click the
        // abilities would have NO cooldown (spammable Shoot kills / permanent Shield). We mirror the SOURCE
        // role's own configured cooldown so a copied ability behaves like the real thing. Falls back to 25s
        // if the source role's value isn't loaded (e.g. that role isn't in the game).
        private static float AbilityCooldown(Ability a) {
            float cd = a switch {
                Ability.Shoot => Sheriff.cooldown,
                Ability.Shield => TimeMaster.cooldown,
                Ability.Camouflage => Camouflager.cooldown,
                Ability.Morphling => Morphling.cooldown,
                _ => 25f
            };
            return cd > 0f ? cd : 25f;
        }

        // ---- Theme ----
        public static readonly Color Color = new Color(0.85f, 0.45f, 0.85f); // purple

        // ---- Options (IDs 1520-1524) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption MaxAbilitiesStored;
        public static CustomOption CopycatHasTasks;
        public static CustomOption AbilitiesNeededToWin;

        // ---- Runtime state ----
        public static PlayerControl copycat;
        public static bool active;

        // Learned abilities (ordered by learning time) + which have actually been used (for the win gate).
        private static readonly List<Ability> learnedAbilities = new();
        private static readonly HashSet<Ability> usedAbilities = new();

        // Effect state (kept in sync on every client; each client times out its own copy).
        public static bool shielded;
        private static float shieldEndTime;
        private static bool camouflaged;
        private static float camoEndTime;
        private static byte morphTargetId = byte.MaxValue;
        private static bool isMorphed;
        private static float morphEndTime;

        // Win snapshot: captured at game-end BEFORE TOR's resetVariables wipes copycat/usedAbilities.
        // Deliberately NOT reset in resetVariables (TOR's end-of-game reset would clear it first).
        private static byte winnerCopycatId = byte.MaxValue;

        // ---- TOR CustomRPC byte values (enum is internal) - verified against TheOtherRoles RPC.cs ----
        private const byte TorMorphlingRpc = 130;  // CustomRPC.MorphlingMorph
        private const byte TorCamouflageRpc = 131; // CustomRPC.CamouflagerCamouflage
        private const byte TorTimeMasterRpc = 126; // CustomRPC.TimeMasterShield
        private const byte TorMurderRpc = 108;     // CustomRPC.UncheckedMurderPlayer (any kill; also our Shoot)
        private const byte TorVentRpc = 107;       // CustomRPC.UseUncheckedVent (anyone venting)

        // ---- Custom RPC (206) subtypes ----
        private const byte RpcId = 206; // == UnknownsCollectionPlugin.CopycatRpcId
        private const byte SubSetCopycat = 0;
        private const byte SubUseAbility = 1; // abilityId, targetId (byte.MaxValue when unused)
        private const byte SubLearn = 2;      // abilityId - broadcast a learn the local Copycat decided on (e.g. sight-gated Vent)

        // ---- Role identity ----
        private static RoleInfo copycatInfo;
        public static RoleInfo CopycatInfo() => copycatInfo ??= new RoleInfo(
            "Copycat", Color, "Copy abilities you witness and win with the winners",
            "Copy abilities you witness and win with the winners", RoleId.Crewmate)
        { isNeutral = true };

        // Buttons
        private static readonly Dictionary<Ability, TheOtherRoles.Objects.CustomButton> abilityButtons = new();
        private static Sprite cachedButtonSprite;

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1520, Types.Neutral, "Copycat",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1521, Types.Neutral, "Copycat Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                MaxAbilitiesStored = CustomOption.Create(1522, Types.Neutral, "Copycat Max Stored Abilities",
                    3f, 1f, 5f, 1f, SpawnRate);
                CopycatHasTasks = CustomOption.Create(1523, Types.Neutral, "Copycat Has Tasks",
                    true, SpawnRate);
                AbilitiesNeededToWin = CustomOption.Create(1524, Types.Neutral, "Copycat Abilities Needed To Win",
                    1f, 0f, 5f, 1f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Copycat] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] CreateOptions failed: {e}");
            }
        }

        // The win is handled by attribute patches (OnGameEndPatch) - no reflection / win-check hijack.
        public static void TryPatch(Harmony harmony) { }

        private static bool CopycatIsAlive() =>
            active && copycat != null && copycat.Data != null && !copycat.Data.IsDead && !copycat.Data.Disconnected;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalCopycat() =>
            copycat != null && PlayerControl.LocalPlayer != null && copycat.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        private static Ability? RpcToAbility(byte callId) {
            return callId switch {
                TorCamouflageRpc => Ability.Camouflage,
                TorMorphlingRpc => Ability.Morphling,
                TorTimeMasterRpc => Ability.Shield,
                TorMurderRpc => Ability.Shoot,
                // Vent is NOT learned here: it is gated on line-of-sight and handled in VentEnterLearnPatch.
                _ => null
            };
        }

        private static int MaxAbilities() =>
            MaxAbilitiesStored != null ? Mathf.RoundToInt(MaxAbilitiesStored.getFloat()) : 3;
        private static int NeededToWin() =>
            AbilitiesNeededToWin != null ? Mathf.RoundToInt(AbilitiesNeededToWin.getFloat()) : 1;

        // ====================================================================
        // Custom RPC senders (each applies locally too)
        // ====================================================================
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetCopycat(byte id) {
            try {
                var w = BeginRpc(SubSetCopycat);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetCopycat(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] SendSetCopycat failed: {e}"); }
        }

        private static void SendUseAbility(Ability ability, byte targetId = byte.MaxValue) {
            try {
                var w = BeginRpc(SubUseAbility);
                w.Write((byte)ability);
                w.Write(targetId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyUseAbility(ability, targetId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] SendUseAbility failed: {e}"); }
        }

        // Broadcast a learn the local Copycat decided on locally (e.g. Vent, which is gated on line-of-sight
        // and therefore can't be observed identically on every client the way an RPC-based ability is).
        private static void SendLearn(Ability ability) {
            try {
                var w = BeginRpc(SubLearn);
                w.Write((byte)ability);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                LearnAbility(ability);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] SendLearn failed: {e}"); }
        }

        // Perform an unchecked murder on every client (broadcast once + local), like the Tesla/Sheriff.
        private static void RpcUncheckedMurder(byte sourceId, byte targetId) {
            try {
                MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, TorMurderRpc, SendOption.Reliable, -1);
                w.Write(sourceId);
                w.Write(targetId);
                w.Write(byte.MaxValue); // showAnimation
                AmongUsClient.Instance.FinishRpcImmediately(w);
                RPCProcedure.uncheckedMurderPlayer(sourceId, targetId, byte.MaxValue);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] RpcUncheckedMurder failed: {e}");
            }
        }

        // ====================================================================
        // Appliers (run on every client)
        // ====================================================================
        private static void ApplySetCopycat(byte id) {
            copycat = Helpers.playerById(id);
            active = copycat != null;
            if (active) UCPromotion.Claim(id);
            learnedAbilities.Clear();
            usedAbilities.Clear();
            shielded = camouflaged = isMorphed = false;
            morphTargetId = byte.MaxValue;
            // CopycatHasTasks == false: the Copycat gets no tasks at all (cleared on every client, like
            // TOR clears tasks for Thief/Lawyer-style role changes). CopycatHasTasks == true (default):
            // keep the assigned tasks as fake tasks - TaskPatch below already excludes them from the crew
            // total, same as TOR's hasFakeTasks() roles (Jester, Jackal, ...).
            if (active && CopycatHasTasks != null && !CopycatHasTasks.getBool())
                copycat.clearAllTasks();
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Copycat] The Copycat is {copycat.Data?.PlayerName}.");
        }

        private static void ApplyUseAbility(Ability ability, byte targetId) {
            if (!active || copycat == null || !IsAlive(copycat)) return;

            switch (ability) {
                case Ability.Camouflage: StartCamouflage(); break;
                case Ability.Morphling: StartMorph(targetId); break;
                case Ability.Shield: StartShield(); break;
                case Ability.Shoot: DoShoot(targetId); break;
                case Ability.Vent: break; // venting is a passive capability granted via roleCanUseVents; nothing to apply
            }

            if (usedAbilities.Add(ability))
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Copycat] Used ability {ability} (distinct used: {usedAbilities.Count}).");
        }

        // ---- Camouflage / Morph: purely cosmetic setLook, applied on every client (never RpcSetColor,
        // which is host-authoritative and would trip anti-cheat on non-owner clients). ----
        // Camouflage greys out EVERYONE (like the real Camouflager's camouflagerCamouflage), not just the
        // Copycat — that's the whole point of the ability: nobody can be told apart while it lasts.
        private static void StartCamouflage() {
            camouflaged = true;
            camoEndTime = Time.time + CamoDuration;
            if (Helpers.MushroomSabotageActive()) return; // don't overwrite the fungle "camo"
            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                if (player != null) player.setLook("", 6, "", "", "", ""); // grey, no cosmetics
        }

        // Collision with the real TOR Camouflager (TheOtherRoles.cs Camouflager.camouflageTimer): if the
        // real Camouflager is still mid-camo, its own resetCamouflage() is responsible for un-greying
        // everyone once ITS timer runs out - if we un-grey here too, we'd cut its camo short for every
        // player. So skip the visual reset in that case; our own edge-detection in HudUpdatePatch re-greys
        // everyone if the real Camouflager's reset fires first while we're still camouflaged (see below).
        private static void EndCamouflage() {
            camouflaged = false;
            if (Helpers.MushroomSabotageActive()) return; // fungle sabotage controls looks
            if (Camouflager.camouflageTimer > 0f) return;  // real Camouflager still active - it owns the reset
            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                if (player != null) player.setDefaultLook();
            // The Copycat itself may still be morphed — re-apply that look on top of the reset.
            RestoreLook();
        }

        private static void StartMorph(byte targetId) {
            var target = Helpers.playerById(targetId);
            if (target == null || target.Data == null || copycat == null) return;
            morphTargetId = targetId;
            isMorphed = true;
            morphEndTime = Time.time + MorphDuration;
            copycat.setLook(target.Data.PlayerName, target.Data.DefaultOutfit.ColorId,
                target.Data.DefaultOutfit.HatId, target.Data.DefaultOutfit.VisorId,
                target.Data.DefaultOutfit.SkinId, target.Data.DefaultOutfit.PetId);
        }

        private static void EndMorph() {
            isMorphed = false;
            morphTargetId = byte.MaxValue;
            RestoreLook();
        }

        // Restore the Copycat's own look. Camouflage and Morph can overlap, so if the other effect is
        // still running we re-apply it instead of falling back to the default outfit.
        private static void RestoreLook() {
            if (copycat == null) return;
            if (camouflaged) { copycat.setLook("", 6, "", "", "", ""); return; }
            if (isMorphed) {
                var t = Helpers.playerById(morphTargetId);
                if (t != null && t.Data != null) {
                    copycat.setLook(t.Data.PlayerName, t.Data.DefaultOutfit.ColorId,
                        t.Data.DefaultOutfit.HatId, t.Data.DefaultOutfit.VisorId,
                        t.Data.DefaultOutfit.SkinId, t.Data.DefaultOutfit.PetId);
                    return;
                }
            }
            copycat.setDefaultLook();
        }

        private static void StartShield() {
            shielded = true;
            shieldEndTime = Time.time + ShieldDuration;
        }

        // Sheriff-style shot. The actual kill is host-authoritative and broadcast exactly once
        // (RpcUncheckedMurder), so it must NOT run per-client - only the host performs it.
        private static void DoShoot(byte targetId) {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            var target = Helpers.playerById(targetId);
            if (copycat == null || !IsAlive(copycat) || !IsAlive(target)) return;
            // Respect Medic/TimeMaster shields and other protections, like a real Sheriff shot: if the
            // kill would be suppressed, the shot is absorbed (no kill, no backfire).
            if (Helpers.checkMuderAttempt(copycat, target) != MurderAttemptResult.PerformKill) return;
            bool targetIsImpostor = target.Data.Role != null && target.Data.Role.IsImpostor;
            if (targetIsImpostor)
                RpcUncheckedMurder(copycat.PlayerId, target.PlayerId);          // clean kill
            else
                RpcUncheckedMurder(copycat.PlayerId, copycat.PlayerId);         // backfire: shooting Crew kills the Copycat
        }

        public static void MarkFromDraft(byte playerId) => ApplySetCopycat(playerId);

        // ---- Learn a witnessed ability (identical on every client - based on the observed RPC). ----
        private static void LearnAbility(Ability ability) {
            if (!active || !CopycatIsAlive()) return;
            if (learnedAbilities.Contains(ability)) return; // already known

            int max = MaxAbilities();
            if (max <= 0) return;
            if (learnedAbilities.Count >= max) {
                var oldest = learnedAbilities[0];
                learnedAbilities.RemoveAt(0);
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Copycat] Dropped oldest ability {oldest} for {ability}.");
            }
            learnedAbilities.Add(ability);
            // Learn blip only for the Copycat itself - nobody else should hear what it just picked up.
            if (IsLocalCopycat()) UCAssets.PlayCopycatLearn();
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Copycat] Learned ability {ability} (known: {learnedAbilities.Count}).");

            // The native Impostor vent button only shows for AU roles it considers vent-capable. A Copycat
            // is an AU Crewmate, so roleCanUseVents alone grants no button. Mirror exactly what TOR does for
            // venting crew (Spy/Vulture/Thief) in RPC.setRole: promote the AU role to Engineer so the vent
            // button appears. Host-authoritative + broadcast, so it's consistent on every client.
            if (ability == Ability.Vent && AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost
                && copycat != null && copycat.Data?.Role != null && !copycat.Data.Role.IsImpostor) {
                copycat.RpcSetRole(RoleTypes.Engineer);
                copycat.CoSetRole(RoleTypes.Engineer, true);
            }
        }

        // ====================================================================
        // RPC receiver + ability learning
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                try {
                    if (callId == RpcId) {
                        byte subtype = reader.ReadByte();
                        switch (subtype) {
                            case SubSetCopycat: ApplySetCopycat(reader.ReadByte()); break;
                            case SubUseAbility: {
                                var ability = (Ability)reader.ReadByte();
                                byte targetId = reader.ReadByte();
                                ApplyUseAbility(ability, targetId);
                                break;
                            }
                            case SubLearn: LearnAbility((Ability)reader.ReadByte()); break;
                        }
                        return false; // consume our own RPC
                    }

                    // Learn from tracked TOR ability RPCs. We only inspect callId here - never read the
                    // reader - so the original TOR handler still gets an untouched stream.
                    if (active && copycat != null) {
                        var ability = RpcToAbility(callId);
                        if (ability != null) LearnAbility(ability.Value);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] HandleRpc failed: {e}");
                }
                return true; // let TOR handle non-Copycat RPCs
            }
        }

        // ====================================================================
        // Shield: suppress normal kills on the shielded Copycat at the same choke point the Medic
        // shield uses (Helpers.checkMuderAttempt), so no downstream MurderPlayer side effects fire.
        // (Unchecked murders - e.g. Tesla/Saboteur/our own backfire - bypass this, by design.)
        // ====================================================================
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkMuderAttempt))]
        static class ShieldPatch {
            public static bool Prefix([HarmonyArgument(1)] PlayerControl target, ref MurderAttemptResult __result) {
                try {
                    if (active && shielded && copycat != null && target != null
                        && target.PlayerId == copycat.PlayerId && CopycatIsAlive()) {
                        __result = MurderAttemptResult.SuppressKill;
                        return false;
                    }
                } catch { }
                return true;
            }
        }

        // ====================================================================
        // Vent: once the Vent ability is learned, grant the Copycat vent access through TOR's single
        // gate (roleCanUseVents). Every part of TOR's vent machinery - the native Impostor vent button,
        // Vent.CanUse, Vent.Use, moving between vents - routes through this, so no custom button is needed.
        // ====================================================================
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.roleCanUseVents))]
        static class VentAccessPatch {
            public static void Postfix(PlayerControl player, ref bool __result) {
                try {
                    if (active && copycat != null && player != null && player.PlayerId == copycat.PlayerId
                        && CopycatIsAlive() && learnedAbilities.Contains(Ability.Vent))
                        __result = true;
                } catch { }
            }
        }

        // Learn the Vent ability when the Copycat SEES someone enter a vent. Normal venting uses AU's native
        // vent path (Vent.EnterVent, which runs on every client), NOT TOR's UseUncheckedVent RPC 107 — that
        // only fires for Trickster boxes. Like every copied ability, Vent must be WITNESSED: only the local
        // Copycat evaluates line-of-sight, then broadcasts the learn so every client stays consistent.
        [HarmonyPatch(typeof(Vent), nameof(Vent.EnterVent))]
        static class VentEnterLearnPatch {
            public static void Postfix([HarmonyArgument(0)] PlayerControl pc) {
                try {
                    if (!active || copycat == null || pc == null) return;
                    if (!IsLocalCopycat() || !CopycatIsAlive()) return;
                    if (learnedAbilities.Contains(Ability.Vent)) return;
                    if (pc.PlayerId == copycat.PlayerId) return; // don't learn from your own vent
                    if (!CopycatCanSee(pc)) return;               // only learn if actually visible
                    SendLearn(Ability.Vent);
                } catch { }
            }
        }

        // Whether the local Copycat can currently see another player: within its light radius and with an
        // unobstructed line of sight (same range + wall check TOR's setTarget uses).
        private static bool CopycatCanSee(PlayerControl other) {
            try {
                if (copycat == null || copycat.Data == null || other == null) return false;
                var ship = MapUtilities.CachedShipStatus;
                if (ship == null) return false;
                Vector2 from = copycat.GetTruePosition();
                Vector2 diff = other.GetTruePosition() - from;
                float dist = diff.magnitude;
                if (dist > ship.CalculateLightRadius(copycat.Data)) return false;
                return !PhysicsHelpers.AnyNonTriggersBetween(from, diff.normalized, dist, Constants.ShipAndObjectsMask);
            } catch { return false; }
        }

        // Count the Vent ability as "used" (for the win gate) the first time the local Copycat vents.
        [HarmonyPatch(typeof(Vent), nameof(Vent.Use))]
        static class VentUsePatch {
            public static void Postfix() {
                try {
                    if (!IsLocalCopycat() || !CopycatIsAlive()) return;
                    if (!learnedAbilities.Contains(Ability.Vent) || usedAbilities.Contains(Ability.Vent)) return;
                    // Don't credit a vent that TOR's Vent.Use prefix actually blocked (Deputy handcuff /
                    // Trapper trap) — otherwise a blocked button press would wrongly count toward the win.
                    var me = PlayerControl.LocalPlayer;
                    if (Deputy.handcuffedPlayers.Contains(me.PlayerId) || Trapper.playersOnMap.Contains(me.PlayerId)) return;
                    SendUseAbility(Ability.Vent);
                } catch { }
            }
        }

        // ====================================================================
        // Round reset
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                copycat = null;
                active = false;
                learnedAbilities.Clear();
                usedAbilities.Clear();
                shielded = camouflaged = isMorphed = false;
                morphTargetId = byte.MaxValue;
                shieldEndTime = camoEndTime = morphEndTime = 0f;
                lastRealCamoTimer = 0f;
                abilityButtons.Clear();
                // NOTE: winnerCopycatId is intentionally NOT reset here (see its declaration).
            }
        }

        // ====================================================================
        // Game start: host picks the Copycat among plain Crewmates and broadcasts it.
        // ====================================================================
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainCrewmate).ToList();
                    if (candidates.Count == 0) return;
                    SendSetCopycat(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Per-frame: effect timeouts (all clients) + local targeting for Shoot/Morph.
        // ====================================================================
        private static PlayerControl currentTarget;      // Shoot target (close)
        private static PlayerControl currentMorphTarget;  // Morph target (farther)

        // Edge-detects the real Camouflager's own reset (TheOtherRoles.cs Camouflager.camouflageTimer
        // ticking down to 0 -> Camouflager.resetCamouflage()), which un-greys everyone on every client
        // regardless of whether our own camouflage is still running. Mirrors the oldTimer>0 && newTimer<=0
        // pattern TOR itself uses in PlayerControlPatch.morphlingAndCamouflagerUpdate.
        private static float lastRealCamoTimer;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (!active || copycat == null) return;

                    // Time out effects on EVERY client (each ends its own copy so cosmetics/shield stay
                    // consistent - the start RPC set the end time on all of them).
                    if (shielded && Time.time >= shieldEndTime) shielded = false;
                    if (camouflaged && Time.time >= camoEndTime) EndCamouflage();
                    if (isMorphed && Time.time >= morphEndTime) EndMorph();

                    // The real Camouflager's reset just ran (its timer ticked 0 this frame). If our own
                    // camo is still active, TOR's resetCamouflage() un-greyed everyone out from under us -
                    // restore it. If we're not camouflaged but ARE morphed, TOR's reset also wiped the
                    // Copycat's own look back to default - reapply the morph on top.
                    float realCamoTimer = Camouflager.camouflageTimer;
                    if (lastRealCamoTimer > 0f && realCamoTimer <= 0f && !Helpers.MushroomSabotageActive()) {
                        if (camouflaged)
                            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                                if (player != null) player.setLook("", 6, "", "", "", "");
                        else if (isMorphed)
                            RestoreLook();
                    }
                    lastRealCamoTimer = realCamoTimer;

                    // Targeting only matters for the local Copycat's buttons.
                    if (!IsLocalCopycat()) return;
                    currentTarget = null;
                    currentMorphTarget = null;
                    if (!CopycatIsAlive() || InMeeting()) return;

                    float closestShoot = 2f;   // shoot range
                    float closestMorph = 5f;   // morph can target from farther
                    bool wantShoot = learnedAbilities.Contains(Ability.Shoot);
                    bool wantMorph = learnedAbilities.Contains(Ability.Morphling);
                    if (!wantShoot && !wantMorph) return;

                    foreach (var p in PlayerControl.AllPlayerControls) {
                        if (p == null || !IsAlive(p) || p.PlayerId == copycat.PlayerId) continue;
                        float d = Vector2.Distance(copycat.GetTruePosition(), p.GetTruePosition());
                        if (wantShoot && d < closestShoot) { closestShoot = d; currentTarget = p; }
                        if (wantMorph && d < closestMorph) { closestMorph = d; currentMorphTarget = p; }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] HudUpdate failed: {e}");
                }
            }
        }

        // Effects never carry through a meeting.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                if (!active) return;
                shielded = false;
                if (camouflaged) EndCamouflage();
                if (isMorphed) EndMorph();
            }
        }

        // ====================================================================
        // Buttons (one per ability)
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    cachedButtonSprite = __instance.KillButton != null && __instance.KillButton.graphic != null
                        ? __instance.KillButton.graphic.sprite : null;
                    CreateAbilityButton(Ability.Camouflage, __instance,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowLeft);
                    CreateAbilityButton(Ability.Morphling, __instance,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter);
                    CreateAbilityButton(Ability.Shield, __instance,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowRight);
                    CreateAbilityButton(Ability.Shoot, __instance,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowRight);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] Button creation failed: {e}");
                }
            }
        }

        private static void CreateAbilityButton(Ability ability, HudManager __instance, Vector3 pos) {
            var button = new TheOtherRoles.Objects.CustomButton(
                () => OnAbilityClick(ability),
                () => IsAbilityVisible(ability),
                () => IsAbilityAvailable(ability),
                () => { /* nothing on meeting */ },
                GetAbilitySprite(ability),
                pos,
                __instance, KeyCode.None, false, AbilityNames[(int)ability]);
            button.MaxTimer = AbilityCooldown(ability);
            button.Timer = button.MaxTimer; // start on cooldown (elapses before the ability is ever learned)
            abilityButtons[ability] = button;
        }

        private static readonly Dictionary<Ability, Sprite> abilitySpriteCache = new();
        private static Sprite GetAbilitySprite(Ability ability) {
            if (abilitySpriteCache.TryGetValue(ability, out var cached)) return cached;
            string resource = ability switch {
                Ability.Camouflage => "TheOtherRoles.Resources.CamoButton.png",
                Ability.Morphling => "TheOtherRoles.Resources.MorphButton.png",
                Ability.Shield => "TheOtherRoles.Resources.TimeShieldButton.png",
                Ability.Shoot => "TheOtherRoles.Resources.SheriffKillButton.png",
                _ => null
            };
            if (resource != null) {
                var spr = Helpers.loadSpriteFromResources(resource, 115f);
                if (spr != null) { abilitySpriteCache[ability] = spr; return spr; }
            }
            return cachedButtonSprite;
        }

        private static bool IsAbilityVisible(Ability ability) {
            return active && IsLocalCopycat() && CopycatIsAlive() && learnedAbilities.Contains(ability);
        }

        private static bool IsAbilityAvailable(Ability ability) {
            if (!IsAbilityVisible(ability)) return false;
            if (PlayerControl.LocalPlayer == null || !PlayerControl.LocalPlayer.CanMove || InMeeting()) return false;
            return ability switch {
                Ability.Shoot => currentTarget != null,
                Ability.Morphling => currentMorphTarget != null && !isMorphed,
                Ability.Camouflage => !camouflaged,
                Ability.Shield => !shielded,
                _ => true
            };
        }

        private static void OnAbilityClick(Ability ability) {
            if (!IsLocalCopycat() || !IsAbilityAvailable(ability)) return;
            switch (ability) {
                case Ability.Shoot: SendUseAbility(ability, currentTarget.PlayerId); break;
                case Ability.Morphling: SendUseAbility(ability, currentMorphTarget.PlayerId); break;
                default: SendUseAbility(ability); break;
            }
            // Start the cooldown (HasEffect=false buttons don't auto-reset their Timer). Refresh MaxTimer
            // from the source role's current cooldown so it matches the real ability at use time.
            if (abilityButtons.TryGetValue(ability, out var b) && b != null) {
                b.MaxTimer = AbilityCooldown(ability);
                b.Timer = b.MaxTimer;
            }
        }

        // ====================================================================
        // Task management: the Copycat is neutral, so remove its tasks from the crew total.
        // ====================================================================
        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class TaskPatch {
            public static void Postfix(GameData __instance) {
                try {
                    if (!active || copycat == null || copycat.Data == null) return;
                    var (completed, total) = TasksHandler.taskInfo(copycat.Data);
                    __instance.TotalTasks -= total;
                    __instance.CompletedTasks -= completed;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] TaskPatch failed: {e}");
                }
            }
        }

        // ====================================================================
        // Win with the winning team if alive and enough abilities were used. Never blocks a win.
        // ====================================================================
        // VeryLow (not Last): runs after TOR's Normal postfix but BEFORE Bug's Last postfix. If a Bug
        // hijacked the win (it clears the winner list and sets itself alone), Bug must have the final say,
        // so the Copycat's append must not come after it. On a normal team win, Bug's postfix no-ops and
        // this append stands.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.VeryLow)]
        static class OnGameEndPatch {
            // Runs before TOR's OnGameEnd postfix calls resetVariables(): snapshot whether the Copycat
            // has earned a shared win (alive + used enough abilities), since the postfix runs after reset.
            public static void Prefix() {
                winnerCopycatId = byte.MaxValue;
                if (active && CopycatIsAlive() && usedAbilities.Count >= NeededToWin())
                    winnerCopycatId = copycat.PlayerId;
            }

            // Runs AFTER TOR's postfix: append the Copycat to the winners (does not replace them).
            public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ref EndGameResult endGameResult) {
                try {
                    if (winnerCopycatId == byte.MaxValue) return;
                    var p = Helpers.playerById(winnerCopycatId);
                    if (p == null || p.Data == null) return;
                    foreach (var w in EndGameResult.CachedWinners)
                        if (w != null && w.PlayerName == p.Data.PlayerName) return; // already a winner
                    EndGameResult.CachedWinners.Add(new CachedPlayerData(p.Data));
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Copycat] Copycat wins with the winners!");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] OnGameEnd failed: {e}");
                }
            }
        }

        // ====================================================================
        // Role identity: show the Copycat over the Crewmate entry (until nothing else applies).
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || copycat == null || p == null || p != copycat || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = CopycatInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, CopycatInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Copycat] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
