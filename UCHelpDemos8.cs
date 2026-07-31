// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHelpDemos8 - bespoke help-menu demo vignettes (see UCHelpMenu's ExtraDemos registry) for the
 * three Paket-W roles: Werewolf (Impostor), Hunter (Crew) and Pelican (Neutral).
 *
 * Same contract as the other demo packs: a Create/Animate pair per role, both STATELESS - Animate
 * recomputes every position/colour/alpha purely from the loop phase, so the vignettes can never
 * drift and a rebuild needs no bookkeeping. Built exclusively from the shared UCHelpMenu stage API
 * (Crew/StagePic/StageRect/StageSprite/StageCap/MakeBtn factories + the per-frame
 * Put/ColA/Size2/FigPut/FigCol/FigDead/BtnPop/Burst helpers).
 *
 * Captions are ASCII-only on purpose: the stage clones the HUD kill-timer TMP, whose atlas has no
 * glyphs for the usual UI symbols.
 */

using UnityEngine;
using static UnknownsCollection.UCHelpMenu;

namespace UnknownsCollection {
    public static class UCHelpDemos8 {
        public static void Register() {
            RegisterDemo("Werewolf", CreateWerewolf, AnimateWerewolf);
            RegisterDemo("Hunter", CreateHunter, AnimateHunter);
            RegisterDemo("Pelican", CreatePelican, AnimatePelican);
        }

        // Shared tone for the two wolf scenes: near-black fur with a burning eye.
        private static readonly Color WolfFur = new Color(0.16f, 0.14f, 0.18f);
        private static readonly Color WolfEye = new Color(1f, 0.55f, 0.15f);
        private static readonly Color Silver = new Color(0.82f, 0.86f, 0.94f);

        // ================================================================
        // Werewolf: the lights go out, the alpha charge fills, and the last Impostor turns into
        // the beast - unfixable darkness, everyone reduced to a flashlight, a mauled victim left
        // under a blood ring. Then the form runs out and the man walks away.
        // ================================================================
        private static void CreateWerewolf() {
            StageRect("dark", Color.black, stageSize.x - 0.06f, stageSize.y - 0.06f, 504);
            StageSprite("torch", UCFx.Dot, new Color(0.6f, 0.62f, 0.7f), 1f, 505);
            Crew("man", DemoRed);                 // human form
            Crew("wolf", WolfFur, 0.26f);         // the beast: bigger and darker
            Crew("vic", DemoBlue);
            StageDot("eye", WolfEye, 0.05f);
            StagePic("ring", UCAssets.WerewolfBloodRing, 0.16f, 505);
            StagePic("maw", UCAssets.OverlayWolfHead, 0.36f, 509);
            MakeBtn("wolfBtn", UCAssets.WerewolfTransformIcon);
            StageRect("chargeBg", new Color(1f, 1f, 1f, 0.12f), 0.85f, 0.06f);
            StageRect("charge", DemoRed, 0.85f, 0.045f);
            StageCap("outCap", "LIGHTS OUT", 0.5f, Accent);
            StageSprite("fx", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimateWerewolf() {
            float p = P(8f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.95f, 1f)));

            // Lights out for the whole loop except the very last beat.
            float darkK = Ease(Seg(p, 0.04f, 0.12f)) * (1f - Ease(Seg(p, 0.9f, 0.97f)));
            Put("dark", 0f, 0f);
            ColA("dark", Color.black, 0.62f * darkK * fade);
            PutCap("outCap", 0f, stageSize.y / 2f - 0.12f);
            CapA("outCap", Seg(p, 0.06f, 0.14f) * (1f - Seg(p, 0.24f, 0.32f)) * fade);

            float wolfOn = Ease(Seg(p, 0.42f, 0.5f)) * (1f - Ease(Seg(p, 0.82f, 0.9f)));
            float figY = FloorY + 0.25f;

            // The victim only keeps a small torch of light once the beast is out - the blanket
            // flashlight radius everybody but the wolf is reduced to.
            float vicX = 0.85f;
            float torch = Mathf.Lerp(1.15f, 0.42f, wolfOn);
            Put("torch", vicX, figY);
            Size2("torch", torch, torch);
            ColA("torch", new Color(0.6f, 0.62f, 0.7f), 0.16f * darkK * fade);

            // Charge: only counts down while it is dark, and it is spent by transforming.
            float charge = wolfOn > 0.01f ? 0f : 0.85f * Ease(Seg(p, 0.14f, 0.4f));
            BarLeft("chargeBg", -1.62f, 0.31f, 0.85f, 0.06f);
            BarLeft("charge", -1.62f, 0.31f, Mathf.Max(charge, 0.001f), 0.045f);
            ColA("chargeBg", Color.white, 0.12f * fade);
            ColA("charge", Color.Lerp(DemoRed, Accent, Seg(p, 0.3f, 0.4f)), (wolfOn > 0.01f ? 0f : 0.9f) * fade);

            // Human form walks in, transforms on the spot, the beast charges the victim, reverts.
            float walkIn = Seg(p, 0.06f, 0.3f);
            float hunt = Seg(p, 0.5f, 0.62f);
            float leave = Seg(p, 0.88f, 0.99f);
            float manX = p < 0.42f ? Move(-1.5f, -0.75f, walkIn) : Move(-0.2f, -1.55f, leave);
            float beastX = Move(-0.75f, vicX - 0.42f, hunt);

            FigPut("man", manX, 0f, p >= 0.88f, (Mid(walkIn) || Mid(leave)) ? 1f : 0f);
            FigCol("man", DemoRed, (1f - wolfOn) * fade);
            FigPut("wolf", beastX, 0f, false, Mid(hunt) ? 1f : 0f);
            FigCol("wolf", WolfFur, wolfOn * fade);
            Put("eye", beastX + 0.09f, figY + 0.11f);
            ColA("eye", WolfEye, wolfOn * fade * (0.7f + 0.3f * Mathf.Sin(stageT * 6f)));

            BtnPop("wolfBtn", -0.75f, BtnY, Seg(p, 0.3f, 0.46f));
            Burst("fx", -0.75f, figY, Seg(p, 0.4f, 0.54f), 0.8f, DemoRed);

            // The maw snaps shut on the victim, who goes down under a public blood ring.
            float bite = Seg(p, 0.62f, 0.7f);
            Put("maw", vicX - 0.22f, figY + 0.06f);
            PicScale("maw", Mathf.Lerp(0.42f, 0.28f, Ease(bite)));
            ColA("maw", Color.Lerp(WolfFur, Color.white, 0.35f), Mid(bite) ? fade : 0f);

            float killed = Seg(p, 0.66f, 0.74f);
            FigPut("vic", vicX, 0f, true, 0f);
            FigCol("vic", Color.Lerp(DemoBlue, new Color(0.42f, 0.42f, 0.5f), killed), fade);
            FigDead("vic", killed);

            float ringUp = Seg(p, 0.68f, 0.8f);
            Put("ring", vicX, FloorY + 0.015f);
            PicScale("ring", 0.1f + 0.08f * Ease(ringUp));
            ColA("ring", Color.white, 0.85f * Ease(ringUp) * fade);
        }

        // ================================================================
        // Hunter: the last Impostor IS the beast and the original Sheriff is still alive - so the
        // Sheriff becomes The Hunter. Silver bolt, stronger flashlight, and the wolf goes down.
        // ================================================================
        // Geometry of the "Monster Hunter" hat sprite (tmp/hunterhut.html, 300x375 px): the crewmate
        // it is calibrated against sits between y146 (head) and y340 (feet), counted from the TOP.
        // The demo derives its hat size from the figure instead of hardcoding a height, so the two
        // stay aligned even if the demo crewmate is ever rescaled.
        private const float HatSpriteH = 375f, HatBeanTop = 146f, HatBeanBottom = 340f;
        private static float hunterHatH;      // world height of the whole hat sprite
        private static float hunterHatMidY;   // its centre above the floor line, feet on the ground

        private static void CreateHunter() {
            StageRect("dark", Color.black, stageSize.x - 0.06f, stageSize.y - 0.06f, 504);
            Crew("sher", DemoOrange);
            // Since 2026-07-31 the Hunter is a HAT over the ordinary crewmate, not a full-figure skin -
            // so the demo keeps the sheriff's own bean visible and only puts the costume on top.
            float bodyH = (UCAssets.OverlayCrewBody != null ? UCAssets.OverlayCrewBody.bounds.size.y : 2.56f) * 0.19f;
            hunterHatH = bodyH * HatSpriteH / (HatBeanBottom - HatBeanTop);
            hunterHatMidY = hunterHatH * (HatSpriteH / 2f - (HatSpriteH - HatBeanBottom)) / HatSpriteH;
            StagePic("hunter", UCAssets.HunterHatSprite, hunterHatH, 508);
            Crew("wolf", WolfFur, 0.26f);
            StageDot("eye", WolfEye, 0.05f);
            StageSprite("torch", UCFx.Dot, Silver, 1f, 505);
            StagePic("bolt", UCAssets.OverlaySilverBolt, 0.2f, 509);
            MakeBtn("shootBtn", UCAssets.HunterShootIcon);
            StageCap("huntCap", "THE HUNT IS ON", 0.45f, Silver);
            StageSprite("fx1", UCFx.Ring, Silver, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, Silver, 0.1f, 508);
        }

        private static void AnimateHunter() {
            float p = P(8f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.95f, 1f)));
            float figY = FloorY + 0.25f;

            Put("dark", 0f, 0f);
            ColA("dark", Color.black, 0.5f * fade);

            float promote = Ease(Seg(p, 0.2f, 0.3f));
            float sherX = -0.95f;

            FigPut("sher", sherX, 0f, false, 0f);
            // The sheriff STAYS visible under the costume - that is the whole point of the hat: his
            // own colour keeps telling the crew who is under there.
            FigCol("sher", DemoOrange, fade);
            Put("hunter", sherX, FloorY + hunterHatMidY);
            ColA("hunter", Color.white, promote * fade);

            // His own, brighter light: the carve-out from the wolf's blanket darkness.
            float torch = Mathf.Lerp(0.5f, 1.15f, promote);
            Put("torch", sherX, figY);
            Size2("torch", torch, torch);
            ColA("torch", Silver, 0.14f * fade);

            Burst("fx1", sherX, figY, Seg(p, 0.18f, 0.34f), 0.85f, Silver);
            PutCap("huntCap", 0f, stageSize.y / 2f - 0.12f);
            CapA("huntCap", Seg(p, 0.28f, 0.36f) * (1f - Seg(p, 0.5f, 0.6f)) * fade);

            float wolfX = 0.95f;
            float dead = Seg(p, 0.66f, 0.76f);
            FigPut("wolf", wolfX, 0f, true, 0f);
            FigCol("wolf", WolfFur, fade);
            FigDead("wolf", dead);
            Put("eye", wolfX - 0.09f, figY + 0.11f);
            ColA("eye", WolfEye, fade * (1f - Ease(dead)) * (0.7f + 0.3f * Mathf.Sin(stageT * 6f)));

            BtnPop("shootBtn", sherX, BtnY, Seg(p, 0.4f, 0.56f));

            // The bolt: fired at ~0.56, buried in the beast at ~0.66.
            float shot = Seg(p, 0.56f, 0.66f);
            Put("bolt", Mathf.Lerp(sherX + 0.3f, wolfX - 0.12f, Ease(shot)), figY + 0.06f);
            ColA("bolt", Silver, Mid(shot) ? fade : 0f);
            Burst("fx2", wolfX, figY, Seg(p, 0.64f, 0.8f), 0.9f, Silver);
        }

        // ================================================================
        // Pelican: his kill leaves NO body - the victims sit in his belly until a meeting digests
        // them. Kill the Pelican first and every corpse he carries drops on top of him at once.
        // ================================================================
        private static void CreatePelican() {
            Crew("pel", DemoCyan);
            Crew("v1", DemoBlue);
            Crew("v2", DemoGreen);
            Crew("imp", DemoRed);
            StagePic("beak", UCAssets.OverlayPelican, 0.34f, 509);
            MakeBtn("swBtn", UCAssets.PelicanSwallowIcon);
            StageCap("belly", "BELLY 0", 0.5f, DemoCyan);
            StageCap("noBody", "NO BODY", 0.45f, Accent);
            StageSprite("fx1", UCFx.Ring, DemoCyan, 0.1f, 508);
            StageSprite("fx2", UCFx.Ring, DemoRed, 0.1f, 508);
        }

        private static void AnimatePelican() {
            float p = P(9f);
            float fade = Ease(Seg(p, 0.02f, 0.08f)) * (1f - Ease(Seg(p, 0.96f, 1f)));
            float figY = FloorY + 0.25f;

            // Two swallows, then the Pelican himself is killed and gives everything back.
            float swallow1 = Seg(p, 0.16f, 0.24f);
            float swallow2 = Seg(p, 0.42f, 0.5f);
            float pelDead = Seg(p, 0.66f, 0.74f);
            float release = Seg(p, 0.74f, 0.86f);

            float walk1 = Seg(p, 0.02f, 0.14f);
            float walk2 = Seg(p, 0.28f, 0.4f);
            float pelX = p < 0.28f ? Move(-1.35f, -0.5f, walk1) : Move(-0.5f, 0.55f, walk2);
            FigPut("pel", pelX, 0f, false, (Mid(walk1) || Mid(walk2)) ? 1f : 0f);
            FigCol("pel", DemoCyan, fade);
            FigDead("pel", pelDead);

            // Victim 1 vanishes completely at the first swallow, victim 2 at the second - and both
            // come back, lying down, the moment the Pelican falls.
            float v1Alpha = (1f - Ease(swallow1)) + Ease(release);
            float v2Alpha = (1f - Ease(swallow2)) + Ease(release);
            float v1X = p < 0.74f ? -0.1f : Mathf.Lerp(-0.1f, pelX - 0.32f, Ease(release));
            float v2X = p < 0.74f ? 0.95f : Mathf.Lerp(0.95f, pelX + 0.34f, Ease(release));
            FigPut("v1", v1X, 0f, true, 0f);
            FigCol("v1", DemoBlue, Mathf.Clamp01(v1Alpha) * fade);
            FigDead("v1", Mathf.Max(Ease(swallow1) * 0.6f, Ease(release)));
            FigPut("v2", v2X, 0f, true, 0f);
            FigCol("v2", DemoGreen, Mathf.Clamp01(v2Alpha) * fade);
            FigDead("v2", Mathf.Max(Ease(swallow2) * 0.6f, Ease(release)));

            BtnPop("swBtn", pelX, BtnY, Mid(Seg(p, 0.1f, 0.26f)) ? Seg(p, 0.1f, 0.26f) : Seg(p, 0.36f, 0.52f));

            float biting = Mid(swallow1) ? swallow1 : (Mid(swallow2) ? swallow2 : 0f);
            float beakX = Mid(swallow1) ? v1X : v2X;
            Put("beak", beakX, figY + 0.05f);
            PicScale("beak", Mathf.Lerp(0.38f, 0.24f, Ease(biting)));
            ColA("beak", Color.white, biting > 0f ? fade : 0f);

            CapText("belly", p < 0.2f ? "BELLY 0" : p < 0.46f ? "BELLY 1" : p < 0.78f ? "BELLY 2" : "BELLY 0");
            PutCap("belly", -1.25f, 0.32f);
            CapA("belly", 0.8f * fade);

            float noBody = Seg(p, 0.24f, 0.32f) * (1f - Seg(p, 0.36f, 0.44f));
            PutCap("noBody", v1X, 0.24f);
            CapA("noBody", noBody * fade);

            // The counterplay: whoever kills the Pelican gets every piece of evidence back at once.
            float impIn = Seg(p, 0.52f, 0.66f);
            float impOut = Seg(p, 0.86f, 0.98f);
            float impX = p < 0.86f ? Move(1.55f, pelX + 0.4f, impIn) : Move(pelX + 0.4f, 1.6f, impOut);
            FigPut("imp", impX, 0f, p < 0.86f, (Mid(impIn) || Mid(impOut)) ? 1f : 0f);
            FigCol("imp", DemoRed, fade);

            Burst("fx1", pelX, figY, release, 0.95f, DemoCyan);
            Burst("fx2", pelX, figY, Seg(p, 0.64f, 0.78f), 0.7f, DemoRed);
        }
    }
}
