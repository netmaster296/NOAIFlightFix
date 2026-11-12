# Changelog

## 1.2.0 (2026-11-08)

Made after the first live test of the Cascadia Falling map: 8 of 12 scripted take-offs were released at corner speed and
400 m and then sank into the ground 10-20 s later in their first hard turn toward a target.

- The gentle turn-out (bank <= `TurnOutMaxBank`, effort <= 1) now follows every release (`TurnOutAfterEveryRelease`,
  default on) for `TurnOutSeconds` (now 25), ending early only at `TurnOutSafeHeight` (1,500 m) and corner speed
  together or when a missile tracks the aircraft.
- The climb-out releases at `MinHeight` 800 m (was 400).
- An existing config keeps its old values: set `MinHeight` and `TurnOutSeconds` by hand.

## 1.1.0 (2026-11-08, after review)

- Late AutoAim prefix after WingCommand caps effort and bank during the climb-out.
- Turn-out guard after a slow release (`TurnOutSeconds`, `TurnOutMaxBank`).
- Descent limit accounts for AutoAim's hold scaling at low speed (AutopilotPlane.cs:46).
- NOCustomMaps switch refreshed from the combat-state patch as well as from `Update`.
- Building/ship rule skipped with a boresight gun selected.
- Summary line adds turn-out guards and climb-out releases by type.
- README corrections (WingCommand, Ship, baselines, terrain check).

## 1.0.0 (2026-11-08)

First build.
