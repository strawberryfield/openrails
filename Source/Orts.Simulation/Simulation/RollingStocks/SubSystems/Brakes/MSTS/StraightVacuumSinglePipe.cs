﻿﻿// COPYRIGHT 2009, 2010, 2011, 2012, 2013 by the Open Rails project.
// 
// This file is part of Open Rails.
// 
// Open Rails is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Open Rails is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Open Rails.  If not, see <http://www.gnu.org/licenses/>.


using Microsoft.Xna.Framework;
using System;
using ORTS.Common;
using ORTS.Scripting.Api;
using System.Collections.Generic;
using System.Diagnostics;

namespace Orts.Simulation.RollingStocks.SubSystems.Brakes.MSTS
{
    class StraightVacuumSinglePipe : VacuumSinglePipe
    {
        public StraightVacuumSinglePipe(TrainCar car)
            : base(car)
        {

        }


        public override void Initialize(bool handbrakeOn, float maxVacuumInHg, float fullServVacuumInHg, bool immediateRelease)
        {
            CylPressurePSIA = BrakeLine1PressurePSI = Vac.ToPress(fullServVacuumInHg);
            HandbrakePercent = handbrakeOn & (Car as MSTSWagon).HandBrakePresent ? 100 : 0;
            (Car as MSTSWagon).NonAutoBrakePresent = true; // Set flag to indicate that non auto brake is set in train
        }

        public override void InitializeMoving() // used when initial speed > 0
        {

            BrakeLine1PressurePSI = Vac.ToPress(Car.Train.EqualReservoirPressurePSIorInHg);
            CylPressurePSIA = Vac.ToPress(Car.Train.EqualReservoirPressurePSIorInHg);
            HandbrakePercent = 0;
        }


        public override void Update(float elapsedClockSeconds)
        {


            if (BrakeLine1PressurePSI < CylPressurePSIA) // Increase BP pressure, hence vacuum brakes are being released
            {
                float dp = elapsedClockSeconds * MaxReleaseRatePSIpS;
                float vr = NumBrakeCylinders * BrakeCylVolM3 / BrakePipeVolumeM3;
                if (CylPressurePSIA - dp < BrakeLine1PressurePSI + dp * vr)
                    dp = (CylPressurePSIA - BrakeLine1PressurePSI) / (1 + vr);
                CylPressurePSIA -= dp;

            }
            else if (BrakeLine1PressurePSI > CylPressurePSIA)  // Decrease BP pressure, hence vacuum brakes are being applied
            {
                float dp = elapsedClockSeconds * MaxApplicationRatePSIpS;
                float vr = NumBrakeCylinders * BrakeCylVolM3 / BrakePipeVolumeM3;
                if (CylPressurePSIA + dp > BrakeLine1PressurePSI - dp * vr)
                    dp = (BrakeLine1PressurePSI - CylPressurePSIA) / (1 + vr);
                CylPressurePSIA += dp;
            }


            float f;
            if (!Car.BrakesStuck)
            {

                float brakecylinderfraction = ((OneAtmospherePSI - CylPressurePSIA) / MaxForcePressurePSI);
                brakecylinderfraction = MathHelper.Clamp(brakecylinderfraction, 0, 1);

                f = Car.MaxBrakeForceN * brakecylinderfraction;

                if (f < Car.MaxHandbrakeForceN * HandbrakePercent / 100)
                    f = Car.MaxHandbrakeForceN * HandbrakePercent / 100;
            }
            else
            {
                f = Math.Max(Car.MaxBrakeForceN, Car.MaxHandbrakeForceN / 2);
            }
            Car.BrakeRetardForceN = f * Car.BrakeShoeRetardCoefficientFrictionAdjFactor; // calculates value of force applied to wheel, independent of wheel skid
            if (Car.BrakeSkid) // Test to see if wheels are skiding due to excessive brake force
            {
                Car.BrakeForceN = f * Car.SkidFriction;   // if excessive brakeforce, wheel skids, and loses adhesion
            }
            else
            {
                Car.BrakeForceN = f * Car.BrakeShoeCoefficientFrictionAdjFactor; // In advanced adhesion model brake shoe coefficient varies with speed, in simple odel constant force applied as per value in WAG file, will vary with wheel skid.
            }

            MSTSLocomotive lead = (MSTSLocomotive)Car.Train.LeadLocomotive;

            float MaxVacuumPipeLevelPSI = lead == null ? Bar.ToPSI(Bar.FromInHg(21)) : lead.TrainBrakeController.MaxPressurePSI;
            // Set value for large ejector to operate - in this instance as there is no small ejector, the whole brake pipe charging rate is used.
            float LargeEjectorChargingRateInHgpS = lead == null ? 10.0f : (lead.BrakePipeChargingRatePSIorInHgpS);
            float TrainPipeLeakLossPSI = lead == null ? 0.0f : (lead.TrainBrakePipeLeakPSIorInHgpS);
            float AdjTrainPipeLeakLossPSI = 0.0f;

            // Calculate train pipe pressure at lead locomotive.
            if (lead != null)
            {
                // Calculate adjustment times for varying lengths of trains
                float AdjLargeEjectorChargingRateInHgpS;
                if (lead.LargeSteamEjectorIsOn)
                {
                    AdjLargeEjectorChargingRateInHgpS = (Me3.FromFt3(200.0f) / Car.Train.TotalTrainBrakeCylinderVolumeM3) * LargeEjectorChargingRateInHgpS;
                }
                else
                {

                    AdjLargeEjectorChargingRateInHgpS = 0;
                }
                float AdjBrakeServiceTimeFactorS = (Car.Train.TotalTrainBrakeSystemVolumeM3 / Me3.FromFt3(200.0f)) * lead.BrakeServiceTimeFactorS;
                AdjTrainPipeLeakLossPSI = (Car.Train.TotalTrainBrakeSystemVolumeM3 / Me3.FromFt3(200.0f)) * lead.TrainBrakePipeLeakPSIorInHgpS;

                // Straight brake is opposite of automatic brake, ie vacuum pipe goes from 14.503psi (0 InHg - Release) to 2.24 (25InHg - Apply)

                // Apply brakes - brakepipe has to have vacuum increased to max vacuum value (ie decrease psi), vacuum is created by large ejector control

                lead.BrakeSystem.BrakeLine1PressurePSI -= elapsedClockSeconds * AdjLargeEjectorChargingRateInHgpS;
                if (lead.BrakeSystem.BrakeLine1PressurePSI < (OneAtmospherePSI - MaxVacuumPipeLevelPSI))
                {
                    lead.BrakeSystem.BrakeLine1PressurePSI = OneAtmospherePSI - MaxVacuumPipeLevelPSI;
                }

                // Release brakes - brakepipe has to have brake pipe decreased back to atmospheric pressure to apply brakes (ie psi increases).
                if (lead.TrainBrakeController.TrainBrakeControllerState == ControllerState.StrBrkReleaseOn)
                {

                    lead.BrakeSystem.BrakeLine1PressurePSI *= (1 + elapsedClockSeconds / AdjBrakeServiceTimeFactorS); ;
                    if (lead.BrakeSystem.BrakeLine1PressurePSI > OneAtmospherePSI)
                    {
                        lead.BrakeSystem.BrakeLine1PressurePSI = OneAtmospherePSI;
                    }
                }

                // leaks in train pipe will reduce vacuum (increase pressure)
                lead.BrakeSystem.BrakeLine1PressurePSI += elapsedClockSeconds * AdjTrainPipeLeakLossPSI;

                // Keep brake line within relevant limits - ie between 21 or 25 InHg and Atmospheric pressure.
                lead.BrakeSystem.BrakeLine1PressurePSI = MathHelper.Clamp(lead.BrakeSystem.BrakeLine1PressurePSI, OneAtmospherePSI - MaxVacuumPipeLevelPSI, OneAtmospherePSI);

                // update values using vacuum single pipe class
                base.Update(elapsedClockSeconds);

            }
        }

        // This overides the information for each individual wagon in the extended HUD  
        public override string[] GetDebugStatus(Dictionary<BrakeSystemComponent, PressureUnit> units)
        {
            // display differently as a straight vacuum brake

            return new string[] {
                "1VS",
                FormatStrings.FormatPressure(Vac.FromPress(CylPressurePSIA), PressureUnit.InHg, PressureUnit.InHg, true),
                FormatStrings.FormatPressure(Vac.FromPress(BrakeLine1PressurePSI), PressureUnit.InHg, PressureUnit.InHg, true),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                HandbrakePercent > 0 ? string.Format("{0:F0}%", HandbrakePercent) : string.Empty,
                FrontBrakeHoseConnected? "I" : "T",
                string.Format("A{0} B{1}", AngleCockAOpen? "+" : "-", AngleCockBOpen? "+" : "-"),
            };
        }

    }
}