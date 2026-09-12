// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Last Words (MODIFIER)
 *
 * The carrier may write one sentence at any time during the round (N opens a small entry box,
 * Enter saves, Esc closes). If they die - killed, ejected, guessed, whatever - the sentence is
 * posted in the NEXT meeting as an anonymous chat bubble named "Last Words" in the modifier's
 * colour, with no avatar. Whoever is dead by then could be the author; with several deaths the
 * crew cannot tell which one wrote it. An impostor Last Words (option, on by default) can leave
 * a lie behind: "It was Blue, I saw them vent."
 *
 * WHERE THE TEXT LIVES. The carrier's client sends the sentence to everyone the moment it is
 * saved (RPC, owner only); every client keeps the latest copy. When a meeting starts and the
 * carrier is dead, EVERY client posts the bubble locally, once - no host step, nothing to
 * disagree about, and a client that joined late simply has no copy (it never had the carrier's
 * words, so it prints nothing). The text is capped (option, 120 characters by default) both at
 * entry and again at receipt.
 *
 * THE BOX is a plain screen-space canvas (the UCColorGrant technique), keyboard read from
 * Input.inputString. While it is open the crewmate is pinned (moveable = false) and every
 * hotkey that could fire from the letters typed is muted: TOR's CustomButton.onClickEvent (the
 * kill/ability buttons) and vanilla's KeyboardJoystick.HandleHud (use/report/kill/map) both get
 * a prefix that returns false while typing. The box does not open while the chat is open, so
 * chat typing never opens it.
 *
 * THE BUBBLE: ChatController.AddChat(LocalPlayer, text) with a flag set, and a Priority.Last
 * postfix on ChatBubble.SetName that, while the flag is set, renames the bubble to "Last Words",
 * tints the name and hides the avatar. Bubbles are pooled, so the same postfix re-enables the
 * avatar on every later reuse.
 *
 * ARCHITECTURE: modifier over any role (the Gambler pattern), host-authoritative pick, custom RPC
 * module 222 on UCRpc.CallId = 230, gated on "everyone has the mod". Options 1675-1678, display
 * RoleId sentinel 233, no draft entry. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class LastWords {
        // ---- Theme ----
        // Quill ink: a light steel blue, lighter and greyer than the King's royal blue.
        public static readonly Color Color = new Color(0.45f, 0.62f, 0.90f);

        // ---- Options (IDs 1675-1678) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption WhoCanBe;            // 0 crew only, 1 anyone
        public static CustomOption MaxLength;

        // ---- Runtime state ----
        public static PlayerControl carrier;
        public static bool active;
        private static byte carrierId = byte.MaxValue;
        private static string words = "";               // latest saved sentence (every client)
        private static bool posted;
        private static float postAt = -1f;              // meeting-start delay so the chat is ready

        // ---- Custom RPC subtypes: module byte 222 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = UnknownsCollectionPlugin.LastWordsRpcId;
        private const byte SubSet = 0;          // playerId (255 = clear)      host -> everyone
        private const byte SubWords = 1;        // playerId, string            carrier -> everyone

        private const int HardCap = 300;
        private static readonly System.Random rnd = new System.Random();

        // ---- Identity (display-only sentinel RoleId, see Gambler / Void / Sleepwalker) ----
        private const RoleId LastWordsRoleId = (RoleId)233;
        private static RoleInfo info;
        public static RoleInfo Info() => info ??= new RoleInfo(
            "Last Words", Color, "Press N to write your last words - they are read out if you die",
            "Your last words are posted after your death", LastWordsRoleId, false, true);

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1675, Types.Modifier, "Last Words",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1676, Types.Modifier, "Last Words Minimum Players To Spawn",
                    5f, 4f, 15f, 1f, SpawnRate);
                WhoCanBe = CustomOption.Create(1677, Types.Modifier, "Last Words Can Be",
                    new string[] { "Crew Only", "Anyone" }, SpawnRate);
                MaxLength = CustomOption.Create(1678, Types.Modifier, "Last Words Maximum Length",
                    120f, 40f, 200f, 20f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[LastWords] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[LastWords] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            UCRpc.Register(RpcId, HandleModuleRpc);
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(CloseBox);

            // Hotkey mutes while the box is open. Both are manual so a renamed vanilla method can
            // never take the whole PatchAll down with it.
            try {
                var m = AccessTools.Method(typeof(TheOtherRoles.Objects.CustomButton), "onClickEvent");
                if (m != null) harmony.Patch(m, prefix: new HarmonyMethod(typeof(LastWords), nameof(MuteWhileTyping)));
                else UnknownsCollectionPlugin.Logger?.LogWarning("[LastWords] CustomButton.onClickEvent not found - ability hotkeys stay live while typing.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[LastWords] CustomButton mute patch failed: {e}");
            }
            try {
                var m = AccessTools.Method(typeof(KeyboardJoystick), "HandleHud");
                if (m != null) harmony.Patch(m, prefix: new HarmonyMethod(typeof(LastWords), nameof(MuteWhileTyping)));
                else UnknownsCollectionPlugin.Logger?.LogWarning("[LastWords] KeyboardJoystick.HandleHud not found - vanilla hotkeys stay live while typing.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[LastWords] KeyboardJoystick mute patch failed: {e}");
            }
        }

        // Prefix for both mutes: false skips the original while the entry box is open.
        public static bool MuteWhileTyping() => !typing;

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalCarrier() =>
            active && carrier != null && PlayerControl.LocalPlayer != null
            && carrier.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static int Cap() => Mathf.Clamp((int)(MaxLength?.getFloat() ?? 120f), 10, HardCap);

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
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[LastWords] SendSet failed: {e}"); }
        }

        private static void SendWords(string text) {
            try {
                var w = BeginRpc(SubWords);
                w.Write(carrierId);
                w.Write(text ?? "");
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyWords(carrierId, text);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[LastWords] SendWords failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSet: {
                        byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("LastWords.Set")) ApplySet(id);
                        break;
                    }
                    case SubWords: {
                        byte id = reader.ReadByte();
                        string text = reader.ReadString();
                        if (UCRpc.RequireOwnerOrHost(carrier, "LastWords.Words")) ApplyWords(id, text);
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[LastWords] HandleRpc failed: {e}");
            }
        }

        private static void ApplySet(byte id) {
            carrier = Helpers.playerById(id);
            active = carrier != null;
            carrierId = active ? id : byte.MaxValue;
            words = "";
            posted = false;
            postAt = -1f;
            if (active) {
                if (IsLocalCarrier()) {
                    UCRevealFx.PlayReveal();
                    ShowHint(UCLocalization.Tr("uc.ui.lastwords.hint_set"));
                }
                UnknownsCollectionPlugin.Logger?.LogInfo($"[LastWords] Last Words: {carrier.Data?.PlayerName}.");
            }
        }

        private static void ApplyWords(byte id, string text) {
            if (!active || id != carrierId) return;
            text = Sanitize(text);
            words = text;
            UnknownsCollectionPlugin.Logger?.LogInfo($"[LastWords] words saved ({words.Length} chars).");
        }

        // Printable characters only, no line breaks, no TMP tags (a "<" would let the author colour
        // or resize the bubble), capped.
        private static string Sanitize(string text) {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text) {
                if (char.IsControl(c)) continue;
                if (c == '<' || c == '>') continue;
                sb.Append(c);
            }
            string s = sb.ToString().Trim();
            int cap = Cap();
            if (s.Length > cap) s = s.Substring(0, cap);
            return s;
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
                    UnknownsCollectionPlugin.Logger?.LogError($"[LastWords] IntroEnd pick failed: {e}");
                }
            }
        }

        // "Anyone" includes neutrals (a Jester's last words are a fine lie too); never on top of
        // another modifier (UCPromotion.HasAnyModifier, TOR's one-modifier rule).
        private static bool IsModifierCandidate(PlayerControl p) {
            try {
                if (!UCPromotion.IsAlive(p) || p.Data.Role == null) return false;
                if (UCPromotion.HasAnyModifier(p)) return false;
                if ((WhoCanBe?.getSelection() ?? 1) == 1) return true;
                if (p.Data.Role.IsImpostor) return false;
                var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
                return info == null || !info.isNeutral;
            } catch { return false; }
        }

        // ---- The entry box (carrier's client) ----
        private static bool typing;
        private static string buffer = "";
        private static GameObject box;
        private static TextMeshProUGUI bodyLabel;
        private static TextMeshProUGUI countLabel;
        private static readonly Dictionary<Color, Sprite> solids = new();

        private static bool CanOpenBox() {
            try {
                if (!IsLocalCarrier()) return false;
                var me = PlayerControl.LocalPlayer;
                if (me?.Data == null || me.Data.IsDead) return false;
                if (ShipStatus.Instance == null) return false;
                if (MeetingHud.Instance != null || ExileController.Instance != null) return false;
                if (Minigame.Instance != null) return false;
                var hud = FastDestroyableSingleton<HudManager>.Instance;
                if (hud == null || hud.Chat == null) return false;
                if (hud.Chat.IsOpenOrOpening) return false;
                return true;
            } catch { return false; }
        }

        private static void Tick() {
            try {
                if (typing) {
                    if (box == null || !IsLocalCarrier() || PlayerControl.LocalPlayer?.Data == null
                        || PlayerControl.LocalPlayer.Data.IsDead || MeetingHud.Instance != null) {
                        CloseBox();
                    } else {
                        ReadKeyboard();
                    }
                } else if (Input.GetKeyDown(KeyCode.N) && CanOpenBox()) {
                    OpenBox();
                }
                TickHint();
                TickPost();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[LastWords] tick failed: {e.Message}");
                CloseBox();
            }
        }

        private static void ReadKeyboard() {
            var me = PlayerControl.LocalPlayer;
            if (me != null) me.moveable = false;
            if (Input.GetKeyDown(KeyCode.Escape)) { CloseBox(); return; }

            string typed = Input.inputString;
            if (string.IsNullOrEmpty(typed)) return;
            bool changed = false;
            int cap = Cap();
            foreach (char c in typed) {
                if (c == '\b') {
                    if (buffer.Length > 0) { buffer = buffer.Substring(0, buffer.Length - 1); changed = true; }
                } else if (c == '\n' || c == '\r') {
                    Save();
                    return;
                } else if (!char.IsControl(c) && c != '<' && c != '>' && buffer.Length < cap) {
                    buffer += c;
                    changed = true;
                }
            }
            if (changed) RefreshBox();
        }

        private static void Save() {
            string text = Sanitize(buffer);
            SendWords(text);
            CloseBox();
            ShowHint(text.Length == 0
                ? UCLocalization.Tr("uc.ui.lastwords.cleared")
                : UCLocalization.Tr("uc.ui.lastwords.saved"));
        }

        private static void OpenBox() {
            CloseBox();
            buffer = words ?? "";
            typing = true;
            try {
                box = Canvas("UCLastWordsBox", 9020);
                var card = Box(box, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                               new Vector2(0f, 150f), new Vector2(780f, 170f), new Color(0.08f, 0.09f, 0.14f, 0.96f));
                var head = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                               new Vector2(0, -12), new Vector2(-36, 30), new Color(0, 0, 0, 0));
                Label(head, UCLocalization.Tr("uc.ui.lastwords.title"), 22, Color,
                      TextAlignmentOptions.Left).fontStyle = FontStyles.Bold;
                var body = Box(card, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f),
                               new Vector2(0, -6), new Vector2(-36, -78), new Color(1f, 1f, 1f, 0.06f));
                bodyLabel = Label(body, "", 20, Color.white, TextAlignmentOptions.TopLeft);
                bodyLabel.margin = new Vector4(10, 8, 10, 8);
                var foot = Box(card, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0),
                               new Vector2(0, 10), new Vector2(-36, 26), new Color(0, 0, 0, 0));
                Label(foot, UCLocalization.Tr("uc.ui.lastwords.hint_keys"), 14,
                      new Color(0.7f, 0.7f, 0.76f), TextAlignmentOptions.Left);
                countLabel = Label(foot, "", 14, new Color(0.7f, 0.7f, 0.76f), TextAlignmentOptions.Right);
                RefreshBox();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[LastWords] box build failed: {e.Message}");
                CloseBox();
            }
        }

        private static void RefreshBox() {
            if (bodyLabel != null) {
                // Blinking caret; only ASCII in HUD text.
                string caret = (Time.time % 1f) < 0.5f ? "|" : " ";
                bodyLabel.text = buffer + caret;
            }
            if (countLabel != null) countLabel.text = $"{buffer.Length}/{Cap()}";
        }

        private static void CloseBox() {
            typing = false;
            buffer = "";
            try { if (box != null) UnityEngine.Object.Destroy(box); } catch { }
            box = null;
            bodyLabel = null;
            countLabel = null;
            try { var me = PlayerControl.LocalPlayer; if (me != null && !typing) me.moveable = true; } catch { }
        }

        // ---- tiny UGUI helpers (UCColorGrant's shapes, static) ----
        private static Sprite Solid(Color c) {
            if (solids.TryGetValue(c, out var s) && s != null) return s;
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, c); tex.Apply();
            var sp = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            tex.hideFlags |= HideFlags.HideAndDontSave;
            sp.hideFlags |= HideFlags.HideAndDontSave;
            solids[c] = sp;
            return sp;
        }

        private static GameObject Canvas(string name, int order) {
            var go = new GameObject(name);
            var c = go.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = order;
            var sc = go.AddComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);
            sc.matchWidthOrHeight = 0.5f;
            return go;
        }

        private static GameObject Box(GameObject parent, Vector2 min, Vector2 max, Vector2 pivot,
                                      Vector2 pos, Vector2 size, Color col) {
            var go = new GameObject("B");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = min; rt.anchorMax = max; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.sprite = Solid(col);
            img.raycastTarget = false;
            return go;
        }

        private static TextMeshProUGUI Label(GameObject parent, string text, float size,
                                             Color col, TextAlignmentOptions align) {
            var go = new GameObject("T");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.sizeDelta = Vector2.zero;
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.color = col; t.alignment = align;
            t.enableWordWrapping = true;
            t.raycastTarget = false;
            return t;
        }

        // ---- HUD hint (carrier only): a short fading line, showFlash's text technique ----
        private static TextMeshPro hintText;
        private static float hintStart;
        private const float HintLife = 5f;

        private static void ShowHint(string line) {
            try {
                var hud = FastDestroyableSingleton<HudManager>.Instance;
                if (hud == null || hud.KillButton == null || hud.KillButton.cooldownTimerText == null) return;
                if (hintText != null) UnityEngine.Object.Destroy(hintText.gameObject);
                hintText = UnityEngine.Object.Instantiate(hud.KillButton.cooldownTimerText, hud.transform);
                hintText.name = "LastWordsHint";
                hintText.text = line;
                hintText.enableWordWrapping = false;
                hintText.transform.localScale = Vector3.one * 0.5f;
                hintText.transform.localPosition += new Vector3(0f, 2f, -69f);
                hintText.color = new Color(Color.r, Color.g, Color.b, 0f);
                hintText.gameObject.SetActive(true);
                hintStart = Time.time;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[LastWords] hint failed: {e.Message}");
            }
        }

        private static void TickHint() {
            if (hintText == null) return;
            float t = Time.time - hintStart;
            float a = Mathf.Clamp01(t / 0.4f) * (1f - Mathf.Clamp01((t - (HintLife - 0.8f)) / 0.8f));
            hintText.color = new Color(Color.r, Color.g, Color.b, a);
            if (t >= HintLife) { UnityEngine.Object.Destroy(hintText.gameObject); hintText = null; }
        }

        // ---- The posting: every client, at the first meeting after the carrier's death ----
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    CloseBox();
                    if (!active || posted || string.IsNullOrEmpty(words)) return;
                    if (carrier == null || carrier.Data == null || !carrier.Data.IsDead) return;
                    postAt = Time.time + 1.5f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[LastWords] meeting start failed: {e}");
                }
            }
        }

        private static void TickPost() {
            if (postAt < 0f || Time.time < postAt) return;
            postAt = -1f;
            if (posted || MeetingHud.Instance == null) return;
            posted = true;
            PostAnonymous("\"" + ApplyChatCensor(words) + "\"");
            UnknownsCollectionPlugin.Logger?.LogInfo("[LastWords] last words posted.");
        }

        // The vanilla chat filter, applied the way vanilla applies it to RECEIVED chat: on every
        // viewer's own client by that viewer's own "Censor Chat" setting (the author never sees the
        // stars). Same word list, same replacement.
        private static string ApplyChatCensor(string text) {
            try {
                if (string.IsNullOrEmpty(text)) return text;
                var mp = AmongUs.Data.DataManager.Settings?.Multiplayer;
                if (mp != null && mp.CensorChat) return BlockedWords.CensorWords(text);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[LastWords] chat censor failed: {e.Message}");
            }
            return text;
        }

        private static bool postingAnonymous;

        private static void PostAnonymous(string text) {
            try {
                var hud = FastDestroyableSingleton<HudManager>.Instance;
                if (hud == null || hud.Chat == null || PlayerControl.LocalPlayer == null) return;
                postingAnonymous = true;
                try { hud.Chat.AddChat(PlayerControl.LocalPlayer, text); }
                finally { postingAnonymous = false; }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[LastWords] post failed: {e.Message}");
                postingAnonymous = false;
            }
        }

        // Bubbles are pooled: the avatar hidden for the anonymous bubble is shown again on the next
        // SetName of that bubble. Priority.Last so TOR's own SetName postfix (impostor-red names for
        // the Spy etc.) cannot recolour ours afterwards.
        [HarmonyPatch(typeof(ChatBubble), nameof(ChatBubble.SetName))]
        [HarmonyPriority(Priority.Last)]
        static class BubbleNamePatch {
            public static void Postfix(ChatBubble __instance) {
                try {
                    if (__instance == null) return;
                    if (postingAnonymous) {
                        if (__instance.NameText != null) {
                            __instance.NameText.text = UCLocalization.Tr("uc.ui.lastwords.sender");
                            __instance.NameText.color = Color;
                        }
                        if (__instance.Player != null) __instance.Player.gameObject.SetActive(false);
                    } else if (__instance.Player != null && !__instance.Player.gameObject.activeSelf) {
                        __instance.Player.gameObject.SetActive(true);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[LastWords] bubble name failed: {e.Message}");
                }
            }
        }

        // ---- Role identity: APPEND, never replace - a modifier rides on top of the real role ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, [HarmonyArgument(1)] bool showModifier,
                                        ref List<RoleInfo> __result) {
                try {
                    if (!active || carrier == null || p == null || p != carrier || __result == null) return;
                    if (!showModifier) return;
                    if (!__result.Contains(Info())) __result.Add(Info());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[LastWords] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            CloseBox();
            try { if (hintText != null) UnityEngine.Object.Destroy(hintText.gameObject); } catch { }
            hintText = null;
            carrier = null;
            active = false;
            carrierId = byte.MaxValue;
            words = "";
            posted = false;
            postAt = -1f;
            postingAnonymous = false;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("LastWords", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("LastWords", FullReset);
        }
    }
}
