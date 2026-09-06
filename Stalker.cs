// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Stalker (Neutral)
 *
 * One target, assigned at the start. The Stalker has to WATCH that target for a configured amount
 * of time without being seen. He keeps his normal crew vision and on top of it carries a narrow
 * torch cone (the vanilla flashlight, the same piece TOR's Lighter uses) that reaches further than
 * crew vision - the target has to stand INSIDE that cone. The clock only runs while
 *
 *   1. the target is in the cone (distance <= cone reach, angle inside the cone, clear line of
 *      sight), and
 *   2. NOBODY alive could see the Stalker with STANDARD crew vision - every living player, the
 *      target included, checked against the crew light radius (sabotaged lights shrink it, so a
 *      blackout is the Stalker's friend) plus a wall raycast. Impostors usually see further than
 *      crew, but the rule deliberately uses the crew radius so the Stalker can reason about it.
 *
 * The sweet spot is therefore the ring between the crew radius and the cone reach, in line of
 * sight: close enough to watch, far enough not to be watched.
 *
 * THE TARGET FEELS IT. Every 15 seconds the Stalker's client sends the remaining time; the target
 * (and only the target) sees a "stalk meter" on the HUD. Option 1645 decides whether that meter is
 * shown never, only once half the time is done, or always.
 *
 * AT 100 % THE STALKER STRIKES. He gets a kill button (target only, own cooldown) - but any death
 * of the target after 100 % counts: his kill, an ejection at a meeting, a guess by anyone. The
 * moment the target is dead the Stalker wins ALONE and the game ends (own GameOverReason 34).
 *
 * IF THE TARGET DIES EARLY (before 100 %) the Stalker is finished: by default he becomes TOR's
 * Pursuer (the Lawyer's fallback role - the same promotion path), optionally he gets a new target
 * and keeps his progress instead. A target that merely disconnects is replaced either way.
 *
 * WHO COMPUTES WHAT
 *   - The stalking clock runs on the STALKER'S OWN CLIENT: only he knows where his cone points,
 *     and every position he needs is replicated to him anyway. He broadcasts the meter (15 s) and
 *     the moment of completion. Nothing else is trusted from him; the strike itself runs through
 *     TOR's regular kill funnel (checkMuderAttempt + MurderPlayer, the Sheriff/Hunter shape), so
 *     every shield applies.
 *   - The HOST watches the target's fate and ends the game / promotes the Stalker.
 *
 * THE CONE, TECHNICALLY. The vanilla flashlight REPLACES the vision circle with a cone plus a tiny
 * ambient disc (LightSource.PlayerRadius, read-only) - that is not "normal vision plus a cone". And
 * TOR hard-codes that flashlight to the Lighter anyway (prefixes returning false on
 * IsFlashlightEnabled / AdjustLighting). So the Stalker's own light source is left completely alone
 * (normal crew vision, TOR's rules) and the cone is a SECOND light: a clone of the player's
 * LightSource, parented to him, switched into flashlight mode with SetupLightingForGameplay and fed
 * the cone reach through SetViewDistance every frame. Both lights draw into the same lighting
 * buffer, so the player sees the union: his circle and the long narrow cone. The cone direction for
 * the CLOCK is read back from that clone (lastFlashlightDirection), so what the player sees and what
 * counts are one thing. Cone width: option 1644 is the vanilla flashlight width (0.1-1.0, 1.0 = full
 * circle); the clock treats it as the fraction of the full circle, i.e. half-angle = width * 180
 * degrees. Should the clone ever fail to render (untested engine corner), the clock still runs on
 * pure geometry - only the visual is lost, and the log says so.
 *
 * ARCHITECTURE mirrors Necromancer/Collector: neutral tag over a plain Crewmate, host-authoritative
 * pick, custom RPC module 218 on UCRpc.CallId = 230, gated on "everyone has the mod".
 * Options 1640-1649, draft sentinel 220, win reason 34, WinCondition banner 15. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using TMPro;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Stalker {
        // ---- Theme ----
        // Night indigo: darker and greyer than the Poltergeist's spectral violet and the Copycat's
        // pink-purple, so the three never read as the same tag at name size.
        public static readonly Color Color = new Color(0.40f, 0.38f, 0.65f);

        // ---- Options (IDs 1640-1649) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption StalkTime;        // seconds of unseen watching needed
        public static CustomOption ConeReach;        // x crew vision (1.5-2.0)
        public static CustomOption ConeWidth;        // vanilla flashlight width
        public static CustomOption MeterMode;        // Never / From 50% / Always
        public static CustomOption StrikeCooldown;
        public static CustomOption TargetDeath;      // Becomes Pursuer / New Target
        public static CustomOption HasTasks;
        public static CustomOption CanVent;

        // ---- Runtime state ----
        public static PlayerControl stalker;
        public static bool active;
        private static byte stalkerPlayerId = byte.MaxValue;
        private static byte targetId = byte.MaxValue;
        // The clock (Stalker's own client). Mirrored coarsely to everyone through the meter RPC.
        private static float progress;               // seconds of unseen stalking so far
        private static bool complete;                // 100 % reached (synced via SubComplete)
        private static float nextMeterSend;
        private static float lastMeterSent = -1f;
        // What the other clients know (target HUD + host bookkeeping).
        private static int meterRemaining = -1;      // seconds, -1 = nothing received yet
        private static int meterPercent = 0;
        // Per-frame diagnostics for the button label (owner only).
        private static bool inConeNow;
        private static bool seenNow;
        private static PlayerControl strikeTarget;

        private const int StalkerWinReason = 34;     // Necromancer uses 33
        private static readonly List<byte> winnerIds = new List<byte>(); // survives resetVariables
        private static float nextWinTry;
        private static float nextHostTick;
        private static float nextClockTick;
        private static float lastClockAt = -1f;

        // ---- Custom RPC subtypes: module byte 218 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = UnknownsCollectionPlugin.StalkerRpcId;
        private const byte SubSet = 0;          // stalkerId (255 = clear), targetId   host -> everyone
        private const byte SubMeter = 1;        // remaining seconds (byte), percent    stalker -> everyone
        private const byte SubComplete = 2;     // -                                    stalker -> everyone
        private const byte SubFallback = 3;     // mode (0 = pursuer)                   host -> everyone
        private const byte SubSetTarget = 4;    // targetId                             host -> everyone

        private static readonly System.Random rnd = new System.Random();

        // ---- Role identity ----
        private static RoleInfo stalkerInfo;
        public static RoleInfo StalkerInfo() => stalkerInfo ??= new RoleInfo(
            "Stalker", Color, "Watch your target unseen, then strike",
            "Stalk your target", RoleId.Crewmate)
        { isNeutral = true };

        private static TheOtherRoles.Objects.CustomButton strikeButton;

        // TOR's Create(float) accumulates step errors for non-binary steps (0.05 / 0.1): pre-rounded
        // selections keep the display clean and the default on the right index.
        private static object[] FloatRange(float min, float max, float step) {
            var sels = new List<object>();
            for (double s = min; s <= max + step * 0.5; s += step) sels.Add((float)Math.Round(s, 2));
            return sels.ToArray();
        }

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1640, Types.Neutral, "Stalker",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1641, Types.Neutral, "Stalker Minimum Players To Spawn",
                    7f, 4f, 15f, 1f, SpawnRate);
                StalkTime = CustomOption.Create(1642, Types.Neutral, "Stalking Time Needed",
                    90f, 30f, 300f, 10f, SpawnRate);
                ConeReach = new CustomOption(1643, Types.Neutral, "Stalk Cone Reach (x Crew Vision)",
                    FloatRange(1.5f, 2f, 0.25f), 1.75f, SpawnRate, false);
                ConeWidth = new CustomOption(1644, Types.Neutral, "Stalk Cone Width",
                    FloatRange(0.1f, 0.5f, 0.05f), 0.2f, SpawnRate, false);
                MeterMode = CustomOption.Create(1645, Types.Neutral, "Target Sees The Stalk Meter",
                    new string[] { "Always", "From 50%", "Never" }, SpawnRate);
                StrikeCooldown = CustomOption.Create(1646, Types.Neutral, "Strike Cooldown",
                    10f, 5f, 60f, 5f, SpawnRate);
                TargetDeath = CustomOption.Create(1647, Types.Neutral, "If The Target Dies Before 100%",
                    new string[] { "Stalker Becomes Pursuer", "New Target, Progress Kept" }, SpawnRate);
                HasTasks = CustomOption.Create(1648, Types.Neutral, "Stalker Has Tasks",
                    false, SpawnRate);
                CanVent = CustomOption.Create(1649, Types.Neutral, "Stalker Can Use Vents",
                    false, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Stalker] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            UCRpc.Register(RpcId, HandleModuleRpc);
        }

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        public static bool IsLocalStalker() =>
            active && stalker != null && PlayerControl.LocalPlayer != null
            && stalker.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static bool IsLocalTarget() =>
            active && targetId != byte.MaxValue && PlayerControl.LocalPlayer != null
            && PlayerControl.LocalPlayer.PlayerId == targetId;
        private static PlayerControl Target() => targetId == byte.MaxValue ? null : Helpers.playerById(targetId);
        private static float NeedSeconds() => StalkTime?.getFloat() ?? 90f;
        private static float Reach() => ConeReach?.getFloat() ?? 1.75f;
        private static float Width() => Mathf.Clamp(ConeWidth?.getFloat() ?? 0.2f, 0.1f, 1f);
        private static int Percent() => Mathf.Clamp(Mathf.FloorToInt(progress / Mathf.Max(1f, NeedSeconds()) * 100f), 0, 100);

        // Standard crew vision under the CURRENT light conditions (sabotage-aware, Submerged-aware):
        // TOR's own helper, the same number every crewmate's circle is drawn from.
        private static float CrewRadius() {
            try {
                var ship = MapUtilities.CachedShipStatus;
                if (ship == null) return 0f;
                return ShipStatusPatch.GetNeutralLightRadius(ship, false);
            } catch { return 0f; }
        }

        // Can `viewer` see world point `at` with STANDARD crew vision? (Witness.CanSee, with the
        // fixed crew radius instead of the role's own sight.)
        private static bool CrewCanSee(PlayerControl viewer, Vector2 at, float crewRadius) {
            if (!IsAlive(viewer) || viewer.inVent) return false;
            Vector2 from = viewer.GetTruePosition();
            Vector2 dir = at - from;
            float mag = dir.magnitude;
            if (mag > crewRadius) return false;
            if (mag < 0.05f) return true;
            return !PhysicsHelpers.AnyNonTriggersBetween(from, dir.normalized, mag, Constants.ShipAndObjectsMask);
        }

        // ---- RPC ----
        private static MessageWriter BeginRpc(byte subtype) {
            var w = UCRpc.Begin(RpcId);
            w.Write(subtype);
            return w;
        }

        public static void SendSet(byte id, byte target) {
            try {
                var w = BeginRpc(SubSet);
                w.Write(id);
                w.Write(target);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySet(id, target);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] SendSet failed: {e}"); }
        }

        private static void SendMeter(int remaining, int percent) {
            try {
                var w = BeginRpc(SubMeter);
                w.Write((byte)Mathf.Clamp(remaining, 0, 255));
                w.Write((byte)Mathf.Clamp(percent, 0, 100));
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyMeter(remaining, percent);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] SendMeter failed: {e}"); }
        }

        private static void SendComplete() {
            try {
                var w = BeginRpc(SubComplete);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyComplete();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] SendComplete failed: {e}"); }
        }

        private static void SendFallback(byte mode) {
            try {
                var w = BeginRpc(SubFallback);
                w.Write(mode);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyFallback(mode);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] SendFallback failed: {e}"); }
        }

        private static void SendSetTarget(byte id) {
            try {
                var w = BeginRpc(SubSetTarget);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetTarget(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] SendSetTarget failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSet: {
                        byte id = reader.ReadByte();
                        byte target = reader.ReadByte();
                        if (UCRpc.RequireHost("Stalker.Set")) ApplySet(id, target);
                        break;
                    }
                    case SubMeter: {
                        int remaining = reader.ReadByte();
                        int percent = reader.ReadByte();
                        if (UCRpc.RequireOwnerOrHost(stalker, "Stalker.Meter")) ApplyMeter(remaining, percent);
                        break;
                    }
                    case SubComplete: {
                        if (UCRpc.RequireOwnerOrHost(stalker, "Stalker.Complete")) ApplyComplete();
                        break;
                    }
                    case SubFallback: {
                        byte mode = reader.ReadByte();
                        if (UCRpc.RequireHost("Stalker.Fallback")) ApplyFallback(mode);
                        break;
                    }
                    case SubSetTarget: {
                        byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("Stalker.SetTarget")) ApplySetTarget(id);
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] HandleRpc failed: {e}");
            }
        }

        private static void ApplySet(byte id, byte target) {
            stalker = Helpers.playerById(id);
            active = stalker != null;
            stalkerPlayerId = active ? id : byte.MaxValue;
            progress = 0f;
            complete = false;
            meterRemaining = -1;
            meterPercent = 0;
            lastMeterSent = -1f;
            nextMeterSend = Time.time + 15f;
            if (!active) { targetId = byte.MaxValue; return; }
            UCPromotion.Claim(id);
            ApplySetTarget(target);
            UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[Stalker] The Stalker is {stalker.Data?.PlayerName}, target {Target()?.Data?.PlayerName ?? "?"}.");
        }

        private static void ApplySetTarget(byte id) {
            targetId = id;
            meterRemaining = -1;
            meterPercent = 0;
            lastMeterSent = -1f;
            var t = Target();
            if (IsLocalStalker() && t != null && t.Data != null) {
                try {
                    var hud = FastDestroyableSingleton<HudManager>.Instance;
                    if (hud != null && hud.Chat != null)
                        hud.Chat.AddChat(PlayerControl.LocalPlayer,
                            UCLocalization.Tr("uc.ui.stalker.target_assigned", t.Data.PlayerName));
                } catch { }
            }
        }

        private static void ApplyMeter(int remaining, int percent) {
            meterRemaining = remaining;
            meterPercent = percent;
        }

        private static void ApplyComplete() {
            if (complete) return;
            complete = true;
            meterRemaining = 0;
            meterPercent = 100;
            ForceConeOff();
            if (IsLocalStalker()) {
                Helpers.showFlash(Color, 1f, UCLocalization.Tr("uc.ui.stalker.flash_ready"));
                if (strikeButton != null) {
                    strikeButton.MaxTimer = Mathf.Max(0.1f, StrikeCooldown?.getFloat() ?? 10f);
                    strikeButton.Timer = 0f;   // the first strike is available at once
                }
            }
            UnknownsCollectionPlugin.Logger?.LogInfo("[Stalker] 100 % - the Stalker is ready to strike.");
        }

        // The target died too early: either TOR's Pursuer promotion (Lawyer path) or a fresh target.
        private static void ApplyFallback(byte mode) {
            if (!active) return;
            if (mode == 0) {
                var p = stalker;
                bool wasLocal = IsLocalStalker();
                active = false;
                ForceConeOff();
                try { Pursuer.pursuer = p; } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] pursuer promotion failed: {e}"); }
                if (wasLocal) {
                    Helpers.showFlash(Pursuer.color, 1f);
                    try {
                        var hud = FastDestroyableSingleton<HudManager>.Instance;
                        if (hud != null && hud.Chat != null)
                            hud.Chat.AddChat(PlayerControl.LocalPlayer, UCLocalization.Tr("uc.ui.stalker.became_pursuer"));
                    } catch { }
                }
                UnknownsCollectionPlugin.Logger?.LogInfo("[Stalker] target died early - the Stalker is the Pursuer now.");
            }
        }

        public static void MarkFromDraft(byte playerId) {
            // The draft mark runs on every client with no host round-trip; the target is chosen by
            // the host right after (HostPickTargetIfMissing), so the role is complete once the RPC
            // lands. Until then targetId stays 255 and the clock simply does not run.
            ApplySet(playerId, byte.MaxValue);
        }

        // ---- Pick (host, random path - the draft path goes through MarkFromDraft) ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPickPatch {
            public static void Postfix() {
                try {
                    if (!AmHost()) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 7f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainCrewmate).ToList();
                    if (candidates.Count == 0) return;
                    var pick = candidates[rnd.Next(candidates.Count)];
                    byte target = PickTarget(pick.PlayerId);
                    if (target == byte.MaxValue) return;
                    SendSet(pick.PlayerId, target);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] IntroEnd pick failed: {e}");
                }
            }
        }

        private static byte PickTarget(byte exclude) {
            var pool = PlayerControl.AllPlayerControls.ToArray()
                .Where(p => IsAlive(p) && p.PlayerId != exclude).ToList();
            if (pool.Count == 0) return byte.MaxValue;
            return pool[rnd.Next(pool.Count)].PlayerId;
        }

        // Draft picks arrive without a target (see MarkFromDraft): the host fills it in on its first
        // tick after the intro. Also the replacement path for a disconnected target.
        private static void HostPickTargetIfMissing() {
            if (!AmHost() || !active || stalker == null) return;
            var t = Target();
            bool missing = targetId == byte.MaxValue || t == null || t.Data == null || t.Data.Disconnected;
            if (!missing) return;
            byte next = PickTarget(stalkerPlayerId);
            if (next == byte.MaxValue) return;
            SendSetTarget(next);
        }

        // ---- Strike button ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = UCAssets.StalkerIcon
                        ?? (__instance.KillButton != null && __instance.KillButton.graphic != null
                            ? __instance.KillButton.graphic.sprite : null);
                    strikeButton = new TheOtherRoles.Objects.CustomButton(
                        OnStrikeClick,
                        () => IsLocalStalker()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => complete && strikeTarget != null && PlayerControl.LocalPlayer.CanMove,
                        () => { if (strikeButton != null && complete) strikeButton.Timer = strikeButton.MaxTimer; },
                        sprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowRight,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.stalker.button_stalk", 0));
                    strikeButton.MaxTimer = 1f;
                    strikeButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] Button creation failed: {e}");
                }
            }
        }

        // Target only, through TOR's kill funnel (the Hunter/Sheriff shape): shields, rewinds and
        // armor behave exactly as for any other special kill.
        private static void OnStrikeClick() {
            try {
                if (!IsLocalStalker() || !complete || strikeTarget == null) return;
                var target = strikeTarget;
                if (target.PlayerId != targetId) return;

                MurderAttemptResult result = Helpers.checkMuderAttempt(stalker, target);
                if (result == MurderAttemptResult.SuppressKill) return;
                if (result == MurderAttemptResult.PerformKill)
                    Helpers.MurderPlayer(stalker, target, true);

                if (strikeButton != null) strikeButton.Timer = strikeButton.MaxTimer;
                strikeTarget = null;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] strike failed: {e}");
            }
        }

        // ---- Per-frame driver ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (!active) { HideMeter(); return; }

                    if (IsLocalStalker()) {
                        ClockTick();
                        StrikeTargetTick();
                        ButtonLabelTick();
                        TickCone();
                    }
                    NameColorTick();
                    MeterTick();

                    if (!AmHost()) return;
                    if (Time.realtimeSinceStartup < nextHostTick) return;
                    nextHostTick = Time.realtimeSinceStartup + 0.5f;
                    HostPickTargetIfMissing();
                    HostFateTick();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] HudUpdate failed: {e}");
                }
            }
        }

        // The clock (owner only). Runs at ~10 Hz - the geometry checks (one raycast per living
        // player) are not free, and a tenth of a second of clock granularity is invisible.
        private static void ClockTick() {
            if (complete) { inConeNow = false; seenNow = false; return; }
            if (Time.time < nextClockTick) return;
            // Elapsed since the previous tick, capped so a hitch (or the first tick) never credits
            // a whole second of stalking at once.
            float dt = lastClockAt < 0f ? 0f : Mathf.Min(0.25f, Time.time - lastClockAt);
            lastClockAt = Time.time;
            nextClockTick = Time.time + 0.1f;

            inConeNow = false;
            seenNow = false;
            if (InMeeting() || !IsAlive(stalker) || stalker.inVent) return;
            var target = Target();
            if (!IsAlive(target) || target.inVent) return;

            float crewRadius = CrewRadius();
            if (crewRadius <= 0f) return;

            Vector2 me = stalker.GetTruePosition();
            Vector2 them = target.GetTruePosition();
            Vector2 toTarget = them - me;
            float dist = toTarget.magnitude;

            // 1. target inside the cone: distance, angle, line of sight
            float reach = crewRadius * Reach();
            if (dist <= reach && dist > 0.05f) {
                Vector2 dir = ConeDirection();
                float halfAngle = Width() * 180f;
                if (Vector2.Angle(dir, toTarget) <= halfAngle
                    && !PhysicsHelpers.AnyNonTriggersBetween(me, toTarget.normalized, dist, Constants.ShipAndObjectsMask))
                    inConeNow = true;
            }
            if (!inConeNow) return;

            // 2. nobody (target included) could see the Stalker with standard crew vision
            foreach (var p in PlayerControl.AllPlayerControls) {
                if (p == null || p.PlayerId == stalkerPlayerId) continue;
                if (CrewCanSee(p, me, crewRadius)) { seenNow = true; break; }
            }
            if (seenNow) return;

            progress += dt;
            if (progress >= NeedSeconds()) {
                progress = NeedSeconds();
                SendComplete();
                return;
            }

            // Meter broadcast every 15 s (only while the option lets the target see it - a message
            // nobody displays would still be sniffable, so it is simply not sent).
            if (Time.time >= nextMeterSend) {
                nextMeterSend = Time.time + 15f;
                int pct = Percent();
                int mode = MeterMode?.getSelection() ?? 0;
                bool allowed = mode == 0 || (mode == 1 && pct >= 50);
                if (allowed) {
                    int remaining = Mathf.CeilToInt(NeedSeconds() - progress);
                    SendMeter(remaining, pct);
                    lastMeterSent = Time.time;
                }
            }
        }

        // Where the cone points: the cone light keeps the last flashlight direction (mouse or stick,
        // whatever SetFlashlightInputMethod chose); fall back to the sprite's facing.
        private static Vector2 ConeDirection() {
            try {
                if (coneLight != null) {
                    Vector2 d = coneLight.lastFlashlightDirection;
                    if (d.sqrMagnitude > 0.0001f) return d.normalized;
                }
            } catch { }
            try {
                bool flipped = PlayerControl.LocalPlayer.cosmetics.currentBodySprite.BodySprite.flipX;
                return flipped ? Vector2.left : Vector2.right;
            } catch { return Vector2.right; }
        }

        private static void StrikeTargetTick() {
            strikeTarget = null;
            if (!complete || InMeeting() || !IsAlive(stalker)) return;
            var target = Target();
            if (!IsAlive(target)) return;
            // TOR's kill-range/line-of-sight probe, restricted to the one legal victim.
            var untargetable = PlayerControl.AllPlayerControls.ToArray()
                .Where(p => p != null && p.PlayerId != targetId).ToList();
            strikeTarget = PlayerControlFixedUpdatePatch.setTarget(false, false, untargetable);
            if (strikeTarget != null) PlayerControlFixedUpdatePatch.setPlayerOutline(strikeTarget, Color);
        }

        private static void ButtonLabelTick() {
            if (strikeButton == null || !UCLabelThrottle.Due("stalker.button")) return;
            if (complete) {
                strikeButton.buttonText = UCLocalization.Tr("uc.ui.stalker.button_strike");
                if (strikeButton.actionButtonRenderer != null) strikeButton.actionButtonRenderer.color = Palette.EnabledColor;
                return;
            }
            int pct = Percent();
            string key = seenNow ? "uc.ui.stalker.button_seen"
                       : inConeNow ? "uc.ui.stalker.button_locked"
                       : "uc.ui.stalker.button_stalk";
            strikeButton.buttonText = UCLocalization.Tr(key, pct);
            if (strikeButton.actionButtonRenderer != null) {
                // Grammar of the tint: indigo = the clock runs, warm = somebody could see you.
                strikeButton.actionButtonRenderer.color =
                    seenNow ? new Color(1f, 0.55f, 0.35f)
                    : inConeNow ? Color
                    : Palette.EnabledColor;
            }
        }

        // The Stalker sees his target's name in his own colour (world + meeting). Nobody else sees
        // anything - the Witness name-tint pattern.
        private static void NameColorTick() {
            try {
                if (!IsLocalStalker()) return;
                var t = Target();
                if (t?.cosmetics?.nameText != null) t.cosmetics.nameText.color = Color;
                var meeting = MeetingHud.Instance;
                if (meeting?.playerStates == null) return;
                foreach (var ps in meeting.playerStates) {
                    if (ps == null || ps.NameText == null) continue;
                    if (ps.TargetPlayerId == targetId) ps.NameText.color = Color;
                }
            } catch { }
        }

        // ---- The cone: a second light source (see the header) ----
        private static LightSource coneLight;      // the clone, Stalker's client only
        private static bool coneWarned;

        private static bool LocalWantsCone() {
            try {
                if (!active || complete) return false;
                if (!IsLocalStalker()) return false;
                var me = PlayerControl.LocalPlayer;
                if (me == null || me.Data == null || me.Data.IsDead || me.Data.Disconnected) return false;
                if (InMeeting()) return false;
                if (Werewolf.WolfDarkActive()) return false;   // the night regime owns every torch
                return true;
            } catch { return false; }
        }

        // Per frame on the Stalker's client: create / feed / drop the cone light. The clone is a
        // full copy of the player's own LightSource (same components, same lighting material, same
        // child mesh), parented to the player so it moves with him; the only differences are the
        // flashlight mode (SetupLightingForGameplay) and its own view distance (the cone reach).
        private static void TickCone() {
            bool want = LocalWantsCone();
            if (!want) { ForceConeOff(); return; }
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null || me.lightSource == null || me.TargetFlashlight == null) return;
                if (coneLight == null) {
                    var src = me.lightSource;
                    var go = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
                    go.name = "StalkerConeLight";
                    go.transform.localPosition = src.transform.localPosition;
                    go.transform.localRotation = src.transform.localRotation;
                    go.transform.localScale = src.transform.localScale;
                    coneLight = go.GetComponent<LightSource>();
                    if (coneLight == null) {
                        UnityEngine.Object.Destroy(go);
                        if (!coneWarned) {
                            coneWarned = true;
                            UnknownsCollectionPlugin.Logger?.LogWarning("[Stalker] cone light clone has no LightSource - cone visual disabled (clock still runs).");
                        }
                        return;
                    }
                    // Same input method the vanilla flashlight would use (mouse / stick), then the
                    // flashlight setup on the CLONE only - the player's own light stays TOR's.
                    try { me.SetFlashlightInputMethod(); } catch { }
                    coneLight.SetupLightingForGameplay(true, Width(), me.TargetFlashlight.transform);
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Stalker] cone light created.");
                }
                // Reach follows the live crew radius (sabotage shrinks both, in step).
                float reach = CrewRadius() * Reach();
                if (reach > 0f) coneLight.SetViewDistance(reach);
                coneLight.SetFlashlightEnabled(true);
            } catch (Exception e) {
                if (!coneWarned) {
                    coneWarned = true;
                    UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] cone light failed (clock still runs): {e}");
                }
            }
        }

        private static void ForceConeOff() {
            if (coneLight == null) return;
            try { UnityEngine.Object.Destroy(coneLight.gameObject); } catch { }
            coneLight = null;
        }

        // ---- The stalk meter (target's HUD) ----
        private static TextMeshPro meterText;
        private static int meterShownRemaining = int.MinValue;
        private static bool meterShownComplete;

        private static void EnsureMeter() {
            if (meterText != null) return;
            var hud = HudManager.Instance;
            if (hud == null) return;
            var go = new GameObject("StalkerMeterText");
            go.transform.SetParent(hud.transform);
            go.transform.localPosition = new Vector3(0f, -1.95f, -50f);
            go.transform.localScale = Vector3.one;
            meterText = go.AddComponent<TextMeshPro>();
            meterText.fontSize = 1.5f;
            meterText.alignment = TextAlignmentOptions.Center;
            meterText.enableWordWrapping = false;
            meterText.color = Color;
            meterShownRemaining = int.MinValue;
            meterShownComplete = false;
        }

        private static bool MeterAllowedLocally() {
            int mode = MeterMode?.getSelection() ?? 0;
            if (mode == 2) return false;
            if (mode == 1 && meterPercent < 50 && !complete) return false;
            return true;
        }

        private static void MeterTick() {
            bool show = IsLocalTarget() && IsAlive(PlayerControl.LocalPlayer) && !InMeeting()
                        && IsAlive(stalker) && meterRemaining >= 0 && MeterAllowedLocally();
            if (!show) { HideMeter(); return; }
            EnsureMeter();
            if (meterText == null) return;
            if (!meterText.gameObject.activeSelf) meterText.gameObject.SetActive(true);
            if (complete) {
                if (!meterShownComplete) {
                    meterShownComplete = true;
                    meterText.text = UCLocalization.Tr("uc.ui.stalker.meter_ready");
                    meterText.color = new Color(1f, 0.45f, 0.35f);
                }
                // Pulse: the target should feel the knife.
                meterText.transform.localScale = Vector3.one * (1f + 0.06f * Mathf.Sin(Time.time * 5f));
                return;
            }
            if (meterShownRemaining != meterRemaining) {
                meterShownRemaining = meterRemaining;
                meterText.text = UCLocalization.Tr("uc.ui.stalker.meter", meterRemaining);
                meterText.color = Color;
                meterText.transform.localScale = Vector3.one;
            }
        }

        private static void HideMeter() {
            if (meterText != null && meterText.gameObject.activeSelf) meterText.gameObject.SetActive(false);
        }

        // ---- The host watches the target's fate ----
        private static void HostFateTick() {
            if (!active || stalker == null) return;
            if (InMeeting()) return;   // an ejection lands only when the exile UI is gone
            var target = Target();
            if (target == null || target.Data == null) return;
            if (target.Data.Disconnected) return;   // replaced by HostPickTargetIfMissing
            if (!target.Data.IsDead) return;

            if (complete) {
                if (!IsAlive(stalker)) return;   // a dead Stalker does not win
                if (Time.time < nextWinTry) return;
                nextWinTry = Time.time + 2f;
                UnknownsCollectionPlugin.Logger?.LogInfo("[Stalker] the target is dead - the Stalker wins.");
                GameManager.Instance.RpcEndGame((GameOverReason)StalkerWinReason, false);
                return;
            }

            // Too early.
            int mode = TargetDeath?.getSelection() ?? 0;
            if (mode == 0) SendFallback(0);
            else {
                byte next = PickTarget(stalkerPlayerId);
                if (next != byte.MaxValue) SendSetTarget(next);
                else SendFallback(0);   // nobody left to stalk
            }
        }

        // ---- Winner list + end screen (Necromancer pattern; reason 34, banner 15) ----
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)]
        static class OnGameEndPatch {
            public static void Prefix() {
                winnerIds.Clear();
                if (!active || stalkerPlayerId == byte.MaxValue) return;
                winnerIds.Add(stalkerPlayerId);
            }

            public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ref EndGameResult endGameResult) {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != StalkerWinReason) return;
                    if (winnerIds.Count == 0) return;
                    EndGameResult.CachedWinners.Clear();
                    foreach (byte id in winnerIds) {
                        var p = Helpers.playerById(id);
                        if (p != null && p.Data != null)
                            EndGameResult.CachedWinners.Add(new CachedPlayerData(p.Data));
                    }
                    SetWinCondition(15); // Necromancer 14
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Stalker] The Stalker wins!");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] OnGameEnd failed: {e}");
                }
            }
        }

        private static FieldInfo winConditionField;
        private static void SetWinCondition(int value) {
            try {
                if (winConditionField == null) {
                    var atdType = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.AdditionalTempData");
                    if (atdType != null)
                        winConditionField = atdType.GetField("winCondition", BindingFlags.Public | BindingFlags.Static);
                }
                if (winConditionField != null) {
                    var wcEnum = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.WinCondition");
                    if (wcEnum != null)
                        winConditionField.SetValue(null, Enum.ToObject(wcEnum, value));
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] SetWinCondition failed: {e}");
            }
        }

        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.SetEverythingUp))]
        [HarmonyPriority(Priority.Last)]
        static class EndGameFxPatch {
            public static void Postfix(EndGameManager __instance) {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != StalkerWinReason) return;
                    if (__instance.WinText != null) {
                        GameObject bonus = UnityEngine.Object.Instantiate(__instance.WinText.gameObject);
                        bonus.transform.position = new Vector3(__instance.WinText.transform.position.x,
                            __instance.WinText.transform.position.y - 0.5f,
                            __instance.WinText.transform.position.z);
                        bonus.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
                        var text = bonus.GetComponent<TMP_Text>();
                        text.text = UCLocalization.Tr("uc.ui.stalker.win_banner");
                        text.color = Color;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] end-screen FX failed: {e}");
                }
            }
        }

        // ---- Task accounting: the Stalker's tasks never count toward the crew total (client-side
        // Collector pattern). Stops the moment he is the Pursuer - TOR's own fake-task rule takes
        // over from there. ----
        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class TaskPatch {
            public static void Postfix(GameData __instance) {
                try {
                    if (!active || stalker == null || stalker.Data == null) return;
                    if (HasTasks?.getBool() ?? false) return;
                    var (done, total) = TasksHandler.taskInfo(stalker.Data);
                    __instance.TotalTasks -= total;
                    __instance.CompletedTasks -= done;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] TaskPatch failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(Helpers), nameof(Helpers.roleCanUseVents))]
        static class VentPatch {
            public static void Postfix(PlayerControl player, ref bool __result) {
                try {
                    if (!active || player == null || stalker == null) return;
                    if (player.PlayerId != stalkerPlayerId) return;
                    if (CanVent?.getBool() ?? false) __result = true;
                } catch { }
            }
        }

        // ---- Role identity (while active; as the Pursuer TOR's own RoleInfo takes over) ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || stalker == null || p == null || p != stalker || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = StalkerInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, StalkerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Stalker] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets. PlayerId-keyed state ALSO clears on OnGameJoined (the lobby-leak rule). ----
        private static void FullReset() {
            ForceConeOff();
            stalker = null;
            active = false;
            stalkerPlayerId = byte.MaxValue;
            targetId = byte.MaxValue;
            progress = 0f;
            complete = false;
            nextMeterSend = 0f;
            lastMeterSent = -1f;
            meterRemaining = -1;
            meterPercent = 0;
            inConeNow = false;
            seenNow = false;
            strikeTarget = null;
            nextWinTry = 0f;
            nextHostTick = 0f;
            nextClockTick = 0f;
            lastClockAt = -1f;
            meterShownRemaining = int.MinValue;
            meterShownComplete = false;
            HideMeter();
            // strikeButton deliberately NOT nulled (the resetVariables button-timing rule).
            // winnerIds deliberately survives resetVariables (read after reset at game end).
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Stalker", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() { FullReset(); winnerIds.Clear(); }
        }
    }
}
