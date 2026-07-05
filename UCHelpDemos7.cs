// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHelpDemos7 - bespoke help-menu demo vignettes (see UCHelpMenu's ExtraDemos registry) for
 * eight TOR modifiers: Tiebreaker, Sunglasses, Mini, VIP, Invert, Chameleon, Armored, Shifter.
 * Each Create/Animate pair is a small, stateless, looping mini-story acting out that
 * modifier's mechanic, built purely from the shared UCHelpMenu stage API (Crew/StageDot/
 * StageRect/StageSprite/StageCap factories + per-frame Put/ColA/FigPut/FigCol/Burst helpers).
 */

using System;
using UnityEngine;
using static UnknownsCollection.UCHelpMenu;

namespace UnknownsCollection {
    public static class UCHelpDemos7 {
        public static void Register() {
            RegisterDemo("Tiebreaker", CreateTiebreaker, AnimateTiebreaker);
            RegisterDemo("Sunglasses", CreateSunglasses, AnimateSunglasses);
            RegisterDemo("Mini", CreateMini, AnimateMini);
            RegisterDemo("VIP", CreateVip, AnimateVip);
            RegisterDemo("Invert", CreateInvert, AnimateInvert);
            RegisterDemo("Chameleon", CreateChameleon, AnimateChameleon);
            RegisterDemo("Armored", CreateArmored, AnimateArmored);
            RegisterDemo("Shifter", CreateShifter, AnimateShifter);
        }

        // ================================================================
        // Tiebreaker: a tied vote - the holder secretly casts an extra vote that decides it,
        // then everyone learns who broke the tie.
        // ================================================================
        private static void CreateTiebreaker() {
            Crew("tie", DemoOrange);
            StageRect("barA", DemoBlue, 0.34f, 0.02f);
            StageRect("barB", DemoGreen, 0.34f, 0.02f);
            StageDot("vote", DemoOrange, 0.09f);
            StageCap("tieCap", "TIE", 1.0f, Accent);
            StageCap("plusCap", "+1", 0.85f, DemoOrange);
            StageCap("winCap", "TIEBREAKER!", 0.6f, Accent);
            StageSprite("fx", UCFx.Ring, DemoOrange, 0.1f, 508);
        }

        private static void AnimateTiebreaker() {
            float p = P(6.5f);
            float fade = Ease(Seg(p, 0.02f, 0.1f)) * (1f - Ease(Seg(p, 0.92f, 1f)));

            float grow = Seg(p, 0.08f, 0.36f);
            float extra = Seg(p, 0.56f, 0.68f);
            float hA = Mathf.Lerp(0.02f, 0.5f, Ease(grow)) + Mathf.Lerp(0f, 0.18f, Ease(extra));
            float hB = Mathf.Lerp(0.02f, 0.5f, Ease(grow));

            Put("barA", -0.42f, FloorY + hA / 2f); Size2("barA", 0.34f, Mathf.Max(hA, 0.001f)); ColA("barA", DemoBlue, fade);
            Put("barB", 0.4f, FloorY + hB / 2f); Size2("barB", 0.34f, Mathf.Max(hB, 0.001f)); ColA("barB", DemoGreen, fade);

            FigPut("tie", 1.35f, 0f, true, 0f);
            FigCol("tie", DemoOrange, fade);

            float tieWin = Seg(p, 0.3f, 0.4f) * (1f - Seg(p, 0.46f, 0.54f));
            PutCap("tieCap", -0.01f, 0.34f); CapA("tieCap", tieWin * fade);

            float voteFly = Seg(p, 0.46f, 0.56f);
            Put("vote", Mathf.Lerp(1.35f, -0.42f, Ease(voteFly)), Mathf.Lerp(FloorY + 0.32f, FloorY + hA + 0.04f, Ease(voteFly)));
            ColA("vote", DemoOrange, Mid(voteFly) ? 0.9f * fade : 0f);

            float plusWin = Seg(p, 0.56f, 0.62f) * (1f - Seg(p, 0.76f, 0.86f));
            PutCap("plusCap", -0.42f, FloorY + hA + 0.16f); CapA("plusCap", plusWin * fade);

            float winWin = Seg(p, 0.66f, 0.76f) * (1f - Seg(p, 0.86f, 0.94f));
            PutCap("winCap", 1.35f, 0.34f); CapA("winCap", winWin * fade);

            Burst("fx", -0.42f, FloorY + hA, Seg(p, 0.58f, 0.72f), 0.55f, DemoOrange);
        }

        // ================================================================
        // Sunglasses: reduced Crewmate vision, both lights-on and lights-off (sabotage).
        // ================================================================
        private static void CreateSunglasses() {
            Crew("norm", DemoBlue);
            Crew("shd", DemoCyan);
            StageSprite("visN", UCFx.Ring, DemoWhite, 1f, 505);
            StageSprite("visS", UCFx.Ring, Accent, 1f, 505);
            StageRect("dark", Color.black, stageSize.x - 0.06f, stageSize.y - 0.06f, 504);
            StageRect("glasses", DemoDark, 0.14f, 0.05f, 507);
            StageCap("labelN", "NORMAL", 0.5f, DemoWhite);
            StageCap("labelS", "SUNGLASSES", 0.44f, Accent);
            StageSprite("fx", UCFx.Ring, Accent, 0.1f, 508);
        }

        private static void AnimateSunglasses() {
            float p = P(6f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.95f, 1f)));

            float on1 = Ease(Seg(p, 0.46f, 0.52f));
            float off1 = Ease(Seg(p, 0.92f, 0.98f));
            float dark = on1 * (1f - off1);

            Put("dark", 0f, 0f); ColA("dark", Color.black, dark * 0.55f * fade);

            FigPut("norm", -0.55f, 0f, false, 0f);
            FigCol("norm", DemoBlue, fade);
            FigPut("shd", 0.55f, 0f, false, 0f);
            FigCol("shd", DemoCyan, fade);

            float figY = FloorY + 0.25f;
            float dN = Mathf.Lerp(1.5f, 0.85f, dark);
            float dS = Mathf.Lerp(0.9f, 0.5f, dark);
            Put("visN", -0.55f, figY); Size2("visN", dN, dN); ColA("visN", DemoWhite, 0.4f * fade);
            Put("visS", 0.55f, figY); Size2("visS", dS, dS); ColA("visS", Accent, 0.4f * fade);

            Put("glasses", 0.595f, figY + 0.16f); ColA("glasses", DemoDark, fade);

            PutCap("labelN", -0.55f, 0.32f); CapA("labelN", 0.85f * fade);
            PutCap("labelS", 0.55f, 0.32f); CapA("labelS", 0.85f * fade);

            Burst("fx", 0f, stageSize.y / 2f - 0.14f, Seg(p, 0.46f, 0.56f), 0.4f, Accent);
        }

        // ================================================================
        // Mini: shrunk and unkillable while growing; a kill only lands once fully grown.
        // ================================================================
        private static void CreateMini() {
            Crew("miniS", DemoWhite, 0.11f);
            Crew("miniG", DemoWhite, 0.19f);
            Crew("imp", DemoRed);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
            StageCap("immuneCap", "X", 1.0f, DemoCyan);
            StageCap("grownCap", "GROWN!", 0.55f, Accent);
        }

        private static void AnimateMini() {
            float p = P(7f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.95f, 1f)));

            float a1 = Seg(p, 0.05f, 0.24f);
            float r1 = Seg(p, 0.94f, 1f);
            float ix = p < 0.24f ? Move(1.45f, 0.5f, a1) : (p < 0.94f ? 0.5f : Move(0.5f, 1.45f, r1));
            bool walking = Mid(a1) || Mid(r1);
            FigPut("imp", ix, 0f, true, walking ? 1f : 0f);
            FigCol("imp", DemoRed, fade);

            FigPut("miniS", -0.3f, 0f, false, 0f);
            FigPut("miniG", -0.3f, 0f, false, 0f);
            float growP = Seg(p, 0.46f, 0.6f);
            FigCol("miniS", DemoWhite, fade * (1f - growP));
            FigCol("miniG", DemoWhite, fade * growP);

            float atk1 = Seg(p, 0.26f, 0.42f);
            float atk2 = Seg(p, 0.74f, 0.92f);
            Burst("fx", -0.3f, FloorY + (p < 0.5f ? 0.13f : 0.24f), p < 0.5f ? atk1 : atk2, p < 0.5f ? 0.35f : 0.7f, DemoRed);

            FigDead("miniG", Seg(p, 0.82f, 0.92f));

            PutCap("immuneCap", -0.3f, FloorY + 0.32f);
            CapA("immuneCap", Seg(p, 0.3f, 0.36f) * (1f - Seg(p, 0.42f, 0.5f)) * fade);

            PutCap("grownCap", -0.3f, 0.32f);
            CapA("grownCap", Seg(p, 0.5f, 0.58f) * (1f - Seg(p, 0.66f, 0.74f)) * fade);
        }

        // ================================================================
        // VIP: when the holder dies, everyone sees a screen flash (colored by their team).
        // ================================================================
        private static void CreateVip() {
            Crew("vip", DemoBlue);
            Crew("imp", DemoRed);
            StageRect("flash", DemoWhite, stageSize.x, stageSize.y, 509);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
            StageCap("star", "*", 1.0f, Accent);
            StageCap("deadCap", "VIP DIED", 0.5f, DemoBlue);
        }

        private static void AnimateVip() {
            float p = P(6f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.95f, 1f)));

            float aIn = Seg(p, 0.04f, 0.22f);
            float aOut = Seg(p, 0.56f, 0.74f);
            float ix = p < 0.24f ? Move(1.45f, 0.35f, aIn) : (p < 0.56f ? 0.35f : Move(0.35f, 1.45f, aOut));
            bool walking = Mid(aIn) || Mid(aOut);
            FigPut("imp", ix, 0f, true, walking ? 1f : 0f);
            FigCol("imp", DemoRed, fade);

            FigPut("vip", -0.3f, 0f, false, 0f);
            float killed = Seg(p, 0.26f, 0.36f);
            FigCol("vip", DemoBlue, fade);
            FigDead("vip", killed);

            PutCap("star", -0.3f, 0.34f);
            CapA("star", fade * (1f - Seg(p, 0.26f, 0.34f)));

            Burst("fx", -0.3f, FloorY + 0.25f, Seg(p, 0.26f, 0.4f), 0.65f, DemoRed);

            Put("flash", 0f, 0f);
            float flashA = Ease(Seg(p, 0.3f, 0.36f)) * (1f - Ease(Seg(p, 0.48f, 0.62f)));
            ColA("flash", DemoBlue, flashA * 0.6f);

            PutCap("deadCap", 0f, 0.4f);
            CapA("deadCap", Seg(p, 0.42f, 0.5f) * (1f - Seg(p, 0.8f, 0.9f)) * fade);
        }

        // ================================================================
        // Invert: movement controls are reversed - pressing one way sends the holder the other.
        // ================================================================
        private static void CreateInvert() {
            Crew("inv", DemoPurple);
            StageCap("inputCap", "PRESS >", 0.55f, DemoWhite);
            StageCap("confuseCap", "?", 1.1f, DemoPurple);
            StageCap("actualCap", "< ACTUAL", 0.5f, Accent);
        }

        private static void AnimateInvert() {
            float p = P(6f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.94f, 1f)));

            float walkSeg = Seg(p, 0.06f, 0.86f);
            float baseX = Move(0.9f, -0.9f, walkSeg);
            float wobble = 0.16f * Mathf.Sin(stageT * 9f);
            float x = baseX + wobble * (Mid(walkSeg) ? 1f : 0f);
            bool faceLeft = Mathf.Cos(stageT * 9f) < 0f;
            FigPut("inv", x, 0f, faceLeft, Mid(walkSeg) ? 1f : 0f);
            FigCol("inv", DemoPurple, fade);

            PutCap("inputCap", 1.35f, 0.34f);
            CapA("inputCap", fade * (0.55f + 0.35f * Mathf.Sin(stageT * 4f)));

            PutCap("actualCap", -1.35f, 0.34f);
            CapA("actualCap", fade * (0.55f + 0.35f * Mathf.Sin(stageT * 4f + 3.14f)));

            PutCap("confuseCap", x, 0.3f);
            CapA("confuseCap", fade * (0.35f + 0.3f * Mathf.Sin(stageT * 2f)) * (Mid(walkSeg) ? 1f : 0f));
        }

        // ================================================================
        // Chameleon: standing still fades the holder toward invisible; moving snaps it back.
        // ================================================================
        private static void CreateChameleon() {
            Crew("cham", DemoGreen);
            StageSprite("shimmer", UCFx.Dot, DemoGreen, 0.5f, 505);
            StageCap("hideCap", "HIDDEN", 0.55f, DemoGreen);
        }

        private static void AnimateChameleon() {
            float p = P(6.5f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.95f, 1f)));

            float xA = -0.5f, xB = 0.5f;
            float m1 = Seg(p, 0.4f, 0.56f);
            float m2 = Seg(p, 0.9f, 0.98f);
            bool moving = Mid(m1) || Mid(m2);
            float x = p < 0.4f ? xA : (p < 0.56f ? Move(xA, xB, m1) : (p < 0.9f ? xB : Move(xB, xA, m2)));
            bool faceLeft = p >= 0.56f;

            float still1 = Mathf.Lerp(1f, 0.12f, Ease(Seg(p, 0.06f, 0.38f)));
            float still2 = Mathf.Lerp(1f, 0.12f, Ease(Seg(p, 0.6f, 0.88f)));
            float visAlpha = moving ? 1f : (p < 0.5f ? still1 : still2);

            FigPut("cham", x, 0f, faceLeft, moving ? 1f : 0f);
            FigCol("cham", DemoGreen, visAlpha * fade);

            float figY = FloorY + 0.25f;
            Put("shimmer", x, figY);
            ColA("shimmer", DemoGreen, (1f - visAlpha) * 0.3f * (0.5f + 0.5f * Mathf.Sin(stageT * 6f)) * fade);

            PutCap("hideCap", x, 0.3f);
            CapA("hideCap", fade * (1f - visAlpha) * (visAlpha < 0.4f ? 1f : 0f));
        }

        // ================================================================
        // Armored: blocks the very first kill attempt of the round (breaking visibly), but
        // not the next one.
        // ================================================================
        private static void CreateArmored() {
            Crew("arm", DemoGray);
            Crew("imp", DemoRed);
            StageSprite("shield", UCFx.Ring, Accent, 0.55f, 507);
            StageSprite("fx", UCFx.Ring, Accent, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoRed, 0.1f, 508);
            StageCap("blockCap", "BLOCKED!", 0.55f, Accent);
            StageCap("confuseCap", "?", 1.0f, DemoRed);
        }

        private static void AnimateArmored() {
            float p = P(7.5f);
            float fade = Ease(Seg(p, 0.02f, 0.06f)) * (1f - Ease(Seg(p, 0.97f, 1f)));

            float a1 = Seg(p, 0.04f, 0.26f);
            float r1 = Seg(p, 0.46f, 0.58f);
            float a2 = Seg(p, 0.62f, 0.78f);
            float r2 = Seg(p, 0.9f, 0.99f);
            float ix =
                p < 0.26f ? Move(1.45f, 0.4f, a1) :
                p < 0.46f ? 0.4f :
                p < 0.58f ? Move(0.4f, 1.45f, r1) :
                p < 0.62f ? 1.45f :
                p < 0.78f ? Move(1.45f, 0.4f, a2) :
                p < 0.9f ? 0.4f :
                Move(0.4f, 1.45f, r2);
            bool walking = Mid(a1) || Mid(r1) || Mid(a2) || Mid(r2);
            FigPut("imp", ix, 0f, true, walking ? 1f : 0f);
            FigCol("imp", DemoRed, fade);

            FigPut("arm", -0.35f, 0f, false, 0f);
            FigCol("arm", DemoGray, fade);
            FigDead("arm", Seg(p, 0.82f, 0.92f));

            float shieldIntact = 1f - Ease(Seg(p, 0.28f, 0.38f));
            float figY = FloorY + 0.25f;
            float shieldSize = 0.55f + 0.03f * Mathf.Sin(stageT * 3f);
            Put("shield", -0.35f, figY); Size2("shield", shieldSize, shieldSize);
            ColA("shield", Accent, shieldIntact * fade);

            Burst("fx", -0.35f, figY, Seg(p, 0.28f, 0.42f), 0.55f, Accent);
            Burst("fx2", -0.35f, figY, Seg(p, 0.82f, 0.94f), 0.7f, DemoRed);

            PutCap("blockCap", -0.35f, 0.32f);
            CapA("blockCap", Seg(p, 0.3f, 0.38f) * (1f - Seg(p, 0.48f, 0.58f)) * fade);

            PutCap("confuseCap", ix, 0.32f);
            CapA("confuseCap", Seg(p, 0.34f, 0.42f) * (1f - Seg(p, 0.5f, 0.6f)) * fade);
        }

        // ================================================================
        // Shifter: swaps roles with a fellow Crewmate at the next meeting's end - but picking
        // an Impostor or Neutral instead makes the Shifter secretly vanish, no body left behind.
        // ================================================================
        private static void CreateShifter() {
            Crew("shf", DemoWhite);
            Crew("good", DemoGreen);
            Crew("bad", DemoRed);
            StageCap("tagA", "A", 0.8f, Accent);
            StageCap("tagB", "B", 0.8f, DemoGreen);
            StageCap("swapCap", "SWAPPED!", 0.5f, Accent);
            StageCap("poofCap", "GONE", 0.55f, DemoGray);
            StageSprite("fx1", UCFx.Ring, Accent, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoGray, 0.1f, 508);
            StageSprite("sm0", UCFx.Smoke, DemoGray, 0.2f, 507);
            StageSprite("sm1", UCFx.Smoke, DemoGray, 0.2f, 507);
        }

        private static void AnimateShifter() {
            float p = P(7.5f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.94f, 1f)));

            float goodWin = Ease(Seg(p, 0.06f, 0.14f)) * (1f - Ease(Seg(p, 0.46f, 0.54f)));
            float badWin = Ease(Seg(p, 0.48f, 0.56f)) * (1f - Ease(Seg(p, 0.9f, 0.96f)));
            float shfVanish = Ease(Seg(p, 0.64f, 0.78f));

            FigPut("shf", -0.4f, 0f, false, 0f);
            FigCol("shf", DemoWhite, fade * (1f - shfVanish));

            FigPut("good", 0.4f, 0f, true, 0f);
            FigCol("good", DemoGreen, fade * goodWin);

            FigPut("bad", 0.4f, 0f, true, 0f);
            FigCol("bad", DemoRed, fade * badWin);

            float swap1 = Seg(p, 0.16f, 0.3f);
            float tagFade = fade * goodWin;
            float xA = Move(-0.4f, 0.4f, swap1);
            float xB = Move(0.4f, -0.4f, swap1);
            PutCap("tagA", xA, 0.32f); CapA("tagA", tagFade);
            PutCap("tagB", xB, 0.32f); CapA("tagB", tagFade);

            float swapPop = Seg(p, 0.3f, 0.36f) * (1f - Seg(p, 0.42f, 0.48f));
            PutCap("swapCap", 0f, 0.4f); CapA("swapCap", swapPop * fade);
            Burst("fx1", 0f, FloorY + 0.25f, Seg(p, 0.2f, 0.32f), 0.55f, Accent);

            float poofPop = Seg(p, 0.7f, 0.78f) * (1f - Seg(p, 0.86f, 0.94f));
            PutCap("poofCap", -0.4f, 0.34f); CapA("poofCap", poofPop * fade);
            Burst("fx2", -0.4f, FloorY + 0.25f, Seg(p, 0.64f, 0.78f), 0.5f, DemoGray);

            for (int i = 0; i < 2; i++) {
                float sp = Seg(p, 0.66f + i * 0.03f, 0.8f + i * 0.03f);
                Put("sm" + i, -0.4f + (i - 0.5f) * 0.13f, FloorY + 0.08f + 0.2f * sp);
                ColA("sm" + i, DemoGray, Mid(sp) ? 0.5f * (1f - sp) * fade : 0f);
            }
        }
    }
}
