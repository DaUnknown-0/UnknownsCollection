// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Siphoner (Crewmate)
 *
 * A normal TOR Crewmate is silently promoted to "The Siphoner" at game start (host-authoritative pick,
 * broadcast via RPC 196). While the Siphoner stands close to an Impostor, it drains the Impostor's kill
 * power: every tick the nearby Impostor's KILL COOLDOWN is pushed back by a configurable amount.
 *
 * Why host-authoritative? A Crewmate client does NOT know who the Impostors are, so it cannot run the
 * proximity check itself. The HOST (which knows every role + every position) runs the detection and
 * broadcasts a drain pulse via RPC 196; the targeted Impostor's own client applies SetKillTimer to
 * itself (the only place a kill timer can be extended). This is fully host-side and host-verifiable.
 *
 * ARCHITECTURE mirrors Tesla/Saboteur: brand-new role over the real Crewmate role (first UC crew role)
 * - own RoleInfo tag, a small custom RPC (196), no buttons (passive). Gated by the mod-wide No-Start
 * handshake (everyone has the mod).
 *
 * Options live in the 1460-1469 block (+ Drain Duration at 1550 in the overflow range, since the
 * 1460-block is full). See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Siphoner {
        // ---- Theme ----
        public static readonly Color Color = new Color(0.30f, 0.80f, 0.90f); // siphon cyan (crew role tag)

        // ---- Options (IDs 1460-1466) ----
        public static CustomOption SpawnRate;        // 1460 (header) - crew role chance
        public static CustomOption SpawnMinPlayers;  // 1461 - minimum LOBBY players to spawn
        public static CustomOption DrainRange;        // 1462 - proximity range (world units)
        public static CustomOption PenaltyPerTick;    // 1463 - kill-cd seconds added per tick
        public static CustomOption TickInterval;      // 1464 - seconds between drain ticks
        public static CustomOption ScaleWithDistance; // 1465 - closer = stronger drain
        public static CustomOption WarnImpostor;      // 1466 - drained Impostor sees a warning flash
        public static CustomOption AffectSabotage;    // 1467 - also drain sabotage power (hold the sabotage cd)
        public static CustomOption SabotageHold;      // 1468 - seconds the sabotage cooldown is held while draining
        public static CustomOption DrainCooldown;      // 1469 - cooldown after draining ends
        public static CustomOption DrainDuration;      // 1550 - seconds the drain stays active before it auto-ends
                                                       // (1460-block full, so this lives in the UC overflow range)

        // ---- Runtime state ----
        public static PlayerControl siphoner;
        public static bool active;
        private static float lastDrainTime;
        private static bool drainActive;
        private static TheOtherRoles.Objects.CustomButton drainButton;

        // ---- Custom RPC (196) subtypes ----
        private const byte RpcId = 196; // == UnknownsCollectionPlugin.SiphonerRpcId
        private const byte SubSetSiphoner = 0;  // siphonerId
        private const byte SubDrain = 1;        // impostorId, penalty(float)
        private const byte SubSabotageHold = 2; // seconds(float) - block sabotage until Time.time+seconds
        private const byte SubToggleDrain = 3;  // active(bool) - toggle drain on/off

        // Cross-plugin sabotage-block channel (AppDomain shared data, no hard dependency). Useful TOR
        // Stuff's Sabotage Tuning reads this same key and honours the block instead of fighting our
        // shared-timer write (mirrors how Sabotage Tuning suppresses the Chance modifier). Holds the
        // absolute Time.time until which sabotage is suppressed on THIS client.
        public const string SabBlockKey = "TORMods.SiphonerSabotageBlockUntil";

        // ---- Role identity ----
        private static RoleInfo siphonerInfo;
        public static RoleInfo SiphonerInfo() => siphonerInfo ??= new RoleInfo(
            "Siphoner", Color, "Drain the Impostor's kill power by standing near them",
            "Drain the Impostor's kill power by standing near them", RoleId.Crewmate);

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1460, Types.Crewmate, "Siphoner",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1461, Types.Crewmate, "Siphoner Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                DrainRange = CustomOption.Create(1462, Types.Crewmate, "Siphoner Drain Range",
                    2f, 0.5f, 5f, 0.25f, SpawnRate);
                PenaltyPerTick = CustomOption.Create(1463, Types.Crewmate, "Siphoner Kill-Cooldown Added Per Tick",
                    3f, 0.5f, 15f, 0.5f, SpawnRate);
                TickInterval = CustomOption.Create(1464, Types.Crewmate, "Siphoner Drain Tick Interval",
                    2f, 0.5f, 10f, 0.5f, SpawnRate);
                ScaleWithDistance = CustomOption.Create(1465, Types.Crewmate, "Siphoner Drain Stronger When Closer",
                    true, SpawnRate);
                WarnImpostor = CustomOption.Create(1466, Types.Crewmate, "Drained Impostor Sees A Warning",
                    true, SpawnRate);
                AffectSabotage = CustomOption.Create(1467, Types.Crewmate, "Siphoner Also Drains Sabotage Power",
                    true, SpawnRate);
                SabotageHold = CustomOption.Create(1468, Types.Crewmate, "Sabotage Blocked While Draining (sec)",
                    8f, 1f, 30f, 1f, AffectSabotage);
                DrainDuration = CustomOption.Create(1550, Types.Crewmate, "Siphoner Drain Duration",
                    10f, 3f, 30f, 1f, SpawnRate);
                DrainCooldown = CustomOption.Create(1469, Types.Crewmate, "Siphoner Drain Cooldown",
                    20f, 5f, 60f, 2.5f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Siphoner] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { /* all patches are attribute-based */ }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalSiphoner() =>
            siphoner != null && PlayerControl.LocalPlayer != null && siphoner.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        private static float Range() => DrainRange != null ? DrainRange.getFloat() : 2f;
        private static float Penalty() => PenaltyPerTick != null ? PenaltyPerTick.getFloat() : 3f;
        private static float Interval() => TickInterval != null ? TickInterval.getFloat() : 2f;

        // The map's shared sabotage cooldown system (vanilla gates EVERY sabotage on this one Timer; the
        // host validates incoming sabotages against it). Same lever Useful TOR Stuff's SabotageTuning uses.
        private static SabotageSystemType GetSab() {
            var ship = MapUtilities.CachedShipStatus;
            if (ship == null || ship.Systems == null) return null;
            if (!ship.Systems.TryGetValue(SystemTypes.Sabotage, out ISystemType sys) || sys == null) return null;
            return sys.TryCast<SabotageSystemType>();
        }

        // ====================================================================
        // Custom RPC senders (each applies locally too)
        // ====================================================================
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetSiphoner(byte id) {
            try {
                var w = BeginRpc(SubSetSiphoner);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetSiphoner(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] SendSetSiphoner failed: {e}"); }
        }

        private static void SendDrain(byte impostorId, float penalty) {
            try {
                var w = BeginRpc(SubDrain);
                w.Write(impostorId);
                w.Write(penalty);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyDrain(impostorId, penalty);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] SendDrain failed: {e}"); }
        }

        // Tell every client to suppress sabotage for the next `seconds` (honoured by Sabotage Tuning if
        // present; the host shared-timer write below is the gate when Sabotage Tuning is off).
        private static void SendSabotageHold(float seconds) {
            try {
                var w = BeginRpc(SubSabotageHold);
                w.Write(seconds);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySabotageHold(seconds);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] SendSabotageHold failed: {e}"); }
        }

        private static void ApplySabotageHold(float seconds) {
            try { AppDomain.CurrentDomain.SetData(SabBlockKey, Time.time + seconds); } catch { }
        }

        private static void SendToggleDrain(bool on) {
            try {
                var w = BeginRpc(SubToggleDrain);
                w.Write(on);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyToggleDrain(on);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] SendToggleDrain failed: {e}"); }
        }

        private static void ApplyToggleDrain(bool on) {
            drainActive = on;
            if (!on) lastDrainTime = 0f;
            // Suction cue only for the Siphoner itself - audible for others it would leak its position.
            if (on && IsLocalSiphoner()) UCAssets.PlaySiphonerDrain();
        }

        // ---- Appliers (run on every client) ----
        private static void ApplySetSiphoner(byte id) {
            siphoner = Helpers.playerById(id);
            active = siphoner != null;
            if (active) UCPromotion.Claim(id);
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Siphoner] The Siphoner is {siphoner.Data?.PlayerName}.");
        }

        // The targeted Impostor's OWN kill timer is pushed back by the penalty; everyone else ignores it.
        // A vampire bite sound plays to audibly warn the drained Impostor (replaces the old blue flash).
        private static void ApplyDrain(byte impostorId, float penalty) {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.PlayerId != impostorId) return;
            try {
                // Add the penalty to the impostor's CURRENT kill cooldown (not the base/maximum): each drain
                // tick pushes the remaining timer up by `penalty`, gradually starving the kill. TOR's
                // SetKillTimer prefix clamps the result to the configured KillCooldown, so it can never
                // exceed the maximum — but it also never snaps straight to it the way the old hold did.
                me.SetKillTimer(me.killTimer + penalty);
                if (WarnImpostor == null || WarnImpostor.getBool())
                    SoundEffectsManager.play("vampireBite");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Siphoner] drain apply failed: {e.Message}");
            }
        }

        public static void MarkFromDraft(byte playerId) => ApplySetSiphoner(playerId);

        // ====================================================================
        // RPC receiver
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubSetSiphoner: ApplySetSiphoner(reader.ReadByte()); break;
                        case SubDrain: {
                            byte impostorId = reader.ReadByte();
                            float penalty = reader.ReadSingle();
                            ApplyDrain(impostorId, penalty);
                            break;
                        }
                        case SubSabotageHold: ApplySabotageHold(reader.ReadSingle()); break;
                        case SubToggleDrain: ApplyToggleDrain(reader.ReadBoolean()); break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        // ====================================================================
        // Round reset
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                siphoner = null;
                active = false;
                lastDrainTime = 0f;
                drainActive = false;
                // drainButton deliberately kept (resetVariables runs after HudManager.Start).
            }
        }

        // ====================================================================
        // Game start: host picks the Siphoner among plain Crewmates and broadcasts it.
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
                    SendSetSiphoner(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Button: press to drain for a fixed duration, then it auto-ends and goes on cooldown.
        // Uses TOR's CustomButton effect mode (HasEffect/EffectDuration/OnEffectEnds): the click
        // starts the drain, the green effect timer counts the active window down, and when it
        // hits zero OnEffectEnds stops the drain and arms the cooldown — no manual toggle-off.
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    // Own icon (a kill-cooldown clock being drained); TOR sprites only as fallback.
                    Sprite sprite = UCAssets.SiphonerIcon;
                    if (sprite == null)
                        try { sprite = Helpers.loadSpriteFromResources("TheOtherRoles.Resources.VampireButton.png", 115f); } catch { }
                    if (sprite == null && __instance.KillButton != null && __instance.KillButton.graphic != null)
                        sprite = __instance.KillButton.graphic.sprite;

                    float duration = DrainDuration != null ? DrainDuration.getFloat() : 10f;
                    float cd = DrainCooldown != null ? DrainCooldown.getFloat() : 20f;
                    drainButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            // OnClick: start the drain for the effect window.
                            if (!active || !IsLocalSiphoner()) return;
                            SendToggleDrain(true);
                        },
                        () => active && IsLocalSiphoner()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove,
                        () => {
                            // OnMeetingEnds: make sure the drain is off and reset to a full cooldown.
                            if (drainActive) SendToggleDrain(false);
                            drainButton.isEffectActive = false;
                            drainButton.Timer = drainButton.MaxTimer;
                        },
                        sprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter,
                        __instance, KeyCode.F,
                        true,        // HasEffect
                        duration,    // EffectDuration = active drain window
                        () => {
                            // OnEffectEnds: stop the drain and start the cooldown.
                            if (drainActive) SendToggleDrain(false);
                            drainButton.Timer = DrainCooldown != null ? DrainCooldown.getFloat() : 20f;
                        },
                        false, "DRAIN");
                    drainButton.EffectDuration = duration;
                    drainButton.MaxTimer = cd;
                    drainButton.Timer = 5f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] Button creation failed: {e}");
                }
            }
        }

        // ====================================================================
        // Host-authoritative proximity drain (runs only on the host, throttled by the tick interval).
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || InMeeting() || !IsAlive(siphoner) || !drainActive) return;
                    if (Time.time - lastDrainTime < Interval()) return;
                    lastDrainTime = Time.time;

                    Vector2 here = siphoner.GetTruePosition();
                    float range = Range();
                    bool anyInRange = false;
                    foreach (var p in PlayerControl.AllPlayerControls) {
                        if (!IsAlive(p) || p.Data.Role == null || !p.Data.Role.IsImpostor) continue;
                        float dist = Vector2.Distance(here, p.GetTruePosition());
                        if (dist > range) continue;
                        anyInRange = true;
                        float penalty = Penalty();
                        if (ScaleWithDistance == null || ScaleWithDistance.getBool())
                            penalty *= Mathf.Clamp(2f - dist / Mathf.Max(range, 0.01f), 1f, 2f);
                        SendDrain(p.PlayerId, penalty);
                    }

                    // Also drain sabotage power: hold the shared sabotage cooldown up while an Impostor is
                    // near. The host validates sabotages against sab.Timer and serialises it to everyone,
                    // so this blocks the sabotage menu for all impostors while the Siphoner is draining.
                    if (anyInRange && (AffectSabotage == null || AffectSabotage.getBool())) {
                        float hold = SabotageHold != null ? SabotageHold.getFloat() : 8f;
                        // Vanilla path (Sabotage Tuning off): host-authoritative shared-timer write.
                        var sab = GetSab();
                        if (sab != null && sab.Timer < hold) sab.Timer = hold;
                        // Integration path: broadcast the block so Sabotage Tuning (if present) honours it
                        // on every client instead of pinning the shared timer back to idle.
                        SendSabotageHold(hold);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] drain tick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Role identity: show the Siphoner as its own role over the Crewmate entry.
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || siphoner == null || p == null || p != siphoner || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = SiphonerInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, SiphonerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Siphoner] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
