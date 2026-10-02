// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Skinwalker (Impostor)
 *
 * The horror pack's shapeshifter (User 2026-10-02, "super"). Right after a kill the Skinwalker can
 * "Wear Skin" while he stands at the fresh body: the body vanishes and he walks around in the victim's
 * appearance - colour, hat, visor, skin, pet and name - until the next meeting (or a set time). The
 * crew believes the victim is still alive.
 *
 *  - The window is short (10 s by default) and the body has to be in report range, so the skin is a
 *    choice: take it now, or leave the body and run.
 *  - LOOK: purely cosmetic setLook on every client (the Copycat/Morphling way, never RpcSetColor).
 *    Camouflage and the Fungle mushroom sabotage win while they run; a half-second check puts the
 *    skin back on afterwards (TOR's own resets call setDefaultLook for everyone).
 *  - ENDS at the next meeting's start, after the time option, or when the Skinwalker dies.
 *  - Seen by the Giant: a Skinwalker wearing the Giant is drawn big, the Giant's size follows the look.
 *
 * Network: Sub 1 skin (owner-authored, victimId) - the body removal and the look happen on every
 * client. Impostor tag over a plain Impostor. Options 1757-1760, RPC module 228, draft sentinel 225.
 * See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Skinwalker {
        public static readonly Color Color = Palette.ImpostorRed;

        // ---- Options (IDs 1757-1760) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption Window;     // seconds after the kill
        public static CustomOption Lasts;      // 0 until the next meeting, 1 45 s, 2 90 s

        // ---- Runtime state (every client) ----
        public static PlayerControl skinwalker;
        public static bool active;
        private static byte lastVictim = byte.MaxValue;
        private static float lastKillAt = -999f;
        private static byte wornId = byte.MaxValue;
        private static float wornUntil;
        private static CustomButton skinButton;

        private const byte RpcId = UnknownsCollectionPlugin.SkinwalkerRpcId;
        private const byte SubSet = 0;    // playerId          host -> everyone
        private const byte SubSkin = 1;   // victimId          Skinwalker -> everyone

        private static RoleInfo info;
        public static RoleInfo SkinwalkerInfo() => info ??= new RoleInfo(
            "Skinwalker", Color, "Wear your victim's skin and walk among them",
            "Wear your victim's skin", RoleId.Impostor);

        private static readonly System.Random rnd = new System.Random();

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1757, Types.Impostor, "Skinwalker", CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1758, Types.Impostor, "Skinwalker Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                Window = CustomOption.Create(1759, Types.Impostor, "Time To Take The Skin After A Kill (s)", 10f, 3f, 30f, 1f, SpawnRate);
                Lasts = new CustomOption(1760, Types.Impostor, "The Skin Lasts",
                    new string[] { "Until The Next Meeting", "45 Seconds", "90 Seconds" }, "Until The Next Meeting", SpawnRate, false);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Skinwalker] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Skinwalker] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) => UCRpc.Register(RpcId, HandleModuleRpc);

        // ---- queries (Giant) ----
        public static bool IsDisguised(PlayerControl p) =>
            active && skinwalker != null && p != null && p.PlayerId == skinwalker.PlayerId && wornId != byte.MaxValue;
        public static bool WearsSkinOf(PlayerControl p, byte ownerId) => IsDisguised(p) && wornId == ownerId;

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalSkinwalker() =>
            active && skinwalker != null && PlayerControl.LocalPlayer != null && skinwalker.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static float WindowSeconds => Window?.getFloat() ?? 10f;

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
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Skinwalker] SendSet failed: {e}"); }
        }

        private static void SendSkin(byte victim) {
            try {
                var w = BeginRpc(SubSkin);
                w.Write(victim);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySkin(victim);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Skinwalker] SendSkin failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                byte val = reader.ReadByte();
                if (sub == SubSet) { if (UCRpc.RequireHost("Skinwalker.Set")) ApplySet(val); }
                else if (sub == SubSkin) { if (UCRpc.RequireOwnerOrHost(skinwalker, "Skinwalker.Skin")) ApplySkin(val); }
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Skinwalker] HandleRpc failed: {e}"); }
        }

        private static void ApplySet(byte id) {
            skinwalker = id == byte.MaxValue ? null : Helpers.playerById(id);
            active = skinwalker != null;
            lastVictim = byte.MaxValue;
            wornId = byte.MaxValue;
            if (!active) return;
            UCPromotion.Claim(id);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Skinwalker] The Skinwalker is {skinwalker.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySet(playerId);

        private static void ApplySkin(byte victimId) {
            if (!active || skinwalker == null || InMeeting()) return;
            var victim = Helpers.playerById(victimId);
            if (victim == null || victim.Data == null || !victim.Data.IsDead) return;
            foreach (var body in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                if (body != null && body.ParentId == victimId) UnityEngine.Object.Destroy(body.gameObject);
            wornId = victimId;
            int lasts = Lasts?.getSelection() ?? 0;
            wornUntil = lasts == 1 ? Time.time + 45f : lasts == 2 ? Time.time + 90f : float.MaxValue;
            lastVictim = byte.MaxValue;
            ApplyLook();
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Skinwalker] wears the skin of {victim.Data.PlayerName}.");
        }

        private static bool LookBlocked() {
            try { return Camouflager.camouflageTimer > 0f || Helpers.MushroomSabotageActive(); } catch { return false; }
        }

        private static void ApplyLook() {
            if (skinwalker == null || wornId == byte.MaxValue || LookBlocked()) return;
            var v = Helpers.playerById(wornId);
            if (v == null || v.Data == null) return;
            var o = v.Data.DefaultOutfit;
            skinwalker.setLook(v.Data.PlayerName, o.ColorId, o.HatId, o.VisorId, o.SkinId, o.PetId);
        }

        private static void EndSkin(string why) {
            if (wornId == byte.MaxValue) return;
            wornId = byte.MaxValue;
            try { if (skinwalker != null && skinwalker.Data != null && !LookBlocked()) skinwalker.setDefaultLook(); } catch { }
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Skinwalker] skin dropped ({why}).");
        }

        // ---- Kills: remember the Skinwalker's last victim (every client) ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (!active || skinwalker == null || __instance == null || target == null) return;
                    if (target.Data == null || !target.Data.IsDead) return;
                    if (target.PlayerId == skinwalker.PlayerId) { EndSkin("Skinwalker died"); return; }
                    if (__instance.PlayerId != skinwalker.PlayerId) return;
                    lastVictim = target.PlayerId;
                    lastKillAt = Time.time;
                } catch { }
            }
        }

        // ---- Keep the skin on / end it ----
        private static float nextLookCheck;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (!active || wornId == byte.MaxValue || skinwalker == null) return;
                    if (skinwalker.Data == null || skinwalker.Data.IsDead) { EndSkin("Skinwalker dead"); return; }
                    if (Time.time >= wornUntil) { EndSkin("time"); return; }
                    if (Time.time < nextLookCheck) return;
                    nextLookCheck = Time.time + 0.5f;
                    var v = Helpers.playerById(wornId);
                    if (v == null || v.Data == null || LookBlocked()) return;
                    if (skinwalker.CurrentOutfit != null && skinwalker.CurrentOutfit.ColorId != v.Data.DefaultOutfit.ColorId) ApplyLook();
                } catch { }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try { if (active) { EndSkin("meeting"); lastVictim = byte.MaxValue; } } catch { }
            }
        }

        // ---- Pick (host, random path) ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPickPatch {
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
                    UnknownsCollectionPlugin.Logger?.LogError($"[Skinwalker] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Button ----
        // CustomButton asks every frame; the body search runs at most five times a second.
        private static float nextSkinCheck;
        private static bool skinCheck;

        private static bool CanTakeSkin() {
            if (Time.time < nextSkinCheck) return skinCheck;
            nextSkinCheck = Time.time + 0.2f;
            skinCheck = CanTakeSkinNow();
            return skinCheck;
        }

        private static bool CanTakeSkinNow() {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null || lp.Data == null || lp.Data.IsDead || InMeeting()) return false;
            if (wornId != byte.MaxValue || lastVictim == byte.MaxValue) return false;
            if (Time.time - lastKillAt > WindowSeconds) return false;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                if (b != null && b.ParentId == lastVictim && !b.Reported
                    && Vector2.Distance(lp.GetTruePosition(), b.TruePosition) <= lp.MaxReportDistance) return true;
            return false;
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = UCAssets.SkinwalkerIcon ?? Helpers.loadSpriteFromResources("TheOtherRoles.Resources.MorphButton.png", 115f);
                    skinButton = new CustomButton(
                        () => { if (CanTakeSkin()) SendSkin(lastVictim); },
                        () => IsLocalSkinwalker() && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove && CanTakeSkin(),
                        () => { },
                        sprite,
                        CustomButton.ButtonPositions.upperRowLeft,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.skinwalker.button"));
                    skinButton.MaxTimer = 0f;
                    skinButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Skinwalker] Button creation failed: {e}");
                }
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || skinwalker == null || p == null || p != skinwalker || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++)
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) { __result[i] = SkinwalkerInfo(); replaced = true; }
                    if (!replaced) __result.Insert(0, SkinwalkerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Skinwalker] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            skinwalker = null;
            active = false;
            lastVictim = byte.MaxValue;
            wornId = byte.MaxValue;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Skinwalker", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("Skinwalker", FullReset);
        }
    }
}
