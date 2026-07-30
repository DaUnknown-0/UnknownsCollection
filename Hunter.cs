// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Hunter (Paket W, Stufe 2) - the SHERIFF'S ENDGAME against the Werewolf.
 *
 * The Werewolf (Werewolf.cs, Paket W1) is a comeback mechanic: he only gets his power once he is the
 * last living Impostor. The Hunter is the crew's answer to exactly that moment. The instant the board
 * reads "beast alive, every other Impostor dead, the Sheriff still standing", the Sheriff is promoted
 * - publicly, visibly - into The Hunter:
 *
 *   TRIGGER    Host-authoritative, checked in the two death paths (MurderPlayer + Exiled postfix) plus
 *              a cheap 1 Hz host poll that also catches a DISCONNECT of the last other Impostor. Fires
 *              exactly once per round (hostFired) and is broadcast with SubSetHunter, so every client
 *              applies the same promotion from one message.
 *   SKIN       Crew-visible: the Sheriff's cosmetics are replaced by the hand-drawn hunter flipbook
 *              (HunterFx -> UCCharacterSkin, the same renderer-swap mechanic the wolf skin uses),
 *              tinted with the player's own colour so the crew still recognises "our sheriff".
 *   DEPUTY     Option 1506: a living Deputy is promoted into the vacated Sheriff slot through TOR'S OWN
 *              RPCProcedure.deputyPromotes() path - the crew does not lose its sheriff, it gains a
 *              hunter. Deterministic on every client (Deputy.deputy is synced state), so this needs no
 *              RPC of its own.
 *   SIGHT      Option 1504: inside the wolf darkness the Hunter is exempted from the blanket flashlight
 *              radius - he gets the crew radius scaled by his own multiplier. That carve-out lives in
 *              W1's CalculateLightRadius postfix (Werewolf.cs, LightPatch), which calls
 *              FlashlightMultiplierValue() below.
 *   KILL       Sheriff rules with his own animated button (hunter_shoot): the Werewolf and any other
 *              Impostor die, neutral KILLERS die if option 1505 allows it, an innocent shot kills the
 *              Hunter himself. The silver TOUGHNESS rule from W1 is NOT duplicated here - it lives
 *              where every field kill funnels through (Werewolf.SilverBulletPatch), so a wolf-form
 *              beast survives the first bolt and dies to the second no matter who fires it.
 *   GUESS      Option 1507: a Hunter who was already a Guesser keeps his full menu; a Hunter who was
 *              not gets a guess menu with ONLY the Werewolf in it (one shot, wrong guess kills him as
 *              always).
 *
 * WHY THE PATCHES LOOK LIKE THEY DO
 * ---------------------------------
 *  - Sheriff.sheriff is NEVER written by this file. TOR reads that field in a dozen places (armored
 *    check in Helpers.checkMuderAttempt:524, name colours in UpdatePatch:83-92, the deputy handcuff
 *    button, PlayerControlPatch.deputyCheckPromotion:180-190). Clearing it would silently change all
 *    of them - most importantly it would make TOR auto-promote the Deputy even when option 1506 says
 *    NO. Instead the Hunter simply keeps whatever slot TOR gives him and TOR's own sheriff kill button
 *    is neutralised for him by WRAPPING its HasButton delegate (CustomButton.HasButton is a public
 *    field, Objects/CustomButton.cs:28, and Update() bails out on !HasButton() before it even reads the
 *    hotkey, :199-202). One delegate, no state, re-applied per round, undone on reset.
 *  - The trigger's "all non-werewolf Impostors are dead" probe is Werewolf.IsLastImpostor() - the very
 *    same question the wolf's own transform gate asks, so the two can never disagree.
 *  - "Original sheriff" (option 1503) is Sheriff.formerSheriff == null && Sheriff.formerDeputy == null.
 *    The plan called this "Sheriff.fromDeputy" - that field DOES NOT EXIST in TOR. TOR records a
 *    deputy promotion in Sheriff.replaceCurrentSheriff (TheOtherRoles.cs:267-273, sets formerSheriff)
 *    and RPCProcedure.deputyPromotes (RPC.cs:682-690, sets formerDeputy); both being null is exactly
 *    "this is still the sheriff the round started with".
 *  - The kill goes through Helpers.MurderPlayer(killer, target, showAnimation) (Helpers.cs:544) - the
 *    same "broadcast UncheckedMurderPlayer + execute locally" call TOR's own sheriff button uses
 *    (Buttons.cs:413-418), so Werewolf.SilverBulletPatch sees the Hunter's shot on every client.
 *  - HandleGuesser.isGuesser / .remainingShots are patched (postfix / prefix) rather than writing
 *    Guesser.niceGuesser: making the Hunter a real TOR Guesser would put him in the guess grid as a
 *    Guesser, share the global shot counters with a real Guesser and change his endgame role line.
 *    TOR does not patch its own HandleGuesser methods, so a prefix there competes with nobody.
 *  - The werewolf-only guess grid is produced by temporarily swapping RoleInfo.allRoleInfos for a
 *    one-entry list around TOR's private guesserOnClick (reflection patch, Finalizer restores even if
 *    the original throws). TOR builds that grid by iterating exactly that list (MeetingPatch.cs:415).
 *
 * Options: 1502-1507 (see ID-Registry.md), all parented to the Werewolf spawn rate - without a
 * Werewolf there is no Hunter.
 * RPC: SubSetHunter = 3 on the SHARED Werewolf module byte 211 (UCRpc channel 230). There is
 * deliberately no second subtype: the granted-guesser shot budget is decremented on every client by
 * TOR's own guesserShoot -> HandleGuesser.remainingShots(killerId, true) call (RPC.cs:1019), which our
 * prefix intercepts everywhere, so a separate "the hunter guessed" message would only add a way for
 * the clients to disagree.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Hunter {
        // ---- Theme ----
        // Cold silver-steel, deliberately NOT Sheriff gold: the crew should read "something changed".
        public static readonly Color Color = new Color(0.82f, 0.86f, 0.94f, 1f);

        // ---- Options (1502-1507) ----
        public static CustomOption Enabled;               // 1502
        public static CustomOption OnlyOriginalSheriff;   // 1503
        public static CustomOption FlashlightMultiplier;  // 1504
        public static CustomOption CanKillNeutralKillers; // 1505
        public static CustomOption DeputyPromotes;        // 1506
        public static CustomOption Guessing;              // 1507

        // ---- Runtime state (synced by SubSetHunter, therefore identical on every client) ----
        public static PlayerControl hunter;
        public static bool active;
        // Was he a TOR Guesser BEFORE the promotion? Decides whether he keeps his own full menu
        // (option 1507 = "Full Menu If Already Guesser") or gets the werewolf-only one.
        private static bool wasNaturalGuesser;
        // Shot budget of a GRANTED (non-natural) hunter guesser. Kept per client and decremented from
        // the same deterministic call on all of them - see the file header.
        private static int guessShotsLeft;

        // Host-only: the trigger already fired this round (the "exactly once" guard).
        private static bool hostFired;
        private static float nextHostPoll;

        // Owner-client only.
        private static PlayerControl currentTarget;
        private static float nextSkinTry;

        private static TheOtherRoles.Objects.CustomButton killButton;
        // TOR's own sheriff kill button, once its HasButton has been wrapped for this round. Compared by
        // reference so a freshly created button (HudManager.Start runs every round) is re-wrapped.
        // Deliberately NOT cleared in resetVariables - that runs AFTER HudManager.Start.
        private static TheOtherRoles.Objects.CustomButton wrappedSheriffButton;

        // Saved role list while the werewolf-only guess grid is being built (see GuesserGridPatch).
        private static List<RoleInfo> savedRoleInfos;

        // ---- Constants ----
        private const byte RpcId = UnknownsCollectionPlugin.WerewolfRpcId; // shared with the Werewolf
        public const byte SubSetHunter = 3;   // playerId, wasNaturalGuesser(byte 0/1)
        private const int GrantedGuessShots = 1;   // a werewolf-only menu needs exactly one attempt
        private const float HostPollInterval = 1f;
        private const float SkinRetryInterval = 1f;

        // Option 1507 selection order (index 0 is the default - TOR's string[] overload always binds
        // index 0, CustomOptions.cs:78, so the wanted default is simply listed FIRST).
        private const int GuessFullIfGuesser = 0, GuessWerewolfOnly = 1, GuessOff = 2;

        // ---- Role identity ----
        private static RoleInfo hunterInfo;
        public static RoleInfo HunterInfo() => hunterInfo ??= new RoleInfo(
            "Hunter", Color, "Hunt the beast with silver",
            "Hunt the beast with silver", RoleId.Sheriff);

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                // Parented to the WEREWOLF spawn rate: the Hunter has no spawn roll of his own, he is
                // an event inside the Werewolf's round. Without a Werewolf these settings are moot, so
                // they hide together with the rest of the wolf block.
                var parent = Werewolf.SpawnRate;
                Enabled = CustomOption.Create(1502, Types.Impostor, "Hunter Enabled",
                    true, parent);
                OnlyOriginalSheriff = CustomOption.Create(1503, Types.Impostor, "Hunter Only From The Original Sheriff",
                    true, parent);
                // Percent instead of a 1.0-2.5 factor - see the note on Werewolf's 1554: TOR
                // accumulates slider values in float, so a 0.1 step showed up as "1,5000001".
                FlashlightMultiplier = CustomOption.Create(1504, Types.Impostor, "Hunter Flashlight Multiplier (%)",
                    160f, 100f, 250f, 10f, parent);
                CanKillNeutralKillers = CustomOption.Create(1505, Types.Impostor, "Hunter Can Kill Neutral Killers",
                    true, parent);
                DeputyPromotes = CustomOption.Create(1506, Types.Impostor, "Deputy Promotes To Sheriff When The Hunter Rises",
                    true, parent);
                // Verbose, role-specific choice texts on purpose: UCLocalization matches selection
                // strings by their English TEXT across ALL uc.* keys, so a bare "Off" here would
                // silently re-translate every bool option in the mod (same reason as option 1557).
                Guessing = CustomOption.Create(1507, Types.Impostor, "Hunter Guessing",
                    new string[] { "Full Menu If Already Guesser", "Only The Werewolf", "No Hunter Guessing" },
                    parent);

                HunterFx.Init(); // force the FX static ctor (UCFx tick/reset registration)
                UnknownsCollectionPlugin.Logger?.LogInfo("[Hunter] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            try {
                // TheOtherRoles.Patches.MeetingHudPatch is INTERNAL and guesserOnClick is private, so
                // the werewolf-only grid can only be reached by reflection (same idiom Werewolf.cs uses
                // for the internal Objects.Trap). A missing method is not fatal: the Hunter then simply
                // gets TOR's full guess menu instead of the restricted one.
                var meetingPatch = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.MeetingHudPatch");
                var m = meetingPatch?.GetMethod("guesserOnClick",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (m == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        "[Hunter] MeetingHudPatch.guesserOnClick not found - option 1507's werewolf-only menu " +
                        "falls back to the full guesser grid.");
                } else {
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(Hunter).GetMethod(nameof(BeforeGuesserGrid),
                            BindingFlags.Public | BindingFlags.Static)),
                        finalizer: new HarmonyMethod(typeof(Hunter).GetMethod(nameof(AfterGuesserGrid),
                            BindingFlags.Public | BindingFlags.Static)));
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Hunter] Guesser grid hook patched.");
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] TryPatch failed: {e}");
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;

        public static bool IsLocalHunter() =>
            active && hunter != null && PlayerControl.LocalPlayer != null
            && hunter.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        // Called from Werewolf.LightPatch (W1) for the Hunter's own darkness carve-out.
        // Option 1504 stores whole percent (160 = 1.6x) - see the comment at its definition.
        public static float FlashlightMultiplierValue() =>
            FlashlightMultiplier != null ? FlashlightMultiplier.getFloat() / 100f : 1.6f;

        private static int GuessMode() => Guessing != null ? Guessing.getSelection() : GuessFullIfGuesser;

        // "He may guess, but only for the beast." True for a Hunter who was NOT already a Guesser (and
        // for everyone if the host picked "Only The Werewolf").
        private static bool RestrictedGuesser() =>
            active && hunter != null && GuessMode() != GuessOff
            && (GuessMode() == GuessWerewolfOnly || !wasNaturalGuesser);

        // The Hunter's guessing is GRANTED by us (not inherited from a real Guesser role) - only then do
        // we own his shot budget. The guesser game mode is left completely alone.
        private static bool GrantedGuesser(byte playerId) =>
            active && hunter != null && hunter.PlayerId == playerId
            && !wasNaturalGuesser && GuessMode() != GuessOff && !HandleGuesser.isGuesserGm;

        private static bool HuntIsOn() =>
            active && IsAlive(hunter) && Werewolf.active && Werewolf.werewolf != null
            && Werewolf.werewolf.Data != null && !Werewolf.werewolf.Data.Disconnected
            && !Werewolf.werewolf.Data.IsDead;

        // ====================================================================
        // RPC (shared module byte 211, dispatched by Werewolf.HandleModuleRpc)
        // ====================================================================
        private static void SendSetHunter(byte id, bool naturalGuesser) {
            try {
                var w = Werewolf.BeginRpc(SubSetHunter);
                w.Write(id);
                w.Write((byte)(naturalGuesser ? 1 : 0));
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetHunter(id, naturalGuesser);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] SendSetHunter failed: {e}");
            }
        }

        // Withdraw the promotion again (host tooling only - the game itself never un-promotes).
        // ApplySetHunter refuses to run while `active` is set, on purpose: the promotion must happen
        // exactly once per round. So a host tool that reassigns roles cannot simply send a new
        // SubSetHunter - it needs this explicit clear, which travels the same module byte and
        // therefore takes the costume off on EVERY client, not just the host's screen.
        // playerId 255 means "no hunter"; ApplyClearHunter runs the shared ClearState().
        public static void SendClearHunter() {
            try {
                var w = Werewolf.BeginRpc(SubSetHunter);
                w.Write(byte.MaxValue);
                w.Write((byte)0);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetHunter(byte.MaxValue, false);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] SendClearHunter failed: {e}");
            }
        }

        // Runs on EVERY client (the host applies it locally right after sending).
        public static void ApplySetHunter(byte id, bool naturalGuesser) {
            try {
                // 255 = withdrawal (see SendClearHunter). Handled BEFORE the once-per-round guard,
                // which is exactly what it has to bypass; hostFired stays true so UC's own trigger
                // does not re-promote the same sheriff a moment later.
                if (id == byte.MaxValue) {
                    if (!active) return;
                    string was = hunter?.Data?.PlayerName ?? "?";
                    ClearState();
                    hostFired = true;
                    UnknownsCollectionPlugin.Logger?.LogInfo($"[Hunter] promotion withdrawn from {was}.");
                    return;
                }

                if (active) return;                       // the promotion happens exactly once
                var p = Helpers.playerById(id);
                if (p == null) return;

                hunter = p;
                active = true;
                wasNaturalGuesser = naturalGuesser;
                guessShotsLeft = GuessMode() == GuessOff || naturalGuesser ? 0 : GrantedGuessShots;

                // Everyone sees the change - this is the whole point of the role.
                HunterFx.AttachSkin(p);
                nextSkinTry = Time.time + SkinRetryInterval;

                // The crew must not lose its sheriff: TOR's own promotion path, run locally on every
                // client from synced state (Deputy.deputy). Only hand the slot over if the Hunter is
                // actually holding it - with option 1503 off the current sheriff may already BE a
                // promoted deputy, and then there is nothing left to promote.
                if (DeputyPromotes != null && DeputyPromotes.getBool()
                    && Deputy.deputy != null && IsAlive(Deputy.deputy)
                    && Sheriff.sheriff != null && Sheriff.sheriff.PlayerId == p.PlayerId) {
                    RPCProcedure.deputyPromotes();
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Hunter] Deputy promoted into the vacated Sheriff slot.");
                }

                if (IsLocalHunter()) {
                    // A fresh full cooldown at the moment of the promotion: the hunt starts now, not
                    // with whatever was left on the sheriff's timer.
                    if (killButton != null) {
                        killButton.MaxTimer = Mathf.Max(0.1f, Sheriff.cooldown);
                        killButton.Timer = killButton.MaxTimer;
                    }
                    try { Helpers.showFlash(Color, 2.5f, UCLocalization.Tr("uc.ui.hunter.promote_flash")); } catch { }
                }

                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Hunter] {p.Data?.PlayerName} is now The Hunter (natural guesser: {naturalGuesser}).");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] ApplySetHunter failed: {e}");
            }
        }

        // ====================================================================
        // Trigger (host-authoritative)
        // ====================================================================
        private static bool TriggerReady() {
            try {
                if (hostFired || active) return false;
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return false;
                if (Enabled != null && !Enabled.getBool()) return false;
                if (!TeslaVersionHandshake.EveryoneHasMod()) return false;

                // The beast must be alive AND the last Impostor standing - the exact same probe the
                // Werewolf's own transform gate uses, so the two conditions can never disagree.
                if (!Werewolf.active || !IsAlive(Werewolf.werewolf)) return false;
                if (!Werewolf.IsLastImpostor()) return false;

                var s = Sheriff.sheriff;
                if (!IsAlive(s)) return false;
                if (s.PlayerId == Werewolf.werewolf.PlayerId) return false; // paranoia: never the beast
                if (OnlyOriginalSheriff != null && OnlyOriginalSheriff.getBool()
                    && (Sheriff.formerSheriff != null || Sheriff.formerDeputy != null)) return false;
                return true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Hunter] TriggerReady failed: {e.Message}");
                return false;
            }
        }

        private static void CheckTrigger() {
            if (!TriggerReady()) return;
            hostFired = true;                              // set BEFORE sending: exactly once
            var s = Sheriff.sheriff;
            bool natural = Guesser.isGuesser(s.PlayerId);
            UnknownsCollectionPlugin.Logger?.LogInfo("[Hunter] Trigger conditions met - the hunt begins.");
            SendSetHunter(s.PlayerId, natural);
        }

        // Both death paths, exactly as the spec asks. Postfixes: at this point the victim's
        // Data.IsDead is already set, so IsLastImpostor() sees the new board.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        [HarmonyPriority(Priority.Low)]
        static class MurderTriggerPatch {
            public static void Postfix([HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (active && hunter != null && target != null && target.PlayerId == hunter.PlayerId)
                        HunterFx.DetachSkin();             // the hunter falls -> the costume comes off
                    CheckTrigger();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] murder trigger failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Exiled))]
        [HarmonyPriority(Priority.Low)]
        static class ExileTriggerPatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    if (active && hunter != null && __instance != null && __instance.PlayerId == hunter.PlayerId)
                        HunterFx.DetachSkin();
                    CheckTrigger();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] exile trigger failed: {e}");
                }
            }
        }

        // ====================================================================
        // Button
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)] // after TOR's own HudManagerStartPatch created its buttons
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    WrapSheriffButton();

                    var sprite = UCAssets.HunterShootIcon
                        ?? (__instance.KillButton != null && __instance.KillButton.graphic != null
                            ? __instance.KillButton.graphic.sprite : null);
                    killButton = new TheOtherRoles.Objects.CustomButton(
                        OnKillClick,
                        () => active && IsLocalHunter()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => currentTarget != null && PlayerControl.LocalPlayer.CanMove,
                        () => { if (killButton != null) killButton.Timer = killButton.MaxTimer; },
                        sprite,
                        // The slot TOR's own sheriff kill button occupies - which is suppressed for the
                        // Hunter right above, so the row never carries two kill buttons.
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowRight,
                        __instance, KeyCode.Q, false, UCLocalization.Tr("uc.ui.hunter.button_kill"));
                    killButton.MaxTimer = Mathf.Max(0.1f, Sheriff.cooldown);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] Button creation failed: {e}");
                }
            }
        }

        // TOR's sheriffKillButton lives in the INTERNAL TheOtherRoles.HudManagerStartPatch, so the
        // reference is fetched by reflection. HasButton is a public field on the (public) CustomButton
        // type, so the wrap itself is ordinary managed code.
        private static FieldInfo sheriffButtonField;
        private static bool sheriffButtonFieldTried;
        private static bool sheriffButtonMissingLogged;

        private static TheOtherRoles.Objects.CustomButton SheriffKillButton() {
            try {
                if (!sheriffButtonFieldTried) {
                    sheriffButtonFieldTried = true;
                    var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.HudManagerStartPatch");
                    sheriffButtonField = t?.GetField("sheriffKillButton",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                return sheriffButtonField?.GetValue(null) as TheOtherRoles.Objects.CustomButton;
            } catch {
                return null;
            }
        }

        private static void WrapSheriffButton() {
            try {
                var btn = SheriffKillButton();
                if (btn == null) {
                    // One-shot: this also runs from the per-frame driver, so it must never spam.
                    if (!sheriffButtonMissingLogged) {
                        sheriffButtonMissingLogged = true;
                        UnknownsCollectionPlugin.Logger?.LogWarning(
                            "[Hunter] TOR's sheriff kill button not found - a Hunter who keeps the Sheriff slot " +
                            "would see two kill buttons.");
                    }
                    return;
                }
                if (ReferenceEquals(btn, wrappedSheriffButton)) return; // already wrapped this round
                var original = btn.HasButton;
                if (original == null) return;
                btn.HasButton = () => {
                    try { return original() && !IsLocalHunter(); }
                    catch { return original(); }
                };
                wrappedSheriffButton = btn;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Hunter] sheriff button wrap failed: {e.Message}");
            }
        }

        // Sheriff rules, one silver bolt at a time. Mirrors TOR's own sheriff button
        // (Buttons.cs:393-431) so every TOR shield/rewind/armor interaction behaves identically.
        private static void OnKillClick() {
            try {
                if (!active || !IsLocalHunter() || currentTarget == null) return;
                var target = currentTarget;

                MurderAttemptResult result = Helpers.checkMuderAttempt(hunter, target);
                if (result == MurderAttemptResult.SuppressKill) return;

                if (result == MurderAttemptResult.PerformKill) {
                    PlayerControl victim = IsLegalPrey(target) ? target : hunter;   // misfire = self
                    // An armored Hunter survives his own misfire (TOR's sheriff does the same).
                    if (victim.PlayerId == hunter.PlayerId && Helpers.checkArmored(hunter, true, true)) {
                        // armor ate the backfire - no kill at all
                    } else {
                        Helpers.MurderPlayer(hunter, victim, true);
                    }
                }

                if (killButton != null) killButton.Timer = killButton.MaxTimer;
                currentTarget = null;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] kill click failed: {e}");
            }
        }

        // Who the silver is allowed to touch. The beast first - in wolf form the toughness rule from W1
        // may still turn this into a mere wound, which is decided in Werewolf.SilverBulletPatch, not
        // here.
        private static bool IsLegalPrey(PlayerControl t) {
            try {
                if (t == null || t.Data == null) return false;
                if (Werewolf.active && Werewolf.werewolf != null && t.PlayerId == Werewolf.werewolf.PlayerId)
                    return true;
                if (t.Data.Role != null && t.Data.Role.IsImpostor
                    && (t != Mini.mini || Mini.isGrownUp())) return true;
                if (Sheriff.spyCanDieToSheriff && Spy.spy != null && Spy.spy == t) return true;
                // Option 1505 - unlike TOR's sheriff, the Jackal/Sidekick are gated by it too: they ARE
                // the neutral killers the option talks about.
                bool neutrals = CanKillNeutralKillers == null || CanKillNeutralKillers.getBool();
                if (neutrals && (Jackal.jackal == t || Sidekick.sidekick == t)) return true;
                if (neutrals && Helpers.isNeutral(t) && Helpers.isKiller(t)) return true;
                return false;
            } catch {
                return false;
            }
        }

        // ====================================================================
        // Per-frame driver
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    // Host poll (1 Hz): also catches the case the two death postfixes cannot see - the
                    // last other Impostor DISCONNECTING. hostFired keeps it a one-shot.
                    if (!hostFired && Time.time >= nextHostPoll) {
                        nextHostPoll = Time.time + HostPollInterval;
                        CheckTrigger();
                    }
                    if (!active || hunter == null) return;

                    // Self-healing skin: covers the initial attach, the re-attach after a meeting and a
                    // player object that was rebuilt underneath us. Throttled so a missing frame set
                    // does not retry every frame.
                    if (!HunterFx.SkinAttached && IsAlive(hunter) && !InMeeting() && Time.time >= nextSkinTry) {
                        nextSkinTry = Time.time + SkinRetryInterval;
                        HunterFx.AttachSkin(hunter);
                    }

                    if (!IsLocalHunter()) return;

                    // ---- owner client only ----
                    // Re-assert the suppression of TOR's sheriff kill button. HudManager.Start is not
                    // the only place TOR builds it: setCustomButtonCooldowns() re-runs
                    // createButtonsPostfix when the buttons were not initialized yet (Buttons.cs:94-98),
                    // which would hand us a fresh, unwrapped button. The reference compare inside makes
                    // this a no-op in the normal case.
                    WrapSheriffButton();

                    if (killButton != null) killButton.MaxTimer = Mathf.Max(0.1f, Sheriff.cooldown);

                    if (!IsAlive(hunter) || InMeeting()) { currentTarget = null; return; }
                    currentTarget = PlayerControlFixedUpdatePatch.setTarget();
                    if (currentTarget != null)
                        PlayerControlFixedUpdatePatch.setPlayerOutline(currentTarget, Sheriff.color);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] HudUpdate failed: {e}");
                }
            }
        }

        // ====================================================================
        // Meetings: skin off, flavor text on
        // ====================================================================
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    HunterFx.DetachSkin();   // re-attached by the per-frame driver once the meeting ends
                    PostFlavor();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] MeetingStart failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        [HarmonyPriority(Priority.First)] // before UCGuesser touches allRoleInfos again
        static class MeetingClosePatch {
            public static void Postfix() {
                RestoreRoleInfos();
                nextSkinTry = 0f;            // let the driver re-dress the hunter immediately
            }
        }

        // One random line per meeting while the hunt is running, per faction, LOCAL ONLY (the chat entry
        // is never sent anywhere - each player is told their own half of the story).
        private static void PostFlavor() {
            try {
                if (!HuntIsOn()) return;
                var me = PlayerControl.LocalPlayer;
                if (me == null || me.Data == null) return;

                string key;
                if (IsLocalHunter()) key = $"uc.hunter.flavor.hunter{UnityEngine.Random.Range(1, 4)}";
                else if (Werewolf.werewolf != null && me.PlayerId == Werewolf.werewolf.PlayerId)
                    key = $"uc.hunter.flavor.wolf{UnityEngine.Random.Range(1, 5)}";
                else key = $"uc.hunter.flavor.crew{UnityEngine.Random.Range(1, 5)}";

                var hud = HudManager.Instance;
                if (hud != null && hud.Chat != null)
                    hud.Chat.AddChat(me, UCLocalization.Tr(key));
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Hunter] flavor text failed: {e.Message}");
            }
        }

        // ====================================================================
        // Guessing (option 1507)
        // ====================================================================

        // Grant the guess UI without making him a real TOR Guesser. Postfix-only widening: an existing
        // "true" is never touched, and the guesser GAME MODE is left completely alone.
        [HarmonyPatch(typeof(HandleGuesser), nameof(HandleGuesser.isGuesser))]
        static class IsGuesserPatch {
            public static void Postfix(byte playerId, ref bool __result) {
                try {
                    if (__result || HandleGuesser.isGuesserGm) return;
                    if (!active || hunter == null || playerId != hunter.PlayerId) return;
                    if (GuessMode() == GuessOff) return;
                    __result = true;
                } catch { }
            }
        }

        // Own shot budget for a GRANTED hunter guesser, so his shot never eats a real Guesser's
        // remainingShotsEvilGuesser (which is what TOR's default branch would decrement). A prefix is
        // safe here: TOR does not patch its own HandleGuesser methods, so nothing competes with us.
        [HarmonyPatch(typeof(HandleGuesser), nameof(HandleGuesser.remainingShots))]
        static class RemainingShotsPatch {
            public static bool Prefix(byte playerId, bool shoot, ref int __result) {
                try {
                    if (!GrantedGuesser(playerId)) return true;
                    __result = guessShotsLeft;
                    if (shoot) guessShotsLeft = Mathf.Max(0, guessShotsLeft - 1);
                    return false;
                } catch {
                    return true;
                }
            }
        }

        // Werewolf-only grid: TOR builds the guess buttons by iterating RoleInfo.allRoleInfos
        // (MeetingPatch.cs:415), so a one-entry list for the duration of that call produces a menu with
        // exactly one choice. Public because Harmony patches them in from TryPatch by reflection.
        public static void BeforeGuesserGrid() {
            try {
                if (savedRoleInfos != null) return;                 // re-entry guard
                if (!IsLocalHunter() || !RestrictedGuesser()) return;
                var wolf = Werewolf.WerewolfInfo();
                if (wolf == null || !Werewolf.active) return;
                savedRoleInfos = RoleInfo.allRoleInfos;
                RoleInfo.allRoleInfos = new List<RoleInfo> { wolf };
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] guess grid restrict failed: {e}");
                RestoreRoleInfos();
            }
        }

        // Finalizer, not a postfix: it runs even if the original threw, so the global role list can
        // never stay trimmed.
        public static void AfterGuesserGrid() => RestoreRoleInfos();

        private static void RestoreRoleInfos() {
            try {
                if (savedRoleInfos == null) return;
                RoleInfo.allRoleInfos = savedRoleInfos;
                savedRoleInfos = null;
            } catch { }
        }

        // ====================================================================
        // Role identity
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || hunter == null || p == null || p != hunter || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] == null) continue;
                        if (__result[i].roleId == RoleId.Sheriff || __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = HunterInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, HunterInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hunter] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ====================================================================
        // Round reset
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { ClearState(); }
        }

        // The same belt-and-suspenders rule the rest of the mod adopted after the "resetVariables lobby
        // leak": a round that ends without resetVariables must not carry this state into a FOREIGN lobby.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinPatch {
            public static void Postfix() { ClearState(); }
        }

        private static void ClearState() {
            try { HunterFx.DetachSkin(); } catch { }
            RestoreRoleInfos();
            hunter = null;
            active = false;
            wasNaturalGuesser = false;
            guessShotsLeft = 0;
            hostFired = false;
            nextHostPoll = 0f;
            nextSkinTry = 0f;
            currentTarget = null;
            // killButton / wrappedSheriffButton are deliberately KEPT: resetVariables runs AFTER
            // HudManager.Start, so nulling them here would throw away the buttons of the round that is
            // just starting.
        }
    }
}
