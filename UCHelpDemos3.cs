// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHelpDemos3 - bespoke role-help demo vignettes (see UCHelpMenu's demo-stage doc comment for the
 * shared stateless-animation contract). This pack covers eight TOR Neutral/Impostor roles that
 * revolve around meetings and secret win conditions rather than straightforward kill loops:
 *
 *   - Evil Guesser: correctly guesses a role and shoots it dead; guessing wrong kills the Guesser.
 *   - Jester:       acts suspicious, gets voted out on purpose - and wins for it.
 *   - Arsonist:     douses victims one at a time, then ignites everyone at once.
 *   - Jackal:       kills like an Impostor AND recruits a Sidekick into their own team.
 *   - Sidekick:     stands with the Jackal; if the Jackal dies, the Sidekick is promoted.
 *   - Vulture:      no kills of its own - eats existing corpses to win.
 *   - Lawyer:       secretly bonded to a client; wins together if that client survives.
 *   - Prosecutor:   secretly bonded to a Crewmate client; wins by getting them voted out.
 */

using System;
using UnityEngine;
using static UnknownsCollection.UCHelpMenu;

namespace UnknownsCollection {
    public static class UCHelpDemos3 {
        public static void Register() {
            RegisterDemo("Evil Guesser", CreateBadGuesser, AnimateBadGuesser);
            RegisterDemo("Jester", CreateJester, AnimateJester);
            RegisterDemo("Arsonist", CreateArsonist, AnimateArsonist);
            RegisterDemo("Jackal", CreateJackal, AnimateJackal);
            RegisterDemo("Sidekick", CreateSidekick, AnimateSidekick);
            RegisterDemo("Vulture", CreateVulture, AnimateVulture);
            RegisterDemo("Lawyer", CreateLawyer, AnimateLawyer);
            RegisterDemo("Prosecutor", CreateProsecutor, AnimateProsecutor);
        }

        // ================================================================
        // Evil Guesser - meeting-only shooter: right guess kills the target, wrong guess kills them.
        // ================================================================
        private static void CreateBadGuesser() {
            Crew("guesser", DemoRed);
            Crew("tgtA", DemoBlue);
            Crew("tgtB", DemoGreen);
            StageCap("q", "?", 1.1f, Accent);
            StageCap("hit", "HIT", 1.0f, DemoGreen);
            StageCap("miss", "MISS", 1.0f, DemoRed);
            StageSprite("fx1", UCFx.Ring, DemoRed, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateBadGuesser() {
            float p = P(7.6f);
            float fade = Ease(Seg(p, 0f, 0.05f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            const float gx = -0.7f, tx = 0.7f;
            float midY = FloorY + 0.25f;

            FigPut("guesser", gx, 0f, false, 0f);
            FigPut("tgtA", tx, 0f, true, 0f);
            FigPut("tgtB", tx, 0f, true, 0f);

            float guesserAlive = 1f - Seg(p, 0.58f, 0.68f);
            FigCol("guesser", DemoRed, fade * guesserAlive);
            FigDead("guesser", Seg(p, 0.58f, 0.70f));

            FigCol("tgtA", DemoBlue, fade * (1f - Seg(p, 0.30f, 0.38f)));
            FigDead("tgtA", Seg(p, 0.20f, 0.30f));

            FigCol("tgtB", DemoGreen, fade * Ease(Seg(p, 0.40f, 0.46f)));

            float q1 = Ease(Seg(p, 0.08f, 0.14f)) * (1f - Ease(Seg(p, 0.16f, 0.20f)));
            float q2 = Ease(Seg(p, 0.48f, 0.54f)) * (1f - Ease(Seg(p, 0.56f, 0.60f)));
            PutCap("q", tx, 0.30f);
            CapA("q", fade * (q1 + q2));

            PutCap("hit", tx, 0.32f);
            CapA("hit", fade * Ease(Seg(p, 0.19f, 0.24f)) * (1f - Ease(Seg(p, 0.30f, 0.36f))));

            PutCap("miss", gx, 0.32f);
            CapA("miss", fade * Ease(Seg(p, 0.57f, 0.62f)) * (1f - Ease(Seg(p, 0.68f, 0.74f))));

            Burst("fx1", tx, midY, Seg(p, 0.20f, 0.34f), 0.85f, DemoRed);
            Burst("fx2", gx, midY, Seg(p, 0.58f, 0.72f), 0.85f, DemoRed);
        }

        // ================================================================
        // Jester - acts shifty, gets voted out on purpose, wins for it.
        // ================================================================
        private static readonly Color JesterColor = new Color(0.95f, 0.50f, 0.80f);

        private static void CreateJester() {
            Crew("jester", JesterColor);
            Crew("c1", DemoBlue);
            Crew("c2", DemoGreen);
            StageDot("voteA", Accent, 0.08f);
            StageDot("voteB", Accent, 0.08f);
            StageCap("sus", "?", 1.0f, Accent);
            StageCap("win", "WIN", 1.2f, JesterColor);
            StageSprite("fx", UCFx.Ring, JesterColor, 0.1f, 508);
        }

        private static void AnimateJester() {
            float p = P(7.0f);
            float fade = Ease(Seg(p, 0f, 0.05f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            const float c1x = -1.1f, c2x = 1.1f;

            FigCol("c1", DemoBlue, fade);
            FigCol("c2", DemoGreen, fade);
            FigPut("c1", c1x, 0f, false, 0f);
            FigPut("c2", c2x, 0f, true, 0f);

            // shifty idle wiggle the whole time it's still on stage (walk>0 while standing still
            // reads as a nervous fidget via FigPut's built-in bob/waddle).
            float eject = Seg(p, 0.52f, 0.66f);
            float jx = Move(0f, 0.30f, eject);
            float jy = Move(0f, 0.55f, eject);
            FigPut("jester", jx, jy, false, Mid(eject) ? 0f : 0.4f);
            FigCol("jester", JesterColor, fade * (1f - Ease(eject)));

            bool glanceC1 = ((int)(stageT * 1.4f)) % 2 == 0;
            PutCap("sus", glanceC1 ? c1x : c2x, 0.30f);
            CapA("sus", fade * (0.5f + 0.5f * Mathf.Sin(stageT * 5f)) * Ease(Seg(p, 0.06f, 0.30f)) * (1f - Ease(Seg(p, 0.32f, 0.36f))));

            float voteWinA = Seg(p, 0.36f, 0.48f);
            float voteWinB = Seg(p, 0.40f, 0.50f);
            Put("voteA", Move(c1x, 0f, voteWinA), Move(0.1f, 0.05f, voteWinA));
            ColA("voteA", Accent, fade * (Mid(voteWinA) ? 1f : 0f));
            Put("voteB", Move(c2x, 0f, voteWinB), Move(0.1f, 0.05f, voteWinB));
            ColA("voteB", Accent, fade * (Mid(voteWinB) ? 1f : 0f));

            Burst("fx", 0f, FloorY + 0.25f, Seg(p, 0.48f, 0.60f), 0.7f, Accent);

            PutCap("win", 0f, 0.15f);
            CapA("win", fade * Ease(Seg(p, 0.68f, 0.78f)) * (1f - Ease(Seg(p, 0.90f, 0.96f))));
        }

        // ================================================================
        // Arsonist - douses each victim in turn, then ignites everyone at once.
        // ================================================================
        private static void CreateArsonist() {
            Crew("ars", DemoOrange);
            Crew("v1", DemoBlue);
            Crew("v2", DemoGreen);
            StageDot("splash1", DemoOrange, 0.09f);
            StageDot("splash2", DemoOrange, 0.09f);
            StageRect("barBg", new Color(1f, 1f, 1f, 0.12f), 0.7f, 0.055f);
            StageRect("bar", DemoOrange, 0.7f, 0.04f);
            StageSprite("fx1", UCFx.Ring, DemoOrange, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoOrange, 0.1f, 508);
            StageCap("win", "WIN", 1.0f, DemoOrange);
        }

        private static void AnimateArsonist() {
            float p = P(8.0f);
            float fade = Ease(Seg(p, 0f, 0.02f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            const float v1x = -1.1f, v2x = 1.1f;

            FigCol("v1", DemoBlue, fade);
            FigCol("v2", DemoGreen, fade);
            FigPut("v1", v1x, 0f, false, 0f);
            FigPut("v2", v2x, 0f, true, 0f);

            float approach1 = Seg(p, 0.03f, 0.15f);
            float douse1 = Seg(p, 0.17f, 0.28f);
            float approach2 = Seg(p, 0.30f, 0.44f);
            float douse2 = Seg(p, 0.46f, 0.57f);
            float retreat = Seg(p, 0.59f, 0.67f);

            float ax;
            bool aFace;
            float walk;
            if (p < 0.28f) { ax = Move(-1.65f, -0.85f, approach1); aFace = false; walk = Mid(approach1) ? 1f : 0f; }
            else if (p < 0.57f) { ax = Move(-0.85f, 0.85f, approach2); aFace = false; walk = Mid(approach2) ? 1f : 0f; }
            else if (p < 0.67f) { ax = Move(0.85f, 0f, retreat); aFace = true; walk = Mid(retreat) ? 1f : 0f; }
            else { ax = 0f; aFace = true; walk = 0f; }
            FigPut("ars", ax, 0f, aFace, walk);
            FigCol("ars", DemoOrange, fade);

            Put("splash1", v1x, FloorY + 0.02f);
            ColA("splash1", DemoOrange, fade * (0.4f + 0.4f * Mathf.Sin(stageT * 9f)) * Ease(douse1) * (1f - Ease(Seg(p, 0.28f, 0.32f))));
            Put("splash2", v2x, FloorY + 0.02f);
            ColA("splash2", DemoOrange, fade * (0.4f + 0.4f * Mathf.Sin(stageT * 9f)) * Ease(douse2) * (1f - Ease(Seg(p, 0.57f, 0.61f))));

            bool showBar1 = Mid(douse1);
            bool showBar2 = Mid(douse2);
            float barX = showBar1 ? v1x : v2x;
            float fill = showBar1 ? douse1 : (showBar2 ? douse2 : 0f);
            bool barVisible = showBar1 || showBar2;
            BarLeft("barBg", barX - 0.35f, BtnY, 0.7f, 0.055f);
            ColA("barBg", new Color(1f, 1f, 1f, 1f), barVisible ? 0.12f * fade : 0f);
            BarLeft("bar", barX - 0.35f, BtnY, 0.7f * fill, 0.04f);
            ColA("bar", DemoOrange, barVisible ? fade : 0f);

            float dead = Ease(Seg(p, 0.72f, 0.84f));
            FigDead("v1", Seg(p, 0.72f, 0.84f));
            FigDead("v2", Seg(p, 0.72f, 0.84f));
            FigCol("v1", Color.Lerp(DemoBlue, DemoOrange, dead * 0.8f), fade);
            FigCol("v2", Color.Lerp(DemoGreen, DemoOrange, dead * 0.8f), fade);
            Burst("fx1", v1x, FloorY + 0.25f, Seg(p, 0.70f, 0.84f), 0.8f, DemoOrange);
            Burst("fx2", v2x, FloorY + 0.25f, Seg(p, 0.70f, 0.84f), 0.8f, DemoOrange);

            float hop = Mathf.Sin(Mathf.Clamp01(Seg(p, 0.84f, 0.92f)) * Mathf.PI) * 0.10f;
            if (p >= 0.67f) FigPut("ars", 0f, hop, true, 0f);

            PutCap("win", 0f, 0.16f);
            CapA("win", fade * Ease(Seg(p, 0.86f, 0.92f)) * (1f - Ease(Seg(p, 0.96f, 1f))));
        }

        // ================================================================
        // Jackal - kills like an Impostor, then recruits a Sidekick into their own team.
        // ================================================================
        private static readonly Color JackalColor = new Color(0.70f, 0.62f, 0.22f);

        private static void CreateJackal() {
            Crew("jackal", JackalColor);
            Crew("vic", DemoBlue);
            Crew("rec", DemoGreen);
            MakeBtn("killBtn", KillButtonSprite(null));
            MakeBtn("recruitBtn", null);
            StageCap("plus", "+1", 1.1f, JackalColor);
            StageSprite("fx1", UCFx.Ring, DemoRed, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, JackalColor, 0.1f, 508);
        }

        private static void AnimateJackal() {
            float p = P(7.6f);
            float fade = Ease(Seg(p, 0f, 0.04f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            const float vicX = -1.2f, recX = 1.2f;

            FigPut("vic", vicX, 0f, false, 0f);
            FigPut("rec", recX, 0f, true, 0f);

            float toKill = Seg(p, 0.03f, 0.19f);
            float backCenter = Seg(p, 0.30f, 0.42f);
            float toRecruit = Seg(p, 0.44f, 0.60f);
            float jx;
            bool jFace;
            float walk;
            if (p < 0.22f) { jx = Move(0f, -0.85f, toKill); jFace = true; walk = Mid(toKill) ? 1f : 0f; }
            else if (p < 0.42f) { jx = Move(-0.85f, 0f, backCenter); jFace = false; walk = Mid(backCenter) ? 1f : 0f; }
            else if (p < 0.62f) { jx = Move(0f, 0.85f, toRecruit); jFace = false; walk = Mid(toRecruit) ? 1f : 0f; }
            else { jx = 0.85f; jFace = false; walk = 0f; }
            FigPut("jackal", jx, 0f, jFace, walk);
            FigCol("jackal", JackalColor, fade);

            FigCol("vic", DemoBlue, fade * (1f - Ease(Seg(p, 0.26f, 0.34f))));
            FigDead("vic", Seg(p, 0.20f, 0.28f));
            BtnPop("killBtn", jx, BtnY, Seg(p, 0.16f, 0.30f));
            Burst("fx1", vicX, FloorY + 0.25f, Seg(p, 0.19f, 0.32f), 0.85f, DemoRed);

            float recruited = Ease(Seg(p, 0.62f, 0.74f));
            FigCol("rec", Color.Lerp(DemoGreen, JackalColor, recruited), fade);
            BtnPop("recruitBtn", jx, BtnY, Seg(p, 0.58f, 0.72f));
            Burst("fx2", recX, FloorY + 0.25f, Seg(p, 0.62f, 0.76f), 0.7f, JackalColor);

            PutCap("plus", recX, 0.30f);
            CapA("plus", fade * Ease(Seg(p, 0.64f, 0.70f)) * (1f - Ease(Seg(p, 0.86f, 0.94f))));

            float hop = Mathf.Sin(Mathf.Clamp01(Seg(p, 0.78f, 0.90f)) * Mathf.PI) * 0.08f;
            if (p >= 0.62f) FigPut("jackal", jx, hop, true, 0f);
        }

        // ================================================================
        // Sidekick - stands with the Jackal; if the Jackal dies, the Sidekick is promoted.
        // ================================================================
        private static readonly Color SidekickTeamColor = new Color(0.70f, 0.62f, 0.22f);

        private static void CreateSidekick() {
            Crew("jack", SidekickTeamColor);
            Crew("side", Color.Lerp(Color.white, SidekickTeamColor, 0.55f));
            StageCap("label", "JACKAL", 0.85f, SidekickTeamColor);
            StageSprite("fx1", UCFx.Ring, DemoRed, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, SidekickTeamColor, 0.1f, 508);
        }

        private static void AnimateSidekick() {
            float p = P(7.2f);
            float fade = Ease(Seg(p, 0f, 0.05f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            const float jackX = -0.3f, sideX = 0.3f;

            FigPut("jack", jackX, 0f, false, 0.15f);
            FigPut("side", sideX, 0f, true, 0.15f);

            FigCol("jack", SidekickTeamColor, fade * (1f - Ease(Seg(p, 0.40f, 0.50f))));
            FigDead("jack", Seg(p, 0.32f, 0.44f));
            Burst("fx1", jackX, FloorY + 0.25f, Seg(p, 0.32f, 0.46f), 0.85f, DemoRed);

            float promoted = Ease(Seg(p, 0.52f, 0.66f));
            Color sideBase = Color.Lerp(Color.white, SidekickTeamColor, 0.55f);
            FigCol("side", Color.Lerp(sideBase, SidekickTeamColor, promoted), fade);
            Burst("fx2", sideX, FloorY + 0.25f, Seg(p, 0.52f, 0.66f), 0.7f, SidekickTeamColor);

            PutCap("label", sideX, 0.32f);
            CapA("label", fade * Ease(Seg(p, 0.56f, 0.64f)) * (1f - Ease(Seg(p, 0.88f, 0.95f))));

            float hop = Mathf.Sin(Mathf.Clamp01(Seg(p, 0.68f, 0.80f)) * Mathf.PI) * 0.09f;
            FigPut("side", sideX, hop, true, 0f);
        }

        // ================================================================
        // Vulture - no kills of its own; wins by eating a set number of existing corpses.
        // ================================================================
        private static readonly Color VultureColor = new Color(0.55f, 0.42f, 0.30f);

        private static void CreateVulture() {
            Crew("vul", VultureColor);
            Crew("b1", DemoWhite);
            Crew("b2", DemoWhite);
            StageCap("count", "1/2", 0.9f, Accent);
            StageCap("win", "WIN", 1.0f, VultureColor);
            StageSprite("fx1", UCFx.Ring, VultureColor, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, VultureColor, 0.1f, 508);
        }

        private static void AnimateVulture() {
            float p = P(7.4f);
            float fade = Ease(Seg(p, 0f, 0.04f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            const float b1x = -1.1f, b2x = 1.1f;

            float approach1 = Seg(p, 0.06f, 0.28f);
            float approach2 = Seg(p, 0.48f, 0.70f);

            float vx;
            bool vFace;
            float walk;
            if (p < 0.28f) { vx = Move(0f, -0.85f, approach1); vFace = true; walk = Mid(approach1) ? 1f : 0f; }
            else if (p < 0.70f) { vx = Move(-0.85f, 0.85f, approach2); vFace = false; walk = Mid(approach2) ? 1f : 0f; }
            else { vx = 0.85f; vFace = false; walk = 0f; }
            FigPut("vul", vx, 0f, vFace, walk);
            FigCol("vul", VultureColor, fade);

            FigPut("b1", b1x, 0f, false, 0f);
            FigDead("b1", 1f);
            FigCol("b1", DemoWhite, fade * (1f - Ease(Seg(p, 0.32f, 0.40f))));

            FigPut("b2", b2x, 0f, false, 0f);
            FigDead("b2", 1f);
            FigCol("b2", DemoWhite, fade * Ease(Seg(p, 0.44f, 0.50f)) * (1f - Ease(Seg(p, 0.74f, 0.82f))));

            Burst("fx1", b1x, FloorY + 0.15f, Seg(p, 0.30f, 0.42f), 0.7f, VultureColor);
            Burst("fx2", b2x, FloorY + 0.15f, Seg(p, 0.72f, 0.84f), 0.7f, VultureColor);

            PutCap("count", vx, 0.30f);
            float c1 = Ease(Seg(p, 0.32f, 0.38f)) * (1f - Ease(Seg(p, 0.44f, 0.48f)));
            float c2 = Ease(Seg(p, 0.74f, 0.80f)) * (1f - Ease(Seg(p, 0.86f, 0.90f)));
            CapText("count", p < 0.6f ? "1/2" : "2/2");
            CapA("count", fade * (c1 + c2));

            PutCap("win", 0.85f, 0.30f);
            CapA("win", fade * Ease(Seg(p, 0.86f, 0.92f)) * (1f - Ease(Seg(p, 0.96f, 1f))));
        }

        // ================================================================
        // Lawyer - secretly bonded to a client; wins together if that client survives to the end.
        // ================================================================
        private static readonly Color LawyerColor = new Color(0.75f, 0.60f, 0.30f);

        private static void CreateLawyer() {
            Crew("law", LawyerColor);
            Crew("client", DemoRed);
            StageDot("d0", LawyerColor, 0.05f);
            StageDot("d1", LawyerColor, 0.05f);
            StageDot("d2", LawyerColor, 0.05f);
            StageRect("barBg", new Color(1f, 1f, 1f, 0.12f), 0.9f, 0.05f);
            StageRect("bar", LawyerColor, 0.9f, 0.036f);
            StageCap("win", "WIN", 1.2f, LawyerColor);
            StageSprite("fx", UCFx.Ring, LawyerColor, 0.1f, 508);
        }

        private static void AnimateLawyer() {
            float p = P(7.2f);
            float fade = Ease(Seg(p, 0f, 0.05f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            const float lawX = -0.5f, cliX = 0.5f;

            FigPut("law", lawX, 0f, false, 0.1f);
            FigPut("client", cliX, 0f, true, 0.1f);
            FigCol("law", LawyerColor, fade);
            FigCol("client", DemoRed, fade);

            float bondPulse = 0.35f + 0.35f * Mathf.Sin(stageT * 3.2f);
            float bondWindow = 1f - Ease(Seg(p, 0.55f, 0.62f));
            for (int i = 0; i < 3; i++) {
                float x = Mathf.Lerp(lawX + 0.15f, cliX - 0.15f, (i + 1) / 4f);
                Put("d" + i, x, 0.06f);
                ColA("d" + i, LawyerColor, fade * bondPulse * bondWindow);
            }

            float countdown = Seg(p, 0.06f, 0.56f);
            BarLeft("barBg", -0.45f, BtnY, 0.9f, 0.05f);
            ColA("barBg", new Color(1f, 1f, 1f, 1f), 0.12f * fade);
            BarLeft("bar", -0.45f, BtnY, 0.9f * (1f - countdown), 0.036f);
            ColA("bar", LawyerColor, fade * (1f - Ease(Seg(p, 0.55f, 0.60f))));

            Burst("fx", 0f, FloorY + 0.25f, Seg(p, 0.56f, 0.68f), 0.9f, LawyerColor);

            float hop = Mathf.Sin(Mathf.Clamp01(Seg(p, 0.62f, 0.78f)) * Mathf.PI) * 0.08f;
            FigPut("law", lawX, hop, false, 0f);
            FigPut("client", cliX, hop, true, 0f);

            PutCap("win", 0f, 0.34f);
            CapA("win", fade * Ease(Seg(p, 0.60f, 0.68f)) * (1f - Ease(Seg(p, 0.90f, 0.96f))));
        }

        // ================================================================
        // Prosecutor - secretly bonded to a Crewmate client; wins by getting them voted out.
        // ================================================================
        private static readonly Color ProsecutorColor = new Color(0.42f, 0.55f, 0.78f);

        private static void CreateProsecutor() {
            Crew("pros", ProsecutorColor);
            Crew("client", DemoGreen);
            StageDot("d0", ProsecutorColor, 0.05f);
            StageDot("d1", ProsecutorColor, 0.05f);
            StageDot("d2", ProsecutorColor, 0.05f);
            StageDot("voteA", Accent, 0.08f);
            StageDot("voteB", Accent, 0.08f);
            StageCap("win", "WIN", 1.2f, ProsecutorColor);
            StageSprite("fx", UCFx.Ring, ProsecutorColor, 0.1f, 508);
        }

        private static void AnimateProsecutor() {
            float p = P(7.2f);
            float fade = Ease(Seg(p, 0f, 0.05f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            const float prosX = -0.5f, cliX = 0.5f;

            FigPut("pros", prosX, 0f, false, 0.1f);
            FigCol("pros", ProsecutorColor, fade);

            float bondPulse = 0.35f + 0.35f * Mathf.Sin(stageT * 3.2f);
            float bondWindow = 1f - Ease(Seg(p, 0.28f, 0.34f));
            for (int i = 0; i < 3; i++) {
                float x = Mathf.Lerp(prosX + 0.15f, cliX - 0.15f, (i + 1) / 4f);
                Put("d" + i, x, 0.06f);
                ColA("d" + i, ProsecutorColor, fade * bondPulse * bondWindow);
            }

            float voteWinA = Seg(p, 0.30f, 0.42f);
            float voteWinB = Seg(p, 0.34f, 0.46f);
            Put("voteA", Move(-1.2f, cliX, voteWinA), Move(0.10f, 0.05f, voteWinA));
            ColA("voteA", Accent, fade * (Mid(voteWinA) ? 1f : 0f));
            Put("voteB", Move(1.2f, cliX, voteWinB), Move(0.10f, 0.05f, voteWinB));
            ColA("voteB", Accent, fade * (Mid(voteWinB) ? 1f : 0f));

            float eject = Seg(p, 0.48f, 0.64f);
            float cx = Move(cliX, cliX + 0.25f, eject);
            float cy = Move(0f, 0.55f, eject);
            FigPut("client", cx, cy, true, Mid(eject) ? 0f : 0.1f);
            FigCol("client", DemoGreen, fade * (1f - Ease(eject)));

            Burst("fx", cliX, FloorY + 0.25f, Seg(p, 0.46f, 0.58f), 0.8f, ProsecutorColor);

            float hop = Mathf.Sin(Mathf.Clamp01(Seg(p, 0.68f, 0.82f)) * Mathf.PI) * 0.09f;
            FigPut("pros", prosX, hop, false, 0f);

            PutCap("win", prosX, 0.34f);
            CapA("win", fade * Ease(Seg(p, 0.70f, 0.78f)) * (1f - Ease(Seg(p, 0.92f, 0.97f))));
        }
    }
}
