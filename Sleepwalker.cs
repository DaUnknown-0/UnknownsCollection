// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Sleepwalker (MODIFIER, the third one in this mod after the Gambler and the Void)
 *
 * He dozes off during the meeting and wakes up somewhere else. When the exile screen ends and
 * everyone walks away from the table, the Sleepwalker is not there: he stands in a random room of
 * the map (option: at least N units from the table), his screen fades in from black with a short
 * "You wake up in Electrical" line. Nobody else gets a cue - the others merely notice that one
 * player is missing at the table and, if they look at the map, a figure snapping elsewhere.
 * So he never has a start-of-round alibi ("we walked off together"), and he starts every round
 * alone. Option: a wake-up chance per meeting below 100 % keeps "I am the Sleepwalker" an
 * unreliable claim. Option: the same happens once at game start (off by default - the start spawn
 * is what UTS' Anti Start Kill protects, and a Sleepwalker away from it is on his own).
 *
 * WHO. Crew only by default (the Gambler/Void rule); option 1667 opens it to impostors or to
 * everyone. An impostor Sleepwalker is a real buff (alone next to a lone crewmate right after the
 * meeting, and a ready-made alibi), which is why that is the host's call, not the default.
 *
 * THE TELEPORT. TOR's Anti-Teleport modifier is the exact inverse, and it shows where the seams
 * are: the vanilla post-meeting spawn is done by each client for ITS OWN player inside
 * ExileController.WrapUp, so the only place a different position sticks is a WrapUp postfix on
 * the carrier's own client (RpcSnapTo - the vanilla SnapTo RPC then moves him on every other
 * client, no custom sync needed). The Airship picks its spawn in a minigame that closes AFTER
 * WrapUp, so SpawnInMinigame.Close gets the same postfix (TOR's AirshipSpawnInPatch pattern),
 * guarded by a short window so a spawn selection later in the round is never mistaken for it.
 *
 * THE POSITION is picked on the HOST (during the exile screen, so the RPC is long in before any
 * WrapUp runs) and logged with the room name - host-verifiable in the log, one source of truth.
 * Map-agnostic: anchors are every task console and vent (both exist on every map, custom ones
 * included), a candidate is a small random offset from an anchor that (a) is inside some ship
 * room, (b) touches no solid ship collider (triggers ignored - room areas are triggers), and (c)
 * keeps the table distance. Sixty tries, else no wake-up this round (logged). Submerged is off:
 * its floors would need the floor switch on top.
 *
 * ARCHITECTURE: modifier over any role (the Gambler pattern), host-authoritative pick, custom RPC
 * module 221 on UCRpc.CallId = 230, gated on "everyone has the mod". Options 1665-1670, display
 * RoleId sentinel 232 (Gambler 230, Void 231), no draft entry (modifiers are not drafted).
 * See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using TMPro;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Sleepwalker {
        // ---- Theme ----
        // Pillow peach: warm and pale, apart from the golds (Beacon/Collector), the amber Witness and
        // the pinks/purples (Copycat, Void, Poltergeist).
        public static readonly Color Color = new Color(0.98f, 0.68f, 0.58f);

        // ---- Options (IDs 1665-1670) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption WhoCanBe;            // 0 crew only, 1 crew & impostor, 2 anyone
        public static CustomOption WakeChance;          // % per meeting
        public static CustomOption MinTableDistance;
        public static CustomOption AtGameStart;

        // ---- Runtime state ----
        public static PlayerControl sleepwalker;
        public static bool active;
        private static byte sleepwalkerId = byte.MaxValue;
        private static Vector2 wakePos;
        private static bool wakeArmed;                  // host said "wake up at wakePos" - consume at the next spawn
        private static float wakeWindowUntil;           // after the first snap: re-snap window for the Airship spawn minigame

        // ---- Custom RPC subtypes: module byte 221 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = UnknownsCollectionPlugin.SleepwalkerRpcId;
        private const byte SubSet = 0;          // playerId (255 = clear)          host -> everyone
        private const byte SubWake = 1;         // playerId, float x, float y      host -> everyone

        private static readonly System.Random rnd = new System.Random();

        // ---- Identity (display-only sentinel RoleId, see Gambler / Void) ----
        private const RoleId SleepwalkerRoleId = (RoleId)232;
        private static RoleInfo sleepwalkerInfo;
        public static RoleInfo SleepwalkerInfo() => sleepwalkerInfo ??= new RoleInfo(
            "Sleepwalker", Color, "You never wake up where you fell asleep",
            "Wakes up somewhere else after each meeting", SleepwalkerRoleId, false, true);

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1665, Types.Modifier, "Sleepwalker",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1666, Types.Modifier, "Sleepwalker Minimum Players To Spawn",
                    5f, 4f, 15f, 1f, SpawnRate);
                WhoCanBe = CustomOption.Create(1667, Types.Modifier, "Sleepwalker Can Be",
                    new string[] { "Crew Only", "Crew & Impostor", "Anyone" }, SpawnRate);
                WakeChance = CustomOption.Create(1668, Types.Modifier, "Wake-Up Chance Per Meeting",
                    100f, 10f, 100f, 10f, SpawnRate);
                MinTableDistance = CustomOption.Create(1669, Types.Modifier, "Minimum Distance From The Table",
                    10f, 0f, 30f, 2f, SpawnRate);
                AtGameStart = CustomOption.Create(1670, Types.Modifier, "Also Sleepwalks At Game Start",
                    false, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Sleepwalker] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            UCRpc.Register(RpcId, HandleModuleRpc);
            UCFx.RegisterTick(TickFx);
            UCFx.RegisterReset(StopFx);
        }

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalSleepwalker() =>
            active && sleepwalker != null && PlayerControl.LocalPlayer != null
            && sleepwalker.PlayerId == PlayerControl.LocalPlayer.PlayerId;

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
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] SendSet failed: {e}"); }
        }

        private static void SendWake(byte id, Vector2 pos) {
            try {
                var w = BeginRpc(SubWake);
                w.Write(id);
                w.Write(pos.x);
                w.Write(pos.y);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyWake(id, pos);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] SendWake failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSet: {
                        byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("Sleepwalker.Set")) ApplySet(id);
                        break;
                    }
                    case SubWake: {
                        byte id = reader.ReadByte();
                        float x = reader.ReadSingle();
                        float y = reader.ReadSingle();
                        if (UCRpc.RequireHost("Sleepwalker.Wake")) ApplyWake(id, new Vector2(x, y));
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] HandleRpc failed: {e}");
            }
        }

        private static void ApplySet(byte id) {
            sleepwalker = Helpers.playerById(id);
            active = sleepwalker != null;
            sleepwalkerId = active ? id : byte.MaxValue;
            wakeArmed = false;
            wakeWindowUntil = 0f;
            if (active) {
                // A modifier rides on top of a role; no Claim(): the Sleepwalker may share a player
                // with a UC role, exactly like the Gambler. The tag is visible to its carrier from the
                // start (Mini/Giant family, not the hidden VIP/Bait/Bloody one), so the cue may play.
                if (IsLocalSleepwalker()) UCRevealFx.PlayReveal();
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Sleepwalker] The Sleepwalker is {sleepwalker.Data?.PlayerName}.");
            }
        }

        // Everyone stores the position (harmless), only the carrier acts on it. Outside a meeting or
        // exile screen (the game-start path) the carrier snaps at once; otherwise the next WrapUp does.
        private static void ApplyWake(byte id, Vector2 pos) {
            if (!active || id != sleepwalkerId) return;
            wakePos = pos;
            wakeArmed = true;
            if (!IsLocalSleepwalker()) return;
            if (MeetingHud.Instance == null && ExileController.Instance == null) Snap("game start");
        }

        // ---- Pick (host; the modifier has no draft entry) ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPickPatch {
            public static void Postfix() {
                try {
                    if (!AmHost()) return;
                    if (!active) {
                        if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                        if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                        if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 5f)) return;

                        int chance = SpawnRate.getSelection() * 10;
                        if (rnd.Next(1, 101) > chance) return;

                        var candidates = PlayerControl.AllPlayerControls.ToArray().Where(IsModifierCandidate).ToList();
                        if (candidates.Count == 0) return;
                        SendSet(candidates[rnd.Next(candidates.Count)].PlayerId);
                    }
                    // Forced by host tooling before the intro ended, or just picked: the start wake-up.
                    if (active && (AtGameStart?.getBool() ?? false)) HostRollWake("game start");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] IntroEnd pick failed: {e}");
                }
            }
        }

        // Option 1667 decides the pool; never on top of another modifier (TOR's, the Gambler or the
        // Void): one modifier per player, TOR's own rule (UCPromotion.HasAnyModifier).
        private static bool IsModifierCandidate(PlayerControl p) {
            try {
                if (!UCPromotion.IsAlive(p) || p.Data.Role == null) return false;
                if (UCPromotion.HasAnyModifier(p)) return false;
                int who = WhoCanBe?.getSelection() ?? 0;
                if (who == 2) return true;
                var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
                if (info != null && info.isNeutral) return false;
                if (who == 0 && p.Data.Role.IsImpostor) return false;
                return true;
            } catch { return false; }
        }

        // ---- The wake-up: rolled and placed on the host during the exile screen ----
        // The exiled player of THIS meeting is not dead yet at Begin (TOR marks him in WrapUp), so he is
        // excluded by id: a Sleepwalker who just got voted out does not get a wake-up position.
        [HarmonyPatch(typeof(ExileController), nameof(ExileController.BeginForGameplay))]
        [HarmonyPriority(Priority.Low)]
        static class ExileBeginPatch {
            public static void Postfix(ExileController __instance) {
                try {
                    if (!AmHost() || !active) return;
                    byte exiledId = byte.MaxValue;
                    try { exiledId = __instance?.initData?.networkedPlayer?.PlayerId ?? byte.MaxValue; } catch { }
                    if (exiledId == sleepwalkerId) return;
                    HostRollWake("meeting");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] exile-begin roll failed: {e}");
                }
            }
        }

        private static void HostRollWake(string why) {
            if (!IsAlive(sleepwalker)) return;
            int chance = (int)(WakeChance?.getFloat() ?? 100f);
            if (why != "game start" && rnd.Next(1, 101) > chance) {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Sleepwalker] {why}: stays at the table this time ({chance}% chance).");
                return;
            }
            var pos = PickWakePosition();
            if (pos == null) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Sleepwalker] {why}: no valid wake-up spot found - no wake-up.");
                return;
            }
            UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[Sleepwalker] {why}: {sleepwalker.Data?.PlayerName} wakes up in {RoomNameAt(pos.Value, false)} ({pos.Value.x:F1}, {pos.Value.y:F1}).");
            SendWake(sleepwalkerId, pos.Value);
        }

        // ---- Position search (host) ----
        private static Vector2? PickWakePosition() {
            try {
                var ship = ShipStatus.Instance;
                if (ship == null) return null;
                if (SubmergedCompatibility.IsSubmerged) return null;

                var anchors = new List<(Vector2 pos, float spread)>();
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Console>())
                    if (c != null) anchors.Add((c.transform.position, 1.3f));
                if (ship.AllVents != null)
                    foreach (var v in ship.AllVents)
                        if (v != null) anchors.Add((v.transform.position, 0.4f));
                if (anchors.Count == 0) return null;

                for (int i = anchors.Count - 1; i > 0; i--) {
                    int j = rnd.Next(i + 1);
                    (anchors[i], anchors[j]) = (anchors[j], anchors[i]);
                }

                float minTable = MinTableDistance?.getFloat() ?? 10f;
                Vector2 table = ship.MeetingSpawnCenter;
                int tries = 0;
                foreach (var a in anchors) {
                    if (Vector2.Distance(a.pos, table) < minTable) continue;
                    for (int k = 0; k < 6 && tries < 60; k++) {
                        tries++;
                        float ang = (float)(rnd.NextDouble() * Math.PI * 2);
                        float r = a.spread * (0.25f + 0.75f * (float)rnd.NextDouble());
                        var p = a.pos + new Vector2(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r);
                        if (Vector2.Distance(p, table) < minTable) continue;
                        if (IsWalkable(p)) return p;
                    }
                    if (tries >= 60) break;
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] position search failed: {e}");
            }
            return null;
        }

        // Inside some ship room (never outside the playable area; a map without room data skips that
        // test) and clear of every SOLID ship/object collider. Room areas themselves are trigger
        // colliders, so triggers are ignored.
        private static bool IsWalkable(Vector2 p) {
            bool hasRooms = false;
            try { hasRooms = ShipStatus.Instance?.FastRooms != null && ShipStatus.Instance.FastRooms.Count > 0; } catch { }
            if (hasRooms && RoomNameAt(p, true) == null) return false;
            var hits = Physics2D.OverlapCircleAll(p, 0.3f, Constants.ShipAndObjectsMask);
            if (hits != null)
                foreach (var h in hits)
                    if (h != null && !h.isTrigger) return false;
            return true;
        }

        // Name of the room containing `p` (null when none); translated for the HUD line, the raw
        // SystemTypes for the log.
        private static string RoomNameAt(Vector2 p, bool nullIfNone) {
            try {
                var ship = ShipStatus.Instance;
                if (ship != null && ship.FastRooms != null)
                    foreach (var room in ship.FastRooms.Values)
                        if (room != null && room.roomArea != null && room.roomArea.OverlapPoint(p))
                            return room.RoomId.ToString();
            } catch { }
            return nullIfNone ? null : "none";
        }

        private static string TranslatedRoomNameAt(Vector2 p) {
            try {
                var ship = ShipStatus.Instance;
                if (ship != null && ship.FastRooms != null)
                    foreach (var room in ship.FastRooms.Values)
                        if (room != null && room.roomArea != null && room.roomArea.OverlapPoint(p))
                            return DestroyableSingleton<TranslationController>.Instance.GetString(room.RoomId);
            } catch { }
            return null;
        }

        // ---- The snap (carrier's own client) ----
        // Priority.Last: after vanilla's own spawn AND after TOR's WrapUpPostfix (Anti-Teleport etc.).
        [HarmonyPatch(typeof(ExileController), nameof(ExileController.WrapUp))]
        [HarmonyPriority(Priority.Last)]
        static class ExileWrapUpPatch {
            public static void Postfix() {
                try {
                    if (!wakeArmed || !IsLocalSleepwalker()) return;
                    Snap("meeting");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] wrap-up snap failed: {e}");
                }
            }
        }

        // The Airship overrides WrapUp with its own WrapUpAndSpawn (TOR patches both as well).
        [HarmonyPatch(typeof(AirshipExileController), nameof(AirshipExileController.WrapUpAndSpawn))]
        [HarmonyPriority(Priority.Last)]
        static class AirshipWrapUpPatch {
            public static void Postfix() {
                try {
                    if (!wakeArmed || !IsLocalSleepwalker()) return;
                    Snap("meeting (airship)");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] airship wrap-up snap failed: {e}");
                }
            }
        }

        // Airship: the spawn choice closes AFTER WrapUp and puts the player at the chosen spot, so the
        // snap is repeated once more within the window the first snap opened (game start included).
        [HarmonyPatch(typeof(SpawnInMinigame), nameof(SpawnInMinigame.Close))]
        static class AirshipSpawnInPatch {
            public static void Postfix() {
                try {
                    if (!IsLocalSleepwalker()) return;
                    if (wakeArmed) { Snap("spawn choice"); return; }
                    // Re-snap AND restart the fade: this is the moment the room actually becomes visible.
                    if (Time.time < wakeWindowUntil) { ReSnap(); StartFx(); }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] spawn-choice snap failed: {e}");
                }
            }
        }

        // A new meeting cancels anything still armed or in its window.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                wakeArmed = false;
                wakeWindowUntil = 0f;
                StopFx();
            }
        }

        private static void Snap(string why) {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.Data == null || me.Data.IsDead) { wakeArmed = false; return; }
            if (me.MyPhysics == null || me.NetTransform == null) return;
            if (me.inMovingPlat) return;    // the Airship gap platform: never mid-ride, try again on Close
            wakeArmed = false;
            // Generous: the Airship spawn choice is open for a while before it auto-picks, and a new
            // meeting closes the window anyway. Nothing else opens a SpawnInMinigame mid-round.
            wakeWindowUntil = Time.time + 30f;
            ReSnap();
            StartFx();
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Sleepwalker] {why}: woke up at ({wakePos.x:F1}, {wakePos.y:F1}).");
        }

        private static void ReSnap() {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.NetTransform == null) return;
            me.NetTransform.RpcSnapTo(wakePos);
            // TOR clears this after its own post-spawn teleports: the snap must not count as movement
            // for the Chameleon.
            try { if (Chameleon.lastMoved != null) Chameleon.lastMoved.Remove(me.PlayerId); } catch { }
        }

        // ---- Wake-up FX (carrier only): fade in from black + one line ----
        // Own overlay cloned from HudManager.FullScreen (the Poltergeist hex vignette technique), so
        // this never touches TOR's shared FullScreen renderer - Helpers.showFlash disables that one
        // when it ends, and other flashes may be running.
        private static SpriteRenderer blackOverlay;
        private static TextMeshPro wakeText;
        private static float fxStart;
        private static bool fxRunning;
        private const float HoldBlack = 0.35f;
        private const float FadeBlack = 0.9f;
        private const float TextLife = 3.0f;

        private static void StartFx() {
            StopFx();
            try {
                var hud = FastDestroyableSingleton<HudManager>.Instance;
                if (hud == null || hud.FullScreen == null) return;
                blackOverlay = UnityEngine.Object.Instantiate(hud.FullScreen, hud.transform);
                blackOverlay.name = "SleepwalkerWakeOverlay";
                blackOverlay.gameObject.SetActive(true);
                blackOverlay.enabled = true;
                blackOverlay.color = new Color(0f, 0f, 0f, 1f);

                string room = TranslatedRoomNameAt(wakePos);
                string line = string.IsNullOrEmpty(room)
                    ? UCLocalization.Tr("uc.ui.sleepwalker.wake_nowhere")
                    : string.Format(UCLocalization.Tr("uc.ui.sleepwalker.wake"), room);
                if (hud.KillButton != null && hud.KillButton.cooldownTimerText != null) {
                    wakeText = UnityEngine.Object.Instantiate(hud.KillButton.cooldownTimerText, hud.transform);
                    wakeText.name = "SleepwalkerWakeText";
                    wakeText.text = line;
                    wakeText.enableWordWrapping = false;
                    wakeText.transform.localScale = Vector3.one * 0.5f;
                    wakeText.transform.localPosition += new Vector3(0f, 2f, -69f);
                    wakeText.color = new Color(Color.r, Color.g, Color.b, 0f);
                    wakeText.gameObject.SetActive(true);
                }
                fxStart = Time.time;
                fxRunning = true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Sleepwalker] wake FX failed: {e.Message}");
                StopFx();
            }
        }

        private static void TickFx() {
            if (!fxRunning) return;
            try {
                float t = Time.time - fxStart;
                if (blackOverlay != null) {
                    float a = t < HoldBlack ? 1f : 1f - Mathf.Clamp01((t - HoldBlack) / FadeBlack);
                    // ease-out so the room "arrives" softly
                    a = a * a;
                    blackOverlay.color = new Color(0f, 0f, 0f, a);
                    if (a <= 0f) { UnityEngine.Object.Destroy(blackOverlay.gameObject); blackOverlay = null; }
                }
                if (wakeText != null) {
                    float a = Mathf.Clamp01(t / 0.5f) * (1f - Mathf.Clamp01((t - (TextLife - 0.7f)) / 0.7f));
                    wakeText.color = new Color(Color.r, Color.g, Color.b, a);
                    if (t >= TextLife) { UnityEngine.Object.Destroy(wakeText.gameObject); wakeText = null; }
                }
                if (blackOverlay == null && wakeText == null) fxRunning = false;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Sleepwalker] wake FX tick failed: {e.Message}");
                StopFx();
            }
        }

        private static void StopFx() {
            fxRunning = false;
            try { if (blackOverlay != null) UnityEngine.Object.Destroy(blackOverlay.gameObject); } catch { }
            try { if (wakeText != null) UnityEngine.Object.Destroy(wakeText.gameObject); } catch { }
            blackOverlay = null;
            wakeText = null;
        }

        // ---- Role identity: APPEND, never replace - a modifier rides on top of the real role ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, [HarmonyArgument(1)] bool showModifier,
                                        ref List<RoleInfo> __result) {
                try {
                    if (!active || sleepwalker == null || p == null || p != sleepwalker || __result == null) return;
                    if (!showModifier) return;
                    if (!__result.Contains(SleepwalkerInfo())) __result.Add(SleepwalkerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            StopFx();
            sleepwalker = null;
            active = false;
            sleepwalkerId = byte.MaxValue;
            wakeArmed = false;
            wakeWindowUntil = 0f;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Sleepwalker", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("Sleepwalker", FullReset);
        }
    }
}
