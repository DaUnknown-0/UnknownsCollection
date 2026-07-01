// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Poltergeist (ghost role - keeps its original team)
 *
 * NOT a start role: the FIRST player to die (kills always; exile too if the option allows) is promoted
 * to the Poltergeist, host-authoritatively, and keeps its original team and win condition - a dead
 * Crewmate haunts for the crew, a dead Impostor for the impostors.
 *
 * Abilities run on a shared ENERGY pool (max + regen/s configurable; no regen during meetings):
 *   - Door Haunt (cheap):    slams the nearest single door shut for X seconds. Works on Skeld's auto
 *                            doors (timer override), Polus/Airship plain doors and Fungle's mushroom
 *                            doors; Mira HQ has no doors, so the button simply never finds a target.
 *   - Hex (medium):          curses the nearest living player: Speed boost / Blind / Night vision
 *                            (modes individually allowed by options, cycled with the button hotkey
 *                            logic - see HexMode).
 *   - Ghost Hand (channel):  counts as ONE hand on a Reactor/Seismic console while channeling,
 *                            draining energy per second. A living player must still take the other
 *                            console - the ghost weakens, but never solo-fixes, the impostor stall.
 *   - Manifest:              see PoltergeistManifest.cs (appear as a copy of a living player).
 *
 * Everything is gated on "everyone has the mod" (TeslaVersionHandshake) like the Tesla, because the
 * effects are client-side on every client. Options 1560-1579, custom RPC 208. See ID-Registry.md.
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
using AmongUs.GameOptions;

namespace UnknownsCollection {
    public static class Poltergeist {
        // ---- Theme ----
        public static readonly Color Color = new Color(0.62f, 0.42f, 1f); // spectral violet

        // ---- Options (IDs 1560-1579) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption ExileCounts;
        public static CustomOption EnergyMax;
        public static CustomOption EnergyRegen;
        public static CustomOption EnergyStartPercent;
        public static CustomOption ManifestCost;
        public static CustomOption ManifestDuration;
        public static CustomOption ManifestCanVent;
        public static CustomOption ManifestKillRefund; // None / Half / Full
        public static CustomOption DoorCost;
        public static CustomOption DoorDuration;
        public static CustomOption HandDrainPerSecond;
        public static CustomOption HexCost;
        public static CustomOption HexDuration;
        public static CustomOption HexSpeedAllowed;
        public static CustomOption HexBlindAllowed;
        public static CustomOption HexNightVisionAllowed;
        public static CustomOption KeepsTasks;

        // ---- Runtime state (synced) ----
        public static PlayerControl poltergeist;
        public static bool active;
        // Hexes: playerId -> (mode, endTime). Applied on every client; each client only *acts* on what
        // concerns it (own speed / own light), so purely visual clients still track state for FX.
        public static readonly Dictionary<byte, (int mode, float end)> hexes = new();
        public static bool handChanneling;

        // ---- Runtime state (host only) ----
        private static bool armed;             // host rolled the spawn chance at intro
        private static bool promoted;          // first death already consumed

        // ---- Runtime state (local Poltergeist client) ----
        public static float energy;
        private static float speedHexBase;     // original speed while a speed hex is on the LOCAL player
        private static bool speedHexApplied;

        // Doors we slammed: doorId -> reopen time (every client runs the same timers from the RPC).
        private static readonly Dictionary<int, float> hauntedDoors = new();

        public const int HexSpeed = 0;
        public const int HexBlind = 1;
        public const int HexNightVision = 2;
        private static int hexMode = HexSpeed;

        // ---- Custom RPC (208) subtypes ----
        private const byte RpcId = UnknownsCollectionPlugin.PoltergeistRpcId;
        private const byte SubSetPoltergeist = 0;  // playerId
        private const byte SubDoorHaunt = 1;       // doorId(int), duration(float)
        private const byte SubHex = 2;             // targetId, mode, duration(float)
        private const byte SubHandStart = 3;       // (none)
        private const byte SubHandStop = 4;        // (none)
        // 5-7 reserved for the Manifest ability (PoltergeistManifest.cs)

        // ---- Role identity ----
        private static RoleInfo poltergeistInfo;
        public static RoleInfo PoltergeistInfo() => poltergeistInfo ??= new RoleInfo(
            "Poltergeist", Color, "Haunt the living from beyond - for your team",
            "Haunt the living from beyond", RoleId.Crewmate);

        private static TheOtherRoles.Objects.CustomButton doorButton;
        private static TheOtherRoles.Objects.CustomButton hexButton;
        private static TheOtherRoles.Objects.CustomButton handButton;

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1560, Types.Neutral, "Poltergeist",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1561, Types.Neutral, "Poltergeist Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                ExileCounts = CustomOption.Create(1562, Types.Neutral, "Exile Counts As First Death",
                    false, SpawnRate);
                EnergyMax = CustomOption.Create(1563, Types.Neutral, "Poltergeist Energy Maximum",
                    100f, 50f, 200f, 10f, SpawnRate);
                EnergyRegen = CustomOption.Create(1564, Types.Neutral, "Energy Regeneration Per Second",
                    3f, 1f, 10f, 0.5f, SpawnRate);
                EnergyStartPercent = CustomOption.Create(1565, Types.Neutral, "Starting Energy (%)",
                    50f, 0f, 100f, 10f, SpawnRate);
                ManifestCost = CustomOption.Create(1566, Types.Neutral, "Manifest Energy Cost",
                    60f, 20f, 150f, 5f, SpawnRate);
                ManifestDuration = CustomOption.Create(1567, Types.Neutral, "Manifest Duration",
                    12f, 5f, 30f, 1f, SpawnRate);
                ManifestCanVent = CustomOption.Create(1568, Types.Neutral, "Manifested Poltergeist Can Vent",
                    true, SpawnRate);
                ManifestKillRefund = CustomOption.Create(1569, Types.Neutral, "Killing A Manifest Refunds Kill Cooldown",
                    new string[] { "No Refund", "Half Refund", "Full Refund" }, SpawnRate);
                DoorCost = CustomOption.Create(1570, Types.Neutral, "Door Haunt Energy Cost",
                    20f, 5f, 60f, 5f, SpawnRate);
                DoorDuration = CustomOption.Create(1571, Types.Neutral, "Door Haunt Duration",
                    8f, 3f, 20f, 1f, SpawnRate);
                HandDrainPerSecond = CustomOption.Create(1572, Types.Neutral, "Ghost Hand Energy Drain Per Second",
                    5f, 1f, 15f, 1f, SpawnRate);
                HexCost = CustomOption.Create(1573, Types.Neutral, "Hex Energy Cost",
                    35f, 10f, 80f, 5f, SpawnRate);
                HexDuration = CustomOption.Create(1574, Types.Neutral, "Hex Duration",
                    10f, 5f, 30f, 1f, SpawnRate);
                HexSpeedAllowed = CustomOption.Create(1575, Types.Neutral, "Hex: Speed Boost Allowed",
                    true, SpawnRate);
                HexBlindAllowed = CustomOption.Create(1576, Types.Neutral, "Hex: Blindness Allowed",
                    true, SpawnRate);
                HexNightVisionAllowed = CustomOption.Create(1577, Types.Neutral, "Hex: Night Vision Allowed",
                    true, SpawnRate);
                KeepsTasks = CustomOption.Create(1578, Types.Neutral, "Poltergeist Keeps Its Tasks",
                    false, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Poltergeist] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { }

        // ---- Shared helpers ----

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalPoltergeist() =>
            active && poltergeist != null && PlayerControl.LocalPlayer != null
            && poltergeist.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        public static bool IsImpostorTeam() =>
            poltergeist != null && poltergeist.Data != null && poltergeist.Data.Role != null
            && poltergeist.Data.Role.IsImpostor;

        // The ghost buttons only exist while the Poltergeist is a plain dead ghost in the play phase.
        private static bool GhostButtonsUsable() =>
            IsLocalPoltergeist()
            && PlayerControl.LocalPlayer.Data != null && PlayerControl.LocalPlayer.Data.IsDead
            && MeetingHud.Instance == null && ExileController.Instance == null
            && !PoltergeistManifest.IsManifested;

        // ---- RPC plumbing ----

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetPoltergeist(byte id) {
            try {
                var w = BeginRpc(SubSetPoltergeist);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetPoltergeist(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] SendSet failed: {e}"); }
        }

        private static void SendDoorHaunt(int doorId, float duration) {
            try {
                var w = BeginRpc(SubDoorHaunt);
                w.Write(doorId);
                w.Write(duration);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyDoorHaunt(doorId, duration);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] SendDoor failed: {e}"); }
        }

        private static void SendHex(byte targetId, byte mode, float duration) {
            try {
                var w = BeginRpc(SubHex);
                w.Write(targetId);
                w.Write(mode);
                w.Write(duration);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyHex(targetId, mode, duration);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] SendHex failed: {e}"); }
        }

        private static void SendHandStart() {
            try {
                var w = BeginRpc(SubHandStart);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyHandStart();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] SendHandStart failed: {e}"); }
        }

        private static void SendHandStop() {
            try {
                var w = BeginRpc(SubHandStop);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyHandStop();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] SendHandStop failed: {e}"); }
        }

        // ---- RPC application (runs on every client) ----

        private static void ApplySetPoltergeist(byte id) {
            poltergeist = Helpers.playerById(id);
            active = poltergeist != null;
            if (!active) return;
            UCPromotion.Claim(id);
            energy = (EnergyMax?.getFloat() ?? 100f) * (EnergyStartPercent?.getFloat() ?? 50f) / 100f;
            UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[Poltergeist] {poltergeist.Data?.PlayerName} rose as the Poltergeist " +
                $"({(IsImpostorTeam() ? "Impostor" : "Crew")} team).");
            if (IsLocalPoltergeist()) {
                Helpers.showFlash(Color, 2.5f, "You rose as the Poltergeist! Haunt the living for your team.");
                UCAssets.PlayManifest(0.7f);
            }
        }

        private static void ApplyDoorHaunt(int doorId, float duration) {
            try {
                var ship = ShipStatus.Instance;
                if (ship == null || ship.AllDoors == null) return;
                OpenableDoor door = null;
                foreach (var d in ship.AllDoors) {
                    if (d != null && d.Id == doorId) { door = d; break; }
                }
                if (door == null) return;

                door.SetDoorway(false);
                var auto = door.TryCast<AutoOpenDoor>();
                if (auto != null) {
                    // Skeld auto doors reopen themselves via ClosedTimer - just stretch it to our duration.
                    auto.ClosedTimer = duration;
                } else {
                    // Plain / mushroom doors stay closed until opened: schedule our own reopen. Players
                    // can still open them earlier through the vanilla door console - organic counterplay.
                    hauntedDoors[doorId] = Time.time + duration;
                }

                Vector2 at = door.transform.position;
                PoltergeistFx.SpawnDoorBurst(at);
                UCAssets.PlayDoorSlam(at);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] ApplyDoorHaunt failed: {e}");
            }
        }

        private static void ApplyHex(byte targetId, byte mode, float duration) {
            var target = Helpers.playerById(targetId);
            if (target == null) return;
            hexes[targetId] = (mode, Time.time + duration);
            PoltergeistFx.SpawnHexBurst(target);
            UCAssets.PlayHex();

            // Speed is client-authoritative: only the hexed player's own client changes its physics.
            if (PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.PlayerId == targetId
                && mode == HexSpeed && !speedHexApplied) {
                speedHexBase = PlayerControl.LocalPlayer.MyPhysics.Speed;
                PlayerControl.LocalPlayer.MyPhysics.Speed = speedHexBase * 1.4f;
                speedHexApplied = true;
            }
        }

        private static void ApplyHandStart() {
            handChanneling = true;
            UCAssets.PlayGhostHand();
        }

        private static void ApplyHandStop() {
            handChanneling = false;
            PoltergeistFx.SetChannel(Vector2.zero, false);
        }

        // ---- ability triggers (local Poltergeist client) ----

        private static OpenableDoor NearestHauntableDoor(float maxDist) {
            var ship = ShipStatus.Instance;
            if (ship == null || ship.AllDoors == null || PlayerControl.LocalPlayer == null) return null;
            Vector2 pos = PlayerControl.LocalPlayer.GetTruePosition();
            OpenableDoor best = null;
            float bestD = maxDist;
            foreach (var d in ship.AllDoors) {
                if (d == null || !d.IsOpen) continue;
                float dist = Vector2.Distance(pos, d.transform.position);
                if (dist < bestD) { bestD = dist; best = d; }
            }
            return best;
        }

        private static PlayerControl NearestLivingPlayer(float maxDist) {
            if (PlayerControl.LocalPlayer == null) return null;
            Vector2 pos = PlayerControl.LocalPlayer.GetTruePosition();
            PlayerControl best = null;
            float bestD = maxDist;
            foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                if (!IsAlive(p) || p.PlayerId == PlayerControl.LocalPlayer.PlayerId) continue;
                float dist = Vector2.Distance(pos, p.GetTruePosition());
                if (dist < bestD) { bestD = dist; best = p; }
            }
            return best;
        }

        private static readonly List<int> allowedHexModes = new();
        private static void RefreshAllowedHexModes() {
            allowedHexModes.Clear();
            if (HexSpeedAllowed?.getBool() ?? true) allowedHexModes.Add(HexSpeed);
            if (HexBlindAllowed?.getBool() ?? true) allowedHexModes.Add(HexBlind);
            if (HexNightVisionAllowed?.getBool() ?? true) allowedHexModes.Add(HexNightVision);
        }
        private static string HexModeName(int mode) => mode switch {
            HexSpeed => "SPEED", HexBlind => "BLIND", _ => "SIGHT"
        };

        // Reactor-style critical system that is currently active and holds user consoles, or 0.
        private static SystemTypes ActiveReactorSystem() {
            try {
                var ship = ShipStatus.Instance;
                if (ship == null) return 0;
                foreach (var sys in new[] { SystemTypes.Reactor, SystemTypes.Laboratory }) {
                    if (!ship.Systems.ContainsKey(sys)) continue;
                    var reactor = ship.Systems[sys].TryCast<ReactorSystemType>();
                    if (reactor != null && reactor.IsActive) return sys;
                }
            } catch { }
            return 0;
        }

        private static void StartGhostHand() {
            var sys = ActiveReactorSystem();
            if (sys == 0 || ShipStatus.Instance == null) return;
            var reactor = ShipStatus.Instance.Systems[sys].TryCast<ReactorSystemType>();
            if (reactor == null) return;
            // The ghost always takes console 0 (vanilla shows the held hand in the minigame, so the
            // crew naturally covers the other pad). Enumerating UserConsolePairs through interop to
            // pick the free console is deliberately avoided until verified in-game.
            byte amount = (byte)(ReactorSystemType.AddUserOp | 0);
            ShipStatus.Instance.RpcUpdateSystem(sys, amount);
            SendHandStart();
        }

        private static void StopGhostHand(bool releaseConsole) {
            if (releaseConsole) {
                var sys = ActiveReactorSystem();
                if (sys != 0 && ShipStatus.Instance != null) {
                    var reactor = ShipStatus.Instance.Systems[sys].TryCast<ReactorSystemType>();
                    if (reactor != null)
                        ShipStatus.Instance.RpcUpdateSystem(sys, (byte)(ReactorSystemType.RemoveUserOp | 0));
                }
            }
            SendHandStop();
        }

        // ---- Reset / lifecycle ----

        private static void ResetAll() {
            poltergeist = null;
            active = false;
            armed = false;
            promoted = false;
            energy = 0;
            hexes.Clear();
            hauntedDoors.Clear();
            handChanneling = false;
            speedHexApplied = false;
            hexMode = HexSpeed;
            doorButton = null;
            hexButton = null;
            handButton = null;
            PoltergeistFx.Clear();
            PoltergeistManifest.Reset();
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { ResetAll(); }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubSetPoltergeist: ApplySetPoltergeist(reader.ReadByte()); break;
                        case SubDoorHaunt: {
                            int doorId = reader.ReadInt32();
                            float dur = reader.ReadSingle();
                            ApplyDoorHaunt(doorId, dur);
                            break;
                        }
                        case SubHex: {
                            byte target = reader.ReadByte();
                            byte mode = reader.ReadByte();
                            float dur = reader.ReadSingle();
                            ApplyHex(target, mode, dur);
                            break;
                        }
                        case SubHandStart: ApplyHandStart(); break;
                        case SubHandStop: ApplyHandStop(); break;
                        default: PoltergeistManifest.HandleRpc(subtype, reader); break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        // Host: roll the spawn chance once at intro end. No player is picked yet - the first death is.
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;
                    armed = true;
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Poltergeist] Armed - the first death will rise.");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] IntroEnd arm failed: {e}");
                }
            }
        }

        // ---- First-death promotion (host). Same hooks as the Follower - the two roles COMPOSE:
        // the Follower (a living player) takes over the dead player's role, while the dead player
        // itself rises as the Poltergeist. ----

        private static void HandleDeath(PlayerControl target, bool byExile) {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            if (!armed || promoted || target == null || target.Data == null) return;
            if (byExile && !(ExileCounts?.getBool() ?? false)) return; // exiles don't consume the trigger
            promoted = true;
            UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[Poltergeist] First death: {target.Data?.PlayerName} ({(byExile ? "exile" : "kill")}).");
            SendSetPoltergeist(target.PlayerId);
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try { HandleDeath(target, byExile: false); } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] murder hook failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(ExileController), nameof(ExileController.WrapUp))]
        static class ExileWrapUpPatch {
            public static void Postfix(ExileController __instance) {
                try {
                    var networkedPlayer = __instance?.initData.networkedPlayer;
                    HandleDeath(networkedPlayer != null ? networkedPlayer.Object : null, byExile: true);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] exile hook failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(AirshipExileController), nameof(AirshipExileController.WrapUpAndSpawn))]
        static class AirshipExileWrapUpPatch {
            public static void Postfix(AirshipExileController __instance) {
                try {
                    var networkedPlayer = __instance?.initData.networkedPlayer;
                    HandleDeath(networkedPlayer != null ? networkedPlayer.Object : null, byExile: true);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] exile hook failed: {e}");
                }
            }
        }

        // ---- Buttons ----

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    doorButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            var door = NearestHauntableDoor(1.6f);
                            if (door == null) return;
                            float cost = DoorCost?.getFloat() ?? 20f;
                            if (energy < cost) return;
                            energy -= cost;
                            SendDoorHaunt(door.Id, DoorDuration?.getFloat() ?? 8f);
                        },
                        () => GhostButtonsUsable(),
                        () => energy >= (DoorCost?.getFloat() ?? 20f) && NearestHauntableDoor(1.6f) != null,
                        () => { },
                        UCAssets.DoorIcon,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowLeft,
                        __instance, KeyCode.F, false, "DOOR");
                    doorButton.MaxTimer = 1f; doorButton.Timer = 0f;

                    hexButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            RefreshAllowedHexModes();
                            if (allowedHexModes.Count == 0) return;
                            if (!allowedHexModes.Contains(hexMode)) hexMode = allowedHexModes[0];
                            var target = NearestLivingPlayer(3f);
                            if (target == null) return;
                            float cost = HexCost?.getFloat() ?? 35f;
                            if (energy < cost) return;
                            energy -= cost;
                            SendHex(target.PlayerId, (byte)hexMode, HexDuration?.getFloat() ?? 10f);
                        },
                        () => { RefreshAllowedHexModes(); return GhostButtonsUsable() && allowedHexModes.Count > 0; },
                        () => energy >= (HexCost?.getFloat() ?? 35f) && NearestLivingPlayer(3f) != null,
                        () => { },
                        UCAssets.HexIcon,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter,
                        __instance, KeyCode.G, false, "HEX");
                    hexButton.MaxTimer = 1f; hexButton.Timer = 0f;

                    handButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            if (handChanneling) { StopGhostHand(releaseConsole: true); return; }
                            if (ActiveReactorSystem() == 0) return;
                            if (energy < (HandDrainPerSecond?.getFloat() ?? 5f)) return;
                            StartGhostHand();
                        },
                        () => GhostButtonsUsable() || (IsLocalPoltergeist() && handChanneling),
                        () => handChanneling
                              || (ActiveReactorSystem() != 0 && energy >= (HandDrainPerSecond?.getFloat() ?? 5f)),
                        () => { },
                        UCAssets.HandIcon,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowRight,
                        __instance, KeyCode.H, false, "HAND");
                    handButton.MaxTimer = 1f; handButton.Timer = 0f;

                    PoltergeistManifest.CreateButton(__instance);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] Button creation failed: {e}");
                }
            }
        }

        // ---- Per-frame driver ----

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    PoltergeistFx.Tick();
                    if (!active || poltergeist == null) return;

                    float now = Time.time;
                    bool inMeeting = MeetingHud.Instance != null || ExileController.Instance != null;

                    // Energy regeneration (local Poltergeist only; paused in meetings).
                    if (IsLocalPoltergeist() && !inMeeting) {
                        float max = EnergyMax?.getFloat() ?? 100f;
                        energy = Mathf.Min(max, energy + (EnergyRegen?.getFloat() ?? 3f) * Time.deltaTime);
                    }

                    // Hex expiry + local speed restore.
                    if (hexes.Count > 0) {
                        var expired = hexes.Where(kv => now >= kv.Value.end).Select(kv => kv.Key).ToList();
                        foreach (var id in expired) hexes.Remove(id);
                    }
                    if (speedHexApplied) {
                        bool stillHexed = PlayerControl.LocalPlayer != null
                            && hexes.TryGetValue(PlayerControl.LocalPlayer.PlayerId, out var h)
                            && h.mode == HexSpeed;
                        if (!stillHexed || inMeeting) {
                            if (PlayerControl.LocalPlayer != null && speedHexBase > 0)
                                PlayerControl.LocalPlayer.MyPhysics.Speed = speedHexBase;
                            speedHexApplied = false;
                        }
                    }

                    // Haunted-door reopen (plain/mushroom doors; auto doors reopen themselves).
                    if (hauntedDoors.Count > 0 && ShipStatus.Instance != null && ShipStatus.Instance.AllDoors != null) {
                        var due = hauntedDoors.Where(kv => now >= kv.Value).Select(kv => kv.Key).ToList();
                        foreach (var id in due) {
                            hauntedDoors.Remove(id);
                            foreach (var d in ShipStatus.Instance.AllDoors) {
                                if (d == null || d.Id != id || d.IsOpen) continue;
                                // If a vanilla door sabotage took the same room over meanwhile, let it own the door.
                                if (DoorSabotageTimerActive(d.Room)) continue;
                                d.SetDoorway(true);
                            }
                        }
                    }

                    // Ghost Hand channel: drain + keep FX on the ghost, stop when spent or fixed.
                    if (handChanneling) {
                        PoltergeistFx.SetChannel(poltergeist.GetTruePosition(), true);
                        if (IsLocalPoltergeist()) {
                            energy -= (HandDrainPerSecond?.getFloat() ?? 5f) * Time.deltaTime;
                            bool sabotageGone = ActiveReactorSystem() == 0;
                            if (energy <= 0 || sabotageGone || inMeeting || !GhostButtonsUsableForHand()) {
                                energy = Mathf.Max(0, energy);
                                StopGhostHand(releaseConsole: !sabotageGone);
                            }
                        } else if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost
                                   && (poltergeist.Data == null || poltergeist.Data.Disconnected)) {
                            // Host fallback: the Poltergeist disconnected mid-channel.
                            ApplyHandStop();
                        }
                    }

                    // Dynamic button labels: current energy on every ghost button.
                    if (IsLocalPoltergeist()) {
                        int e = Mathf.FloorToInt(energy);
                        if (doorButton != null) doorButton.buttonText = $"DOOR {e}";
                        if (hexButton != null) hexButton.buttonText = $"HEX:{HexModeName(hexMode)} {e}";
                        if (handButton != null) handButton.buttonText = handChanneling ? $"HOLDING {e}" : $"HAND {e}";

                        // Cycle the hex mode with the J key (shown in the label).
                        if (Input.GetKeyDown(KeyCode.J)) {
                            RefreshAllowedHexModes();
                            if (allowedHexModes.Count > 0) {
                                int idx = allowedHexModes.IndexOf(hexMode);
                                hexMode = allowedHexModes[(idx + 1) % allowedHexModes.Count];
                            }
                        }
                    }

                    PoltergeistManifest.Tick();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] HudUpdate failed: {e}");
                }
            }

            private static bool GhostButtonsUsableForHand() =>
                IsLocalPoltergeist() && PlayerControl.LocalPlayer.Data != null
                && PlayerControl.LocalPlayer.Data.IsDead && !PoltergeistManifest.IsManifested;

            private static bool DoorSabotageTimerActive(SystemTypes room) {
                try {
                    var ship = ShipStatus.Instance;
                    if (ship == null || !ship.Systems.ContainsKey(SystemTypes.Doors)) return false;
                    var doors = ship.Systems[SystemTypes.Doors].TryCast<DoorsSystemType>();
                    return doors != null && doors.GetTimer(room) > 0f;
                } catch { return false; }
            }
        }

        // Meetings end all transient haunting: doors spring open (vanilla resets doors anyway),
        // hexes fade, the ghost hand lets go.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    hexes.Clear();
                    hauntedDoors.Clear();
                    if (handChanneling && IsLocalPoltergeist()) StopGhostHand(releaseConsole: false);
                    else handChanneling = false;
                    PoltergeistFx.SetChannel(Vector2.zero, false);
                    PoltergeistManifest.OnMeeting();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] meeting reset failed: {e}");
                }
            }
        }

        // ---- Hex vision effects (each client's own light only) ----

        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.CalculateLightRadius))]
        static class LightPatch {
            public static void Postfix(ref float __result, ShipStatus __instance, [HarmonyArgument(0)] NetworkedPlayerInfo p) {
                try {
                    if (!active || p == null || hexes.Count == 0) return;
                    if (!hexes.TryGetValue(p.PlayerId, out var hex)) return;
                    if (hex.mode == HexBlind)
                        __result *= 0.35f;
                    else if (hex.mode == HexNightVision)
                        __result = __instance.MaxLightRadius * GameOptionsManager.Instance.currentNormalGameOptions.CrewLightMod;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] LightPatch failed: {e}");
                }
            }
        }

        // ---- Task-win accounting: without KeepsTasks, a crew Poltergeist's tasks leave the pool ----

        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class TaskPatch {
            public static void Postfix(GameData __instance) {
                try {
                    if (!active || poltergeist == null || poltergeist.Data == null) return;
                    if (KeepsTasks?.getBool() ?? false) return;
                    if (IsImpostorTeam()) return; // impostors have no real tasks anyway
                    var (completed, total) = TasksHandler.taskInfo(poltergeist.Data);
                    __instance.TotalTasks -= total;
                    __instance.CompletedTasks -= completed;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] TaskPatch failed: {e}");
                }
            }
        }

        // ---- Role identity: shown IN ADDITION to the original role (the Poltergeist keeps its team) ----

        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || poltergeist == null || p == null || p != poltergeist || __result == null) return;
                    if (!__result.Contains(PoltergeistInfo()))
                        __result.Insert(0, PoltergeistInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
