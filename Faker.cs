// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Faker (Impostor)
 *
 * Lays down a FAKE BODY of a living player (User 2026-10-08, "interessant"; chosen by the Faker, also himself
 * or a fellow Impostor). Whoever reports it starts a normal meeting: the intro shows the victim as DEAD,
 * the player list then shows him alive. The lie is short, its worth is the forced meeting and the confusion.
 *
 *  - VICTIM: chosen with a second button (H) that cycles through ALL living players, himself and fellow
 *    Impostors included, wherever they are. (The first version took the nearest player in range; that put the
 *    victim right next to his own "body" and gave the Faker away, playtest 2026-10-08.)
 *  - ONE fake body per victim at a time, limited uses per game, cooldown, and a lifetime after which the body
 *    simply vanishes. All fake bodies vanish when a meeting starts (it is over by then) or the victim really dies.
 *  - The Faker cannot report his own fake body (it would be an emergency button without a price).
 *  - A fake body is no real body for TOR's cleaners: Janitor/Cleaner/Vulture clean it away, but the Vulture
 *    does not get his eaten-body count for it (RPCProcedure.cleanBody prefix).
 *
 * Spike (2026-10-08, DebugUnlock key V, Freeplay): a DeadBody with the ParentId of a LIVING player is created
 * from GameManager.DeadBodyPrefab, can be reported, and the meeting starts cleanly (no exception). Dead bodies
 * are no network objects, so the host-checked RPC creates the body on every client identically.
 *
 * Network: Sub 0 set (host -> everyone), Sub 1 place (owner-authored: victimId, x, y). Options 1794-1798,
 * RPC module 231, draft sentinel 229. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Faker {
        public static readonly Color Color = Palette.ImpostorRed;

        // ---- Options (IDs 1794-1798) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption Cooldown;    // seconds
        public static CustomOption Uses;        // fake bodies per game
        public static CustomOption Lifetime;    // seconds until a fake body vanishes
        public static CustomOption ReportOwn;   // the Faker may report his own fake body (off: he may not)

        // ---- Runtime state (every client) ----
        public static PlayerControl faker;
        public static bool active;
        private static int usesLeft;
        private static byte selectedVictim = byte.MaxValue;   // local Faker only; 255 = himself
        private static CustomButton fakeButton;
        private static CustomButton pickButton;

        private sealed class Fake {
            public DeadBody body;
            public float expiresAt;
        }
        private static readonly Dictionary<byte, Fake> fakes = new Dictionary<byte, Fake>();

        private const byte RpcId = UnknownsCollectionPlugin.FakerRpcId;
        private const byte SubSet = 0;     // playerId          host -> everyone (255 = clear)
        private const byte SubPlace = 1;   // victimId, x, y    Faker -> everyone

        private static RoleInfo info;
        public static RoleInfo FakerInfo() => info ??= new RoleInfo(
            "Faker", Color, "Lay a fake body and force a meeting",
            "Lay a fake body", RoleId.Impostor);

        private static readonly System.Random rnd = new System.Random();

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1794, Types.Impostor, "Faker", CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1795, Types.Impostor, "Faker Minimum Players To Spawn",
                    6f, 4f, 30f, 1f, SpawnRate);
                Cooldown = CustomOption.Create(1796, Types.Impostor, "Fake Body Cooldown (s)", 30f, 10f, 90f, 5f, SpawnRate);
                Uses = CustomOption.Create(1797, Types.Impostor, "Fake Bodies Per Game", 2f, 1f, 5f, 1f, SpawnRate);
                Lifetime = CustomOption.Create(1798, Types.Impostor, "Fake Body Lifetime (s)", 40f, 10f, 120f, 5f, SpawnRate);
                ReportOwn = CustomOption.Create(1799, Types.Impostor, "Faker Can Report His Own Fake Body", false, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Faker] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Faker] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) => UCRpc.Register(RpcId, HandleModuleRpc);

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalFaker() =>
            active && faker != null && PlayerControl.LocalPlayer != null && faker.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static bool Alive(PlayerControl p) => p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        // ---- RPC ----
        private static MessageWriter BeginRpc(byte sub) {
            var w = UCRpc.Begin(RpcId);
            w.Write(sub);
            return w;
        }

        public static void SendSet(byte id) {
            try {
                var w = BeginRpc(SubSet);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySet(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Faker] SendSet failed: {e}"); }
        }

        private static void SendPlace(byte victim, Vector2 at) {
            try {
                var w = BeginRpc(SubPlace);
                w.Write(victim);
                w.Write(at.x);
                w.Write(at.y);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyPlace(victim, at);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Faker] SendPlace failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                if (sub == SubSet) {
                    byte id = reader.ReadByte();
                    if (UCRpc.RequireHost("Faker.Set")) ApplySet(id);
                } else if (sub == SubPlace) {
                    byte victim = reader.ReadByte();
                    var at = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    if (UCRpc.RequireOwnerOrHost(faker, "Faker.Place")) ApplyPlace(victim, at);
                }
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Faker] HandleRpc failed: {e}"); }
        }

        private static void ApplySet(byte id) {
            ClearFakes("role reassigned");
            faker = id == byte.MaxValue ? null : Helpers.playerById(id);
            active = faker != null;
            usesLeft = Mathf.RoundToInt(Uses?.getFloat() ?? 2f);
            selectedVictim = byte.MaxValue;
            if (!active) return;
            UCPromotion.Claim(id);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Faker] The Faker is {faker.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySet(playerId);

        // ---- The fake body (every client) ----
        private static void ApplyPlace(byte victimId, Vector2 at) {
            try {
                if (!active || faker == null || InMeeting()) return;
                var victim = Helpers.playerById(victimId);
                if (!Alive(victim)) return;                       // a fake body needs a living victim
                DestroyFake(victimId);                            // at most one per victim
                var prefab = GameManager.Instance != null ? GameManager.Instance.DeadBodyPrefab : null;
                if (prefab == null) { UnknownsCollectionPlugin.Logger?.LogWarning("[Faker] DeadBodyPrefab missing."); return; }

                var db = UnityEngine.Object.Instantiate(prefab);
                db.enabled = false;
                db.ParentId = victimId;
                if (db.bodyRenderers != null)
                    foreach (var r in db.bodyRenderers) if (r != null) victim.SetPlayerMaterialColors(r);
                if (db.bloodSplatter != null) victim.SetPlayerMaterialColors(db.bloodSplatter);
                var pos = new Vector3(at.x, at.y, at.y / 1000f);
                db.transform.position = pos;
                db.enabled = true;

                fakes[victimId] = new Fake { body = db, expiresAt = Time.time + (Lifetime?.getFloat() ?? 40f) };
                usesLeft = Mathf.Max(0, usesLeft - 1);
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Faker] fake body of {victim.Data.PlayerName} at {at.x:F1}/{at.y:F1} ({usesLeft} left).");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Faker] ApplyPlace failed: {e}");
            }
        }

        private static void DestroyFake(byte victimId) {
            if (!fakes.TryGetValue(victimId, out var f)) return;
            fakes.Remove(victimId);
            try { if (f.body != null) UnityEngine.Object.Destroy(f.body.gameObject); } catch { }
        }

        private static void ClearFakes(string why) {
            if (fakes.Count == 0) return;
            foreach (var id in fakes.Keys.ToArray()) DestroyFake(id);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Faker] fake bodies cleared ({why}).");
        }

        // The report block is a rule of the role, not a technical limit: off in Freeplay (nothing is at stake there,
        // and a solo test could not report anything otherwise) and when the host allows it (option 1799).
        private static bool ReportBlockApplies() {
            try {
                if (AmongUsClient.Instance != null && AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay) return false;
                if (ReportOwn != null && ReportOwn.getBool()) return false;
            } catch { }
            return true;
        }

        /// True while a fake body of this LIVING player stands in the map (the reporting guards use it).
        private static bool IsFakeVictim(byte playerId) {
            if (!fakes.TryGetValue(playerId, out var f) || f.body == null) return false;
            var p = Helpers.playerById(playerId);
            return Alive(p);
        }

        // Expiry, destroyed bodies, and a victim who really died meanwhile (then there is a real body).
        private static float nextTick;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                try {
                    if (fakes.Count == 0 || Time.time < nextTick) return;
                    nextTick = Time.time + 0.25f;
                    foreach (var kv in fakes.ToArray()) {
                        var victim = Helpers.playerById(kv.Key);
                        if (kv.Value.body == null || Time.time >= kv.Value.expiresAt || !Alive(victim)) DestroyFake(kv.Key);
                    }
                } catch { }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try { ClearFakes("meeting"); } catch { }
            }
        }

        // ---- The Faker cannot report his own fake body (client call and host call) ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdReportDeadBody))]
        static class CmdReportGuardPatch {
            public static bool Prefix(PlayerControl __instance, NetworkedPlayerInfo target) {
                try {
                    if (!active || faker == null || target == null || __instance == null) return true;
                    if (__instance.PlayerId == faker.PlayerId && IsFakeVictim(target.PlayerId) && ReportBlockApplies()) return false;
                } catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ReportDeadBody))]
        static class ReportGuardPatch {
            public static bool Prefix(PlayerControl __instance, NetworkedPlayerInfo target) {
                try {
                    if (!active || faker == null || target == null || __instance == null) return true;
                    if (__instance.PlayerId == faker.PlayerId && IsFakeVictim(target.PlayerId) && ReportBlockApplies()) return false;
                } catch { }
                return true;
            }
        }

        // ---- A fake body is cleaned away without the Vulture's eaten-body count ----
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.cleanBody))]
        static class CleanBodyPatch {
            public static bool Prefix(byte playerId) {
                try {
                    if (!IsFakeVictim(playerId)) return true;
                    DestroyFake(playerId);
                    return false;          // skips TOR's own cleaning AND the Vulture's eatenBodies++
                } catch { }
                return true;
            }
        }

        // ---- Pick (host, random path) ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndPickPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix() {
                try {
                    if (!AmHost() || active) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;
                    if (rnd.Next(1, 101) > SpawnRate.getSelection() * 10) return;
                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;
                    SendSet(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Faker] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Round: victim choice + buttons ----

        // Everybody alive who has no fake body standing yet (the Faker himself and fellow Impostors included),
        // in a stable order (PlayerId), so the pick button walks the same circle every time.
        private static List<PlayerControl> Candidates() {
            var list = new List<PlayerControl>();
            foreach (var p in PlayerControl.AllPlayerControls) {
                if (!Alive(p) || fakes.ContainsKey(p.PlayerId)) continue;
                list.Add(p);
            }
            list.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
            return list;
        }

        // The victim the next press of FAKE BODY uses: the picked player while he is a valid candidate,
        // otherwise the Faker himself, and when even that is taken, nobody.
        private static byte PlannedVictim() {
            if (faker == null) return byte.MaxValue;
            if (selectedVictim != byte.MaxValue) {
                var v = Helpers.playerById(selectedVictim);
                if (Alive(v) && !fakes.ContainsKey(selectedVictim)) return selectedVictim;
            }
            return !fakes.ContainsKey(faker.PlayerId) ? faker.PlayerId : byte.MaxValue;
        }

        private static void CyclePick() {
            try {
                var list = Candidates();
                if (list.Count == 0) return;
                byte planned = PlannedVictim();
                int idx = list.FindIndex(p => p.PlayerId == planned);
                selectedVictim = list[(idx + 1) % list.Count].PlayerId;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Faker] picking the victim failed: {e.Message}");
            }
        }

        private static bool CanFake() {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null || lp.Data == null || lp.Data.IsDead || InMeeting() || usesLeft <= 0) return false;
            byte v = PlannedVictim();
            return v != byte.MaxValue && !fakes.ContainsKey(v);
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = UCAssets.FakerIcon ?? Helpers.loadSpriteFromResources("TheOtherRoles.Resources.MorphButton.png", 115f);
                    fakeButton = new CustomButton(
                        () => {
                            if (!CanFake()) return;
                            SendPlace(PlannedVictim(), PlayerControl.LocalPlayer.GetTruePosition());
                            fakeButton.Timer = fakeButton.MaxTimer;
                            selectedVictim = byte.MaxValue;     // the next fake starts from himself again
                        },
                        () => IsLocalFaker() && PlayerControl.LocalPlayer.Data != null
                              && !PlayerControl.LocalPlayer.Data.IsDead && usesLeft > 0,
                        () => PlayerControl.LocalPlayer.CanMove && CanFake(),
                        // The cooldown starts over when the meeting ends.
                        () => { if (fakeButton != null) fakeButton.Timer = fakeButton.MaxTimer; },
                        sprite,
                        CustomButton.ButtonPositions.upperRowLeft,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.faker.button"));
                    fakeButton.MaxTimer = Cooldown?.getFloat() ?? 30f;
                    fakeButton.Timer = 10f;

                    // Second button (H): who is the victim? Its label shows the picked player's name.
                    pickButton = new CustomButton(
                        () => CyclePick(),
                        () => IsLocalFaker() && PlayerControl.LocalPlayer.Data != null
                              && !PlayerControl.LocalPlayer.Data.IsDead && usesLeft > 0,
                        () => PlayerControl.LocalPlayer.CanMove && !InMeeting(),
                        () => { },
                        UCAssets.FakerSwapIcon ?? sprite,      // own icon: "switch the victim", not the body
                        CustomButton.ButtonPositions.upperRowFarLeft,
                        __instance, KeyCode.H, false, UCLocalization.Tr("uc.ui.faker.button_self"));
                    pickButton.MaxTimer = 0f;
                    pickButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Faker] Button creation failed: {e}");
                }
            }
        }

        // The pick button's label is the picked player's name ("FAKE SELF" for himself). If the picked
        // player died or left, the plan falls back to the Faker himself on its own (PlannedVictim).
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class LabelPatch {
            private static string lastLabel;
            public static void Postfix() {
                try {
                    if (pickButton == null || !IsLocalFaker()) return;
                    byte v = PlannedVictim();
                    string want;
                    if (v == byte.MaxValue) want = "-";
                    else if (faker != null && v == faker.PlayerId) want = UCLocalization.Tr("uc.ui.faker.button_self");
                    else {
                        var vp = Helpers.playerById(v);
                        string nm = vp?.Data?.PlayerName ?? "?";
                        // The name in the victim's own colour: the left button reads as "who", not as a second action.
                        int ci = vp?.Data?.DefaultOutfit != null ? vp.Data.DefaultOutfit.ColorId : -1;
                        want = ci >= 0 && ci < Palette.PlayerColors.Length ? Helpers.cs(Palette.PlayerColors[ci], nm) : nm;
                    }
                    if (want != lastLabel) { pickButton.buttonText = want; lastLabel = want; }
                } catch { }
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || faker == null || p == null || p != faker || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++)
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) { __result[i] = FakerInfo(); replaced = true; }
                    if (!replaced) __result.Insert(0, FakerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Faker] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            ClearFakes("reset");
            faker = null;
            active = false;
            usesLeft = 0;
            selectedVictim = byte.MaxValue;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Faker", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("Faker", FullReset);
        }
    }
}
