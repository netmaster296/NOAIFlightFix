// NO AI Flight Fix - realistic descents and climb-outs for the game's fixed-wing AI.
// Copyright (C) 2026 netmaster296
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any
// later version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE file.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AIFlightFix
{
    /// <summary>
    /// Host-side fixes for two fixed-wing AI faults in Nuclear Option 0.34.6 (decompile references in README.md):
    /// <list type="number">
    /// <item>Target-height dive. AIPilotCombatModes.FlyToTarget copies a target's radarAlt as the height to hold when it
    /// is above 10 m (AIPilotCombatModes.cs:298-301); a Building reports half its height (Building.cs:242), so an
    /// aircraft at 9 km is told to hold 16-30 m and dives at full throttle. Fix: a Building's or Ship's radarAlt is not
    /// adopted, the hold height handed to the autopilot never sits so far below the aircraft that it would descend faster
    /// than about DiveLimit.DescentRate, and above DiveLimit.CapAboveRadarAlt the terrain-following waypoint is never more
    /// than DiveLimit.MaxDiveAngle below the horizontal.</item>
    /// <item>Fresh-launch crash. AIPilotTakeoffState hands over to the combat AI at 75 m above ground whatever the speed
    /// (AIPilotTakeoffState.cs:112-119), and a hard turn at 84-146 m/s cuts the pitch command to 10%
    /// (AutopilotPlane.cs:116). Fix: after that handover the aircraft climbs straight ahead with the wings near level
    /// until it is ClimbOut.MinHeight above ground and near corner speed (or a height cap or time limit), then the combat
    /// AI turns it. A slow release (height cap or time limit) gets a gentle turn-out (bank <= ClimbOut.TurnOutMaxBank,
    /// effort <= 1) for up to ClimbOut.TurnOutSeconds; a second AutoAim prefix ordered after WingCommand enforces both
    /// caps against WingCommand's AI sharp-turn boost.</item>
    /// </list>
    /// Only AI aircraft on the server/host are touched (the AI runs only there, Pilot.cs:245-249). No networked state, no
    /// SyncVar writes, no Burst job structs. Every patch is wrapped: on an exception that fix switches itself off.
    /// </summary>
    [BepInPlugin(Id, "NO AI Flight Fix", Version)]
    [BepInDependency(CustomMapsId, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.jack.noaiflightfix";
        public const string Version = "1.2.0";
        internal const string CustomMapsId = "com.javoski.custommaps";
        // WingCommand 0.9.2.2 (Plugin.cs:15, :90) patches AutopilotPlane.AutoAim for all AI fixed-wing in combat
        // (AiSharpTurnPatch); our late prefix is ordered after it.
        internal const string WingCommandId = "com.marci.wingcommand";

        internal static ManualLogSource Log;

        // ---- config --------------------------------------------------------------------------------------------------
        private static ConfigEntry<bool> Enabled, OnlyCustomMaps, LogEachEvent;
        private static ConfigEntry<bool> IgnoreStructureHeight;
        private static ConfigEntry<bool> DiveLimitOn, DiveCapOn;
        private static ConfigEntry<float> DescentRate, EvadeDescentRate, CapAboveRadarAlt, MaxDiveAngle;
        private static ConfigEntry<bool> ClimbOutOn, ClimbFullThrottle, ClimbEndOnMissile;
        private static ConfigEntry<float> ClimbMinHeight, ClimbSpeedFraction, ClimbMaxHeight, ClimbMaxSeconds,
                                          ClimbMinAngle, ClimbMaxAngle, ClimbMaxBank, TurnOutSeconds, TurnOutMaxBank, TurnOutSafeHeight;
        private static ConfigEntry<bool> TurnOutAfterEveryRelease;

        // ---- per-AI state (dies with the state object) ---------------------------------------------------------------
        private sealed class AiState
        {
            public bool Climbing;
            public float ClimbStart, ClimbStartAlt, ClimbStartSpeed;
            public Vector3 ClimbDir;
            public bool DiveLimited, DiveCapped;
            public Unit LastIgnored;
            /// <summary>Time.timeSinceLevelLoad of the climb-out release that started a turn-out guard; -1 = none.</summary>
            public float ReleasedAt = -1f;
        }
        private static readonly ConditionalWeakTable<AIPilotCombatModes, AiState> _ai = new ConditionalWeakTable<AIPilotCombatModes, AiState>();

        // The combat state whose FixedUpdateState is running now (set in its prefix, cleared in its finalizer), so the
        // autopilot patches act only on the AutoAim call made by AIPilotCombatModes.FixedUpdateState (:1263).
        [ThreadStatic] private static AIPilotCombatModes _ctxState;
        [ThreadStatic] private static Aircraft _ctxAircraft;

        private static AccessTools.FieldRef<AIPilotCombatModes, float> _targetHeight;
        private static AccessTools.FieldRef<AIPilotCombatModes, Unit> _currentTarget;
        private static AccessTools.FieldRef<PilotBaseState, Pilot> _statePilot;
        private static AccessTools.FieldRef<AIPilotCombatModes, WeaponInfo> _weaponInfo; // private, AIPilotCombatModes.cs:119

        // ---- faults --------------------------------------------------------------------------------------------------
        private enum Fix { Context = 0, Structure = 1, Dive = 2, Cap = 3, Climb = 4 }
        private static readonly string[] FixNames = { "context", "building/ship height", "descent limit", "dive cap", "climb-out" };
        private static readonly bool[] _faulted = new bool[5];

        // ---- map gate ------------------------------------------------------------------------------------------------
        private static FieldInfo _ncmActiveField;
        private static bool _ncmLookedUp;
        private static bool _customMap;

        // ---- per-mission counters ------------------------------------------------------------------------------------
        private static readonly HashSet<int> _aiSeen = new HashSet<int>();
        private static readonly HashSet<int> _diveAircraft = new HashSet<int>();
        private static int _heightIgnored, _divesLimited, _divesCapped, _climbStarted;
        private static readonly int[] _climbEnds = new int[6];
        private static readonly int[] _turnOutEnds = new int[3];
        private static int _turnOutStarted;
        // release reason per aircraft type (definition.jsonKey), so a live test shows how often the time limit decides
        private static readonly SortedDictionary<string, int[]> _climbEndsByType = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        private static object _mission;
        private static string _missionName = "";
        private static float _lastLevelTime, _nextPoll, _nextMapPoll;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch, read at start-up. false = nothing is patched.");
            OnlyCustomMaps = Config.Bind("General", "OnlyCustomMaps", false,
                "true = act only while a NOCustomMaps map is loaded (CustomMaps.AITerrainSwitch.Active). " +
                "false (default) = act on every map, stock maps included: both faults are in the game's own AI.");
            LogEachEvent = Config.Bind("General", "LogEachEvent", false,
                "Also log one line per limited dive, ignored structure height and climb-out (for testing). " +
                "The one summary line per mission is always written.");

            IgnoreStructureHeight = Config.Bind("TargetHeight", "IgnoreBuildingAndShipHeight", true,
                "Do not copy a Building's or Ship's radarAlt as the height to hold (AIPilotCombatModes.FlyToTarget). " +
                "A building reports half its height (16-30 m) as radarAlt. The building is then treated like a vehicle " +
                "target: the game's ManageAltitude moves the hold height. Not applied while the AI has a boresight gun " +
                "selected (strafing keeps the stock low ingress). In practice Ship matters only for a vessel on a lake " +
                "above sea level (a ship at sea reports about 0 m).");

            DiveLimitOn = Config.Bind("DiveLimit", "Enabled", true,
                "Never hand the autopilot a hold height so far below the aircraft that it would descend faster than " +
                "DescentRate over level ground.");
            DescentRate = Config.Bind("DiveLimit", "DescentRate", 40f,
                new ConfigDescription("Largest commanded descent rate, m/s, while no missile is tracking the aircraft.",
                    new AcceptableValueRange<float>(5f, 300f)));
            EvadeDescentRate = Config.Bind("DiveLimit", "EvadeDescentRate", 80f,
                new ConfigDescription("Largest commanded descent rate, m/s, while a missile is tracking the aircraft " +
                    "(the game's radar-missile evasion asks for 10 m, AIPilotCombatModes.cs:1163).",
                    new AcceptableValueRange<float>(5f, 300f)));
            DiveCapOn = Config.Bind("DiveLimit", "DiveCapEnabled", true,
                "Above CapAboveRadarAlt, keep the terrain-following waypoint no steeper than MaxDiveAngle below the " +
                "horizontal (NOCustomMaps allows 60 deg, the stock waypoint has no limit).");
            CapAboveRadarAlt = Config.Bind("DiveLimit", "CapAboveRadarAlt", 2000f,
                new ConfigDescription("Height above ground, m, above which the dive cap applies.", new AcceptableValueRange<float>(0f, 20000f)));
            MaxDiveAngle = Config.Bind("DiveLimit", "MaxDiveAngle", 30f,
                new ConfigDescription("Steepest terrain-following dive, degrees below horizontal, above CapAboveRadarAlt.",
                    new AcceptableValueRange<float>(5f, 60f)));

            ClimbOutOn = Config.Bind("ClimbOut", "Enabled", true,
                "After the take-off state hands over to the combat AI (at 75 m above ground), climb straight ahead with " +
                "the wings near level until the conditions below are met, then let the combat AI turn.");
            ClimbMinHeight = Config.Bind("ClimbOut", "MinHeight", 800f,
                new ConfigDescription("Height above ground, m, the climb-out must reach (together with SpeedFraction).",
                    new AcceptableValueRange<float>(100f, 3000f)));
            ClimbSpeedFraction = Config.Bind("ClimbOut", "SpeedFraction", 0.85f,
                new ConfigDescription("Fraction of the type's corner speed (AircraftParameters.cornerSpeed) it must reach.",
                    new AcceptableValueRange<float>(0.3f, 1.2f)));
            ClimbMaxHeight = Config.Bind("ClimbOut", "MaxHeight", 1500f,
                new ConfigDescription("Release at this height above ground, m, even if still slow.", new AcceptableValueRange<float>(200f, 6000f)));
            ClimbMaxSeconds = Config.Bind("ClimbOut", "MaxSeconds", 45f,
                new ConfigDescription("Release after this many seconds whatever the height and speed.", new AcceptableValueRange<float>(5f, 300f)));
            ClimbMinAngle = Config.Bind("ClimbOut", "MinClimbAngle", 6f,
                new ConfigDescription("Climb angle, deg, at take-off speed. The take-off state itself climbs at about 9.5 deg " +
                    "to 75 m (50 m up per 300 m, AIPilotTakeoffState.cs:177).", new AcceptableValueRange<float>(0f, 30f)));
            ClimbMaxAngle = Config.Bind("ClimbOut", "MaxClimbAngle", 15f,
                new ConfigDescription("Climb angle, deg, at corner speed.", new AcceptableValueRange<float>(0f, 30f)));
            ClimbMaxBank = Config.Bind("ClimbOut", "MaxBank", 25f,
                new ConfigDescription("Bank limit, deg, during the climb-out (the combat AI allows 180).", new AcceptableValueRange<float>(5f, 90f)));
            ClimbFullThrottle = Config.Bind("ClimbOut", "FullThrottle", true,
                "Full throttle during the climb-out (the combat AI may otherwise use its cruise throttle).");
            ClimbEndOnMissile = Config.Bind("ClimbOut", "EndOnMissile", true,
                "End the climb-out as soon as a missile is tracking the aircraft, so the game's evasion can act.");
            TurnOutSeconds = Config.Bind("ClimbOut", "TurnOutSeconds", 25f,
                new ConfigDescription("After a climb-out released while still below SpeedFraction x corner speed (height cap or " +
                    "time limit), keep the turn gentle (bank <= TurnOutMaxBank, aim effort <= 1, which also undoes " +
                    "WingCommand's AI sharp-turn effort and bank boost) for up to this many seconds or until the aircraft " +
                    "reaches that speed. 0 = off. Never applies while a missile is tracking the aircraft.",
                    new AcceptableValueRange<float>(0f, 120f)));
            TurnOutMaxBank = Config.Bind("ClimbOut", "TurnOutMaxBank", 60f,
                new ConfigDescription("Bank limit, deg, during that turn-out (the combat AI allows 180, AIPilotCombatModes.cs:1263).",
                    new AcceptableValueRange<float>(20f, 180f)));
            TurnOutAfterEveryRelease = Config.Bind("ClimbOut", "TurnOutAfterEveryRelease", true,
                "1.2.0: apply the gentle turn-out after every climb-out release, not only a slow one. It then ends after " +
                "TurnOutSeconds, when a missile tracks the aircraft, or once it is at TurnOutSafeHeight above ground and at " +
                "SpeedFraction x corner speed (11-08 test: fast releases at 400 m still sank in their first hard turn).");
            TurnOutSafeHeight = Config.Bind("ClimbOut", "TurnOutSafeHeight", 1500f,
                new ConfigDescription("Height above ground, m, at which the every-release turn-out may end early (with the speed).",
                    new AcceptableValueRange<float>(200f, 6000f)));

            if (!Enabled.Value) { Log.LogInfo("disabled in the config; nothing patched"); return; }

            try
            {
                _targetHeight = AccessTools.FieldRefAccess<AIPilotCombatModes, float>("targetHeight");
                _currentTarget = AccessTools.FieldRefAccess<AIPilotCombatModes, Unit>("currentTarget");
                _statePilot = AccessTools.FieldRefAccess<PilotBaseState, Pilot>("pilot");
            }
            catch (Exception e)
            {
                Log.LogWarning("AIPilotCombatModes fields not as expected (game version changed?); nothing patched: " + e.Message);
                return;
            }
            try { _weaponInfo = AccessTools.FieldRefAccess<AIPilotCombatModes, WeaponInfo>("currentWeaponInfo"); }
            catch (Exception e) { _weaponInfo = null; Log.LogWarning("AIPilotCombatModes.currentWeaponInfo not found; the gun exception of the building/ship rule is off: " + e.Message); }

            var harmony = new Harmony(Id);
            var combatFixed = AccessTools.DeclaredMethod(typeof(AIPilotCombatModes), "FixedUpdateState", new[] { typeof(Pilot) });
            var combatLeave = AccessTools.DeclaredMethod(typeof(AIPilotCombatModes), "LeaveState");
            var flyTo = AccessTools.DeclaredMethod(typeof(AIPilotCombatModes), "FlyToTarget", new[] { typeof(bool) });
            var autoAim = AccessTools.DeclaredMethod(typeof(AutopilotPlane), "AutoAim", new[]
                { typeof(GlobalPosition), typeof(bool), typeof(bool), typeof(bool), typeof(float), typeof(float), typeof(bool), typeof(float), typeof(Vector3) });
            var terrainWp = AccessTools.DeclaredMethod(typeof(Autopilot), "TerrainWaypoint", new[] { typeof(Vector3), typeof(float), typeof(float) });
            var takeoffFixed = AccessTools.DeclaredMethod(typeof(AIPilotTakeoffState), "FixedUpdateState", new[] { typeof(Pilot) });

            var done = new List<string>();
            bool ctx = combatFixed != null && autoAim != null
                       && Try(harmony, combatFixed, prefix: Hm(nameof(CombatFixedPrefix), Priority.First), finalizer: Hm(nameof(CombatFixedFinalizer)));
            if (ctx && Try(harmony, autoAim, prefix: Hm(nameof(AutoAimPrefix), Priority.First))) done.Add("AutopilotPlane.AutoAim");
            else { ctx = false; Log.LogWarning("could not patch AIPilotCombatModes.FixedUpdateState / AutopilotPlane.AutoAim; descent limit and climb-out are off"); }
            // Second AutoAim prefix, ordered after WingCommand's and NOCustomMaps' prefixes: it caps effort and bank during
            // the climb-out and the turn-out guard, so another mod's prefix cannot undo them.
            var late = new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(AutoAimLatePrefix)))
            {
                priority = Priority.Last,
                after = new[] { WingCommandId, CustomMapsId }
            };
            if (ctx && Try(harmony, autoAim, prefix: late)) done.Add("AutopilotPlane.AutoAim (late)");
            else if (ctx) Log.LogWarning("could not add the late AutopilotPlane.AutoAim prefix; climb-out bank/effort caps may be overridden by other mods");
            if (ctx && terrainWp != null && Try(harmony, terrainWp, postfix: Hm(nameof(TerrainWaypointPostfix), Priority.Last))) done.Add("Autopilot.TerrainWaypoint");
            else _faulted[(int)Fix.Cap] = true;
            if (flyTo != null && Try(harmony, flyTo, prefix: Hm(nameof(FlyToTargetPrefix)), postfix: Hm(nameof(FlyToTargetPostfix)))) done.Add("AIPilotCombatModes.FlyToTarget");
            else { _faulted[(int)Fix.Structure] = true; Log.LogWarning("could not patch AIPilotCombatModes.FlyToTarget; building/ship height fix is off"); }
            if (ctx && takeoffFixed != null && Try(harmony, takeoffFixed, postfix: Hm(nameof(TakeoffFixedPostfix)))) done.Add("AIPilotTakeoffState.FixedUpdateState");
            else { _faulted[(int)Fix.Climb] = true; Log.LogWarning("could not patch AIPilotTakeoffState.FixedUpdateState; climb-out is off"); }
            if (combatLeave != null) Try(harmony, combatLeave, postfix: Hm(nameof(CombatLeavePostfix)));
            if (!ctx) { _faulted[(int)Fix.Dive] = true; _faulted[(int)Fix.Climb] = true; }

            Log.LogInfo("NO AI Flight Fix " + Version + " loaded; patched " + string.Join(", ", done) +
                        (ctx ? ", AIPilotCombatModes.FixedUpdateState" : "") +
                        ". Acts on " + (OnlyCustomMaps.Value ? "NOCustomMaps maps only" : "every map") + ", host/server AI only.");
        }

        private static HarmonyMethod Hm(string name, int priority = -1)
        {
            var hm = new HarmonyMethod(AccessTools.Method(typeof(Plugin), name));
            if (priority >= 0) hm.priority = priority;
            // run before NOCustomMaps' own AutoAim prefix (PlaneFloorPatch) so its terrain floor sees our aim point
            if (priority == Priority.First) hm.before = new[] { CustomMapsId };
            return hm;
        }

        private static bool Try(Harmony h, MethodBase m, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null)
        {
            try { h.Patch(m, prefix: prefix, postfix: postfix, finalizer: finalizer); return true; }
            catch (Exception e) { Log.LogWarning("patching " + m.DeclaringType?.Name + "." + m.Name + " failed: " + e.Message); return false; }
        }

        private static void Fault(Fix f, Exception e)
        {
            if (_faulted[(int)f]) return;
            _faulted[(int)f] = true;
            Log.LogError("the " + FixNames[(int)f] + " fix failed and is now off until restart: " + e);
        }

        private static void Event(string text)
        {
            if (LogEachEvent.Value) Log.LogInfo(text);
        }

        // ---- who is handled ------------------------------------------------------------------------------------------

        /// <summary>
        /// An AI-flown aircraft on the server/host. Pilot_OnInitialize starts the AI states only when the aircraft has no
        /// player and IsServer (Pilot.cs:242-256); the extra player checks keep player aircraft out in any case.
        /// </summary>
        private static bool Managed(Pilot pilot, out Aircraft a)
        {
            a = null;
            if (pilot == null) return false;
            a = pilot.aircraft;
            if (a == null) return false;
            if (pilot.playerControlled || pilot.player != null || a.Player != null) return false;
            if (!a.IsServer) return false;
            if (OnlyCustomMaps.Value && !_customMap) return false;
            if (_aiSeen.Add(a.GetInstanceID()) && _aiSeen.Count == 1) { try { _missionName = MissionManager.CurrentMission?.Name ?? ""; } catch { } }
            return true;
        }

        private static int MissileCount(Aircraft a)
        {
            var mws = a.GetMissileWarningSystem();
            return mws != null && mws.knownMissiles != null ? mws.knownMissiles.Count : 0;
        }

        private static string Name(Aircraft a)
        {
            try { return string.IsNullOrEmpty(a.UniqueName) ? a.name : a.UniqueName; } catch { return "?"; }
        }

        // ---- patches: context ----------------------------------------------------------------------------------------

        private static void CombatFixedPrefix(AIPilotCombatModes __instance, Pilot pilot)
        {
            _ctxState = null;
            _ctxAircraft = null;
            if (_faulted[(int)Fix.Context]) return;
            try
            {
                // Refresh the NOCustomMaps switch where it is used (not only from Update), so the descent-limit geometry
                // and OnlyCustomMaps are right even if this component's Update stops or lags at mission start.
                float now = Time.realtimeSinceStartup;
                if (now >= _nextMapPoll) { _nextMapPoll = now + 1f; _customMap = ReadCustomMapActive(); }
                if (Managed(pilot, out var a)) { _ctxState = __instance; _ctxAircraft = a; }
            }
            catch (Exception e) { Fault(Fix.Context, e); }
        }

        private static Exception CombatFixedFinalizer(Exception __exception)
        {
            _ctxState = null;
            _ctxAircraft = null;
            return __exception; // leave the game's own behaviour (Pilot.cs:459-466 logs it) unchanged
        }

        private static void CombatLeavePostfix(AIPilotCombatModes __instance)
        {
            try
            {
                if (__instance != null && _ai.TryGetValue(__instance, out var ai))
                {
                    if (ai.Climbing)
                    {
                        ai.Climbing = false;
                        _climbEnds[(int)ClimbRelease.LeftCombat]++;
                        CountByType(_statePilot(__instance)?.aircraft, ClimbRelease.LeftCombat);
                    }
                    ai.ReleasedAt = -1f;
                }
            }
            catch { }
        }

        // ---- patch: AutopilotPlane.AutoAim (descent limit + climb-out) ----------------------------------------------

        private static void AutoAimPrefix(AutopilotPlane __instance, ref GlobalPosition destination, ref bool aimVelocity,
                                          ref float bankAllowed, ref bool followTerrain, ref float altitudeHold)
        {
            var st = _ctxState;
            var a = _ctxAircraft;
            if (st == null || a == null || __instance == null || __instance.aircraft != a) return;
            AiState ai;
            try { ai = _ai.GetOrCreateValue(st); }
            catch (Exception e) { Fault(Fix.Context, e); return; }

            if (ai.Climbing)
            {
                if (_faulted[(int)Fix.Climb] || !ClimbOutOn.Value) ai.Climbing = false;
                else
                {
                    try
                    {
                        if (ClimbStep(st, a, ai, ref destination, ref aimVelocity, ref bankAllowed, ref followTerrain, ref altitudeHold)) return;
                    }
                    catch (Exception e) { ai.Climbing = false; Fault(Fix.Climb, e); }
                }
            }

            if (!followTerrain || _faulted[(int)Fix.Dive] || !DiveLimitOn.Value) { ai.DiveLimited = false; return; }
            try
            {
                var p = a.GetAircraftParameters();
                float g = p != null ? p.aircraftGLimit : 9f;
                float rate = MissileCount(a) > 0 ? EvadeDescentRate.Value : DescentRate.Value;
                // NOCustomMaps' waypoint looks out to twice the look-ahead (see FlightMath.HoldFloor); the stock one does not
                float reach = _customMap ? 2f : 1f;
                float cone = _customMap ? FlightMath.NcmCone : 0f;
                // AutoAim scales the hold down below 1.9 x landing speed after this prefix (AutopilotPlane.cs:46), so the
                // hold is raised until the scaled value meets the floor. AutoAim uses its airspeed checker; a.speed is
                // the same apart from wind.
                float scale = FlightMath.HoldScale(a.speed, p != null ? p.landingSpeed : 30f);
                float h = FlightMath.LimitHold(altitudeHold, scale, a.radarAlt, a.speed, g, rate, reach, cone, out bool raised);
                if (raised)
                {
                    if (!ai.DiveLimited && h - altitudeHold > 50f)
                    {
                        ai.DiveLimited = true;
                        _divesLimited++;
                        _diveAircraft.Add(a.GetInstanceID());
                        Event("descent limited: " + Name(a) + " at " + a.radarAlt.ToString("0") + " m AGL, " + a.speed.ToString("0") +
                              " m/s, asked to hold " + altitudeHold.ToString("0") + " m, given " + h.ToString("0") + " m (" + rate.ToString("0") + " m/s)");
                    }
                    altitudeHold = h;
                }
                else ai.DiveLimited = false;
            }
            catch (Exception e) { Fault(Fix.Dive, e); }
        }

        /// <summary>One fixed step of the climb-out. Returns true when it set the autopilot inputs itself.</summary>
        private static bool ClimbStep(AIPilotCombatModes st, Aircraft a, AiState ai, ref GlobalPosition destination, ref bool aimVelocity,
                                      ref float bankAllowed, ref bool followTerrain, ref float altitudeHold)
        {
            var p = a.GetAircraftParameters();
            float corner = p != null ? p.cornerSpeed : 150f;
            float takeoff = p != null ? p.takeoffSpeed : 60f;
            float elapsed = Time.timeSinceLevelLoad - ai.ClimbStart;
            bool missile = ClimbEndOnMissile.Value && MissileCount(a) > 0;
            var r = FlightMath.ClimbOutState(elapsed, a.radarAlt, a.speed, corner, missile,
                                             ClimbMinHeight.Value, ClimbSpeedFraction.Value, ClimbMaxHeight.Value, ClimbMaxSeconds.Value);
            if (r != ClimbRelease.None)
            {
                ai.Climbing = false;
                _climbEnds[(int)r]++;
                CountByType(a, r);
                // A release still below the speed gate (height cap, time limit) gets the turn-out guard; a missile
                // release does not (the evasion needs full authority), and HeightAndSpeed is already fast enough.
                if (r != ClimbRelease.Missile && TurnOutSeconds.Value > 0f &&
                    (TurnOutAfterEveryRelease.Value || a.speed < ClimbSpeedFraction.Value * corner))
                {
                    ai.ReleasedAt = Time.timeSinceLevelLoad;
                    _turnOutStarted++;
                }
                else ai.ReleasedAt = -1f;
                // Start the combat AI's height logic from here, as it would have at the handover (EnterState sets
                // targetHeight = radarAlt, AIPilotCombatModes.cs:138), instead of from the 75 m it was given then.
                ref float th = ref _targetHeight(st);
                if (th < a.radarAlt) th = a.radarAlt;
                if (altitudeHold < a.radarAlt) altitudeHold = a.radarAlt;
                Event("climb-out ended (" + r + "): " + Name(a) + " after " + elapsed.ToString("0") + " s at " + a.radarAlt.ToString("0") +
                      " m AGL, " + a.speed.ToString("0") + " m/s (corner " + corner.ToString("0") + "); started at " +
                      ai.ClimbStartAlt.ToString("0") + " m, " + ai.ClimbStartSpeed.ToString("0") + " m/s");
                return false;
            }

            float angle = FlightMath.ClimbAngleDeg(a.speed, takeoff, corner, ClimbMinAngle.Value, ClimbMaxAngle.Value) * FlightMath.Deg;
            float d = FlightMath.ClimbAimDistance(a.speed);
            var dest = a.GlobalPosition() + ai.ClimbDir * d + Vector3.up * (d * Mathf.Tan(angle));
            float bank = Mathf.Min(bankAllowed, ClimbMaxBank.Value);
            if (ClimbFullThrottle.Value)
            {
                var ci = a.GetInputs();
                if (ci != null) ci.throttle = 1f;
            }
            destination = dest;
            aimVelocity = true;   // as the take-off state does (AIPilotTakeoffState.cs:185)
            followTerrain = false;
            bankAllowed = bank;
            return true;
        }

        /// <summary>
        /// Second AutoAim prefix, after WingCommand's AiSharpTurnPatch and NOCustomMaps. WingCommand raises effort to 2-2.5
        /// (AutoAim's num3 then becomes 1 and the low-speed damping of bank and turn rate is gone, AutopilotPlane.cs:47,
        /// :50, :57, :99) and raises bankAllowed to 70-140 with Math.Max (SharpTurnPolicy.SafeBankCeiling) whenever the
        /// destination is 30 deg or more off the velocity. During a climb-out, and during the turn-out guard after a
        /// slow release, this caps them again. The stock combat AI never passes an effort above 1 (aimEffort 0.25-1,
        /// AIPilotCombatModes.cs:286-1179), so the effort cap only undoes another mod's boost.
        /// </summary>
        private static void AutoAimLatePrefix(AutopilotPlane __instance, ref float effort, ref float bankAllowed)
        {
            var st = _ctxState;
            var a = _ctxAircraft;
            if (st == null || a == null || __instance == null || __instance.aircraft != a || _faulted[(int)Fix.Climb]) return;
            try
            {
                if (!_ai.TryGetValue(st, out var ai)) return;
                if (ai.Climbing)
                {
                    effort = Mathf.Min(effort, 1f);
                    bankAllowed = Mathf.Min(bankAllowed, ClimbMaxBank.Value);
                    return;
                }
                if (ai.ReleasedAt < 0f) return;
                if (!ClimbOutOn.Value) { ai.ReleasedAt = -1f; return; }
                var p = a.GetAircraftParameters();
                float corner = p != null ? p.cornerSpeed : 150f;
                float since = Time.timeSinceLevelLoad - ai.ReleasedAt;
                bool missile = MissileCount(a) > 0;
                bool hold = TurnOutAfterEveryRelease.Value
                    ? FlightMath.TurnOutGuardEvery(since, a.speed, a.radarAlt, corner, ClimbSpeedFraction.Value, TurnOutSeconds.Value,
                                                   TurnOutSafeHeight.Value, missile)
                    : FlightMath.TurnOutGuard(since, a.speed, corner, ClimbSpeedFraction.Value, TurnOutSeconds.Value, missile);
                if (hold)
                {
                    effort = Mathf.Min(effort, 1f);
                    bankAllowed = Mathf.Min(bankAllowed, TurnOutMaxBank.Value);
                }
                else
                {
                    ai.ReleasedAt = -1f;
                    var end = missile ? TurnOutEnd.Missile : (since >= TurnOutSeconds.Value ? TurnOutEnd.Timeout : TurnOutEnd.Speed);
                    _turnOutEnds[(int)end]++;
                    Event("turn-out guard ended (" + end + "): " + Name(a) + " after " + since.ToString("0") + " s at " +
                          a.radarAlt.ToString("0") + " m AGL, " + a.speed.ToString("0") + " m/s (corner " + corner.ToString("0") + ")");
                }
            }
            catch (Exception e) { Fault(Fix.Climb, e); }
        }

        private static string TypeKey(Aircraft a)
        {
            try { var d = a != null ? a.definition : null; return d != null && !string.IsNullOrEmpty(d.jsonKey) ? d.jsonKey : "?"; }
            catch { return "?"; }
        }

        private static void CountByType(Aircraft a, ClimbRelease r)
        {
            try
            {
                string k = TypeKey(a);
                if (!_climbEndsByType.TryGetValue(k, out var n)) _climbEndsByType[k] = n = new int[6];
                n[(int)r]++;
            }
            catch { }
        }

        // ---- patch: Autopilot.TerrainWaypoint (dive cap) -------------------------------------------------------------

        private static void TerrainWaypointPostfix(Autopilot __instance, ref GlobalPosition __result)
        {
            var st = _ctxState;
            var a = _ctxAircraft;
            if (st == null || a == null || __instance == null || __instance.aircraft != a) return;
            if (_faulted[(int)Fix.Cap] || !DiveCapOn.Value) return;
            try
            {
                var ai = _ai.GetOrCreateValue(st);
                if (!(a.radarAlt > CapAboveRadarAlt.Value)) { ai.DiveCapped = false; return; }
                GlobalPosition gp = a.GlobalPosition();
                Vector3 d = __result - gp;
                float hz = Mathf.Sqrt(d.x * d.x + d.z * d.z);
                float ny = FlightMath.CapDive(d.y, hz, MaxDiveAngle.Value, out bool capped);
                if (capped)
                {
                    if (!ai.DiveCapped)
                    {
                        ai.DiveCapped = true;
                        _divesCapped++;
                        Event("dive capped: " + Name(a) + " at " + a.radarAlt.ToString("0") + " m AGL, waypoint " +
                              (Mathf.Atan2(d.y, hz) / FlightMath.Deg).ToString("0") + " deg -> " + (-MaxDiveAngle.Value).ToString("0") + " deg");
                    }
                    __result = new GlobalPosition(__result.x, gp.y + ny, __result.z);
                }
                else ai.DiveCapped = false;
            }
            catch (Exception e) { Fault(Fix.Cap, e); }
        }

        // ---- patch: AIPilotCombatModes.FlyToTarget (building/ship height) --------------------------------------------
        // Runs from the 1 s check (ChooseCombatMode, AIPilotCombatModes.cs:973-986), outside FixedUpdateState, so it
        // makes its own host/AI check through the state's pilot.

        private static void FlyToTargetPrefix(AIPilotCombatModes __instance, bool checkMode, out float __state)
        {
            __state = float.NaN;
            if (!checkMode || _faulted[(int)Fix.Structure] || !IgnoreStructureHeight.Value || __instance == null) return;
            try
            {
                if (Managed(_statePilot(__instance), out _)) __state = _targetHeight(__instance);
            }
            catch (Exception e) { Fault(Fix.Structure, e); }
        }

        private static void FlyToTargetPostfix(AIPilotCombatModes __instance, float __state)
        {
            if (float.IsNaN(__state) || _faulted[(int)Fix.Structure]) return;
            try
            {
                Unit t = _currentTarget(__instance);
                if (t == null || !(t is Building || t is Ship)) return;
                // A boresight-gun attack keeps the stock behaviour: a strafing run needs the low ingress, and the descent
                // limit already keeps the drop to it gradual. currentWeaponInfo is the weapon selected for this target
                // (FlyToTarget requires it non-null, :293, and picks the attack mode from it, :305-324).
                if (_weaponInfo != null)
                {
                    var w = _weaponInfo(__instance);
                    if (w != null && w.gun && w.boresight) return;
                }
                ref float th = ref _targetHeight(__instance);
                // the only write in FlyToTarget is targetHeight = currentTarget.radarAlt when radarAlt > 10 (:298-301)
                if (th != __state && th == t.radarAlt)
                {
                    th = __state;
                    if (!ReferenceEquals(_ai.GetOrCreateValue(__instance).LastIgnored, t))
                    {
                        _ai.GetOrCreateValue(__instance).LastIgnored = t;
                        _heightIgnored++;
                        var p = _statePilot(__instance);
                        Event("structure height ignored: " + (p != null && p.aircraft != null ? Name(p.aircraft) : "?") + " -> " + t.name +
                              " (radarAlt " + t.radarAlt.ToString("0") + " m), keeps " + __state.ToString("0") + " m");
                    }
                }
            }
            catch (Exception e) { Fault(Fix.Structure, e); }
        }

        // ---- patch: AIPilotTakeoffState.FixedUpdateState (start of a climb-out) --------------------------------------

        private static void TakeoffFixedPostfix(AIPilotTakeoffState __instance, Pilot pilot)
        {
            if (_faulted[(int)Fix.Climb] || !ClimbOutOn.Value || pilot == null) return;
            try
            {
                // Only the 75 m handover switches from here to the combat state (AIPilotTakeoffState.cs:112-120); the
                // other switch goes to parkedState (:98-102).
                if (!(pilot.currentState is AIPilotCombatModes cs) || ReferenceEquals(pilot.currentState, __instance)) return;
                if (!Managed(pilot, out var a)) return;
                if (a.radarAlt < 10f) return;
                Vector3 v = a.rb != null ? a.rb.velocity : Vector3.zero;
                v.y = 0f;
                if (v.sqrMagnitude < 1f) { v = a.transform.forward; v.y = 0f; }
                if (v.sqrMagnitude < 1e-4f) return;
                var ai = _ai.GetOrCreateValue(cs);
                ai.Climbing = true;
                ai.ClimbStart = Time.timeSinceLevelLoad;
                ai.ClimbDir = v.normalized;
                ai.ClimbStartAlt = a.radarAlt;
                ai.ClimbStartSpeed = a.speed;
                ai.ReleasedAt = -1f;
                ai.DiveLimited = ai.DiveCapped = false;
                _climbStarted++;
                Event("climb-out started: " + Name(a) + " at " + a.radarAlt.ToString("0") + " m AGL, " + a.speed.ToString("0") + " m/s");
            }
            catch (Exception e) { Fault(Fix.Climb, e); }
        }

        // ---- map gate + summary --------------------------------------------------------------------------------------

        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextPoll) return;
            _nextPoll = now + 1f;
            try
            {
                _customMap = ReadCustomMapActive();
                object m = MissionManager.CurrentMission;
                var gs = GameManager.gameState;
                bool inGame = gs == GameState.SinglePlayer || gs == GameState.Multiplayer;
                float tl = Time.timeSinceLevelLoad;
                bool newLevel = tl + 1f < _lastLevelTime;
                _lastLevelTime = tl;
                if (!inGame) m = null;
                // flush only when LEAVING a mission (or when the same mission object is loaded again), so AI counted in
                // the first second of a new mission, before this poll notices it, are not printed as a separate line
                bool left = _mission != null && !ReferenceEquals(m, _mission);
                bool reloaded = _mission != null && m != null && ReferenceEquals(m, _mission) && newLevel;
                if (left || reloaded) Flush();
                _mission = m;
            }
            catch { }
        }

        private void OnApplicationQuit()
        {
            Flush();
        }

        private static bool ReadCustomMapActive()
        {
            if (!_ncmLookedUp)
            {
                var t = AccessTools.TypeByName("CustomMaps.AITerrainSwitch");
                _ncmActiveField = t != null ? AccessTools.Field(t, "Active") : null;
                if (t != null || Time.realtimeSinceStartup > 60f || !Chainloader.PluginInfos.ContainsKey(CustomMapsId)) _ncmLookedUp = true;
            }
            try { return _ncmActiveField != null && (bool)_ncmActiveField.GetValue(null); }
            catch { return false; }
        }

        /// <summary>The one summary line per mission (written when the mission ends, the level changes or the game quits).</summary>
        private static void Flush()
        {
            if (_aiSeen.Count == 0) return;
            int ended = 0;
            foreach (var n in _climbEnds) ended += n;
            Log.LogInfo("mission '" + _missionName + "': " + _aiSeen.Count + " AI aircraft handled; descents limited " + _divesLimited +
                        " (" + _diveAircraft.Count + " aircraft), dives capped " + _divesCapped + ", building/ship heights ignored " + _heightIgnored +
                        "; climb-outs held " + _climbStarted + " (released: height+speed " + _climbEnds[(int)ClimbRelease.HeightAndSpeed] +
                        ", height cap " + _climbEnds[(int)ClimbRelease.HeightCap] + ", time limit " + _climbEnds[(int)ClimbRelease.Timeout] +
                        ", missile " + _climbEnds[(int)ClimbRelease.Missile] + ", left combat " + _climbEnds[(int)ClimbRelease.LeftCombat] +
                        ", unfinished " + Math.Max(0, _climbStarted - ended) + "); turn-out guards " + _turnOutStarted +
                        " (ended: speed " + _turnOutEnds[(int)TurnOutEnd.Speed] + ", time " + _turnOutEnds[(int)TurnOutEnd.Timeout] +
                        ", missile " + _turnOutEnds[(int)TurnOutEnd.Missile] + ")" + ByTypeText() + FaultText());
            _aiSeen.Clear();
            _diveAircraft.Clear();
            _heightIgnored = _divesLimited = _divesCapped = _climbStarted = _turnOutStarted = 0;
            Array.Clear(_climbEnds, 0, _climbEnds.Length);
            Array.Clear(_turnOutEnds, 0, _turnOutEnds.Length);
            _climbEndsByType.Clear();
            _missionName = "";
        }

        /// <summary>"; climb-out releases by type: CAS1 hs 3/cap 0/time 5/msl 0/left 1, ..." (empty when none).</summary>
        private static string ByTypeText()
        {
            if (_climbEndsByType.Count == 0) return "";
            var parts = new List<string>();
            foreach (var kv in _climbEndsByType)
            {
                var n = kv.Value;
                parts.Add(kv.Key + " hs " + n[(int)ClimbRelease.HeightAndSpeed] + "/cap " + n[(int)ClimbRelease.HeightCap] +
                          "/time " + n[(int)ClimbRelease.Timeout] + "/msl " + n[(int)ClimbRelease.Missile] + "/left " + n[(int)ClimbRelease.LeftCombat]);
            }
            return "; climb-out releases by type: " + string.Join(", ", parts);
        }

        private static string FaultText()
        {
            var off = new List<string>();
            for (int i = 0; i < _faulted.Length; i++) if (_faulted[i]) off.Add(FixNames[i]);
            return off.Count == 0 ? "" : "; OFF after a fault or missing patch: " + string.Join(", ", off);
        }
    }
}
