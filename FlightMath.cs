// NO AI Flight Fix - realistic descents and climb-outs for the game's fixed-wing AI.
// Copyright (C) 2026 netmaster296
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any
// later version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE file.

using System;

namespace AIFlightFix
{
    /// <summary>Why a climb-out ended (or None while it is still held).</summary>
    public enum ClimbRelease
    {
        None = 0,
        /// <summary>High enough and close to corner speed: the normal end.</summary>
        HeightAndSpeed = 1,
        /// <summary>Reached ClimbOut.MaxHeight while still slow.</summary>
        HeightCap = 2,
        /// <summary>ClimbOut.MaxSeconds passed.</summary>
        Timeout = 3,
        /// <summary>A missile is tracking it: the game's evasion takes over.</summary>
        Missile = 4,
        /// <summary>The AI left the combat state (landing, ejected, destroyed) before any of the above.</summary>
        LeftCombat = 5,
    }

    /// <summary>Why a post-climb-out turn-out guard ended.</summary>
    public enum TurnOutEnd
    {
        /// <summary>Reached ClimbOut.SpeedFraction x corner speed.</summary>
        Speed = 0,
        /// <summary>ClimbOut.TurnOutSeconds passed while still slow.</summary>
        Timeout = 1,
        /// <summary>A missile started tracking it.</summary>
        Missile = 2,
    }

    /// <summary>
    /// The numbers behind both fixes, kept free of Unity types so the offline harness (round2/plugin/harness) compiles this
    /// exact file and replays recorded NOTelemetry traces through it.
    /// </summary>
    public static class FlightMath
    {
        public const float Deg = (float)(Math.PI / 180.0);

        /// <summary>
        /// The terrain-following look-ahead the game uses: max(speed, 100) * 6 * (9 / gLimit)
        /// (TerrainWarningSystem.cs:49, GetFollowTerrainWaypoint).
        /// </summary>
        public static float LookAhead(float speed, float gLimit)
        {
            float g = gLimit > 0f ? gLimit : 9f;
            return Math.Max(speed, 100f) * 6f * (9f / g);
        }

        /// <summary>
        /// The lowest hold height (m above ground) that still gives a descent of about <paramref name="descentRate"/> m/s
        /// over level ground. The autopilot steers at a waypoint <c>reach</c> metres ahead at ground + hold, so its flight
        /// path angle is about atan((hold - radarAlt) / reach) and its descent rate speed * sin(angle). Solving for the
        /// largest allowed gap: gap = reach * tan(asin(rate / speed)).
        /// <para><paramref name="reachFactor"/> is 1 for the stock waypoint (Autopilot.TerrainWaypoint: ground + hold at
        /// the look-ahead) and 2 under NOCustomMaps, whose envelope samples the ground out to twice the look-ahead with the
        /// hold as clearance plus a cone of 0.02 per metre and takes the shallower angle (AITerrain.cs Waypoint;
        /// TerrainEnvelope.cs:41-64 ClimbAngle, :86-94 WaypointAngle, :105-108 Horizon, Cone = 0.02 at :9), so there
        /// gap = reach * (tan(angle) + cone). The offline harness checked this model against 2,486 recorded waypoints
        /// (median error 0.8 deg).</para>
        /// </summary>
        public static float HoldFloor(float radarAlt, float speed, float gLimit, float descentRate, float reachFactor, float cone)
        {
            if (!(descentRate > 0f) || !Finite(radarAlt) || !Finite(speed)) return float.NegativeInfinity;
            float v = Math.Max(speed, 1f);
            float s = Math.Min(descentRate / v, (float)Math.Sin(60f * Deg)); // never more than the 60 deg NCM clamp
            float angle = (float)Math.Asin(s);
            float reach = LookAhead(speed, gLimit) * Math.Max(reachFactor, 1f);
            return radarAlt - reach * ((float)Math.Tan(angle) + Math.Max(cone, 0f));
        }

        /// <summary>The NOCustomMaps envelope cone (TerrainEnvelope.Cone, TerrainEnvelope.cs:9).</summary>
        public const float NcmCone = 0.02f;

        /// <summary>Applies <see cref="HoldFloor"/>: returns the hold the autopilot should get and whether it was raised.</summary>
        public static float LimitHold(float hold, float radarAlt, float speed, float gLimit, float descentRate, float reachFactor, float cone, out bool raised)
        {
            float floor = HoldFloor(radarAlt, speed, gLimit, descentRate, reachFactor, cone);
            if (Finite(floor) && hold < floor)
            {
                raised = true;
                return floor;
            }
            raised = false;
            return hold;
        }

        /// <summary>
        /// The factor AutopilotPlane.AutoAim applies to the hold it is given before it builds the terrain waypoint:
        /// <c>altitudeHold *= Mathf.Lerp(0.1, 1, (airspeed - 0.9 * landingSpeed) / landingSpeed)</c> (AutopilotPlane.cs:46;
        /// Mathf.Lerp clamps t to 0..1). It is 1 at or above 1.9 x landing speed and falls to 0.1 at 0.9 x landing speed.
        /// </summary>
        public static float HoldScale(float airspeed, float landingSpeed)
        {
            float ls = Math.Max(landingSpeed, 1f);
            float t = Clamp01((airspeed - 0.9f * ls) / ls);
            return 0.1f + 0.9f * t;
        }

        /// <summary>
        /// <see cref="LimitHold(float,float,float,float,float,float,float,out bool)"/> for a hold that AutoAim will still
        /// multiply by <paramref name="scale"/> (<see cref="HoldScale"/>): the hold returned is such that
        /// scale x hold is at least the floor, so the waypoint the autopilot builds respects the descent limit.
        /// </summary>
        public static float LimitHold(float hold, float scale, float radarAlt, float speed, float gLimit, float descentRate, float reachFactor, float cone, out bool raised)
        {
            float k = Finite(scale) ? Math.Max(Math.Min(scale, 1f), 0.1f) : 1f;
            float floor = HoldFloor(radarAlt, speed, gLimit, descentRate, reachFactor, cone);
            if (Finite(floor) && k * hold < floor)
            {
                raised = true;
                return floor / k;
            }
            raised = false;
            return hold;
        }

        /// <summary>
        /// Whether the turn-out guard after a climb-out still applies: it lasts <paramref name="seconds"/> after the
        /// release, ends as soon as the aircraft reaches <paramref name="speedFraction"/> x corner speed, and never applies
        /// while a missile is tracking it (the game's evasion must have full authority).
        /// </summary>
        public static bool TurnOutGuard(float sinceRelease, float speed, float cornerSpeed, float speedFraction, float seconds, bool missile)
        {
            if (missile || !(sinceRelease >= 0f) || !(sinceRelease < seconds)) return false;
            return speed < speedFraction * Math.Max(cornerSpeed, 1f);
        }

        /// <summary>
        /// 1.2.0: the guard after EVERY climb-out release. The 11-08 launch test (session 20261108-163950) had 8 of 12
        /// scripted take-offs released at corner speed and 400 m, then sink into the ground 10-20 s later in their first
        /// hard turn toward a target: being fast was not enough. This guard lasts <paramref name="seconds"/>, never while a
        /// missile is tracking the aircraft, and ends early only once it is both at <paramref name="safeHeight"/> above
        /// ground and at <paramref name="speedFraction"/> x corner speed.
        /// </summary>
        public static bool TurnOutGuardEvery(float sinceRelease, float speed, float radarAlt, float cornerSpeed, float speedFraction,
                                             float seconds, float safeHeight, bool missile)
        {
            if (missile || !(sinceRelease >= 0f) || !(sinceRelease < seconds)) return false;
            return !(radarAlt >= safeHeight && speed >= speedFraction * Math.Max(cornerSpeed, 1f));
        }

        /// <summary>
        /// Caps how far below the aircraft a terrain-following waypoint may sit: the returned height difference
        /// (waypoint minus aircraft, m) is never steeper than <paramref name="maxDiveDeg"/> below the horizontal.
        /// Raising a waypoint only ever makes the aircraft fly higher, so this never reduces terrain clearance.
        /// </summary>
        public static float CapDive(float dy, float horizontal, float maxDiveDeg, out bool capped)
        {
            capped = false;
            if (!Finite(dy) || !(horizontal > 1f) || !(maxDiveDeg > 0f) || maxDiveDeg >= 89f) return dy;
            float minDy = -horizontal * (float)Math.Tan(maxDiveDeg * Deg);
            if (dy < minDy)
            {
                capped = true;
                return minDy;
            }
            return dy;
        }

        /// <summary>
        /// Climb-out flight path angle (deg): <paramref name="minDeg"/> at or below take-off speed, rising linearly to
        /// <paramref name="maxDeg"/> at corner speed, so a slow aircraft accelerates first and a fast one climbs.
        /// </summary>
        public static float ClimbAngleDeg(float speed, float takeoffSpeed, float cornerSpeed, float minDeg, float maxDeg)
        {
            float lo = Math.Max(takeoffSpeed, 1f);
            float hi = Math.Max(cornerSpeed, lo + 1f);
            float t = Clamp01((speed - lo) / (hi - lo));
            return minDeg + (maxDeg - minDeg) * t;
        }

        /// <summary>The climb-out end test, in order: missile, height + speed, height cap, time limit.</summary>
        public static ClimbRelease ClimbOutState(float elapsed, float radarAlt, float speed, float cornerSpeed, bool missile,
                                                 float minHeight, float speedFraction, float maxHeight, float maxSeconds)
        {
            if (missile) return ClimbRelease.Missile;
            if (radarAlt >= minHeight && speed >= speedFraction * Math.Max(cornerSpeed, 1f)) return ClimbRelease.HeightAndSpeed;
            if (radarAlt >= maxHeight) return ClimbRelease.HeightCap;
            if (elapsed >= maxSeconds) return ClimbRelease.Timeout;
            return ClimbRelease.None;
        }

        /// <summary>Horizontal distance of the climb-out aim point (m). NOCustomMaps' floor under straight flight only
        /// looks at destinations farther than max(1500, 6 * speed) (TerrainEnvelope.FloorApplies, :119-122), so the aim
        /// point is put beyond that and NOCustomMaps' terrain floor keeps working during the climb.</summary>
        public static float ClimbAimDistance(float speed)
        {
            return Math.Max(3000f, 10f * Math.Max(speed, 0f));
        }

        public static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : (float.IsNaN(v) ? 0f : v));
        }

        public static bool Finite(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }
    }
}
