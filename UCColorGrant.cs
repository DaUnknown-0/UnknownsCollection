// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCColorGrant - the host can put a player into ANY colour, given as a hex code, if that player
 * says yes.
 *
 * WHY CONSENT IS THE WHOLE DESIGN, NOT A COURTESY
 * A colour is the one thing in a lobby that belongs to the player rather than to the round. A host
 * who can simply reassign it can grief with it, and there is no undo the player controls. So the
 * host never sets a colour here: he ASKS. The request travels to the target, the target answers,
 * and only an accepted answer is carried out - by the host, because colour assignment is
 * host-authoritative in Among Us. A declined or unanswered request changes nothing at all.
 *
 * HOW AN ARBITRARY HEX BECOMES A COLOUR EVERYBODY SEES
 * RpcSetColor sends an INDEX, never an RGB value, so a free colour cannot be sent as a colour. It
 * is sent as a SLOT instead: UCColors appends a block of empty palette slots, and the sequence on
 * an accepted request is
 *      1. host picks a free slot,
 *      2. host broadcasts "slot N is now #RRGGBB" - every client with this mod writes it,
 *      3. host calls RpcSetColor(N).
 * Step 2 has to come first and has to reach everyone, or somebody renders the player in whatever
 * that slot held before.
 *
 * WHO CAN BE ASKED
 * Only a player who has this mod, because the prompt IS this mod - somebody without it would never
 * see the question and would look to the host like a player ignoring him. The host's list says so
 * per player rather than letting him wonder. And the whole feature stands down unless EVERY client
 * has the mod: a client without it has a shorter palette, so the slot index is past the end of its
 * array. UCColors' lobby guard is the second half of that rule.
 *
 * LOBBY ONLY. Changing a colour mid-round rewrites who people think they are looking at.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Hazel;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.UI;

namespace UnknownsCollection {
    public static class UCColorGrant {

        public const byte RpcId = 217;      // UC module byte on channel 230
        private const byte SubRequest = 0;  // host -> target: would you take this colour?
        private const byte SubAnswer  = 1;  // target -> host: yes / no
        private const byte SubSetSlot = 2;  // host -> everyone: slot N is now this colour

        /// The colour the LOCAL player is being asked about, if any.
        public static bool HasPending { get; private set; }
        public static Color32 PendingColour { get; private set; }
        /// What the host is waiting for, so his list can show it.
        public static readonly Dictionary<byte, Color32> Outstanding = new();

        public static void RegisterRpc() => UCRpc.Register(RpcId, Handle);

        // ================================================================================
        // Rules
        // ================================================================================
        public static bool InLobby() =>
            AmongUsClient.Instance != null && !AmongUsClient.Instance.IsGameStarted
            && ShipStatus.Instance == null;

        /// Does this player have the mod? Only such a player can be shown the question.
        public static bool HasMod(PlayerControl p) {
            try {
                if (p == null) return false;
                if (p == PlayerControl.LocalPlayer) return true;
                var clients = AmongUsClient.Instance?.allClients;
                if (clients == null) return false;
                for (int i = 0; i < clients.Count; i++) {
                    var c = clients[i];
                    if (c == null || c.Character == null || c.Character.PlayerId != p.PlayerId) continue;
                    return TeslaVersionHandshake.playerVersions.ContainsKey(c.Id);
                }
                return false;
            } catch { return false; }
        }

        /// Can a free colour be handed out at all right now?
        public static bool Available() => UCColors.Installed && UCColors.Safe() && InLobby();

        // ================================================================================
        // Sending
        // ================================================================================
        public static void Ask(PlayerControl target, Color32 rgb) {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            if (!Available() || target == null || !HasMod(target)) return;

            Outstanding[target.PlayerId] = rgb;
            var w = UCRpc.Begin(RpcId);
            w.Write(SubRequest);
            w.Write(target.PlayerId);
            w.Write(rgb.r); w.Write(rgb.g); w.Write(rgb.b);
            AmongUsClient.Instance.FinishRpcImmediately(w);
            ReceiveRequest(target.PlayerId, rgb);          // the host may be asking himself
        }

        public static void Answer(bool accepted) {
            if (!HasPending || PlayerControl.LocalPlayer == null) return;
            var rgb = PendingColour;
            HasPending = false;

            var w = UCRpc.Begin(RpcId);
            w.Write(SubAnswer);
            w.Write(PlayerControl.LocalPlayer.PlayerId);
            w.Write(rgb.r); w.Write(rgb.g); w.Write(rgb.b);
            w.Write((byte)(accepted ? 1 : 0));
            AmongUsClient.Instance.FinishRpcImmediately(w);
            ReceiveAnswer(PlayerControl.LocalPlayer.PlayerId, rgb, accepted);
        }

        // ================================================================================
        // Receiving
        // ================================================================================
        private static void Handle(MessageReader r) {
            try {
                byte sub = r.ReadByte();
                if (sub == SubRequest) {
                    // Only the host may ask. Without this, any client could pop the prompt on
                    // anybody - the consent would be real but the asker would not be.
                    if (!UCRpc.SenderIsHost) return;
                    byte id = r.ReadByte();
                    var rgb = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), byte.MaxValue);
                    ReceiveRequest(id, rgb);
                } else if (sub == SubAnswer) {
                    byte who = r.ReadByte();
                    var rgb = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), byte.MaxValue);
                    bool ok = r.ReadByte() != 0;
                    // Only the player themselves may answer for themselves.
                    var sender = UCRpc.Sender;
                    if (sender == null || sender.PlayerId != who) return;
                    ReceiveAnswer(who, rgb, ok);
                } else if (sub == SubSetSlot) {
                    if (!UCRpc.SenderIsHost) return;
                    byte slot = r.ReadByte();
                    var rgb = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), byte.MaxValue);
                    UCColors.SetSlot(slot, rgb);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCColorGrant] rpc failed: {e}");
            }
        }

        private static void ReceiveRequest(byte targetId, Color32 rgb) {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.PlayerId != targetId) return;
            if (!InLobby()) return;
            PendingColour = rgb;
            HasPending = true;
        }

        private static void ReceiveAnswer(byte who, Color32 rgb, bool accepted) {
            Outstanding.Remove(who);
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

            var target = PlayerControl.AllPlayerControls.ToArray()
                                      .FirstOrDefault(p => p != null && p.PlayerId == who);
            if (target == null) return;

            if (!accepted) {
                Notify(UCLocalization.Tr("uc.colorgrant.declined", target.Data?.PlayerName ?? "?"));
                return;
            }
            // Re-checked on arrival, not just before sending: the room can change while the player
            // is deciding - somebody without the mod may have joined, or the slots may have filled.
            if (!Available()) {
                Notify(UCLocalization.Tr("uc.colorgrant.unavailable", target.Data?.PlayerName ?? "?"));
                return;
            }
            int slot = UCColors.IsCustom(target.Data.DefaultOutfit.ColorId)
                       ? target.Data.DefaultOutfit.ColorId      // already in one: recolour it
                       : UCColors.FreeSlot();
            if (slot < 0) {
                Notify(UCLocalization.Tr("uc.colorgrant.no_slot", target.Data?.PlayerName ?? "?"));
                return;
            }

            // Fill the slot EVERYWHERE before anybody is put into it, or a client renders whatever
            // that slot held a moment ago.
            BroadcastSlot(slot, rgb);

            target.RpcSetColor((byte)slot);
            // Recorded so the host can put this back: a late joiner never received the slot, and a
            // colour index can come back changed from a round (UCColors.LobbyGuardPatch.Restore).
            UCColors.RememberGrant(target, slot);
            UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[UCColorGrant] {target.Data?.PlayerName} accepted #{rgb.r:X2}{rgb.g:X2}{rgb.b:X2} in slot {slot}.");
        }

        /// Tells everyone what a slot holds, and writes it locally too. Used when a colour is
        /// granted and again whenever the host has to restore one.
        public static void BroadcastSlot(int slot, Color32 rgb) {
            try {
                if (AmongUsClient.Instance == null) return;
                var w = UCRpc.Begin(RpcId);
                w.Write(SubSetSlot);
                w.Write((byte)slot);
                w.Write(rgb.r); w.Write(rgb.g); w.Write(rgb.b);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                UCColors.SetSlot(slot, rgb);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCColorGrant] BroadcastSlot failed: {e}");
            }
        }

        private static void Notify(string text) {
            try {
                var hud = HudManager.Instance;
                if (hud != null && hud.Notifier != null) hud.Notifier.AddDisconnectMessage(text);
            } catch { }
        }

        // ================================================================================
        // Hex
        // ================================================================================
        /// Parses "#RRGGBB", "RRGGBB" or "RGB". Null when it is not a colour yet - the entry screen
        /// uses that to keep the send button off while the host is still typing.
        public static Color32? ParseHex(string s) {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim().TrimStart('#');
            try {
                if (s.Length == 3) {
                    int r = Convert.ToInt32($"{s[0]}{s[0]}", 16);
                    int g = Convert.ToInt32($"{s[1]}{s[1]}", 16);
                    int b = Convert.ToInt32($"{s[2]}{s[2]}", 16);
                    return new Color32((byte)r, (byte)g, (byte)b, byte.MaxValue);
                }
                if (s.Length == 6)
                    return new Color32(Convert.ToByte(s.Substring(0, 2), 16),
                                       Convert.ToByte(s.Substring(2, 2), 16),
                                       Convert.ToByte(s.Substring(4, 2), 16), byte.MaxValue);
            } catch { }
            return null;
        }

        public static string ToHex(Color32 c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        internal static class ResetPatch {
            public static void Postfix() { HasPending = false; Outstanding.Clear(); }
        }
    }

    /*
     * The three screens: the host's player list, the hex entry, and the question the target gets.
     * Plain screen-space canvases built on demand, the same shape UTS' NewcomerShieldUI uses.
     */
    public class UCColorGrantUI : MonoBehaviour {
        public static UCColorGrantUI Instance { get; private set; }
        public UCColorGrantUI(IntPtr ptr) : base(ptr) { }

        private static readonly Dictionary<Color, Sprite> solids = new();
        private GameObject lobbyButton, panel, prompt;
        private bool promptShown;
        private float nextPoll;

        // Hex entry state. `typing` is what makes Update read the keyboard.
        private bool typing;
        private byte hexTarget;
        private string hexBuffer = "";
        private TMPro.TextMeshProUGUI hexLabel, hexHint;
        private GameObject hexPreview;

        /// A few colours worth one click. Purpur is the one this feature started as.
        private static readonly (string name, Color32 col)[] Presets = {
            ("Purpur", UCColors.Purpur),
            ("Gold",   new Color32(0xFF, 0xC1, 0x07, 0xFF)),
            ("Mint",   new Color32(0x3D, 0xDC, 0x97, 0xFF)),
            ("Ice",    new Color32(0x8E, 0xD6, 0xFF, 0xFF)),
            ("Rose",   new Color32(0xFF, 0x6F, 0x91, 0xFF)),
            ("Kohle",  new Color32(0x2B, 0x2B, 0x33, 0xFF)),
        };

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
        }

        // ---------------------------------------------------------------- tiny UGUI helpers
        [HideFromIl2Cpp]
        private static Sprite Solid(Color c) {
            if (solids.TryGetValue(c, out var s) && s != null) return s;
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, c); tex.Apply();
            var sp = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            DontDestroyOnLoad(tex); DontDestroyOnLoad(sp);
            solids[c] = sp;
            return sp;
        }

        [HideFromIl2Cpp]
        private static GameObject Canvas(string name, int order) {
            var go = new GameObject(name);
            DontDestroyOnLoad(go);
            var c = go.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = order;
            var sc = go.AddComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);
            sc.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            return go;
        }

        [HideFromIl2Cpp]
        private static GameObject Box(GameObject parent, Vector2 min, Vector2 max, Vector2 pivot,
                                      Vector2 pos, Vector2 size, Color col) {
            var go = new GameObject("B");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = min; rt.anchorMax = max; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            go.AddComponent<Image>().sprite = Solid(col);
            return go;
        }

        [HideFromIl2Cpp]
        private static TMPro.TextMeshProUGUI Label(GameObject parent, string text, float size,
                                                   Color col, TMPro.TextAlignmentOptions align) {
            var go = new GameObject("T");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.sizeDelta = Vector2.zero;
            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.color = col; t.alignment = align;
            t.enableWordWrapping = true;
            return t;
        }

        [HideFromIl2Cpp]
        private static void OnClick(GameObject go, Action a) =>
            go.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)a);

        // ---------------------------------------------------------------- the crewmate preview
        /*
         * A flat swatch answers "which colour" but not the question anybody actually has, which is
         * "what will I look like". The game's own answer would be a PoolablePlayer, but that is a
         * world-space object whose prefab only exists on certain screens, and these panels are
         * screen-space UGUI. So the silhouette is generated once into two alpha masks - body and
         * visor - and every preview is those two masks tinted. Shaded with the colour AND its
         * darker tone, the way the game shades a bean; with only the bright one every dark colour
         * would look alike.
         */
        private static Sprite bodyMask, visorMask;

        [HideFromIl2Cpp]
        private static Sprite BuildMask(bool visor) {
            const int S = 96;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++) {
                    float u = x / (float)S, v = y / (float)S;      // v = 0 at the bottom
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, visor ? Visor(u, v) : Body(u, v)));
                }
            tex.Apply();
            var sp = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f));
            DontDestroyOnLoad(tex); DontDestroyOnLoad(sp);
            return sp;
        }

        [HideFromIl2Cpp]
        private static float Body(float u, float v) =>
            Mathf.Clamp01(Mathf.Max(Mathf.Max(RoundBox(u, v, 0.16f, 0.10f, 0.62f, 0.86f, 0.22f),
                                              RoundBox(u, v, 0.60f, 0.30f, 0.82f, 0.66f, 0.09f)),
                                    Mathf.Max(RoundBox(u, v, 0.20f, 0.02f, 0.38f, 0.22f, 0.05f),
                                              RoundBox(u, v, 0.44f, 0.02f, 0.62f, 0.22f, 0.05f))));

        [HideFromIl2Cpp]
        private static float Visor(float u, float v) => RoundBox(u, v, 0.30f, 0.58f, 0.68f, 0.80f, 0.10f);

        [HideFromIl2Cpp]
        private static float RoundBox(float u, float v, float x0, float y0, float x1, float y1, float r) {
            float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f;
            float hx = (x1 - x0) * 0.5f - r, hy = (y1 - y0) * 0.5f - r;
            float dx = Mathf.Max(Mathf.Abs(u - cx) - Mathf.Max(hx, 0f), 0f);
            float dy = Mathf.Max(Mathf.Abs(v - cy) - Mathf.Max(hy, 0f), 0f);
            return Mathf.Clamp01(0.5f - (Mathf.Sqrt(dx * dx + dy * dy) - r) / (2f / 96f));
        }

        [HideFromIl2Cpp]
        private static void Preview(GameObject parent, Color body, Color shadow, float size, Vector2 pos) {
            if (bodyMask == null) bodyMask = BuildMask(false);
            if (visorMask == null) visorMask = BuildMask(true);

            void Layer(Sprite sp, Color col, Vector2 off) {
                var go = new GameObject("P");
                go.transform.SetParent(parent.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = pos + off;
                rt.sizeDelta = new Vector2(size, size);
                var img = go.AddComponent<Image>();
                img.sprite = sp; img.color = col; img.raycastTarget = false;
            }
            Layer(bodyMask, shadow, new Vector2(-size * 0.045f, -size * 0.045f));
            Layer(bodyMask, body, Vector2.zero);
            Layer(visorMask, new Color(0.65f, 0.79f, 0.85f), Vector2.zero);
        }

        [HideFromIl2Cpp]
        private static void PreviewOf(GameObject parent, Color32 rgb, float size, Vector2 pos) {
            var sh = UCColors.Darker(rgb);
            Preview(parent, new Color(rgb.r / 255f, rgb.g / 255f, rgb.b / 255f),
                    new Color(sh.r / 255f, sh.g / 255f, sh.b / 255f), size, pos);
        }

        [HideFromIl2Cpp]
        private static void PreviewOfIndex(GameObject parent, int i, float size, Vector2 pos) {
            try {
                if (i >= 0 && i < Palette.PlayerColors.Length) {
                    PreviewOf(parent, Palette.PlayerColors[i], size, pos);
                    return;
                }
            } catch { }
            PreviewOf(parent, new Color32(0x80, 0x80, 0x80, 0xFF), size, pos);
        }

        // ---------------------------------------------------------------- lifecycle
        public void Update() {
            try {
                if (typing) ReadKeyboard();

                if (Time.time < nextPoll) return;
                nextPoll = Time.time + 0.25f;

                bool host = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
                bool show = host && UCColorGrant.InLobby() && UCColors.Installed;
                if (show && lobbyButton == null) BuildLobbyButton();
                if (!show && lobbyButton != null) { Destroy(lobbyButton); lobbyButton = null; ClosePanel(); }

                if (UCColorGrant.HasPending && !promptShown) BuildPrompt();
                if (!UCColorGrant.HasPending && prompt != null) ClosePrompt();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCColorGrant] UI tick failed: {e}");
            }
        }

        /*
         * Keyboard, read the way LobbyPasswordGate reads it: Input.inputString per frame into a
         * buffer. The lobby has no text field to focus, and typing would otherwise walk the
         * crewmate around, so movement is pinned off while the entry screen is up.
         */
        [HideFromIl2Cpp]
        private void ReadKeyboard() {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me != null) me.moveable = false;

                string typed = Input.inputString;
                if (string.IsNullOrEmpty(typed)) return;
                bool changed = false;
                foreach (char c in typed) {
                    if (c == '\b') {
                        if (hexBuffer.Length > 0) { hexBuffer = hexBuffer.Substring(0, hexBuffer.Length - 1); changed = true; }
                    } else if (c == '\n' || c == '\r') {
                        SendHex();
                        return;
                    } else if (Uri.IsHexDigit(c) && hexBuffer.Length < 6) {
                        hexBuffer += char.ToUpperInvariant(c);
                        changed = true;
                    }
                }
                if (changed) RefreshHex();
            } catch { }
        }

        [HideFromIl2Cpp]
        private void BuildLobbyButton() {
            lobbyButton = Canvas("UCColorGrantButton", 9000);
            var b = Box(lobbyButton, Vector2.zero, Vector2.zero, Vector2.zero,
                        new Vector2(28, 140), new Vector2(330, 46), new Color(0.35f, 0.1f, 0.5f, 0.95f));
            Label(b, UCLocalization.Tr("uc.colorgrant.lobby_button"), 18, Color.white,
                  TMPro.TextAlignmentOptions.Center).fontStyle = TMPro.FontStyles.Bold;
            OnClick(b, TogglePanel);
        }

        [HideFromIl2Cpp] public void TogglePanel() { if (panel != null) ClosePanel(); else OpenPanel(); }

        [HideFromIl2Cpp]
        private void ClosePanel() {
            if (panel != null) { Destroy(panel); panel = null; }
            StopTyping();
        }

        [HideFromIl2Cpp]
        private void StopTyping() {
            typing = false; hexLabel = null; hexHint = null; hexPreview = null;
            try { var me = PlayerControl.LocalPlayer; if (me != null) me.moveable = true; } catch { }
        }

        [HideFromIl2Cpp]
        private void ClosePrompt() {
            if (prompt != null) { Destroy(prompt); prompt = null; }
            promptShown = false;
        }

        // ---------------------------------------------------------------- host: the player list
        [HideFromIl2Cpp]
        private void OpenPanel() {
            StopTyping();
            panel = Canvas("UCColorGrantPanel", 9010);
            Box(panel, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero,
                new Color(0, 0, 0, 0.85f));
            var card = Box(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                           Vector2.zero, new Vector2(760, 620), new Color(0.1f, 0.09f, 0.14f, 0.98f));

            var head = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                           new Vector2(0, -14), new Vector2(-40, 34), new Color(0, 0, 0, 0));
            Label(head, UCLocalization.Tr("uc.colorgrant.title"), 24, new Color(0.75f, 0.55f, 1f),
                  TMPro.TextAlignmentOptions.Left).fontStyle = TMPro.FontStyles.Bold;

            var sub = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                          new Vector2(0, -52), new Vector2(-40, 44), new Color(0, 0, 0, 0));
            Label(sub, UCColors.Safe() ? UCLocalization.Tr("uc.colorgrant.subtitle")
                                       : UCLocalization.Tr("uc.colorgrant.blocked"),
                  15, UCColors.Safe() ? new Color(0.7f, 0.7f, 0.76f) : new Color(1f, 0.6f, 0.5f),
                  TMPro.TextAlignmentOptions.TopLeft);

            float y = -104f;
            foreach (var p in PlayerControl.AllPlayerControls) {
                if (p == null || p.Data == null || p.Data.Disconnected) continue;
                BuildRow(card, p, y);
                y -= 46f;
                if (y < -520f) break;
            }

            var close = Box(card, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                            new Vector2(0, 16), new Vector2(150, 38), new Color(0.3f, 0.3f, 0.38f, 0.95f));
            Label(close, UCLocalization.Tr("uc.colorgrant.close"), 17, Color.white,
                  TMPro.TextAlignmentOptions.Center);
            OnClick(close, ClosePanel);
        }

        [HideFromIl2Cpp]
        private void BuildRow(GameObject card, PlayerControl p, float y) {
            var row = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                          new Vector2(0, y), new Vector2(-40, 40), new Color(1, 1, 1, 0.05f));

            int cur = 0;
            try { cur = p.Data.DefaultOutfit.ColorId; } catch { }
            var swatch = Box(row, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                             new Vector2(10, 0), new Vector2(32, 32), new Color(0, 0, 0, 0));
            PreviewOfIndex(swatch, cur, 30f, Vector2.zero);

            var name = Box(row, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                           new Vector2(46, 0), new Vector2(300, 30), new Color(0, 0, 0, 0));
            Label(name, p.Data.PlayerName ?? "?", 17, Color.white, TMPro.TextAlignmentOptions.Left);

            string state = !UCColorGrant.HasMod(p) ? UCLocalization.Tr("uc.colorgrant.no_mod")
                         : UCColorGrant.Outstanding.ContainsKey(p.PlayerId) ? UCLocalization.Tr("uc.colorgrant.waiting")
                         : !UCColors.Safe() ? UCLocalization.Tr("uc.colorgrant.blocked_short")
                         : "";
            if (state != "") {
                var st = Box(row, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                             new Vector2(-12, 0), new Vector2(280, 30), new Color(0, 0, 0, 0));
                Label(st, state, 15, new Color(0.65f, 0.65f, 0.7f), TMPro.TextAlignmentOptions.Right);
                return;
            }

            var btn = Box(row, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                          new Vector2(-12, 0), new Vector2(150, 30), new Color(0.35f, 0.15f, 0.5f, 0.95f));
            Label(btn, UCLocalization.Tr("uc.colorgrant.pick"), 15, Color.white,
                  TMPro.TextAlignmentOptions.Center);
            byte pid = p.PlayerId;
            OnClick(btn, () => OpenHex(pid));
        }

        // ---------------------------------------------------------------- host: the hex entry
        [HideFromIl2Cpp]
        private void OpenHex(byte targetId) {
            if (panel != null) { Destroy(panel); panel = null; }
            hexTarget = targetId;
            hexBuffer = "";
            typing = true;

            panel = Canvas("UCColorGrantHex", 9010);
            Box(panel, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero,
                new Color(0, 0, 0, 0.85f));
            var card = Box(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                           Vector2.zero, new Vector2(700, 520), new Color(0.1f, 0.09f, 0.14f, 0.98f));

            var target = PlayerControl.AllPlayerControls.ToArray()
                                      .FirstOrDefault(x => x != null && x.PlayerId == targetId);
            var head = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                           new Vector2(0, -14), new Vector2(-40, 34), new Color(0, 0, 0, 0));
            Label(head, UCLocalization.Tr("uc.colorgrant.pick_for", target?.Data?.PlayerName ?? "?"),
                  22, new Color(0.75f, 0.55f, 1f), TMPro.TextAlignmentOptions.Left)
                .fontStyle = TMPro.FontStyles.Bold;

            // The typed value, big, with a live crewmate beside it.
            var field = Box(card, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                            new Vector2(26, -70), new Vector2(340, 60), new Color(1, 1, 1, 0.07f));
            hexLabel = Label(field, "#", 30, Color.white, TMPro.TextAlignmentOptions.Center);
            hexLabel.fontStyle = TMPro.FontStyles.Bold;

            hexPreview = Box(card, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                             new Vector2(400, -70), new Vector2(120, 120), new Color(0, 0, 0, 0));

            var hint = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                           new Vector2(0, -142), new Vector2(-52, 30), new Color(0, 0, 0, 0));
            hexHint = Label(hint, UCLocalization.Tr("uc.colorgrant.hex_hint"), 14,
                            new Color(0.65f, 0.65f, 0.72f), TMPro.TextAlignmentOptions.Left);

            // Presets: one click instead of six keystrokes.
            for (int i = 0; i < Presets.Length; i++) {
                var pr = Presets[i];
                var chip = Box(card, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                               new Vector2(26 + i * 106, -190), new Vector2(96, 96),
                               new Color(1, 1, 1, 0.06f));
                PreviewOf(chip, pr.col, 62f, new Vector2(0, 10));
                var cap = Box(chip, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                              new Vector2(0, 4), new Vector2(92, 18), new Color(0, 0, 0, 0));
                Label(cap, pr.name, 13, new Color(0.8f, 0.8f, 0.85f), TMPro.TextAlignmentOptions.Center);
                var col = pr.col;
                OnClick(chip, () => { hexBuffer = $"{col.r:X2}{col.g:X2}{col.b:X2}"; RefreshHex(); });
            }

            var send = Box(card, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                           new Vector2(-90, 18), new Vector2(170, 40), new Color(0.25f, 0.45f, 0.3f, 0.95f));
            Label(send, UCLocalization.Tr("uc.colorgrant.send"), 17, Color.white,
                  TMPro.TextAlignmentOptions.Center);
            OnClick(send, SendHex);

            var back = Box(card, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                           new Vector2(90, 18), new Vector2(170, 40), new Color(0.3f, 0.3f, 0.38f, 0.95f));
            Label(back, UCLocalization.Tr("uc.colorgrant.back"), 17, Color.white,
                  TMPro.TextAlignmentOptions.Center);
            OnClick(back, () => { StopTyping(); ClosePanel(); OpenPanel(); });

            RefreshHex();
        }

        [HideFromIl2Cpp]
        private void RefreshHex() {
            try {
                if (hexLabel != null) hexLabel.text = "#" + hexBuffer.PadRight(6, '_');
                var rgb = UCColorGrant.ParseHex(hexBuffer);
                if (hexHint != null)
                    hexHint.text = rgb.HasValue ? UCLocalization.Tr("uc.colorgrant.hex_ok")
                                                : UCLocalization.Tr("uc.colorgrant.hex_hint");
                if (hexPreview != null) {
                    for (int i = hexPreview.transform.childCount - 1; i >= 0; i--)
                        Destroy(hexPreview.transform.GetChild(i).gameObject);
                    if (rgb.HasValue) PreviewOf(hexPreview, rgb.Value, 104f, Vector2.zero);
                }
            } catch { }
        }

        [HideFromIl2Cpp]
        private void SendHex() {
            var rgb = UCColorGrant.ParseHex(hexBuffer);
            if (!rgb.HasValue) return;                     // still incomplete - do nothing
            var target = PlayerControl.AllPlayerControls.ToArray()
                                      .FirstOrDefault(x => x != null && x.PlayerId == hexTarget);
            if (target != null) UCColorGrant.Ask(target, rgb.Value);
            StopTyping();
            ClosePanel();
        }

        // ---------------------------------------------------------------- target: the question
        [HideFromIl2Cpp]
        private void BuildPrompt() {
            ClosePrompt();
            promptShown = true;
            var rgb = UCColorGrant.PendingColour;

            prompt = Canvas("UCColorGrantPrompt", 9020);
            var card = Box(prompt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                           Vector2.zero, new Vector2(560, 280), new Color(0.1f, 0.09f, 0.14f, 0.98f));

            var head = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                           new Vector2(0, -14), new Vector2(-40, 64), new Color(0, 0, 0, 0));
            Label(head, UCLocalization.Tr("uc.colorgrant.prompt", UCColorGrant.ToHex(rgb)), 18,
                  Color.white, TMPro.TextAlignmentOptions.Top);

            // Side by side: what you are now, and what you would become. The question is a
            // comparison, so showing only the new colour would answer half of it.
            int mine = 0;
            try { mine = PlayerControl.LocalPlayer.Data.DefaultOutfit.ColorId; } catch { }
            var stage = Box(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                            new Vector2(0, 10), new Vector2(340, 100), new Color(0, 0, 0, 0));
            PreviewOfIndex(stage, mine, 78f, new Vector2(-76, 0));
            PreviewOf(stage, rgb, 78f, new Vector2(76, 0));
            var arrow = Box(stage, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                            Vector2.zero, new Vector2(60, 30), new Color(0, 0, 0, 0));
            Label(arrow, "->", 24, new Color(0.7f, 0.7f, 0.76f), TMPro.TextAlignmentOptions.Center);

            var yes = Box(card, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                          new Vector2(-95, 20), new Vector2(170, 40), new Color(0.2f, 0.5f, 0.3f, 0.95f));
            Label(yes, UCLocalization.Tr("uc.colorgrant.accept"), 17, Color.white,
                  TMPro.TextAlignmentOptions.Center);
            OnClick(yes, () => { UCColorGrant.Answer(true); ClosePrompt(); });

            var no = Box(card, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                         new Vector2(95, 20), new Vector2(170, 40), new Color(0.45f, 0.2f, 0.2f, 0.95f));
            Label(no, UCLocalization.Tr("uc.colorgrant.decline"), 17, Color.white,
                  TMPro.TextAlignmentOptions.Center);
            OnClick(no, () => { UCColorGrant.Answer(false); ClosePrompt(); });
        }
    }
}
