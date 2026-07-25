// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCKillOverlayTOR - custom kill cutscenes for the TOR roles with a SPECIAL kill ability
 * (partial extension of UCKillOverlay; gated by the "TOR Kill Animations" toggle in the UC
 * options popup, per-player BepInEx config - pure cosmetics, so intentionally NOT host-synced).
 *
 * Detection needs no new network traffic: TOR's own RPCs already carry everything.
 *  - Field kills all funnel through RPCProcedure.uncheckedMurderPlayer(sourceId, targetId,
 *    showAnimation) - an observe-only prefix reads the TRUE killer there (the overlay itself
 *    later only sees a masked killer for showAnimation==0 kills) and arms the matching Kind
 *    for the victim, exactly like the UC roles' FX-RPC arming.
 *  - showAnimation==0 ("masked") kills (Vampire bite death, Warlock curse proxy kill, bomb
 *    deaths) get their cutscene at the DEATH, never at the bite/spell moment - a marked player
 *    who is still alive must not learn what is coming (user decision). The killer figure IS
 *    shown to the dead victim: TOR reveals every role to ghosts anyway, so nothing new leaks
 *    (user decision). Because ShowKillAnimation only sees (victim, victim) for masked kills,
 *    the REAL killer color travels through the arming side (ArmVictim killerColor). The Witch's
 *    spell death resolves at meeting end via Exiled() and uses the Poisoner-style direct
 *    PlayFor + queue instead (WitchSpellDeath, hooked at uncheckedExilePlayer - RPC 110 is
 *    witch-exile-exclusive across the whole mod family).
 *  - Thief steal kills are armed from RPCProcedure.thiefStealsRole (its parameter IS the
 *    victim); by the time the murder RPC lands, thiefStealsRole has already cleared
 *    Thief.thief via clearAndReload, so the murder hook can no longer attribute it.
 *  - Guesser shots never reach uncheckedMurderPlayer (they exile) - TOR manually calls
 *    ShowKillAnimation on the dying client during the MEETING, so the existing prefix catches
 *    them via killer identity; the sequence is flagged playInMeeting like vanilla's.
 *
 * Per the "use TOR's normal effects where possible / no new sounds" directives, the sequences
 * reuse TOR's own assets - SoundEffectsManager cues (vampireBite/warlockCurse/witchSpell/
 * shifterShift/fail/pursuerBlank) and sprites (Garlic, Bomb, TargetIcon, NinjaTraceW) - plus
 * already-shipped UC sounds (scout_whoosh, maniac_explosion, witness_sting, collector_pickup).
 * Custom assets are SPRITES only, where TOR has nothing fitting (revolver/badge/muzzle/katana/
 * claw/mask/rolecard/wanted/coin).
 */

using System;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;   // the role containers (Sheriff, Vampire, ...) are nested
using UnityEngine;

namespace UnknownsCollection {
    public static partial class UCKillOverlay {
        // Extra actor for sequences that need a third figure (the Bounty poster's mini portrait).
        private static Fig extraFig;

        // ==================== TOR asset bridges ====================

        private static Sprite TorSprite(string path, float ppu) {
            try { return TheOtherRoles.Helpers.loadSpriteFromResources(path, ppu); } catch { return null; }
        }
        private static void TorSfx(string name) {
            try { SoundEffectsManager.play(name); } catch { }
        }

        private static bool IsPlayer(PlayerControl p, byte id) => p != null && p.PlayerId == id;

        // ArmVictim + a diagnostic line, so playtests can verify detection in the BepInEx log.
        // Passes the REAL killer's color along: masked kills reach ShowKillAnimation as
        // (victim, victim), so the killer figure's color must come from the arming side.
        private static void Arm(Kind kind, byte victimId, byte killerId) {
            int color = -1;
            try {
                var k = TheOtherRoles.Helpers.playerById(killerId);
                if (k?.Data?.DefaultOutfit != null) color = k.Data.DefaultOutfit.ColorId;
            } catch { }
            ArmVictim(kind, victimId, 5f, color);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[UCKillOverlay] TOR kill armed: {kind} victim={victimId}");
        }

        // ==================== detection (observe-only prefixes on TOR) ====================

        // Thief steal: armed from the steal RPC itself (see header). Runs BEFORE TOR's handler,
        // while Thief.thief still points at the thief.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.thiefStealsRole))]
        static class ThiefStealObserverPatch {
            public static void Prefix(byte playerId) {
                try {
                    if (!TorAnimsOn) return;
                    if (Thief.thief != null && Thief.thief.PlayerId != playerId)
                        Arm(Kind.ThiefSteal, playerId, Thief.thief.PlayerId);
                } catch { }
            }
        }

        // All TOR field kills pass through here with the TRUE killer id (the overlay call later
        // only sees a masked one for showAnimation==0). Observe-only: arm and get out.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.uncheckedMurderPlayer))]
        static class TorMurderObserverPatch {
            public static void Prefix(byte sourceId, byte targetId, byte showAnimation) {
                try {
                    if (!TorAnimsOn) return;
                    bool masked = showAnimation == 0;
                    bool self = sourceId == targetId;

                    // Thief: only the failed-steal suicide is left to catch here (steal kills are
                    // armed from thiefStealsRole; by now a stealing thief is no longer Thief.thief).
                    if (IsPlayer(Thief.thief, sourceId)) {
                        if (self) Arm(Kind.ThiefFail, targetId, sourceId);
                        return;
                    }
                    // Paket W4: the Hunter usually still HOLDS the Sheriff slot (Hunter.cs never
                    // writes Sheriff.sheriff), so without this guard his silver bolt would be armed
                    // as a SheriffShot - and an armed entry beats the identity match in SelectRaw.
                    // Bailing out here leaves his kills to SelectWolfPack (Kind.SilverBolt), which
                    // also correctly plays NOTHING when the shot only wounded the beast. His MISFIRE
                    // (source == target) is deliberately left to the Sheriff branch below - the
                    // exploding-gun sequence fits a Hunter who shot an innocent just as well.
                    if (Hunter.active && !self && IsPlayer(Hunter.hunter, sourceId)) return;
                    // Sheriff (incl. a promoted Deputy - TOR repoints Sheriff.sheriff): hit or misfire.
                    if (IsPlayer(Sheriff.sheriff, sourceId)) {
                        Arm(self ? Kind.SheriffMisfire : Kind.SheriffShot, targetId, sourceId);
                        return;
                    }
                    // Bomb deaths are exactly the Bomber's masked kills (each client kills itself
                    // locally) - checked BEFORE the self bail-out, because the bomber caught in his
                    // OWN blast arrives as source==target==bomber and is a bomb victim like anyone.
                    if (IsPlayer(Bomber.bomber, sourceId)) {
                        if (masked) Arm(Kind.BomberBomb, targetId, sourceId);
                        return;
                    }
                    if (self) return;
                    if (IsPlayer(Ninja.ninja, sourceId)) { Arm(Kind.NinjaDash, targetId, sourceId); return; }
                    if (IsPlayer(Jackal.jackal, sourceId) || IsPlayer(Sidekick.sidekick, sourceId)) {
                        Arm(Kind.JackalClaw, targetId, sourceId);
                        return;
                    }
                    // Vampire/Warlock: direct kills and the masked variants (delayed bite death /
                    // curse proxy kill) both play - the cutscene fires at the death, where the
                    // victim is a ghost and sees all roles anyway.
                    if (IsPlayer(Vampire.vampire, sourceId)) { Arm(masked ? Kind.VampireBiteDeath : Kind.VampireKill, targetId, sourceId); return; }
                    if (IsPlayer(Witch.witch, sourceId)) { if (!masked) Arm(Kind.WitchKill, targetId, sourceId); return; }
                    if (IsPlayer(Warlock.warlock, sourceId)) { Arm(masked ? Kind.WarlockCurse : Kind.WarlockKill, targetId, sourceId); return; }
                    // Bounty hit: only when the target IS the bounty (regular kills stay vanilla).
                    // BountyHunter.bounty may only be known on some clients - then only those play it.
                    if (IsPlayer(BountyHunter.bountyHunter, sourceId) && IsPlayer(BountyHunter.bounty, targetId))
                        Arm(Kind.BountyHit, targetId, sourceId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[UCKillOverlay] TOR murder observer: {e.Message}");
                }
            }
        }

        // Witch spell deaths resolve at meeting end via Exiled() (never through ShowKillAnimation) -
        // same situation as the Poisoner, so the same direct-PlayFor path: the queue waits for the
        // exile UI, audience is victim + witch (the sequence itself stays anonymous - no killer
        // figure). Hook point: RPCProcedure.uncheckedExilePlayer - RPC 110 is sent EXCLUSIVELY by
        // TOR's witch-execution block (verified across TOR and the whole mod family), and it runs
        // exactly once per client, so no dedup is needed.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.uncheckedExilePlayer))]
        static class WitchExileObserverPatch {
            public static void Prefix(byte targetId) {
                try {
                    if (!TorAnimsOn || Witch.witch == null) return;
                    var lp = PlayerControl.LocalPlayer;
                    if (lp == null) return;
                    if (lp.PlayerId != targetId && lp.PlayerId != Witch.witch.PlayerId) return;   // audience
                    var target = TheOtherRoles.Helpers.playerById(targetId);
                    if (target == null || target.Data == null) return;
                    PlayFor(Kind.WitchSpellDeath, Witch.witch.Data, target.Data);
                    UnknownsCollectionPlugin.Logger?.LogInfo($"[UCKillOverlay] TOR kill armed: WitchSpellDeath victim={targetId}");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[UCKillOverlay] witch exile observer: {e.Message}");
                }
            }
        }

        // Guesser shots: TOR calls ShowKillAnimation manually on the dying client during the
        // meeting - identity check on the killer, no arming involved.
        private static Kind SelectTorMeeting(NetworkedPlayerInfo killer, NetworkedPlayerInfo victim) {
            try {
                if (MeetingHud.Instance == null || killer == null || victim == null) return Kind.None;
                if (killer.PlayerId == victim.PlayerId) return Kind.None;
                byte k = killer.PlayerId;
                if (IsPlayer(Guesser.niceGuesser, k) || IsPlayer(Guesser.evilGuesser, k)) return Kind.GuesserShot;
                if (HandleGuesser.isGuesserGm && HandleGuesser.isGuesser(k)) return Kind.GuesserShot;
            } catch { }
            return Kind.None;
        }

        // ==================== construction ====================

        private static void BuildTor(Pending p) {
            switch (p.kind) {
                case Kind.SheriffShot:
                    duration = 1.65f;
                    killerFig = MakeFig(p.killerColor, false, -4.2f, -0.35f, 10);
                    victimFig = MakeFig(p.victimColor, true, 4.2f, -0.35f, 10);
                    propA = Make("revolver", UCAssets.OverlayRevolver, -4.2f, -0.1f, 14, Color.white, 0.62f);
                    propB = Make("muzzle", UCAssets.OverlayMuzzle, 0f, 0f, 22, new Color(1f, 1f, 1f, 0f), 0.9f, true);
                    propC = Make("badge", UCAssets.OverlayStar, -4.2f, 0.75f, 15, new Color(1f, 1f, 1f, 0f), 0.42f);
                    particles = MakeParticles(6, UCFx.Smoke, new Color(0.75f, 0.75f, 0.78f), 20, false);
                    break;

                case Kind.SheriffMisfire:
                    duration = 1.8f;
                    killerFig = MakeFig(p.killerColor, false, -4.2f, -0.35f, 10);
                    propA = Make("revolver", UCAssets.OverlayRevolver, -4.2f, -0.1f, 14, Color.white, 0.62f);
                    propB = Make("burst", UCAssets.OverlayBurst, 0f, 0f, 22, new Color(1f, 1f, 1f, 0f), 0.42f);
                    propC = Make("badge", UCAssets.OverlayStar, -4.2f, 0.75f, 15, new Color(1f, 1f, 1f, 0f), 0.42f);
                    particles = MakeParticles(7, UCFx.Smoke, new Color(0.35f, 0.35f, 0.38f), 20, false);
                    break;

                // The killer figure shows in the death variants too: TOR reveals every role to
                // ghosts anyway, so the dead victim learns nothing it would not see regardless
                // (user decision). The REAL killer color travels via the arming side.
                case Kind.VampireKill:
                case Kind.VampireBiteDeath:   // bite death: same scene, just no garlic on stage
                    duration = 1.65f;
                    killerFig = MakeFig(p.killerColor, false, -4.2f, -0.35f, 10);
                    if (p.kind == Kind.VampireKill)
                        propB = Make("garlic", TorSprite("TheOtherRoles.Resources.Garlic.png", 180f), 1.7f, -0.95f, 12, new Color(1f, 1f, 1f, 0f), 1.15f);
                    victimFig = MakeFig(p.victimColor, true, 0.45f, -0.35f, 10);
                    propA = Make("fangs", UCAssets.OverlayFangs, 0.45f, 3f, 20, new Color(1f, 1f, 1f, 0f), 1.05f);
                    particles = MakeParticles(6, UCFx.Smoke, new Color(0.45f, 0.1f, 0.16f), 18, false);
                    break;

                case Kind.WarlockKill:
                case Kind.WarlockCurse:       // curse proxy kill: identical scene
                    duration = 1.65f;
                    killerFig = MakeFig(p.killerColor, false, -4.2f, -0.35f, 10);
                    victimFig = MakeFig(p.victimColor, true, 0.55f, -0.35f, 10);
                    propA = Make("sigil", UCAssets.OverlaySigil, 0.55f, -0.95f, 8, new Color(1f, 1f, 1f, 0f), 1.15f, true);
                    particles = MakeParticles(8, UCFx.Spark, new Color(0.75f, 0.5f, 1f), 24, true);
                    break;

                case Kind.WitchKill:
                case Kind.WitchSpellDeath:    // spell death (after the meeting): same scene
                    duration = 1.7f;
                    killerFig = MakeFig(p.killerColor, false, -4.2f, -0.35f, 10);
                    victimFig = MakeFig(p.victimColor, true, 2.3f, -0.35f, 10);
                    propB = Make("hat", UCAssets.OverlayHat, -4.2f, 0.95f, 14, Color.white, 0.68f);
                    particles = MakeParticles(9, UCFx.Dot, new Color(0.5f, 0.95f, 0.45f), 24, true);
                    break;

                case Kind.NinjaDash:
                    duration = 1.6f;
                    killerFig = MakeFig(p.killerColor, false, -4.2f, -0.35f, 10);
                    victimFig = MakeFig(p.victimColor, true, 1.6f, -0.35f, 10);
                    propA = Make("katana", UCAssets.OverlayKatana, -6f, 0.1f, 26, new Color(1f, 1f, 1f, 0f), 1.15f);
                    propB = Make("trace", TorSprite("TheOtherRoles.Resources.NinjaTraceW.png", 120f), -2.4f, -0.35f, 9, new Color(1f, 1f, 1f, 0f), 1f);
                    particles = MakeParticles(6, UCFx.Spark, new Color(0.9f, 0.95f, 1f), 28, true);
                    break;

                case Kind.BomberBomb:
                    duration = 1.6f;
                    victimFig = MakeFig(p.victimColor, true, 0.9f, -0.35f, 10);   // no killer: the bomb is the star
                    propA = Make("bomb", TorSprite("TheOtherRoles.Resources.Bomb.png", 110f) ?? UCAssets.OverlayBomb, -4f, -0.62f, 14, new Color(1f, 1f, 1f, 0f), 1f);
                    propB = Make("boom", UCAssets.OverlayBurst, -1.3f, -0.35f, 40, new Color(1f, 1f, 1f, 0f), 0.3f);
                    particles = MakeParticles(8, UCFx.Smoke, new Color(0.45f, 0.45f, 0.5f), 35, false);
                    break;

                case Kind.GuesserShot:
                    duration = 1.5f;
                    victimFig = MakeFig(p.victimColor, false, 0f, -0.35f, 10);
                    propA = Make("reticle", TorSprite("TheOtherRoles.Resources.TargetIcon.png", 40f) ?? UCAssets.OverlayReticle, 0f, 0.25f, 20, new Color(1f, 1f, 1f, 0f), 1f);
                    particles = MakeParticles(5, UCFx.Spark, new Color(1f, 0.4f, 0.35f), 26, true);
                    break;

                case Kind.ThiefSteal:
                    duration = 1.75f;
                    killerFig = MakeFig(p.killerColor, false, -4.2f, -0.35f, 10);
                    victimFig = MakeFig(p.victimColor, true, 1.9f, -0.35f, 10);
                    propA = Make("rolecard", UCAssets.OverlayRoleCard, 1.9f, 0.1f, 24, new Color(1f, 1f, 1f, 0f), 0.5f);
                    propB = Make("mask", UCAssets.OverlayMask, -4.2f, 0.55f, 14, Color.white, 0.42f);
                    particles = MakeParticles(6, UCFx.Spark, new Color(1f, 0.85f, 0.4f), 26, true);
                    break;

                case Kind.ThiefFail:
                    duration = 1.7f;
                    killerFig = MakeFig(p.killerColor, false, -0.4f, -0.35f, 10);
                    propA = Make("rolecard", UCAssets.OverlayRoleCard, 1.15f, 0.15f, 24, new Color(1f, 1f, 1f, 0f), 0.5f);
                    propB = Make("mask", UCAssets.OverlayMask, -0.4f, 0.55f, 14, Color.white, 0.42f);
                    particles = MakeParticles(5, UCFx.Smoke, new Color(0.55f, 0.55f, 0.6f), 20, false);
                    break;

                case Kind.JackalClaw:
                    duration = 1.5f;
                    killerFig = MakeFig(p.killerColor, false, -4.2f, -0.35f, 10);
                    victimFig = MakeFig(p.victimColor, true, 1.4f, -0.35f, 10);
                    propA = Make("claw1", UCAssets.OverlayClaw, 1.4f, 0.15f, 24, new Color(0.45f, 0.95f, 0.85f, 0f), 0.95f);
                    propB = Make("claw2", UCAssets.OverlayClaw, 1.5f, 0.05f, 24, new Color(0.45f, 0.95f, 0.85f, 0f), 0.8f);
                    if (propB != null) propB.flipX = true;
                    particles = MakeParticles(6, UCFx.Spark, new Color(0.5f, 1f, 0.9f), 28, true);
                    break;

                case Kind.BountyHit:
                    duration = 1.8f;
                    killerFig = MakeFig(p.killerColor, false, -4.2f, -0.35f, 10);
                    victimFig = MakeFig(p.victimColor, true, 1.7f, -0.35f, 10);
                    propA = Make("wanted", UCAssets.OverlayWanted, -2f, 4f, 12, Color.white, 0.85f);
                    extraFig = MakeFig(p.victimColor, false, -2f, 3.4f, 13);   // portrait on the poster
                    extraFig.SetScale(0.52f);
                    particles = MakeParticles(7, UCAssets.OverlayCoin, Color.white, 30, false);
                    break;
            }
        }

        // ==================== choreographies ====================

        private static void UpdateTorSeq(float t, float exit) {
            switch (activeKind) {
                case Kind.SheriffShot: UpdateSheriffShot(t, exit); break;
                case Kind.SheriffMisfire: UpdateSheriffMisfire(t, exit); break;
                case Kind.VampireKill: UpdateVampireKill(t, exit); break;
                case Kind.VampireBiteDeath: UpdateVampireKill(t, exit); break;   // garlic guard
                case Kind.WarlockKill: UpdateWarlockKill(t, exit); break;
                case Kind.WarlockCurse: UpdateWarlockKill(t, exit); break;
                case Kind.WitchKill: UpdateWitchKill(t, exit); break;
                case Kind.WitchSpellDeath: UpdateWitchKill(t, exit); break;
                case Kind.NinjaDash: UpdateNinjaDash(t, exit); break;
                case Kind.BomberBomb: UpdateBomberBomb(t, exit); break;
                case Kind.GuesserShot: UpdateGuesserShot(t, exit); break;
                case Kind.ThiefSteal: UpdateThiefSteal(t, exit); break;
                case Kind.ThiefFail: UpdateThiefFail(t, exit); break;
                case Kind.JackalClaw: UpdateJackalClaw(t, exit); break;
                case Kind.BountyHit: UpdateBountyHit(t, exit); break;
            }
        }

        // High-noon duel: draw, BANG, victim keels over; the badge glints through the smoke.
        private static void UpdateSheriffShot(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.18f));
            float kx = Mathf.Lerp(-4.2f, -2.3f, ein);
            killerFig.SetPos(kx, -0.35f);
            killerFig.SetAlpha(ein * exit);
            victimFig.SetAlpha(ein * exit);

            // badge floats over the sheriff, glinting once
            propC.transform.localPosition = new Vector3(kx + 0.05f, 0.78f + 0.03f * Mathf.Sin(t * 9f), 0f);
            float glint = Mathf.Sin(Mathf.Clamp01(Seg(t, 0.2f, 0.34f)) * Mathf.PI);
            propC.color = Color.Lerp(Color.white, new Color(1f, 1f, 0.75f), glint);
            SetAlpha(propC, ein * exit);

            // revolver raises from a low ready into the aim
            float aim = Smooth(Seg(t, 0.16f, 0.36f));
            propA.transform.localPosition = new Vector3(kx + 1.05f, -0.28f + 0.28f * aim, 0f);
            propA.transform.localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(-24f, 0f, aim));
            SetAlpha(propA, ein * exit);

            bool fired = t >= 0.46f;
            if (fired) Sound(1, () => TorSfx("pursuerBlank"));   // TOR's blank-shot bang
            float bang = Seg(t, 0.46f, 0.56f);
            propB.transform.localPosition = new Vector3(kx + 2.05f, 0.02f, 0f);
            propB.transform.localScale = Vector3.one * (0.55f + 0.75f * EaseOut(bang));
            SetAlpha(propB, fired ? Mathf.Max(0f, 1f - Seg(t, 0.46f, 0.62f)) : 0f);
            SetAlpha(flash, fired ? Mathf.Max(0f, 0.55f - 2.6f * (t - 0.46f)) : 0f);
            if (fired && t < 0.52f) killerFig.SetPos(kx - 0.12f * Mathf.Sin(Seg(t, 0.46f, 0.52f) * Mathf.PI), -0.35f); // recoil

            // gun smoke drifting up from the muzzle
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.5f + i * 0.05f, 0.95f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                sr.transform.localPosition = new Vector3(kx + 2.05f + 0.25f * ph + Jitter(0.03f), 0.05f + 0.8f * ph, 0f);
                sr.transform.localScale = Vector3.one * (0.5f + 0.9f * ph);
                SetAlpha(sr, (1f - ph) * 0.5f * exit);
            }

            // the victim takes the hit: jerk back, then keel over
            if (t < 0.46f) {
                victimFig.SetPos(Mathf.Lerp(4.2f, 2.4f, ein), -0.35f);
            } else {
                float jerk = EaseOut(Seg(t, 0.46f, 0.54f));
                float fall = Smooth(Seg(t, 0.56f, 0.85f));
                victimFig.SetPos(2.4f + 0.35f * jerk + 0.25f * fall, -0.35f - 0.55f * fall);
                victimFig.SetRot(-80f * fall);
                if (t < 0.54f) victimFig.SetTint(Color.Lerp(victimFig.color, Color.white, 1f - jerk), exit);
                else victimFig.SetTint(victimFig.color, exit);
            }
        }

        // The misfire: the sheriff aims at nothing, the gun blows up in his hand, the badge drops.
        private static void UpdateSheriffMisfire(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.18f));
            float kx = Mathf.Lerp(-4.2f, -0.7f, ein);
            killerFig.SetAlpha(ein * exit);

            float aim = Smooth(Seg(t, 0.2f, 0.4f));
            bool burst = t >= 0.5f;

            propC.transform.localPosition = new Vector3(kx + 0.05f, 0.78f, 0f);
            SetAlpha(propC, ein * exit);

            if (!burst) {
                killerFig.SetPos(kx, -0.35f);
                propA.transform.localPosition = new Vector3(kx + 1.05f, -0.28f + 0.28f * aim, 0f);
                propA.transform.localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(-24f, 0f, aim));
                SetAlpha(propA, ein * exit);
                SetAlpha(propB, 0f);
            } else {
                Sound(1, () => TorSfx("pursuerBlank"));   // TOR's blank-shot bang
                // the revolver bursts and tumbles out of frame
                float boom = Seg(t, 0.5f, 0.62f);
                propB.transform.localPosition = new Vector3(kx + 1.05f, 0f, 0f);
                propB.transform.localScale = Vector3.one * (0.3f + 0.5f * EaseOut(boom));
                SetAlpha(propB, Mathf.Max(0f, 1f - Seg(t, 0.5f, 0.68f)));
                SetAlpha(flash, Mathf.Max(0f, 0.5f - 2.4f * (t - 0.5f)));
                float tumble = EaseIn(Seg(t, 0.52f, 0.85f));
                propA.transform.localPosition = new Vector3(kx + 1.05f + 1.6f * tumble, 0f - 2.6f * tumble * tumble, 0f);
                propA.transform.localRotation = Quaternion.Euler(0, 0, 520f * tumble);
                SetAlpha(propA, (1f - Seg(t, 0.78f, 0.92f)) * exit);

                // sheriff blinks white, wobbles, falls backwards; the badge pops off and drops
                float wob = Seg(t, 0.52f, 0.72f);
                float fall = Smooth(Seg(t, 0.72f, 0.95f));
                bool hot = t < 0.6f && ((int)(Time.time * 22f)) % 2 == 0;
                killerFig.SetTint(hot ? Color.white : killerFig.color, exit);
                killerFig.SetPos(kx + 0.08f * Mathf.Sin(wob * 18f) * (1f - fall) - 0.4f * fall, -0.35f - 0.5f * fall);
                killerFig.SetRot(6f * Mathf.Sin(wob * 14f) * (1f - fall) + 85f * fall);
                float drop = EaseIn(Seg(t, 0.6f, 0.9f));
                propC.transform.localPosition = new Vector3(kx + 0.05f + 0.5f * drop, 0.78f - 2.7f * drop, 0f);
                propC.transform.localRotation = Quaternion.Euler(0, 0, 260f * drop);
            }

            // black powder smoke
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.54f + i * 0.04f, 0.98f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                sr.transform.localPosition = new Vector3(kx + 1.05f + Jitter(0.06f) + 0.2f * ph, 0.05f + 1f * ph, 0f);
                sr.transform.localScale = Vector3.one * (0.6f + 1.1f * ph);
                SetAlpha(sr, (1f - ph) * 0.6f * exit);
            }
        }

        // The vampire lunges in and the fangs snap shut; the garlic right next to it came too late.
        // Also runs the anonymous VampireBiteDeath (killerFig/propB are null there - the delayed
        // bite catches up with the victim out of nowhere).
        private static void UpdateVampireKill(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.18f));
            victimFig.SetAlpha(ein * exit);
            if (propB != null) SetAlpha(propB, ein * 0.95f * exit);   // garlic on the floor

            float lunge = EaseOut(Seg(t, 0.05f, 0.3f));
            if (killerFig != null) {
                killerFig.SetPos(Mathf.Lerp(-4.2f, -0.75f, lunge), -0.35f);
                killerFig.SetAlpha(Mathf.Min(ein + lunge, 1f) * exit);
            }

            // fangs drop onto the victim and CHOMP
            float dropF = Smooth(Seg(t, 0.3f, 0.46f));
            float chomp = Seg(t, 0.46f, 0.54f);
            if (t >= 0.44f) Sound(1, () => TorSfx("vampireBite"));
            propA.transform.localPosition = new Vector3(0.45f, Mathf.Lerp(3f, 0.55f, dropF) - 0.35f * EaseOut(chomp), 0f);
            propA.transform.localScale = Vector3.one * (1.05f - 0.12f * EaseOut(chomp));
            SetAlpha(propA, dropF > 0f ? (1f - Seg(t, 0.62f, 0.74f)) * exit : 0f);
            SetAlpha(flash, chomp > 0f ? Mathf.Max(0f, 0.3f - 1.8f * (t - 0.46f)) : 0f);

            // the garlic rocks indignantly - it did NOT prevent this one
            if (propB != null) {
                float rock = Seg(t, 0.5f, 0.75f);
                propB.transform.localRotation = Quaternion.Euler(0, 0, 14f * Mathf.Sin(rock * 16f) * (rock > 0f && rock < 1f ? 1f : 0f));
            }

            // the victim pales and sinks
            float pale = Smooth(Seg(t, 0.5f, 0.8f));
            victimFig.SetTint(Color.Lerp(victimFig.color, new Color(0.72f, 0.75f, 0.82f), 0.8f * pale), exit);
            if (t >= 0.74f) {
                float fall = Smooth(Seg(t, 0.74f, 0.95f));
                victimFig.SetRot(80f * fall);
                victimFig.SetPos(0.45f + 0.3f * fall, -0.35f - 0.55f * fall);
            }

            // dark red wisps (bats in spirit) fluttering off the bite
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.5f + i * 0.05f, 0.9f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                sr.transform.localPosition = new Vector3(0.45f + Mathf.Sin(ph * 9f + i * 2.3f) * 0.5f, 0.3f + 1.6f * ph, 0f);
                sr.transform.localScale = Vector3.one * (0.45f + 0.4f * ph);
                SetAlpha(sr, Mathf.Sin(ph * Mathf.PI) * 0.55f * exit);
            }
        }

        // A curse circle ignites under the victim and drags it down. Also runs the anonymous
        // WarlockCurse proxy-kill variant (killerFig is null there - the circle strikes alone).
        private static void UpdateWarlockKill(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.18f));
            victimFig.SetAlpha(ein * exit);
            if (killerFig != null) {
                killerFig.SetPos(Mathf.Lerp(-4.2f, -2.4f, ein), -0.35f);
                killerFig.SetAlpha(ein * exit);
            }

            if (t >= 0.24f) Sound(1, () => TorSfx("warlockCurse"));

            // the sigil fades in below the victim, spinning slowly, pulsing while the curse works
            float ignite = Smooth(Seg(t, 0.24f, 0.42f));
            propA.transform.localRotation = Quaternion.Euler(0, 0, 360f * t);   // one slow revolution
            float pulse = 0.75f + 0.25f * Mathf.Sin(t * 21f);
            SetAlpha(propA, ignite * pulse * exit);
            propA.transform.localScale = Vector3.one * (1.15f + 0.06f * Mathf.Sin(t * 13f));

            // violet sparks orbit up out of the circle
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.4f + i * 0.04f, 0.85f + i * 0.02f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                double ang = i * (Math.PI * 2.0 / particles.Length) + ph * 5f;
                sr.transform.localPosition = new Vector3(0.55f + (float)Math.Cos(ang) * 0.85f * (1f - 0.4f * ph),
                                                         -0.9f + 2.1f * ph, 0f);
                sr.transform.localScale = Vector3.one * (0.7f - 0.3f * ph);
                SetAlpha(sr, Mathf.Sin(ph * Mathf.PI) * 0.85f * exit);
            }

            // the victim darkens to curse-violet, shudders, gets pulled DOWN by the circle
            float grip = Smooth(Seg(t, 0.45f, 0.7f));
            victimFig.SetTint(Color.Lerp(victimFig.color, new Color(0.36f, 0.2f, 0.55f), 0.75f * grip), exit);
            float sink = Smooth(Seg(t, 0.68f, 0.95f));
            victimFig.SetPos(0.55f + 0.05f * Mathf.Sin(t * 40f) * grip * (1f - sink), -0.35f - 1.5f * sink);
            victimFig.SetRot(8f * Mathf.Sin(t * 30f) * grip * (1f - sink));
            if (sink > 0f) victimFig.SetAlpha((1f - EaseIn(sink)) * exit);
        }

        // The witch flicks a green spell stream over; the victim briefly glows and drops.
        private static void UpdateWitchKill(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.18f));
            float kx = Mathf.Lerp(-4.2f, -2.3f, ein);
            killerFig.SetPos(kx, -0.35f);
            killerFig.SetAlpha(ein * exit);
            victimFig.SetPos(Mathf.Lerp(4.2f, 2.3f, ein), -0.35f);
            victimFig.SetAlpha(ein * exit);

            // hat rides on the witch's head, tipping forward with the cast
            float cast = Smooth(Seg(t, 0.3f, 0.42f));
            propB.transform.localPosition = new Vector3(kx - 0.05f, 0.92f, 0f);
            propB.transform.localRotation = Quaternion.Euler(0, 0, -18f * Mathf.Sin(cast * Mathf.PI));
            SetAlpha(propB, ein * exit);
            if (t >= 0.3f) Sound(1, () => TorSfx("witchSpell"));

            // spell stream: green motes arc from witch to victim
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.32f + i * 0.035f, 0.62f + i * 0.035f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                float x = Mathf.Lerp(kx + 0.6f, 2.3f, ph);
                float y = -0.1f + 1.1f * Mathf.Sin(ph * Mathf.PI) + Jitter(0.04f);
                sr.transform.localPosition = new Vector3(x, y, 0f);
                sr.transform.localScale = Vector3.one * (0.6f + 0.25f * Mathf.Sin(ph * Mathf.PI));
                SetAlpha(sr, 0.9f * exit);
            }

            // impact: green glow, small hop, then the drop
            float hit = Seg(t, 0.6f, 0.68f);
            if (hit > 0f) SetAlpha(flash, Mathf.Max(0f, 0.3f - 1.6f * (t - 0.6f)));
            float soakW = Smooth(Seg(t, 0.6f, 0.82f));
            victimFig.SetTint(Color.Lerp(victimFig.color, new Color(0.4f, 0.9f, 0.4f), 0.7f * soakW), exit);
            if (t >= 0.6f && t < 0.72f) victimFig.SetPos(2.3f, -0.35f + 0.22f * Mathf.Sin(Seg(t, 0.6f, 0.72f) * Mathf.PI));
            if (t >= 0.76f) {
                float fall = Smooth(Seg(t, 0.76f, 0.95f));
                victimFig.SetRot(82f * fall);
                victimFig.SetPos(2.3f + 0.3f * fall, -0.35f - 0.55f * fall);
            }
        }

        // The ninja vanishes, a blade streaks the screen, he reappears behind the victim -
        // one beat of stillness, then the victim falls.
        private static void UpdateNinjaDash(float t, float exit) {
            // extra darkness for the assassination
            SetAlpha(dim, Mathf.Min(0.9f, dim.color.a + 0.08f * Smooth(Seg(t, 0f, 0.12f))));

            float ein = EaseOut(Seg(t, 0f, 0.16f));
            victimFig.SetAlpha(ein * exit);

            if (t < 0.3f) {
                killerFig.SetPos(Mathf.Lerp(-4.2f, -2.5f, ein), -0.35f);
                killerFig.SetAlpha(ein);
            } else if (t < 0.52f) {
                // vanished: only the trace marks where he stood
                killerFig.SetAlpha(0f);
                SetAlpha(propB, 0.55f * (1f - Seg(t, 0.3f, 0.52f)) * exit);
                propB.transform.localPosition = new Vector3(-2.5f, -0.35f, 0f);
            } else {
                // reappears BEHIND the victim
                killerFig.SetPos(3.3f, -0.35f);
                killerFig.SetAlpha(Mathf.Min(1f, Seg(t, 0.52f, 0.58f) * 2f) * exit);
                SetAlpha(propB, 0f);
            }
            if (t >= 0.3f) Sound(1, () => UCAssets.PlayScoutWhoosh(PlayerControl.LocalPlayer.GetTruePosition()));

            // the blade streak crosses the whole frame during the dash
            bool streak = t >= 0.32f && t < 0.5f;
            if (streak) {
                float s = Seg(t, 0.32f, 0.5f);
                propA.transform.localPosition = new Vector3(Mathf.Lerp(-5.5f, 5.5f, EaseOut(s)), 0.05f, 0f);
                propA.transform.localScale = new Vector3(1.15f + 0.7f * Mathf.Sin(s * Mathf.PI), 1.15f, 1f);
                SetAlpha(propA, 0.95f);
                SetAlpha(flash, 0.18f * Mathf.Sin(s * Mathf.PI));
            } else {
                SetAlpha(propA, 0f);
            }
            if (t >= 0.5f) Sound(2, () => UCAssets.PlayScoutWhoosh(PlayerControl.LocalPlayer.GetTruePosition(), 0.5f));

            // white sparks along the cut line
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.34f + i * 0.02f, 0.6f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                sr.transform.localPosition = new Vector3(-3.5f + i * 1.35f, 0.05f + Jitter(0.05f), 0f);
                sr.transform.localScale = Vector3.one * (0.5f + 0.4f * Mathf.Sin(ph * Mathf.PI));
                SetAlpha(sr, Mathf.Sin(ph * Mathf.PI) * 0.8f * exit);
            }

            // the samurai beat: the victim stands frozen... then falls apart-style keel
            if (t < 0.72f) {
                victimFig.SetPos(1.6f, -0.35f);
            } else {
                float fall = Smooth(Seg(t, 0.72f, 0.92f));
                victimFig.SetRot(-84f * fall);
                victimFig.SetPos(1.6f - 0.3f * fall, -0.35f - 0.55f * fall);
            }
        }

        // The classic round bomb rolls up to the victim, the fuse sparks down, BOOM -
        // clearly distinct from the Maniac's dropped timer bomb (this one is anonymous).
        private static void UpdateBomberBomb(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.16f));
            bool exploded = t >= 0.6f;
            victimFig.SetAlpha(ein * (t < 0.92f ? 1f : exit));

            if (!exploded) {
                victimFig.SetPos(0.9f + Jitter(0.02f * Seg(t, 0.4f, 0.6f)), -0.35f);
                // roll in: translation + matching rotation
                float roll = EaseOut(Seg(t, 0.04f, 0.34f));
                float bx = Mathf.Lerp(-4f, -0.55f, roll);
                propA.transform.localPosition = new Vector3(bx, -0.62f, 0f);
                propA.transform.localRotation = Quaternion.Euler(0, 0, -roll * 540f);
                SetAlpha(propA, Mathf.Min(1f, Seg(t, 0.04f, 0.12f) * 2f));

                // fuse sparks jitter above the bomb, faster toward the bang
                float panic = Seg(t, 0.34f, 0.6f);
                for (int i = 0; i < particles.Length; i++) {
                    if (i >= 3) { SetAlpha(particles[i], 0f); continue; }
                    var sr = particles[i];
                    sr.transform.localPosition = new Vector3(bx + 0.28f + Jitter(0.05f), -0.1f + Jitter(0.05f), 0f);
                    sr.transform.localScale = Vector3.one * (0.35f + 0.3f * Mathf.PingPong(t * (14f + 22f * panic) + i, 1f));
                    SetAlpha(sr, panic > 0f ? 0.9f : 0.45f);
                }
            } else {
                Sound(1, () => UCAssets.PlayExplosion(PlayerControl.LocalPlayer.GetTruePosition()));
                SetAlpha(propA, 0f);
                float boom = Seg(t, 0.6f, 0.9f);
                propB.transform.localScale = Vector3.one * Mathf.Lerp(0.3f, 3.2f, EaseOut(boom));
                propB.transform.localRotation = Quaternion.Euler(0, 0, -35f * boom);
                SetAlpha(propB, (boom < 0.75f ? 1f : (1f - Seg(boom, 0.75f, 1f))) * exit);
                SetAlpha(flash, Mathf.Max(0f, 0.85f - 2.4f * (t - 0.6f)));

                // the victim is launched UP and out, spinning (the Maniac flings sideways)
                float fly = EaseOut(Seg(t, 0.62f, 1f));
                victimFig.SetPos(0.9f + 0.9f * fly, -0.35f + 4.4f * fly - 1.4f * fly * fly);
                victimFig.SetRot(560f * fly);

                for (int i = 0; i < particles.Length; i++) {
                    float ph = Seg(t, 0.62f + i * 0.02f, 1f);
                    var sr = particles[i];
                    if (ph <= 0f) { SetAlpha(sr, 0f); continue; }
                    double ang = i * (Math.PI * 2.0 / particles.Length);
                    float r = 0.3f + EaseOut(ph) * 1.8f;
                    sr.transform.localPosition = new Vector3(-0.55f + (float)Math.Cos(ang) * r, -0.4f + (float)Math.Sin(ang) * r * 0.7f + 0.4f * ph, 0f);
                    sr.transform.localScale = Vector3.one * (0.9f + 1.5f * ph);
                    SetAlpha(sr, (1f - ph) * 0.75f * exit);
                }
            }
        }

        // Guessed: TOR's own target icon locks onto the victim and the verdict lands.
        private static void UpdateGuesserShot(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.14f));
            victimFig.SetAlpha(ein * exit);
            if (t >= 0.1f) Sound(1, () => UCAssets.PlayWitnessSting());

            // the reticle contracts from screen-size onto the victim, spinning slightly
            float lockOn = Smooth(Seg(t, 0.08f, 0.42f));
            propA.transform.localPosition = new Vector3(0f, 0.25f, 0f);
            propA.transform.localScale = Vector3.one * Mathf.Lerp(3.4f, 1.05f, lockOn);
            propA.transform.localRotation = Quaternion.Euler(0, 0, 90f * (1f - lockOn));
            // locked: two confirmation blinks
            float blink = t >= 0.46f && t < 0.62f ? (((int)(Time.time * 12f)) % 2 == 0 ? 1f : 0.35f) : 1f;
            propA.color = new Color(1f, blink < 1f ? 0.25f : 1f, blink < 1f ? 0.25f : 1f, ein * blink * (1f - Seg(t, 0.66f, 0.78f)));

            bool judged = t >= 0.64f;
            if (judged) {
                SetAlpha(flash, Mathf.Max(0f, 0.45f - 2.2f * (t - 0.64f)));
                float fall = Smooth(Seg(t, 0.68f, 0.92f));
                victimFig.SetRot(82f * fall);
                victimFig.SetPos(0.3f * fall, -0.35f - 0.55f * fall);
            }

            // red sparks burst off the lock-on moment
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.64f + i * 0.02f, 0.92f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                double ang = i * (Math.PI * 2.0 / particles.Length) + 0.8;
                float r = 0.3f + EaseOut(ph) * 1.3f;
                sr.transform.localPosition = new Vector3((float)Math.Cos(ang) * r, 0.25f + (float)Math.Sin(ang) * r, 0f);
                sr.transform.localScale = Vector3.one * (0.7f - 0.4f * ph);
                SetAlpha(sr, (1f - ph) * 0.85f * exit);
            }
        }

        // The masked thief plucks the glowing role card out of the victim and absorbs it.
        private static void UpdateThiefSteal(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.16f));
            victimFig.SetAlpha(ein * exit);

            float dash = EaseOut(Seg(t, 0.08f, 0.3f));
            float kx = Mathf.Lerp(-4.2f, 0.35f, dash);
            killerFig.SetPos(kx, -0.35f);
            killerFig.SetAlpha(Mathf.Min(1f, ein + dash) * exit);
            // domino mask rides on the thief's visor
            propB.transform.localPosition = new Vector3(kx + 0.4f, 0.52f, 0f);
            SetAlpha(propB, Mathf.Min(1f, ein + dash) * exit);

            if (t >= 0.36f) Sound(1, () => TorSfx("shifterShift"));

            // the role card rises out of the victim, arcs over to the thief, gets absorbed
            float rise = Smooth(Seg(t, 0.36f, 0.52f));
            float carry = Smooth(Seg(t, 0.52f, 0.72f));
            float absorb = Seg(t, 0.72f, 0.8f);
            float cx = Mathf.Lerp(1.9f, kx + 0.3f, carry);
            float cy = Mathf.Lerp(0.1f, 1.35f, rise) + Mathf.Sin(carry * Mathf.PI) * 0.7f;   // arced hand-off
            propA.transform.localPosition = new Vector3(cx, cy - 1.2f * absorb, 0f);
            propA.transform.localScale = Vector3.one * (0.5f * (1f - 0.7f * absorb));
            propA.transform.localRotation = Quaternion.Euler(0, 0, -14f * carry);
            SetAlpha(propA, rise > 0f ? (1f - absorb) * exit : 0f);
            if (absorb > 0f) SetAlpha(flash, Mathf.Max(0f, 0.25f - 1.6f * (t - 0.72f)));

            // gold sparkle trail behind the card
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.4f + i * 0.05f, 0.78f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                sr.transform.localPosition = new Vector3(cx + Jitter(0.18f), cy - 0.2f - 0.3f * ph, 0f);
                sr.transform.localScale = Vector3.one * (0.5f - 0.25f * ph);
                SetAlpha(sr, Mathf.Sin(ph * Mathf.PI) * 0.8f * exit);
            }

            // robbed of its role, the victim grays out and collapses
            float drain = Smooth(Seg(t, 0.5f, 0.78f));
            victimFig.SetTint(Color.Lerp(victimFig.color, new Color(0.5f, 0.5f, 0.55f), 0.8f * drain), exit);
            if (t >= 0.78f) {
                float fall = Smooth(Seg(t, 0.78f, 0.96f));
                victimFig.SetRot(80f * fall);
                victimFig.SetPos(1.9f + 0.3f * fall, -0.35f - 0.55f * fall);
            }
        }

        // The steal slips: the card flashes red and shatters, the mask drops, the thief keels over.
        private static void UpdateThiefFail(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.16f));
            killerFig.SetAlpha(ein * exit);

            float reach = Smooth(Seg(t, 0.18f, 0.34f));
            bool failed = t >= 0.46f;
            float kx = -0.4f + 0.35f * reach;
            if (!failed) {
                killerFig.SetPos(kx, -0.35f);
                killerFig.SetRot(-10f * reach);
            }
            propB.transform.localPosition = new Vector3(kx + 0.4f, 0.52f, 0f);

            // the coveted card appears... and denies
            float appear = Smooth(Seg(t, 0.3f, 0.42f));
            if (!failed) {
                propA.transform.localPosition = new Vector3(1.15f, 0.15f + 0.05f * Mathf.Sin(t * 11f), 0f);
                propA.transform.localScale = Vector3.one * (0.5f * appear);
                propA.color = new Color(1f, 1f, 1f, appear * exit);
            } else {
                Sound(1, () => TorSfx("fail"));
                float deny = Seg(t, 0.46f, 0.6f);
                bool red = ((int)(Time.time * 16f)) % 2 == 0;
                propA.color = new Color(1f, red ? 0.3f : 1f, red ? 0.3f : 1f, (1f - Seg(t, 0.58f, 0.68f)) * exit);
                propA.transform.localPosition = new Vector3(1.15f + Jitter(0.04f * (1f - deny)), 0.15f, 0f);

                // recoil: mask slips off and drops, the thief staggers and falls backward
                float stagger = Smooth(Seg(t, 0.52f, 0.68f));
                float fall = Smooth(Seg(t, 0.68f, 0.94f));
                killerFig.SetPos(kx - 0.6f * stagger - 0.5f * fall, -0.35f - 0.5f * fall);
                killerFig.SetRot(10f * stagger + 75f * fall);
                float drop = EaseIn(Seg(t, 0.56f, 0.86f));
                propB.transform.localPosition = new Vector3(kx + 0.4f - 0.25f * drop, 0.52f - 2.4f * drop, 0f);
                propB.transform.localRotation = Quaternion.Euler(0, 0, 200f * drop);
            }

            // a puff of gray disappointment
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.5f + i * 0.05f, 0.9f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                sr.transform.localPosition = new Vector3(1.15f + Jitter(0.05f), 0.15f + 0.7f * ph, 0f);
                sr.transform.localScale = Vector3.one * (0.4f + 0.6f * ph);
                SetAlpha(sr, (1f - ph) * 0.45f * exit);
            }
        }

        // Feral pounce: two crossing claw rips, the victim goes down fast.
        private static void UpdateJackalClaw(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.14f));
            victimFig.SetAlpha(ein * exit);

            // pounce: fast approach with a slight forward lean
            float pounce = EaseOut(Seg(t, 0.12f, 0.3f));
            killerFig.SetPos(Mathf.Lerp(-4.2f, -0.3f, pounce), -0.35f + 0.5f * Mathf.Sin(pounce * Mathf.PI));
            killerFig.SetRot(-14f * Mathf.Sin(pounce * Mathf.PI));
            killerFig.SetAlpha(Mathf.Min(1f, ein + pounce) * exit);

            if (t >= 0.3f) Sound(1, () => UCAssets.PlayScoutWhoosh(PlayerControl.LocalPlayer.GetTruePosition(), 0.9f));

            // first rip, then the crossing second
            float rip1 = Seg(t, 0.3f, 0.42f);
            SetAlpha(propA, rip1 > 0f ? Mathf.Sin(Mathf.Min(rip1, 1f) * Mathf.PI) * 0.95f : 0f);
            propA.transform.localScale = Vector3.one * (0.85f + 0.25f * EaseOut(rip1));
            float rip2 = Seg(t, 0.44f, 0.56f);
            SetAlpha(propB, rip2 > 0f ? Mathf.Sin(Mathf.Min(rip2, 1f) * Mathf.PI) * 0.95f : 0f);
            propB.transform.localScale = Vector3.one * (0.7f + 0.25f * EaseOut(rip2));
            if (rip1 > 0f && rip1 < 1f) SetAlpha(flash, 0.2f * Mathf.Sin(rip1 * Mathf.PI));
            else if (rip2 > 0f && rip2 < 1f) SetAlpha(flash, 0.2f * Mathf.Sin(rip2 * Mathf.PI));

            // the victim is shoved with each rip, then drops
            float shove = 0.18f * EaseOut(rip1) + 0.22f * EaseOut(rip2);
            if (t < 0.6f) {
                victimFig.SetPos(1.4f + shove, -0.35f);
                bool hot = (rip1 > 0f && rip1 < 1f) || (rip2 > 0f && rip2 < 1f);
                victimFig.SetTint(hot && ((int)(Time.time * 24f)) % 2 == 0 ? Color.white : victimFig.color, exit);
            } else {
                float fall = Smooth(Seg(t, 0.6f, 0.85f));
                victimFig.SetTint(victimFig.color, exit);
                victimFig.SetRot(-80f * fall);
                victimFig.SetPos(1.8f - 0.25f * fall, -0.35f - 0.55f * fall);
            }

            // teal sparks off the rips
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.32f + i * 0.04f, 0.7f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                double ang = i * (Math.PI * 2.0 / particles.Length) - 0.6;
                float r = 0.2f + EaseOut(ph) * 1.1f;
                sr.transform.localPosition = new Vector3(1.45f + (float)Math.Cos(ang) * r, 0.1f + (float)Math.Sin(ang) * r, 0f);
                sr.transform.localScale = Vector3.one * (0.6f - 0.3f * ph);
                SetAlpha(sr, (1f - ph) * 0.8f * exit);
            }
        }

        // The wanted poster drops in with the victim's portrait; the hunter cashes the bounty
        // and the coins rain.
        private static void UpdateBountyHit(float t, float exit) {
            float ein = EaseOut(Seg(t, 0f, 0.16f));
            victimFig.SetAlpha(ein * exit);
            killerFig.SetPos(Mathf.Lerp(-4.2f, -0.6f, EaseOut(Seg(t, 0.14f, 0.36f))), -0.35f);
            killerFig.SetAlpha(ein * exit);

            // poster (with portrait) drops and settles
            float drop = Bounce(Seg(t, 0.02f, 0.3f));
            float py = Mathf.Lerp(4f, 0.55f, drop);
            propA.transform.localPosition = new Vector3(-2.35f, py, 0f);
            SetAlpha(propA, exit);
            extraFig.SetPos(-2.35f, py - 0.85f);   // portrait framed by the empty poster middle
            extraFig.SetAlpha(0.95f * exit);

            // the claim: flash + the real victim goes down
            bool claimed = t >= 0.52f;
            if (!claimed) {
                victimFig.SetPos(Mathf.Lerp(4.2f, 1.7f, ein), -0.35f);
            } else {
                SetAlpha(flash, Mathf.Max(0f, 0.4f - 2f * (t - 0.52f)));
                float fall = Smooth(Seg(t, 0.56f, 0.8f));
                victimFig.SetRot(-80f * fall);
                victimFig.SetPos(1.7f + 0.3f * fall, -0.35f - 0.55f * fall);
                // poster gets a satisfied tilt once the bounty is in
                propA.transform.localRotation = Quaternion.Euler(0, 0, -7f * Smooth(Seg(t, 0.56f, 0.7f)));
                extraFig.go.transform.localRotation = propA.transform.localRotation;
            }
            if (t >= 0.72f) Sound(1, () => UCAssets.PlayRelicPickup(PlayerControl.LocalPlayer.GetTruePosition()));

            // coin rain over the hunter
            for (int i = 0; i < particles.Length; i++) {
                float ph = Seg(t, 0.72f + i * 0.03f, 1f);
                var sr = particles[i];
                if (ph <= 0f || ph >= 1f) { SetAlpha(sr, 0f); continue; }
                float x = -0.6f + (i - particles.Length / 2f) * 0.32f + Jitter(0.02f);
                sr.transform.localPosition = new Vector3(x, 1.6f - 2.6f * EaseIn(ph), 0f);
                sr.transform.localScale = Vector3.one * 0.5f;
                sr.transform.localRotation = Quaternion.Euler(0, 0, 240f * ph * (i % 2 == 0 ? 1f : -1f));
                SetAlpha(sr, Mathf.Min(1f, (1f - ph) * 3f) * exit);
            }
        }
    }
}
