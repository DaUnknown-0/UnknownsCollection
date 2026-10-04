// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Giant (Modifier)
 *
 * The counterpart of TOR's Mini (User 2026-10-02): bigger, a little slower, sees further. Anyone can
 * be the Giant by default (option: crew only / crew & impostor / anyone); it never rides on top of
 * another modifier.
 *
 *  - SIZE: TOR resets every player's size in PlayerControlFixedUpdatePatch.playerSizeUpdate each
 *    frame (0.7, the Mini smaller). A later FixedUpdate postfix scales the Giant up and shrinks the collider
 *    radius by the same factor, exactly TOR's Mini correction the other way round: the body that
 *    bumps into walls keeps its vanilla size, so a Giant never gets stuck in a door. During
 *    camouflage and the Fungle mushroom sabotage everybody keeps the default size (TOR's own rule for
 *    the Mini - the size would give the Giant away). A Morphling or Skinwalker wearing the Giant's
 *    look is drawn big as well, again as TOR does for the Mini.
 *  - SPEED: a velocity multiply on the owner and on the remote network transform (the M-6 pattern of
 *    Scout and PlayerTuning), never a MyPhysics.Speed write.
 *  - VISION: a multiplicative stage in UCVision.
 *
 * Options 1763-1768, RPC module 229 (Sub 0 set), display sentinel RoleId 236. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Patches;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Giant {
        // Earthy stone grey-brown: not the Mini's pale yellow, not a team colour.
        public static readonly Color Color = new Color(0.62f, 0.50f, 0.36f);

        // ---- Options (IDs 1763-1768) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption WhoCanBe;        // 0 crew only, 1 crew & impostor, 2 anyone
        public static CustomOption Size;            // x the normal size
        public static CustomOption SpeedFactor;     // % of the normal speed
        public static CustomOption VisionFactor;    // % of the normal vision

        // ---- Runtime state ----
        public static PlayerControl giant;
        public static bool active;

        private const byte RpcId = UnknownsCollectionPlugin.GiantRpcId;
        private const byte SubSet = 0;   // playerId (255 = clear), host -> everyone

        private const RoleId GiantRoleId = (RoleId)236;
        private static RoleInfo giantInfo;
        public static RoleInfo GiantInfo() => giantInfo ??= new RoleInfo(
            "Giant", Color, "You are huge: slower, but you see further",
            "Bigger, slower, sees further", GiantRoleId, false, true);

        private static readonly System.Random rnd = new System.Random();

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1763, Types.Modifier, "Giant", CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1764, Types.Modifier, "Giant Minimum Players To Spawn",
                    4f, 4f, 15f, 1f, SpawnRate);
                WhoCanBe = new CustomOption(1765, Types.Modifier, "Giant Can Be",
                    new string[] { "Crew Only", "Crew & Impostor", "Anyone" }, "Anyone", SpawnRate, false);
                // 0.1 steps: pre-rounded selections, TOR's float accumulation would drop the default to the min
                Size = new CustomOption(1766, Types.Modifier, "Giant Size", FloatRange(1.1f, 1.6f, 0.1f), 1.3f, SpawnRate, false);
                SpeedFactor = CustomOption.Create(1767, Types.Modifier, "Giant Speed (%)", 85f, 60f, 100f, 5f, SpawnRate);
                VisionFactor = CustomOption.Create(1768, Types.Modifier, "Giant Vision (%)", 125f, 100f, 175f, 5f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Giant] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Giant] CreateOptions failed: {e}");
            }
        }

        // The Auditor's FloatRange (see project note on TOR's float option accumulation).
        private static object[] FloatRange(float min, float max, float step) {
            var sels = new List<object>();
            for (double s = min; s <= max + step * 0.5; s += step) sels.Add((float)Math.Round(s, 2));
            return sels.ToArray();
        }

        public static void TryPatch(Harmony harmony) => UCRpc.Register(RpcId, HandleModuleRpc);

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsGiant(PlayerControl p) => active && giant != null && p != null && p.PlayerId == giant.PlayerId;
        private static float SizeValue => Mathf.Clamp(Size?.getFloat() ?? 1.3f, 1f, 2f);

        // ---- RPC ----
        public static void SendSet(byte id) {
            try {
                var w = UCRpc.Begin(RpcId);
                w.Write(SubSet);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySet(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Giant] SendSet failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                if (sub == SubSet) {
                    byte id = reader.ReadByte();
                    if (UCRpc.RequireHost("Giant.Set")) ApplySet(id);
                }
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Giant] HandleRpc failed: {e}"); }
        }

        private static void RestoreNormalSize(PlayerControl p) {
            if (p == null) return;   // Unity null too: a destroyed PlayerControl needs nothing
            try {
                p.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
                var collider = p.Collider.CastFast<CircleCollider2D>();
                if (collider != null) collider.radius = Mini.defaultColliderRadius;
            } catch { }
        }

        private static void ApplySet(byte id) {
            // Taken away (Role Control): give the old holder normal size back. In a running round TOR's
            // playerSizeUpdate would do it next frame, in freeplay nothing would.
            if (giant != null && giant.PlayerId != id) RestoreNormalSize(giant);
            giant = id == byte.MaxValue ? null : Helpers.playerById(id);
            active = giant != null;
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Giant] The Giant is {giant.Data?.PlayerName}.");
        }

        // ---- Pick (host; modifiers have no draft entry) ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndPickPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix() {
                try {
                    if (!AmHost() || active) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 4f)) return;
                    if (rnd.Next(1, 101) > SpawnRate.getSelection() * 10) return;
                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(IsCandidate).ToList();
                    if (candidates.Count == 0) return;
                    SendSet(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Giant] IntroEnd pick failed: {e}");
                }
            }
        }

        private static bool IsCandidate(PlayerControl p) {
            try {
                if (!UCPromotion.IsAlive(p) || p.Data.Role == null) return false;
                if (UCPromotion.HasAnyModifier(p)) return false;
                int who = WhoCanBe?.getSelection() ?? 2;
                if (who == 2) return true;
                var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
                if (info != null && info.isNeutral) return false;
                if (who == 0 && p.Data.Role.IsImpostor) return false;
                return true;
            } catch { return false; }
        }

        // ---- Size: after TOR's own per-frame size reset ----
        // Whose look carries the Giant's body: the Giant himself, or a Morphling / Skinwalker wearing him.
        private static bool DrawnAsGiant(PlayerControl p) {
            if (!active || giant == null || p == null) return false;
            if (p.PlayerId == giant.PlayerId) {
                // the Giant himself, unless he is morphed into somebody else
                if (Morphling.morphling != null && p == Morphling.morphling && Morphling.morphTimer > 0f) return false;
                if (Skinwalker.IsDisguised(p)) return false;
                return true;
            }
            if (Morphling.morphling != null && p == Morphling.morphling && Morphling.morphTarget == giant && Morphling.morphTimer > 0f) return true;
            return Skinwalker.WearsSkinOf(p, giant.PlayerId);
        }

        // After TOR's own FixedUpdate postfix (which calls playerSizeUpdate - but only while the game state
        // is Started, so a size hook inside playerSizeUpdate never ran in freeplay).
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
        static class SizePatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix(PlayerControl __instance) {
                try {
                    var p = __instance;
                    if (!DrawnAsGiant(p)) return;
                    if (Camouflager.camouflageTimer > 0f || Helpers.MushroomSabotageActive()) return;
                    float scale = 0.7f * SizeValue;
                    p.transform.localScale = new Vector3(scale, scale, 1f);
                    var collider = p.Collider.CastFast<CircleCollider2D>();
                    if (collider != null) collider.radius = Mini.defaultColliderRadius * 0.7f / scale;
                } catch { }
            }
        }

        // ---- Speed: velocity multiply (owner + remote prediction) ----
        private static float SpeedMult(PlayerControl p) =>
            IsGiant(p) ? Mathf.Clamp((SpeedFactor?.getFloat() ?? 85f) / 100f, 0.3f, 1f) : 1f;

        [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
        static class SpeedOwnerPatch {
            public static void Postfix(PlayerPhysics __instance) {
                try {
                    if (!active || !__instance.AmOwner || __instance.myPlayer == null || __instance.myPlayer.Data == null) return;
                    float m = SpeedMult(__instance.myPlayer);
                    if (Mathf.Abs(m - 1f) < 0.0001f) return;
                    if (__instance.myPlayer.Data.IsDead || !__instance.myPlayer.CanMove) return;
                    __instance.body.velocity *= m;
                } catch { }
            }
        }

        [HarmonyPatch(typeof(CustomNetworkTransform), nameof(CustomNetworkTransform.FixedUpdate))]
        static class SpeedRemotePatch {
            public static void Postfix(CustomNetworkTransform __instance) {
                try {
                    if (!active || __instance.AmOwner || __instance.myPlayer == null || __instance.myPlayer.Data == null) return;
                    float m = SpeedMult(__instance.myPlayer);
                    if (Mathf.Abs(m - 1f) < 0.0001f) return;
                    if (__instance.myPlayer.Data.IsDead) return;
                    __instance.body.velocity *= m;
                } catch { }
            }
        }

        // ---- Vision: a multiplicative stage in UCVision ----
        public static float VisionMult(NetworkedPlayerInfo p) {
            if (!active || giant == null || p == null || p.PlayerId != giant.PlayerId || p.IsDead) return 1f;
            return Mathf.Clamp((VisionFactor?.getFloat() ?? 125f) / 100f, 1f, 2f);
        }

        // ---- Role identity: append (a modifier rides on top of the real role) ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, [HarmonyArgument(1)] bool showModifier, ref List<RoleInfo> __result) {
                try {
                    if (!active || giant == null || p == null || p != giant || __result == null || !showModifier) return;
                    if (!__result.Contains(GiantInfo())) __result.Add(GiantInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Giant] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            // TOR's playerSizeUpdate only runs in a started game, so outside one (lobby, freeplay
            // end) nothing else would shrink a still-existing former Giant back.
            RestoreNormalSize(giant);
            giant = null;
            active = false;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Giant", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("Giant", FullReset);
        }
    }
}
