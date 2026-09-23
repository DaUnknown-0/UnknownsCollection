# Unknown's Collection

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin that adds **24 custom roles and 6 modifiers** to
[The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles) (TOR) for *Among Us*.

Each role is layered on top of TOR purely through Harmony patches: **the original TOR source is never
modified**. The plugin only takes a hard dependency on TheOtherRoles.

> The roles are client-side, so the lobby can only start when **every player runs the same Unknown's
> Collection version** (the host gets a warning otherwise). All Impostor roles are also pickable in TOR's
> **Role Draft**.

📖 **Full documentation:** <https://daunknown-0.github.io/tor-mods-wiki/unknowns.html> (searchable, EN/DE).

This mod is not affiliated with Among Us or Innersloth LLC, and the content contained therein is not endorsed or otherwise sponsored by Innersloth LLC. Portions of the materials contained herein are property of Innersloth LLC. © Innersloth LLC.

---

## Roles

### Impostor

- **The Tesla**: Charges exactly two players during a meeting (one **+**, one **−**). While the charged
  pair stays too close, a hidden countdown drains; separating pauses it, a meeting refills it. At zero,
  both die. Victims see a `⚡ charged` / pulsing `⚡ danger` warning but never the exact timer.
  *Options:* trigger distance, countdown seconds, min alive players, can-charge-self, self-charge-kills,
  grace after meeting.
- **The Saboteur**: Once per round: sabotage a task console (lethal when the victim completes it, with a
  crew search/defuse counterplay) or lay an invisible stun trap. *Options:* tokens per round, task/trap
  costs, extra kill cooldown, trap count/stun/limp, crew search & defuse, min-alive gates.
- **The Silencer**: Marks a victim with the SILENCE button; the marked player is **muted** in the next
  meeting (no vote, no chat) and shown a red `[MUTED]` tag in-game and on their vote area so everyone can
  also mute their voice client. *Options:* mark cooldown, targets per round, muted-can-still-skip,
  show-in-game-marker.
- **The Poisoner**: Kills poison the victim's body; the next player to **report** a poisoned body becomes
  poisoned and dies after **X meetings** unless cured by the **Medic's Antidote**. The doomed reporter
  gets a private "you don't feel so good" message in the meeting. *Options:* poison death after meetings
  (min 2), Medic antidote uses per round, max poisoned bodies per round.
- **The Illusionist**: Records a movement path and replays it as an **unkillable, shielded clone** that
  fools would-be killers. *Options:* max recording length, playback cooldown, blocked-kill-costs-cooldown,
  clone-shield-visible-to-everyone.
- **The Maniac**: Plants a bomb on a player; the bomb can be **passed** to a nearby player before it
  detonates, killing whoever holds it at the end. *Options:* bomb cooldown, unaware delay, pass window,
  explosion range.
- **The Shade**: Kills make the victim's body **vanish**; others find it by walking close, then it
  auto-reports. Bait victims stay visible so their own report still exposes the killer. *Options:* find
  distance.
- **The Manipulator**: Makes the ship's **security devices lie** for a while, for everyone: the admin
  table shows a fabricated (but synced and believable) player distribution, and vitals shows dead
  players as alive. Works with whatever devices the map has (Skeld/Mira: admin; Fungle: vitals;
  Polus/Airship: both); comms sabotage keeps its normal "signal lost" look. *Options:* cooldown,
  duration, fake admin, fake vitals.
- **The Auditor**: Every task a **living** crewmate finishes drops into the Auditor's own task list. Doing
  it himself **resets that exact task** for that exact crewmate: server-authoritatively, so the task bar
  really falls. His queue holds only a few tasks at once (further completions are lost), each with its own
  lifetime that freezes while he is working on it, and a task is untouchable once its owner is known to be
  dead. His **kill cooldown scales with the number of reverts**: punished at the start, dangerous once he
  has put in the shifts. *Options:* queue size, task lifetime, cooldown multiplier at 0 / at full reverts,
  reverts for full effect, sees-who-completed-it (auto-off when a Spy could exist), can't-guess-the-Snitch,
  victim notification.
- **The Werewolf**: An Impostor with a second shape. While the lights are sabotaged an alpha charge
  builds; as the last living Impostor (by default) the Werewolf can then transform: faster, with a shorter
  kill cooldown, while the whole map drops into wolf darkness where everyone else is down to a flashlight
  beam. With **Nightfall** installed the transformation switches to a first-person view. *Options:* wolf
  kill cooldown reduction, wolf speed, charge time, form duration, silver interaction, howl, charge reset
  on lights fix, only as last Impostor, Spy counts as Impostor, form restrictions, exhaustion after the
  revert, Trapper/Saboteur traps and Deputy handcuffs against the wolf, ignores Bait.

### Crewmate

- **The Siphoner**: Toggles a DRAIN aura: a nearby Impostor's kill cooldown is held at full (and, for a
  while after, a lingering deficit) so they can't kill; optionally also holds the sabotage cooldown.
  *Options:* drain range, penalty per tick, tick interval, scale-with-distance, warn drained impostor,
  also-drain-sabotage + block seconds, drain cooldown.
- **The Witness**: If the Witness is the **sole living crewmate who sees a kill** (in range + clear line
  of sight), the killer is noted: their name glows red for the Witness; if the Witness dies and their body
  is reported, everyone sees the note; if the Witness survives to the meeting, anonymous notes go to a few
  random players. *Options:* sight range factor, red name permanent, note recipients.
- **The Scout**: Activatable ability: go **transparent and fast**, and lights/sabotage don't reduce
  vision while it's active. *Options:* ability duration, cooldown, speed multiplier, transparency.
- **The Beacon**: Lights never reduce the Beacon's vision, and nearby crewmates **share the Beacon's full
  vision**. *Options:* share radius, not-guessable.
- **The King**: A crewmate with no tasks and no powers, but a court: the King knows his advisor's role
  from the start. *Options:* the King is always TOR's VIP.
- **The Hunter**: Not a rolled role but an event inside a Werewolf round: once every non-Werewolf
  Impostor is dead, the living original Sheriff rises as the Hunter, the one crewmate the beast should
  fear. *Options:* enabled, only from the original Sheriff, flashlight multiplier, can kill neutral
  killers, Deputy promotion, Hunter guessing, Monster Hunter hat.

### Neutral

- **The Bug**: **Survive to the end to win alone**: when a **team** win (Crewmate, Impostor or Jackal)
  triggers while the Bug is alive, the Bug hijacks it. Neutral solo wins (Jester, Arsonist, Vulture, …)
  are left alone. *Options:* min players; optional glitchy win-screen effects.
- **The Follower**: Takes over the **full role of the first player to die** (team, ability and win
  condition: including Impostor/Neutral). *Options:* min players.
- **The Collector**: The host scatters **relics** across the map (anchored near task consoles, on
  every map). Only the Collector sees them; impostors can optionally **sense** a faint shimmer nearby.
  Collecting takes a few seconds of **channeling** (moving cancels; nearby players hear a quiet
  glitter). Enough relics win: instantly or, per option, only if the Collector also survives to the
  end (then it hijacks the next team win, like the Bug). *Options:* relics spawned/needed, channel
  duration, win mode, impostor sense + radius, has tasks.
- **The Copycat**: **Learns abilities by witnessing them**: Camouflage, Morph, Shield (unkillable),
  Shoot (Sheriff-style, backfires on Crew) and Vent (vent access). Each learned ability is a button; the
  Copycat wins **with the winning team** if alive and it used enough abilities. *Options:* max stored
  abilities, has tasks, abilities needed to win.
- **The Pelican**: A neutral killer that does not kill, it **swallows**. Victims vanish into the belly
  and can still come back, until the first meeting digests them. At two survivors the round turns into an
  open hunt with its own countdown. *Options:* swallow cooldown, hunt countdown, has tasks, hunt also
  blocks sabotage.
- **The Necromancer**: **Raises fresh corpses** (a body only exists between a kill and the next
  meeting). A raised player, a thrall, walks, talks and does tasks like anyone else, but their vote
  weighs nothing and they cannot guess, and nobody else can tell. The Necromancer wins at a meeting once
  enough of the living belong to him; if he dies, every thrall dies with him. *Options:* raising
  duration, raise cooldown, corpse freshness window, win threshold, minimum thralls, vents, tasks,
  excludes the Poltergeist.
- **The Stalker**: A neutral with **one target**. It watches them through a narrow torch cone nobody else
  can see; once the stalk meter is full it may strike, and any death of the target after that wins the
  game for the Stalker alone. *Options:* stalking time needed, target sees the meter, strike cooldown,
  what happens when the target dies early, tasks, vents.

### Ghost

- **The Poltergeist**: not a start role: the **first player to die** (kills always; exile too, per
  option) rises as the Poltergeist and **keeps its original team**: a dead Crewmate haunts for the
  crew, a dead Impostor for the impostors. Abilities share an **energy pool** (regenerates over time,
  never in meetings): **Door Haunt** slams a single door shut; **Hex** curses the nearest living player
  (speed boost / blindness / night vision); **Ghost Hand** counts as one hand on a Reactor/Seismic
  console while channeling; **Manifest** appears as a copy of a nearby living player (can vent, per
  option): killing the manifest poofs it without a body and refunds the killer's cooldown per option.
  *Options:* exile counts, energy max/regen/start, per-ability costs & durations, hex effect toggles,
  manifest vent & kill refund, keeps tasks.

### Modifiers

Picked by the host at the end of the intro, never on top of another modifier.

- **The Gambler** (crew): Bets on the round itself: wagers on kills, votes, tasks and deaths, which the
  next meeting settles for a reward or a penalty. *Options:* bet cooldown, open bets at once, kill bet
  window, task and vote bet thresholds, speed change and duration, kill cooldown change, Impostors are
  told about cooldown changes.
- **The Void** (crew, after-death modifier): The first ejection that would hit the Void does not
  happen, the vote turns into a skip (once per game). The exile gets its own scene: the ballots fly
  straight through the Void into a rift. *Options:* own vote counts, exile scene (all maps, Unknown's
  Atlas maps only, off).
- **The Sleepwalker**: Dozes off in the meeting and wakes up in a random spot of the map instead of at
  the table, with no cue for anyone else. *Options:* who can get it, wake-up chance per meeting, minimum
  distance from the table, also at game start.
- **Last Words**: The carrier can write one sentence during the round (N opens the box). If they die,
  it appears in the next meeting as an anonymous chat bubble. *Options:* who can get it, maximum length.
- **The Sixth Sense** (crew): The screen edge pulses like a heartbeat whenever a killer whose kill is
  ready stands within range. It never says who or where. *Options:* range, counts the Jackal and
  Sidekick, pulse grows with proximity.
- **The Colorblind**: The carrier plays in black and white; the MedBay scan cures it. *Options:* who can
  get it, tasks stay in colour, MedBay cure, cure for neutrals and Impostors.

Every role and modifier also has the two standard options: a **spawn chance** and a **minimum lobby size to spawn**.

---

## Audio & visuals

Every role ships **custom button and role textures** and its own **stereo sound cues** for ability
activations, kills, meeting reveals and wins (31 sound effects in total). Particle, glow and reveal
effects share a common `UCFx` base so the whole collection stays visually consistent. Assets are loaded
and cached once via `UCAssets`.

---

## Host tooling hook: PlayerTuning

Besides the roles, the mod carries one module that is **not** a role: `PlayerTuning` (module byte 213
on the shared RPC channel). It lets a host tool set per-player overrides that every client then
applies locally: movement speed, ability/kill cooldown, a vent ban, and a full task-list
replacement. It has no options and never acts on its own: it only reacts to what a host sends, and
every send is gated on the usual "everyone runs this build" handshake. State clears on round start
and when joining another lobby.

This exists here because the *effects* are client-side (movement is client-authoritative, cooldowns
tick locally, vents resolve locally), so the receiving code has to be present on every client.

---

## Installation

1. Install [BepInEx (IL2CPP)](https://github.com/BepInEx/BepInEx) and
   [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles).
2. Drop `UnknownsCollection.dll` into `Among Us/BepInEx/plugins/` (next to `TheOtherRoles.dll`).
3. Launch the game. Every player who should see the roles needs the mod (same version).

---

## Versioning

| Form | Meaning |
|---|---|
| `vX.Y.Z` | Stable release (published as GitHub *latest*). |
| `vX.Y.Z.W` | Test build — the 4th component `W` is the test number (published as a GitHub *pre-release*). |

The version line in-game shows `vX.Y.Z` for stable builds and `vX.Y.Z.W` for test builds. The `.W`
suffix is only shown while the shared **"Test Versions"** toggle (top-right of the Mod Manager) is
**on** — it is **off by default**, so normal players see a clean `vX.Y.Z`.

A stable `vX.Y.Z` always **supersedes** its own test builds `vX.Y.Z.W` (semantic ordering).

## Mod Manager & self-updater

When *Useful TOR Stuff* (the Mod Manager owner) is installed, Unknown's Collection registers itself in
the Mod Manager and exposes a **channel-aware self-updater**:

- **Test Versions OFF** → updates to / stays on the newest **stable** release.
- **Test Versions ON** → updates to the newest **test build**, but only when it is genuinely ahead of
  the latest stable.
- Toggling the switch can download/install the matching channel build (with a confirmation prompt); it
  only downloads when the target is actually a different version.

The updater only ever talks to the GitHub Releases API (subject to GitHub's unauthenticated
60 requests/hour limit); gameplay never does.

---

## Building from source

Requires the .NET 6 SDK. The project references `TheOtherRoles.dll`:

```bash
dotnet build -c Release -p:TheOtherRolesDll=/path/to/TheOtherRoles.dll
```

`nuget.config` adds the BepInEx and Reactor package feeds. Releases are built automatically by the
GitHub Actions workflow on a `vX.Y.Z` / `vX.Y.Z.W` tag.

---

## Credits & license

- Created by **DaUnknown-0**.
- Based on [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles) (GPL-3.0).
- Licensed under **GPL-3.0-or-later** — see [LICENSE](LICENSE).
