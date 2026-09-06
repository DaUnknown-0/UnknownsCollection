// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Void (crew MODIFIER, the second one in this mod after the Gambler)
 *
 * A crewmate the vote cannot reach - once. Everyone votes normally, the icons land, the tally is
 * shown; but if the Void would be the one ejected, the ejection does not happen: the exile screen
 * runs like a skipped vote, only its line reads "The vote vanished into the void." in the Void's
 * colours with a light glitch, and the round goes on. After that the Void is a normal, votable
 * crewmate - the immunity is spent (the design review's "only once per game").
 *
 * The price: the Void's OWN vote never counts (option 1657 can switch that off). The icon still
 * shows where it landed - only the tally ignores it (the Necromancer's thrall tell, documented).
 *
 * AN AFTER DEATH MODIFIER (design decision 2026-09-06): the Void belongs to TOR's VIP / Bait /
 * Bloody family. While TOR's "VIP, Bait & Bloody Are Hidden" (option 1009) is on, the tag is hidden
 * from everyone INCLUDING its carrier until they are dead or the game has ended - the player only
 * learns they were the Void when the vote fails to eject them (or afterwards). No reveal cue while
 * hidden. Its spawn option sits under its own "After Death Modifier" heading in the Modifier tab.
 *
 * WHY THE EJECTION IS REPLACED, NOT SUPPRESSED. The clean cut is the host's RpcVotingComplete: TOR's
 * CheckForEndVoting prefix decides who is exiled and calls it with the verdict. A prefix on it swaps
 * the exiled player for "nobody" (skip, no tie) - so nothing in TOR ever believes the Void died:
 * no lover cascade, no Jester/Lawyer/Prosecutor bookkeeping, no ghost. Everything on the exile
 * screen is purely cosmetic, driven on every client from a host RPC that arrives right before the
 * vote result.
 *
 * THE SCREEN (design decision 2026-09-06: no figure animation, the plain skip screen): the vanilla
 * typewriter line is kept, its text is swapped (completeString AND TOR's GetString path for
 * StringNames.NoExileSkip/Tie, so it lands regardless of when the coroutine reads it) and tinted
 * void-violet. The glitch is two ghost copies of the same text object - one magenta, one deep
 * purple - riding slightly offset behind it like chromatic aberration; every few hundred
 * milliseconds a short burst shoves them further apart, flickers the main colour and replaces a
 * couple of the ghosts' characters with ASCII noise (the HUD font has no exotic glyphs).
 *
 * ARCHITECTURE: modifier over any crew role (the Gambler pattern), host-authoritative pick, custom
 * RPC module 219 on UCRpc.CallId = 230, gated on "everyone has the mod". Options 1655-1657, display
 * RoleId sentinel 231 (Gambler 230), no draft entry (modifiers are not drafted). See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Hazel;
using TMPro;
using UnityEngine;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class VoidModifier {
        // ---- Theme ----
        // Void purple (the Tower-Defense-Simulator "void" look: black, deep purple, hot magenta).
        // Distinct from the Poltergeist's lavender and the Copycat's pink; the King moved to royal
        // blue so the two purples never sit next to each other.
        public static readonly Color Color = new Color(0.62f, 0.22f, 0.95f);
        private static readonly Color VoidMagenta = new Color(1f, 0.25f, 0.90f);
        private static readonly Color VoidDeep = new Color(0.32f, 0.08f, 0.55f);

        // ---- Options (IDs 1655-1657) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption OwnVoteCounts;

        // ---- Runtime state ----
        public static PlayerControl voidPlayer;
        public static bool active;
        private static byte voidPlayerId = byte.MaxValue;
        private static bool immunityUsed;
        private static bool pending;            // host said "the vote hit the Void" - style the next exile screen

        // ---- Custom RPC subtypes: module byte 219 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = UnknownsCollectionPlugin.VoidRpcId;
        private const byte SubSet = 0;          // playerId (255 = clear)      host -> everyone
        private const byte SubTriggered = 1;    // playerId                    host -> everyone

        private static readonly System.Random rnd = new System.Random();

        // ---- Identity (display-only sentinel RoleId, see Gambler) ----
        private const RoleId VoidRoleId = (RoleId)231;
        private static RoleInfo voidInfo;
        public static RoleInfo VoidInfo() => voidInfo ??= new RoleInfo(
            "Void", Color, "The vote cannot reach you - once",
            "One ejection passes through you", VoidRoleId, false, true);

        public static void CreateOptions() {
            try {
                // Own heading in the Modifier tab: this is an AFTER DEATH modifier (TOR's VIP / Bait /
                // Bloody family) - hidden from its carrier while option 1009 hides that family.
                SpawnRate = CustomOption.Create(1655, Types.Modifier, "Void",
                    CustomOptionHolder.rates, null, true, heading: "After Death Modifier (hidden like VIP, Bait & Bloody)");
                SpawnMinPlayers = CustomOption.Create(1656, Types.Modifier, "Void Minimum Players To Spawn",
                    5f, 4f, 15f, 1f, SpawnRate);
                OwnVoteCounts = CustomOption.Create(1657, Types.Modifier, "Void's Own Vote Counts",
                    false, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Void] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Void] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            UCRpc.Register(RpcId, HandleModuleRpc);

            // Vote weight 0 - postfix on TOR's INTERNAL vote counting (the Necromancer/ChanceMod hook).
            try {
                var t = typeof(CustomOption).Assembly
                    .GetType("TheOtherRoles.Patches.MeetingHudPatch+MeetingCalculateVotesPatch");
                var m = t == null ? null : AccessTools.Method(t, "CalculateVotes");
                if (m != null)
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(VoidModifier), nameof(CalculateVotesPostfix)));
                else
                    UnknownsCollectionPlugin.Logger?.LogWarning("[Void] CalculateVotes not found - the Void's own vote would COUNT.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Void] vote patch failed: {e}");
            }
        }

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalVoid() =>
            active && voidPlayer != null && PlayerControl.LocalPlayer != null
            && voidPlayer.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        // ---- RPC ----
        private static MessageWriter BeginRpc(byte subtype) {
            var w = UCRpc.Begin(RpcId);
            w.Write(subtype);
            return w;
        }

        public static void SendSet(byte id) {
            try {
                var w = BeginRpc(SubSet);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySet(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Void] SendSet failed: {e}"); }
        }

        private static void SendTriggered(byte id) {
            try {
                var w = BeginRpc(SubTriggered);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyTriggered(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Void] SendTriggered failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSet: {
                        byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("Void.Set")) ApplySet(id);
                        break;
                    }
                    case SubTriggered: {
                        byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("Void.Triggered")) ApplyTriggered(id);
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Void] HandleRpc failed: {e}");
            }
        }

        private static void ApplySet(byte id) {
            voidPlayer = Helpers.playerById(id);
            active = voidPlayer != null;
            voidPlayerId = active ? id : byte.MaxValue;
            immunityUsed = false;
            pending = false;
            if (active) {
                // A modifier rides on top of a role; no Claim(): the Void may share a player with a UC
                // role, exactly like the Gambler. The reveal cue only plays when the tag is visible to
                // its carrier - an after-death modifier that is hidden must stay silent, or the cue
                // itself would tell the player what they got.
                if (IsLocalVoid() && AfterDeathModifiersVisible()) UCRevealFx.PlayReveal();
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Void] The Void is {voidPlayer.Data?.PlayerName}.");
            }
        }

        // TOR's rule for its after-death modifiers (RoleInfo.getRoleInfoForPlayer): VIP, Bait and
        // Bloody are hidden - from their own carrier too - while option 1009 is on, unless the local
        // player is dead or the game has ended. The Void follows exactly that rule.
        private static bool AfterDeathModifiersVisible() {
            try {
                if (CustomOptionHolder.modifiersAreHidden == null || !CustomOptionHolder.modifiersAreHidden.getBool()) return true;
                if (PlayerControl.LocalPlayer?.Data != null && PlayerControl.LocalPlayer.Data.IsDead) return true;
                return AmongUsClient.Instance != null
                       && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Ended;
            } catch { return true; }
        }

        private static void ApplyTriggered(byte id) {
            if (!active || id != voidPlayerId) return;
            immunityUsed = true;
            pending = true;
            UnknownsCollectionPlugin.Logger?.LogInfo("[Void] the vote hit the Void - immunity spent, the exile screen will read void.");
        }

        // ---- Pick (host; the modifier has no draft entry) ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPickPatch {
            public static void Postfix() {
                try {
                    if (!AmHost()) return;
                    if (active) return;   // forced by host tooling before the intro ended
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 5f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(IsModifierCandidate).ToList();
                    if (candidates.Count == 0) return;
                    SendSet(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Void] IntroEnd pick failed: {e}");
                }
            }
        }

        // Crew only (design decision): no impostor, no neutral - whatever crew role they already have.
        private static bool IsModifierCandidate(PlayerControl p) {
            try {
                if (!UCPromotion.IsAlive(p) || p.Data.Role == null || p.Data.Role.IsImpostor) return false;
                var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
                if (info != null && info.isNeutral) return false;
                return true;
            } catch { return false; }
        }

        // ---- The Void's own vote: weight 0 (postfix on TOR's CalculateVotes, manual patch) ----
        public static void CalculateVotesPostfix([HarmonyArgument(0)] MeetingHud hud,
                                                 ref Dictionary<byte, int> __result) {
            try {
                if (!active || hud == null || __result == null) return;
                if (OwnVoteCounts?.getBool() ?? false) return;
                foreach (var ps in hud.playerStates) {
                    if (ps == null || ps.AmDead || !ps.DidVote) continue;
                    if (ps.TargetPlayerId != voidPlayerId) continue;
                    byte votedFor = ps.VotedFor;
                    if (votedFor == 252 || votedFor == 254 || votedFor == 255) continue;
                    int weight = (Mayor.mayor != null && Mayor.mayor.PlayerId == ps.TargetPlayerId
                                  && Mayor.voteTwice) ? 2 : 1;
                    if (!__result.TryGetValue(votedFor, out int cur)) continue;
                    int next = cur - weight;
                    // Remove instead of writing 0 (TOR's MaxPair starts at int.MinValue).
                    if (next <= 0) __result.Remove(votedFor);
                    else __result[votedFor] = next;
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Void] CalculateVotes postfix failed: {e}");
            }
        }

        // ---- The immunity: the host's verdict is rewritten before it leaves the host ----
        // Only the host ever calls RpcVotingComplete (TOR's CheckForEndVoting prefix runs there), so
        // this prefix is host-side by construction. The exile RPC that follows carries "nobody".
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.RpcVotingComplete))]
        [HarmonyPriority(Priority.High)]
        static class VotingCompletePatch {
            public static void Prefix([HarmonyArgument(1)] ref NetworkedPlayerInfo exiled,
                                      [HarmonyArgument(2)] ref bool tie) {
                try {
                    if (!active || immunityUsed || exiled == null) return;
                    if (exiled.PlayerId != voidPlayerId) return;
                    if (!IsAlive(voidPlayer)) return;
                    immunityUsed = true;
                    SendTriggered(voidPlayerId);
                    exiled = null;
                    tie = false;
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Void] ejection of the Void voided (host).");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Void] voting-complete prefix failed: {e}");
                }
            }
        }

        // ---- The exile screen: the skip line, in void colours, with a light glitch ----
        private static TextMeshPro mainText;
        private static Color mainOriginalColor;
        private static Vector3 mainHome;
        private static TextMeshPro ghostA;      // magenta, pushed left/up
        private static TextMeshPro ghostB;      // deep purple, pushed right/down
        private static bool styling;
        private static float styleStart;
        private static float nextBurst;
        private static float burstUntil;
        private static string noisyGhostText;
        private static bool tickRegistered;
        private const string Noise = "#%&/|_-=+<>*";   // plain ASCII only - the HUD font has nothing else

        [HarmonyPatch(typeof(ExileController), nameof(ExileController.BeginForGameplay))]
        [HarmonyPriority(Priority.Last)]   // after vanilla Begin AND after TOR's prefix bookkeeping
        static class ExileBeginPatch {
            public static void Postfix(ExileController __instance) {
                try {
                    if (!pending) return;
                    pending = false;
                    if (__instance == null) return;
                    StartStyling(__instance);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Void] exile styling failed: {e}");
                    StopStyling();
                }
            }
        }

        // TOR's own GetString postfix rewrites the exile line for real ejections; for our skip the
        // vanilla "No one was ejected" line is replaced here. Priority.Last so it has the final word.
        [HarmonyPatch(typeof(TranslationController), nameof(TranslationController.GetString),
            new Type[] { typeof(StringNames), typeof(Il2CppReferenceArray<Il2CppSystem.Object>) })]
        [HarmonyPriority(Priority.Last)]
        static class ExileTextPatch {
            public static void Postfix(ref string __result, [HarmonyArgument(0)] StringNames id) {
                try {
                    if (!styling) return;
                    if (id == StringNames.NoExileSkip || id == StringNames.NoExileTie)
                        __result = UCLocalization.Tr("uc.ui.void.exile_text");
                } catch { }
            }
        }

        private static void StartStyling(ExileController ctrl) {
            StopStyling();
            mainText = ctrl.Text;
            if (mainText == null) return;
            try { ctrl.completeString = UCLocalization.Tr("uc.ui.void.exile_text"); } catch { }
            mainOriginalColor = mainText.color;
            mainHome = mainText.transform.localPosition;
            mainText.color = Color;

            ghostA = MakeGhost(mainText, "VoidGhostA", VoidMagenta);
            ghostB = MakeGhost(mainText, "VoidGhostB", VoidDeep);

            styling = true;
            styleStart = Time.time;
            nextBurst = Time.time + 0.6f;
            burstUntil = 0f;
            noisyGhostText = null;
            if (!tickRegistered) { tickRegistered = true; UCFx.RegisterTick(Tick); UCFx.RegisterReset(StopStyling); }
            UnknownsCollectionPlugin.Logger?.LogInfo("[Void] exile line styled.");
        }

        // A sibling copy of the exile text object: same font, size and alignment, drawn a hair
        // behind the original (local z), tinted, never receiving the typewriter itself - Tick mirrors
        // the visible characters over every frame.
        private static TextMeshPro MakeGhost(TextMeshPro src, string name, Color tint) {
            try {
                var go = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
                go.name = name;
                var tmp = go.GetComponent<TextMeshPro>();
                if (tmp == null) { UnityEngine.Object.Destroy(go); return null; }
                tmp.text = "";
                tmp.color = new Color(tint.r, tint.g, tint.b, 0.35f);
                go.transform.localPosition = mainHome + new Vector3(0f, 0f, 0.01f);
                go.transform.localScale = src.transform.localScale;
                return tmp;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Void] ghost text failed: {e.Message}");
                return null;
            }
        }

        private static void Tick() {
            if (!styling) return;
            try {
                if (mainText == null || ExileController.Instance == null) { StopStyling(); return; }
                float now = Time.time;
                string shown = mainText.text ?? "";

                // Bursts: every 0.35-0.9 s a 60-140 ms jolt.
                if (now >= nextBurst) {
                    burstUntil = now + 0.06f + (float)rnd.NextDouble() * 0.08f;
                    nextBurst = burstUntil + 0.35f + (float)rnd.NextDouble() * 0.55f;
                    noisyGhostText = Corrupt(shown);
                }
                bool burst = now < burstUntil;

                // Main line: void violet, a magenta flicker during a burst, a slow breathing pulse
                // otherwise (so the colour never looks static even between jolts).
                float breathe = 0.85f + 0.15f * Mathf.Sin((now - styleStart) * 5.5f);
                Color main = burst ? Color.Lerp(Color, VoidMagenta, 0.6f) : Color * breathe;
                mainText.color = new Color(main.r, main.g, main.b, 1f);
                Vector3 jolt = burst
                    ? new Vector3(((float)rnd.NextDouble() - 0.5f) * 0.06f, ((float)rnd.NextDouble() - 0.5f) * 0.03f, 0f)
                    : Vector3.zero;
                mainText.transform.localPosition = mainHome + jolt;

                // Ghosts: chromatic-aberration offsets, wider and noisier during a burst.
                float spread = burst ? 0.09f : 0.025f;
                float ghostAlpha = burst ? 0.75f : 0.30f;
                string ghostText = burst && noisyGhostText != null && noisyGhostText.Length == shown.Length
                    ? noisyGhostText : shown;
                if (ghostA != null) {
                    ghostA.text = ghostText;
                    ghostA.color = new Color(VoidMagenta.r, VoidMagenta.g, VoidMagenta.b, ghostAlpha);
                    ghostA.transform.localPosition = mainHome + new Vector3(-spread, spread * 0.35f, 0.01f) + jolt * 0.5f;
                }
                if (ghostB != null) {
                    ghostB.text = burst ? shown : ghostText;
                    ghostB.color = new Color(VoidDeep.r * 1.6f, VoidDeep.g * 1.6f, VoidDeep.b * 1.6f, ghostAlpha);
                    ghostB.transform.localPosition = mainHome + new Vector3(spread, -spread * 0.35f, 0.01f) - jolt * 0.5f;
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Void] exile glitch tick failed: {e.Message}");
                StopStyling();
            }
        }

        // Replace one to three visible, non-space characters with ASCII noise.
        private static string Corrupt(string s) {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s);
            int n = 1 + rnd.Next(3);
            for (int k = 0; k < n; k++) {
                int i = rnd.Next(sb.Length);
                if (sb[i] == ' ') continue;
                sb[i] = Noise[rnd.Next(Noise.Length)];
            }
            return sb.ToString();
        }

        private static void StopStyling() {
            styling = false;
            try {
                if (mainText != null) {
                    mainText.color = mainOriginalColor;
                    mainText.transform.localPosition = mainHome;
                }
            } catch { }
            try { if (ghostA != null) UnityEngine.Object.Destroy(ghostA.gameObject); } catch { }
            try { if (ghostB != null) UnityEngine.Object.Destroy(ghostB.gameObject); } catch { }
            ghostA = null;
            ghostB = null;
            mainText = null;
            noisyGhostText = null;
        }

        [HarmonyPatch(typeof(ExileController), nameof(ExileController.WrapUp))]
        static class ExileWrapUpPatch {
            public static void Prefix() { StopStyling(); }
        }

        // ---- Role identity: APPEND, never replace - a modifier rides on top of the real role ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, [HarmonyArgument(1)] bool showModifier,
                                        ref List<RoleInfo> __result) {
                try {
                    if (!active || voidPlayer == null || p == null || p != voidPlayer || __result == null) return;
                    if (!showModifier) return;
                    if (!AfterDeathModifiersVisible()) return;   // hidden like VIP/Bait/Bloody
                    if (!__result.Contains(VoidInfo())) __result.Add(VoidInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Void] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            StopStyling();
            voidPlayer = null;
            active = false;
            voidPlayerId = byte.MaxValue;
            immunityUsed = false;
            pending = false;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Void", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("Void", FullReset);
        }
    }
}
