// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHelpDemos1 - bespoke help-menu demo vignettes (UCHelpMenu.RegisterDemo) for the Mafia trio
 * and the "shapeshifter" cluster of TOR Impostor roles:
 *   Godfather, Mafioso, Janitor, Morphling, Camouflager, Vampire, Eraser, Trickster.
 *
 * Every Create/Animate pair follows UCHelpMenu's stateless-per-frame contract: Create<Role>()
 * registers actors/props once (Crew/StageDot/StageRect/StageSprite/StagePic/StageCap/MakeBtn),
 * Animate<Role>() derives every position/color/alpha purely from stageT each frame - nothing is
 * cached between frames. See UCHelpMenu's own "Tesla"/"Saboteur" cases for the reference style.
 */

using System;
using UnityEngine;
using static UnknownsCollection.UCHelpMenu;

namespace UnknownsCollection {
    public static class UCHelpDemos1 {
        public static void Register() {
            RegisterDemo("Godfather", CreateGodfather, AnimateGodfather);
            RegisterDemo("Mafioso", CreateMafioso, AnimateMafioso);
            RegisterDemo("Janitor", CreateJanitor, AnimateJanitor);
            RegisterDemo("Morphling", CreateMorphling, AnimateMorphling);
            RegisterDemo("Camouflager", CreateCamouflager, AnimateCamouflager);
            RegisterDemo("Vampire", CreateVampire, AnimateVampire);
            RegisterDemo("Eraser", CreateEraser, AnimateEraser);
            RegisterDemo("Trickster", CreateTrickster, AnimateTrickster);
        }

        // small stateless helper: 0 while `rise` hasn't started, ramps to 1 with `rise`, holds at 1,
        // ramps back down with `fall`. Lets a "hold a state, then release it" blend read as one line.
        private static float Plateau(float rise, float fall) => Mathf.Clamp01(Ease(rise) - Ease(fall));

        private static readonly Color MafiosoCol = new Color(0.85f, 0.5f, 0.35f);
        private static readonly Color JanitorCol = new Color(0.58f, 0.55f, 0.62f);
        private static readonly Color GarlicCol = new Color(0.85f, 0.95f, 0.6f);
        private static readonly Color TricksterCol = new Color(0.62f, 0.4f, 0.72f);
        private static readonly Color VentCol = new Color(0.6f, 0.62f, 0.66f);

        // ====================================================================
        // Godfather - a normal Impostor kill, with the two other Mafia members idling in the
        // background to show the three-member team they lead.
        // ====================================================================
        private static void CreateGodfather() {
            Crew("gf", DemoRed);
            Crew("maf", MafiosoCol);
            Crew("jan", JanitorCol);
            Crew("vic", DemoBlue);
            MakeBtn("killBtn", KillButtonSprite(null));
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
            StageCap("gfTag", "GF", 0.6f, Accent);
        }

        private static void AnimateGodfather() {
            float p = P(6.5f);
            float FigMidY = FloorY + 0.25f;

            // the rest of the Mafia trio: always present, idling
            FigPut("maf", 1.3f, 0f, true, 0.18f);
            FigCol("maf", MafiosoCol, 1f);
            FigPut("jan", 1.62f, 0f, true, 0.18f);
            FigCol("jan", JanitorCol, 1f);

            float appr = Seg(p, 0.04f, 0.34f);
            float retreat = Seg(p, 0.62f, 0.92f);
            float gx = p < 0.6f ? Move(-1.6f, -0.2f, appr) : Move(-0.2f, -1.6f, retreat);
            bool gfWalk = Mid(appr) || Mid(retreat);
            FigPut("gf", gx, 0f, p >= 0.6f, gfWalk ? 1f : 0f);
            FigCol("gf", DemoRed, 1f);
            PutCap("gfTag", gx, 0.24f);
            CapA("gfTag", 1f);

            FigPut("vic", 0.25f, 0f, true, 0f);
            float dead = Seg(p, 0.42f, 0.54f);
            FigCol("vic", Color.Lerp(DemoBlue, new Color(0.5f, 0.5f, 0.55f), dead), 1f);
            FigDead("vic", dead);

            BtnPop("killBtn", gx, BtnY, Seg(p, 0.3f, 0.5f));
            Burst("fx", 0.25f, FigMidY, Seg(p, 0.4f, 0.54f), 0.75f, DemoRed);
        }

        // ====================================================================
        // Mafioso - a first, BLOCKED kill attempt while the Godfather is alive, then the Godfather
        // dies and the Mafioso's kill button lights up for real.
        // ====================================================================
        private static void CreateMafioso() {
            Crew("gf", DemoRed);
            Crew("maf", MafiosoCol);
            Crew("vic", DemoBlue);
            MakeBtn("killBtn", KillButtonSprite(null));
            StageCap("blockX", "X", 1.1f, DemoRed);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateMafioso() {
            float p = P(8.5f);
            float FigMidY = FloorY + 0.25f;

            FigPut("vic", 0.55f, 0f, true, 0f);
            FigCol("vic", DemoBlue, 1f);

            // godfather idles at the side, then dies mid-loop (unlocking the Mafioso)
            float gfDead = Seg(p, 0.32f, 0.42f);
            FigPut("gf", 1.5f, 0f, true, 0.15f);
            FigCol("gf", DemoRed, 1f);
            FigDead("gf", gfDead);
            Burst("fx2", 1.5f, FigMidY, Seg(p, 0.31f, 0.43f), 0.7f, DemoRed);

            // first attempt: blocked while the Godfather is alive
            float appr1 = Seg(p, 0.02f, 0.16f);
            float retreat1 = Seg(p, 0.2f, 0.3f);
            float mx1 = p < 0.18f ? Move(-1.6f, -0.15f, appr1) : Move(-0.15f, -0.7f, retreat1);
            bool blocked = p < 0.3f;

            // second attempt: for real, after the Godfather has died
            float appr2 = Seg(p, 0.48f, 0.64f);
            float retreat2 = Seg(p, 0.82f, 0.97f);
            float mx2 = p < 0.8f ? Move(-0.7f, -0.15f, appr2) : Move(-0.15f, -1.6f, retreat2);

            float mx = blocked ? mx1 : mx2;
            bool facingRight = blocked ? p < 0.18f : p < 0.8f;
            bool walking = blocked ? (Mid(appr1) || Mid(retreat1)) : (Mid(appr2) || Mid(retreat2));
            FigPut("maf", mx, 0f, !facingRight, walking ? 1f : 0f);
            FigCol("maf", MafiosoCol, 1f);

            BtnPop("killBtn", mx, BtnY, blocked ? Seg(p, 0.1f, 0.24f) : Seg(p, 0.56f, 0.76f));
            PutCap("blockX", mx1, 0.22f);
            CapA("blockX", Seg(p, 0.12f, 0.18f) * (1f - Seg(p, 0.26f, 0.32f)));

            float dead = Seg(p, 0.68f, 0.8f);
            FigCol("vic", Color.Lerp(DemoBlue, new Color(0.5f, 0.5f, 0.55f), dead), 1f);
            FigDead("vic", dead);
            Burst("fx", 0.55f, FigMidY, Seg(p, 0.66f, 0.8f), 0.75f, DemoRed);
        }

        // ====================================================================
        // Janitor - can't kill, but makes an existing body vanish (dragged off/hidden) instead of
        // being reported.
        // ====================================================================
        private static void CreateJanitor() {
            Crew("body", DemoWhite);
            Crew("jan", JanitorCol);
            MakeBtn("hideBtn", null);
            StageSprite("s0", UCFx.Smoke, DemoGray, 0.24f, 507);
            StageSprite("s1", UCFx.Smoke, DemoGray, 0.2f, 507);
        }

        private static void AnimateJanitor() {
            float p = P(6.5f);
            float FigMidY = FloorY + 0.25f;

            float appr = Seg(p, 0.04f, 0.3f);
            float retreat = Seg(p, 0.6f, 0.9f);
            float jx = p < 0.5f ? Move(-1.6f, -0.3f, appr) : Move(-0.3f, -1.6f, retreat);
            bool walking = Mid(appr) || Mid(retreat);
            FigPut("jan", jx, 0f, p >= 0.5f, walking ? 1f : 0f);
            FigCol("jan", JanitorCol, 1f);

            BtnPop("hideBtn", -0.3f, BtnY, Seg(p, 0.3f, 0.44f));

            // body already lying on the ground; fades away (hidden) then reappears near the loop
            // seam so the vignette reads as "a fresh body next time"
            float hideOut = Seg(p, 0.42f, 0.52f);
            float respawn = Seg(p, 0.88f, 0.98f);
            float bodyA = Mathf.Max(1f - Ease(hideOut), Ease(respawn));
            FigPut("body", 0f, 0f, true, 0f);
            FigCol("body", new Color(0.5f, 0.5f, 0.55f), bodyA);
            FigDead("body", 1f);

            for (int i = 0; i < 2; i++) {
                float sp = Seg(p, 0.44f + i * 0.04f, 0.6f + i * 0.04f);
                Put("s" + i, -0.1f + i * 0.2f, FloorY + 0.06f + 0.2f * sp);
                ColA("s" + i, DemoGray, Mid(sp) ? 0.55f * (1f - sp) : 0f);
            }
        }

        // ====================================================================
        // Morphling - scans a target, then morphs into their look (color/footprint), while a
        // faint red ring stays attached the whole time: still red to fellow Impostors, still
        // trackable.
        // ====================================================================
        // Distinctly darker than DemoRed: with both at full red the stage showed TWO nearly
        // identical red figures and you couldn't tell which one was the Morphling.
        private static readonly Color MateCol = new Color(0.5f, 0.17f, 0.2f);

        private static void CreateMorphling() {
            Crew("vic", DemoBlue);
            Crew("morph", DemoRed);
            Crew("mate", MateCol);
            StageCap("mateCap", "IMP", 0.5f, new Color(1f, 0.55f, 0.55f));
            StageSprite("scanRing", UCFx.Ring, DemoCyan, 0.5f, 507);
            StageCap("scanCap", "SCAN", 0.65f, DemoCyan);
            StageSprite("trackRing", UCFx.Ring, DemoRed, 0.4f, 507);
            StageSprite("fx", UCFx.Ring, Accent, 0.1f, 508);
        }

        private static void AnimateMorphling() {
            float p = P(7.5f);
            float FigMidY = FloorY + 0.25f;

            // a fellow Impostor idling in the background - the one who still sees through the disguise
            FigPut("mate", 1.5f, 0f, true, 0.15f);
            FigCol("mate", MateCol, 1f);
            PutCap("mateCap", 1.5f, 0.24f);
            CapA("mateCap", 0.7f);

            FigPut("vic", 1.05f, 0f, true, 0f);
            FigCol("vic", DemoBlue, 1f);

            float appr = Seg(p, 0.02f, 0.2f);
            float back = Seg(p, 0.24f, 0.34f);
            float mx = p < 0.24f ? Move(-1.6f, 0.55f, appr) : Move(0.55f, -0.4f, back);
            bool walking = Mid(appr) || Mid(back);
            FigPut("morph", mx, 0f, p >= 0.24f, walking ? 1f : 0f);

            float morphed = Plateau(Seg(p, 0.4f, 0.5f), Seg(p, 0.9f, 0.98f));
            FigCol("morph", Color.Lerp(DemoRed, DemoBlue, morphed), 1f);

            Put("scanRing", 1.05f, FigMidY);
            float scanPulse = 0.4f + 0.6f * Mathf.Abs(Mathf.Sin(stageT * 8f));
            ColA("scanRing", DemoCyan, Seg(p, 0.16f, 0.22f) * (1f - Seg(p, 0.26f, 0.3f)) * scanPulse);
            PutCap("scanCap", 1.05f, 0.24f);
            CapA("scanCap", Seg(p, 0.16f, 0.22f) * (1f - Seg(p, 0.26f, 0.3f)));

            Burst("fx", -0.4f, FigMidY, Seg(p, 0.38f, 0.52f), 0.6f, Accent);

            // faint red ring stays attached to the morphling the whole time it wears the disguise -
            // still red/trackable to fellow Impostors, even though everyone else sees blue
            Put("trackRing", mx, FigMidY);
            float trackPulse = 0.5f + 0.5f * Mathf.Sin(stageT * 3f);
            ColA("trackRing", DemoRed, morphed * 0.4f * trackPulse);
        }

        // ====================================================================
        // Camouflager - one button hides everyone's identity: names/hats vanish and all colors
        // blend to the same gray for a set duration, then it wears off.
        // ====================================================================
        private static void CreateCamouflager() {
            Crew("cam", DemoRed);
            Crew("c1", DemoBlue);
            Crew("c2", DemoGreen);
            StageDot("hat0", DemoRed, 0.055f);
            StageDot("hat1", DemoBlue, 0.055f);
            StageDot("hat2", DemoGreen, 0.055f);
            MakeBtn("camBtn", null);
            StageRect("barBg", new Color(1f, 1f, 1f, 0.12f), 0.9f, 0.06f);
            StageRect("bar", DemoGray, 0.9f, 0.045f);
            StageSprite("fx", UCFx.Ring, DemoGray, 0.1f, 508);
        }

        private static void AnimateCamouflager() {
            float p = P(7.5f);
            float FigMidY = FloorY + 0.25f;

            float camX = -0.75f, c1X = 0f, c2X = 0.75f;
            FigPut("cam", camX, 0f, false, 0.15f);
            FigPut("c1", c1X, 0f, false, 0.15f);
            FigPut("c2", c2X, 0f, false, 0.15f);

            float blend = Plateau(Seg(p, 0.16f, 0.28f), Seg(p, 0.8f, 0.92f));
            FigCol("cam", Color.Lerp(DemoRed, DemoGray, blend), 1f);
            FigCol("c1", Color.Lerp(DemoBlue, DemoGray, blend), 1f);
            FigCol("c2", Color.Lerp(DemoGreen, DemoGray, blend), 1f);

            BtnPop("camBtn", camX, BtnY, Seg(p, 0.06f, 0.2f));

            // hats/identity markers hidden while camouflaged
            Put("hat0", camX, FloorY + 0.56f); ColA("hat0", DemoRed, 1f - blend);
            Put("hat1", c1X, FloorY + 0.56f); ColA("hat1", DemoBlue, 1f - blend);
            Put("hat2", c2X, FloorY + 0.56f); ColA("hat2", DemoGreen, 1f - blend);

            Put("barBg", 0f, 0.31f); ColA("barBg", Color.white, 0.12f * blend);
            float fillFrac = 1f - Seg(p, 0.3f, 0.78f);
            BarLeft("bar", -0.45f, 0.31f, 0.9f * fillFrac, 0.045f);
            ColA("bar", DemoGray, blend);

            Burst("fx", 0f, FigMidY, Seg(p, 0.16f, 0.3f), 0.9f, DemoGray);
        }

        // ====================================================================
        // Vampire - bites a first victim, who stays alive (walking) with a mark overhead until a
        // delayed death; then approaches a second victim standing near a garlic, where the bite
        // attempt is BLOCKED (X over the bite button, the garlic flares) and only a normal,
        // instant kill goes through.
        // ====================================================================
        // The real TOR garlic sprite (the one lying on the map) - a soft dot alone read as a
        // stray light bokeh, not as garlic. Loaded per Create so a missing TOR assembly just
        // falls back to the dot.
        private static Sprite garlicSprite;

        private static void CreateVampire() {
            Crew("vamp", DemoRed);
            Crew("vic1", DemoBlue);
            Crew("vic2", DemoGreen);
            garlicSprite = null;
            try { garlicSprite = TheOtherRoles.Helpers.loadSpriteFromResources("TheOtherRoles.Resources.Garlic.png", 300f); } catch { }
            StageDot("garlicGlow", GarlicCol, 0.34f);
            if (garlicSprite != null) StagePic("garlic", garlicSprite, 0.26f, 507);
            else StageDot("garlic", GarlicCol, 0.2f);
            StageCap("mark", "!", 1.0f, Accent);
            StageCap("blockX", "X", 1.1f, DemoRed);
            MakeBtn("biteBtn", null);
            MakeBtn("killBtn", KillButtonSprite(null));
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateVampire() {
            float p = P(9.5f);
            float FigMidY = FloorY + 0.25f;

            FigPut("vic1", -0.5f, 0f, true, 0f);
            float bitten = Plateau(Seg(p, 0.14f, 0.2f), Seg(p, 0.56f, 0.62f));
            float dead1 = Seg(p, 0.56f, 0.66f);
            FigCol("vic1", Color.Lerp(DemoBlue, new Color(0.5f, 0.5f, 0.55f), dead1), 1f);
            FigDead("vic1", dead1);

            float appr1 = Seg(p, 0.02f, 0.14f);
            float retreat1 = Seg(p, 0.3f, 0.42f);
            float vx1 = p < 0.28f ? Move(-1.7f, -0.9f, appr1) : Move(-0.9f, -1.7f, retreat1);

            float appr2 = Seg(p, 0.60f, 0.72f);
            float retreat2 = Seg(p, 0.95f, 0.995f);
            float vx2 = p < 0.94f ? Move(-1.7f, 0.2f, appr2) : Move(0.2f, -1.7f, retreat2);
            float vampX = p < 0.5f ? vx1 : vx2;
            bool vampWalk = p < 0.5f ? (Mid(appr1) || Mid(retreat1)) : (Mid(appr2) || Mid(retreat2));
            bool vampFace = p < 0.5f ? p >= 0.28f : p >= 0.94f;
            FigPut("vamp", vampX, 0f, vampFace, vampWalk ? 1f : 0f);
            FigCol("vamp", DemoRed, 1f);

            // scene 1: the bite lands, the marked victim dies with a delay
            // scene 2: a second bite ATTEMPT next to the garlic, blocked
            BtnPop("biteBtn", vampX, BtnY, p < 0.5f ? Seg(p, 0.1f, 0.24f) : Seg(p, 0.73f, 0.82f));
            PutCap("mark", -0.5f, 0.22f);
            CapA("mark", bitten);
            Burst("fx", -0.5f, FigMidY, Seg(p, 0.55f, 0.68f), 0.65f, DemoRed);

            FigPut("vic2", 0.6f, 0f, true, 0f);
            float dead2 = Seg(p, 0.89f, 0.94f);
            FigCol("vic2", Color.Lerp(DemoGreen, new Color(0.5f, 0.5f, 0.55f), dead2), 1f);
            FigDead("vic2", dead2);

            // the garlic (real TOR sprite on a soft glow) flares up while it blocks the bite;
            // the X sits over the bite button
            float flare = Seg(p, 0.74f, 0.82f);
            float pulse = Mid(flare) ? Mathf.Sin(flare * Mathf.PI) : 0f;
            float gh = (garlicSprite != null ? 0.26f : 0.2f) * (1f + 0.3f * pulse);
            Put("garlic", 0.9f, FloorY + gh * 0.45f);
            PicScale("garlic", gh);
            ColA("garlic", garlicSprite != null ? Color.white : GarlicCol, 1f);
            Put("garlicGlow", 0.9f, FloorY + gh * 0.45f);
            PicScale("garlicGlow", 0.34f * (1f + 0.6f * pulse));
            ColA("garlicGlow", GarlicCol, 0.3f + 0.6f * pulse);
            PutCap("blockX", vampX, BtnY);
            CapA("blockX", Ease(Seg(p, 0.76f, 0.79f)) * (1f - Ease(Seg(p, 0.82f, 0.85f))));

            // only after the blocked bite: the ordinary kill
            BtnPop("killBtn", vampX, BtnY, Seg(p, 0.85f, 0.94f));
            Burst("fx2", 0.6f, FigMidY, Seg(p, 0.88f, 0.95f), 0.65f, DemoRed);
        }

        // ====================================================================
        // Eraser - marks a target; the erase resolves right before the NEXT exile, no matter who
        // actually gets voted out.
        // ====================================================================
        private static void CreateEraser() {
            Crew("era", DemoRed);
            Crew("vic", DemoWhite);
            Crew("other", DemoBlue);
            StageCap("mark", "?", 1.0f, Accent);
            StageDot("roleIcon", Accent, 0.09f);
            StageDot("plainIcon", DemoGray, 0.09f);
            MakeBtn("eraseBtn", null);
            StageSprite("fx", UCFx.Ring, Accent, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateEraser() {
            float p = P(8f);
            float FigMidY = FloorY + 0.25f;

            FigPut("vic", -0.3f, 0f, true, 0f);
            FigCol("vic", DemoWhite, 1f);

            float appr = Seg(p, 0.02f, 0.18f);
            float retreat = Seg(p, 0.24f, 0.36f);
            float ex = p < 0.22f ? Move(-1.6f, -0.75f, appr) : Move(-0.75f, -1.6f, retreat);
            bool walking = Mid(appr) || Mid(retreat);
            FigPut("era", ex, 0f, p >= 0.22f, walking ? 1f : 0f);
            FigCol("era", DemoRed, 1f);

            BtnPop("eraseBtn", ex, BtnY, Seg(p, 0.1f, 0.24f));
            float marked = Plateau(Seg(p, 0.14f, 0.2f), Seg(p, 0.66f, 0.72f));
            PutCap("mark", -0.3f, 0.22f);
            CapA("mark", marked);

            // "the erase resolves right before the next exile" - someone ELSE gets voted out while
            // the erase quietly lands on the original target regardless
            float othIn = Seg(p, 0.44f, 0.58f);
            float othX = Move(1.6f, 0.5f, othIn);
            FigPut("other", othX, 0f, true, Mid(othIn) ? 1f : 0f);
            float exiled = Seg(p, 0.6f, 0.68f);
            FigCol("other", Color.Lerp(DemoBlue, new Color(0.5f, 0.5f, 0.55f), exiled), 1f - 0.8f * Seg(p, 0.72f, 0.84f));
            FigDead("other", exiled);
            Burst("fx2", 0.5f, FigMidY, Seg(p, 0.6f, 0.72f), 0.6f, DemoRed);

            // the marked victim's role icon turns plain the moment the exile resolves
            float erased = Plateau(Seg(p, 0.62f, 0.7f), Seg(p, 0.92f, 0.99f));
            Put("roleIcon", -0.3f, FloorY + 0.56f);
            ColA("roleIcon", Accent, 1f - erased);
            Put("plainIcon", -0.3f, FloorY + 0.56f);
            ColA("plainIcon", DemoGray, erased);
            Burst("fx", -0.3f, FigMidY, Seg(p, 0.6f, 0.72f), 0.5f, Accent);
        }

        // ====================================================================
        // Trickster - places three jack-in-the-boxes (invisible for now), which turn into a
        // private vent network once the third is placed; then Lights Out blinds a Crewmate.
        // ====================================================================
        private static void CreateTrickster() {
            Crew("tri", TricksterCol);
            Crew("vicBlind", DemoBlue);
            StageDot("box0", VentCol, 0.1f);
            StageDot("box1", VentCol, 0.1f);
            StageDot("box2", VentCol, 0.1f);
            MakeBtn("boxBtn", null);
            MakeBtn("lightsBtn", null);
            StageCap("net", "VENT", 0.55f, Accent);
            StageRect("dark", Color.black, 0.9f, 0.75f, 508);
            StageSprite("fx", UCFx.Ring, Accent, 0.1f, 508);
        }

        private static void AnimateTrickster() {
            float p = P(8.5f);
            float FigMidY = FloorY + 0.25f;

            float[] boxX = { -1.2f, -0.3f, 0.6f };
            float[] placeStart = { 0.03f, 0.17f, 0.31f };
            float[] placeEnd = { 0.13f, 0.27f, 0.41f };

            float tx;
            bool walking;
            if (p < 0.13f) { tx = Move(-1.55f, boxX[0], Seg(p, placeStart[0], placeEnd[0])); walking = Mid(Seg(p, placeStart[0], placeEnd[0])); }
            else if (p < 0.27f) { tx = Move(boxX[0], boxX[1], Seg(p, 0.14f, placeEnd[1])); walking = Mid(Seg(p, 0.14f, placeEnd[1])); }
            else if (p < 0.6f) { tx = p < 0.41f ? Move(boxX[1], boxX[2], Seg(p, 0.28f, placeEnd[2])) : boxX[2]; walking = Mid(Seg(p, 0.28f, placeEnd[2])); }
            else { tx = Move(boxX[2], 0f, Seg(p, 0.62f, 0.74f)); walking = Mid(Seg(p, 0.62f, 0.74f)); }
            FigPut("tri", tx, 0f, p >= 0.6f, walking ? 1f : 0f);
            FigCol("tri", TricksterCol, 1f);

            for (int i = 0; i < 3; i++) {
                BtnPop("boxBtn", boxX[i], BtnY, Seg(p, placeStart[i] + 0.04f, placeEnd[i] + 0.06f));
            }

            // boxes: near-invisible once placed, then pop fully visible once the network reveals
            float revealed = Plateau(Seg(p, 0.5f, 0.58f), Seg(p, 0.94f, 0.99f));
            for (int i = 0; i < 3; i++) {
                float placed = Seg(p, placeEnd[i], placeEnd[i] + 0.02f);
                Put("box" + i, boxX[i], FloorY + 0.05f);
                ColA("box" + i, VentCol, Mathf.Max(0.14f * placed, revealed));
            }
            Burst("fx", 0f, FigMidY, Seg(p, 0.5f, 0.6f), 1.1f, Accent);
            PutCap("net", 0f, 0.24f);
            CapA("net", Seg(p, 0.52f, 0.6f) * (1f - Seg(p, 0.9f, 0.98f)));

            BtnPop("lightsBtn", tx, BtnY, Seg(p, 0.62f, 0.76f));

            FigPut("vicBlind", 1.3f, 0f, true, 0.12f);
            float blinded = Plateau(Seg(p, 0.78f, 0.86f), Seg(p, 0.94f, 0.99f));
            FigCol("vicBlind", Color.Lerp(DemoBlue, new Color(0.05f, 0.05f, 0.07f), blinded * 0.85f), 1f);
            Put("dark", 1.15f, 0.05f);
            ColA("dark", Color.black, blinded * 0.55f);
        }
    }
}
