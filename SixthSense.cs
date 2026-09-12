// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Sixth Sense (crew MODIFIER)
 *
 * The carrier's screen edge pulses whenever a killer whose kill is READY stands within range.
 * It says THAT, never who: no direction, no name. Option: the pulse grows with proximity (a
 * killer right next to you glows harder than one at the edge of the range), or it is flat.
 * A killer on cooldown is invisible to the sense, so an impostor who just killed walks past
 * unnoticed, and one who is waiting for the cooldown to end is a slowly rising dread.
 *
 * WHAT COUNTS AS A KILLER. Impostors (vanilla kill button: killTimer), and - option, on by
 * default - the Jackal and a Sidekick allowed to kill (TOR CustomButtons: jackalKillButton /
 * sidekickKillButton, their Timer). A Janitor never counts, a Mafioso only once the Godfather is
 * dead. Sheriff/Deputy are crew and never count; UC roles with their own strike buttons (Pelican,
 * Stalker, Necromancer ...) are not read either: the sense is about the classic kill.
 *
 * WHY AN RPC. Cooldowns are LOCAL: nobody's client knows another player's killTimer. So every
 * killer's own client watches its own readiness and broadcasts each transition (ready <-> not
 * ready) - one small message per cooldown cycle, only while a Sixth Sense exists. Every client
 * keeps the map; the carrier's client combines it with positions (which everybody has) and draws
 * the pulse. Owner-only guard on the message; the host may also send it (a host-side fallback,
 * same rule as the other owner messages).
 *
 * THE PULSE is the Poltergeist hex vignette technique: an own SpriteRenderer cloned from
 * HudManager.FullScreen with a procedural radial-edge sprite sized to the real FullScreen, never
 * TOR's shared renderer. Carrier only, alive only, never in meetings.
 *
 * ARCHITECTURE: modifier over any crew role (the Gambler pattern), host-authoritative pick, custom
 * RPC module 223 on UCRpc.CallId = 230, gated on "everyone has the mod". Options 1680-1684, display
 * RoleId sentinel 234, no draft entry. See ID-Registry.md.
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
    public static class SixthSense {
        // ---- Theme ----
        // Warning amber-orange: the Witness is a yellowish amber, impostor red is darker and redder.
        public static readonly Color Color = new Color(1f, 0.52f, 0.16f);

        // ---- Options (IDs 1680-1684) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption Range;
        public static CustomOption DetectsNeutralKillers;
        public static CustomOption GrowsWithProximity;

        // ---- Runtime state ----
        public static PlayerControl carrier;
        public static bool active;
        private static byte carrierId = byte.MaxValue;
        private static readonly Dictionary<byte, bool> ready = new();   // killer id -> kill ready
        private static bool localReadyState;
        private static bool localReadyKnown;

        // ---- Custom RPC subtypes: module byte 223 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = UnknownsCollectionPlugin.SixthSenseRpcId;
        private const byte SubSet = 0;          // playerId (255 = clear)      host -> everyone
        private const byte SubReady = 1;        // playerId, bool ready        killer -> everyone

        private static readonly System.Random rnd = new System.Random();

        // ---- Identity (display-only sentinel RoleId, see Gambler / Void / Sleepwalker / Last Words) ----
        private const RoleId SixthSenseRoleId = (RoleId)234;
        private static RoleInfo info;
        public static RoleInfo Info() => info ??= new RoleInfo(
            "Sixth Sense", Color, "Your screen pulses when a ready killer is near - never who",
            "Feels a ready killer nearby", SixthSenseRoleId, false, true);

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1680, Types.Modifier, "Sixth Sense",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1681, Types.Modifier, "Sixth Sense Minimum Players To Spawn",
                    5f, 4f, 15f, 1f, SpawnRate);
                Range = CustomOption.Create(1682, Types.Modifier, "Sixth Sense Range",
                    3f, 1f, 8f, 0.5f, SpawnRate);
                DetectsNeutralKillers = CustomOption.Create(1683, Types.Modifier, "Sixth Sense Detects Jackal And Sidekick",
                    true, SpawnRate);
                GrowsWithProximity = CustomOption.Create(1684, Types.Modifier, "Pulse Grows With Proximity",
                    true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[SixthSense] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[SixthSense] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            UCRpc.Register(RpcId, HandleModuleRpc);
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(HideVignette);
        }

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalCarrier() =>
            active && carrier != null && PlayerControl.LocalPlayer != null
            && carrier.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        // ---- RPC ----
        private static MessageWriter BeginRpc(byte subtype) {
            var w = UCRpc.Begin(RpcId);
            w.Write(subtype);
            return w;
        }

        public static void SendSet(byte id) {
            try {
                var w = BeginRpc(SubSet);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySet(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[SixthSense] SendSet failed: {e}"); }
        }

        private static void SendReady(bool isReady) {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null) return;
                var w = BeginRpc(SubReady);
                w.Write(me.PlayerId);
                w.Write(isReady);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyReady(me.PlayerId, isReady);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[SixthSense] SendReady failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSet: {
                        byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("SixthSense.Set")) ApplySet(id);
                        break;
                    }
                    case SubReady: {
                        byte id = reader.ReadByte();
                        bool isReady = reader.ReadBoolean();
                        // The sender must be the killer it reports about (or the host).
                        if (UCRpc.RequireOwnerOrHost(Helpers.playerById(id), "SixthSense.Ready")) ApplyReady(id, isReady);
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[SixthSense] HandleRpc failed: {e}");
            }
        }

        private static void ApplySet(byte id) {
            carrier = Helpers.playerById(id);
            active = carrier != null;
            carrierId = active ? id : byte.MaxValue;
            ready.Clear();
            localReadyKnown = false;    // every killer re-announces its state on the next tick
            if (active) {
                if (IsLocalCarrier()) UCRevealFx.PlayReveal();
                UnknownsCollectionPlugin.Logger?.LogInfo($"[SixthSense] Sixth Sense: {carrier.Data?.PlayerName}.");
            } else {
                HideVignette();
            }
        }

        private static void ApplyReady(byte id, bool isReady) {
            if (!active) return;
            ready[id] = isReady;
        }

        // ---- Pick (host; the modifier has no draft entry) ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPickPatch {
            public static void Postfix() {
                try {
                    if (!AmHost()) return;
                    if (active) return;   // forced by host tooling before the intro ended
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 5f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(IsModifierCandidate).ToList();
                    if (candidates.Count == 0) return;
                    SendSet(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[SixthSense] IntroEnd pick failed: {e}");
                }
            }
        }

        // Crew only (an impostor would only ever sense his own team); never on top of another modifier.
        private static bool IsModifierCandidate(PlayerControl p) {
            try {
                if (!UCPromotion.IsAlive(p) || p.Data.Role == null || p.Data.Role.IsImpostor) return false;
                var ri = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
                if (ri != null && ri.isNeutral) return false;
                if (UCPromotion.HasAnyModifier(p)) return false;
                return true;
            } catch { return false; }
        }

        // ---- Killer side: watch the own readiness, announce transitions ----
        private static FieldInfo jackalButtonField, sidekickButtonField;
        private static bool buttonFieldsTried;

        private static TheOtherRoles.Objects.CustomButton TorButton(ref FieldInfo field, string name) {
            try {
                if (!buttonFieldsTried) {
                    buttonFieldsTried = true;
                    var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.HudManagerStartPatch");
                    jackalButtonField = t?.GetField("jackalKillButton", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    sidekickButtonField = t?.GetField("sidekickKillButton", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                return field?.GetValue(null) as TheOtherRoles.Objects.CustomButton;
            } catch { return null; }
        }

        // null = the local player is no killer the sense reads; otherwise "kill ready right now".
        private static bool? LocalKillReady() {
            var me = PlayerControl.LocalPlayer;
            if (me?.Data == null || me.Data.IsDead) return null;
            try {
                if (me.Data.Role != null && me.Data.Role.IsImpostor) {
                    if (Janitor.janitor != null && Janitor.janitor.PlayerId == me.PlayerId) return null;
                    if (Mafioso.mafioso != null && Mafioso.mafioso.PlayerId == me.PlayerId
                        && Godfather.godfather != null && !Godfather.godfather.Data.IsDead) return null;
                    return me.killTimer <= 0f;
                }
                if (!(DetectsNeutralKillers?.getBool() ?? true)) return null;
                if (Jackal.jackal != null && Jackal.jackal.PlayerId == me.PlayerId) {
                    var b = TorButton(ref jackalButtonField, "jackalKillButton");
                    return b != null && b.Timer <= 0f;
                }
                if (Sidekick.sidekick != null && Sidekick.sidekick.PlayerId == me.PlayerId && Sidekick.canKill) {
                    var b = TorButton(ref sidekickButtonField, "sidekickKillButton");
                    return b != null && b.Timer <= 0f;
                }
            } catch { }
            return null;
        }

        private static float nextKillerPoll;

        private static void TickKiller() {
            if (!active) return;
            if (Time.time < nextKillerPoll) return;
            nextKillerPoll = Time.time + 0.2f;
            bool? state = LocalKillReady();
            if (state == null) {
                // Not a killer (any more): if we once announced "ready", take it back.
                if (localReadyKnown && localReadyState) { localReadyState = false; SendReady(false); }
                localReadyKnown = false;
                return;
            }
            if (!localReadyKnown || localReadyState != state.Value) {
                localReadyKnown = true;
                localReadyState = state.Value;
                SendReady(state.Value);
            }
        }

        // Cooldowns restart after every meeting; forget the old states, the killers re-announce.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                ready.Clear();
                localReadyKnown = false;
                HideVignette();
            }
        }

        // ---- Carrier side: the pulse ----
        private static SpriteRenderer vignette;
        private static Sprite vignetteSprite;

        private static Sprite BuildVignetteSprite(Sprite reference) {
            if (vignetteSprite != null) return vignetteSprite;
            if (reference == null || reference.pixelsPerUnit <= 0f) return null;
            try {
                float worldW = reference.rect.width / reference.pixelsPerUnit;
                float worldH = reference.rect.height / reference.pixelsPerUnit;
                if (worldW <= 0f || worldH <= 0f) return null;
                const int texH = 96;
                int texW = Mathf.Clamp(Mathf.RoundToInt(texH * (worldW / worldH)), 2, 400);
                float ppu = texH / worldH;
                var tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false);
                for (int x = 0; x < texW; x++)
                    for (int y = 0; y < texH; y++) {
                        float dx = (x - (texW - 1) / 2f) / (texW / 2f);
                        float dy = (y - (texH - 1) / 2f) / (texH / 2f);
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float alpha = Mathf.Clamp01((d - 0.62f) / 0.5f);
                        alpha *= alpha;
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                    }
                tex.Apply();
                tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
                vignetteSprite = Sprite.Create(tex, new Rect(0, 0, texW, texH), new Vector2(0.5f, 0.5f), ppu);
                vignetteSprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[SixthSense] vignette sprite build failed: {e.Message}");
            }
            return vignetteSprite;
        }

        // 0 = nothing near; otherwise the strongest signal in [0,1] over every ready killer in range.
        private static float Signal() {
            var me = PlayerControl.LocalPlayer;
            if (me == null) return 0f;
            float range = Mathf.Max(0.5f, Range?.getFloat() ?? 3f);
            bool grows = GrowsWithProximity?.getBool() ?? true;
            Vector2 mine = me.GetTruePosition();
            float best = 0f;
            foreach (var kv in ready) {
                if (!kv.Value) continue;
                var k = Helpers.playerById(kv.Key);
                if (!IsAlive(k) || k.PlayerId == me.PlayerId) continue;
                float d = Vector2.Distance(mine, k.GetTruePosition());
                if (d > range) continue;
                float s = grows ? Mathf.Clamp01(1f - d / range) * 0.75f + 0.25f : 1f;
                if (s > best) best = s;
            }
            return best;
        }

        private static float shown;    // smoothed signal, so the pulse eases in and out

        private static void TickCarrier() {
            var hud = HudManager.Instance;
            if (hud == null || hud.FullScreen == null) return;
            if (vignette != null && (vignette.gameObject == null || vignette.transform.parent != hud.transform))
                vignette = null;

            bool want = IsLocalCarrier() && !PlayerControl.LocalPlayer.Data.IsDead
                        && MeetingHud.Instance == null && ExileController.Instance == null
                        && ShipStatus.Instance != null;
            float target = want ? Signal() : 0f;
            shown = Mathf.MoveTowards(shown, target, Time.deltaTime * (target > shown ? 2.5f : 1.5f));
            if (shown <= 0.001f) {
                if (vignette != null && vignette.gameObject.activeSelf) vignette.gameObject.SetActive(false);
                return;
            }
            if (vignette == null) {
                var sprite = BuildVignetteSprite(hud.FullScreen.sprite);
                if (sprite == null) return;
                vignette = UnityEngine.Object.Instantiate(hud.FullScreen, hud.transform);
                vignette.name = "SixthSenseVignette";
                vignette.sprite = sprite;
                vignette.gameObject.SetActive(false);
            }
            if (!vignette.gameObject.activeSelf) vignette.gameObject.SetActive(true);
            vignette.enabled = true;
            // Heartbeat: a quick double-thump per ~1.1 s, stronger with the signal.
            float t = (Time.time * 0.9f) % 1f;
            float beat = Mathf.Max(Mathf.Exp(-Mathf.Pow((t - 0.10f) / 0.07f, 2f)),
                                   0.7f * Mathf.Exp(-Mathf.Pow((t - 0.32f) / 0.08f, 2f)));
            float alpha = shown * (0.16f + 0.30f * beat);
            vignette.color = new Color(Color.r, Color.g, Color.b, alpha);
        }

        private static void HideVignette() {
            shown = 0f;
            try { if (vignette != null) UnityEngine.Object.Destroy(vignette.gameObject); } catch { }
            vignette = null;
        }

        private static void Tick() {
            try {
                if (!active) return;
                TickKiller();
                TickCarrier();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[SixthSense] tick failed: {e.Message}");
            }
        }

        // ---- Role identity: APPEND, never replace - a modifier rides on top of the real role ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, [HarmonyArgument(1)] bool showModifier,
                                        ref List<RoleInfo> __result) {
                try {
                    if (!active || carrier == null || p == null || p != carrier || __result == null) return;
                    if (!showModifier) return;
                    if (!__result.Contains(Info())) __result.Add(Info());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[SixthSense] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            HideVignette();
            carrier = null;
            active = false;
            carrierId = byte.MaxValue;
            ready.Clear();
            localReadyKnown = false;
            localReadyState = false;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("SixthSense", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("SixthSense", FullReset);
        }
    }
}
