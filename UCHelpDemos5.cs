// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHelpDemos5 - bespoke help-menu demo vignettes (see UCHelpMenu) for eight TOR Crewmate
 * roles: Detective, Time Master, Medic, Swapper, Seer, Hacker, Tracker, Snitch.
 * Each Create-/Animate-method pair is registered via RegisterDemo and picked up automatically by
 * UCHelpMenu's reflection-based pack loader (any type named UCHelpDemos* with a public
 * static Register()). Every animation is a stateless loop driven purely by stageT/P(),
 * exactly like the hand-built UC role vignettes (Tesla, Saboteur, ...).
 */

using System;
using UnityEngine;
using static UnknownsCollection.UCHelpMenu;

namespace UnknownsCollection {
    public static class UCHelpDemos5 {
        // ---- flavor colors not already covered by the shared Demo* palette ----
        private static readonly Color SwapperPink = new Color(0.95f, 0.55f, 0.85f);
        private static readonly Color SeerCol = new Color(0.55f, 0.72f, 0.95f);
        private static readonly Color SeerSoul = new Color(0.8f, 0.87f, 1f);
        private static readonly Color HackerGreen = new Color(0.35f, 0.95f, 0.6f);
        private static readonly Color SnitchTan = new Color(0.85f, 0.72f, 0.45f);

        public static void Register() {
            RegisterDemo("Detective", CreateDetective, AnimateDetective);
            RegisterDemo("Time Master", CreateTimeMaster, AnimateTimeMaster);
            RegisterDemo("Medic", CreateMedic, AnimateMedic);
            RegisterDemo("Swapper", CreateSwapper, AnimateSwapper);
            RegisterDemo("Seer", CreateSeer, AnimateSeer);
            RegisterDemo("Hacker", CreateHacker, AnimateHacker);
            RegisterDemo("Tracker", CreateTracker, AnimateTracker);
            RegisterDemo("Snitch", CreateSnitch, AnimateSnitch);
        }

        // ====================================================================
        // Detective: kill leaves footprints behind the fleeing killer; the Detective
        // follows the trail to the body and, reporting quickly, gets a color clue.
        // ====================================================================
        private static void CreateDetective() {
            Crew("kil", DemoRed);
            Crew("vic", DemoWhite);
            Crew("det", DemoCyan);
            StageDot("fp0", DemoRed, 0.05f);
            StageDot("fp1", DemoRed, 0.05f);
            StageDot("fp2", DemoRed, 0.05f);
            StageSprite("killFx", UCFx.Ring, DemoRed, 0.1f, 508);
            StageDot("clue", DemoRed, 0.14f);
            StageCap("clueCap", "RED!", 0.85f, DemoRed);
        }

        private static void AnimateDetective() {
            float p = P(7f);
            float fade = Ease(Seg(p, 0f, 0.06f)) * (1f - Ease(Seg(p, 0.94f, 1f)));

            float approach = Seg(p, 0.04f, 0.26f);
            float flee = Seg(p, 0.34f, 0.56f);
            float kx = p < 0.34f ? Move(-1.6f, -0.32f, approach) : Move(-0.32f, -1.65f, flee);
            bool kilFaceLeft = p >= 0.34f;
            float kilWalk = (Mid(approach) || Mid(flee)) ? 1f : 0f;
            FigPut("kil", kx, 0f, kilFaceLeft, kilWalk);
            FigCol("kil", DemoRed, fade);

            FigPut("vic", 0.05f, 0f, true, 0f);
            float dieSeg = Seg(p, 0.28f, 0.34f);
            FigCol("vic", DemoWhite, fade);
            FigDead("vic", Ease(dieSeg));
            Burst("killFx", 0f, FloorY + 0.25f, Seg(p, 0.28f, 0.42f), 0.55f, DemoRed);

            // footprints: pop in as the killer's flee path passes each spot, stay until reset
            float fp0A = Ease(Seg(p, 0.36f, 0.4f)) * (1f - Ease(Seg(p, 0.9f, 0.98f)));
            Put("fp0", -0.45f, FloorY + 0.015f); ColA("fp0", DemoRed, 0.55f * fp0A * fade);
            float fp1A = Ease(Seg(p, 0.41f, 0.45f)) * (1f - Ease(Seg(p, 0.9f, 0.98f)));
            Put("fp1", -0.8f, FloorY + 0.015f); ColA("fp1", DemoRed, 0.55f * fp1A * fade);
            float fp2A = Ease(Seg(p, 0.46f, 0.5f)) * (1f - Ease(Seg(p, 0.9f, 0.98f)));
            Put("fp2", -1.15f, FloorY + 0.015f); ColA("fp2", DemoRed, 0.55f * fp2A * fade);

            float detArr = Seg(p, 0.58f, 0.78f);
            float detX = Move(1.7f, 0.05f, detArr);
            FigPut("det", detX, 0f, true, Mid(detArr) ? 1f : 0f);
            FigCol("det", DemoCyan, fade);

            // report: the killer's color is revealed as a clue
            float rep = Ease(Seg(p, 0.8f, 0.88f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            Put("clue", detX, 0.32f); ColA("clue", DemoRed, rep * fade);
            PutCap("clueCap", detX, 0.42f); CapA("clueCap", rep * fade);
        }

        // ====================================================================
        // Time Master: shield up, kill attempt bounces off, time rewinds - undoing the
        // killer's approach, not the Time Master's own position - before the next try.
        // ====================================================================
        private static void CreateTimeMaster() {
            Crew("tm", DemoPurple);
            Crew("kil", DemoRed);
            StageSprite("shieldRing", UCFx.Ring, DemoPurple, 0.5f, 507);
            MakeBtn("shieldBtn", null);
            StageSprite("failFx", UCFx.Ring, DemoPurple, 0.1f, 508);
            StageCap("rewindCap", "REWIND", 0.75f, DemoPurple);
        }

        private static void AnimateTimeMaster() {
            float p = P(6.5f);
            float fade = Ease(Seg(p, 0f, 0.06f)) * (1f - Ease(Seg(p, 0.94f, 1f)));

            float approach = Seg(p, 0.04f, 0.32f);
            float rewindP = Seg(p, 0.5f, 0.74f);
            float kx = p < 0.5f ? Move(-1.6f, -0.3f, approach) : Move(-0.3f, -1.6f, rewindP);
            bool kilFaceLeft = p >= 0.5f;
            float kilWalk = (Mid(approach) || Mid(rewindP)) ? 1f : 0f;
            FigPut("kil", kx, 0f, kilFaceLeft, kilWalk);
            FigCol("kil", DemoRed, fade);

            FigPut("tm", 0.35f, 0f, true, 0f);
            FigCol("tm", DemoPurple, fade);

            BtnPop("shieldBtn", 0.35f, BtnY, Seg(p, 0.14f, 0.3f));

            float shieldActive = Seg(p, 0.16f, 0.5f);
            float pulse = 0.6f + 0.4f * Mathf.Sin(stageT * 8f);
            Put("shieldRing", 0.35f, FloorY + 0.25f);
            ColA("shieldRing", DemoPurple, (Mid(shieldActive) ? pulse : 0f) * fade);

            Burst("failFx", 0.35f, FloorY + 0.25f, Seg(p, 0.3f, 0.44f), 0.5f, DemoPurple);

            float rewindTextA = Ease(Seg(p, 0.5f, 0.58f)) * (1f - Ease(Seg(p, 0.7f, 0.76f)));
            PutCap("rewindCap", 0f, 0.42f); CapA("rewindCap", rewindTextA * fade);
        }

        // ====================================================================
        // Medic: shields an ally (brackets show it), the killer's strike bounces off
        // the shield and the ally never falls.
        // ====================================================================
        private static void CreateMedic() {
            Crew("med", DemoGreen);
            Crew("ally", DemoBlue);
            Crew("kil", DemoRed);
            MakeBtn("shieldBtn", null);
            StageCap("brL", "[", 1.3f, Color.white);
            StageCap("brR", "]", 1.3f, Color.white);
            StageSprite("shieldRing", UCFx.Ring, DemoGreen, 0.5f, 507);
            StageSprite("atkFx", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateMedic() {
            float p = P(7f);
            float fade = Ease(Seg(p, 0f, 0.06f)) * (1f - Ease(Seg(p, 0.94f, 1f)));

            float medMove = Seg(p, 0.03f, 0.22f);
            float medX = Move(-1.6f, -0.85f, medMove);
            FigPut("med", medX, 0f, false, Mid(medMove) ? 1f : 0f);
            FigCol("med", DemoGreen, fade);

            FigPut("ally", 0f, 0f, false, 0f);
            FigCol("ally", DemoBlue, fade);

            BtnPop("shieldBtn", 0f, BtnY, Seg(p, 0.16f, 0.32f));

            float bracketsA = Ease(Seg(p, 0.24f, 0.32f)) * (1f - Ease(Seg(p, 0.9f, 0.98f)));
            PutCap("brL", -0.28f, 0.05f); CapA("brL", bracketsA * fade);
            PutCap("brR", 0.28f, 0.05f); CapA("brR", bracketsA * fade);

            float shieldGlow = 0.5f + 0.5f * Mathf.Sin(stageT * 5f);
            Put("shieldRing", 0f, FloorY + 0.25f);
            ColA("shieldRing", DemoGreen, bracketsA * shieldGlow * 0.8f * fade);

            float kilMove = Seg(p, 0.4f, 0.62f);
            float kilRetreat = Seg(p, 0.78f, 0.95f);
            float kx = p < 0.72f ? Move(1.6f, 0.28f, kilMove) : Move(0.28f, 1.6f, kilRetreat);
            bool kilFaceLeft = p < 0.72f;
            float kilWalk = (Mid(kilMove) || Mid(kilRetreat)) ? 1f : 0f;
            FigPut("kil", kx, 0f, kilFaceLeft, kilWalk);
            FigCol("kil", DemoRed, fade);

            Burst("atkFx", 0.15f, FloorY + 0.25f, Seg(p, 0.62f, 0.78f), 0.55f, DemoRed);
            // the ally is never FigDead - the shield holds, the kill simply fails
        }

        // ====================================================================
        // Swapper: casts the ability, two vote bars visibly cross and swap heights.
        // ====================================================================
        private static void CreateSwapper() {
            Crew("swp", SwapperPink);
            Crew("pa", DemoBlue);
            Crew("pb", DemoGreen);
            MakeBtn("swapBtn", null);
            StageRect("barA", DemoBlue, 0.16f, 0.1f, 506);
            StageRect("barB", DemoGreen, 0.16f, 0.1f, 506);
            StageSprite("stAB", UCFx.Streak, SwapperPink, 0.28f, 508);
            StageSprite("stBA", UCFx.Streak, SwapperPink, 0.28f, 508);
            StageCap("meetCap", "MEETING", 0.6f, new Color(1f, 1f, 1f, 0.5f));
        }

        private static void AnimateSwapper() {
            float p = P(6.5f);
            float fade = Ease(Seg(p, 0f, 0.06f)) * (1f - Ease(Seg(p, 0.94f, 1f)));

            FigPut("swp", 0f, 0f, false, 0f); FigCol("swp", SwapperPink, fade);
            FigPut("pa", -0.65f, 0f, false, 0f); FigCol("pa", DemoBlue, fade);
            FigPut("pb", 0.65f, 0f, true, 0f); FigCol("pb", DemoGreen, fade);

            PutCap("meetCap", 0f, 0.42f); CapA("meetCap", 0.5f * fade);

            BtnPop("swapBtn", 0f, BtnY, Seg(p, 0.28f, 0.44f));

            float swapProg = Ease(Seg(p, 0.42f, 0.62f));
            float baseY = 0.1f;
            float hA = Mathf.Lerp(0.5f, 0.2f, swapProg);
            float hB = Mathf.Lerp(0.2f, 0.5f, swapProg);
            Size2("barA", 0.16f, hA); Put("barA", -0.65f, baseY + hA / 2f); ColA("barA", DemoBlue, fade);
            Size2("barB", 0.16f, hB); Put("barB", 0.65f, baseY + hB / 2f); ColA("barB", DemoGreen, fade);

            float crossWin = Seg(p, 0.42f, 0.62f);
            float sxAB = Move(-0.6f, 0.6f, crossWin);
            float syAB = baseY + 0.3f + 0.12f * Mathf.Sin(crossWin * Mathf.PI);
            Put("stAB", sxAB, syAB); ColA("stAB", SwapperPink, (Mid(crossWin) ? 1f : 0f) * fade);

            float sxBA = Move(0.6f, -0.6f, crossWin);
            float syBA = baseY + 0.3f - 0.12f * Mathf.Sin(crossWin * Mathf.PI);
            Put("stBA", sxBA, syBA); ColA("stBA", SwapperPink, (Mid(crossWin) ? 1f : 0f) * fade);
        }

        // ====================================================================
        // Seer: a player dies elsewhere - a screen flash hits, and a fading soul
        // lingers at the death spot before dissolving away.
        // ====================================================================
        private static void CreateSeer() {
            Crew("seer", SeerCol);
            Crew("vic", DemoWhite);
            Crew("soul", SeerSoul);
            StageRect("flash", Color.white, 1f, 1f, 509);
            StageSprite("dieFx", UCFx.Ring, DemoWhite, 0.1f, 508);
            StageCap("bang", "!", 1.0f, SeerCol);
        }

        private static void AnimateSeer() {
            float p = P(7f);
            float fade = Ease(Seg(p, 0f, 0.06f)) * (1f - Ease(Seg(p, 0.94f, 1f)));

            FigPut("seer", 0.9f, 0f, true, 0f);
            FigCol("seer", SeerCol, fade);

            FigPut("vic", -0.7f, 0f, false, 0f);
            float dieSeg = Seg(p, 0.08f, 0.14f);
            FigCol("vic", DemoWhite, fade);
            FigDead("vic", Ease(dieSeg));
            Burst("dieFx", -0.7f, FloorY + 0.25f, Seg(p, 0.08f, 0.22f), 0.55f, DemoWhite);

            Size2("flash", stageSize.x, stageSize.y);
            float flashA = Ease(Seg(p, 0.1f, 0.16f)) * (1f - Ease(Seg(p, 0.22f, 0.32f)));
            ColA("flash", Color.white, flashA * 0.55f);

            float bangA = Ease(Seg(p, 0.1f, 0.15f)) * (1f - Ease(Seg(p, 0.2f, 0.28f)));
            PutCap("bang", 0.9f, 0.42f); CapA("bang", bangA * fade);

            float soulA = Ease(Seg(p, 0.32f, 0.44f)) * (1f - Ease(Seg(p, 0.82f, 0.95f)));
            float bob = 0.05f * Mathf.Sin(stageT * 2.2f);
            FigPut("soul", -0.7f, 0.1f + bob, false, 0f);
            FigCol("soul", SeerSoul, soulA * fade, soulA * fade * 0.25f);
        }

        // ====================================================================
        // Hacker: walks up, activates the ability (frozen while it's active), the admin
        // table reveals true colors and the vitals readout ticks up, then it ends.
        // ====================================================================
        private static void CreateHacker() {
            Crew("hak", HackerGreen);
            StageRect("table", new Color(1f, 1f, 1f, 0.08f), 1.1f, 0.42f, 504);
            StageDot("d0", DemoGray, 0.11f);
            StageDot("d1", DemoGray, 0.11f);
            StageDot("d2", DemoGray, 0.11f);
            MakeBtn("hackBtn", null);
            StageCap("vitals", "0s", 0.6f, Color.white);
        }

        private static void AnimateHacker() {
            float p = P(6.5f);
            float fade = Ease(Seg(p, 0f, 0.06f)) * (1f - Ease(Seg(p, 0.94f, 1f)));

            float inMove = Seg(p, 0.02f, 0.22f);
            float outMove = Seg(p, 0.76f, 0.96f);
            float hx = p < 0.5f ? Move(-1.6f, -0.5f, inMove) : Move(-0.5f, 1.6f, outMove);
            float hakWalk = (Mid(inMove) || Mid(outMove)) ? 1f : 0f;
            FigPut("hak", hx, 0f, false, hakWalk);
            FigCol("hak", HackerGreen, fade);

            BtnPop("hackBtn", -0.5f, BtnY, Seg(p, 0.22f, 0.36f));

            Put("table", 0.85f, 0.05f); ColA("table", Color.white, 0.08f * fade);

            float revealA = Ease(Seg(p, 0.32f, 0.42f)) * (1f - Ease(Seg(p, 0.62f, 0.72f)));
            Color c0 = Color.Lerp(DemoGray, DemoRed, revealA);
            Color c1 = Color.Lerp(DemoGray, DemoBlue, revealA);
            Color c2 = Color.Lerp(DemoGray, DemoGreen, revealA);
            Put("d0", 0.65f, 0.1f); ColA("d0", c0, fade);
            Put("d1", 0.85f, 0.1f); ColA("d1", c1, fade);
            Put("d2", 1.05f, 0.1f); ColA("d2", c2, fade);

            float vitalsA = Ease(Seg(p, 0.34f, 0.42f)) * (1f - Ease(Seg(p, 0.64f, 0.72f)));
            int secs = Mathf.FloorToInt(Mathf.Lerp(0f, 45f, Ease(Seg(p, 0.32f, 0.68f))));
            CapText("vitals", secs + "s");
            PutCap("vitals", 0.85f, 0.32f); CapA("vitals", vitalsA * fade);
        }

        // ====================================================================
        // Tracker: a target keeps walking, but the arrow/pin only updates at set
        // intervals - it lags behind the real position until the next refresh.
        // ====================================================================
        private static void CreateTracker() {
            Crew("trk", DemoOrange);
            Crew("tgt", DemoBlue);
            StageDot("pin", Accent, 0.12f);
            StageRect("line", Accent, 0.01f, 0.02f, 505);
            StageSprite("ping", UCFx.Ring, Accent, 0.1f, 508);
        }

        private static void AnimateTracker() {
            float p = P(7f);
            float fade = Ease(Seg(p, 0f, 0.06f)) * (1f - Ease(Seg(p, 0.94f, 1f)));

            FigPut("trk", -1.1f, 0f, false, 0f);
            FigCol("trk", DemoOrange, fade);

            float tgtX = Move(-1.55f, 1.55f, p);
            FigPut("tgt", tgtX, 0f, false, 1f);
            FigCol("tgt", DemoBlue, fade);

            // discrete "last known position" updates - the marker only jumps at these times
            float u0 = 0.12f, u1 = 0.36f, u2 = 0.58f, u3 = 0.8f;
            bool tracked = p >= u0;
            float markerAt = u0;
            if (p >= u3) markerAt = u3;
            else if (p >= u2) markerAt = u2;
            else if (p >= u1) markerAt = u1;
            else if (p >= u0) markerAt = u0;

            float mx = Move(-1.55f, 1.55f, markerAt);
            float markerA = tracked ? fade : 0f;
            Put("pin", mx, 0.4f); ColA("pin", Accent, markerA);

            float pingProg = tracked ? Seg(p, markerAt, markerAt + 0.14f) : 1f;
            Burst("ping", mx, 0.4f, pingProg, 0.5f, Accent);

            float lineLeft = Mathf.Min(-1.1f, mx);
            float lineW = Mathf.Abs(mx - (-1.1f));
            Put("line", lineLeft + lineW / 2f, FloorY + 0.05f);
            Size2("line", Mathf.Max(lineW, 0.001f), 0.014f);
            ColA("line", Accent, 0.4f * markerA);
        }

        // ====================================================================
        // Snitch: tasks fill up, evil gets warned in advance as they near completion,
        // then at the meeting the killer's last known location is revealed.
        // ====================================================================
        private static void CreateSnitch() {
            Crew("sni", SnitchTan);
            Crew("kil", DemoRed);
            StageRect("taskBarBg", new Color(1f, 1f, 1f, 0.14f), 0.9f, 0.06f);
            StageRect("taskBar", DemoGreen, 0.9f, 0.045f);
            StageCap("warnCap", "!", 1.1f, DemoRed);
            StageDot("pin", Accent, 0.12f);
            StageSprite("pingFx", UCFx.Ring, Accent, 0.1f, 508);
            StageCap("infoCap", "LOCATION!", 0.55f, Color.white);
        }

        private static void AnimateSnitch() {
            float p = P(7.5f);
            float fade = Ease(Seg(p, 0f, 0.06f)) * (1f - Ease(Seg(p, 0.94f, 1f)));

            FigPut("sni", -0.5f, 0f, false, 0f); FigCol("sni", SnitchTan, fade);
            FigPut("kil", 1.0f, 0f, true, 0f); FigCol("kil", DemoRed, fade);

            float fillProg = Ease(Seg(p, 0.04f, 0.42f));
            BarLeft("taskBarBg", -0.95f, 0.34f, 0.9f, 0.06f); ColA("taskBarBg", Color.white, 0.14f * fade);
            BarLeft("taskBar", -0.95f, 0.34f, 0.9f * fillProg, 0.045f); ColA("taskBar", DemoGreen, fade);

            // evil is warned in advance while the Snitch is still close to finishing
            float warnA = Ease(Seg(p, 0.3f, 0.4f)) * (1f - Ease(Seg(p, 0.46f, 0.54f)));
            PutCap("warnCap", 1.0f, 0.42f); CapA("warnCap", warnA * fade);

            // meeting: the killer's last known location is revealed
            float pinA = Ease(Seg(p, 0.62f, 0.7f)) * (1f - Ease(Seg(p, 0.92f, 0.98f)));
            Put("pin", 1.0f, 0.4f); ColA("pin", Accent, pinA * fade);
            Burst("pingFx", 1.0f, 0.4f, Seg(p, 0.62f, 0.76f), 0.5f, Accent);

            float infoA = Ease(Seg(p, 0.66f, 0.76f)) * (1f - Ease(Seg(p, 0.9f, 0.98f)));
            PutCap("infoCap", -0.5f, 0.42f); CapA("infoCap", infoA * fade);
        }
    }
}
