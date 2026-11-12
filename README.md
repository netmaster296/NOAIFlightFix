# NO AI Flight Fix

A small BepInEx 5 / Harmony 2 plugin for **Nuclear Option 0.34.6**. It fixes two faults in the game's own fixed-wing AI
that crashed most AI aircraft in our recorded test sessions of the *Cascadia Falling* map:

1. **Target-height dive.** An aircraft at 9 km is told to hold 16-30 m above the ground and dives into the ground or
   sea at full throttle.
2. **Fresh-launch crash.** An aircraft that has just taken off is handed to the combat AI at 75 m and 84-146 m/s, banks
   hard toward its target and sinks into the ground.

- GUID `com.jack.noaiflightfix`, version 1.2.0, GPL-3.0-or-later (see `LICENSE`), copyright netmaster296.

## Requirements and install

- Nuclear Option 0.34.6 and BepInEx 5 (HarmonyX). It works with or without NOCustomMaps; the tested version is
  NOCustomMaps 1.4.2.
- Install with the Nuclear Option Mod Manager (NOMNOM), or copy `NOAIFlightFix.dll` (from the GitHub release) into
  `BepInEx\plugins\`.
- **Host/server only.** The AI runs only there, so install it on the host. With a dedicated server, install it on the
  server. Players who join do not need it. On a client the plugin loads and does nothing.
- It changes **only AI aircraft, never player aircraft**, and writes no networked state or SyncVar. It uses no Burst job
  structs. Because it acts only on AI aircraft and only on the host, it gives no player an advantage.
- It applies on every map by default; the `OnlyCustomMaps` setting limits it to NOCustomMaps maps.

## Compatibility: WingCommand must have `AiSharpTurns` off

**If you run WingCommand (`com.marci.wingcommand`, tested with 0.9.2.2) on the host, set `AiSharpTurns = false` in
`BepInEx/config/com.marci.wingcommand.cfg`.** With it on, WingCommand's AutoAim postfix multiplies the AI's pitch
commands and adds rudder kicks after this plugin's climb-out releases the aircraft, and most fresh take-offs crash.

Measured in the Cascadia Falling tests (game 0.34.6, NOCustomMaps 1.4.2):

| WingCommand `AiSharpTurns` | Result |
|---|---|
| off | 0 of 12 scripted take-offs crashed (A/B test); 0 of 67 take-offs crashed within 60 s across the autoHost runs |
| on | 9 of 12 scripted take-offs crashed; about 30 % of take-offs crashed within 60 s in the two playtests |

Details are under "Not covered and known limits". The setting belongs to a third-party mod; this plugin does not change
it.

## What has been verified in game

From *Cascadia Falling* runs on game 0.34.6 with NOCustomMaps 1.4.2: the saved BepInEx logs of four launch tests on
2026-11-08 that ran this version, and the recorder data (not the logs, which were not kept) of two playtests on
2026-11-09/10:

- The plugin loaded and logged no errors or warnings in any of the saved logs. The start-up line read `NO AI Flight Fix 1.2.0 loaded;
  patched AutopilotPlane.AutoAim, AutopilotPlane.AutoAim (late), Autopilot.TerrainWaypoint,
  AIPilotCombatModes.FlyToTarget, AIPilotTakeoffState.FixedUpdateState, AIPilotCombatModes.FixedUpdateState`.
- The building rule worked: events such as `structure height ignored: ... -> radarStation1 (radarAlt 18 m), keeps
  ~1,550-2,475 m` were logged.
- Airborne-spawn deaths dropped from 54 of 83 (2026-11-08, before the fix) to 2 of 9 and 4 of 15 in the two playtests.
  That drop is the plugin and a mission change together: the same round also lowered the mission's airborne entry
  heights. The plugin's own share shows in the dive statistics: steep dives above 2,000 m went from 30 % to 0 %, and
  fast descents from 37 % to 5 %.
- Take-offs: see the WingCommand table above (0 of 12 and 0 of 67 crashed with `AiSharpTurns` off).

**Not verified in game:** that an airborne entry at 9 km descends at about 40 m/s (the dive and descent statistics above
are measured, the exact rate is not); that the frame-time cost is negligible (expected: one table lookup per AI per
physics step); that the late AutoAim prefix really runs after WingCommand's (Harmony `after` + `Priority.Last`); that a
slow release turns at 60 degrees or less; the whole-mission summary line format against a real log (the example below
uses made-up numbers); behaviour on a dedicated server.

## What goes wrong (decompile evidence)

The file:line references below are to the decompiled `Assembly-CSharp` 0.34.6 and NOCustomMaps 1.4.2.

### 1. Target-height dive

- `AIPilotCombatModes.FlyToTarget` copies the target's radar altitude as the height to hold whenever it is above 10 m:
  `if (currentTarget.radarAlt > 10f) targetHeight = currentTarget.radarAlt;` (AIPilotCombatModes.cs:298-301). It also
  sets full throttle and terrain-following (:285-287).
- A building reports half its height as its radar altitude: `radarAlt = definition.height * 0.5f` (Building.cs:242).
  A radar station gives 18 m, a fuel tank 16 m and a large factory 30 m. Low helicopters (35-54 m) and the radar-missile
  evasion (`targetHeight = 10f`, :1163) lower the hold height the same way.
- `FixedUpdateState` passes that height to the autopilot each step (:1262-1263). The terrain-following waypoint sits at
  ground + hold, `max(v,100) * 6 * 9/gLimit` ahead (TerrainWarningSystem.cs:44-52; Autopilot.cs:215-232). On a custom
  map NOCustomMaps replaces that waypoint (TerrainWaypointPatch.cs; AITerrain.cs Waypoint) and clamps it to +-60 degrees
  (TerrainEnvelope.cs:11, :86-94). The stock waypoint has no clamp at all.
- The terrain warning looks only 5 s ahead and adds just `urgency * 100 m` of lift (TerrainWarningSystem.cs:20-42;
  AutopilotPlane.cs:63-68). That is too little and too late at 400-530 m/s.
- In our recorded 2026-11-08 session, 43 of the 45 ground or sea impacts by airborne-spawned aircraft began with this
  drop.

### 2. Fresh-launch crash

- `AIPilotTakeoffState` switches to `AIPilotCombatModes` as soon as `radarAlt > 75f`, whatever the speed
  (AIPilotTakeoffState.cs:112-120).
- The combat state then holds about that height (`targetHeight = radarAlt`, AIPilotCombatModes.cs:138), allows 180
  degrees of bank (:1263) and turns toward its destination.
- During the bank, the roll error scales the pitch command by `Lerp(1, 0.1, |rollErr| * 0.015)`. At 60 degrees or more
  of roll error, pitch is cut to 10% (AutopilotPlane.cs:116).
- Below corner speed (84-146 m/s against 180-200 m/s), the nose drops and the aircraft hits the ground 12-48 s after
  lift-off. With a required turn of 90 degrees or more, 32 of 40 crashed. JBLM, where Runway 18 departs away from the
  front, lost 30 of 39 in that session.

## What the plugin changes

| Patch | Kind | Effect |
|---|---|---|
| `AIPilotCombatModes.FlyToTarget` | prefix + postfix | If the target is a `Building` or `Ship`, the copy of its radarAlt (:298-301) is undone and the aircraft keeps its own height. The building is then treated like a vehicle target: only `ManageAltitude` (:507-535) moves the hold height, about -5 m/s net (+5 at :513-516, -10 at :534), so the approach is slower than stock. Not applied while the AI has a boresight gun selected (`currentWeaponInfo.gun && boresight`, :119, :321), so strafing keeps the stock low ingress (the descent limit still keeps that drop gradual). `Ship` matters only for a vessel on a lake above sea level: a ship at sea reports about 0 m (`Unit.CheckRadarAlt`, Unit.cs:1646-1661, and `Ship` has no radarAlt override). |
| `AIPilotCombatModes.FixedUpdateState` | prefix + finalizer | Marks "this AutoAim call comes from an AI combat state on the host", so the patches below act on nothing else. Also refreshes the NOCustomMaps switch once a second. |
| `AutopilotPlane.AutoAim` | prefix (runs before NOCustomMaps' own prefix) | **Descent limit:** with terrain-following on, the hold height passed in is raised so that the aircraft descends at about `DescentRate` (40 m/s), or `EvadeDescentRate` (80 m/s) while a missile tracks it, rather than at up to 60 degrees in a dive. AutoAim later scales the hold by `Lerp(0.1, 1, (airspeed - 0.9 x landingSpeed) / landingSpeed)` (AutopilotPlane.cs:46); since 1.1.0 the raised hold is divided by that factor so slow aircraft get the same limit. Low-level holds and climbs are never changed. **Climb-out:** see below. |
| `AutopilotPlane.AutoAim` | second prefix, `Priority.Last`, ordered after WingCommand (`com.marci.wingcommand`) and NOCustomMaps | During a climb-out and the turn-out guard: `effort = min(effort, 1)` and `bankAllowed = min(bankAllowed, MaxBank or TurnOutMaxBank)`. The stock combat AI never passes an effort above 1 (aimEffort 0.25-1, AIPilotCombatModes.cs:286-1179), so this only undoes another mod's boost (see WingCommand below). Does nothing at any other time. |
| `Autopilot.TerrainWaypoint` | postfix (after NOCustomMaps' prefix) | **Dive cap:** above `CapAboveRadarAlt` (2,000 m), the terrain-following waypoint is never more than `MaxDiveAngle` (30 degrees) below the horizontal. It only ever raises the waypoint, so terrain clearance never shrinks. |
| `AIPilotTakeoffState.FixedUpdateState` | postfix | Detects the 75 m handover and starts a **climb-out** for that aircraft. |
| `AIPilotCombatModes.LeaveState` | postfix | Ends a climb-out if the AI leaves combat (landing, parked, destroyed). |

**Descent-limit maths** (in `FlightMath.cs`): the hold is kept at least at
`radarAlt - reach * (tan(asin(rate / speed)) + cone)`. `reach` is the look-ahead, doubled under NOCustomMaps, whose
envelope samples the ground out to twice the look-ahead with the hold as clearance plus a 0.02 cone (TerrainEnvelope.cs:9,
:41-64, :105-108). `cone` is 0 on stock maps. Over level ground the result is a steady descent at `rate`. The real
`targetHeight` field is not changed (except by the building/ship rule), so the AI still ends up at the height it wanted;
it just gets there at a realistic descent rate.

**Climb-out** (applies only after a runway, carrier or ski-jump take-off, never to airborne spawns):

- The aircraft holds the heading it had at the handover and climbs on a flight-path angle that rises with speed:
  `MinClimbAngle` (6 degrees) at take-off speed, up to `MaxClimbAngle` (15 degrees) at corner speed. For comparison, the
  take-off state itself flies about 9.5 degrees (AIPilotTakeoffState.cs:177).
- During the climb it has full throttle, bank limited to `MaxBank` (25 degrees), velocity-aimed as the take-off state
  does (:185), and terrain-following off.
- The aim point is far enough ahead (`max(3000, 10 * speed)` m) that NOCustomMaps' floor under straight flight still
  lifts it over rising ground (TerrainEnvelope.FloorApplies, :119-122). The game's terrain warning stays active.
- The climb ends at the first of:
  - `MinHeight` (800 m since 1.2.0) above ground **and** `SpeedFraction` (0.85) x corner speed;
  - `MaxHeight` (1,500 m);
  - `MaxSeconds` (45 s);
  - a missile tracking it (`EndOnMissile`).
- At the release, `targetHeight` is raised to the current radar altitude, as the game's own handover does
  (AIPilotCombatModes.cs:138). The combat AI then turns the aircraft.
- **Turn-out guard:** since 1.2.0 (`TurnOutAfterEveryRelease`, default on) the turn back toward the target is kept gentle
  after every release, not only a slow one: bank at most `TurnOutMaxBank` (60 degrees) instead of the combat AI's 180
  (:1263), effort at most 1. It ends after `TurnOutSeconds` (25 s), or early when the aircraft is at `TurnOutSafeHeight`
  (1,500 m above ground) and corner speed together, or when a missile tracks it. It never applies while a missile is
  tracking the aircraft, and a missile release does not start it. (In 1.1.0 the guard followed only a slow release,
  one that came from the height cap or the time limit below `SpeedFraction` x corner speed.)

**Host only and AI only.**
- The game starts AI states only when the aircraft has no player and `IsServer` is true (Pilot.cs:242-256). Every
  patch checks again: `aircraft.IsServer`, `pilot.playerControlled == false`, `pilot.player == null`,
  `aircraft.Player == null`.
- On a client the plugin loads and does nothing.

**Never throws into the game.** Every patch body is wrapped in try/catch. On an exception, that one fix logs the error
once and switches itself off until restart. If a target method or field is missing (after a game update), that fix is
not patched and the start-up log says so.

## Config (`BepInEx/config/com.jack.noaiflightfix.cfg`)

| Section / key | Default | Meaning |
|---|---|---|
| General / Enabled | true | Master switch, read at start-up. |
| General / OnlyCustomMaps | false | true = act only while a NOCustomMaps map is loaded (`CustomMaps.AITerrainSwitch.Active`). Both faults are in the stock AI, so the default applies everywhere. |
| General / LogEachEvent | false | One log line per limited descent, ignored structure height and climb-out, for testing. |
| TargetHeight / IgnoreBuildingAndShipHeight | true | The building/ship rule (not applied with a boresight gun selected). |
| DiveLimit / Enabled | true | The descent limit. |
| DiveLimit / DescentRate | 40 | m/s, normal flight. |
| DiveLimit / EvadeDescentRate | 80 | m/s, while a missile is tracking the aircraft. |
| DiveLimit / DiveCapEnabled | true | The dive cap. |
| DiveLimit / CapAboveRadarAlt | 2000 | m above ground. |
| DiveLimit / MaxDiveAngle | 30 | degrees below horizontal. |
| ClimbOut / Enabled | true | The climb-out. |
| ClimbOut / MinHeight | 800 | m above ground (with SpeedFraction). 400 in 1.1.0 and earlier. |
| ClimbOut / SpeedFraction | 0.85 | x the type's `cornerSpeed`. |
| ClimbOut / MaxHeight | 1500 | m above ground, release even if slow. |
| ClimbOut / MaxSeconds | 45 | s, release regardless. |
| ClimbOut / MinClimbAngle, MaxClimbAngle | 6, 15 | degrees, at take-off speed and at corner speed. |
| ClimbOut / MaxBank | 25 | degrees. |
| ClimbOut / FullThrottle | true | |
| ClimbOut / EndOnMissile | true | |
| ClimbOut / TurnOutSeconds | 25 | s, length of the turn-out guard; 0 = off. (20 in 1.1.0.) |
| ClimbOut / TurnOutMaxBank | 60 | degrees, bank limit during the turn-out guard. |
| ClimbOut / TurnOutAfterEveryRelease | true | Apply the turn-out guard after every release, not only a slow one (1.2.0). |
| ClimbOut / TurnOutSafeHeight | 1500 | m above ground at which the every-release guard may end early (with corner speed). |

All values except `Enabled` are read live, so they can be changed in BepInEx.ConfigurationManager during a mission.
BepInEx keeps the values already in an existing config file, so after updating from 1.1.0 or earlier, set `MinHeight`
and `TurnOutSeconds` by hand to get the new defaults.

## Log

On start-up the plugin writes one line listing what it patched. It then writes one summary line per mission, when the
mission ends or the game quits. The format is shown below; the numbers are made up for illustration, not measured:

```
mission 'Cascadia Falling': 212 AI aircraft handled; descents limited 61 (55 aircraft), dives capped 9, building/ship heights ignored 24; climb-outs held 92 (released: height+speed 70, height cap 3, time limit 12, missile 2, left combat 4, unfinished 1); turn-out guards 15 (ended: speed 9, time 5, missile 1); climb-out releases by type: CAS1 hs 3/cap 0/time 5/msl 0/left 1, Fighter1 hs 20/cap 0/time 1/msl 1/left 0
```

"Left combat" includes aircraft destroyed during the climb-out. "Unfinished" means still climbing when the mission
ended, or removed without leaving the state. The by-type list (keys are `definition.jsonKey`) shows how often the time
limit or height cap, rather than height + speed, decided the release for each type. A turn-out guard still running at
the end of a mission is not counted under "ended".

## How to verify in a session

Run a mission with a recorder on the host (we used our own recording plugin, NOTelemetry, which is not part of this
release) and compare it with a session recorded without this plugin. Compare like with like: our baseline session had
WingCommand 0.9.2.2 with `AiSharpTurns = true`, so judge the fix against a run with the same WingCommand setting, or with
it off as recommended above.

- Take-offs: look for crashes with a small age, or a death within 60 s of the take-off-to-combat handover. Baseline
  in our 2026-11-08 session: JBLM 30 of 39, Comox 6 of 30, FleetCarrier1_7 6 of 8.
- Airborne spawns: count crashes within 400 s. Baseline: 54 of 83.
- The take-off to combat handover itself is unchanged (`from_state AIPilotTakeoffState`, `to_state AIPilotCombatModes`);
  the climb-out is not a separate AI state.
- In flight traces after a handover, `roll` should stay within about 25 degrees and `radar_alt` should rise to 800 m or
  more (or 45 s should pass) before the heading changes. In a descent:
  - `vspeed` should stay near -40 m/s (-80 with a missile).
  - the waypoint elevation should be a few degrees, not -50 to -60.
  - `target_height` should no longer drop to 16-30 m when the target is a building.
  - `target_height` still shows the AI's own value, because the descent limit changes only what the autopilot is
    given. A helicopter target therefore still shows about 50 m there, while the aircraft descends slowly.
- After a slow release, `roll` should stay within about 60 degrees until the speed reaches 0.85 x corner or the turn-out
  time passes.
- `BepInEx/LogOutput.log`: the summary line above. With `LogEachEvent = true` there is also one line per event,
  including "turn-out guard ended (Speed|Timeout|Missile)".

## Offline checks done (2026-11-08, 1.0.0 and 1.1.0)

These were done before the in-game tests above; the test harness is not part of this repository.

- `dotnet build -c Release`: 0 errors, 0 warnings.
- DLL scan: the metadata TypeRefs and MemberRefs, plus a raw string scan, show no `NuclearOption.Jobs.*`, no `*Fields`
  struct, no `PtrAllocation`, no Burst or Unity.Collections type.
- A harness compiling this plugin's own `FlightMath.cs` replayed the recorded 2026-11-08 session. Results:
  - All 38 unit checks of the maths pass (24 in 1.0.0, plus 14 for the 1.1.0 hold scaling and turn-out guard; e.g. a
    95 m/s aircraft with a 60 m/s landing speed descends at 40 m/s with the scaling against 63-78 m/s without it).
    The replay sections below do not model the hold scaling. It is 1 at or above 1.9 x landing speed; the replayed
    aircraft fly at 200 m/s or more, so it matters there only for a type landing faster than about 105 m/s (not
    checked per type).
  - The waypoint geometry model (NOCustomMaps follow angle with the 2x look-ahead envelope) matches the 2,486 recorded
    waypoint elevations with a median error of 0.8 degrees (p90 9.3). A plain look-ahead gives a median error of 10.4.
  - Of 63 high-altitude target-height collapses, 22 were toward a building, which the building/ship rule removes
    outright. In a point-mass replay of all 63 (constant speed, 2 s lag, level ground), the steady descent with the
    limit is 40 m/s (max 41) against a median peak of 205 m/s without it. The median time to 500 m above ground goes
    from 56 s to 188 s.
  - Of 92 take-off handovers, the stock combat AI met the release gate (400 m and 0.85 x corner within 45 s, the
    1.0.0 gate) in only 7. None of the 42 that crashed within 60 s had met it first. The climb-out angle at the handover
    would be 7.4-10.6 degrees (median by field). Assuming constant speed, the estimate to 400 m is 17-37 s; this is an
    estimate, not a measurement.

## Not covered and known limits

- The Fighter1 / SmallFighter1 loss of control at 5-11 km (high angle of attack before any target) is not addressed
  here.
- Climb-out heading is the runway heading. At JBLM (Runway 18) the aircraft climbs south for up to 45 s before turning
  north. That is realistic, but slower to reach the front than stock's (fatal) immediate turn.
- Straight climb-out terrain was checked offline on all 51 fixed-wing runway directions of the Cascadia map (hand-over
  at 75 m over the runway end, 4-10 degrees at 90-146 m/s, 45 s or 1,500 m): clearance never falls below the starting
  75 m; the worst demanded angle is 2.5 degrees at Port Angeles Runway 15. Mountain pads (Hurricane Ridge, Stevens Pass,
  the FOBs) are 60 m helipads used only by helicopters and tiltwings, whose states this plugin does not touch.
- **WingCommand 0.9.2.2** (`AiSharpTurns`) patches `AutopilotPlane.AutoAim` for **all** AI fixed-wing aircraft in the
  combat state, not only commanded wingmen (its `AiSharpTurnPatch`; eligible above 60 m AGL, above 1.15 x landing speed,
  gear up). When the destination is 30 degrees or more off the velocity its prefix sets effort 2-2.5, which makes
  AutoAim's `num3` 1 and removes the game's low-speed damping of bank and turn rate (AutopilotPlane.cs:47, :50, :57,
  :99), and raises the bank limit to 70-140 degrees with `Math.Max`. Its postfix multiplies pitch by up to 2.2 and adds
  rudder kicks. Since 1.1.0 this plugin's late AutoAim prefix caps effort and bank again during the climb-out and the
  turn-out guard. It cannot undo WingCommand's postfix (pitch multiplier, rudder kick, throttle/brake), which still acts
  during the turn-out; WingCommand brakes only above 1.15 x corner speed, so not in the guard. During the climb-out
  itself the aim point is straight ahead, so WingCommand's 30 degree test is normally not met. In the game tests, take-offs
  with `AiSharpTurns` on crashed (table above), so keep it off on the host. It is a third-party mod's setting.
- The descent-limit geometry assumes NOCustomMaps' terrain waypoint is working on a custom map. If NOCustomMaps'
  waypoint patch has faulted (it disables itself) the real waypoint is the stock one and the limit allows about 84 m/s
  instead of 40 (review estimate at 230 m/s). Not detected.
- `EvadeDescentRate` (80 m/s) also slows the game's radar-missile defensive dive (`targetHeight = 10f`, :1163): from
  8 km that takes about 100 s. Whether that trade-off is wanted is an open question.
- The per-mission line is written when the game state leaves SinglePlayer or Multiplayer, when the mission object
  changes, or on quit. On a dedicated server whose state is neither, it is written only on quit.

## Build

`dotnet build -c Release` gives `bin/NOAIFlightFix.dll`. The csproj has a default game folder (a Steam library path on
the author's machine); override it with `-p:GameDir=...`.

## Changes

See [CHANGELOG.md](CHANGELOG.md). Current version: 1.2.0.

## AI assistance

This plugin and its documentation were written with AI assistance (Claude, by Anthropic) and tested as described under "What has been verified in game" and "Not verified in game".
