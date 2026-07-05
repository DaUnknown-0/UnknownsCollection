// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHelpDemos6 - bespoke help-menu demo vignettes (see UCHelpMenu.RegisterDemo) for:
 * Spy, Security Guard, Medium, Trapper, Bait, Lover, Bloody, Anti tp.
 *
 * Every Create/Animate pair is registered under the role's EXACT display name and driven
 * entirely by UCHelpMenu.stageT each frame - no state is kept between frames (see UCHelpMenu's
 * demo-stage doc comment for the shared stage API).
 */

using System;
using UnityEngine;
using static UnknownsCollection.UCHelpMenu;

namespace UnknownsCollection {
    public static class UCHelpDemos6 {
        public static void Register() {
            RegisterDemo("Spy", CreateSpy, AnimateSpy);
            RegisterDemo("Security Guard", CreateSecurityGuard, AnimateSecurityGuard);
            RegisterDemo("Medium", CreateMedium, AnimateMedium);
            RegisterDemo("Trapper", CreateTrapper, AnimateTrapper);
            RegisterDemo("Bait", CreateBait, AnimateBait);
            RegisterDemo("Lover", CreateLover, AnimateLover);
            RegisterDemo("Bloody", CreateBloody, AnimateBloody);
            RegisterDemo("Anti tp", CreateAntiTp, AnimateAntiTp);
        }

        // ---- per-role palette (independent of player colors, kept next to their Create) ----
        private static readonly Color SecColor = new Color(0.4f, 0.8f, 0.95f);
        private static readonly Color MediumColor = new Color(0.78f, 0.7f, 0.95f);
        private static readonly Color SoulColor = new Color(0.65f, 0.85f, 0.95f);
        private static readonly Color TrapColor = new Color(0.55f, 0.78f, 0.4f);
        private static readonly Color LoverLink = new Color(1f, 0.4f, 0.75f);
        private static readonly Color BloodyColor = new Color(0.72f, 0.05f, 0.08f);
        private static readonly Color AntiTpColor = new Color(0.4f, 0.9f, 0.85f);

        // ====================================================================
        // Spy - a Crewmate with no ability who looks like a third Impostor to the real
        // Impostors, who genuinely cannot tell them apart. An Impostor checks two identical
        // red-looking figures, hesitates (can't be sure which is the Spy), and backs off -
        // the demo briefly reveals the Spy's true blue color to the VIEWER only.
        // ====================================================================
        private static void CreateSpy() {
            Crew("mate", DemoRed);
            Crew("spy", DemoRed);
            Crew("imp", DemoRed);
            MakeBtn("killBtn", KillButtonSprite(null));
            StageCap("q", "?", 1.3f, Accent);
            StageCap("tag", "SPY", 0.85f, DemoBlue);
        }

        private static void AnimateSpy() {
            float p = P(7.5f);
            float fade = Ease(Seg(p, 0.02f, 0.1f)) * (1f - Ease(Seg(p, 0.94f, 1f)));

            float approach = Seg(p, 0.04f, 0.3f);
            float retreat = Seg(p, 0.66f, 0.92f);
            float ix = p < 0.5f ? Move(-1.55f, -0.05f, approach) : Move(-0.05f, -1.55f, retreat);
            FigPut("imp", ix, 0f, p >= 0.5f, (Mid(approach) || Mid(retreat)) ? 1f : 0f);
            FigCol("imp", DemoRed, fade);

            FigPut("mate", -0.55f, 0f, false, 0f);
            FigCol("mate", DemoRed, fade);

            FigPut("spy", 0.55f, 0f, true, 0f);
            float reveal = Ease(Seg(p, 0.64f, 0.76f)) * (1f - Ease(Seg(p, 0.86f, 0.94f)));
            FigCol("spy", Color.Lerp(DemoRed, DemoBlue, reveal), fade);

            float q = Seg(p, 0.34f, 0.44f) * (1f - Seg(p, 0.5f, 0.6f));
            PutCap("q", ix, 0.24f);
            CapA("q", q * fade);

            PutCap("tag", 0.55f, 0.26f);
            CapA("tag", reveal * fade);

            BtnPop("killBtn", ix, BtnY, Seg(p, 0.36f, 0.58f));
        }

        // ====================================================================
        // Security Guard - spends screws to seal a vent (blocking it for good) or place
        // cameras, then watches remotely once out of screws. Vignette: seal a vent with a
        // screw, an Impostor bounces off it trying to vent, then the Guard switches to a
        // remote camera feed from the same spot.
        // ====================================================================
        private static void CreateSecurityGuard() {
            Crew("guard", SecColor);
            Crew("imp", DemoRed);
            StageRect("vent", DemoDark, 0.34f, 0.46f, 505);
            StageDot("screw", new Color(1f, 0.85f, 0.4f), 0.07f);
            StageCap("lock", "LOCKED", 0.55f, Accent);
            StageCap("blockX", "X", 1.1f, DemoRed);
            StageRect("screen", new Color(0.12f, 0.5f, 0.62f), 0.4f, 0.28f, 505);
            StageCap("rec", "REC", 0.6f, DemoRed);
            MakeBtn("sealBtn", null);
            MakeBtn("camBtn", null);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateSecurityGuard() {
            float p = P(8f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.96f, 1f)));
            float figMidY = FloorY + 0.25f;
            const float ventX = -0.55f;

            float approach = Seg(p, 0.03f, 0.22f);
            float gx = Move(-1.55f, ventX + 0.35f, approach);
            FigPut("guard", gx, 0f, false, Mid(approach) ? 1f : 0f);
            FigCol("guard", SecColor, fade);

            Put("vent", ventX, FloorY + 0.23f);
            float sealing = Seg(p, 0.26f, 0.34f);
            ColA("vent", Color.Lerp(DemoDark, new Color(0.55f, 0.45f, 0.2f), Ease(sealing)), fade);

            BtnPop("sealBtn", gx, BtnY, Seg(p, 0.22f, 0.36f));

            float fly = Seg(p, 0.24f, 0.32f);
            Put("screw", Mathf.Lerp(gx, ventX, Ease(fly)), FloorY + 0.24f + 0.05f * Mathf.Sin(fly * Mathf.PI));
            ColA("screw", new Color(1f, 0.85f, 0.4f), Mid(fly) ? fade : 0f);

            PutCap("lock", ventX, 0.28f);
            CapA("lock", Ease(Seg(p, 0.32f, 0.4f)) * (1f - Ease(Seg(p, 0.88f, 0.96f))) * fade);

            float impApproach = Seg(p, 0.4f, 0.6f);
            float impBounce = Seg(p, 0.62f, 0.74f);
            float ix = p < 0.62f ? Move(1.55f, ventX + 0.16f, impApproach) : Move(ventX + 0.16f, 0.5f, impBounce);
            FigPut("imp", ix, 0f, p < 0.62f, (Mid(impApproach) || Mid(impBounce)) ? 1f : 0f);
            FigCol("imp", DemoRed, fade);

            Burst("fx", ventX + 0.16f, figMidY, Seg(p, 0.6f, 0.7f), 0.5f, DemoRed);
            PutCap("blockX", ventX + 0.16f, 0.24f);
            CapA("blockX", Seg(p, 0.6f, 0.66f) * (1f - Seg(p, 0.74f, 0.82f)) * fade);

            BtnPop("camBtn", gx, BtnY, Seg(p, 0.66f, 0.8f));
            Put("screen", gx, 0.3f);
            float camOn = Seg(p, 0.7f, 0.78f) * (1f - Seg(p, 0.9f, 0.97f));
            ColA("screen", new Color(0.12f, 0.5f, 0.62f), camOn * fade);
            PutCap("rec", gx, 0.3f);
            CapA("rec", camOn * fade);
        }

        // ====================================================================
        // Medium - questions the fading soul of last round's victim for a random hint. A
        // kill happens, the body rises as a translucent soul, the Medium walks up and asks
        // "?", gets a CLUE hint, and the soul fades away for good (one round only).
        // ====================================================================
        private static void CreateMedium() {
            Crew("med", MediumColor);
            Crew("killer", DemoRed);
            Crew("vic", DemoWhite);
            Crew("soul", SoulColor);
            StageCap("q", "?", 1.2f, Accent);
            StageCap("hint", "CLUE", 0.75f, MediumColor);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateMedium() {
            float p = P(8f);
            float fade = Ease(Seg(p, 0.02f, 0.1f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            float figMidY = FloorY + 0.25f;

            float kIn = Seg(p, 0.02f, 0.14f), kOut = Seg(p, 0.24f, 0.4f);
            float kx = p < 0.24f ? Move(1.5f, 0.4f, kIn) : Move(0.4f, 1.55f, kOut);
            FigPut("killer", kx, 0f, p < 0.24f, (Mid(kIn) || Mid(kOut)) ? 1f : 0f);
            FigCol("killer", DemoRed, fade);

            float killed = Seg(p, 0.16f, 0.24f);
            FigPut("vic", 0.3f, 0f, true, 0f);
            FigCol("vic", DemoWhite, fade);
            FigDead("vic", killed);
            Burst("fx", 0.3f, figMidY, Seg(p, 0.15f, 0.27f), 0.6f, DemoRed);

            float rise = Seg(p, 0.24f, 0.36f);
            float vanish = Seg(p, 0.8f, 0.92f);
            FigPut("soul", 0.3f, 0.16f * rise + 0.02f * Mathf.Sin(stageT * 2.2f), false, 0f);
            FigCol("soul", SoulColor, 0.65f * rise * (1f - vanish) * fade, 0f);

            float mIn = Seg(p, 0.3f, 0.5f);
            float mx = Move(-1.5f, -0.15f, mIn);
            FigPut("med", mx, 0f, false, Mid(mIn) ? 1f : 0f);
            FigCol("med", MediumColor, fade);

            float q = Seg(p, 0.52f, 0.6f) * (1f - Seg(p, 0.66f, 0.74f));
            PutCap("q", mx, 0.26f);
            CapA("q", q * fade);

            float hint = Seg(p, 0.62f, 0.72f) * (1f - Seg(p, 0.84f, 0.92f));
            PutCap("hint", 0.3f, 0.32f);
            CapA("hint", hint * fade);
        }

        // ====================================================================
        // Trapper - hides traps that stun whoever steps in them and eventually reveal that
        // player's identity. An Impostor wanders onto a hidden trap, gets stunned and
        // revealed; the Trapper then strolls straight back over their own trap unharmed.
        // ====================================================================
        private static void CreateTrapper() {
            Crew("trap", TrapColor);
            Crew("imp", DemoRed);
            StageDot("mine", TrapColor, 0.09f);
            StageCap("bang", "!", 1.2f, Accent);
            StageCap("tag", "IMPOSTOR", 0.5f, DemoRed);
            StageSprite("fx", UCFx.Ring, DemoWhite, 0.1f, 508);
        }

        private static void AnimateTrapper() {
            float p = P(8.5f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.96f, 1f)));
            float figMidY = FloorY + 0.25f;
            const float mineX = -0.2f;

            float placeIn = Seg(p, 0.03f, 0.2f);
            float placeOut = Seg(p, 0.26f, 0.34f);
            float placeBack = Seg(p, 0.8f, 0.94f);
            float tx = p < 0.26f ? Move(-1.5f, mineX, placeIn)
                     : p < 0.8f ? Move(mineX, -1.55f, placeOut)
                     : Move(-1.55f, mineX, placeBack);
            bool trapFaceLeft = p >= 0.2f && p < 0.8f;
            bool trapWalk = Mid(placeIn) || Mid(placeOut) || Mid(placeBack);
            FigPut("trap", tx, 0f, trapFaceLeft, trapWalk ? 1f : 0f);
            FigCol("trap", TrapColor, fade);

            Put("mine", mineX, FloorY + 0.03f);
            float hidden = Seg(p, 0.2f, 0.26f);
            float mineVisible = Mathf.Lerp(0.75f, 0.15f, Ease(hidden));
            float flash = Seg(p, 0.57f, 0.6f) * (1f - Seg(p, 0.63f, 0.68f));
            ColA("mine", Color.Lerp(TrapColor, Color.white, flash), fade * Mathf.Max(mineVisible, flash));

            float impIn = Seg(p, 0.34f, 0.58f);
            float ix = Move(1.5f, mineX, impIn);
            FigPut("imp", ix, 0f, true, Mid(impIn) ? 1f : 0f);
            FigCol("imp", DemoRed, fade);

            float stun = Seg(p, 0.58f, 0.64f);
            Burst("fx", mineX, figMidY, Seg(p, 0.57f, 0.7f), 0.6f, DemoWhite);
            PutCap("bang", mineX, 0.26f);
            CapA("bang", Ease(stun) * (1f - Seg(p, 0.68f, 0.78f)) * fade);

            float reveal = Seg(p, 0.66f, 0.78f) * (1f - Seg(p, 0.9f, 0.98f));
            PutCap("tag", mineX, 0.32f);
            CapA("tag", reveal * fade);
        }

        // ====================================================================
        // Bait - forces the killer to auto-report the body. A kill happens, the Impostor
        // tries to flee, gets snapped straight back to the body and is left with no choice
        // but to REPORT it themselves.
        // ====================================================================
        private static void CreateBait() {
            Crew("vic", DemoOrange);
            Crew("imp", DemoRed);
            StageCap("tag", "BAIT", 0.7f, DemoOrange);
            StageCap("rep", "REPORT", 0.55f, Accent);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateBait() {
            float p = P(7f);
            float fade = Ease(Seg(p, 0.02f, 0.1f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            float figMidY = FloorY + 0.25f;

            FigPut("vic", 0.2f, 0f, true, 0f);
            float killed = Seg(p, 0.24f, 0.32f);
            FigCol("vic", Color.Lerp(DemoOrange, new Color(0.5f, 0.4f, 0.3f), killed), fade);
            FigDead("vic", killed);
            PutCap("tag", 0.2f, 0.26f);
            CapA("tag", (1f - killed) * fade);

            float approach = Seg(p, 0.04f, 0.24f);
            float flee = Seg(p, 0.34f, 0.46f);
            float snapBack = Seg(p, 0.5f, 0.62f);
            float ix; bool faceLeft;
            if (p < 0.34f) { ix = Move(-1.5f, 0.5f, approach); faceLeft = false; }
            else if (p < 0.5f) { ix = Move(0.5f, -0.6f, flee); faceLeft = true; }
            else { ix = Move(-0.6f, 0.5f, snapBack); faceLeft = false; }
            bool impWalk = Mid(approach) || Mid(flee) || Mid(snapBack);
            FigPut("imp", ix, 0f, faceLeft, impWalk ? 1f : 0f);
            FigCol("imp", DemoRed, fade);

            Burst("fx", 0.2f, figMidY, Seg(p, 0.23f, 0.35f), 0.6f, DemoRed);

            float report = Seg(p, 0.64f, 0.74f) * (1f - Seg(p, 0.86f, 0.94f));
            PutCap("rep", ix, 0.26f);
            CapA("rep", report * fade);
        }

        // ====================================================================
        // Lover - two secretly linked players win together if both survive; lose one and the
        // other may auto-suicide. One Lover is killed by an Impostor; moments later, untouched,
        // the other Lover dies too, and the bond between them fades out.
        // ====================================================================
        private static void CreateLover() {
            Crew("a", DemoBlue);
            Crew("b", DemoGreen);
            Crew("imp", DemoRed);
            StageRect("link", LoverLink, 1.1f, 0.03f, 505);
            StageCap("tag", "LOVERS", 0.55f, LoverLink);
            StageSprite("fx1", UCFx.Ring, DemoRed, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, LoverLink, 0.1f, 508);
        }

        private static void AnimateLover() {
            float p = P(7.5f);
            float fade = Ease(Seg(p, 0.02f, 0.1f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            float figMidY = FloorY + 0.25f;

            FigPut("a", -0.55f, 0f, false, 0f);
            FigPut("b", 0.55f, 0f, true, 0f);

            float aKilled = Seg(p, 0.3f, 0.38f);
            float bDies = Seg(p, 0.5f, 0.58f);
            FigCol("a", Color.Lerp(DemoBlue, new Color(0.4f, 0.4f, 0.45f), aKilled), fade);
            FigCol("b", Color.Lerp(DemoGreen, new Color(0.4f, 0.4f, 0.45f), bDies), fade);
            FigDead("a", aKilled);
            FigDead("b", bDies);

            float impIn = Seg(p, 0.06f, 0.28f);
            float impOut = Seg(p, 0.4f, 0.6f);
            float ix = p < 0.4f ? Move(-1.55f, -0.85f, impIn) : Move(-0.85f, -1.6f, impOut);
            FigPut("imp", ix, 0f, p >= 0.4f, (Mid(impIn) || Mid(impOut)) ? 1f : 0f);
            FigCol("imp", DemoRed, fade);

            Burst("fx1", -0.55f, figMidY, Seg(p, 0.29f, 0.4f), 0.6f, DemoRed);
            Burst("fx2", 0.55f, figMidY, Seg(p, 0.49f, 0.62f), 0.6f, LoverLink);

            float linkStrength = 1f - Ease(Seg(p, 0.3f, 0.5f));
            Put("link", 0f, figMidY + 0.02f);
            ColA("link", LoverLink, 0.55f * linkStrength * fade * (0.7f + 0.3f * Mathf.Sin(stageT * 3f)));

            PutCap("tag", 0f, 0.3f);
            CapA("tag", (1f - Ease(Seg(p, 0.28f, 0.4f))) * fade);
        }

        // ====================================================================
        // Bloody - if killed, the killer leaves a trail matching the victim's color behind
        // them for a while. A kill happens, and as the Impostor flees, colored footprints
        // bloom in their wake and fade out again after their set duration.
        // ====================================================================
        private static void CreateBloody() {
            Crew("vic", BloodyColor);
            Crew("imp", DemoRed);
            StageCap("tag", "BLOODY", 0.55f, BloodyColor);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
            for (int i = 0; i < 6; i++) StageDot("t" + i, BloodyColor, 0.06f);
        }

        private static void AnimateBloody() {
            float p = P(7.5f);
            float fade = Ease(Seg(p, 0.02f, 0.1f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            float figMidY = FloorY + 0.25f;

            FigPut("vic", 0.1f, 0f, true, 0f);
            float killed = Seg(p, 0.22f, 0.3f);
            FigCol("vic", Color.Lerp(BloodyColor, new Color(0.4f, 0.4f, 0.45f), killed), fade);
            FigDead("vic", killed);
            PutCap("tag", 0.1f, 0.26f);
            CapA("tag", (1f - killed) * fade);

            float approach = Seg(p, 0.02f, 0.22f);
            float leave = Seg(p, 0.34f, 0.72f);
            float ix = p < 0.34f ? Move(-1.5f, 0.1f, approach) : Move(0.1f, 1.55f, leave);
            FigPut("imp", ix, 0f, p >= 0.34f, (Mid(approach) || Mid(leave)) ? 1f : 0f);
            FigCol("imp", DemoRed, fade);

            Burst("fx", 0.1f, figMidY, Seg(p, 0.21f, 0.33f), 0.6f, DemoRed);

            float trailIn = Seg(p, 0.34f, 0.4f);
            float trailFade = 1f - Ease(Seg(p, 0.78f, 0.92f));
            for (int i = 0; i < 6; i++) {
                float xi = Mathf.Lerp(0.1f, 1.55f, (i + 0.5f) / 6f);
                Put("t" + i, xi, FloorY + 0.015f);
                float dropped = ix >= xi ? 1f : 0f;
                ColA("t" + i, BloodyColor, dropped * 0.6f * fade * trailFade * trailIn);
            }
        }

        // ====================================================================
        // Anti tp - not teleported to the meeting table on a report/emergency; stays right
        // where they were instead. A meeting is called: the normal crewmate teleports to the
        // table and back, while the Anti tp crewmate simply never moves.
        // ====================================================================
        private static void CreateAntiTp() {
            Crew("norm", DemoBlue);
            Crew("anti", AntiTpColor);
            StageRect("table", new Color(0.5f, 0.4f, 0.3f), 0.4f, 0.16f, 505);
            StageCap("meet", "MEETING", 0.6f, Accent);
            StageCap("tag", "ANTI-TP", 0.55f, AntiTpColor);
            StageCap("stay", "STAYS", 0.55f, AntiTpColor);
            StageSprite("fx1", UCFx.Streak, DemoBlue, 0.32f, 508);
            StageSprite("fx2", UCFx.Ring, AntiTpColor, 0.12f, 508);
        }

        private static void AnimateAntiTp() {
            float p = P(7f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.96f, 1f)));

            float callFlash = Seg(p, 0.14f, 0.2f) * (1f - Seg(p, 0.26f, 0.34f));
            PutCap("meet", 0f, 0.32f);
            CapA("meet", callFlash * fade);

            FigPut("anti", 0.65f, 0f, false, 0f);
            FigCol("anti", AntiTpColor, fade);
            PutCap("tag", 0.65f, 0.24f);
            CapA("tag", (1f - Seg(p, 0.2f, 0.3f)) * fade * 0.9f);

            float resist = Seg(p, 0.22f, 0.3f) * (1f - Seg(p, 0.6f, 0.7f));
            PutCap("stay", 0.65f, 0.3f);
            CapA("stay", resist * fade);
            Burst("fx2", 0.65f, FloorY + 0.25f, Seg(p, 0.2f, 0.34f), 0.4f, AntiTpColor);

            Put("table", 0f, FloorY + 0.22f);
            ColA("table", new Color(0.5f, 0.4f, 0.3f), fade * 0.9f);

            float teleOut = Seg(p, 0.2f, 0.28f);
            float teleIn = Seg(p, 0.3f, 0.38f);
            float backOut = Seg(p, 0.62f, 0.7f);
            float backIn = Seg(p, 0.72f, 0.8f);
            float normX, normA;
            if (p < 0.3f) { normX = -0.9f; normA = 1f - Ease(teleOut); }
            else if (p < 0.62f) { normX = 0f; normA = Ease(teleIn); }
            else if (p < 0.72f) { normX = 0f; normA = 1f - Ease(backOut); }
            else { normX = -0.9f; normA = Ease(backIn); }
            FigPut("norm", normX, 0f, false, 0f);
            FigCol("norm", DemoBlue, normA * fade);

            Put("fx1", normX, FloorY + 0.25f);
            bool streak = (p > 0.19f && p < 0.32f) || (p > 0.6f && p < 0.74f);
            ColA("fx1", DemoBlue, streak ? 0.7f : 0f);
        }
    }
}
