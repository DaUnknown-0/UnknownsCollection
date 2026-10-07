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
        private const byte SubRelease = 3;  // player -> host: I picked a palette colour myself, forget my grant
        private const byte SubCancel  = 4;  // host -> target: the question is withdrawn, close the prompt

        /// An unanswered question no longer blocks the player's row after this long (audit 04.10.).
        private const float AskTimeout = 60f;
        private static readonly Dictionary<byte, float> askedAt = new();

        /// The colour the LOCAL player is being asked about, if any.
        public static bool HasPending { get; private set; }
        public static Color32 PendingColour { get; private set; }

        /// <summary>Autotest (UTS UI gallery): a question as if the host had asked, without any RPC.</summary>
        internal static void DiagPending(Color32 rgb, bool on) { PendingColour = rgb; HasPending = on; }
        /// What the host is waiting for, so his list can show it.
        public static readonly Dictionary<byte, Color32> Outstanding = new();

        public static void RegisterRpc() => UCRpc.Register(RpcId, Handle);

        // ================================================================================
        // Rules
        // ================================================================================
        public static bool InLobby() =>
            DiagGallery() ||
            (AmongUsClient.Instance != null && !AmongUsClient.Instance.IsGameStarted
             && ShipStatus.Instance == null);

        // UTS' UI gallery autotest photographs the screens in freeplay; it publishes this flag.
        private static bool DiagGallery() {
            try { return AppDomain.CurrentDomain.GetData("UTS.UIGallery.Active") is bool b && b; } catch { return false; }
        }

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
            askedAt[target.PlayerId] = Time.realtimeSinceStartup;
            var w = UCRpc.Begin(RpcId);
            w.Write(SubRequest);
            w.Write(target.PlayerId);
            w.Write(rgb.r); w.Write(rgb.g); w.Write(rgb.b);
            AmongUsClient.Instance.FinishRpcImmediately(w);
            ReceiveRequest(target.PlayerId, rgb);          // the host may be asking himself
        }

        /// The round started before the player answered: the prompt must not stay on screen.
        internal static void DropPending() { HasPending = false; }

        /// Host: is this player's question still open? One older than AskTimeout is dropped here, so
        /// the row offers "Pick colour..." again (a player who never answers blocked it for good).
        public static bool IsWaiting(byte playerId) {
            if (!Outstanding.ContainsKey(playerId)) return false;
            if (askedAt.TryGetValue(playerId, out float t) && Time.realtimeSinceStartup - t > AskTimeout) {
                Cancel(playerId);
                return false;
            }
            return true;
        }

        /// Host: withdraw a question. The target's prompt closes; a late "yes" is then refused like
        /// any answer to a question that is not open.
        public static void Cancel(byte playerId) {
            Outstanding.Remove(playerId);
            askedAt.Remove(playerId);
            try {
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                var w = UCRpc.Begin(RpcId);
                w.Write(SubCancel);
                w.Write(playerId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ReceiveCancel(playerId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[UCColorGrant] cancel failed: {e}"); }
        }

        private static void ReceiveCancel(byte targetId) {
            var me = PlayerControl.LocalPlayer;
            if (me != null && me.PlayerId == targetId) HasPending = false;
        }

        /// Host, before asking: the colour must stay tellable apart from every colour another player
        /// wears (audit 04.10.: #FF0000 could be handed out next to a Red). Weighted RGB distance
        /// ("redmean"), 0 to about 765; below MinColourDistance two crewmates read as the same.
        private const float MinColourDistance = 90f;
        public static string TooSimilarTo(byte targetId, Color32 rgb) {
            try {
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (p == null || p.Data == null || p.Data.Disconnected || p.PlayerId == targetId) continue;
                    int id = p.Data.DefaultOutfit.ColorId;
                    if (id < 0 || id >= Palette.PlayerColors.Length) continue;
                    if (ColourDistance(rgb, Palette.PlayerColors[id]) < MinColourDistance) return p.Data.PlayerName ?? "?";
                }
            } catch { }
            return null;
        }

        private static float ColourDistance(Color32 a, Color32 b) {
            float rm = (a.r + b.r) / 2f;
            float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
            return Mathf.Sqrt((2f + rm / 256f) * dr * dr + 4f * dg * dg + (2f + (255f - rm) / 256f) * db * db);
        }

        /// Sent by the player's own client when he picks a palette colour in the wardrobe while wearing
        /// a granted one: his own choice ends the grant (the host otherwise restored it every 0.5 s).
        internal static void SendRelease() {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null || AmongUsClient.Instance == null) return;
                var w = UCRpc.Begin(RpcId);
                w.Write(SubRelease);
                w.Write(me.PlayerId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                if (AmongUsClient.Instance.AmHost) UCColors.ForgetGrant(me.PlayerId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[UCColorGrant] release failed: {e}"); }
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
                } else if (sub == SubRelease) {
                    byte who = r.ReadByte();
                    var sender = UCRpc.Sender;
                    if (sender == null || sender.PlayerId != who) return;   // only for oneself
                    if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) UCColors.ForgetGrant(who);
                } else if (sub == SubCancel) {
                    if (!UCRpc.SenderIsHost) return;
                    ReceiveCancel(r.ReadByte());
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
            bool asked = Outstanding.TryGetValue(who, out var askedRgb);
            Outstanding.Remove(who);
            askedAt.Remove(who);
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            // Only an answer to a question the host actually asked, for exactly that colour: an
            // unasked "yes" let a modified client give itself any colour and use up slots (Opus audit
            // round 2, the same class as the AUDIT H-3 hardening).
            if (accepted && (!asked || askedRgb.r != rgb.r || askedRgb.g != rgb.g || askedRgb.b != rgb.b)) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCColorGrant] ignored an answer from player {who} to a question the host never asked.");
                return;
            }

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
            public static void Postfix() { HasPending = false; Outstanding.Clear(); askedAt.Clear(); }
        }
    }

    /*
     * The three screens: the host's player list, the hex entry, and the question the target gets.
     * Screen-space canvases built on demand, drawn in the look of TOR - Forgotten Fixes' panels
     * (deep-blue card with a thick pale outline and a hard shadow, slanted title tab, capsule
     * buttons, the game's fonts). UC has no reference to UTS, so the few shapes it needs are
     * generated here; the palette matches UTS' VanillaUI.
     */
    public class UCColorGrantUI : MonoBehaviour {
        public static UCColorGrantUI Instance { get; private set; }
        public UCColorGrantUI(IntPtr ptr) : base(ptr) { }

        private static readonly Dictionary<Color, Sprite> solids = new();
        private GameObject lobbyButton, panel, prompt;
        private RectTransform lobbyButtonRect;
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

        // ---------------------------------------------------------------- theme (matches UTS VanillaUI)
        private static readonly Color ColFrame = new Color(0.72f, 0.78f, 0.84f);
        private static readonly Color ColBody = new Color(0.09f, 0.11f, 0.15f);
        private static readonly Color ColField = new Color(0.14f, 0.17f, 0.22f);
        private static readonly Color ColFieldLight = new Color(0.21f, 0.26f, 0.33f);
        private static readonly Color ColMuted = new Color(0.66f, 0.72f, 0.80f);
        private static readonly Color ColShadow = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color ColBackdrop = new Color(0f, 0f, 0f, 0.72f);
        private static readonly Color ColPurple = new Color(0.58f, 0.32f, 0.86f);   // this feature's accent
        private static readonly Color ColGreen = new Color(0.30f, 0.74f, 0.42f);
        private static readonly Color ColRed = new Color(0.86f, 0.28f, 0.32f);
        private static readonly Color ColGrey = new Color(0.36f, 0.42f, 0.50f);
        private static readonly Color ColWarn = new Color(1f, 0.6f, 0.5f);
        private const int Outline = 5;

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

        // Rounded rectangle (border 0) or ring (border > 0), white, sliced; tinted by Image.color.
        private static readonly Dictionary<int, Sprite> roundCache = new();

        [HideFromIl2Cpp]
        private static Sprite Round(int radius, int border) {
            int key = radius * 100 + border;
            if (roundCache.TryGetValue(key, out var c) && c != null) return c;
            int n = radius * 2 + 4;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            var px = new Color[n * n];
            float half = n / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++) {
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                    float outside = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0) - radius;
                    float a = Mathf.Clamp01(0.5f - outside);
                    if (border > 0) a *= Mathf.Clamp01(outside + border + 0.5f);
                    px[y * n + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, true);
            float b = radius + 2;
            var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0,
                                   SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            sp.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            roundCache[key] = sp;
            return sp;
        }

        // The slanted title tab: square on the left, the right edge leaning; sliced.
        private static Sprite slant;

        [HideFromIl2Cpp]
        private static Sprite Slant() {
            if (slant != null) return slant;
            const int h = 48, lean = 22, w = 70;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) {
                    float right = w - 1f - lean * (1f - (y + 0.5f) / h);
                    px[y * w + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(right - (x + 0.5f) + 0.5f));
                }
            tex.SetPixels(px);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, true);
            slant = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0,
                                  SpriteMeshType.FullRect, new Vector4(12, 2, lean + 8, 2));
            tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            slant.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return slant;
        }

        // The game's faces, picked by name: Brook for titles and buttons, upright Barlow Bold for text.
        private static TMPro.TMP_FontAsset fontBody, fontHead;

        [HideFromIl2Cpp]
        private static void FindFonts() {
            if (fontBody != null && fontHead != null) return;
            try {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMPro.TMP_FontAsset>())) {
                    var f = o.TryCast<TMPro.TMP_FontAsset>();
                    if (f == null) continue;
                    string n = f.name.ToLowerInvariant();
                    if (n.Contains("italic") || n.Contains("outline") || n.Contains("masked")) continue;
                    if (n.StartsWith("barlow-bold")) fontBody = f;
                    else if (n.Contains("brook") && (fontHead == null || n.Contains("sdf"))) fontHead = f;
                }
            } catch { }
        }

        // Brook is a dynamic font asset: HasCharacters reports false for glyphs it has not baked yet,
        // so plain ASCII headings skip that check (the first run kept TMP's default face, 2026-10-07).
        [HideFromIl2Cpp]
        private static void Face(TMPro.TextMeshProUGUI t, bool heading) {
            FindFonts();
            var f = heading ? fontHead : fontBody;
            if (f == null) return;
            bool ascii = true;
            foreach (char ch in t.text) if (ch > 0x7E) { ascii = false; break; }
            bool ok = ascii;
            if (!ok) { try { ok = f.HasCharacters(t.text); } catch { } }
            if (ok) t.font = f;
            if (heading) t.fontStyle &= ~TMPro.FontStyles.Bold;
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
                                      Vector2 pos, Vector2 size, Color col, int radius = 0) {
            var go = new GameObject("B");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = min; rt.anchorMax = max; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            if (radius > 0) { img.sprite = Round(radius, 0); img.type = Image.Type.Sliced; img.color = col; }
            else img.sprite = Solid(col);
            return go;
        }

        [HideFromIl2Cpp]
        private static GameObject Layer(GameObject parent, Sprite sp, Color col, Vector2 offset) {
            var go = new GameObject("L");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = offset;
            var img = go.AddComponent<Image>();
            img.sprite = sp; img.type = Image.Type.Sliced; img.color = col; img.raycastTarget = false;
            return go;
        }

        [HideFromIl2Cpp]
        private static TMPro.TextMeshProUGUI Label(GameObject parent, string text, float size,
                                                   Color col, TMPro.TextAlignmentOptions align, bool heading = false) {
            var go = new GameObject("T");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.sizeDelta = Vector2.zero;
            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.color = col; t.alignment = align;
            t.enableWordWrapping = true;
            t.raycastTarget = false;
            Face(t, heading);
            return t;
        }

        [HideFromIl2Cpp]
        private static void OnClick(GameObject go, Action a) {
            // a veil for hover and press, like the panels of Forgotten Fixes
            var veil = Layer(go, Round(16, 0), Color.white, Vector2.zero);
            var img = veil.GetComponent<Image>();
            img.raycastTarget = true;
            var b = veil.AddComponent<Button>();
            b.targetGraphic = img;
            var c = b.colors;
            c.normalColor = new Color(1f, 1f, 1f, 0f);
            c.highlightedColor = new Color(1f, 1f, 1f, 0.2f);
            c.pressedColor = new Color(0f, 0f, 0f, 0.25f);
            c.selectedColor = new Color(1f, 1f, 1f, 0f);
            c.fadeDuration = 0.06f;
            b.colors = c;
            img.CrossFadeColor(c.normalColor, 0f, true, true);
            b.onClick.AddListener((UnityEngine.Events.UnityAction)a);
        }

        /// <summary>Dim backdrop and the card: hard shadow, dark body, thick pale outline.</summary>
        [HideFromIl2Cpp]
        private static GameObject Card(GameObject root, Vector2 size, bool dim = true) {
            if (dim) Box(root, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, ColBackdrop);
            var card = new GameObject("Card");
            card.transform.SetParent(root.transform, false);
            var rt = card.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            Layer(card, Round(22, 0), ColShadow, new Vector2(8, -10));
            Layer(card, Round(22, 0), ColBody, Vector2.zero).GetComponent<Image>().raycastTarget = true;
            Layer(card, Round(22, Outline), ColFrame, Vector2.zero);
            return card;
        }

        /// <summary>The slanted title tab in the top-left corner of a card.</summary>
        [HideFromIl2Cpp]
        private static void Title(GameObject card, string text, Color tab) {
            float w = Mathf.Clamp(text.Length * 22f + 90f, 260f, 640f);
            var go = new GameObject("TitleTab");
            go.transform.SetParent(card.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(Outline + 18f, -Outline - 10f);
            rt.sizeDelta = new Vector2(w, 54f);
            var img = go.AddComponent<Image>();
            img.sprite = Slant(); img.type = Image.Type.Sliced; img.color = tab; img.raycastTarget = false;
            var t = Label(go, text.ToUpperInvariant(), 34, Color.white, TMPro.TextAlignmentOptions.Left, true);
            t.rectTransform.offsetMin = new Vector2(22, 0); t.rectTransform.offsetMax = new Vector2(-40, 0);
            t.enableWordWrapping = false; t.enableAutoSizing = true; t.fontSizeMin = 18; t.fontSizeMax = 34;
        }

        /// <summary>A capsule button: flat colour, darker edge, hard shadow, Brook label.</summary>
        [HideFromIl2Cpp]
        private static GameObject Btn(GameObject parent, Vector2 anchor, Vector2 pos, Vector2 size, string label,
                                      Color col, Action onClick) {
            var go = new GameObject("Btn");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            int r = Mathf.Clamp(Mathf.RoundToInt(size.y / 2f) - 1, 4, 30);
            Layer(go, Round(r, 0), ColShadow, new Vector2(3, -5));
            Layer(go, Round(r, 0), col, Vector2.zero);
            Layer(go, Round(r, 3), new Color(col.r * 0.55f, col.g * 0.55f, col.b * 0.55f), Vector2.zero);
            var t = Label(go, label.ToUpperInvariant(), Mathf.Clamp(size.y * 0.6f, 15f, 30f), Color.white,
                          TMPro.TextAlignmentOptions.Center, true);
            t.rectTransform.offsetMin = new Vector2(10, 0); t.rectTransform.offsetMax = new Vector2(-10, 0);
            t.enableWordWrapping = false; t.enableAutoSizing = true; t.fontSizeMin = 11; t.fontSizeMax = Mathf.Clamp(size.y * 0.6f, 15f, 30f);
            OnClick(go, onClick);
            return go;
        }

        /// <summary>The red X in the top-right corner of a card.</summary>
        [HideFromIl2Cpp]
        private static void CloseX(GameObject card, Action onClose) =>
            Btn(card, new Vector2(1, 1), new Vector2(-Outline - 10f, -Outline - 10f), new Vector2(54, 54), "X", ColRed, onClose);

        // ---------------------------------------------------------------- autotest (UTS UI gallery)
        /// <summary>Opens screen 0 (player list), 1 (hex entry for the local player) or 2 (the question); -1 closes all.</summary>
        public static void DiagShow(int screen) {
            var ui = Instance;
            if (ui == null) return;
            ui.ClosePanel();
            UCColorGrant.DiagPending(default, false);
            ui.ClosePrompt();
            if (screen == 0) ui.OpenPanel();
            else if (screen == 1 && PlayerControl.LocalPlayer != null) { ui.OpenHex(PlayerControl.LocalPlayer.PlayerId); ui.hexBuffer = "3DDC97"; ui.RefreshHex(); }
            else if (screen == 2) { UCColorGrant.DiagPending(new Color32(0xFF, 0xC1, 0x07, 0xFF), true); ui.BuildPrompt(); }
        }

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

                bool show = ShouldShow();
                if (!inLobbyMenu) JoinLobbyMenu();
                // with UTS's lobby menu the entry sits there; the own button only without it
                bool own = show && !inLobbyMenu;
                if (own && lobbyButton == null) BuildLobbyButton();
                if (!own && lobbyButton != null) { Destroy(lobbyButton); lobbyButton = null; lobbyButtonRect = null; }
                if (!show && panel != null) ClosePanel();
                if (lobbyButtonRect != null) lobbyButtonRect.anchoredPosition = LobbyButtonPos();

                // A question that outlives the lobby (the game started first) is dropped (Opus audit round 2).
                if (UCColorGrant.HasPending && !UCColorGrant.InLobby()) UCColorGrant.DropPending();
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
        private static bool ShouldShow() =>
            AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost
            && UCColorGrant.InLobby() && UCColors.Installed;

        /*
         * UTS (since 2026-10-05) gathers the lobby panels in one menu behind a corner button and
         * publishes an Add delegate for other mods' entries. Plain BCL and Unity types only, so no
         * reference to UTS is needed: (id, order, visible, label, click, colour).
         */
        private bool inLobbyMenu;

        [HideFromIl2Cpp]
        private void JoinLobbyMenu() {
            try {
                if (!(AppDomain.CurrentDomain.GetData("UTS.LobbyMenu.Add")
                        is Action<string, int, Func<bool>, Func<string>, Action, Color> add)) return;
                add("uc.colorgrant", 50, ShouldShow, () => UCLocalization.Tr("uc.colorgrant.lobby_button"),
                    () => Instance?.TogglePanel(), new Color(0.35f, 0.1f, 0.5f));
                inLobbyMenu = true;
            } catch { }
        }

        /*
         * Without the lobby menu (a UTS from before it): UTS lines its own lobby buttons (mod sync, newcomer shield, early-death shield, ...) up in
         * one row along the bottom edge and publishes the next free X. A column grew upwards into the
         * lobby's settings button (2026-10-02). A UTS from before the row only publishes the next free
         * Y of its column; without UTS the bottom-left corner is free.
         */
        [HideFromIl2Cpp]
        private static Vector2 LobbyButtonPos() {
            try {
                var d = AppDomain.CurrentDomain;
                if (d.GetData("UTS.LobbyButtons.NextFreeX") is float x) return new Vector2(x, 28f);
                if (d.GetData("UTS.LobbyButtons.NextFreeY") is float y) return new Vector2(28f, y);
            } catch { }
            return new Vector2(28f, 28f);
        }

        [HideFromIl2Cpp]
        private void BuildLobbyButton() {
            lobbyButton = Canvas("UCColorGrantButton", 9000);
            var b = Btn(lobbyButton, Vector2.zero, LobbyButtonPos(), new Vector2(330, 46),
                        UCLocalization.Tr("uc.colorgrant.lobby_button"), ColPurple, TogglePanel);
            lobbyButtonRect = b.GetComponent<RectTransform>();
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
            // Every player gets a row: the card grows with the lobby (audit 04.10.: it stopped at ten
            // rows, a lobby holds fifteen). Rows tighten from 54 to 42 px once there are more than ten.
            var rowPlayers = PlayerControl.AllPlayerControls.ToArray()
                .Where(p => p != null && p.Data != null && !p.Data.Disconnected).ToList();
            float step = rowPlayers.Count > 10 ? 42f : 54f;
            float cardH = Mathf.Clamp(140f + rowPlayers.Count * step + 30f, 320f, 1010f);
            var card = Card(panel, new Vector2(820, cardH));
            Title(card, UCLocalization.Tr("uc.colorgrant.title"), ColPurple);
            CloseX(card, ClosePanel);

            var sub = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                          new Vector2(0, -78), new Vector2(-64, 44), new Color(0, 0, 0, 0));
            Label(sub, UCColors.Safe() ? UCLocalization.Tr("uc.colorgrant.subtitle")
                                       : UCLocalization.Tr("uc.colorgrant.blocked"),
                  15, UCColors.Safe() ? ColMuted : ColWarn, TMPro.TextAlignmentOptions.Center);

            float y = -130f;
            foreach (var p in rowPlayers) {
                BuildRow(card, p, y, step - 8f);
                y -= step;
            }
        }

        [HideFromIl2Cpp]
        private void BuildRow(GameObject card, PlayerControl p, float y, float h) {
            var row = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                          new Vector2(0, y), new Vector2(-60, h), ColField, 10);

            int cur = 0;
            try { cur = p.Data.DefaultOutfit.ColorId; } catch { }
            var swatch = Box(row, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                             new Vector2(12, 0), new Vector2(h - 6f, h - 6f), new Color(0, 0, 0, 0));
            PreviewOfIndex(swatch, cur, h - 8f, Vector2.zero);

            var name = Box(row, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                           new Vector2(h + 14f, 0), new Vector2(300, 30), new Color(0, 0, 0, 0));
            Label(name, p.Data.PlayerName ?? "?", 18, Color.white, TMPro.TextAlignmentOptions.Left).fontStyle = TMPro.FontStyles.Bold;

            bool waiting = UCColorGrant.HasMod(p) && UCColorGrant.IsWaiting(p.PlayerId);
            string state = !UCColorGrant.HasMod(p) ? UCLocalization.Tr("uc.colorgrant.no_mod")
                         : waiting ? UCLocalization.Tr("uc.colorgrant.waiting")
                         : !UCColors.Safe() ? UCLocalization.Tr("uc.colorgrant.blocked_short")
                         : "";
            float bh = Mathf.Min(40f, h - 8f);
            if (waiting) {
                // The question can be withdrawn (audit 04.10.): the row was blocked until an answer.
                var st = Box(row, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                             new Vector2(-196, 0), new Vector2(230, 30), new Color(0, 0, 0, 0));
                Label(st, state, 15, ColMuted, TMPro.TextAlignmentOptions.Right);
                byte cid = p.PlayerId;
                Btn(row, new Vector2(1, 0.5f), new Vector2(-10, 0), new Vector2(170, bh),
                    UCLocalization.Tr("uc.colorgrant.cancel"), ColGrey, () => { UCColorGrant.Cancel(cid); ClosePanel(); OpenPanel(); });
                return;
            }
            if (state != "") {
                var st = Box(row, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                             new Vector2(-16, 0), new Vector2(300, 30), new Color(0, 0, 0, 0));
                Label(st, state, 15, ColMuted, TMPro.TextAlignmentOptions.Right);
                return;
            }

            byte pid = p.PlayerId;
            Btn(row, new Vector2(1, 0.5f), new Vector2(-10, 0), new Vector2(200, bh),
                UCLocalization.Tr("uc.colorgrant.pick"), ColPurple, () => OpenHex(pid));
        }

        // ---------------------------------------------------------------- host: the hex entry
        [HideFromIl2Cpp]
        private void OpenHex(byte targetId) {
            if (panel != null) { Destroy(panel); panel = null; }
            hexTarget = targetId;
            hexBuffer = "";
            typing = true;

            panel = Canvas("UCColorGrantHex", 9010);
            var card = Card(panel, new Vector2(740, 450));

            var target = PlayerControl.AllPlayerControls.ToArray()
                                      .FirstOrDefault(x => x != null && x.PlayerId == targetId);
            Title(card, UCLocalization.Tr("uc.colorgrant.pick_for", target?.Data?.PlayerName ?? "?"), ColPurple);
            CloseX(card, () => { StopTyping(); ClosePanel(); OpenPanel(); });

            // The typed value, big, in a dark field, with a live crewmate beside it.
            var field = Box(card, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                            new Vector2(36, -96), new Vector2(380, 70), ColField, 12);
            hexLabel = Label(field, "#", 40, Color.white, TMPro.TextAlignmentOptions.Center);
            hexLabel.fontStyle = TMPro.FontStyles.Bold;

            hexPreview = Box(card, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                             new Vector2(470, -82), new Vector2(130, 130), new Color(0, 0, 0, 0));

            var hint = Box(card, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                           new Vector2(36, -176), new Vector2(420, 30), new Color(0, 0, 0, 0));
            hexHint = Label(hint, UCLocalization.Tr("uc.colorgrant.hex_hint"), 15, ColMuted, TMPro.TextAlignmentOptions.Left);

            // Presets: one click instead of six keystrokes, each a small card with its crewmate.
            for (int i = 0; i < Presets.Length; i++) {
                var pr = Presets[i];
                var chip = Box(card, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                               new Vector2(36 + i * 112, -228), new Vector2(100, 112), ColField, 12);
                PreviewOf(chip, pr.col, 66f, new Vector2(0, 12));
                var cap = Box(chip, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                              new Vector2(0, 6), new Vector2(96, 20), new Color(0, 0, 0, 0));
                Label(cap, pr.name, 14, ColMuted, TMPro.TextAlignmentOptions.Center);
                var col = pr.col;
                OnClick(chip, () => { hexBuffer = $"{col.r:X2}{col.g:X2}{col.b:X2}"; RefreshHex(); });
            }

            Btn(card, new Vector2(0.5f, 0), new Vector2(-110, 30), new Vector2(200, 52),
                UCLocalization.Tr("uc.colorgrant.send"), ColGreen, SendHex);
            Btn(card, new Vector2(0.5f, 0), new Vector2(110, 30), new Vector2(200, 52),
                UCLocalization.Tr("uc.colorgrant.back"), ColGrey, () => { StopTyping(); ClosePanel(); OpenPanel(); });

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
            string clash = target != null ? UCColorGrant.TooSimilarTo(target.PlayerId, rgb.Value) : null;
            if (clash != null) {
                // Not sent: the lobby could not tell the two apart (User 04.10.).
                if (hexHint != null) hexHint.text = UCLocalization.Tr("uc.colorgrant.too_similar", clash);
                return;
            }
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
            var card = Card(prompt, new Vector2(600, 360), dim: false);
            Title(card, UCLocalization.Tr("uc.colorgrant.lobby_button"), ColPurple);

            var head = Box(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                           new Vector2(0, -80), new Vector2(-60, 56), new Color(0, 0, 0, 0));
            Label(head, UCLocalization.Tr("uc.colorgrant.prompt", UCColorGrant.ToHex(rgb)), 18,
                  Color.white, TMPro.TextAlignmentOptions.Top);

            // Side by side: what you are now, and what you would become. The question is a
            // comparison, so showing only the new colour would answer half of it.
            int mine = 0;
            try { mine = PlayerControl.LocalPlayer.Data.DefaultOutfit.ColorId; } catch { }
            var stage = Box(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                            new Vector2(0, -12), new Vector2(360, 108), ColField, 12);
            PreviewOfIndex(stage, mine, 84f, new Vector2(-90, 0));
            PreviewOf(stage, rgb, 84f, new Vector2(90, 0));
            var arrow = Box(stage, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                            Vector2.zero, new Vector2(60, 40), new Color(0, 0, 0, 0));
            Label(arrow, ">", 40, ColMuted, TMPro.TextAlignmentOptions.Center, true);

            Btn(card, new Vector2(0.5f, 0), new Vector2(-110, 28), new Vector2(200, 52),
                UCLocalization.Tr("uc.colorgrant.accept"), ColGreen, () => { UCColorGrant.Answer(true); ClosePrompt(); });
            Btn(card, new Vector2(0.5f, 0), new Vector2(110, 28), new Vector2(200, 52),
                UCLocalization.Tr("uc.colorgrant.decline"), ColRed, () => { UCColorGrant.Answer(false); ClosePrompt(); });
        }
    }
}
