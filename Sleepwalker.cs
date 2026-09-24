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
 * room, (b) touches no solid ship collider (triggers ignored - room areas are triggers), (c) is
 * REACHABLE on foot from where the players stand, and (d) keeps the table distance. Sixty tries,
 * else no wake-up this round (logged). Submerged is off: its floors would need the floor switch.
 * (c) was added after a Polus round (User 24.09.) woke the Sleepwalker up outside the map: room
 * areas reach past the outer walls, walls are thin edge colliders and the void behind them has no
 * collider at all, so a point 1.3 m from a wall console could land behind the wall. The host now
 * floods a 0.5 m grid from every living player (a step may not cross a solid collider) once per
 * pick and only accepts cells that flood reached.
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
        private static Vector2? PickWakePosition(ReachGrid reachGiven = null) {
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
                var reach = reachGiven ?? BuildReach(ship);
                int tries = 0;
                foreach (var a in anchors) {
                    if (Vector2.Distance(a.pos, table) < minTable) continue;
                    for (int k = 0; k < 6 && tries < 60; k++) {
                        tries++;
                        float ang = (float)(rnd.NextDouble() * Math.PI * 2);
                        float r = a.spread * (0.25f + 0.75f * (float)rnd.NextDouble());
                        var p = a.pos + new Vector2(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r);
                        if (Vector2.Distance(p, table) < minTable) continue;
                        if (IsWalkable(p) && (reach == null || reach.Contains(p))) return p;
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

        // ---- Reachability (host): which floor can a player actually walk to? ----
        // Seeds are the living players (they stand around the table during the exile screen, which is
        // always floor) plus the meeting spawn centre. A cell is open when no solid collider touches a
        // 0.2 circle at its centre; a step to a neighbour is allowed when the straight line between
        // the centres hits no solid collider (the thin edge-collider walls). Doors are open at this
        // point (a meeting resets them). Bounded by the room areas, so the void is never flooded far.
        private sealed class ReachGrid {
            public const float Cell = 0.5f;
            public Vector2 Min;
            public int W, H;
            public bool[] Hit;
            public bool Contains(Vector2 p) {
                int x = Mathf.FloorToInt((p.x - Min.x) / Cell), y = Mathf.FloorToInt((p.y - Min.y) / Cell);
                if (x < 0 || y < 0 || x >= W || y >= H) return false;
                // the point itself or a direct neighbour: a candidate near a cell edge still counts
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++) {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= W || ny >= H || !Hit[ny * W + nx]) continue;
                        var c = Min + new Vector2((nx + 0.5f) * Cell, (ny + 0.5f) * Cell);
                        if (!Blocked(c, p)) return true;
                    }
                return false;
            }
        }

        private static bool Solid(Vector2 p, float r) {
            var hits = Physics2D.OverlapCircleAll(p, r, Constants.ShipAndObjectsMask);
            if (hits != null) foreach (var h in hits) if (h != null && !h.isTrigger) return true;
            return false;
        }

        private static bool Blocked(Vector2 a, Vector2 b) {
            var hits = Physics2D.LinecastAll(a, b, Constants.ShipAndObjectsMask);
            if (hits != null) foreach (var h in hits) if (h.collider != null && !h.collider.isTrigger) return true;
            return false;
        }

        private static ReachGrid BuildReach(ShipStatus ship) {
            try {
                if (ship.FastRooms == null || ship.FastRooms.Count == 0) return null;
                bool any = false;
                Bounds b = default;
                foreach (var room in ship.FastRooms.Values) {
                    if (room == null || room.roomArea == null) continue;
                    if (!any) { b = room.roomArea.bounds; any = true; } else b.Encapsulate(room.roomArea.bounds);
                }
                if (!any) return null;
                var g = new ReachGrid { Min = (Vector2)b.min - Vector2.one };
                g.W = Mathf.CeilToInt((b.size.x + 2f) / ReachGrid.Cell);
                g.H = Mathf.CeilToInt((b.size.y + 2f) / ReachGrid.Cell);
                if (g.W <= 0 || g.H <= 0 || g.W * g.H > 250000) return null;
                g.Hit = new bool[g.W * g.H];
                var open = new sbyte[g.W * g.H];                         // 0 unknown, 1 open, -1 solid
                Vector2 C(int x, int y) => g.Min + new Vector2((x + 0.5f) * ReachGrid.Cell, (y + 0.5f) * ReachGrid.Cell);
                bool IsOpen(int x, int y) {
                    int i = y * g.W + x;
                    if (open[i] == 0) open[i] = (sbyte)(Solid(C(x, y), 0.2f) ? -1 : 1);
                    return open[i] > 0;
                }
                var queue = new Queue<int>();
                var seeds = new List<Vector2> { ship.MeetingSpawnCenter };
                foreach (var pc in PlayerControl.AllPlayerControls)
                    if (pc != null && pc.Data != null && !pc.Data.IsDead && !pc.Data.Disconnected) seeds.Add(pc.GetTruePosition());
                foreach (var sp in seeds) {
                    int x = Mathf.FloorToInt((sp.x - g.Min.x) / ReachGrid.Cell), y = Mathf.FloorToInt((sp.y - g.Min.y) / ReachGrid.Cell);
                    if (x < 0 || y < 0 || x >= g.W || y >= g.H) continue;
                    int i = y * g.W + x;
                    if (g.Hit[i] || !IsOpen(x, y)) continue;
                    g.Hit[i] = true;
                    queue.Enqueue(i);
                }
                int count = 0;
                while (queue.Count > 0) {
                    int i = queue.Dequeue(), x = i % g.W, y = i / g.W;
                    count++;
                    for (int k = 0; k < 4; k++) {
                        int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= g.W || ny >= g.H) continue;
                        int j = ny * g.W + nx;
                        if (g.Hit[j] || !IsOpen(nx, ny) || Blocked(C(x, y), C(nx, ny))) continue;
                        g.Hit[j] = true;
                        queue.Enqueue(j);
                    }
                }
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Sleepwalker] reachable floor: {count} of {g.W * g.H} cells ({count * ReachGrid.Cell * ReachGrid.Cell:F0} m2)");
                return count > 20 ? g : null;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Sleepwalker] reachability grid failed, falling back to room test: {e.Message}");
                return null;
            }
        }

        // ---- Diagnose (nur Freeplay, Standard aus): Diagnostics/Sleepwalker Probe = 1 ----
        // Wuerfelt 200 Punkte mit der ALTEN Pruefung (Raum + kein fester Kollider) und zaehlt, wie viele
        // davon nicht erreichbar sind; setzt den Spieler dann auf bis zu zwei solche alten Fehlgriffe und
        // sechs Punkte der neuen Wahl und fotografiert jeden (UCShots/). Beweis fuer den Polus-Fix 24.09.
        internal static BepInEx.Configuration.ConfigEntry<int> DiagProbe;

        // Autostart fuer die Probe: Wert = MapNames + 1 (1 Skeld, 2 Mira, 3 Polus, 5 Airship, 6 Fungle).
        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        static class ProbeAutoStart {
            private static bool _fired;
            public static void Postfix(MainMenuManager __instance) {
                if (_fired || DiagProbe == null || DiagProbe.Value <= 0) return;
                _fired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, Run((MapNames)(DiagProbe.Value - 1)));
            }

            private static System.Collections.IEnumerator Run(MapNames map) {
                float t0 = Time.time;
                while (true) {
                    bool ok = false;
                    try { ok = EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow(); } catch { }
                    if (ok) break;
                    if (Time.time - t0 > 45f) { UnknownsCollectionPlugin.Logger?.LogError("[Sleepwalker] probe: EOS login timeout"); yield break; }
                    yield return null;
                }
                try {
                    var popover = UnityEngine.Object.FindObjectOfType<FreeplayPopover>(true);
                    if (popover == null) { UnknownsCollectionPlugin.Logger?.LogError("[Sleepwalker] probe: no FreeplayPopover"); yield break; }
                    for (var t = popover.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                    popover.Show();
                    popover.PlayMap(map);
                    UnknownsCollectionPlugin.Logger?.LogInfo($"[Sleepwalker] probe: freeplay started on {map}");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] probe autostart failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class ProbePatch {
            private static int _phase, _i;
            private static float _at;
            private static readonly List<(Vector2 P, string Tag)> Pts = new();

            public static void Postfix() {
                try {
                    if (DiagProbe == null || DiagProbe.Value <= 0) return;
                    var ac = AmongUsClient.Instance;
                    var me = PlayerControl.LocalPlayer;
                    if (ac == null || ac.NetworkMode != NetworkModes.FreePlay || ShipStatus.Instance == null || me == null) { _phase = 0; return; }
                    switch (_phase) {
                        // 30 s: auf Atlas-Karten setzt der Atlas-Autotest den Spieler vorher fuer seine Ansichtsfotos um
                        case 0: _at = Time.time + 30f; _phase = 1; break;
                        case 1:
                            if (Time.time < _at) return;
                            {
                                var sh = ShipStatus.Instance;
                                string dir0 = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "UCShots");
                                System.IO.Directory.CreateDirectory(dir0);
                                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir0, $"sleep_start_{DateTime.Now:HHmmss}.png"));
                                UnknownsCollectionPlugin.Logger?.LogInfo($"[Sleepwalker] probe: ship {sh.GetIl2CppType().Name}, map type {(int)sh.Type}, rooms {sh.FastRooms?.Count ?? 0}, submerged {SubmergedCompatibility.IsSubmerged}");
                            }
                            Probe(ShipStatus.Instance);
                            _i = 0; _at = Time.time; _phase = 2;
                            break;
                        case 2:
                            if (Time.time < _at) return;
                            if (_i >= Pts.Count) { UnknownsCollectionPlugin.Logger?.LogInfo("[Sleepwalker] probe done"); _phase = 4; return; }
                            // offene Minispiele (Atlas laesst nach seinen Ansichtsfotos die Kameras offen) zuerst schliessen
                            if (Minigame.Instance != null) { try { Minigame.Instance.ForceClose(); } catch { } _at = Time.time + 0.8f; return; }
                            me.NetTransform.RpcSnapTo(Pts[_i].P);
                            _at = Time.time + 1.2f; _phase = 3;
                            break;
                        case 3:
                            if (Time.time < _at) return;
                            string dir = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "UCShots");
                            System.IO.Directory.CreateDirectory(dir);
                            string file = System.IO.Path.Combine(dir, $"sleep_{_i}_{Pts[_i].Tag}_{DateTime.Now:HHmmss}.png");
                            ScreenCapture.CaptureScreenshot(file);
                            UnknownsCollectionPlugin.Logger?.LogInfo($"[Sleepwalker] probe shot {_i} {Pts[_i].Tag} ({Pts[_i].P.x:F1}, {Pts[_i].P.y:F1}) in {RoomNameAt(Pts[_i].P, false)} -> {file}");
                            _i++; _at = Time.time + 0.4f; _phase = 2;
                            break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] probe failed: {e}");
                    _phase = 4;
                }
            }

            private static void Probe(ShipStatus ship) {
                Pts.Clear();
                var reach = BuildReach(ship);
                var anchors = new List<(Vector2 pos, float spread)>();
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Console>()) if (c != null) anchors.Add((c.transform.position, 1.3f));
                if (ship.AllVents != null) foreach (var v in ship.AllVents) if (v != null) anchors.Add((v.transform.position, 0.4f));
                int accepted = 0, lost = 0, tries = 0;
                while (accepted < 200 && tries < 5000 && anchors.Count > 0) {
                    tries++;
                    var a = anchors[rnd.Next(anchors.Count)];
                    float ang = (float)(rnd.NextDouble() * Math.PI * 2);
                    float r = a.spread * (0.25f + 0.75f * (float)rnd.NextDouble());
                    var pt = a.pos + new Vector2(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r);
                    if (!IsWalkable(pt)) continue;
                    accepted++;
                    if (reach != null && !reach.Contains(pt)) {
                        lost++;
                        if (lost <= 2) Pts.Add((pt, "old_unreachable"));
                        if (lost <= 8) UnknownsCollectionPlugin.Logger?.LogInfo($"[Sleepwalker] probe: old test accepts unreachable ({pt.x:F1}, {pt.y:F1}) in {RoomNameAt(pt, false)}");
                    }
                }
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Sleepwalker] probe: old test accepted {accepted}, of those unreachable {lost} (reach grid {(reach != null ? "ok" : "none")})");
                // Verteilung der neuen Wahl ueber 300 Zuege, dann 12 Punkte zum Fotografieren
                var perRoom = new Dictionary<string, int>();
                int none = 0;
                for (int k = 0; k < 300; k++) {
                    var w = PickWakePosition(reach);
                    if (w == null) { none++; continue; }
                    string room = RoomNameAt(w.Value, false);
                    perRoom[room] = perRoom.TryGetValue(room, out var n) ? n + 1 : 1;
                    if (reach != null && !reach.Contains(w.Value))
                        UnknownsCollectionPlugin.Logger?.LogError($"[Sleepwalker] probe: NEW pick not reachable ({w.Value.x:F1}, {w.Value.y:F1})");
                    if (k < 12) Pts.Add((w.Value, "new"));
                }
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Sleepwalker] probe: 300 new picks, none {none}, rooms {perRoom.Count}: " +
                    string.Join(", ", perRoom.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
            }
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
