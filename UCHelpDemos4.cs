// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHelpDemos4 - bespoke help-panel demo vignettes (see UCHelpMenu.RegisterDemo) for eight
 * TOR crew/neutral roles: Pursuer, Thief, Mayor, Portalmaker, Engineer, Sheriff, Deputy, Lighter.
 * Each Animate method is STATELESS - every position/color/alpha is recomputed purely from
 * UCHelpMenu.stageT (via P()/Seg()/Move()) so the loop can never drift and needs no bookkeeping.
 */

using System;
using UnityEngine;
using static UnknownsCollection.UCHelpMenu;

namespace UnknownsCollection {
    public static class UCHelpDemos4 {
        public static void Register() {
            RegisterDemo("Pursuer", CreatePursuer, AnimatePursuer);
            RegisterDemo("Thief", CreateThief, AnimateThief);
            RegisterDemo("Mayor", CreateMayor, AnimateMayor);
            RegisterDemo("Portalmaker", CreatePortalmaker, AnimatePortalmaker);
            RegisterDemo("Engineer", CreateEngineer, AnimateEngineer);
            RegisterDemo("Sheriff", CreateSheriff, AnimateSheriff);
            RegisterDemo("Deputy", CreateDeputy, AnimateDeputy);
            RegisterDemo("Lighter", CreateLighter, AnimateLighter);
        }

        // ---- demo-only palette (fixed per role, independent of live player colors) ----
        private static readonly Color PursuerColor = new Color(0.72f, 0.86f, 0.80f);      // pale, ghost-like
        private static readonly Color ThiefColor = new Color(0.55f, 0.50f, 0.62f);        // murky, sneaky
        private static readonly Color MayorColor = new Color(0.30f, 0.68f, 0.55f);        // civic teal-green
        private static readonly Color PortalmakerColor = new Color(0.42f, 0.42f, 0.88f);  // indigo
        private static readonly Color EngineerColor = new Color(0.95f, 0.55f, 0.12f);     // work-order amber
        private static readonly Color SheriffColor = new Color(0.93f, 0.72f, 0.22f);      // badge gold
        private static readonly Color DeputyColor = new Color(0.75f, 0.78f, 0.85f);       // junior badge silver
        private static readonly Color LighterColor = new Color(0.95f, 0.90f, 0.70f);      // warm flashlight glow

        // Shared entrance/exit envelope: 0 at the very start and end of the loop, 1 in between.
        private static float EdgeFade(float p, float inEnd, float outStart)
            => Ease(Seg(p, 0f, inEnd)) * (1f - Ease(Seg(p, outStart, 1f)));

        // A short dip-to-zero-and-back used between two unrelated "acts" in the same loop, so a
        // hard scene reset (repositioning an actor) never reads as a visible jump.
        private static float FadeGap(float p, float outStart, float outEnd, float inStart, float inEnd) {
            float dip = Ease(Seg(p, outStart, outEnd)) * (1f - Ease(Seg(p, inStart, inEnd)));
            return 1f - dip;
        }

        // ====================================================================
        // Pursuer - arms Blank, the next murder attempt (even the Sheriff's) simply misses.
        // ====================================================================
        private static void CreatePursuer() {
            Crew("pur", PursuerColor);
            Crew("imp", DemoRed);
            StageSprite("shield", UCFx.Ring, Color.white, 0.55f, 507);
            StageSprite("miss", UCFx.Ring, Color.white, 0.1f, 508);
            StageCap("missCap", "MISS", 1.0f, Accent);
            MakeBtn("blankBtn", null);
        }

        private static void AnimatePursuer() {
            float p = P(7f);
            float fade = EdgeFade(p, 0.08f, 0.94f);
            const float purX = -0.85f;

            float approach = Seg(p, 0.04f, 0.30f);
            float recoil = Seg(p, 0.52f, 0.84f);
            float impX = p < 0.5f ? Move(1.5f, -0.35f, approach) : Move(-0.35f, 1.5f, recoil);
            bool impWalk = Mid(approach) || Mid(recoil);

            FigPut("pur", purX, 0f, false, 0f);
            FigCol("pur", PursuerColor, fade);
            FigPut("imp", impX, 0f, p < 0.5f, impWalk ? 1f : 0f);
            FigCol("imp", DemoRed, fade);

            float armProg = Seg(p, 0.22f, 0.36f);
            BtnPop("blankBtn", purX, BtnY, armProg);
            float shieldA = Seg(p, 0.24f, 0.34f) * (1f - Seg(p, 0.56f, 0.66f));
            Put("shield", purX, FloorY + 0.25f);
            ColA("shield", Color.white, fade * shieldA * (0.5f + 0.4f * Mathf.Sin(p * 40f)));

            Burst("miss", purX + 0.18f, FloorY + 0.25f, Seg(p, 0.42f, 0.56f), 0.5f, Color.white);
            PutCap("missCap", purX + 0.05f, 0.24f);
            CapA("missCap", fade * Seg(p, 0.44f, 0.50f) * (1f - Seg(p, 0.62f, 0.74f)));
        }

        // ====================================================================
        // Thief - must kill a killer to take over their role; killing anyone else kills the
        // Thief instead (same fate as a misfiring Sheriff). Shown back to back.
        // ====================================================================
        private static void CreateThief() {
            Crew("thief", ThiefColor);
            Crew("imp", DemoRed);
            Crew("vic", DemoBlue);
            StageCap("plus", "+", 1.1f, Accent);
            StageCap("minus", "-", 1.3f, Accent);
            StageSprite("fx1", UCFx.Ring, Accent, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateThief() {
            float p = P(8f);
            float gap = FadeGap(p, 0.40f, 0.44f, 0.47f, 0.51f);
            float edge = EdgeFade(p, 0.05f, 0.95f);

            // Act 1: the Thief walks up to the Impostor and kills it -> takeover, "+"
            float walkInA = Seg(p, 0.03f, 0.16f);
            float walkOutA = Seg(p, 0.34f, 0.44f);
            float thiefXA = p < 0.34f ? Move(-1.5f, -0.35f, walkInA) : Move(-0.35f, -1.6f, walkOutA);
            float killA = Seg(p, 0.18f, 0.26f);
            float redBlend = Ease(Seg(p, 0.20f, 0.30f)) * (1f - Ease(Seg(p, 0.34f, 0.40f)));

            float impA = edge * (1f - Ease(Seg(p, 0.34f, 0.40f)));
            FigPut("imp", -0.35f, 0f, true, 0f);
            FigCol("imp", DemoRed, impA);
            FigDead("imp", killA);
            Burst("fx1", -0.35f, FloorY + 0.25f, Seg(p, 0.20f, 0.32f), 0.7f, Accent);
            PutCap("plus", -0.35f, 0.22f);
            CapA("plus", impA * Seg(p, 0.22f, 0.28f) * (1f - Seg(p, 0.34f, 0.40f)));

            // Act 2: the Thief walks up to a plain Crewmate instead -> misfire, Thief drops
            float walkInB = Seg(p, 0.50f, 0.64f);
            float thiefXB = Move(-1.5f, -0.20f, walkInB);
            float attemptB = Seg(p, 0.66f, 0.74f);
            float deadB = Seg(p, 0.74f, 0.86f);

            float thiefX = p < 0.44f ? thiefXA : thiefXB;
            bool thiefWalk = Mid(walkInA) || Mid(walkOutA) || Mid(walkInB);
            float thiefAlpha = edge * gap;
            FigPut("thief", thiefX, 0f, p < 0.44f ? (walkOutA > 0f && walkOutA < 1f) : false, thiefWalk ? 1f : 0f);
            FigCol("thief", Color.Lerp(ThiefColor, DemoRed, redBlend), thiefAlpha);
            FigDead("thief", deadB);

            float vicA = edge * Ease(Seg(p, 0.47f, 0.53f));
            FigPut("vic", 0.05f, 0f, true, 0f);
            FigCol("vic", DemoBlue, vicA);
            Burst("fx2", -0.05f, FloorY + 0.25f, attemptB, 0.6f, DemoRed);
            PutCap("minus", thiefXB, 0.22f);
            CapA("minus", vicA * Seg(p, 0.76f, 0.82f) * (1f - Seg(p, 0.90f, 0.97f)));
        }

        // ====================================================================
        // Mayor - a single Mayor vote counts twice, tipping a near-tied exile.
        // ====================================================================
        private static void CreateMayor() {
            Crew("mayor", MayorColor);
            Crew("red", DemoRed);
            StageRect("barBg", new Color(1f, 1f, 1f, 0.14f), 1.5f, 0.09f);
            StageRect("bar", DemoRed, 0.02f, 0.07f);
            StageRect("thresh", new Color(1f, 1f, 1f, 0.55f), 0.02f, 0.13f, 506);
            StageDot("d1", Accent, 0.08f);
            StageDot("d2", Accent, 0.08f);
            StageCap("x2", "x2", 1.0f, Accent);
            StageCap("out", "OUT", 0.9f, Accent);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateMayor() {
            float p = P(7f);
            float fade = EdgeFade(p, 0.08f, 0.94f);

            FigPut("mayor", 0.5f, 0f, false, 0f);
            FigCol("mayor", MayorColor, fade);

            float othersFill = 0.55f * Ease(Seg(p, 0.06f, 0.34f));
            float jumpFill = 0.37f * Ease(Seg(p, 0.40f, 0.50f));
            float totalFill = Mathf.Min(1f, othersFill + jumpFill);
            const float barLeft = -1.5f;
            Put("barBg", barLeft + 0.75f, 0.3f);
            ColA("barBg", Color.white, 0.14f * fade);
            Put("thresh", barLeft + 1.5f * 0.72f, 0.3f);
            ColA("thresh", Color.white, 0.5f * fade);
            BarLeft("bar", barLeft, 0.3f, 1.5f * totalFill, 0.07f);
            ColA("bar", DemoRed, 0.9f * fade);

            float castWin = Seg(p, 0.36f, 0.42f);
            PutCap("x2", 0.5f, 0.5f);
            CapA("x2", fade * castWin * (1f - Seg(p, 0.5f, 0.58f)));
            float ballotA = Seg(p, 0.38f, 0.46f);
            float ballotB = Seg(p, 0.40f, 0.48f);
            float tipX = barLeft + 1.5f * totalFill;
            Put("d1", Move(0.5f, tipX, ballotA), Move(0.15f, 0.3f, ballotA));
            ColA("d1", Accent, fade * (Mid(ballotA) ? 0.9f : 0f));
            Put("d2", Move(0.5f, tipX, ballotB), Move(0.15f, 0.3f, ballotB));
            ColA("d2", Accent, fade * (Mid(ballotB) ? 0.9f : 0f));

            float exiled = Seg(p, 0.58f, 0.70f);
            FigPut("red", 1.4f, 0f, true, 0f);
            FigCol("red", Color.Lerp(DemoRed, new Color(0.5f, 0.5f, 0.55f), exiled), fade);
            FigDead("red", exiled);
            Burst("fx", 1.4f, FloorY + 0.25f, Seg(p, 0.58f, 0.74f), 0.85f, DemoRed);
            PutCap("out", 1.4f, 0.26f);
            CapA("out", fade * Seg(p, 0.60f, 0.68f) * (1f - Seg(p, 0.86f, 0.95f)));
        }

        // ====================================================================
        // Portalmaker - two linked portals go live after the next meeting; anyone can then
        // step through one and instantly arrive at the other.
        // ====================================================================
        private static void CreatePortalmaker() {
            Crew("pm", PortalmakerColor);
            Crew("crew", DemoBlue);
            StageSprite("ringA", UCFx.Ring, PortalmakerColor, 0.5f, 507);
            StageSprite("ringB", UCFx.Ring, PortalmakerColor, 0.5f, 507);
            StageSprite("meetFx", UCFx.Ring, Accent, 0.1f, 508);
            MakeBtn("placeBtnA", null);
            MakeBtn("placeBtnB", null);
        }

        private static void AnimatePortalmaker() {
            float p = P(8f);
            float fade = EdgeFade(p, 0.05f, 0.95f);
            const float portalAX = -1.1f, portalBX = 1.1f;

            float walk1 = Seg(p, 0.02f, 0.13f);
            float walk2 = Seg(p, 0.24f, 0.40f);
            float pmX = p < 0.24f ? Move(-1.6f, portalAX, walk1) : Move(portalAX, portalBX, walk2);
            FigPut("pm", pmX, 0f, false, (Mid(walk1) || Mid(walk2)) ? 1f : 0f);
            FigCol("pm", PortalmakerColor, fade);

            BtnPop("placeBtnA", portalAX, BtnY, Seg(p, 0.13f, 0.20f));
            BtnPop("placeBtnB", portalBX, BtnY, Seg(p, 0.40f, 0.47f));

            float meeting = Seg(p, 0.56f, 0.68f);
            float ringGlow = Mid(meeting) ? 0.5f + 0.5f * Mathf.Sin(p * 60f) : 0f;
            float ringAAlpha = Ease(Seg(p, 0.15f, 0.24f)) * (0.6f + 0.4f * ringGlow);
            float ringBAlpha = Ease(Seg(p, 0.42f, 0.51f)) * (0.6f + 0.4f * ringGlow);
            Put("ringA", portalAX, FloorY + 0.25f);
            ColA("ringA", PortalmakerColor, fade * ringAAlpha);
            Put("ringB", portalBX, FloorY + 0.25f);
            ColA("ringB", PortalmakerColor, fade * ringBAlpha);
            Burst("meetFx", 0f, FloorY + 0.25f, Seg(p, 0.58f, 0.70f), 0.9f, Accent);

            float crewIn = Seg(p, 0.70f, 0.82f);
            float crewOut = Seg(p, 0.88f, 0.97f);
            float crewX = p < 0.85f ? Move(-1.6f, portalAX, crewIn) : Move(portalBX, 1.6f, crewOut);
            float crewAlpha;
            if (p < 0.70f) crewAlpha = 0f;
            else if (p < 0.82f) crewAlpha = fade;
            else if (p < 0.85f) crewAlpha = fade * (1f - Ease(Seg(p, 0.82f, 0.85f)));
            else crewAlpha = fade * Ease(Seg(p, 0.85f, 0.88f));
            FigPut("crew", crewX, 0f, false, (Mid(crewIn) || Mid(crewOut)) ? 1f : 0f);
            FigCol("crew", DemoBlue, crewAlpha);
        }

        // ====================================================================
        // Engineer - fixes a sabotage from anywhere, then risks a highlighted, kill-button-lit
        // vent while hiding in it.
        // ====================================================================
        private static void CreateEngineer() {
            Crew("eng", EngineerColor);
            Crew("imp", DemoRed);
            StagePic("console", UCAssets.OverlayConsole, 0.42f, 505);
            StagePic("bolt", UCAssets.OverlayBoltA, 0.26f, 507);
            StageRect("vent", new Color(0.18f, 0.19f, 0.22f, 1f), 0.32f, 0.09f, 504);
            StageSprite("outline", UCFx.Ring, DemoRed, 0.42f, 507);
            MakeBtn("fixBtn", null);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateEngineer() {
            float p = P(7.5f);
            float fade = EdgeFade(p, 0.04f, 0.95f);
            const float engStartX = -1.3f, consoleX = 1.2f, ventX = -0.2f;

            Put("console", consoleX, FloorY + 0.23f);
            float broken = Seg(p, 0.03f, 0.12f) * (1f - Seg(p, 0.26f, 0.34f));
            ColA("console", Color.Lerp(Color.white, new Color(1f, 0.45f, 0.4f), broken), fade);

            float fixProg = Seg(p, 0.14f, 0.22f);
            BtnPop("fixBtn", engStartX, BtnY, fixProg);
            bool zap = p > 0.16f && p < 0.30f && ((int)(p * 90f)) % 3 != 0;
            Put("bolt", consoleX, FloorY + 0.24f + 0.02f * Mathf.Sin(p * 90f));
            ColA("bolt", new Color(0.75f, 0.9f, 1f), zap ? fade * 0.9f : 0f);

            float walkIn = Seg(p, 0.40f, 0.52f);
            float ventX2 = Move(engStartX, ventX, walkIn);
            float duck = Seg(p, 0.52f, 0.58f);
            float caught = Seg(p, 0.86f, 0.94f);
            FigPut("eng", ventX2, -0.16f * Ease(duck) * (1f - caught), false, Mid(walkIn) ? 1f : 0f);
            FigCol("eng", EngineerColor, fade);
            FigDead("eng", caught);

            Put("vent", ventX, FloorY + 0.02f);
            ColA("vent", new Color(0.18f, 0.19f, 0.22f, 1f), fade);
            float warn = Seg(p, 0.58f, 0.84f);
            Put("outline", ventX, FloorY + 0.20f);
            ColA("outline", DemoRed, Mid(warn) ? fade * (0.35f + 0.35f * Mathf.Sin(p * 50f)) : 0f);

            float impWalk = Seg(p, 0.60f, 0.78f);
            float impKill = Seg(p, 0.80f, 0.86f);
            float impX = p < 0.80f ? Move(1.5f, 0.30f, impWalk) : Move(0.30f, ventX + 0.15f, impKill);
            FigPut("imp", impX, 0f, true, Mid(impWalk) ? 1f : 0f);
            FigCol("imp", DemoRed, fade * Ease(Seg(p, 0.56f, 0.62f)));
            Burst("fx", ventX, FloorY + 0.10f, Seg(p, 0.84f, 0.94f), 0.7f, DemoRed);
        }

        // ====================================================================
        // Sheriff - shoots and kills Impostors; shooting a Crewmate kills the Sheriff instead.
        // ====================================================================
        private static void CreateSheriff() {
            Crew("sher", SheriffColor);
            Crew("imp", DemoRed);
            Crew("vic", DemoBlue);
            StageCap("plus", "+", 1.1f, Accent);
            StageCap("minus", "-", 1.3f, Accent);
            MakeBtn("shootBtnA", KillButtonSprite(null));
            MakeBtn("shootBtnB", KillButtonSprite(null));
            StageSprite("fx1", UCFx.Ring, DemoRed, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateSheriff() {
            float p = P(8f);
            float gap = FadeGap(p, 0.40f, 0.44f, 0.47f, 0.51f);
            float edge = EdgeFade(p, 0.05f, 0.95f);

            // Act 1: correct call - the Impostor drops
            float walkInA = Seg(p, 0.03f, 0.16f);
            float walkOutA = Seg(p, 0.34f, 0.44f);
            float sherXA = p < 0.34f ? Move(-1.5f, -0.45f, walkInA) : Move(-0.45f, -1.6f, walkOutA);
            BtnPop("shootBtnA", -0.45f, BtnY, Seg(p, 0.16f, 0.24f));
            float killA = Seg(p, 0.22f, 0.30f);
            float impA = edge * (1f - Ease(Seg(p, 0.34f, 0.40f)));
            FigPut("imp", -0.05f, 0f, true, 0f);
            FigCol("imp", DemoRed, impA);
            FigDead("imp", killA);
            Burst("fx1", -0.05f, FloorY + 0.25f, Seg(p, 0.22f, 0.34f), 0.7f, DemoRed);
            PutCap("plus", -0.45f, 0.22f);
            CapA("plus", impA * Seg(p, 0.24f, 0.30f) * (1f - Seg(p, 0.34f, 0.40f)));

            // Act 2: wrong call - the Sheriff shoots a plain Crewmate and drops themself instead
            float walkInB = Seg(p, 0.50f, 0.64f);
            float sherXB = Move(-1.5f, -0.30f, walkInB);
            BtnPop("shootBtnB", -0.30f, BtnY, Seg(p, 0.64f, 0.72f));
            float attemptB = Seg(p, 0.70f, 0.78f);
            float deadB = Seg(p, 0.78f, 0.88f);

            float sherX = p < 0.44f ? sherXA : sherXB;
            bool sherWalk = Mid(walkInA) || Mid(walkOutA) || Mid(walkInB);
            bool sherFaceLeft = p < 0.44f && Mid(walkOutA);
            float sherAlpha = edge * gap;
            FigPut("sher", sherX, 0f, sherFaceLeft, sherWalk ? 1f : 0f);
            FigCol("sher", SheriffColor, sherAlpha);
            FigDead("sher", deadB);

            float vicA = edge * Ease(Seg(p, 0.47f, 0.53f));
            FigPut("vic", 0.15f, 0f, true, 0f);
            FigCol("vic", DemoBlue, vicA);
            Burst("fx2", -0.30f, FloorY + 0.25f, attemptB, 0.6f, DemoRed);
            PutCap("minus", sherXB, 0.22f);
            CapA("minus", vicA * Seg(p, 0.80f, 0.86f) * (1f - Seg(p, 0.92f, 0.98f)));
        }

        // ====================================================================
        // Deputy - handcuffs a player, disabling kill/ability/vent/report until the cuffs
        // wear off.
        // ====================================================================
        private static void CreateDeputy() {
            Crew("dep", DeputyColor);
            Crew("imp", DemoRed);
            StageSprite("cuffL", UCFx.Ring, Accent, 0.13f, 508);
            StageSprite("cuffR", UCFx.Ring, Accent, 0.13f, 508);
            StageRect("chain", Accent, 0.1f, 0.016f, 508);
            StageSprite("fxSnap", UCFx.Ring, Accent, 0.1f, 508);
            StageRect("cbarBg", new Color(1f, 1f, 1f, 0.12f), 0.8f, 0.05f);
            StageRect("cbar", Accent, 0.8f, 0.038f);
            StageCap("x", "X", 1.2f, DemoRed);
            MakeBtn("cuffBtn", null);
            MakeBtn("killBtn", KillButtonSprite(null));
        }

        private static void AnimateDeputy() {
            float p = P(7f);
            float fade = EdgeFade(p, 0.05f, 0.95f);
            const float impX = 0.0f;

            float walkIn = Seg(p, 0.02f, 0.18f);
            float depX = Move(-1.5f, -0.4f, walkIn);
            FigPut("dep", depX, 0f, false, Mid(walkIn) ? 1f : 0f);
            FigCol("dep", DeputyColor, fade);

            float cuffIn = Seg(p, 0.24f, 0.32f), cuffOut = Seg(p, 0.78f, 0.86f);
            float cuffed = Ease(cuffIn) * (1f - Ease(cuffOut));

            // the impostor rattles against the cuffs in short bursts while locked up
            float rattle = cuffed * Mathf.Clamp01(Mathf.Sin(stageT * 2.1f) * 4f - 2.6f);
            float shake = 0.025f * Mathf.Sin(stageT * 26f) * rattle;
            FigPut("imp", impX + shake, 0f, true, 0.45f * rattle);
            FigCol("imp", DemoRed, fade);

            BtnPop("cuffBtn", depX, BtnY, Seg(p, 0.18f, 0.26f));

            // a pair of cuffs (two rings + chain link) arcs over from the Deputy, snaps shut on
            // the impostor's wrists, and springs open again when the timer runs out
            float wristY = FloorY + 0.26f;
            float flyX = Move(depX + 0.2f, impX, cuffIn) + shake;
            float flyY = Move(wristY + 0.24f, wristY, cuffIn) + 0.14f * Mathf.Sin(Ease(cuffIn) * Mathf.PI);
            float gap = 0.085f + 0.05f * (1f - Ease(cuffIn)) + 0.16f * Ease(cuffOut);
            float cuffA = fade * Ease(Seg(p, 0.24f, 0.27f)) * (1f - Ease(Seg(p, 0.82f, 0.88f)));
            Put("cuffL", flyX - gap, flyY);
            Put("cuffR", flyX + gap, flyY);
            ColA("cuffL", Accent, cuffA);
            ColA("cuffR", Accent, cuffA);
            Put("chain", flyX, flyY);
            Size2("chain", Mathf.Max(0.02f, gap * 2f - 0.08f), 0.016f);
            ColA("chain", Accent, cuffA);
            Burst("fxSnap", impX, wristY, Seg(p, 0.315f, 0.42f), 0.45f, Accent);

            BarLeft("cbarBg", -1.6f, 0.30f, 0.8f, 0.05f);
            ColA("cbarBg", Color.white, 0.12f * fade * Ease(cuffIn) * (1f - Ease(cuffOut)));
            float remaining = 1f - Seg(p, 0.30f, 0.80f);
            BarLeft("cbar", -1.6f, 0.30f, 0.8f * Mathf.Clamp01(remaining), 0.038f);
            ColA("cbar", Accent, fade * Ease(cuffIn) * (1f - Ease(cuffOut)));

            BtnPop("killBtn", impX, BtnY, Seg(p, 0.34f, 0.90f));
            float xA = Ease(Seg(p, 0.36f, 0.42f)) * (1f - Ease(Seg(p, 0.76f, 0.82f)));
            PutCap("x", impX, BtnY);
            CapA("x", fade * xA);
        }

        // ====================================================================
        // Lighter - a movable flashlight-style vision cone whose strength differs depending
        // on whether the lights are on or off.
        // ====================================================================
        private static void CreateLighter() {
            StageRect("dark", Color.black, stageSize.x - 0.06f, stageSize.y - 0.06f, 504);
            StageSprite("cone", UCFx.Dot, LighterColor, 1.0f, 505);
            Crew("lig", LighterColor);
            Crew("crew", DemoBlue);
            StageCap("out", "LIGHTS OUT", 0.65f, Accent);
        }

        private static void AnimateLighter() {
            float p = P(7f);
            float fade = EdgeFade(p, 0.05f, 0.95f);
            const float crewX = 0.85f;

            // rises to "lights off" mid-loop, then settles back to "lights on" before the loop
            // wraps, so the animation never jumps state at the seam.
            float lightsOffRise = Ease(Seg(p, 0.46f, 0.54f));
            float lightsOffFall = 1f - Ease(Seg(p, 0.90f, 0.97f));
            float lightsOff = Mathf.Min(lightsOffRise, lightsOffFall);
            float overlayA = Mathf.Lerp(0.30f, 0.66f, lightsOff);
            bool flicker = p > 0.46f && p < 0.54f;
            if (flicker) overlayA += 0.12f * Mathf.Sin(p * 140f);
            Put("dark", 0f, 0f);
            ColA("dark", Color.black, fade * overlayA);

            float coneX = 1.3f * Mathf.Sin(p * 4f * Mathf.PI);
            float coneRadius = Mathf.Lerp(0.5f, 0.85f, lightsOff);
            Put("cone", coneX, FloorY + 0.25f);
            Size2("cone", coneRadius * 2f, coneRadius * 2f);
            ColA("cone", LighterColor, fade * Mathf.Lerp(0.5f, 0.8f, lightsOff));

            FigPut("lig", 0f, 0f, false, 0f);
            FigCol("lig", LighterColor, fade);

            float dist = Mathf.Abs(coneX - crewX);
            float revealed = Mathf.Clamp01(1f - dist / coneRadius);
            FigPut("crew", crewX, 0f, true, 0f);
            FigCol("crew", DemoBlue, fade * Mathf.Lerp(0.3f, 1f, revealed));

            PutCap("out", 0f, 0.4f);
            CapA("out", fade * Seg(p, 0.47f, 0.50f) * (1f - Seg(p, 0.56f, 0.62f)));
        }
    }
}
