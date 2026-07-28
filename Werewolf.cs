// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Werewolf (Impostor) - Paket W, Stufe 1: the ALPHA MODE.
 *
 * A normal TOR Impostor is silently promoted to "The Werewolf" at game start (host-authoritative pick,
 * broadcast on the shared UC channel, module byte 211). For most of the round he is an ordinary
 * Impostor. His power is a single, conditional endgame move:
 *
 *   CHARGE   A countdown (option 1555) ticks down ONLY while the Lights sabotage is active. Fixing the
 *            lights pauses it (default) or resets it to full (option 1559). At zero the beast is ready.
 *   GATE     He may only transform while he is the LAST living Impostor (option 1513; the Spy can be
 *            counted as one, option 1514). The whole role is therefore a comeback mechanic, not an
 *            opener.
 *   WOLF     Transforming starts Y seconds (option 1556) of WOLF DARKNESS:
 *            - nobody can fix the lights (SwitchMinigame closes itself),
 *            - EVERY player is reduced to a real TORCH: a light radius (option 1515 - infinite, or
 *              2.0x down to 0.5x of the standard crew sight) AND the Lighter's directional flashlight
 *              cone - except the werewolf (full impostor vision, never less than the torch),
 *              the Lighter (keeps his own) and the dead,
 *            - the wolf is faster (1554) and kills faster (1553),
 *            - everyone SEES the beast: after a short pitch-black beat (the Camouflager look) the
 *              werewolf wears the full-body "Werewolf" custom hat as his entire look, at 1.5x
 *              player scale (WerewolfFx owns the pixels, UCHats locks the hat away from the
 *              lobby wardrobe while the role is enabled),
 *            - his victims are marked with a public blood ring - the forensic price of the power.
 *            The form ends by itself after Y, at meeting start and at the werewolf's death; the charge
 *            then restarts from full.
 *   SILVER   Silver is the beast's weakness (option 1557: Wounds / Kills / No effect). Who does what,
 *            as decided by the user on 2026-07-25:
 *              HUNTER   - always kills, in either form and in every mode. Killing the beast is the
 *                         entire reason the crew promoted him; the toughness below is what he exists
 *                         to overcome.
 *              SHERIFF  - kills the wolf in HUMAN form (TOR's own path, untouched). Against the WOLF
 *                         form his shot is survivable ONCE PER GAME: it wounds instead of killing
 *                         (forced revert + slow + kill cooldown to max + the charge starts over).
 *                         The next sheriff bullet kills. Mode "Kills" removes this exception.
 *              TRAPS    - Trapper (1518) and the UC Saboteur's traps (1519) always WOUND, never kill,
 *                         and never consume the toughness: a trap is iron, not silver, and the trapper
 *                         cannot know who walks into it. Mode "No effect" disables them.
 *              HANDCUFFS- the Deputy's cuffs force him back into human shape (1482), independent of
 *                         1557 - being restrained is not silver damage.
 *
 * WHY THE PATCHES LOOK LIKE THEY DO
 * ---------------------------------
 *  - CalculateLightRadius is a POSTFIX. TOR already owns that method with a Prefix that returns false
 *    (ShipStatusPatch.cs:17-81, the Trickster branch at :55 is the precedent for forcing darkness on
 *    everyone). Under HarmonyX every prefix runs and `false` only skips the ORIGINAL - a second prefix
 *    could never win against TOR's, a postfix always does.
 *  - The flashlight CONE is two more postfixes, on PlayerControl.IsFlashlightEnabled and
 *    PlayerControl.AdjustLighting - the exact pair TOR uses for the Lighter (PlayerControlPatch.cs:
 *    1463-1488), whose prefixes both return false and hard-code "only the Lighter". A radius alone is
 *    not a flashlight: it just shrinks the circle (and during a lights sabotage it would even make the
 *    crew's circle BIGGER than it already is). The cone is purely local state - it never leaves the
 *    client whose light it is - and is edge-triggered from the per-frame driver, because AU calls
 *    AdjustLighting on its own schedule only.
 *  - SwitchMinigame.Begin is a POSTFIX that calls Close(), byte-for-byte the shape TOR uses for the
 *    Swapper (UsablesPatch.cs:295-303), so the two coexist instead of fighting.
 *  - The vent block is a POSTFIX on Vent.CanUse (setting canUse/couldUse to false), NOT a prefix on
 *    Vent.Use: TOR's Vent.Use prefix performs the venting itself and returns false, so a competing
 *    prefix there would be pointless. TOR's own Vent.Use prefix asks CanUse first, so the block lands.
 *  - The sheriff's shot is suppressed with a PREFIX on RPCProcedure.uncheckedMurderPlayer - the one
 *    funnel every TOR field kill passes through (Buttons.cs:413-418 sends it, RPC.cs:480 executes it).
 *    That RPC arrives on every client with identical arguments, so the "this hit only wounds" verdict
 *    is deterministic everywhere and needs no extra round trip (waiting for one would let the murder
 *    happen on the clients that already processed the RPC).
 *  - Kill timers go through PlayerControl.SetKillTimer, which TOR clamps to the configured maximum
 *    (PlayerControlPatch.cs:1348-1361) - every value we set is BELOW that clamp, so it always lands.
 *
 * Options: 1551-1559 (core), 1513-1519 (spillover), 1482 (deputy). See ID-Registry.md.
 * RPC: module byte 211 on UCRpc.CallId 230. Subtypes: SubSetWerewolf=0, SubSetForm=1, SubWound=2,
 * SubSetHunter=3 (Hunter.cs) - Paket W2 (the Hunter) is the Sheriff's endgame counter-move against the
 * Werewolf, so it shares this module byte instead of claiming a new one (see Hunter.cs's own header for
 * the full role). This file's own additions for W2: IsLastImpostor is now PUBLIC (Hunter's trigger
 * reuses the exact same "am I the last impostor standing" probe the wolf's own transform gate uses),
 * BeginRpc is INTERNAL (Hunter.cs sends its subtype through it), HandleModuleRpc's switch dispatches
 * SubSetHunter to Hunter.ApplySetHunter, and SilverBulletPatch now also recognizes a shot fired by the
 * Hunter (who may no longer be Sheriff.sheriff after a deputy promotion) and arms WerewolfFx's one-shot
 * death sequence on a LETHAL wolf-form hit.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using UnityEngine;
using AmongUs.GameOptions;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Werewolf {
        // ---- Theme ----
        public static readonly Color Color = Palette.ImpostorRed;

        // ---- Options (core 1551-1559) ----
        public static CustomOption SpawnRate;              // 1551 (header, rates)
        public static CustomOption SpawnMinPlayers;        // 1552
        public static CustomOption KillCooldownReduction;  // 1553 (%)
        public static CustomOption SpeedMultiplier;        // 1554
        public static CustomOption ChargeTime;             // 1555 (s, only counts down in darkness)
        public static CustomOption FormDuration;           // 1556 (s)
        public static CustomOption SilverInteraction;      // 1557 (Kills / Wounds / Off)
        public static CustomOption HowlOnTransform;        // 1558
        public static CustomOption ChargeResetOnLightsFix; // 1559
        // ---- Options (spillover 1513-1519 + 1482) ----
        public static CustomOption OnlyAsLastImpostor;     // 1513
        public static CustomOption SpyCountsAsImpostor;    // 1514
        public static CustomOption FlashlightRadius;       // 1515 (choice: infinite / 2.0x .. 0.5x)
        public static CustomOption WolfFormRestrictions;   // 1516
        public static CustomOption ExhaustionSlow;         // 1517
        public static CustomOption TrapperTrapWounds;      // 1518
        public static CustomOption SaboteurTrapWounds;     // 1519
        public static CustomOption DeputyHandcuffsRevert;  // 1482

        // ---- Runtime state ----
        public static PlayerControl werewolf;
        public static bool active;
        // Synced on every client (SubSetForm): drives the skin, the darkness, the fix block and the
        // blood rings, so all of those need no separate RPC of their own.
        public static bool wolfForm;
        private static float formEndTime;
        // Which of the 7 wolf-form music variants this round uses. Picked by the host in the very same
        // RPC that assigns the role, so the whole lobby shares one musical identity for the round.
        private static int musicVariant;

        // Owner-client only (the werewolf's own client is the authority for its own timers).
        private static float chargeLeft;
        private static bool lightsWereOut;
        private static bool chargeReadyAnnounced;
        private static AudioSource heartbeatSource;

        // Silver bookkeeping. silverHitsTaken counts SHERIFF hits that landed on the WOLF form, per
        // GAME (decision: "the beast is tough ONCE" - a wolf that already carries a silver wound dies
        // to the next bullet even after reverting and transforming again). Trap wounds deliberately do
        // NOT consume the toughness: options 1518/1519 promise a wound, so they always wound.
        private static int silverHitsTaken;
        private static float lastWoundTime = -99f;
        private static float woundSlowUntil;
        private static float exhaustSlowUntil;

        // Speed handling, Scout pattern (Scout.cs:338-346): never cache a "base" speed once - other
        // roles/mods write MyPhysics.Speed too, so the base is re-derived from the CURRENTLY applied
        // multiplier whenever the desired multiplier changes.
        private static float speedBase;
        private static float appliedMult = 1f;

        private static TheOtherRoles.Objects.CustomButton transformButton;

        // ---- Constants ----
        private const float WoundSlowFactor = 0.8f;
        private const float WoundSlowSecs = 10f;
        private const float ExhaustSlowFactor = 0.85f;
        private const float ExhaustSlowSecs = 5f;
        private const float MusicVolume = 0.55f;
        private const int MusicVariants = 7;   // werewolf_form_music + music2..music7
        private const string MusicCue = "werewolf_form";
        private const int MusicPriority = 50;  // per WEREWOLF_PLAN.md §11.2 (reactor 100 wins over us)

        // ---- Custom RPC subtypes: module byte 211 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = UnknownsCollectionPlugin.WerewolfRpcId;
        private const byte SubSetWerewolf = 0;  // playerId, musicVariant
        private const byte SubSetForm = 1;      // wolf(byte 0/1), seconds(float)
        private const byte SubWound = 2;        // (no payload - the wolf is the only possible victim)

        // ---- Role identity ----
        private static RoleInfo werewolfInfo;
        public static RoleInfo WerewolfInfo() => werewolfInfo ??= new RoleInfo(
            "Werewolf", Color, "As the last Impostor, become the beast in the dark",
            "As the last Impostor, become the beast in the dark", RoleId.Impostor);

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1551, Types.Impostor, "Werewolf",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1552, Types.Impostor, "Werewolf Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                KillCooldownReduction = CustomOption.Create(1553, Types.Impostor, "Wolf Kill Cooldown Reduction (%)",
                    30f, 0f, 75f, 5f, SpawnRate);
                // Percent, not a 1.0-2.0 factor: TOR builds slider values with
                // "for (float s = min; s <= max; s += step)" (CustomOptions.cs:85), and 0.1 has no
                // exact float representation - the accumulated error surfaced in the menu as
                // "1,4000001". Whole percent steps are exact, and the label already says "%".
                SpeedMultiplier = CustomOption.Create(1554, Types.Impostor, "Wolf Speed Multiplier (%)",
                    140f, 100f, 200f, 5f, SpawnRate);
                ChargeTime = CustomOption.Create(1555, Types.Impostor, "Alpha Charge Time In Darkness (s)",
                    8f, 3f, 30f, 1f, SpawnRate);
                FormDuration = CustomOption.Create(1556, Types.Impostor, "Wolf Form Duration (s)",
                    12f, 5f, 30f, 1f, SpawnRate);
                // The string-choice overload of CustomOption.Create always defaults to index 0
                // (CustomOptions.cs:78 passes "" as the default value), so the wanted default -
                // "Wounds" - is simply listed FIRST instead of patching defaultSelection after the
                // config entry has already been bound. The choice texts are deliberately verbose:
                // UCLocalization matches selection strings by their English TEXT across all uc.* keys,
                // so a bare "Off" here would silently re-translate every bool option in the mod.
                SilverInteraction = CustomOption.Create(1557, Types.Impostor, "Silver Interaction",
                    new string[] { "Wounds The Wolf", "Kills The Wolf", "No Silver Effect" }, SpawnRate);
                HowlOnTransform = CustomOption.Create(1558, Types.Impostor, "Howl On Transform",
                    true, SpawnRate);
                ChargeResetOnLightsFix = CustomOption.Create(1559, Types.Impostor, "Charge Reset On Lights Fix",
                    false, SpawnRate);

                OnlyAsLastImpostor = CustomOption.Create(1513, Types.Impostor, "Only As Last Impostor",
                    true, SpawnRate);
                SpyCountsAsImpostor = CustomOption.Create(1514, Types.Impostor, "Spy Counts As Impostor",
                    false, SpawnRate);
                // Option 1515 is a CHOICE list, not a numeric slider: the wanted range ("infinite,
                // then 2x down to 0.5x of the standard sight") has a special value at one end that no
                // min/max/step slider can express. It is created through the CONSTRUCTOR instead of
                // CustomOption.Create, because the string overload always defaults to index 0
                // (CustomOptions.cs:80 passes "" as the default value) and index 0 is "Infinite" here -
                // the constructor is public and takes the default value directly, so the list can keep
                // the user's own order without the "list the default first" trick option 1557 needs.
                FlashlightRadius = new CustomOption(1515, Types.Impostor, "Flashlight Radius For Everyone",
                    FlashlightChoices, FlashlightChoices[DefaultFlashlightIndex], SpawnRate, false);
                WolfFormRestrictions = CustomOption.Create(1516, Types.Impostor, "Wolf Form Restrictions",
                    true, SpawnRate);
                ExhaustionSlow = CustomOption.Create(1517, Types.Impostor, "Exhaustion Slow After Revert",
                    true, SpawnRate);
                TrapperTrapWounds = CustomOption.Create(1518, Types.Impostor, "Trapper Trap Wounds The Wolf",
                    true, SpawnRate);
                SaboteurTrapWounds = CustomOption.Create(1519, Types.Impostor, "Saboteur Trap Wounds The Wolf",
                    true, SpawnRate);
                DeputyHandcuffsRevert = CustomOption.Create(1482, Types.Impostor, "Deputy Handcuffs Force Revert",
                    true, SpawnRate);

                WerewolfFx.Init(); // force the FX static ctor (UCFx tick/reset registration)
                UnknownsCollectionPlugin.Logger?.LogInfo("[Werewolf] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            // Receiver registration on the shared UC channel (UCRpc.CallId = 230).
            UCRpc.Register(RpcId, HandleModuleRpc);

            // TheOtherRoles.Objects.Trap is INTERNAL, so its trigger cannot be reached with an
            // attribute patch from this assembly - reflection + a manual postfix is the only way in
            // (same idiom as UCRoleDraft.PatchDraftData). triggerTrap(playerId, trapId) runs on every
            // client via TOR's own TriggerTrap RPC, so the postfix sees every trapping everywhere.
            try {
                var trapType = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Objects.Trap");
                var m = trapType?.GetMethod("triggerTrap", BindingFlags.Public | BindingFlags.Static);
                if (m == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        "[Werewolf] Trap.triggerTrap not found - option 1518 (Trapper trap wounds the wolf) is inert.");
                } else {
                    var post = typeof(Werewolf).GetMethod(nameof(OnTrapperTrap), BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(m, postfix: new HarmonyMethod(post));
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Werewolf] Trapper trap hook patched.");
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] TryPatch failed: {e}");
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);

        public static bool IsLocalWerewolf() =>
            werewolf != null && PlayerControl.LocalPlayer != null
            && werewolf.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        private static float ChargeTimeValue() => ChargeTime != null ? ChargeTime.getFloat() : 8f;
        private static float FormDurationValue() => FormDuration != null ? FormDuration.getFloat() : 12f;
        // Option 1554 stores whole percent (140 = 1.4x) - see the comment at its definition.
        private static float SpeedMultValue() =>
            SpeedMultiplier != null ? SpeedMultiplier.getFloat() / 100f : 1.4f;
        private static float KillReductionValue() =>
            Mathf.Clamp01((KillCooldownReduction != null ? KillCooldownReduction.getFloat() : 30f) / 100f);
        // ---- The torch (option 1515) ----
        //
        // The visible choices and the numbers behind them. Index 0 is "no limit at all", the rest are
        // multipliers of the STANDARD crew sight (see CrewBaseRadius below). The two arrays are kept in
        // lockstep and everything reads the FACTOR BY INDEX, never by parsing the visible text:
        // UCLocalization replaces opt.selections with translated strings (UCLocalization.cs:178), so a
        // text-based lookup would break in every non-English client.
        private static readonly string[] FlashlightChoices = {
            "Infinite",
            "2.0x", "1.9x", "1.8x", "1.7x", "1.6x", "1.5x", "1.4x", "1.3x", "1.2x", "1.1x",
            "1.0x", "0.9x", "0.8x", "0.7x", "0.6x", "0.5x",
        };
        private static readonly float[] FlashlightFactors = {
            0f,   // index 0 = infinite, handled separately - never used as a factor
            2.0f, 1.9f, 1.8f, 1.7f, 1.6f, 1.5f, 1.4f, 1.3f, 1.2f, 1.1f,
            1.0f, 0.9f, 0.8f, 0.7f, 0.6f, 0.5f,
        };
        // 0.5x - the darkest setting, and the closest one to the old fixed 35%-of-MaxLightRadius value.
        private const int DefaultFlashlightIndex = 16;

        private static int FlashlightIndex() {
            if (FlashlightRadius == null) return DefaultFlashlightIndex;
            return Mathf.Clamp(FlashlightRadius.getSelection(), 0, FlashlightFactors.Length - 1);
        }
        private static bool FlashlightInfinite() => FlashlightIndex() == 0;
        private static float FlashlightFactor() => FlashlightFactors[FlashlightIndex()];
        private static bool RestrictionsOn() => WolfFormRestrictions == null || WolfFormRestrictions.getBool();

        // 0 = Wounds (default), 1 = Kills, 2 = No effect - the order of the selection array above.
        // SilverKills is only named for readability: the code branches on "anything but Wounds",
        // because Kills and No-effect differ solely in whether the TRAPS still wound (see TrapHitTheWolf).
        private const int SilverWounds = 0, SilverKills = 1, SilverOff = 2;
        private static int SilverMode() => SilverInteraction != null ? SilverInteraction.getSelection() : SilverWounds;

        private static float BaseKillCooldown() {
            try { return GameOptionsManager.Instance.currentNormalGameOptions.KillCooldown; }
            catch { return 30f; }
        }

        // The wolf darkness is exactly "the werewolf is currently transformed" - wolfForm is synced by
        // SubSetForm, so every client derives the vision override and the fix block from the same flag.
        public static bool WolfDarkActive() => active && wolfForm && IsAlive(werewolf);

        // Same probe TOR's own SabotageTuning/Siphoner and UC's BeaconFx use (BeaconFx.cs:171):
        // the Electrical system cast to SwitchSystem, whose IsActive flag is synced on every client.
        private static bool LightsSabotageActive() {
            try {
                var ship = MapUtilities.CachedShipStatus;
                if (ship == null || ship.Systems == null) return false;
                if (!ship.Systems.TryGetValue(SystemTypes.Electrical, out ISystemType sys) || sys == null) return false;
                var sw = sys.TryCast<SwitchSystem>();
                return sw != null && sw.IsActive;
            } catch {
                return false;
            }
        }

        // "He is the last one left." Counts living Impostors; the Spy is a crewmate in TOR, so it only
        // counts when option 1514 says so (it plays like an impostor for the crew's purposes).
        // PUBLIC (Paket W2): Hunter.cs's own trigger ("all non-werewolf impostors are dead") reuses
        // this exact probe instead of duplicating it - the two conditions are the same question.
        public static bool IsLastImpostor() {
            try {
                int imps = 0;
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (!IsAlive(p) || p.Data.Role == null) continue;
                    if (p.Data.Role.IsImpostor) imps++;
                }
                if (SpyCountsAsImpostor != null && SpyCountsAsImpostor.getBool()
                    && Spy.spy != null && IsAlive(Spy.spy)) imps++;
                return imps <= 1 && werewolf != null && IsAlive(werewolf) && werewolf.Data.Role != null
                       && werewolf.Data.Role.IsImpostor;
            } catch {
                return true;
            }
        }

        private static bool ChargeReady() => chargeLeft <= 0f;

        // LightsSabotageActive is part of the condition, not just of the charging: the beast comes out
        // IN THE DARK. Without it a wolf could bank a full charge during one blackout and then
        // transform minutes later in broad daylight, which defeats the whole point of the alpha mode
        // (and of the crew fixing the lights at all).
        public static bool CanTransformNow() =>
            active && IsLocalWerewolf() && IsAlive(werewolf) && !wolfForm && !InMeeting()
            && ChargeReady() && LightsSabotageActive()
            && (OnlyAsLastImpostor == null || !OnlyAsLastImpostor.getBool() || IsLastImpostor());

        private static string MusicClipName() =>
            musicVariant <= 0 ? "werewolf_form_music" : $"werewolf_form_music{musicVariant + 1}";

        // ====================================================================
        // RPC
        // ====================================================================
        // INTERNAL (Paket W2): Hunter.cs shares this module byte, so it reuses this helper to send its
        // own two subtypes instead of duplicating the "write the subtype right after Begin" idiom.
        internal static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = UCRpc.Begin(RpcId); // shared UC channel; RpcId is the module byte
            w.Write(subtype);
            return w;
        }

        public static void SendSetWerewolf(byte id, byte variant) {
            try {
                var w = BeginRpc(SubSetWerewolf);
                w.Write(id);
                w.Write(variant);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetWerewolf(id, variant);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] SendSetWerewolf failed: {e}"); }
        }

        // Sent by the werewolf's OWN client only (single sender - no host/owner double trigger).
        private static void SendSetForm(bool wolf, float secs) {
            try {
                var w = BeginRpc(SubSetForm);
                w.Write((byte)(wolf ? 1 : 0));
                w.Write(secs);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetForm(wolf, secs);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] SendSetForm failed: {e}"); }
        }

        private static void SendWound() {
            try {
                var w = BeginRpc(SubWound);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyWound();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] SendWound failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSetWerewolf: {
                        byte id = reader.ReadByte();
                        byte variant = reader.ReadByte();
                        ApplySetWerewolf(id, variant);
                        break;
                    }
                    case SubSetForm: {
                        bool wolf = reader.ReadByte() != 0;
                        float secs = reader.ReadSingle();
                        ApplySetForm(wolf, secs);
                        break;
                    }
                    case SubWound: ApplyWound(); break;
                    // Paket W2: the Hunter's own subtype, dispatched here because it shares this module
                    // byte (see the file-header note above and Hunter.cs's own RPC section).
                    case Hunter.SubSetHunter: {
                        byte id = reader.ReadByte();
                        byte naturalGuesser = reader.ReadByte();
                        Hunter.ApplySetHunter(id, naturalGuesser != 0);
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] HandleRpc failed: {e}");
            }
        }

        // ====================================================================
        // Appliers (all of these run on EVERY client)
        // ====================================================================
        private static void ApplySetWerewolf(byte id, byte variant) {
            werewolf = Helpers.playerById(id);
            active = werewolf != null;
            if (active) UCPromotion.Claim(id);
            musicVariant = Mathf.Clamp(variant, 0, MusicVariants - 1);
            wolfForm = false;
            formEndTime = 0f;
            chargeLeft = ChargeTimeValue();
            lightsWereOut = false;
            chargeReadyAnnounced = false;
            silverHitsTaken = 0;
            woundSlowUntil = 0f;
            exhaustSlowUntil = 0f;
            lastWoundTime = -99f;
            if (active)
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Werewolf] The Werewolf is {werewolf.Data?.PlayerName} (music variant {musicVariant + 1}).");
        }

        public static void MarkFromDraft(byte playerId) =>
            // The draft has no music byte to carry, so the variant is derived from the drafted player -
            // still identical on every client, which is all the shared-identity rule needs.
            ApplySetWerewolf(playerId, (byte)(playerId % MusicVariants));

        private static void ApplySetForm(bool wolf, float secs) {
            if (!active || werewolf == null) return;
            if (wolf == wolfForm) {
                if (wolf) formEndTime = Time.time + secs; // idempotent refresh
                return;
            }
            Vector2 pos = werewolf.GetTruePosition();

            if (wolf) {
                wolfForm = true;
                formEndTime = Time.time + secs;

                UCAssets.PlayWerewolfTransformAt(pos);
                if (HowlOnTransform == null || HowlOnTransform.getBool())
                    UCAssets.PlayWerewolfHowlAt(pos);
                WerewolfFx.SpawnFlare(pos);
                WerewolfFx.BeginTransformLook(werewolf);

                // Force-close a lights minigame that is ALREADY open on this client (the Begin postfix
                // below only catches the ones opened from now on) - Swapper precedent, UsablesPatch.cs:295.
                CloseOpenSwitchMinigame();

                if (IsLocalWerewolf()) {
                    StopHeartbeat();
                    // One-time cut of the RUNNING kill timer, on top of the per-kill reduction below.
                    var me = PlayerControl.LocalPlayer;
                    if (me != null && me.killTimer > 0f)
                        me.SetKillTimer(me.killTimer * (1f - KillReductionValue()));
                }
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Werewolf] Wolf form ON for {secs:F1}s.");
            } else {
                wolfForm = false;
                formEndTime = 0f;

                UCAssets.PlayWerewolfRevertAt(pos);
                WerewolfFx.SpawnFlare(pos, 0.35f);
                WerewolfFx.BeginRevertLook();
                UCMusic.Release(MusicCue);

                if (IsLocalWerewolf()) {
                    chargeLeft = ChargeTimeValue();   // the next transformation must be charged again
                    chargeReadyAnnounced = false;
                    if (ExhaustionSlow == null || ExhaustionSlow.getBool())
                        exhaustSlowUntil = Time.time + ExhaustSlowSecs;
                }
                UnknownsCollectionPlugin.Logger?.LogInfo("[Werewolf] Wolf form OFF.");
            }
        }

        // Silent end used by the meeting/death paths: no revert sound, no exhaustion, no howl - the
        // form simply ceases to exist. Runs locally on every client (both triggers are global events).
        private static void EndFormSilent() {
            if (!wolfForm) return;
            wolfForm = false;
            formEndTime = 0f;
            WerewolfFx.ClearLook();
            UCMusic.Release(MusicCue);
            if (IsLocalWerewolf()) {
                chargeLeft = ChargeTimeValue();
                chargeReadyAnnounced = false;
            }
        }

        // The shared "wound" reaction of every anti-beast source (spec §W1 Silber/Anti-Bestie v1).
        // Runs on every client; the 0.5 s guard makes a double delivery (deterministic sheriff path +
        // an RPC-driven trap path landing in the same frame) harmless.
        private static void ApplyWound() {
            if (!active || werewolf == null) return;
            if (Time.time - lastWoundTime < 0.5f) return;
            lastWoundTime = Time.time;

            Vector2 pos = werewolf.GetTruePosition();
            UCAssets.PlayWerewolfSilverAt(pos);
            woundSlowUntil = Time.time + WoundSlowSecs;

            if (wolfForm) {
                // Forced revert: audible + visible, exactly like a voluntary one, but it does NOT grant
                // the exhaustion window on top (the wound slow is already the harsher penalty).
                wolfForm = false;
                formEndTime = 0f;
                UCAssets.PlayWerewolfRevertAt(pos);
                WerewolfFx.BeginRevertLook();
                UCMusic.Release(MusicCue);
                if (IsLocalWerewolf()) {
                    chargeLeft = ChargeTimeValue();
                    chargeReadyAnnounced = false;
                }
            }

            if (IsLocalWerewolf()) {
                var me = PlayerControl.LocalPlayer;
                if (me != null) me.SetKillTimer(BaseKillCooldown()); // straight to maximum
            }
            UnknownsCollectionPlugin.Logger?.LogInfo("[Werewolf] Silver wound applied.");
        }

        // Shared entry point of the two TRAP sources (Trapper + UC Saboteur). Only the werewolf's own
        // client reacts, so the wound is broadcast exactly once.
        //
        // Traps ALWAYS wound, never kill - not even in silver mode "Kills" (user decision 2026-07-25).
        // A trap normally just holds someone in place and the trapper cannot know who walks into it;
        // turning it into the deadliest weapon in the game on a mode switch made an environmental
        // hazard outclass the aimed silver shot. "Kills" therefore applies to the sheriff only.
        private static void TrapHitTheWolf() {
            if (!active || !IsLocalWerewolf() || !IsAlive(werewolf)) return;
            if (SilverMode() == SilverOff) return;
            SendWound();
        }

        private static void CloseOpenSwitchMinigame() {
            try {
                var mg = Minigame.Instance;
                if (mg == null) return;
                var sw = mg.TryCast<SwitchMinigame>();
                if (sw != null) sw.Close();
            } catch { }
        }

        private static void StopHeartbeat() {
            try {
                if (heartbeatSource != null) UCAssets.StopWerewolfHeartbeat();
            } catch { }
            heartbeatSource = null;
        }

        // ====================================================================
        // Round reset
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                try { WerewolfFx.ClearLook(); } catch { }
                try { WerewolfFx.ClearBloodRings(); } catch { }
                try { UCMusic.Release(MusicCue); } catch { }
                StopHeartbeat();
                ForceConeOff();
                werewolf = null;
                active = false;
                wolfForm = false;
                formEndTime = 0f;
                musicVariant = 0;
                chargeLeft = 0f;
                lightsWereOut = false;
                chargeReadyAnnounced = false;
                silverHitsTaken = 0;
                lastWoundTime = -99f;
                woundSlowUntil = 0f;
                exhaustSlowUntil = 0f;
                appliedMult = 1f;
                speedBase = 0f;
                // transformButton deliberately kept (resetVariables runs AFTER HudManager.Start).
            }
        }

        // The role keeps no PlayerId lists, but the two PlayerControl/flag pairs above would still
        // survive into a FOREIGN lobby if a round ends without resetVariables ever running. Clearing
        // them on join is the same belt-and-suspenders rule the mod family adopted after the
        // "resetVariables lobby leak" bug.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinPatch {
            public static void Postfix() {
                try { WerewolfFx.ClearLook(); } catch { }
                try { WerewolfFx.ClearBloodRings(); } catch { }
                try { UCMusic.Release(MusicCue); } catch { }
                StopHeartbeat();
                ForceConeOff();
                werewolf = null;
                active = false;
                wolfForm = false;
                silverHitsTaken = 0;
                appliedMult = 1f;
            }
        }

        // ====================================================================
        // Game end: "was there a beast this round?" (Paket W4, victory scene)
        // ====================================================================
        // TOR calls RPCProcedure.resetVariables() from its own AmongUsClient.OnGameEnd POSTFIX
        // (EndGamePatch.cs:233), which wipes `active`/`werewolf` long before EndGameManager even
        // exists. The victory scene therefore needs a snapshot taken while the statics are still
        // alive - the same lifetime rule the Bug's winnerBugId and the Pelican's winnerPelicanId
        // follow: re-stamped at every game end, deliberately NOT cleared by resetVariables.
        public static bool HadWerewolfThisRound { get; private set; }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.First)]
        static class GameEndSnapshotPatch {
            public static void Prefix() {
                try { HadWerewolfThisRound = active && werewolf != null; } catch { }
            }
        }

        // ====================================================================
        // Game start: host-authoritative pick
        // ====================================================================
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;
                    // The music variant is rolled ONCE per round, here, and travels with the role
                    // assignment - so every client hears the same track for the whole round.
                    SendSetWerewolf(candidates[rnd.Next(candidates.Count)].PlayerId,
                                    (byte)rnd.Next(MusicVariants));
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Button
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = UCAssets.WerewolfTransformIcon
                        ?? (__instance.KillButton != null && __instance.KillButton.graphic != null
                            ? __instance.KillButton.graphic.sprite : null);
                    transformButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            if (!active || !IsLocalWerewolf()) return;
                            if (wolfForm) SendSetForm(false, 0f);
                            else if (CanTransformNow()) SendSetForm(true, FormDurationValue());
                        },
                        () => active && IsLocalWerewolf()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove && !InMeeting()
                              && (wolfForm || CanTransformNow()),
                        () => { },
                        sprite,
                        // Same slot the other single-button UC Impostor roles use (Silencer/Maniac):
                        // the Werewolf is always promoted onto a PLAIN Impostor, so no TOR ability
                        // button can ever share the row with it.
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.werewolf.button_transform"));
                    transformButton.MaxTimer = 0f;
                    transformButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] Button creation failed: {e}");
                }
            }
        }

        // ====================================================================
        // Per-frame driver
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    // The cone is a per-CLIENT thing (every survivor carries his own torch), so it ticks
                    // BEFORE the "is there even a beast" bail-out: that is also the path which switches
                    // the torch back off once the form, the round or the wolf itself is gone.
                    TickCone();

                    if (!active || werewolf == null) return;

                    // 1. Safety net: the beast dies/leaves -> the form dies with it (the murder/exile
                    //    postfixes below do this too; this catches every remaining path).
                    if (wolfForm && !IsAlive(werewolf)) EndFormSilent();

                    // 2. Auto end after Y. The OWNER announces it (single sender); every other client
                    //    ends it locally half a second later in case that announcement never arrives.
                    if (wolfForm && Time.time >= formEndTime) {
                        if (IsLocalWerewolf()) SendSetForm(false, 0f);
                        else if (Time.time >= formEndTime + 0.5f) EndFormSilent();
                    }

                    // 3. Wolf-form music. UCMusic's contract wants a Request EVERY frame while the cue
                    //    should be audible plus a Release at the end (done in ApplySetForm/EndFormSilent).
                    if (wolfForm && !InMeeting()) {
                        UCMusic.Request(MusicCue, MusicClipName(), MusicPriority, MusicVolume,
                                        Mathf.Max(0f, formEndTime - Time.time), true);
                    }

                    if (!IsLocalWerewolf()) return;

                    // ---- everything below is OWNER-CLIENT ONLY ----
                    TickCharge();
                    TickSpeed();
                    TickButton();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] HudUpdate failed: {e}");
                }
            }
        }

        // Alpha charge: counts down ONLY while the lights are out, pauses (default) or resets (1559)
        // when they come back on. Frozen while the wolf form is running - that phase has its own timer.
        private static void TickCharge() {
            // A dead beast charges nothing, and a meeting freezes the countdown - in both cases the
            // heartbeat loop has to stop, or it would keep pounding through the meeting/ghost phase.
            if (InMeeting() || !IsAlive(werewolf)) { StopHeartbeat(); return; }
            bool lightsOut = LightsSabotageActive();

            if (!wolfForm) {
                if (lightsOut && chargeLeft > 0f) {
                    chargeLeft = Mathf.Max(0f, chargeLeft - Time.deltaTime);
                    if (heartbeatSource == null && chargeLeft > 0f)
                        heartbeatSource = UCAssets.PlayWerewolfHeartbeatLoop();
                } else if (!lightsOut) {
                    StopHeartbeat();
                    if (lightsWereOut && ChargeResetOnLightsFix != null && ChargeResetOnLightsFix.getBool()) {
                        chargeLeft = ChargeTimeValue();
                        chargeReadyAnnounced = false;
                    }
                }
                if (chargeLeft <= 0f) {
                    StopHeartbeat();
                    if (!chargeReadyAnnounced) {
                        chargeReadyAnnounced = true;
                        UCAssets.PlayWerewolfGrowl(); // local-only "you are ready" cue
                    }
                }
            } else {
                StopHeartbeat();
            }
            lightsWereOut = lightsOut;
        }

        // Scout pattern (Scout.cs:338-346): re-derive the base speed from the multiplier that is
        // CURRENTLY applied whenever the desired multiplier changes, instead of caching it once - so a
        // TOR ability that writes MyPhysics.Speed in between is not overwritten with a stale value.
        private static void TickSpeed() {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.MyPhysics == null) return;

            float mult = wolfForm ? SpeedMultValue() : 1f;
            // Slows do not stack multiplicatively - the strongest one wins (a wounded, exhausted wolf
            // should not end up at 0.68x by accident).
            float slow = 1f;
            if (Time.time < woundSlowUntil) slow = Mathf.Min(slow, WoundSlowFactor);
            if (Time.time < exhaustSlowUntil) slow = Mathf.Min(slow, ExhaustSlowFactor);
            mult *= slow;

            if (Mathf.Abs(mult - appliedMult) > 0.0001f) {
                speedBase = me.MyPhysics.Speed / Mathf.Max(0.0001f, appliedMult);
                appliedMult = mult;
                if (Mathf.Abs(mult - 1f) <= 0.0001f) {
                    me.MyPhysics.Speed = speedBase;   // hand the speed back untouched and stop writing
                    return;
                }
            }
            if (Mathf.Abs(appliedMult - 1f) > 0.0001f && speedBase > 0f)
                me.MyPhysics.Speed = speedBase * appliedMult;
        }

        private static void TickButton() {
            if (transformButton == null) return;
            // Icon: the flipbook driver (UCButtonAnim) matches by sprite instance id, so setting the
            // STATIC icon here selects which of the two animations plays - the two cooperate.
            var icon = wolfForm ? UCAssets.WerewolfRevertIcon : UCAssets.WerewolfTransformIcon;
            if (icon != null && transformButton.Sprite != icon
                && !IsFrameOf(transformButton.Sprite, wolfForm)) transformButton.Sprite = icon;
            // CustomButton re-applies buttonText every Update (Objects/CustomButton.cs:236), so the
            // label can simply be swapped here instead of rebuilding the button.
            string label = UCLocalization.Tr(
                wolfForm ? "uc.ui.werewolf.button_revert" : "uc.ui.werewolf.button_transform");
            if (wolfForm) {
                // How much wolf time is left. It cannot go on the cooldown ring: CustomButton only
                // fires onClick while Timer < 0, so a running timer there would lock the werewolf into
                // its form. The label is the one place that shows a number without disabling the button.
                int left = Mathf.CeilToInt(Mathf.Max(0f, formEndTime - Time.time));
                label += $" ({left}s)";
            }
            transformButton.buttonText = label;

            if (wolfForm) {
                // Reverting is always allowed - no cooldown ring, the button must be clickable
                // (CustomButton.onClickEvent requires Timer < 0).
                transformButton.MaxTimer = 0f;
                transformButton.Timer = -1f;
            } else {
                transformButton.MaxTimer = ChargeTimeValue();
                transformButton.Timer = chargeLeft > 0f ? chargeLeft : -1f;
            }
        }

        // Cheap guard so TickButton does not fight UCButtonAnim frame-by-frame: while the correct
        // animation is playing, the button sprite is one of ITS frames, never the other icon. The two
        // frame sets are resolved once (GetFrames walks 16 cache lookups + string builds per call -
        // far too much for a check that runs every frame).
        private static Sprite[] transformFrames, revertFrames;
        private static bool frameSetsResolved;

        private static bool IsFrameOf(Sprite current, bool revert) {
            if (current == null) return false;
            if (!frameSetsResolved) {
                frameSetsResolved = true;
                transformFrames = UCAssets.GetFrames("werewolf_transform", 115f);
                revertFrames = UCAssets.GetFrames("werewolf_revert", 115f);
            }
            var frames = revert ? revertFrames : transformFrames;
            if (frames == null) return false;
            for (int i = 0; i < frames.Length; i++) if (frames[i] == current) return true;
            return false;
        }

        // ====================================================================
        // Wolf darkness: vision + lights-fix block
        // ====================================================================

        // What option 1515 measures itself against: the STANDARD crew sight, i.e. what a crewmate sees
        // with the lights ON (MaxLightRadius * CrewLightMod - the value TOR's own GetNeutralLightRadius
        // lerps to at switch value 255, ShipStatusPatch.cs:95). Deliberately NOT the current, sabotaged
        // radius: the wolf darkness always runs during a lights sabotage, so measuring against the
        // sabotaged circle would leave even "2x" pitch black. Taking CrewLightMod into account (the old
        // code used bare MaxLightRadius) means a host who already plays with a bigger or smaller crew
        // vision keeps that as the reference point - "0.5x" is half of what THIS lobby calls normal.
        private static float CrewBaseRadius(ShipStatus ship) {
            float mod = 1f;
            try { mod = GameOptionsManager.Instance.currentNormalGameOptions.CrewLightMod; } catch { }
            return ship.MaxLightRadius * mod;
        }

        // "Infinite" is a number too - the light just has to outrun the screen. The AU camera shows
        // roughly 3 world units in each direction and MaxLightRadius alone already fills a good part of
        // it, so ten times that is past every edge of the view; walls still cut the light off, which is
        // exactly what makes it read as "the torch reaches as far as you can see".
        private const float InfiniteRadiusFactor = 10f;

        private static float TorchRadius(ShipStatus ship) =>
            FlashlightInfinite() ? ship.MaxLightRadius * InfiniteRadiusFactor
                                 : CrewBaseRadius(ship) * FlashlightFactor();

        // POSTFIX - see the file header: TOR's own CalculateLightRadius prefix (ShipStatusPatch.cs:17)
        // returns false, so only a postfix can have the last word.
        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.CalculateLightRadius))]
        static class LightPatch {
            public static void Postfix(ref float __result, ShipStatus __instance,
                                       [HarmonyArgument(0)] NetworkedPlayerInfo p) {
                try {
                    if (!WolfDarkActive() || p == null || __instance == null) return;
                    // The beast owns the dark: full impostor vision - but never LESS than the torch the
                    // crew is walking around with. With the torch set to 2x or infinite the crew's
                    // circle would otherwise out-reach the wolf's, and the hunted would spot the hunter
                    // first. Max() keeps "the wolf sees at least as far as his prey" true at every
                    // setting while leaving the usual (torch < impostor vision) case untouched.
                    if (werewolf != null && p.PlayerId == werewolf.PlayerId) {
                        float imp = __instance.MaxLightRadius
                                    * GameOptionsManager.Instance.currentNormalGameOptions.ImpostorLightMod;
                        __result = Mathf.Max(imp, TorchRadius(__instance));
                        return;
                    }
                    // The Lighter keeps whatever TOR just computed for him (explicit carve-out).
                    if (Lighter.lighter != null && p.PlayerId == Lighter.lighter.PlayerId) return;
                    // Paket W2: the Hunter is exempted from the blanket flashlight too - he gets the
                    // SAME crew radius as everyone else, scaled up by his own multiplier (option 1504,
                    // 1.0-2.5x; 1.0 = same as the crew, 2.5x = well beyond it) instead of a flat value,
                    // so he stays meaningfully ahead of the crew without matching the wolf's full sight.
                    // At "Infinite" there is nothing left to scale - everyone already sees to the edge
                    // of the screen - so his multiplier simply has no effect there.
                    if (Hunter.active && Hunter.hunter != null && p.PlayerId == Hunter.hunter.PlayerId) {
                        __result = FlashlightInfinite()
                            ? TorchRadius(__instance)
                            : CrewBaseRadius(__instance) * FlashlightFactor() * Hunter.FlashlightMultiplierValue();
                        return;
                    }
                    __result = TorchRadius(__instance);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] LightPatch failed: {e}");
                }
            }
        }

        // ---- The flashlight CONE (the other half of "flashlight") ----
        //
        // The radius above only shrinks the circle. What actually makes a torch is the CONE, and TOR's
        // Lighter shows the exact three pieces it takes (PlayerControlPatch.cs:1463-1488):
        //   1. PlayerControl.IsFlashlightEnabled has to say yes for the local player,
        //   2. PlayerControl.AdjustLighting has to hand the light source over to the flashlight setup:
        //      lightSource.SetupLightingForGameplay(true, width, TargetFlashlight.transform),
        //   3. SetFlashlightInputMethod picks how the cone is aimed (mouse / stick).
        // TOR owns BOTH of those methods with prefixes that return false and hard-code "only the
        // Lighter" (IsFlashlightEnabled sets __result = false for everyone else), so POSTFIXES are the
        // only way in - the same rule as the radius patch above.
        //
        // All of this is strictly LOCAL: a cone only ever exists on the client whose own light it is,
        // and it is never sent anywhere.

        // TOR's own Lighter default (option 113 "Flashlight Width", 0.1-1.0). The Hunter's cone is
        // widened by the same multiplier that already scales his radius (option 1504), so one setting
        // shapes his whole torch instead of adding a second slider for it.
        private const float ConeWidth = 0.3f;

        // True while WE are the ones holding the cone on. It is what tells "switch our torch off again"
        // apart from "this client never had one" - without it the postfix would also stamp
        // enableFlashlight=false onto the Lighter's cone and onto Fungle's own night flashlight.
        private static bool coneIsOurs;
        private static bool coneWarned;

        private static bool IsLocalLighter() {
            var me = PlayerControl.LocalPlayer;
            return me != null && Lighter.lighter != null && Lighter.lighter.PlayerId == me.PlayerId;
        }

        // Who walks the dark with a torch: everybody EXCEPT the wolf (he owns the dark with full
        // impostor vision), the Lighter (TOR already gives him his own cone) and the dead (ghosts see
        // everything anyway). Exactly the carve-out list of the radius postfix above.
        private static bool LocalWantsCone() {
            try {
                if (!WolfDarkActive()) return false;
                var me = PlayerControl.LocalPlayer;
                if (me == null || me.Data == null || me.Data.IsDead || me.Data.Disconnected) return false;
                if (werewolf != null && me.PlayerId == werewolf.PlayerId) return false;
                if (IsLocalLighter()) return false;
                return true;
            } catch {
                return false;
            }
        }

        private static float LocalConeWidth() {
            float w = ConeWidth;
            var me = PlayerControl.LocalPlayer;
            if (Hunter.active && Hunter.hunter != null && me != null && Hunter.hunter.PlayerId == me.PlayerId)
                w *= Hunter.FlashlightMultiplierValue();
            return Mathf.Clamp(w, 0.1f, 1f);
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.IsFlashlightEnabled))]
        static class FlashlightEnabledPatch {
            public static void Postfix(PlayerControl __instance, ref bool __result) {
                try {
                    if (__instance == null || !__instance.AmOwner) return;
                    if (LocalWantsCone()) __result = true;
                } catch { }
            }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.AdjustLighting))]
        static class AdjustLightingPatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    if (__instance == null || !__instance.AmOwner) return;
                    bool want = LocalWantsCone();
                    if (!want && !coneIsOurs) return;   // nothing of ours to set, nothing to undo
                    if (__instance.lightSource == null || __instance.TargetFlashlight == null) return;
                    coneIsOurs = want;
                    __instance.SetFlashlightInputMethod();
                    __instance.lightSource.SetupLightingForGameplay(
                        want, LocalConeWidth(), __instance.TargetFlashlight.transform);
                } catch (Exception e) {
                    if (!coneWarned) {   // per-frame path - report it once, never spam the log
                        coneWarned = true;
                        UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] flashlight cone failed: {e}");
                    }
                }
            }
        }

        // The cone is edge-triggered: AU calls AdjustLighting on its own schedule only, so every change
        // of our own condition (transform, revert, silver wound, death, meeting, round end) has to ask
        // for it. One bool compare per frame, one call per actual change.
        private static void TickCone() {
            if (LocalWantsCone() == coneIsOurs) return;
            try {
                var me = PlayerControl.LocalPlayer;
                if (me != null) me.AdjustLighting();   // the postfix above does the actual work
            } catch { }
        }

        // The torch has to be TURNED OFF, not just forgotten: dropping the flag alone would leave it
        // burning into the next lobby (the resetVariables lobby-leak rule).
        private static void ForceConeOff() {
            if (!coneIsOurs) return;
            coneIsOurs = false;
            try {
                var me = PlayerControl.LocalPlayer;
                if (me != null && me.lightSource != null && me.TargetFlashlight != null) {
                    me.SetFlashlightInputMethod();
                    me.lightSource.SetupLightingForGameplay(false, ConeWidth, me.TargetFlashlight.transform);
                }
            } catch { }
        }

        // Nobody fixes the lights while the beast is out. Same shape TOR uses to keep the Swapper away
        // from the switches (UsablesPatch.cs:295-303), so both postfixes simply run one after another.
        [HarmonyPatch(typeof(SwitchMinigame), nameof(SwitchMinigame.Begin))]
        static class SwitchBeginPatch {
            public static void Postfix(SwitchMinigame __instance) {
                try {
                    if (WolfDarkActive() && __instance != null) __instance.Close();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Werewolf] switch block failed: {e.Message}");
                }
            }
        }

        // ====================================================================
        // Wolf-form restrictions (option 1516): no vent, no report
        // ====================================================================

        // Postfix (not a competing prefix): TOR replaces Vent.CanUse wholesale with a prefix that
        // returns false, so this postfix gets the final word on canUse/couldUse. TOR's Vent.Use prefix
        // consults CanUse before venting, so the block reaches the actual vent attempt too.
        [HarmonyPatch(typeof(Vent), nameof(Vent.CanUse))]
        static class VentBlockPatch {
            public static void Postfix(ref float __result,
                                       [HarmonyArgument(0)] NetworkedPlayerInfo pc,
                                       [HarmonyArgument(1)] ref bool canUse,
                                       [HarmonyArgument(2)] ref bool couldUse) {
                try {
                    if (!wolfForm || !RestrictionsOn() || werewolf == null || pc == null) return;
                    if (pc.PlayerId != werewolf.PlayerId) return;
                    canUse = couldUse = false;
                    __result = float.MaxValue;
                } catch { }
            }
        }

        // A beast does not sabotage. Exactly the shape TOR uses to keep the Janitor away from the
        // sabotage map (UsablesPatch.cs:205-215) - a Refresh postfix, so the button is re-disabled
        // right after the game re-enables it, instead of racing it from HudManager.Update.
        [HarmonyPatch(typeof(SabotageButton), nameof(SabotageButton.Refresh))]
        static class SabotageBlockPatch {
            public static void Postfix() {
                try {
                    if (!wolfForm || !RestrictionsOn() || !IsLocalWerewolf()) return;
                    FastDestroyableSingleton<HudManager>.Instance.SabotageButton.SetDisabled();
                } catch { }
            }
        }

        // A beast does not file reports. Returning false here only skips the ORIGINAL DoClick, which is
        // exactly the intent (TOR's own prefix on the same method does the same for handcuffs).
        [HarmonyPatch(typeof(ReportButton), nameof(ReportButton.DoClick))]
        static class ReportBlockPatch {
            public static bool Prefix() {
                try {
                    if (wolfForm && RestrictionsOn() && IsLocalWerewolf()) return false;
                } catch { }
                return true;
            }
        }

        // ====================================================================
        // Kills: cooldown reduction + blood ring + death of the beast
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        [HarmonyPriority(Priority.Low)] // last postfix: TOR sets its own kill timers before us
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (!active || werewolf == null || target == null || __instance == null) return;

                    // The beast itself dies -> the form dies with it, on every client.
                    if (target.PlayerId == werewolf.PlayerId) { EndFormSilent(); return; }

                    if (!wolfForm || __instance.PlayerId != werewolf.PlayerId) return;

                    // Blood ring + bite, drawn by EVERY client from its own copy of the murder: no RPC
                    // needed because wolfForm is already synced (WEREWOLF_PLAN.md §4.6).
                    Vector2 at = target.GetTruePosition();
                    try {
                        foreach (var db in UnityEngine.Object.FindObjectsOfType<DeadBody>()) {
                            if (db != null && db.ParentId == target.PlayerId) { at = db.transform.position; break; }
                        }
                    } catch { }
                    WerewolfFx.SpawnBloodRing(at);
                    UCAssets.PlayWerewolfKillAt(at);

                    // Reduced kill cooldown, owner client only (killTimer is a local value).
                    if (IsLocalWerewolf())
                        __instance.SetKillTimer(BaseKillCooldown() * (1f - KillReductionValue()));
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] MurderPatch failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Exiled))]
        static class ExiledPatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    if (!active || werewolf == null || __instance == null) return;
                    if (__instance.PlayerId == werewolf.PlayerId) EndFormSilent();
                } catch { }
            }
        }

        // ====================================================================
        // Meeting: the form never survives a meeting
        // ====================================================================
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    EndFormSilent();
                    StopHeartbeat();
                    WerewolfFx.ClearBloodRings(); // the bodies they marked are gone after this meeting
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] MeetingStart failed: {e}");
                }
            }
        }

        // ====================================================================
        // Silver: sheriff bullet (tough rule), traps, deputy handcuffs
        // ====================================================================

        // Every TOR field kill funnels through RPCProcedure.uncheckedMurderPlayer (RPC.cs:480) and the
        // sheriff's shot is sent to every client (Buttons.cs:413-418) before being executed locally.
        // The verdict below only reads state that is identical on all clients, so suppressing the kill
        // here is consistent everywhere - and it MUST be decided here, synchronously: waiting for a
        // round trip would let the murder run on the clients that already processed the RPC.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.uncheckedMurderPlayer))]
        static class SilverBulletPatch {
            public static bool Prefix(byte sourceId, byte targetId) {
                try {
                    if (!active || werewolf == null) return true;
                    if (targetId != werewolf.PlayerId || sourceId == targetId) return true;

                    // Paket W2: a silver bullet can come from EITHER the current Sheriff.sheriff
                    // (unchanged W1 behaviour) OR the Hunter - who loses that slot as soon as a Deputy
                    // is promoted into it (option 1506, Hunter.ApplySetHunter), so the old check alone
                    // would miss him from that moment on.
                    bool bySheriff = Sheriff.sheriff != null && sourceId == Sheriff.sheriff.PlayerId;
                    bool byHunter = Hunter.active && Hunter.hunter != null && sourceId == Hunter.hunter.PlayerId;
                    if (!bySheriff && !byHunter) return true;

                    // Snapshot BEFORE anything below mutates state - the death sequence is keyed on
                    // "was he a wolf at the moment of the shot", not on whatever wolfForm reads after a
                    // forced revert or the murder's own postfixes have already run.
                    bool wasWolf = wolfForm;
                    Vector2 pos = werewolf.GetTruePosition();

                    // THE HUNTER ALWAYS KILLS (user decision 2026-07-25), in either form and in every
                    // silver mode - his silver bolts are the whole point of the role, and the toughness
                    // below is what the crew promoted him to overcome. Only the sheriff's stray shot is
                    // survivable.
                    if (byHunter) {
                        if (wasWolf) WerewolfFx.PlaySilverDeath(pos);
                        return true;
                    }

                    if (SilverMode() != SilverWounds) return true; // Kills / Off -> TOR's behaviour, untouched
                    if (!wasWolf) return true;             // human form: silver is lethal as always
                    if (silverHitsTaken >= 1) return true;  // second sheriff hit: the toughness is spent

                    silverHitsTaken++;
                    ApplyWound();
                    return false;                          // the bullet does not kill - this time
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] SilverBulletPatch failed: {e}");
                    return true;
                }
            }
        }

        // Postfix on the INTERNAL TheOtherRoles.Objects.Trap.triggerTrap, patched by reflection in
        // TryPatch. Public because Harmony has to reach it from outside this class.
        public static void OnTrapperTrap(byte playerId) {
            try {
                if (!active || werewolf == null) return;
                if (TrapperTrapWounds != null && !TrapperTrapWounds.getBool()) return;
                if (playerId != werewolf.PlayerId) return;
                if (!wolfForm) return; // the trap only bites the BEAST, a human impostor just gets stuck
                TrapHitTheWolf();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] OnTrapperTrap failed: {e}");
            }
        }

        // UC's own Saboteur trap (SaboteurTrap.Trigger runs on every client, same as TOR's).
        [HarmonyPatch(typeof(SaboteurTrap), nameof(SaboteurTrap.Trigger))]
        static class SaboteurTrapPatch {
            public static void Postfix(byte playerId) {
                try {
                    if (!active || werewolf == null) return;
                    if (SaboteurTrapWounds != null && !SaboteurTrapWounds.getBool()) return;
                    if (playerId != werewolf.PlayerId || !wolfForm) return;
                    TrapHitTheWolf();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] SaboteurTrapPatch failed: {e}");
                }
            }
        }

        // Silver handcuffs (option 1482, USER variant): being cuffed does not block the transformation,
        // it CANCELS it - the wolf darkness ends early. deputyUsedHandcuffs (RPC.cs:676) runs on every
        // client, so the werewolf's own client is gated in as the single sender of the revert.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.deputyUsedHandcuffs))]
        static class HandcuffPatch {
            public static void Postfix(byte targetId) {
                try {
                    if (!active || werewolf == null || !wolfForm) return;
                    if (DeputyHandcuffsRevert != null && !DeputyHandcuffsRevert.getBool()) return;
                    if (targetId != werewolf.PlayerId) return;
                    if (!IsLocalWerewolf()) return;
                    SendSetForm(false, 0f);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] HandcuffPatch failed: {e}");
                }
            }
        }

        // ====================================================================
        // Role identity
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || werewolf == null || p == null || p != werewolf || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) {
                            __result[i] = WerewolfInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, WerewolfInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Werewolf] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
