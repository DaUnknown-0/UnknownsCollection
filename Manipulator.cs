// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Manipulator (Impostor)
 *
 * On button press, the ship's security devices LIE for a configurable duration - for EVERYONE:
 *   - the ADMIN table shows a fabricated player distribution (a synced seed + a time bucket keyed to
 *     the effect end make every client render the SAME believable lie, re-rolled every few seconds);
 *   - VITALS shows every dead player as alive again (disconnects stay shown - hiding those would be
 *     an obvious glitch, and vanilla already re-marks the dead the moment the effect ends).
 *
 * The lie is deliberately subtle - no static, no flicker. Crew members trusting their tools clear
 * rooms that aren't clear and believe victims are still alive.
 *
 * Map coverage: the patches attach to the DEVICES, not the map - Skeld/Mira have Admin (no Vitals),
 * Polus/Airship have both, Fungle has only Vitals. Where a device doesn't exist, that half of the
 * ability simply has nothing to fake. Comms sabotage keeps its normal "signal lost" behaviour (a
 * disabled admin table suddenly showing data would out the Manipulator).
 *
 * ARCHITECTURE mirrors Tesla/Saboteur: impostor tag over the real Impostor, host-agnostic broadcast
 * RPC (210), gated on "everyone has the mod" (the fake runs client-side on every client).
 * Our MapCountOverlay prefix runs at Priority.First - BEFORE TOR's own prefix (UsablesPatch.cs),
 * which fully reimplements the overlay for the Hacker. Options 1590-1595. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Manipulator {
        // ---- Theme ----
        public static readonly Color Color = Palette.ImpostorRed; // impostor role -> red role tag

        // ---- Options (IDs 1590-1595) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption Cooldown;
        public static CustomOption Duration;
        public static CustomOption FakeAdmin;
        public static CustomOption FakeVitals;

        // ---- Runtime state (synced via RPC) ----
        public static PlayerControl manipulator;
        public static bool active;
        private static float fakeUntil;
        private static int fakeSeed;

        // ---- Custom RPC (210) subtypes ----
        private const byte RpcId = UnknownsCollectionPlugin.ManipulatorRpcId;
        private const byte SubSetManipulator = 0; // playerId
        private const byte SubManipulate = 1;     // seed(int), duration(float)

        // ---- Role identity ----
        private static RoleInfo manipulatorInfo;
        public static RoleInfo ManipulatorInfo() => manipulatorInfo ??= new RoleInfo(
            "Manipulator", Color, "Make the ship's security devices lie",
            "Make the security devices lie", RoleId.Impostor);

        private static TheOtherRoles.Objects.CustomButton manipulateButton;

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1590, Types.Impostor, "Manipulator",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1591, Types.Impostor, "Manipulator Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                Cooldown = CustomOption.Create(1592, Types.Impostor, "Manipulation Cooldown",
                    30f, 10f, 60f, 2.5f, SpawnRate);
                Duration = CustomOption.Create(1593, Types.Impostor, "Manipulation Duration",
                    12f, 5f, 30f, 1f, SpawnRate);
                FakeAdmin = CustomOption.Create(1594, Types.Impostor, "Admin Table Shows Fake Positions",
                    true, SpawnRate);
                FakeVitals = CustomOption.Create(1595, Types.Impostor, "Vitals Shows Dead Players As Alive",
                    true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Manipulator] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Manipulator] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { }

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalManipulator() =>
            active && manipulator != null && PlayerControl.LocalPlayer != null
            && manipulator.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        public static bool IsFaking() => active && Time.time < fakeUntil;

        // ---- RPC plumbing ----

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetManipulator(byte id) {
            try {
                var w = BeginRpc(SubSetManipulator);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetManipulator(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Manipulator] SendSet failed: {e}"); }
        }

        private static void SendManipulate(int seed, float duration) {
            try {
                var w = BeginRpc(SubManipulate);
                w.Write(seed);
                w.Write(duration);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyManipulate(seed, duration);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Manipulator] SendManipulate failed: {e}"); }
        }

        private static void ApplySetManipulator(byte id) {
            manipulator = Helpers.playerById(id);
            active = manipulator != null;
            if (active) UCPromotion.Claim(id);
            fakeUntil = 0f;
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Manipulator] The Manipulator is {manipulator.Data?.PlayerName}.");
        }

        private static void ApplyManipulate(int seed, float duration) {
            fakeSeed = seed;
            fakeUntil = Time.time + duration;
            if (IsLocalManipulator()) UCAssets.PlayManipulatorWarp(); // only the Manipulator hears the hijack
        }

        public static void MarkFromDraft(byte playerId) => ApplySetManipulator(playerId);

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubSetManipulator: ApplySetManipulator(reader.ReadByte()); break;
                        case SubManipulate: {
                            int seed = reader.ReadInt32();
                            float dur = reader.ReadSingle();
                            ApplyManipulate(seed, dur);
                            break;
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Manipulator] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                manipulator = null;
                active = false;
                fakeUntil = 0f;
                // manipulateButton deliberately kept: resetVariables runs AFTER HudManager.Start
                // at round start - nulling it here orphans the live button (see Collector.cs).
            }
        }

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

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;
                    SendSetManipulator(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Manipulator] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Button ----

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    float duration = Duration?.getFloat() ?? 12f;
                    manipulateButton = new TheOtherRoles.Objects.CustomButton(
                        () => SendManipulate(rnd.Next(int.MinValue, int.MaxValue), Duration?.getFloat() ?? 12f),
                        () => active && IsLocalManipulator()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove && !IsFaking(),
                        () => { },
                        UCAssets.ManipulatorIcon,
                        // Impostor: right-side slots overlap the kill button; upperRowLeft is shared
                        // with plant/lights-style buttons of OTHER roles, which never coexist with us.
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowLeft,
                        __instance, KeyCode.F, true, duration, () => { }, false, "FAKE");
                    manipulateButton.MaxTimer = Cooldown?.getFloat() ?? 30f;
                    manipulateButton.Timer = 10f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Manipulator] Button creation failed: {e}");
                }
            }
        }

        // ---- ADMIN fake: our prefix runs BEFORE TOR's full reimplementation (Priority.First) ----

        [HarmonyPatch(typeof(MapCountOverlay), nameof(MapCountOverlay.Update))]
        [HarmonyPriority(Priority.First)]
        static class FakeAdminPatch {
            // Own draw cadence. We must NOT share __instance.timer with TOR: under HarmonyX ALL
            // prefixes run even after one returned false (false only skips the ORIGINAL method),
            // so TOR's full-reimplementation prefix still executes every frame. With a shared
            // timer the 0.1s threshold fired alternately in our prefix (fake counts) and in
            // TOR's (real counts) -> the table flickered fake/real. Instead we pin
            // __instance.timer to 0 while faking, which starves TOR's throttle so it never
            // draws, and run our fake on this private timer.
            private static float drawTimer = 1f; // starts above 0.1s -> first fake frame draws
            public static bool Prefix(MapCountOverlay __instance) {
                try {
                    if (!IsFaking() || !(FakeAdmin?.getBool() ?? true)) return true;

                    // Comms sabotage: keep the vanilla/TOR "signal lost" path - an admin table that
                    // works during comms would expose the manipulation.
                    bool commsActive = false;
                    foreach (PlayerTask task in PlayerControl.LocalPlayer.myTasks.GetFastEnumerator())
                        if (task.TaskType == TaskTypes.FixComms) commsActive = true;
                    if (commsActive) { drawTimer = 1f; return true; }

                    __instance.timer = 0f; // starve TOR's prefix (see note above)
                    drawTimer += Time.deltaTime;
                    if (drawTimer < 0.1f) return false;
                    drawTimer = 0f;

                    if (__instance.isSab) { // recover from a previous sab state exactly like TOR does
                        __instance.isSab = false;
                        __instance.BackgroundColor.SetColor(UnityEngine.Color.green);
                        __instance.SabotageText.gameObject.SetActive(false);
                    }

                    // Believable lie: distribute the real number of standing players over the rooms,
                    // re-rolled every 4 s. The bucket is keyed to the effect END (set from the same
                    // RPC on every client), so all clients render the SAME fake distribution.
                    int bodies = PlayerControl.AllPlayerControls.ToArray()
                        .Count(p => p != null && p.Data != null && !p.Data.Disconnected && !p.Data.IsDead);
                    int bucket = Mathf.FloorToInt((fakeUntil - Time.time) / 4f);
                    var fake = new System.Random(fakeSeed ^ (bucket * 486187739));
                    var counts = new int[__instance.CountAreas.Length];
                    for (int b = 0; b < bodies; b++)
                        counts[fake.Next(counts.Length)]++;
                    for (int i = 0; i < __instance.CountAreas.Length; i++)
                        __instance.CountAreas[i].UpdateCount(counts[i]);
                    return false;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Manipulator] admin fake failed: {e}");
                    return true;
                }
            }
        }

        // ---- VITALS fake: postfix flips dead panels back to alive after vanilla updated them ----

        [HarmonyPatch(typeof(VitalsMinigame), nameof(VitalsMinigame.Update))]
        static class FakeVitalsPatch {
            public static void Postfix(VitalsMinigame __instance) {
                try {
                    if (!IsFaking() || !(FakeVitals?.getBool() ?? true)) return;
                    if (__instance == null || __instance.vitals == null) return;
                    foreach (var panel in __instance.vitals) {
                        if (panel == null || !panel.IsDead) continue;
                        if (panel.PlayerInfo == null || panel.PlayerInfo.Disconnected) continue;
                        // SetAlive resets IsDead + the cardio line, but there is NO stored "alive"
                        // background sprite (only VitalBgDead/VitalBgDiscon exist), so the red dead
                        // background stays - the panel kept LOOKING dead. Restore it from the panel
                        // prefab, whose Background still carries the alive sprite.
                        panel.SetAlive();
                        var prefab = __instance.PanelPrefab;
                        if (prefab != null && prefab.Background != null && panel.Background != null)
                            panel.Background.sprite = prefab.Background.sprite;
                        // vanilla re-marks the panel dead every frame (IsDead is false again), so
                        // the real state returns by itself on the frame the fake ends.
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Manipulator] vitals fake failed: {e}");
                }
            }
        }

        // ---- Role identity ----

        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || manipulator == null || p == null || p != manipulator || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) {
                            __result[i] = ManipulatorInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, ManipulatorInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Manipulator] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
