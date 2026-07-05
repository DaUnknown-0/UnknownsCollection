// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHelpDemos2 - bespoke help-menu demo vignettes (see UCHelpMenu.RegisterDemo) for:
 *   Cleaner, Warlock, Bounty Hunter, Witch, Ninja, Bomber, Yo-Yo, Nice Guesser.
 *
 * Each Create* method registers the small cast of actors/props for that role's stage (called
 * once when the role is selected); each Animate* method is a fully STATELESS function of
 * UCHelpMenu.stageT (via P/Seg/Ease/Move/Mid), called every frame while the panel is open, so
 * nothing here ever accumulates state between frames - only stageT drives the whole loop.
 */

using System;
using UnityEngine;
using static UnknownsCollection.UCHelpMenu;

namespace UnknownsCollection {
    public static class UCHelpDemos2 {
        public static void Register() {
            RegisterDemo("Cleaner", CreateCleaner, AnimateCleaner);
            RegisterDemo("Warlock", CreateWarlock, AnimateWarlock);
            RegisterDemo("Bounty Hunter", CreateBountyHunter, AnimateBountyHunter);
            RegisterDemo("Witch", CreateWitch, AnimateWitch);
            RegisterDemo("Ninja", CreateNinja, AnimateNinja);
            RegisterDemo("Bomber", CreateBomber, AnimateBomber);
            RegisterDemo("Yo-Yo", CreateYoYo, AnimateYoYo);
            RegisterDemo("Nice Guesser", CreateNiceGuesser, AnimateNiceGuesser);
        }

        // Fade-in/out envelope for the first/last ~8% of any loop period (p = P(period)).
        // Keeps teleports/pop-ins/pop-outs from ever reading as a jump at the loop seam.
        private static float Env(float p) => Ease(Seg(p, 0f, 0.08f)) * (1f - Ease(Seg(p, 0.92f, 1f)));

        // ================================================================================
        // Cleaner - kills, then wipes the body away entirely (shares cooldown with the kill,
        // so it cannot immediately clean its own victim - shown here as a short beat between).
        // ================================================================================
        private static void CreateCleaner() {
            Crew("cln", DemoRed);
            Crew("vic", DemoBlue);
            MakeBtn("killBtn", KillButtonSprite(null));
            MakeBtn("cleanBtn", null);
            StageSprite("sm0", UCFx.Smoke, DemoGray, 0.3f, 507);
            StageSprite("sm1", UCFx.Smoke, DemoGray, 0.22f, 507);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
            StageCap("tag", "CLEANED", 0.8f, Color.white);
        }

        private static void AnimateCleaner() {
            float p = P(7f);
            float env = Env(p);
            float midY = FloorY + 0.25f;

            float enter = Seg(p, 0.03f, 0.20f);
            float exit = Seg(p, 0.86f, 0.98f);
            float clnX = p < 0.86f ? Move(-1.5f, -0.25f, enter) : Move(-0.25f, 1.55f, exit);
            bool clnWalk = Mid(enter) || Mid(exit);
            FigPut("cln", clnX, 0f, false, clnWalk ? 1f : 0f);
            FigCol("cln", DemoRed, env);

            FigPut("vic", 0.15f, 0f, true, 0f);
            float kill = Seg(p, 0.22f, 0.30f);
            float cleanFade = Ease(Seg(p, 0.60f, 0.78f));
            FigCol("vic", DemoBlue, env * (1f - cleanFade));
            FigDead("vic", kill);

            BtnPop("killBtn", clnX, BtnY, Seg(p, 0.14f, 0.28f));
            BtnPop("cleanBtn", clnX, BtnY, Seg(p, 0.44f, 0.58f));
            Burst("fx", 0.15f, midY, Seg(p, 0.22f, 0.36f), 0.6f, DemoRed);

            float smokeWin = Seg(p, 0.58f, 0.80f);
            Put("sm0", 0.09f, FloorY + 0.08f + 0.22f * smokeWin);
            ColA("sm0", DemoGray, env * Ease(Seg(p, 0.58f, 0.66f)) * (1f - Ease(Seg(p, 0.70f, 0.80f))));
            Put("sm1", 0.21f, FloorY + 0.05f + 0.26f * smokeWin);
            ColA("sm1", DemoGray, env * Ease(Seg(p, 0.62f, 0.70f)) * (1f - Ease(Seg(p, 0.74f, 0.84f))));

            PutCap("tag", 0.15f, 0.30f);
            CapA("tag", env * Ease(Seg(p, 0.60f, 0.66f)) * (1f - Ease(Seg(p, 0.74f, 0.82f))));
        }

        // ================================================================================
        // Warlock - secretly curses A; once A stands near B, the Warlock can strike B from any
        // distance. Using the curse lifts it and roots the Warlock in place for a while.
        // ================================================================================
        private static void CreateWarlock() {
            Crew("wl", DemoRed);
            Crew("a", DemoBlue);
            Crew("b", DemoGreen);
            MakeBtn("curseBtn", null);
            StageCap("curse", "!", 1.0f, DemoPurple);
            StageSprite("beam", UCFx.Streak, DemoPurple, 1f, 508);
            StageSprite("rootRing", UCFx.Ring, DemoPurple, 0.3f, 507);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateWarlock() {
            float p = P(7.5f);
            float env = Env(p);
            float midY = FloorY + 0.25f;

            const float wlX = -1.55f;
            FigPut("wl", wlX, 0f, false, 0f);
            FigCol("wl", DemoRed, env);

            float enter = Seg(p, 0.10f, 0.50f);
            float ax = Move(-0.9f, 0.7f, enter);
            FigPut("a", ax, 0f, false, Mid(enter) ? 1f : 0f);
            FigCol("a", DemoBlue, env);

            const float bx = 1.0f;
            FigPut("b", bx, 0f, true, 0f);
            FigCol("b", DemoGreen, env);
            float kill = Seg(p, 0.58f, 0.68f);
            FigDead("b", kill);

            PutCap("curse", ax, 0.22f);
            CapA("curse", env * Ease(Seg(p, 0.08f, 0.16f)) * (1f - Ease(Seg(p, 0.58f, 0.66f))));

            BtnPop("curseBtn", wlX, BtnY, Seg(p, 0.50f, 0.62f));

            float beamA = Ease(Seg(p, 0.52f, 0.58f)) * (1f - Ease(Seg(p, 0.64f, 0.70f)));
            Put("beam", (wlX + bx) / 2f, midY);
            Size2("beam", bx - wlX, 0.05f);
            ColA("beam", DemoPurple, env * beamA);

            Burst("fx", bx, midY, Seg(p, 0.58f, 0.72f), 0.7f, DemoRed);

            float ringPulse = 0.28f + 0.04f * Mathf.Sin(stageT * 6f);
            Put("rootRing", wlX, FloorY + 0.03f);
            Size2("rootRing", ringPulse, ringPulse);
            float rootA = Ease(Seg(p, 0.66f, 0.78f)) * (1f - Ease(Seg(p, 0.86f, 0.95f)));
            ColA("rootRing", DemoPurple, env * rootA);
        }

        // ================================================================================
        // Bounty Hunter - secret bounty target; killing them slashes the cooldown (green),
        // killing anyone else lengthens it instead (red). Same actors, two outcomes, one loop.
        // ================================================================================
        private static void CreateBountyHunter() {
            Crew("bh", DemoRed);
            Crew("bounty", DemoBlue);
            Crew("decoy", DemoGreen);
            MakeBtn("killBtn", KillButtonSprite(null));
            StageCap("arrow", "^", 1.2f, Accent);
            StageRect("cdBg", new Color(1f, 1f, 1f, 0.12f), 0.9f, 0.06f);
            StageRect("cd", DemoOrange, 0.9f, 0.045f);
            StageCap("minusCD", "-CD", 0.85f, DemoGreen);
            StageCap("plusCD", "+CD", 0.85f, DemoRed);
            StageSprite("fx1", UCFx.Ring, DemoGreen, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateBountyHunter() {
            float p = P(7.5f);
            float env = Env(p);
            float midY = FloorY + 0.25f;

            const float bountyX = -0.55f;
            const float decoyX = 0.65f;
            float e1 = Seg(p, 0.04f, 0.20f);
            float e2 = Seg(p, 0.46f, 0.62f);
            float bhX = p < 0.46f ? Move(-1.5f, bountyX + 0.25f, e1) : Move(bountyX + 0.25f, decoyX, e2);
            FigPut("bh", bhX, 0f, false, (Mid(e1) || Mid(e2)) ? 1f : 0f);
            FigCol("bh", DemoRed, env);

            FigPut("bounty", bountyX, 0f, true, 0f);
            float kill1 = Seg(p, 0.20f, 0.28f);
            FigCol("bounty", DemoBlue, env);
            FigDead("bounty", kill1);

            FigPut("decoy", 0.9f, 0f, true, 0f);
            float kill2 = Seg(p, 0.62f, 0.70f);
            FigCol("decoy", DemoGreen, env);
            FigDead("decoy", kill2);

            PutCap("arrow", bountyX, 0.24f + 0.03f * Mathf.Sin(stageT * 3f));
            CapA("arrow", env * (1f - Ease(Seg(p, 0.18f, 0.26f))));

            float btnProg = p < 0.4f ? Seg(p, 0.14f, 0.26f) : Seg(p, 0.56f, 0.68f);
            BtnPop("killBtn", bhX, BtnY, btnProg);

            Burst("fx1", bountyX, midY, Seg(p, 0.20f, 0.34f), 0.7f, DemoGreen);
            Burst("fx2", 0.9f, midY, Seg(p, 0.62f, 0.76f), 0.7f, DemoRed);

            float w = 0.42f;
            w = Mathf.Lerp(w, 0.10f, Ease(Seg(p, 0.28f, 0.40f)));
            w = Mathf.Lerp(w, 0.85f, Ease(Seg(p, 0.70f, 0.84f)));
            w = Mathf.Lerp(w, 0.42f, Ease(Seg(p, 0.90f, 1.0f)));
            BarLeft("cdBg", -0.45f, 0.30f, 0.9f, 0.06f);
            BarLeft("cd", -0.45f, 0.30f, 0.9f * w, 0.045f);

            PutCap("minusCD", -0.45f, 0.40f);
            CapA("minusCD", env * Ease(Seg(p, 0.28f, 0.36f)) * (1f - Ease(Seg(p, 0.44f, 0.54f))));
            PutCap("plusCD", -0.45f, 0.40f);
            CapA("plusCD", env * Ease(Seg(p, 0.70f, 0.78f)) * (1f - Ease(Seg(p, 0.86f, 0.94f))));
        }

        // ================================================================================
        // Witch - casts a spell that resolves right after the meeting ends. The spelled mark
        // stays visible through the whole meeting, and only THEN does the victim drop.
        // ================================================================================
        private static void CreateWitch() {
            Crew("witch", DemoRed);
            Crew("vic", DemoBlue);
            MakeBtn("spellBtn", null);
            StageCap("curse", "*", 1.3f, DemoPurple);
            StageRect("table", new Color(0.34f, 0.25f, 0.16f, 0.5f), 0.5f, 0.12f, 504);
            StageCap("meet", "MEETING", 0.8f, Accent);
            StageSprite("fx", UCFx.Ring, DemoPurple, 0.1f, 508);
        }

        private static void AnimateWitch() {
            float p = P(7.5f);
            float env = Env(p);
            float midY = FloorY + 0.25f;

            float enter = Seg(p, 0.04f, 0.22f);
            float cw = Seg(p, 0.34f, 0.48f);
            float cv = Seg(p, 0.34f, 0.48f);
            float witchX = p < 0.34f ? Move(-1.5f, -0.6f, enter) : Move(-0.6f, -0.25f, cw);
            float vicX = p < 0.34f ? 0.5f : Move(0.5f, 0.25f, cv);

            FigPut("witch", witchX, 0f, false, (Mid(enter) || Mid(cw)) ? 1f : 0f);
            FigCol("witch", DemoRed, env);

            FigPut("vic", vicX, 0f, true, Mid(cv) ? 1f : 0f);
            float kill = Seg(p, 0.82f, 0.90f);
            FigCol("vic", DemoBlue, env);
            FigDead("vic", kill);

            BtnPop("spellBtn", witchX, BtnY, Seg(p, 0.16f, 0.30f));

            PutCap("curse", vicX, 0.22f);
            CapA("curse", env * Ease(Seg(p, 0.24f, 0.32f)) * (1f - Ease(Seg(p, 0.84f, 0.92f))));

            Put("table", 0f, FloorY + 0.05f);

            PutCap("meet", 0f, 0.40f);
            CapA("meet", env * Ease(Seg(p, 0.40f, 0.50f)) * (1f - Ease(Seg(p, 0.74f, 0.82f))));

            Burst("fx", vicX, midY, Seg(p, 0.82f, 0.94f), 0.7f, DemoPurple);
        }

        // ================================================================================
        // Ninja - marks a player, then blinks straight to their CURRENT spot (even if they
        // moved since) and kills instantly, leaving a fading trace at both ends of the jump.
        // ================================================================================
        private static void CreateNinja() {
            Crew("nin", DemoRed);
            Crew("vic", DemoBlue);
            MakeBtn("markBtn", null);
            MakeBtn("blinkBtn", null);
            StageCap("mark", "X", 1.1f, Accent);
            StageSprite("traceA", UCFx.Smoke, DemoRed, 0.24f, 507);
            StageSprite("traceB", UCFx.Smoke, DemoRed, 0.24f, 507);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateNinja() {
            float p = P(8f);
            float env = Env(p);
            float midY = FloorY + 0.25f;

            float enter = Seg(p, 0.03f, 0.16f);
            float ninjaX = p < 0.60f ? Move(-1.5f, -0.6f, enter) : 0.9f;
            FigPut("nin", ninjaX, 0f, false, Mid(enter) ? 1f : 0f);

            float invis = Ease(Seg(p, 0.66f, 0.74f)) * (1f - Ease(Seg(p, 0.82f, 0.92f)));
            FigCol("nin", DemoRed, env * (1f - 0.7f * invis));

            float reloc = Seg(p, 0.30f, 0.52f);
            float vicX = p < 0.30f ? -0.3f : (p < 0.52f ? Move(-0.3f, 0.9f, reloc) : 0.9f);
            FigPut("vic", vicX, 0f, false, Mid(reloc) ? 1f : 0f);
            float kill = Seg(p, 0.60f, 0.68f);
            FigCol("vic", DemoBlue, env);
            FigDead("vic", kill);

            BtnPop("markBtn", -0.6f, BtnY, Seg(p, 0.12f, 0.24f));
            BtnPop("blinkBtn", -0.6f, BtnY, Seg(p, 0.52f, 0.62f));

            PutCap("mark", vicX, 0.22f);
            CapA("mark", env * Ease(Seg(p, 0.20f, 0.28f)) * (1f - Ease(Seg(p, 0.60f, 0.66f))));

            Put("traceA", -0.6f, FloorY + 0.05f);
            ColA("traceA", DemoRed, env * Ease(Seg(p, 0.58f, 0.64f)) * (1f - Ease(Seg(p, 0.66f, 0.76f))));
            Put("traceB", 0.9f, FloorY + 0.05f);
            ColA("traceB", DemoRed, env * Ease(Seg(p, 0.60f, 0.66f)) * (1f - Ease(Seg(p, 0.70f, 0.82f))));

            Burst("fx", 0.9f, midY, Seg(p, 0.60f, 0.72f), 0.7f, DemoRed);
        }

        // ================================================================================
        // Bomber - plants a bomb that blinks faster and faster; a nearby crewmate tries (and
        // fails) to defuse it in time before it detonates and scatters everyone close by.
        // ================================================================================
        private static void CreateBomber() {
            Crew("bmb", DemoRed);
            Crew("c1", DemoBlue);
            Crew("c2", DemoGreen);
            MakeBtn("plantBtn", null);
            MakeBtn("defuseBtn", null);
            StagePic("bomb", UCAssets.OverlayBomb, 0.2f, 507);
            StagePic("burstPic", UCAssets.OverlayBurst, 0.3f, 509);
            StageSprite("fx", UCFx.Ring, DemoOrange, 0.1f, 508);
        }

        private static void AnimateBomber() {
            float p = P(7.5f);
            float env = Env(p);
            float midY = FloorY + 0.25f;

            float enter = Seg(p, 0.03f, 0.18f);
            float exit = Seg(p, 0.30f, 0.46f);
            float bmbX = p < 0.30f ? Move(-1.5f, 0.3f, enter) : Move(0.3f, -1.55f, exit);
            FigPut("bmb", bmbX, 0f, false, (Mid(enter) || Mid(exit)) ? 1f : 0f);
            FigCol("bmb", DemoRed, env);

            BtnPop("plantBtn", 0.3f, BtnY, Seg(p, 0.14f, 0.26f));

            float appr = Seg(p, 0.48f, 0.62f);
            float scatterC1 = Seg(p, 0.84f, 0.92f);
            float c1X = p < 0.84f ? (p < 0.48f ? -0.15f : Move(-0.15f, 0.15f, appr)) : Move(0.15f, -0.35f, scatterC1);
            FigPut("c1", c1X, 0f, false, (Mid(appr) || Mid(scatterC1)) ? 1f : 0f);
            FigCol("c1", DemoBlue, env);
            FigDead("c1", Seg(p, 0.84f, 0.94f));

            float scatterC2 = Seg(p, 0.84f, 0.92f);
            float c2X = p < 0.84f ? 0.75f : Move(0.75f, 1.05f, scatterC2);
            FigPut("c2", c2X, 0f, true, Mid(scatterC2) ? 1f : 0f);
            FigCol("c2", DemoGreen, env);
            FigDead("c2", Seg(p, 0.84f, 0.94f));

            BtnPop("defuseBtn", c1X, BtnY, Seg(p, 0.60f, 0.72f));

            float blink = 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(stageT * (3f + 14f * p)));
            Color bombTint = Color.Lerp(Color.white, DemoRed, blink);
            Put("bomb", 0.3f, FloorY + 0.2f);
            float bombA = Ease(Seg(p, 0.20f, 0.28f)) * (1f - Ease(Seg(p, 0.86f, 0.94f)));
            ColA("bomb", bombTint, env * bombA);

            float explode = Seg(p, 0.82f, 0.90f);
            Put("burstPic", 0.3f, midY);
            PicScale("burstPic", Mathf.Lerp(0.06f, 0.55f, Ease(explode)));
            ColA("burstPic", Color.white, env * Ease(Seg(p, 0.82f, 0.87f)) * (1f - Ease(Seg(p, 0.90f, 0.97f))));
            Burst("fx", 0.3f, midY, Seg(p, 0.82f, 0.94f), 0.9f, DemoOrange);
        }

        // ================================================================================
        // Yo-Yo - marks a spot, blinks there instantly, then AUTOMATICALLY blinks back after a
        // limited window - no second button press needed. Faint silhouettes linger at both ends.
        // ================================================================================
        private static void CreateYoYo() {
            Crew("yo", DemoRed);
            Crew("vic", DemoBlue);
            MakeBtn("blinkBtn", null);
            StageDot("markDot", Accent, 0.09f);
            StageSprite("siloA", UCFx.Dot, DemoRed, 0.16f, 506);
            StageSprite("siloB", UCFx.Dot, DemoRed, 0.16f, 506);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateYoYo() {
            float p = P(7.5f);
            float env = Env(p);
            float midY = FloorY + 0.25f;

            float yoX;
            if (p < 0.32f) yoX = -1.2f;
            else if (p < 0.64f) yoX = 0.75f;
            else yoX = -1.2f;
            FigPut("yo", yoX, 0f, false, 0f);
            FigCol("yo", DemoRed, env);

            FigPut("vic", 0.9f, 0f, true, 0f);
            float kill = Seg(p, 0.34f, 0.42f);
            FigCol("vic", DemoBlue, env);
            FigDead("vic", kill);

            Put("markDot", 0.75f, FloorY + 0.02f);
            ColA("markDot", Accent, env * Ease(Seg(p, 0.06f, 0.16f)) * (1f - Ease(Seg(p, 0.34f, 0.42f))));

            BtnPop("blinkBtn", -1.2f, BtnY, Seg(p, 0.20f, 0.30f));

            Burst("fx", 0.75f, midY, Seg(p, 0.34f, 0.46f), 0.65f, DemoRed);

            Put("siloA", -1.2f, FloorY + 0.05f);
            ColA("siloA", DemoRed, env * 0.35f * Ease(Seg(p, 0.32f, 0.40f)) * (1f - Ease(Seg(p, 0.50f, 0.64f))));
            Put("siloB", 0.75f, FloorY + 0.05f);
            ColA("siloB", DemoRed, env * 0.35f * Ease(Seg(p, 0.64f, 0.72f)) * (1f - Ease(Seg(p, 0.82f, 0.96f))));
        }

        // ================================================================================
        // Nice Guesser - correctly names a suspect's exact role during a meeting and shoots
        // them; the SAME loop then shows a wrong guess killing the Guesser instead.
        // ================================================================================
        private static void CreateNiceGuesser() {
            Crew("gg", DemoGreen);
            Crew("sus", DemoRed);
            Crew("inn", DemoBlue);
            MakeBtn("guessBtn", null);
            StageCap("q", "GUESS?", 0.75f, Accent);
            StageRect("table", new Color(0.34f, 0.25f, 0.16f, 0.5f), 0.5f, 0.12f, 504);
            StageSprite("fx1", UCFx.Ring, DemoGreen, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateNiceGuesser() {
            float p = P(8f);
            float env = Env(p);
            float midY = FloorY + 0.25f;

            bool ggFaceLeft = p < 0.45f;
            FigPut("gg", 0f, 0f, ggFaceLeft, 0f);
            float ggKilled = Seg(p, 0.72f, 0.80f);
            FigCol("gg", DemoGreen, env);
            FigDead("gg", ggKilled);

            FigPut("sus", -0.7f, 0f, false, 0f);
            float susKilled = Seg(p, 0.26f, 0.34f);
            FigCol("sus", DemoRed, env);
            FigDead("sus", susKilled);

            FigPut("inn", 0.7f, 0f, true, 0f);
            FigCol("inn", DemoBlue, env);

            Put("table", 0f, FloorY + 0.05f);

            float qAlpha = Ease(Seg(p, 0.06f, 0.16f)) * (1f - Ease(Seg(p, 0.24f, 0.30f)))
                         + Ease(Seg(p, 0.52f, 0.62f)) * (1f - Ease(Seg(p, 0.70f, 0.76f)));
            PutCap("q", 0f, 0.34f);
            CapA("q", env * Mathf.Clamp01(qAlpha));

            float btnProg = p < 0.4f ? Seg(p, 0.14f, 0.26f) : Seg(p, 0.60f, 0.72f);
            BtnPop("guessBtn", 0f, BtnY, btnProg);

            Burst("fx1", -0.7f, midY, Seg(p, 0.26f, 0.38f), 0.7f, DemoGreen);
            Burst("fx2", 0f, midY, Seg(p, 0.72f, 0.84f), 0.7f, DemoRed);
        }
    }
}
