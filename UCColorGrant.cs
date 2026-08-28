// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCColorGrant - the host can put a player into another colour, including UC's own, but only if
 * that player says yes.
 *
 * WHY CONSENT IS THE WHOLE DESIGN, NOT A COURTESY
 * A colour is the one thing in a lobby that belongs to the player rather than to the round. A host
 * who can simply reassign it can grief with it, and there is no undo the player controls. So the
 * host never sets a colour here: he ASKS. The request travels to the target, the target answers,
 * and only an accepted answer is carried out - by the host, because colour assignment is
 * host-authoritative in Among Us (PlayerControl.CheckColor resolves clashes on the host and calls
 * RpcSetColor from there). A declined or unanswered request changes nothing at all.
 *
 * WHO CAN BE ASKED
 * Only a player who has this mod, because the prompt IS this mod - somebody without it would never
 * see the question and would look to the host like a player who is ignoring him. The host's list
 * says so per player instead of letting him wonder.
 *
 * WHICH COLOURS CAN BE ASKED FOR
 * Every colour the palette holds, with one condition attached to the ones this mod adds: a colour
 * that exists only here is an index other clients cannot resolve, so it is offered only while
 * everybody in the room has the mod. That is the same rule UCColors already enforces, and this
 * checks it BEFORE sending rather than letting the request go through and be undone by the lobby
 * guard a moment later.
 *
 * LOBBY ONLY. Changing a colour mid-round rewrites who people think they are looking at, which is
 * a different feature and not one anybody asked for.
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
        private const byte SubRequest = 0;  // host -> target: please change to this colour
        private const byte SubAnswer  = 1;  // target -> host: yes / no

        /// The request the LOCAL player is being asked to answer, or -1 for none.
        public static int PendingColour { get; private set; } = -1;
        /// What the host is waiting for, so his list can show it. playerId -> colour index.
        public static readonly Dictionary<byte, int> Outstanding = new();

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

        /// A colour only this mod knows is unusable for anyone without it - see the header.
        public static bool ColourAllowed(int colour) {
            try {
                if (colour < 0 || colour >= Palette.PlayerColors.Length) return false;
                bool ucOnly = UCColors.Index >= 0 && colour >= UCColors.Index;
                return !ucOnly || TeslaVersionHandshake.EveryoneHasMod();
            } catch { return false; }
        }

        public static bool ColourTaken(int colour, PlayerControl except) {
            try {
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (p == null || p.Data == null || p.Data.Disconnected) continue;
                    if (except != null && p.PlayerId == except.PlayerId) continue;
                    if (p.Data.DefaultOutfit.ColorId == colour) return true;
                }
            } catch { }
            return false;
        }

        // ================================================================================
        // Sending
        // ================================================================================
        public static void Ask(PlayerControl target, int colour) {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            if (!InLobby() || target == null) return;
            if (!HasMod(target) || !ColourAllowed(colour) || ColourTaken(colour, target)) return;

            Outstanding[target.PlayerId] = colour;
            var w = UCRpc.Begin(RpcId);
            w.Write(SubRequest);
            w.Write(target.PlayerId);
            w.Write((byte)colour);
            AmongUsClient.Instance.FinishRpcImmediately(w);
            ReceiveRequest(target.PlayerId, (byte)colour);   // the host may be asking himself
        }

        public static void Answer(bool accepted) {
            if (PendingColour < 0 || PlayerControl.LocalPlayer == null) return;
            byte colour = (byte)PendingColour;
            PendingColour = -1;

            var w = UCRpc.Begin(RpcId);
            w.Write(SubAnswer);
            w.Write(PlayerControl.LocalPlayer.PlayerId);
            w.Write(colour);
            w.Write((byte)(accepted ? 1 : 0));
            AmongUsClient.Instance.FinishRpcImmediately(w);
            ReceiveAnswer(PlayerControl.LocalPlayer.PlayerId, colour, accepted);
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
                    ReceiveRequest(r.ReadByte(), r.ReadByte());
                } else if (sub == SubAnswer) {
                    byte who = r.ReadByte(), colour = r.ReadByte();
                    bool ok = r.ReadByte() != 0;
                    // Only the player themselves may answer for themselves.
                    var sender = UCRpc.Sender;
                    if (sender == null || sender.PlayerId != who) return;
                    ReceiveAnswer(who, colour, ok);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCColorGrant] rpc failed: {e}");
            }
        }

        private static void ReceiveRequest(byte targetId, byte colour) {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.PlayerId != targetId) return;
            if (!InLobby()) return;
            PendingColour = colour;
        }

        private static void ReceiveAnswer(byte who, byte colour, bool accepted) {
            Outstanding.Remove(who);
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

            var target = PlayerControl.AllPlayerControls.ToArray().FirstOrDefault(p => p != null && p.PlayerId == who);
            if (target == null) return;

            if (!accepted) {
                Notify(UCLocalization.Tr("uc.colorgrant.declined", target.Data?.PlayerName ?? "?"));
                return;
            }
            // Re-check on arrival, not just before sending: the room can have changed while the
            // player was deciding - somebody without the mod may have joined, or the colour may
            // have been taken in the meantime.
            if (!ColourAllowed(colour) || ColourTaken(colour, target)) {
                Notify(UCLocalization.Tr("uc.colorgrant.unavailable", target.Data?.PlayerName ?? "?"));
                return;
            }
            target.RpcSetColor(colour);
            UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[UCColorGrant] {target.Data?.PlayerName} accepted colour {colour}.");
        }

        private static void Notify(string text) {
            try {
                var hud = HudManager.Instance;
                if (hud != null && hud.Notifier != null) hud.Notifier.AddDisconnectMessage(text);
            } catch { }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        internal static class ResetPatch {
            public static void Postfix() { PendingColour = -1; Outstanding.Clear(); }
        }
    }

    /*
     * The two screens this needs: the host's list, and the question the target gets. Both are plain
     * screen-space canvases built on demand, the same shape UTS' NewcomerShieldUI uses.
     */
    public class UCColorGrantUI : MonoBehaviour {
        public static UCColorGrantUI Instance { get; private set; }
        public UCColorGrantUI(IntPtr ptr) : base(ptr) { }

        private static readonly Dictionary<Color, Sprite> solids = new();
        private GameObject lobbyButton, panel, prompt;
        private int promptShownFor = -1;
        private float nextPoll;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
        }

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

        public void Update() {
            try {
                if (Time.time < nextPoll) return;
                nextPoll = Time.time + 0.25f;

                bool host = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
                bool show = host && UCColorGrant.InLobby();
                if (show && lobbyButton == null) BuildLobbyButton();
                if (!show && lobbyButton != null) { Destroy(lobbyButton); lobbyButton = null; ClosePanel(); }

                // The question, whenever one is outstanding for this player.
                if (UCColorGrant.PendingColour >= 0 && promptShownFor != UCColorGrant.PendingColour)
                    BuildPrompt(UCColorGrant.PendingColour);
                if (UCColorGrant.PendingColour < 0 && prompt != null) ClosePrompt();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCColorGrant] UI tick failed: {e}");
            }
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
        [HideFromIl2Cpp] private void ClosePanel() { if (panel != null) { Destroy(panel); panel = null; } }
        [HideFromIl2Cpp] private void ClosePrompt() {
            if (prompt != null) { Destroy(prompt); prompt = null; }
            promptShownFor = -1;
        }

        [HideFromIl2Cpp]
        private void OpenPanel() {
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
                          new Vector2(0, -50), new Vector2(-40, 40), new Color(0, 0, 0, 0));
            Label(sub, UCLocalization.Tr("uc.colorgrant.subtitle"), 15, new Color(0.7f, 0.7f, 0.76f),
                  TMPro.TextAlignmentOptions.TopLeft);

            float y = -100f;
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

            // The player's colour, as the colour itself - a name plus a swatch says more than a number.
            int cur = 0;
            try { cur = p.Data.DefaultOutfit.ColorId; } catch { }
            var swatch = Box(row, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                             new Vector2(10, 0), new Vector2(32, 32), new Color(0, 0, 0, 0));
            Preview(swatch, cur, 30f, Vector2.zero);

            var name = Box(row, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                           new Vector2(46, 0), new Vector2(300, 30), new Color(0, 0, 0, 0));
            Label(name, p.Data.PlayerName ?? "?", 17, Color.white, TMPro.TextAlignmentOptions.Left);

            bool hasMod = UCColorGrant.HasMod(p);
            bool waiting = UCColorGrant.Outstanding.ContainsKey(p.PlayerId);

            string state = !hasMod ? UCLocalization.Tr("uc.colorgrant.no_mod")
                         : waiting ? UCLocalization.Tr("uc.colorgrant.waiting")
                         : "";
            if (state != "") {
                var st = Box(row, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                             new Vector2(-12, 0), new Vector2(240, 30), new Color(0, 0, 0, 0));
                Label(st, state, 15, new Color(0.65f, 0.65f, 0.7f), TMPro.TextAlignmentOptions.Right);
                return;
            }

            var btn = Box(row, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                          new Vector2(-12, 0), new Vector2(150, 30), new Color(0.35f, 0.15f, 0.5f, 0.95f));
            Label(btn, UCLocalization.Tr("uc.colorgrant.pick"), 15, Color.white,
                  TMPro.TextAlignmentOptions.Center);
            byte pid = p.PlayerId;
            OnClick(btn, () => OpenGrid(pid));
        }

        /*
         * THE PREVIEW, and why it is drawn rather than borrowed.
         *
         * A flat swatch answers "which colour" but not the question anybody actually has, which is
         * "what will I look like". The game's own answer would be a PoolablePlayer, but that is a
         * world-space object with a prefab that only exists on certain screens (the end-game
         * podium has one, a lobby panel does not), and these panels are screen-space UGUI. So the
         * silhouette is generated once into two alpha masks - body and visor - and every preview is
         * those two masks tinted.
         *
         * TINTED WITH THE PALETTE'S OWN TWO TONES: Among Us stores a colour AND its shadow, and a
         * crewmate is shaded with both. Using only the bright one would make every preview look
         * flatter than the real thing, and the dark colours would all look alike.
         */
        private static Sprite bodyMask, visorMask;

        [HideFromIl2Cpp]
        private static Sprite BuildMask(bool visor) {
            const int S = 96;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            for (int y = 0; y < S; y++) {
                for (int x = 0; x < S; x++) {
                    float u = x / (float)S, v = y / (float)S;   // v = 0 at the bottom
                    float a = visor ? Visor(u, v) : Body(u, v);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            var sp = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f));
            DontDestroyOnLoad(tex); DontDestroyOnLoad(sp);
            return sp;
        }

        /// Coverage of the crewmate body at (u, v), both 0..1 with v = 0 at the bottom. Built from
        /// rounded boxes: torso, backpack, and two legs with a notch between them.
        [HideFromIl2Cpp]
        private static float Body(float u, float v) {
            float torso = RoundBox(u, v, 0.16f, 0.10f, 0.62f, 0.86f, 0.22f);
            float pack  = RoundBox(u, v, 0.60f, 0.30f, 0.82f, 0.66f, 0.09f);
            float legL  = RoundBox(u, v, 0.20f, 0.02f, 0.38f, 0.22f, 0.05f);
            float legR  = RoundBox(u, v, 0.44f, 0.02f, 0.62f, 0.22f, 0.05f);
            return Mathf.Clamp01(Mathf.Max(Mathf.Max(torso, pack), Mathf.Max(legL, legR)));
        }

        [HideFromIl2Cpp]
        private static float Visor(float u, float v) => RoundBox(u, v, 0.30f, 0.58f, 0.68f, 0.80f, 0.10f);

        /// A rounded box as coverage, antialiased over roughly one pixel of the 96-wide mask.
        [HideFromIl2Cpp]
        private static float RoundBox(float u, float v, float x0, float y0, float x1, float y1, float r) {
            float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f;
            float hx = (x1 - x0) * 0.5f - r, hy = (y1 - y0) * 0.5f - r;
            float dx = Mathf.Max(Mathf.Abs(u - cx) - Mathf.Max(hx, 0f), 0f);
            float dy = Mathf.Max(Mathf.Abs(v - cy) - Mathf.Max(hy, 0f), 0f);
            float d = Mathf.Sqrt(dx * dx + dy * dy) - r;        // <0 inside
            return Mathf.Clamp01(0.5f - d / (1f / 96f * 2f));
        }

        /// One crewmate in the given palette colour, drawn into `parent` at `size` pixels.
        [HideFromIl2Cpp]
        private static void Preview(GameObject parent, int colour, float size, Vector2 pos) {
            if (bodyMask == null) bodyMask = BuildMask(false);
            if (visorMask == null) visorMask = BuildMask(true);

            Color body = ColourOf(colour), shadow = ShadowOf(colour);

            void Layer(Sprite sp, Color col, Vector2 offset) {
                var go = new GameObject("P");
                go.transform.SetParent(parent.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = pos + offset;
                rt.sizeDelta = new Vector2(size, size);
                var img = go.AddComponent<Image>();
                img.sprite = sp; img.color = col; img.raycastTarget = false;
            }

            // Shadow first, offset a little down and left, exactly the way the game shades a bean.
            Layer(bodyMask, shadow, new Vector2(-size * 0.045f, -size * 0.045f));
            Layer(bodyMask, body, Vector2.zero);
            Layer(visorMask, new Color(0.65f, 0.79f, 0.85f), Vector2.zero);
        }

        [HideFromIl2Cpp]
        private static Color ShadowOf(int i) {
            try {
                if (i >= 0 && i < Palette.ShadowColors.Length) {
                    var c = Palette.ShadowColors[i];
                    return new Color(c.r / 255f, c.g / 255f, c.b / 255f);
                }
            } catch { }
            return new Color(0.25f, 0.25f, 0.25f);
        }

        [HideFromIl2Cpp]
        private static Color ColourOf(int i) {
            try {
                if (i >= 0 && i < Palette.PlayerColors.Length) {
                    var c = Palette.PlayerColors[i];
                    return new Color(c.r / 255f, c.g / 255f, c.b / 255f);
                }
            } catch { }
            return Color.grey;
        }

        [HideFromIl2Cpp]
        private void OpenGrid(byte targetId) {
            ClosePanel();
            panel = Canvas("UCColorGrantGrid", 9010);
            Box(panel, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero,
                new Color(0, 0, 0, 0.85f));
            var card = Box(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                           Vector2.zero, new Vector2(760, 620), new Color(0.1f, 0.09f, 0.14f, 0.98f));

            var target = PlayerControl.AllPlayerControls.ToArray().FirstOrDefault(x => x != null && x.PlayerId == targetId);
            var head = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                           new Vector2(0, -14), new Vector2(-40, 34), new Color(0, 0, 0, 0));
            Label(head, UCLocalization.Tr("uc.colorgrant.pick_for", target?.Data?.PlayerName ?? "?"),
                  22, new Color(0.75f, 0.55f, 1f), TMPro.TextAlignmentOptions.Left)
                .fontStyle = TMPro.FontStyles.Bold;

            int n = 0;
            try { n = Palette.PlayerColors.Length; } catch { }
            const int cols = 10;
            for (int i = 0; i < n; i++) {
                int col = i;
                bool ok = UCColorGrant.ColourAllowed(col) && !UCColorGrant.ColourTaken(col, target);
                int r = i / cols, c = i % cols;
                var chip = Box(card, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                               new Vector2(26 + c * 70, -70 - r * 60), new Vector2(56, 52),
                               ok ? new Color(1, 1, 1, 0.06f) : new Color(1, 1, 1, 0.02f));
                // The crewmate itself, not a square of the colour - a bean is what the player will
                // actually be looking at. Unavailable colours stay greyed rather than disappearing,
                // so the palette keeps its shape and the gap is explained by the row above.
                Preview(chip, col, 44f, Vector2.zero);
                if (!ok) Box(chip, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f),
                             Vector2.zero, Vector2.zero, new Color(0.06f, 0.06f, 0.08f, 0.72f));
                if (ok) OnClick(chip, () => { UCColorGrant.Ask(target, col); ClosePanel(); });
            }

            var back = Box(card, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                           new Vector2(0, 16), new Vector2(150, 38), new Color(0.3f, 0.3f, 0.38f, 0.95f));
            Label(back, UCLocalization.Tr("uc.colorgrant.back"), 17, Color.white,
                  TMPro.TextAlignmentOptions.Center);
            OnClick(back, () => { ClosePanel(); OpenPanel(); });
        }

        [HideFromIl2Cpp]
        private void BuildPrompt(int colour) {
            ClosePrompt();
            promptShownFor = colour;
            prompt = Canvas("UCColorGrantPrompt", 9020);
            var card = Box(prompt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                           Vector2.zero, new Vector2(520, 240), new Color(0.1f, 0.09f, 0.14f, 0.98f));

            var head = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                           new Vector2(0, -16), new Vector2(-40, 60), new Color(0, 0, 0, 0));
            Label(head, UCLocalization.Tr("uc.colorgrant.prompt"), 18, Color.white,
                  TMPro.TextAlignmentOptions.Top);

            // Side by side: what you are now, and what you would become. The question is a
            // comparison, so showing only the target colour would be answering half of it.
            int mine = 0;
            try { mine = PlayerControl.LocalPlayer.Data.DefaultOutfit.ColorId; } catch { }
            var stage = Box(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                            new Vector2(0, 10), new Vector2(320, 90), new Color(0, 0, 0, 0));
            Preview(stage, mine, 72f, new Vector2(-70, 0));
            Preview(stage, colour, 72f, new Vector2(70, 0));
            var arrow = Box(stage, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                            Vector2.zero, new Vector2(60, 30), new Color(0, 0, 0, 0));
            Label(arrow, "->", 24, new Color(0.7f, 0.7f, 0.76f), TMPro.TextAlignmentOptions.Center);

            var yes = Box(card, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                          new Vector2(-90, 20), new Vector2(160, 40), new Color(0.2f, 0.5f, 0.3f, 0.95f));
            Label(yes, UCLocalization.Tr("uc.colorgrant.accept"), 17, Color.white,
                  TMPro.TextAlignmentOptions.Center);
            OnClick(yes, () => { UCColorGrant.Answer(true); ClosePrompt(); });

            var no = Box(card, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                         new Vector2(90, 20), new Vector2(160, 40), new Color(0.45f, 0.2f, 0.2f, 0.95f));
            Label(no, UCLocalization.Tr("uc.colorgrant.decline"), 17, Color.white,
                  TMPro.TextAlignmentOptions.Center);
            OnClick(no, () => { UCColorGrant.Answer(false); ClosePrompt(); });
        }
    }
}
