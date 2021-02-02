﻿// COPYRIGHT 2011, 2012, 2013 by the Open Rails project.
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

// This file is the responsibility of the 3D & Environment Team.
#define SHOW_PHYSICS_GRAPHS     //Matej Pacha - if commented, the physics graphs are not ready for public release

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Orts.Simulation.AIs;
using Orts.Simulation.Physics;
using Orts.Simulation.RollingStocks;
using Orts.Simulation.RollingStocks.SubSystems.Brakes;
using Orts.Simulation.RollingStocks.SubSystems.Brakes.MSTS;
using Orts.Viewer3D.Processes;
using ORTS.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Orts.Viewer3D.Popups
{
    public class HUDWindow : LayeredWindow
    {
        // Set this to the width of each column in font-height units.
        readonly int ColumnWidth = 4;

        // Set to distance from top-left corner to place text.
        const int TextOffset = 10;

        readonly int ProcessorCount = System.Environment.ProcessorCount;

        readonly PerformanceCounter AllocatedBytesPerSecCounter; // \.NET CLR Memory(*)\Allocated Bytes/sec
        float AllocatedBytesPerSecLastValue;

        readonly Viewer Viewer;
        readonly Action<TableData>[] TextPages;
        readonly WindowTextFont TextFont;
        readonly HUDGraphMaterial HUDGraphMaterial;

        int TextPage;
        int LocomotivePage = 2;
        int LastTextPage;
        TableData TextTable = new TableData() { Cells = new string[0, 0] };

        HUDGraphSet ForceGraphs;
        HUDGraphMesh ForceGraphMotiveForce;
        HUDGraphMesh ForceGraphDynamicForce;
        HUDGraphMesh ForceGraphNumOfSubsteps;

        HUDGraphSet LocomotiveGraphs;
        HUDGraphMesh LocomotiveGraphsThrottle;
        HUDGraphMesh LocomotiveGraphsInputPower;
        HUDGraphMesh LocomotiveGraphsOutputPower;

        HUDGraphSet DebugGraphs;
        HUDGraphMesh DebugGraphMemory;
        HUDGraphMesh DebugGraphGCs;
        HUDGraphMesh DebugGraphFrameTime;
        HUDGraphMesh DebugGraphProcessRender;
        HUDGraphMesh DebugGraphProcessUpdater;
        HUDGraphMesh DebugGraphProcessLoader;
        HUDGraphMesh DebugGraphProcessSound;

        public HUDWindow(WindowManager owner)
            : base(owner, TextOffset, TextOffset, "HUD")
        {
            Viewer = owner.Viewer;
            LastTextPage = LocomotivePage;

            ProcessHandle = OpenProcess(0x410 /* PROCESS_QUERY_INFORMATION | PROCESS_VM_READ */, false, Process.GetCurrentProcess().Id);
            ProcessMemoryCounters = new PROCESS_MEMORY_COUNTERS() { Size = 40 };
            ProcessVirtualAddressLimit = GetVirtualAddressLimit();

            try
            {
                var counterDotNetClrMemory = new PerformanceCounterCategory(".NET CLR Memory");
                foreach (var process in counterDotNetClrMemory.GetInstanceNames())
                {
                    var processId = new PerformanceCounter(".NET CLR Memory", "Process ID", process);
                    if (processId.NextValue() == Process.GetCurrentProcess().Id)
                    {
                        AllocatedBytesPerSecCounter = new PerformanceCounter(".NET CLR Memory", "Allocated Bytes/sec", process);
                        break;
                    }
                }
            }
            catch (Exception error)
            {
                Trace.WriteLine(error);
                Trace.TraceWarning("Unable to access Microsoft .NET Framework performance counters. This may be resolved by following the instructions at http://support.microsoft.com/kb/300956");
            }

            Debug.Assert(GC.MaxGeneration == 2, "Runtime is expected to have a MaxGeneration of 2.");

            var textPages = new List<Action<TableData>>();
            textPages.Add(TextPageCommon);
            textPages.Add(TextPageConsistInfo);
            textPages.Add(TextPageLocomotiveInfo);
            textPages.Add(TextPageBrakeInfo);
            textPages.Add(TextPageForceInfo);
            textPages.Add(TextPageDispatcherInfo);
            textPages.Add(TextPageWeather);
            textPages.Add(TextPageDebugInfo);
            TextPages = textPages.ToArray();

            TextFont = owner.TextFontDefaultOutlined;
            ColumnWidth *= TextFont.Height;

            HUDGraphMaterial = (HUDGraphMaterial)Viewer.MaterialManager.Load("Debug");

            LocomotiveGraphs = new HUDGraphSet(Viewer, HUDGraphMaterial);
            LocomotiveGraphsThrottle = LocomotiveGraphs.Add(Viewer.Catalog.GetString("Throttle"), "0", "100%", Color.Blue, 50);
            LocomotiveGraphsInputPower = LocomotiveGraphs.Add(Viewer.Catalog.GetString("Power In/Out"), "0", "100%", Color.Yellow, 50);
            LocomotiveGraphsOutputPower = LocomotiveGraphs.AddOverlapped(Color.Green, 50);

            ForceGraphs = new HUDGraphSet(Viewer, HUDGraphMaterial);
            ForceGraphMotiveForce = ForceGraphs.Add(Viewer.Catalog.GetString("Motive force"), "0%", "100%", Color.Green, 75);
            ForceGraphDynamicForce = ForceGraphs.AddOverlapped(Color.Red, 75);
            ForceGraphNumOfSubsteps = ForceGraphs.Add(Viewer.Catalog.GetString("Num of substeps"), "0", "300", Color.Blue, 25);

            DebugGraphs = new HUDGraphSet(Viewer, HUDGraphMaterial);
            DebugGraphMemory = DebugGraphs.Add(Viewer.Catalog.GetString("Memory"), "0GB", String.Format("{0:F0}GB", (float)ProcessVirtualAddressLimit / 1024 / 1024 / 1024), Color.Orange, 50);
            DebugGraphGCs = DebugGraphs.Add(Viewer.Catalog.GetString("GCs"), "0", "2", Color.Magenta, 20); // Multiple of 4
            DebugGraphFrameTime = DebugGraphs.Add(Viewer.Catalog.GetString("Frame time"), "0.0s", "0.1s", Color.LightGreen, 50);
            DebugGraphProcessRender = DebugGraphs.Add(Viewer.Catalog.GetString("Render process"), "0%", "100%", Color.Red, 20);
            DebugGraphProcessUpdater = DebugGraphs.Add(Viewer.Catalog.GetString("Updater process"), "0%", "100%", Color.Yellow, 20);
            DebugGraphProcessLoader = DebugGraphs.Add(Viewer.Catalog.GetString("Loader process"), "0%", "100%", Color.Magenta, 20);
            DebugGraphProcessSound = DebugGraphs.Add(Viewer.Catalog.GetString("Sound process"), "0%", "100%", Color.Cyan, 20);
#if WITH_PATH_DEBUG
            TextPage = 5;
#endif
        }

        protected internal override void Save(BinaryWriter outf)
        {
            base.Save(outf);
            outf.Write(TextPage);
            outf.Write(LastTextPage);
        }

        protected internal override void Restore(BinaryReader inf)
        {
            base.Restore(inf);
            var page = inf.ReadInt32();
            if (page >= 0 && page <= TextPages.Length)
                TextPage = page;
            page = inf.ReadInt32();
            if (page > 0 && page <= TextPages.Length)
                LastTextPage = page;
            else LastTextPage = LocomotivePage;
        }

        public override void Mark()
        {
            base.Mark();
            HUDGraphMaterial.Mark();
        }

        public override bool Interactive
        {
            get
            {
                return false;
            }
        }

        public override void TabAction()
        {
            TextPage = (TextPage + 1) % TextPages.Length;
            if (TextPage != 0) LastTextPage = TextPage;
        }

        public void ToggleBasicHUD()
        {
            TextPage = TextPage == 0 ? LastTextPage : 0;
        }

        int[] lastGCCounts = new int[3];

        public override void PrepareFrame(RenderFrame frame, ElapsedTime elapsedTime, bool updateFull)
        {
            base.PrepareFrame(frame, elapsedTime, updateFull);
#if SHOW_PHYSICS_GRAPHS
            if (Visible && TextPages[TextPage] == TextPageForceInfo)
            {
                var loco = Viewer.PlayerLocomotive as MSTSLocomotive;
                ForceGraphMotiveForce.AddSample(loco.MotiveForceN / loco.MaxForceN);
                ForceGraphDynamicForce.AddSample(-loco.MotiveForceN / loco.MaxForceN);
                ForceGraphNumOfSubsteps.AddSample((float)loco.LocomotiveAxle.AxleRevolutionsInt.NumOfSubstepsPS / (float)loco.LocomotiveAxle.AxleRevolutionsInt.MaxSubsteps);

                ForceGraphs.PrepareFrame(frame);
            }

            if (Visible && TextPages[TextPage] == TextPageLocomotiveInfo)
            {
                var loco = Viewer.PlayerLocomotive as MSTSLocomotive;
                var locoD = Viewer.PlayerLocomotive as MSTSDieselLocomotive;
                var locoE = Viewer.PlayerLocomotive as MSTSElectricLocomotive;
                var locoS = Viewer.PlayerLocomotive as MSTSSteamLocomotive;
                LocomotiveGraphsThrottle.AddSample(loco.ThrottlePercent * 0.01f);
                if (locoD != null)
                {
                    LocomotiveGraphsInputPower.AddSample(locoD.DieselEngines.MaxOutputPowerW / locoD.DieselEngines.MaxPowerW);
                    LocomotiveGraphsOutputPower.AddSample(locoD.DieselEngines.PowerW / locoD.DieselEngines.MaxPowerW);
                }
                if (locoE != null)
                {
                    LocomotiveGraphsInputPower.AddSample(loco.ThrottlePercent * 0.01f);
                    LocomotiveGraphsOutputPower.AddSample((loco.MotiveForceN / loco.MaxPowerW) * loco.SpeedMpS);
                }
                //TODO: plot correct values
                if (locoS != null)
                {
                    LocomotiveGraphsInputPower.AddSample(loco.ThrottlePercent * 0.01f);
                    LocomotiveGraphsOutputPower.AddSample((loco.MotiveForceN / loco.MaxPowerW) * loco.SpeedMpS);
                }

                LocomotiveGraphs.PrepareFrame(frame);
            }
#endif
            if (Visible && TextPages[TextPage] == TextPageDebugInfo)
            {
                var gcCounts = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                DebugGraphMemory.AddSample((float)GetWorkingSetSize() / ProcessVirtualAddressLimit);
                DebugGraphGCs.AddSample(gcCounts[2] > lastGCCounts[2] ? 1.0f : gcCounts[1] > lastGCCounts[1] ? 0.5f : gcCounts[0] > lastGCCounts[0] ? 0.25f : 0);
                DebugGraphFrameTime.AddSample(Viewer.RenderProcess.FrameTime.Value * 10);
                DebugGraphProcessRender.AddSample(Viewer.RenderProcess.Profiler.Wall.Value / 100);
                DebugGraphProcessUpdater.AddSample(Viewer.UpdaterProcess.Profiler.Wall.Value / 100);
                DebugGraphProcessLoader.AddSample(Viewer.LoaderProcess.Profiler.Wall.Value / 100);
                DebugGraphProcessSound.AddSample(Viewer.SoundProcess.Profiler.Wall.Value / 100);
                lastGCCounts = gcCounts;
                DebugGraphs.PrepareFrame(frame);
            }
        }

        public override void PrepareFrame(ElapsedTime elapsedTime, bool updateFull)
        {
            base.PrepareFrame(elapsedTime, updateFull);

            if (updateFull)
            {
                var table = new TableData() { Cells = new string[TextTable.Cells.GetLength(0), TextTable.Cells.GetLength(1)] };
                TextPages[0](table);
                if (TextPage > 0)
                    TextPages[TextPage](table);
                TextTable = table;
            }
        }

        // ==========================================================================================================================================
        //      Method to construct the various Heads Up Display pages for use by the WebServer 
        //      Replaces the Prepare Frame Method
        //      djr - 20171221
        // ==========================================================================================================================================
        public TableData PrepareTable(int PageNo)
        {
            var table = new TableData() { Cells = new string[1, 1] };

            TextPages[PageNo](table);
            return (table);
        }

        public override void Draw(SpriteBatch spriteBatch)
        {
            // Completely customise the rendering of the HUD - don't call base.Draw(spriteBatch).
            for (var row = 0; row < TextTable.Cells.GetLength(0); row++)
            {
                for (var column = 0; column < TextTable.Cells.GetLength(1); column++)
                {
                    if (TextTable.Cells[row, column] != null)
                    {
                        var text = TextTable.Cells[row, column];
                        var align = text.StartsWith(" ") ? LabelAlignment.Right : LabelAlignment.Left;
                        var color = Color.White;
                        if (text.EndsWith("!!!") || text.EndsWith("???"))
                        {
                            color = text.EndsWith("!!!") ? Color.OrangeRed : Color.Yellow;
                            text = text.Substring(0, text.Length - 3);
                        }
                        else if (text.EndsWith("%%%"))
                        {
                            color = Color.Cyan;
                            text = text.Substring(0, text.Length - 3);
                        }
                        else if (text.EndsWith("$$$"))
                        {
                            color = Color.Pink;
                            text = text.Substring(0, text.Length - 3);
                        }
                        TextFont.Draw(spriteBatch, new Rectangle(TextOffset + column * ColumnWidth, TextOffset + row * TextFont.Height, ColumnWidth, TextFont.Height), Point.Zero, text, align, color);
                    }
                }
            }

#if SHOW_PHYSICS_GRAPHS
            if (Visible && TextPages[TextPage] == TextPageForceInfo)
                ForceGraphs.Draw(spriteBatch);
            if (Visible && TextPages[TextPage] == TextPageLocomotiveInfo)
                LocomotiveGraphs.Draw(spriteBatch);
#endif
            if (Visible && TextPages[TextPage] == TextPageDebugInfo)
                DebugGraphs.Draw(spriteBatch);
        }

        #region Table handling


        // ==========================================================================================================================================
        //      Class used to construct table for display of Heads Up Display pages
        //      Original Code has been altered making the class public for use by the WebServer
        //      djr - 20171221
        // ==========================================================================================================================================
        //sealed class TableData
        //{
        //    public string[,] Cells;
        //    public int CurrentRow;
        //    public int CurrentLabelColumn;
        //    public int CurrentValueColumn;
        //}

        public sealed class TableData
        {
            public string[,] Cells;
            public int CurrentRow;
            public int CurrentLabelColumn;
            public int CurrentValueColumn;
        }

        static void TableSetCell(TableData table, int cellColumn, string format, params object[] args)
        {
            TableSetCell(table, table.CurrentRow, cellColumn, format, args);
        }

        static void TableSetCell(TableData table, int cellRow, int cellColumn, string format, params object[] args)
        {
            if (cellRow > table.Cells.GetUpperBound(0) || cellColumn > table.Cells.GetUpperBound(1))
            {
                var newCells = new string[Math.Max(cellRow + 1, table.Cells.GetLength(0)), Math.Max(cellColumn + 1, table.Cells.GetLength(1))];
                for (var row = 0; row < table.Cells.GetLength(0); row++)
                    for (var column = 0; column < table.Cells.GetLength(1); column++)
                        newCells[row, column] = table.Cells[row, column];
                table.Cells = newCells;
            }
            Debug.Assert(!format.Contains('\n'), "HUD table cells must not contain newlines. Use the table positioning instead.");
            table.Cells[cellRow, cellColumn] = args.Length > 0 ? String.Format(format, args) : format;
        }

        static void TableSetCells(TableData table, int startColumn, params string[] columns)
        {
            for (var i = 0; i < columns.Length; i++)
                TableSetCell(table, startColumn + i, columns[i]);
        }

        static void TableAddLine(TableData table)
        {
            table.CurrentRow++;
        }

        static void TableAddLine(TableData table, string format, params object[] args)
        {
            TableSetCell(table, table.CurrentRow, 0, format, args);
            table.CurrentRow++;
        }

        static void TableAddLines(TableData table, string lines)
        {
            if (lines == null)
                return;

            foreach (var line in lines.Split('\n'))
            {
                var column = 0;
                foreach (var cell in line.Split('\t'))
                    TableSetCell(table, column++, "{0}", cell);
                table.CurrentRow++;
            }
        }

        static void TableSetLabelValueColumns(TableData table, int labelColumn, int valueColumn)
        {
            table.CurrentLabelColumn = labelColumn;
            table.CurrentValueColumn = valueColumn;
        }

        static void TableAddLabelValue(TableData table, string label, string format, params object[] args)
        {
            TableSetCell(table, table.CurrentRow, table.CurrentLabelColumn, label);
            TableSetCell(table, table.CurrentRow, table.CurrentValueColumn, format, args);
            table.CurrentRow++;
        }
        #endregion

        void TextPageCommon(TableData table)
        {
            var playerTrain = Viewer.PlayerLocomotive.Train;
            var showMUReverser = Math.Abs(playerTrain.MUReverserPercent) != 100;
            var showRetainers = playerTrain.RetainerSetting != RetainerSetting.Exhaust;
            var engineBrakeStatus = Viewer.PlayerLocomotive.GetEngineBrakeStatus();
            var brakemanBrakeStatus = Viewer.PlayerLocomotive.GetBrakemanBrakeStatus();
            var dynamicBrakeStatus = Viewer.PlayerLocomotive.GetDynamicBrakeStatus();
            var locomotiveStatus = Viewer.PlayerLocomotive.GetStatus();
            var stretched = playerTrain.Cars.Count > 1 && playerTrain.NPull == playerTrain.Cars.Count - 1;
            var bunched = !stretched && playerTrain.Cars.Count > 1 && playerTrain.NPush == playerTrain.Cars.Count - 1;

            TableSetLabelValueColumns(table, 0, 2);
            TableAddLabelValue(table, Viewer.Catalog.GetString("Version"), VersionInfo.VersionOrBuild);

            // Client and server may have a time difference.
            if (Orts.MultiPlayer.MPManager.IsClient())
                TableAddLabelValue(table, Viewer.Catalog.GetString("Time"), FormatStrings.FormatTime(Viewer.Simulator.ClockTime + Orts.MultiPlayer.MPManager.Instance().serverTimeDifference));
            else
                TableAddLabelValue(table, Viewer.Catalog.GetString("Time"), FormatStrings.FormatTime(Viewer.Simulator.ClockTime));

            if (Viewer.Simulator.IsReplaying)
                TableAddLabelValue(table, Viewer.Catalog.GetString("Replay"), FormatStrings.FormatTime(Viewer.Log.ReplayEndsAt - Viewer.Simulator.ClockTime));

            TableAddLabelValue(table, Viewer.Catalog.GetString("Speed"), FormatStrings.FormatSpeedDisplay(Viewer.PlayerLocomotive.SpeedMpS, Viewer.PlayerLocomotive.IsMetric));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Gradient"), "{0:F1}%", -Viewer.PlayerLocomotive.CurrentElevationPercent);
            TableAddLabelValue(table, Viewer.Catalog.GetString("Direction"), showMUReverser ? "{1:F0} {0}" : "{0}", FormatStrings.Catalog.GetParticularString("Reverser", GetStringAttribute.GetPrettyName(Viewer.PlayerLocomotive.Direction)), Math.Abs(playerTrain.MUReverserPercent));
            TableAddLabelValue(table, Viewer.PlayerLocomotive is MSTSSteamLocomotive ? Viewer.Catalog.GetString("Regulator") : Viewer.Catalog.GetString("Throttle"), "{0:F0}%", Viewer.PlayerLocomotive.ThrottlePercent);
            if ((Viewer.PlayerLocomotive as MSTSLocomotive).TrainBrakeFitted)
                TableAddLabelValue(table, Viewer.Catalog.GetString("Train brake"), "{0}", Viewer.PlayerLocomotive.GetTrainBrakeStatus());
            if (showRetainers)
                TableAddLabelValue(table, Viewer.Catalog.GetString("Retainers"), "{0}% {1}", playerTrain.RetainerPercent, Viewer.Catalog.GetString(GetStringAttribute.GetPrettyName(playerTrain.RetainerSetting)));
            if ((Viewer.PlayerLocomotive as MSTSLocomotive).EngineBrakeFitted) // ideally this test should be using "engineBrakeStatus != null", but this currently does not work, as a controller is defined by default
                TableAddLabelValue(table, Viewer.Catalog.GetString("Engine brake"), "{0}", engineBrakeStatus);
            if ((Viewer.PlayerLocomotive as MSTSLocomotive).BrakemanBrakeFitted)
                TableAddLabelValue(table, Viewer.Catalog.GetString("Brakemen brake"), "{0}", brakemanBrakeStatus);
            if (dynamicBrakeStatus != null)
                TableAddLabelValue(table, Viewer.Catalog.GetString("Dynamic brake"), "{0}", dynamicBrakeStatus);
            if (locomotiveStatus != null)
            {
                var lines = locomotiveStatus.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Length > 0)
                    {
                        var parts = line.Split(new[] { " = " }, 2, StringSplitOptions.None);
                        TableAddLabelValue(table, parts[0], parts.Length > 1 ? parts[1] : "");
                    }
                }
            }
            TableAddLine(table);
            TableAddLabelValue(table, Viewer.Catalog.GetString("FPS"), "{0:F0}", Viewer.RenderProcess.FrameRate.SmoothedValue);
            TableAddLine(table);

            if (Viewer.PlayerLocomotive.Train.TrainType == Train.TRAINTYPE.AI_PLAYERHOSTING)
                TableAddLine(table, Viewer.Catalog.GetString("Autopilot") + "???");

            if (Viewer.PlayerTrain.IsWheelSlip)
                TableAddLine(table, Viewer.Catalog.GetString("Wheel slip") + "!!!");
            else if (Viewer.PlayerTrain.IsWheelSlipWarninq)
                TableAddLine(table, Viewer.Catalog.GetString("Wheel slip warning") + "???");

            if (Viewer.PlayerTrain.IsBrakeSkid )
                TableAddLine(table, Viewer.Catalog.GetString("Wheel skid") + "!!!");

            if (Viewer.PlayerLocomotive.GetSanderOn())
            {
                var sanderBlocked = Viewer.PlayerLocomotive is MSTSLocomotive && Math.Abs(playerTrain.SpeedMpS) > ((MSTSLocomotive)Viewer.PlayerLocomotive).SanderSpeedOfMpS;
                if (sanderBlocked)
                    TableAddLine(table, Viewer.Catalog.GetString("Sander blocked") + "!!!");
                else
                    TableAddLine(table, Viewer.Catalog.GetString("Sander on") + "???");
            }

            if ((Viewer.PlayerLocomotive as MSTSWagon).DoorLeftOpen || (Viewer.PlayerLocomotive as MSTSWagon).DoorRightOpen)
            {
                var color = Math.Abs(Viewer.PlayerLocomotive.SpeedMpS) > 0.1f ? "!!!" : "???";
                var status = "";
                if ((Viewer.PlayerLocomotive as MSTSWagon).DoorLeftOpen)
                    status += Viewer.Catalog.GetString((Viewer.PlayerLocomotive as MSTSLocomotive).GetCabFlipped()? "Right" : "Left");
                if ((Viewer.PlayerLocomotive as MSTSWagon).DoorRightOpen)
                    status += string.Format(status == "" ? "{0}" : " {0}", Viewer.Catalog.GetString((Viewer.PlayerLocomotive as MSTSLocomotive).GetCabFlipped() ? "Left" : "Right"));
                status += color;

                TableAddLabelValue(table, Viewer.Catalog.GetString("Doors open") + color, status);
            }
            if (Orts.MultiPlayer.MPManager.IsMultiPlayer())
            {
                var text = Orts.MultiPlayer.MPManager.Instance().GetOnlineUsersInfo();

                TableAddLabelValue(table, Viewer.Catalog.GetString("MultiPlayerStatus: "), "{0}", Orts.MultiPlayer.MPManager.IsServer()
                    ? Viewer.Catalog.GetString("Dispatcher") : Orts.MultiPlayer.MPManager.Instance().AmAider
                    ? Viewer.Catalog.GetString("Helper") : Orts.MultiPlayer.MPManager.IsClient()
                    ? Viewer.Catalog.GetString("Client") : "");
                TableAddLine(table);
                foreach (var t in text.Split('\t'))
                    TableAddLine(table, "{0}", t);
            }
        }

        void TextPageConsistInfo(TableData table)
        {
            TextPageHeading(table, Viewer.Catalog.GetString("CONSIST INFORMATION"));

            var locomotive = Viewer.PlayerLocomotive;
            var mstsLocomotive = locomotive as MSTSLocomotive;
            var train = locomotive.Train;
            float tonnage = 0f;
            foreach (var car in train.Cars)
            {
                if(car.WagonType == TrainCar.WagonTypes.Freight || car.WagonType == TrainCar.WagonTypes.Passenger)
                    tonnage += car.MassKG;
            }
            TableSetCells(table, 0,
                Viewer.Catalog.GetString("Player"),
                Viewer.Catalog.GetString("Tilted"),
                Viewer.Catalog.GetString("Type"),
                Viewer.Catalog.GetString("Length"),
                Viewer.Catalog.GetString("Weight"), "",
                Viewer.Catalog.GetString("Tonnage"), "",
                Viewer.Catalog.GetString("Control Mode"), "",
                Viewer.Catalog.GetString("Out of Control"), "",
                Viewer.Catalog.GetString("Cab Aspect"));
            TableAddLine(table);
            TableSetCells(table, 0, locomotive.CarID + " " + (mstsLocomotive == null ? "" : mstsLocomotive.UsingRearCab ? Viewer.Catalog.GetParticularString("Cab", "R") : Viewer.Catalog.GetParticularString("Cab", "F")),
                train.IsTilting ? Viewer.Catalog.GetString("Yes") : Viewer.Catalog.GetString("No"),
                train.IsFreight ? Viewer.Catalog.GetString("Freight") : Viewer.Catalog.GetString("Pass"),
                FormatStrings.FormatShortDistanceDisplay(train.Length, locomotive.IsMetric),
                FormatStrings.FormatLargeMass(train.MassKg, locomotive.IsMetric, locomotive.IsUK), "",
                FormatStrings.FormatLargeMass(tonnage, locomotive.IsMetric, locomotive.IsUK), "",
                train.ControlMode.ToString(), "",
                train.OutOfControlReason.ToString(), "",
                mstsLocomotive.TrainControlSystem.CabSignalAspect.ToString());
            TableAddLine(table);
            TableAddLine(table);
            TableSetCells(table, 0,
                Viewer.Catalog.GetString("Car"),
                Viewer.Catalog.GetString("Flipped"),
                Viewer.Catalog.GetString("Type"),
                Viewer.Catalog.GetString("Length"),
                Viewer.Catalog.GetString("Weight"),
                Viewer.Catalog.GetString("Drv/Cabs"),
                Viewer.Catalog.GetString("Wheels"));
            TableAddLine(table);
            foreach (var car in train.Cars.Take(20))
            {
                TableSetCells(table, 0, car.CarID,
                    car.Flipped ? Viewer.Catalog.GetString("Yes") : Viewer.Catalog.GetString("No"),
                    train.IsFreight ? Viewer.Catalog.GetString("Freight") : Viewer.Catalog.GetString("Pass"),
                    FormatStrings.FormatShortDistanceDisplay(car.CarLengthM, locomotive.IsMetric),
                    FormatStrings.FormatLargeMass(car.MassKG, locomotive.IsMetric, locomotive.IsUK),
                    (car.IsDriveable ? Viewer.Catalog.GetParticularString("Cab", "D") : "") + (car.HasFrontCab || car.HasFront3DCab ? Viewer.Catalog.GetParticularString("Cab", "F") : "") + (car.HasRearCab || car.HasRear3DCab ? Viewer.Catalog.GetParticularString("Cab", "R") : ""),
                    GetCarWhyteLikeNotation(car));
                TableAddLine(table);
            }
        }

        static string GetCarWhyteLikeNotation(TrainCar car)
        {
            if (car.WheelAxles.Count == 0)
                return "";

            var whyte = new List<string>();
            var currentCount = 0;
            var currentBogie = car.WheelAxles[0].BogieIndex;
            foreach (var axle in car.WheelAxles)
            {
                if (currentBogie != axle.BogieIndex)
                {
                    whyte.Add(currentCount.ToString());
                    currentBogie = axle.BogieIndex;
                    currentCount = 0;
                }
                currentCount += 2;
            }
            whyte.Add(currentCount.ToString());
            return String.Join("-", whyte.ToArray());
        }

        void TextPageLocomotiveInfo(TableData table)
        {
            TextPageHeading(table, Viewer.Catalog.GetString("LOCOMOTIVE INFORMATION"));

            var locomotive = Viewer.PlayerLocomotive;
            var train = locomotive.Train;

            TableAddLines(table, String.Format("{8}\t\t{0} {4}\t\t{1} {5:F0}%\t\t{2} {6:F0}%\t\t{3} {7}",
                Viewer.Catalog.GetString("Direction"),
                Viewer.PlayerLocomotive is MSTSSteamLocomotive ? Viewer.Catalog.GetParticularString("Steam", "Reverser") : Viewer.Catalog.GetParticularString("NonSteam", "Reverser"),
                Viewer.PlayerLocomotive is MSTSSteamLocomotive ? Viewer.Catalog.GetString("Regulator") : Viewer.Catalog.GetString("Throttle"),
                Viewer.Catalog.GetString("Dynamic brake"),
                FormatStrings.Catalog.GetParticularString("Reverser", GetStringAttribute.GetPrettyName(train.MUDirection)),
                train.MUReverserPercent,
                train.MUThrottlePercent,
                train.MUDynamicBrakePercent >= 0 ? string.Format("{0:F0}%", train.MUDynamicBrakePercent) : Viewer.Catalog.GetString("off"),
                Viewer.Catalog.GetString("PlayerLoco")));
            TableAddLine(table);
            TableSetCells(table, 0,
                               Viewer.Catalog.GetString("Loco"),
                               Viewer.Catalog.GetString("Direction"),
                               Viewer.Catalog.GetString("Flipped"),
                               Viewer.Catalog.GetString("MU'd"),
                               Viewer.Catalog.GetString("Throttle"),
                               Viewer.Catalog.GetString("Speed"),
                               "",
                               Viewer.Catalog.GetString("Power"),
                               Viewer.Catalog.GetString("Force")
                               );
            TableAddLine(table);
            foreach (var car in train.Cars)
                if (car is MSTSLocomotive)
                    TableAddLines(table, car.GetDebugStatus());
        }

        void TextPageBrakeInfo(TableData table)
        {
            TextPageHeading(table, Viewer.Catalog.GetString("BRAKE INFORMATION"));

            var train = Viewer.PlayerLocomotive.Train;
            var mstsLocomotive = Viewer.PlayerLocomotive as MSTSLocomotive;
            var HUDSteamEngineType = mstsLocomotive.SteamEngineType;
            var HUDEngineType = mstsLocomotive.EngineType;

            if ((Viewer.PlayerLocomotive as MSTSLocomotive).TrainBrakeFitted) // Only display the following information if a train brake is defined.
            {
                // If vacuum brakes are used then use this display
                if ((Viewer.PlayerLocomotive as MSTSLocomotive).BrakeSystem is VacuumSinglePipe)
                {

                    if ((Viewer.PlayerLocomotive as MSTSLocomotive).VacuumBrakeEQFitted)
                    {
                        TableAddLines(table, String.Format("{0}\t\t{1}\t\t{2}",
                        Viewer.Catalog.GetString("PlayerLoco"),
                        Viewer.Catalog.GetString("Exhauster"),
                        (Viewer.PlayerLocomotive as MSTSLocomotive).VacuumExhausterIsOn ? Viewer.Catalog.GetString("on") : Viewer.Catalog.GetString("off")));
                    }

                    else if ((Viewer.PlayerLocomotive as MSTSLocomotive).VacuumPumpFitted && (Viewer.PlayerLocomotive as MSTSLocomotive).SmallEjectorControllerFitted)
                    {
                        // Display if vacuum pump, large ejector and small ejector fitted
                        TableAddLines(table, String.Format("{0}\t\t{1}\t\t{2}\t{3}\t\t{4}\t{5}\t{6}\t\t{7}\t\t{8}",
                        Viewer.Catalog.GetString("PlayerLoco"),
                        Viewer.Catalog.GetString("Large Ejector"),
                        (Viewer.PlayerLocomotive as MSTSLocomotive).LargeSteamEjectorIsOn ? Viewer.Catalog.GetString("on") : Viewer.Catalog.GetString("off"),
                        Viewer.Catalog.GetString("Small Ejector"),
                        (Viewer.PlayerLocomotive as MSTSLocomotive).SmallSteamEjectorIsOn ? Viewer.Catalog.GetString("on") : Viewer.Catalog.GetString("off"),
                        Viewer.Catalog.GetString("Pressure"),
                        FormatStrings.FormatPressure((Viewer.PlayerLocomotive as MSTSLocomotive).SteamEjectorSmallPressurePSI, PressureUnit.PSI, (Viewer.PlayerLocomotive as MSTSLocomotive).BrakeSystemPressureUnits[BrakeSystemComponent.MainReservoir], true),
                        Viewer.Catalog.GetString("Vacuum Pump"),
                        (Viewer.PlayerLocomotive as MSTSLocomotive).VacuumPumpOperating ? Viewer.Catalog.GetString("on") : Viewer.Catalog.GetString("off")
                        ));
                    }
                    else if ((Viewer.PlayerLocomotive as MSTSLocomotive).VacuumPumpFitted && !(Viewer.PlayerLocomotive as MSTSLocomotive).SmallEjectorControllerFitted) // Change display so that small ejector is not displayed for vacuum pump operated locomotives
                    {
                        // Display if vacuum pump, and large ejector only fitted
                        TableAddLines(table, String.Format("{0}\t\t{1}\t\t{2}\t{3}\t\t{4}",
                        Viewer.Catalog.GetString("PlayerLoco"),
                        Viewer.Catalog.GetString("Large Ejector"),
                        (Viewer.PlayerLocomotive as MSTSLocomotive).LargeSteamEjectorIsOn ? Viewer.Catalog.GetString("on") : Viewer.Catalog.GetString("off"),
                        Viewer.Catalog.GetString("Vacuum Pump"),
                        (Viewer.PlayerLocomotive as MSTSLocomotive).VacuumPumpOperating ? Viewer.Catalog.GetString("on") : Viewer.Catalog.GetString("off")));
                    }
                    else
                    {
                        // Display if large ejector and small ejector only fitted
                        TableAddLines(table, String.Format("{0}\t\t{1}\t\t{2}\t{3}\t\t{4}\t{5}\t{6}",
                        Viewer.Catalog.GetString("PlayerLoco"),
                        Viewer.Catalog.GetString("Large Ejector"),
                        (Viewer.PlayerLocomotive as MSTSLocomotive).LargeSteamEjectorIsOn ? Viewer.Catalog.GetString("on") : Viewer.Catalog.GetString("off"),
                        Viewer.Catalog.GetString("Small Ejector"),
                        (Viewer.PlayerLocomotive as MSTSLocomotive).SmallSteamEjectorIsOn ? Viewer.Catalog.GetString("on") : Viewer.Catalog.GetString("off"),
                        Viewer.Catalog.GetString("Pressure"),
                        FormatStrings.FormatPressure((Viewer.PlayerLocomotive as MSTSLocomotive).SteamEjectorSmallPressurePSI, PressureUnit.PSI, (Viewer.PlayerLocomotive as MSTSLocomotive).BrakeSystemPressureUnits[BrakeSystemComponent.MainReservoir], true)));
                    }

                    // Lines to show brake system volumes
                    TableAddLines(table, String.Format("{0}\t\t{1}\t\t{2}\t{3}\t\t{4}\t{5}\t{6}",
                    Viewer.Catalog.GetString("Brake Sys Vol"),
                    Viewer.Catalog.GetString("Train Pipe"),
                    FormatStrings.FormatVolume(train.TotalTrainBrakePipeVolumeM3, mstsLocomotive.IsMetric),
                    Viewer.Catalog.GetString("Brake Cyl"),
                    FormatStrings.FormatVolume(train.TotalTrainBrakeCylinderVolumeM3, mstsLocomotive.IsMetric),
                    Viewer.Catalog.GetString("Air Vol"),
                    FormatStrings.FormatVolume(train.TotalCurrentTrainBrakeSystemVolumeM3, mstsLocomotive.IsMetric)
                    ));

                }
                else  // Default to air or electronically braked, use this display
                {
                    TableAddLines(table, String.Format("{0}\t\t{1}\t\t{2}\t{3}\t\t{4}",
                        Viewer.Catalog.GetString("PlayerLoco"),
                        Viewer.Catalog.GetString("Main reservoir"),
                        FormatStrings.FormatPressure((Viewer.PlayerLocomotive as MSTSLocomotive).MainResPressurePSI, PressureUnit.PSI, (Viewer.PlayerLocomotive as MSTSLocomotive).BrakeSystemPressureUnits[BrakeSystemComponent.MainReservoir], true),
                        Viewer.Catalog.GetString("Compressor"),
                        (Viewer.PlayerLocomotive as MSTSLocomotive).CompressorIsOn ? Viewer.Catalog.GetString("on") : Viewer.Catalog.GetString("off")));
                }

                // Display data for other locomotives
                for (var i = 0; i < train.Cars.Count; i++)
                {
                    var car = train.Cars[i];
                    if (car is MSTSLocomotive && car != Viewer.PlayerLocomotive)
                    {
                        TableAddLines(table, String.Format("{0}\t{1}\t{2}\t\t{3}\t{4}\t\t{5}",
                            Viewer.Catalog.GetString("Loco"),
                            car.CarID,
                            Viewer.Catalog.GetString("Main reservoir"),
                            FormatStrings.FormatPressure((car as MSTSLocomotive).MainResPressurePSI, PressureUnit.PSI, (car as MSTSLocomotive).BrakeSystemPressureUnits[BrakeSystemComponent.MainReservoir], true),
                            Viewer.Catalog.GetString("Compressor"),
                            (car as MSTSLocomotive).CompressorIsOn ? Viewer.Catalog.GetString("on") : Viewer.Catalog.GetString("off")));
                    }
                }
                TableAddLine(table);
            }
            // Different display depending upon whether vacuum braked, manual braked or air braked
            if ((Viewer.PlayerLocomotive as MSTSLocomotive).BrakeSystem is VacuumSinglePipe)
            {
                if ((Viewer.PlayerLocomotive as MSTSLocomotive).NonAutoBrakePresent) // Straight brake system
                {
                    TableSetCells(table, 0,
                    Viewer.Catalog.GetString("Car"),
                    Viewer.Catalog.GetString("Type"),
                    Viewer.Catalog.GetString("BrkCyl"),
                    Viewer.Catalog.GetString("BrkPipe"),
                    Viewer.Catalog.GetString(""),
                    Viewer.Catalog.GetString(""),
                    Viewer.Catalog.GetString(""),
                    Viewer.Catalog.GetString(""),
                    Viewer.Catalog.GetString(""),
                    Viewer.Catalog.GetString(""),
                    Viewer.Catalog.GetString("Handbrk"),
                    Viewer.Catalog.GetString("Conn"),
                    Viewer.Catalog.GetString("AnglCock")
                                                                                                );
                    TableAddLine(table);
                }
                else // automatic vacuum brake system
                {
                    TableSetCells(table, 0,
                    Viewer.Catalog.GetString("Car"),
                    Viewer.Catalog.GetString("Type"),
                    Viewer.Catalog.GetString("BrkCyl"),
                    Viewer.Catalog.GetString("BrkPipe"),
                    Viewer.Catalog.GetString("VacRes"),
                    Viewer.Catalog.GetString(""),
                    Viewer.Catalog.GetString(""),
                    Viewer.Catalog.GetString(""),
                    Viewer.Catalog.GetString(""),
                    Viewer.Catalog.GetString(""),
                    Viewer.Catalog.GetString("Handbrk"),
                    Viewer.Catalog.GetString("Conn"),
                    Viewer.Catalog.GetString("AnglCock")
                                                                                                );
                    TableAddLine(table);
                }

                var n = train.Cars.Count; // Number of lines to show
                for (var i = 0; i < n; i++)
                {
                    var j = i < 2 ? i : i * (train.Cars.Count - 1) / (n - 1);
                    var car = train.Cars[j];
                    TableSetCell(table, 0, "{0}", car.CarID);
                    TableSetCells(table, 1, car.BrakeSystem.GetDebugStatus((Viewer.PlayerLocomotive as MSTSLocomotive).BrakeSystemPressureUnits));
                    TableAddLine(table);
                }
            }
            else if ((Viewer.PlayerLocomotive as MSTSLocomotive).BrakeSystem is ManualBraking)
            {
                TableSetCells(table, 0,
                Viewer.Catalog.GetString("Car"),
                Viewer.Catalog.GetString("Type"),
                Viewer.Catalog.GetString("Brk"),
                Viewer.Catalog.GetString(""),
                Viewer.Catalog.GetString(""),
                Viewer.Catalog.GetString(""),
                Viewer.Catalog.GetString(""),
                Viewer.Catalog.GetString(""),
                Viewer.Catalog.GetString(""),
                Viewer.Catalog.GetString(""),
                Viewer.Catalog.GetString("Handbrk"),
                Viewer.Catalog.GetString(" "),
                Viewer.Catalog.GetString("")
                );
                TableAddLine(table);

                var n = train.Cars.Count; // Number of lines to show
                for (var i = 0; i < n; i++)
                {
                    var j = i < 2 ? i : i * (train.Cars.Count - 1) / (n - 1);
                    var car = train.Cars[j];
                    TableSetCell(table, 0, "{0}", car.CarID);
                    TableSetCells(table, 1, car.BrakeSystem.GetDebugStatus((Viewer.PlayerLocomotive as MSTSLocomotive).BrakeSystemPressureUnits));
                    TableAddLine(table);

                }
            }
            else  // default air braked
            {
                TableSetCells(table, 0,
                                Viewer.Catalog.GetString("Car"),
                                Viewer.Catalog.GetString("Type"),
                                Viewer.Catalog.GetString("BrkCyl"),
                                Viewer.Catalog.GetString("BrkPipe"),
                                Viewer.Catalog.GetString("AuxRes"),
                                Viewer.Catalog.GetString("ErgRes"),
                                Viewer.Catalog.GetString("MRPipe"),
                                Viewer.Catalog.GetString("RetValve"),
                                Viewer.Catalog.GetString("TripleValve"),
                                Viewer.Catalog.GetString(""),
                                Viewer.Catalog.GetString("Handbrk"),
                                Viewer.Catalog.GetString("Conn"),
                                Viewer.Catalog.GetString("AnglCock"),
                                Viewer.Catalog.GetString("BleedOff"));
                TableAddLine(table);

                var n = train.Cars.Count; // Number of lines to show
                for (var i = 0; i < n; i++)
                {
                    var j = i < 2 ? i : i * (train.Cars.Count - 1) / (n - 1);
                    var car = train.Cars[j];
                    TableSetCell(table, 0, "{0}", car.CarID);
                    TableSetCells(table, 1, car.BrakeSystem.GetDebugStatus((Viewer.PlayerLocomotive as MSTSLocomotive).BrakeSystemPressureUnits));
                    TableAddLine(table);
                }
            }
        }

        void TextPageForceInfo(TableData table)
        {
            TextPageHeading(table, Viewer.Catalog.GetString("FORCE INFORMATION"));

            var train = Viewer.PlayerLocomotive.Train;
            var mstsLocomotive = Viewer.PlayerLocomotive as MSTSLocomotive;

            if (mstsLocomotive != null)
            {
                if ( mstsLocomotive.AdvancedAdhesionModel)
                {
                    var HUDSteamEngineType = mstsLocomotive.SteamEngineType;
                    var HUDEngineType = mstsLocomotive.EngineType;

                    if (HUDEngineType == TrainCar.EngineTypes.Steam && (HUDSteamEngineType == TrainCar.SteamEngineTypes.Compound || HUDSteamEngineType == TrainCar.SteamEngineTypes.Simple || HUDSteamEngineType == TrainCar.SteamEngineTypes.Unknown)) // For display of steam locomotive adhesion info
                    {
                        TableAddLine(table, Viewer.Catalog.GetString("(Advanced adhesion model)"));
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Loco Adhesion"), "{0:F0}%", mstsLocomotive.LocomotiveCoefficientFrictionHUD * 100.0f);
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Wag Adhesion"), "{0:F0}%", mstsLocomotive.WagonCoefficientFrictionHUD * 100.0f);
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Tang. Force"), "{0:F0}", FormatStrings.FormatForce(N.FromLbf(mstsLocomotive.SteamTangentialWheelForce), mstsLocomotive.IsMetric));
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Static Force"), "{0:F0}", FormatStrings.FormatForce(N.FromLbf(mstsLocomotive.SteamStaticWheelForce), mstsLocomotive.IsMetric));
                      //  TableAddLabelValue(table, Viewer.Catalog.GetString("Axle brake force"), "{0}", FormatStrings.FormatForce(mstsLocomotive.LocomotiveAxle.BrakeForceN, mstsLocomotive.IsMetric));
                    }
                    else  // Advanced adhesion non steam locomotives HUD display
                    {
                        TableAddLine(table, Viewer.Catalog.GetString("(Advanced adhesion model)"));
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Wheel slip"), "{0:F0}% ({1:F0}%/{2})", mstsLocomotive.LocomotiveAxle.SlipSpeedPercent, mstsLocomotive.LocomotiveAxle.SlipDerivationPercentpS, FormatStrings.s);
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Conditions"), "{0:F0}%", mstsLocomotive.LocomotiveAxle.AdhesionConditions * 100.0f);
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Axle drive force"), "{0}", FormatStrings.FormatForce(mstsLocomotive.LocomotiveAxle.DriveForceN, mstsLocomotive.IsMetric));
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Axle brake force"), "{0}", FormatStrings.FormatForce(mstsLocomotive.LocomotiveAxle.BrakeRetardForceN, mstsLocomotive.IsMetric));
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Number of substeps"), "{0:F0} ({1})", mstsLocomotive.LocomotiveAxle.AxleRevolutionsInt.NumOfSubstepsPS,
                                                                                                    Viewer.Catalog.GetStringFmt("filtered by {0:F0}", mstsLocomotive.LocomotiveAxle.FilterMovingAverage.Size));
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Solver"), "{0}", mstsLocomotive.LocomotiveAxle.AxleRevolutionsInt.Method.ToString());
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Stability correction"), "{0:F0}", mstsLocomotive.LocomotiveAxle.AdhesionK);
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Axle out force"), "{0} ({1})",
                            FormatStrings.FormatForce(mstsLocomotive.LocomotiveAxle.AxleForceN, mstsLocomotive.IsMetric),
                            FormatStrings.FormatPower(mstsLocomotive.LocomotiveAxle.AxleForceN * mstsLocomotive.WheelSpeedMpS, mstsLocomotive.IsMetric, false, false));
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Loco Adhesion"), "{0:F0}%", mstsLocomotive.LocomotiveCoefficientFrictionHUD * 100.0f);
                        TableAddLabelValue(table, Viewer.Catalog.GetString("Wagon Adhesion"), "{0:F0}%", mstsLocomotive.WagonCoefficientFrictionHUD * 100.0f);
                    }
                }
                else
                {
                    TableAddLine(table, Viewer.Catalog.GetString("(Simple adhesion model)"));
                    TableAddLabelValue(table, Viewer.Catalog.GetString("Axle out force"), "{0:F0} N ({1:F0} kW)", mstsLocomotive.MotiveForceN, mstsLocomotive.MotiveForceN * mstsLocomotive.SpeedMpS / 1000.0f);
                    TableAddLabelValue(table, Viewer.Catalog.GetString("Loco Adhesion"), "{0:F0}%", mstsLocomotive.LocomotiveCoefficientFrictionHUD * 100.0f);
                    TableAddLabelValue(table, Viewer.Catalog.GetString("Wagon Adhesion"), "{0:F0}%", mstsLocomotive.WagonCoefficientFrictionHUD * 100.0f);
                }
                TableAddLine(table);

                if (train.TrainWindResistanceDependent) // Only show this information if wind resistance is selected
                {
                    TableAddLine(table, "Wind Speed: {0:N2} mph   Wind Direction: {1:N2} Deg   Train Direction: {2:N2} Deg    ResWind: {3:N2} Deg   ResSpeed {4:N2} mph", Me.ToMi(pS.TopH(train.PhysicsWindSpeedMpS)), train.PhysicsWindDirectionDeg, train.PhysicsTrainLocoDirectionDeg, train.ResultantWindComponentDeg, Me.ToMi(pS.TopH(train.WindResultantSpeedMpS)));

                    TableAddLine(table);
                }


            }

            TableSetCells(table, 0,
                Viewer.Catalog.GetString("Car"),
                Viewer.Catalog.GetString("Total"),
                Viewer.Catalog.GetString("Motive"),
                Viewer.Catalog.GetString("Brake"),
                Viewer.Catalog.GetString("Friction"),
                Viewer.Catalog.GetString("Gravity"),
                Viewer.Catalog.GetString("Curve"),
                Viewer.Catalog.GetString("Tunnel"),
                Viewer.Catalog.GetString("Wind"),
                Viewer.Catalog.GetString("Coupler"),
                Viewer.Catalog.GetString("Coupler"),
                Viewer.Catalog.GetString("Slack"),
                Viewer.Catalog.GetString("Mass"),
                Viewer.Catalog.GetString("Gradient"),
                Viewer.Catalog.GetString("Curve"),
                Viewer.Catalog.GetString("Brk Frict."),
                Viewer.Catalog.GetString("Brk Slide"),
                Viewer.Catalog.GetString("Bear Temp")

                // Possibly needed for buffing forces
                //                Viewer.Catalog.GetString("VertD"),
                //                Viewer.Catalog.GetString("VertL"),
                //                Viewer.Catalog.GetString("BuffExc"),
                //                Viewer.Catalog.GetString("CplAng")

                );
            TableAddLine(table);

            var n = train.Cars.Count; // Number of lines to show
            for (var i = 0; i < n; i++)
            {
                var j = i == 0 ? 0 : i * (train.Cars.Count - 1) / (n - 1);
                var car = train.Cars[j];
                TableSetCell(table, 0, "{0}", car.CarID);
                TableSetCell(table, 1, "{0}", FormatStrings.FormatForce(car.TotalForceN, car.IsMetric));
                TableSetCell(table, 2, "{0}{1}", FormatStrings.FormatForce(car.MotiveForceN, car.IsMetric), car.WheelSlip ? "!!!" : car.WheelSlipWarning ? "???" : "");
                TableSetCell(table, 3, "{0}", FormatStrings.FormatForce(car.BrakeForceN + car.DynamicBrakeForceN, car.IsMetric));
                TableSetCell(table, 4, "{0}", FormatStrings.FormatForce(car.FrictionForceN, car.IsMetric));
                TableSetCell(table, 5, "{0}", FormatStrings.FormatForce(car.GravityForceN, car.IsMetric));
                TableSetCell(table, 6, "{0}", FormatStrings.FormatForce(car.CurveForceN, car.IsMetric));
                TableSetCell(table, 7, "{0}", FormatStrings.FormatForce(car.TunnelForceN, car.IsMetric));
                TableSetCell(table, 8, "{0}", FormatStrings.FormatForce(car.WindForceN, car.IsMetric));
                TableSetCell(table, 9, "{0}", FormatStrings.FormatForce(car.CouplerForceU, car.IsMetric));
                TableSetCell(table, 10, "{0} : {1}", car.GetCouplerRigidIndication() ? "R" : "F", car.CouplerExceedBreakLimit ? "xxx"+"!!!" : car.CouplerOverloaded ? "O/L"+"???" : car.HUDCouplerForceIndication == 1 ? "Pull" : car.HUDCouplerForceIndication == 2 ? "Push" : "-");
                TableSetCell(table, 11, "{0}", FormatStrings.FormatVeryShortDistanceDisplay( car.CouplerSlackM, car.IsMetric));
                TableSetCell(table, 12, "{0}", FormatStrings.FormatLargeMass(car.MassKG, car.IsMetric, car.IsUK));
                TableSetCell(table, 13, "{0:F2}%", -car.CurrentElevationPercent);
                TableSetCell(table, 14, "{0}", FormatStrings.FormatDistance(car.CurrentCurveRadius, car.IsMetric));
                TableSetCell(table, 15, "{0:F0}%", car.BrakeShoeCoefficientFriction * 100.0f);
                TableSetCell(table, 16, car.HUDBrakeSkid ? Viewer.Catalog.GetString("Yes") : Viewer.Catalog.GetString("No"));
                TableSetCell(table, 17, "{0} {1}", FormatStrings.FormatTemperature(car.WheelBearingTemperatureDegC, car.IsMetric, false), car.DisplayWheelBearingTemperatureStatus);

                // Possibly needed for buffing forces
                //                TableSetCell(table, 17, "{0}", FormatStrings.FormatForce(car.WagonVerticalDerailForceN, car.IsMetric));
                //                TableSetCell(table, 18, "{0}", FormatStrings.FormatForce(car.TotalWagonLateralDerailForceN, car.IsMetric));
                //                TableSetCell(table, 19, car.BuffForceExceeded ? Viewer.Catalog.GetString("Yes") : "No");

                //                TableSetCell(table, 20, "{0:F2}", MathHelper.ToDegrees(car.WagonFrontCouplerAngleRad));


                TableSetCell(table, 18, car.Flipped ? Viewer.Catalog.GetString("Flipped") : "");
                TableAddLine(table);

            }

            TableAddLine(table);
            TableSetCell(table, 11, "Tot {0}", FormatStrings.FormatShortDistanceDisplay(train.TotalCouplerSlackM, mstsLocomotive.IsMetric));
        }

        void TextPageDispatcherInfo(TableData table)
        {
            // count active trains
            int totalactive = 0;
            foreach (var thisTrain in Viewer.Simulator.AI.AITrains)
            {
                if (thisTrain.MovementState != AITrain.AI_MOVEMENT_STATE.AI_STATIC && thisTrain.TrainType != Train.TRAINTYPE.AI_INCORPORATED)
                {
                    totalactive++;
                }
            }

            TextPageHeading(table, $"{Viewer.Catalog.GetString("DISPATCHER INFORMATION : active trains : ")}{totalactive}");

            TableSetCells(table, 0,
                Viewer.Catalog.GetString("Train"),
                Viewer.Catalog.GetString("Travelled"),
                Viewer.Catalog.GetString("Speed"),
                Viewer.Catalog.GetString("Max"),
                Viewer.Catalog.GetString("AI mode"),
                Viewer.Catalog.GetString("AI data"),
                Viewer.Catalog.GetString("Mode"),
                Viewer.Catalog.GetString("Auth"),
                Viewer.Catalog.GetString("Distance"),
                Viewer.Catalog.GetString("Signal"),
                Viewer.Catalog.GetString("Distance"),
                Viewer.Catalog.GetString("Consist"),
                Viewer.Catalog.GetString("Path"));
            TableAddLine(table);

            // first is player train
            foreach (var thisTrain in Viewer.Simulator.Trains)
            {
                if (thisTrain.TrainType == Train.TRAINTYPE.PLAYER || (thisTrain.TrainType == Train.TRAINTYPE.REMOTE && Orts.MultiPlayer.MPManager.IsServer())
                    || thisTrain.IsActualPlayerTrain)
                {
                    var status = thisTrain.GetStatus(Viewer.MilepostUnitsMetric);
                    if (thisTrain.TrainType == Train.TRAINTYPE.AI_PLAYERHOSTING) status = ((AITrain)thisTrain).AddMovementState(status, Viewer.MilepostUnitsMetric);
                    else if (thisTrain == Program.Simulator.OriginalPlayerTrain && Program.Simulator.Activity != null) status = thisTrain.AddRestartTime(status);
                    else if (thisTrain.IsActualPlayerTrain && Program.Simulator.Activity != null && thisTrain.ControlMode != Train.TRAIN_CONTROL.EXPLORER && !thisTrain.IsPathless)
                        status = thisTrain.AddRestartTime(status);
                    for (var iCell = 0; iCell < status.Length; iCell++)
                        TableSetCell(table, table.CurrentRow, iCell, status[iCell]);
                    TableAddLine(table);
                }
            }

            // next is active AI trains which are delayed
            foreach (var thisTrain in Viewer.Simulator.AI.AITrains)
            {
                if (thisTrain.MovementState != AITrain.AI_MOVEMENT_STATE.AI_STATIC && thisTrain.TrainType != Train.TRAINTYPE.PLAYER
                    && thisTrain.TrainType != Train.TRAINTYPE.AI_INCORPORATED)
                {
                    if (thisTrain.Delay.HasValue && thisTrain.Delay.Value.TotalMinutes >= 1)
                    {
                        var status = thisTrain.GetStatus(Viewer.MilepostUnitsMetric);
                        status = thisTrain.AddMovementState(status, Viewer.MilepostUnitsMetric);
                        for (var iCell = 0; iCell < status.Length; iCell++)
                            TableSetCell(table, table.CurrentRow, iCell, status[iCell]);
                        TableAddLine(table);
                    }
                }
            }

            // next is active AI trains which are not delayed
            foreach (var thisTrain in Viewer.Simulator.AI.AITrains)
            {
                if (thisTrain.MovementState != AITrain.AI_MOVEMENT_STATE.AI_STATIC && thisTrain.TrainType != Train.TRAINTYPE.PLAYER
                    && thisTrain.TrainType != Train.TRAINTYPE.AI_INCORPORATED)
                {
                    if (!thisTrain.Delay.HasValue || thisTrain.Delay.Value.TotalMinutes < 1)
                    {
                        var status = thisTrain.GetStatus(Viewer.MilepostUnitsMetric);
                        status = thisTrain.AddMovementState(status, Viewer.MilepostUnitsMetric);
                        for (var iCell = 0; iCell < status.Length; iCell++)
                            TableSetCell(table, table.CurrentRow, iCell, status[iCell]);
                        TableAddLine(table);
                    }
                }
            }

            // finally is static AI trains
            foreach (var thisTrain in Viewer.Simulator.AI.AITrains)
            {
                if (thisTrain.MovementState == AITrain.AI_MOVEMENT_STATE.AI_STATIC && thisTrain.TrainType != Train.TRAINTYPE.PLAYER)
                {
                    var status = thisTrain.GetStatus(Viewer.MilepostUnitsMetric);
                    status = thisTrain.AddMovementState(status, Viewer.MilepostUnitsMetric);
                    for (var iCell = 0; iCell < status.Length; iCell++)
                        TableSetCell(table, table.CurrentRow, iCell, status[iCell]);
                    TableAddLine(table);
                }
            }
#if WITH_PATH_DEBUG
            TextPageHeading(table, "PATH info");

            TableSetCells(table, 0, "Train", "Path ");
            TableSetCells(table, 8, "Type", "Info");
            TableAddLine(table);

            foreach (var thisTrain in Viewer.Simulator.AI.AITrains)
            {
                if (thisTrain.MovementState != AITrain.AI_MOVEMENT_STATE.AI_STATIC)
                {
                    TextPagePathInfo(thisTrain, table);
                }
            }
            TextPageHeading(table, "ACTIONs info");

            TableSetCells(table, 0, "Train", "Actions ");
            TableAddLine(table);

            foreach (var thisTrain in Viewer.Simulator.AI.AITrains)
            {
                if (thisTrain.MovementState != AITrain.AI_MOVEMENT_STATE.AI_STATIC)
                {
                    TextPageActionsInfo(thisTrain, table);
                }
            }
#endif

        }
#if WITH_PATH_DEBUG
        void TextPagePathInfo(AITrain thisTrain, TableData table)
        {
            // next is active AI trains
            if (thisTrain.MovementState != AITrain.AI_MOVEMENT_STATE.AI_STATIC)
            {
                var status = thisTrain.GetPathStatus(Viewer.MilepostUnitsMetric);
                status = thisTrain.AddPathInfo(status, Viewer.MilepostUnitsMetric);
                for (var iCell = 0; iCell < status.Length; iCell++)
                    TableSetCell(table, table.CurrentRow, iCell, status[iCell]);
                TableAddLine(table);
            }
        }

        void TextPageActionsInfo(AITrain thisTrain, TableData table)
        {
            // next is active AI trains
            if (thisTrain.MovementState != AITrain.AI_MOVEMENT_STATE.AI_STATIC)
            {
                var status = thisTrain.GetActionStatus(Viewer.MilepostUnitsMetric);
                for (var iCell = 0; iCell < status.Length; iCell++)
                    TableSetCell(table, table.CurrentRow, iCell, status[iCell]);
                TableAddLine(table);
            }
        }
#endif

        void TextPageWeather(TableData table)
        {
            TextPageHeading(table, Viewer.Catalog.GetString("WEATHER INFORMATION"));

            TableAddLabelValue(table, Viewer.Catalog.GetString("Visibility"), Viewer.Catalog.GetStringFmt("{0:N0} m", Viewer.Simulator.Weather.FogDistance));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Cloud cover"), Viewer.Catalog.GetStringFmt("{0:F0} %", Viewer.Simulator.Weather.OvercastFactor * 100));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Intensity"), Viewer.Catalog.GetStringFmt("{0:F4} p/s/m^2", Viewer.Simulator.Weather.PricipitationIntensityPPSPM2));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Liquidity"), Viewer.Catalog.GetStringFmt("{0:F0} %", Viewer.Simulator.Weather.PrecipitationLiquidity * 100));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Wind"), Viewer.Catalog.GetStringFmt("{0:F1},{1:F1} m/s", Viewer.Simulator.Weather.WindSpeedMpS.X, Viewer.Simulator.Weather.WindSpeedMpS.Y));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Amb Temp"), FormatStrings.FormatTemperature(Viewer.PlayerLocomotive.Train.TrainOutsideTempC, Viewer.PlayerLocomotive.IsMetric, false));
        }

        void TextPageDebugInfo(TableData table)
        {
            TextPageHeading(table, Viewer.Catalog.GetString("DEBUG INFORMATION"));

            var allocatedBytesPerSecond = AllocatedBytesPerSecCounter == null ? 0 : AllocatedBytesPerSecCounter.NextValue();
            if (allocatedBytesPerSecond >= 1 && AllocatedBytesPerSecLastValue != allocatedBytesPerSecond)
                AllocatedBytesPerSecLastValue = allocatedBytesPerSecond;

            TableAddLabelValue(table, Viewer.Catalog.GetString("Logging enabled"), Viewer.Settings.DataLogger ? Viewer.Catalog.GetString("Yes") : Viewer.Catalog.GetString("No"));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Build"), VersionInfo.Build);
            TableAddLabelValue(table, Viewer.Catalog.GetString("Memory"), Viewer.Catalog.GetStringFmt("{0:F0} MB ({5}, {6}, {7}, {8}, {1:F0} MB managed, {9:F0} kB/frame allocated, {2:F0}/{3:F0}/{4:F0} GCs)", GetWorkingSetSize() / 1024 / 1024, GC.GetTotalMemory(false) / 1024 / 1024, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), Viewer.TextureManager.GetStatus(), Viewer.MaterialManager.GetStatus(), Viewer.ShapeManager.GetStatus(), Viewer.World.Terrain.GetStatus(), AllocatedBytesPerSecLastValue / Viewer.RenderProcess.FrameRate.SmoothedValue / 1024));
            TableAddLabelValue(table, Viewer.Catalog.GetString("CPU"), Viewer.Catalog.GetStringFmt("{0:F0}% ({1})", (Viewer.RenderProcess.Profiler.CPU.SmoothedValue + Viewer.UpdaterProcess.Profiler.CPU.SmoothedValue + Viewer.LoaderProcess.Profiler.CPU.SmoothedValue + Viewer.SoundProcess.Profiler.CPU.SmoothedValue) / ProcessorCount, Viewer.Catalog.GetPluralStringFmt("{0} logical processor", "{0} logical processors", ProcessorCount)));
            TableAddLabelValue(table, Viewer.Catalog.GetString("GPU"), Viewer.Catalog.GetStringFmt("{0:F0} FPS (50th/95th/99th percentiles {1:F1} / {2:F1} / {3:F1} ms, shader model {4})", Viewer.RenderProcess.FrameRate.SmoothedValue, Viewer.RenderProcess.FrameTime.SmoothedP50 * 1000, Viewer.RenderProcess.FrameTime.SmoothedP95 * 1000, Viewer.RenderProcess.FrameTime.SmoothedP99 * 1000, Viewer.Settings.ShaderModel));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Adapter"), Viewer.Catalog.GetStringFmt("{0} ({1:F0} MB)", Viewer.AdapterDescription, Viewer.AdapterMemory / 1024 / 1024));
            if (Viewer.Settings.DynamicShadows)
            {
                TableSetCells(table, 3, Enumerable.Range(0, RenderProcess.ShadowMapCount).Select(i => String.Format(Viewer.Catalog.GetStringFmt("{0}/{1}", RenderProcess.ShadowMapDistance[i], RenderProcess.ShadowMapDiameter[i]))).ToArray());
                TableSetCell(table, 3 + RenderProcess.ShadowMapCount, Viewer.Catalog.GetStringFmt("({0}x{0})", Viewer.Settings.ShadowMapResolution));
                TableAddLine(table, Viewer.Catalog.GetString("Shadow maps"));
                TableSetCells(table, 3, Viewer.RenderProcess.ShadowPrimitivePerFrame.Select(p => p.ToString("F0")).ToArray());
                TableAddLabelValue(table, Viewer.Catalog.GetString("Shadow primitives"), Viewer.Catalog.GetStringFmt("{0:F0}", Viewer.RenderProcess.ShadowPrimitivePerFrame.Sum()));
            }
            TableSetCells(table, 3, Viewer.RenderProcess.PrimitivePerFrame.Select(p => p.ToString("F0")).ToArray());
            TableAddLabelValue(table, Viewer.Catalog.GetString("Render primitives"), Viewer.Catalog.GetStringFmt("{0:F0}", Viewer.RenderProcess.PrimitivePerFrame.Sum()));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Render process"), Viewer.Catalog.GetStringFmt("{0:F0}% ({1:F0}% {2})", Viewer.RenderProcess.Profiler.Wall.SmoothedValue, Viewer.RenderProcess.Profiler.Wait.SmoothedValue, Viewer.Catalog.GetString("wait")));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Updater process"), Viewer.Catalog.GetStringFmt("{0:F0}% ({1:F0}% {2})", Viewer.UpdaterProcess.Profiler.Wall.SmoothedValue, Viewer.UpdaterProcess.Profiler.Wait.SmoothedValue, Viewer.Catalog.GetString("wait")));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Loader process"), Viewer.Catalog.GetStringFmt("{0:F0}% ({1:F0}% {2})", Viewer.LoaderProcess.Profiler.Wall.SmoothedValue, Viewer.LoaderProcess.Profiler.Wait.SmoothedValue, Viewer.Catalog.GetString("wait")));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Sound process"), Viewer.Catalog.GetStringFmt("{0:F0}% ({1:F0}% {2})", Viewer.SoundProcess.Profiler.Wall.SmoothedValue, Viewer.SoundProcess.Profiler.Wait.SmoothedValue, Viewer.Catalog.GetString("wait")));
            TableAddLabelValue(table, Viewer.Catalog.GetString("Total process"), Viewer.Catalog.GetStringFmt("{0:F0}% ({1:F0}% {2})", Viewer.RenderProcess.Profiler.Wall.SmoothedValue + Viewer.UpdaterProcess.Profiler.Wall.SmoothedValue + Viewer.LoaderProcess.Profiler.Wall.SmoothedValue + Viewer.SoundProcess.Profiler.Wall.SmoothedValue, Viewer.RenderProcess.Profiler.Wait.SmoothedValue + Viewer.UpdaterProcess.Profiler.Wait.SmoothedValue + Viewer.LoaderProcess.Profiler.Wait.SmoothedValue + Viewer.SoundProcess.Profiler.Wait.SmoothedValue, Viewer.Catalog.GetString("wait")));
            TableSetCells(table, 0, Viewer.Catalog.GetString("Camera"), "", Viewer.Camera.TileX.ToString("F0"), Viewer.Camera.TileZ.ToString("F0"), Viewer.Camera.Location.X.ToString("F2"), Viewer.Camera.Location.Y.ToString("F2"), Viewer.Camera.Location.Z.ToString("F2"), String.Format("{0:F1} {1}", Viewer.Tiles.GetElevation(Viewer.Camera.CameraWorldLocation), FormatStrings.m), Viewer.Settings.LODBias + "%", String.Format("{0} {1}", Viewer.Settings.ViewingDistance, FormatStrings.m), Viewer.Settings.DistantMountains ? String.Format("{0:F0} {1}", (float)Viewer.Settings.DistantMountainsViewingDistance * 1e-3f, FormatStrings.km) : "");
            TableAddLine(table);
        }

        static void TextPageHeading(TableData table, string name)
        {
            TableAddLine(table);
            TableAddLine(table, name);
        }

        #region Native code
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        public class MEMORYSTATUSEX
        {
            public uint Size;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential, Size = 40)]
        struct PROCESS_MEMORY_COUNTERS
        {
            public int Size;
            public int PageFaultCount;
            public int PeakWorkingSetSize;
            public int WorkingSetSize;
            public int QuotaPeakPagedPoolUsage;
            public int QuotaPagedPoolUsage;
            public int QuotaPeakNonPagedPoolUsage;
            public int QuotaNonPagedPoolUsage;
            public int PagefileUsage;
            public int PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, int size);

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(int dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        readonly IntPtr ProcessHandle;
        PROCESS_MEMORY_COUNTERS ProcessMemoryCounters;
        readonly ulong ProcessVirtualAddressLimit;
        #endregion

        public uint GetWorkingSetSize()
        {
            // Get memory usage (working set).
            GetProcessMemoryInfo(ProcessHandle, out ProcessMemoryCounters, ProcessMemoryCounters.Size);
            return (uint)ProcessMemoryCounters.WorkingSetSize;
        }

        public ulong GetVirtualAddressLimit()
        {
            var buffer = new MEMORYSTATUSEX { Size = 64 };
            GlobalMemoryStatusEx(buffer);
            return Math.Min(buffer.TotalVirtual, buffer.TotalPhysical);
        }
    }

    public class HUDGraphSet
    {
        readonly Viewer Viewer;
        readonly Material Material;
        readonly Vector2 Margin = new Vector2(40, 10);
        readonly int Spacing;
        readonly List<Graph> Graphs = new List<Graph>();

        public HUDGraphSet(Viewer viewer, Material material)
        {
            Viewer = viewer;
            Material = material;
            Spacing = Viewer.WindowManager.TextFontSmallOutlined.Height + 2;
        }

        public HUDGraphMesh AddOverlapped(Color color, int height)
        {
            return Add("", "", "", color, height, true);
        }

        public HUDGraphMesh Add(string labelName, string labelMin, string labelMax, Color color, int height)
        {
            return Add(labelName, labelMin, labelMax, color, height, false);
        }

        HUDGraphMesh Add(string labelName, string labelMin, string labelMax, Color color, int height, bool overlapped)
        {
            HUDGraphMesh mesh;
            Graphs.Add(new Graph()
            {
                Mesh = mesh = new HUDGraphMesh(Viewer, color, height),
                LabelName = labelName,
                LabelMin = labelMin,
                LabelMax = labelMax,
                Overlapped = overlapped,
            });
            for (var i = Graphs.Count - 1; i >= 0; i--)
            {
                var previousGraphs = Graphs.Skip(i + 1).Where(g => !g.Overlapped);
                Graphs[i].YOffset = (int)previousGraphs.Sum(g => g.Mesh.GraphPos.W) + Spacing * previousGraphs.Count();
            }
            return mesh;
        }

        public void PrepareFrame(RenderFrame frame)
        {
            var matrix = Matrix.Identity;
            for (var i = 0; i < Graphs.Count; i++)
            {
                Graphs[i].Mesh.GraphPos.X = Viewer.DisplaySize.X - Margin.X - Graphs[i].Mesh.GraphPos.Z;
                Graphs[i].Mesh.GraphPos.Y = Margin.Y + Graphs[i].YOffset;
                frame.AddPrimitive(Material, Graphs[i].Mesh, RenderPrimitiveGroup.Overlay, ref matrix);
            }
        }

        public void Draw(SpriteBatch spriteBatch)
        {
            var box = new Rectangle();
            for (var i = 0; i < Graphs.Count; i++)
            {
                if (!string.IsNullOrEmpty(Graphs[i].LabelName))
                {
                    box.X = (int)Graphs[i].Mesh.GraphPos.X;
                    box.Y = Viewer.DisplaySize.Y - (int)Graphs[i].Mesh.GraphPos.Y - (int)Graphs[i].Mesh.GraphPos.W - Spacing;
                    box.Width = (int)Graphs[i].Mesh.GraphPos.Z;
                    box.Height = Spacing;
                    Viewer.WindowManager.TextFontSmallOutlined.Draw(spriteBatch, box, Point.Zero, Graphs[i].LabelName, LabelAlignment.Right, Color.White);
                    box.X = box.Right + 3;
                    box.Y += Spacing - 3;
                    Viewer.WindowManager.TextFontSmallOutlined.Draw(spriteBatch, box.Location, Graphs[i].LabelMax, Color.White);
                    box.Y += (int)Graphs[i].Mesh.GraphPos.W - Spacing + 7;
                    Viewer.WindowManager.TextFontSmallOutlined.Draw(spriteBatch, box.Location, Graphs[i].LabelMin, Color.White);
                }
            }
        }

        class Graph
        {
            public HUDGraphMesh Mesh;
            public string LabelName;
            public string LabelMin;
            public string LabelMax;
            public int YOffset;
            public bool Overlapped;
        }
    }

    public class HUDGraphMesh : RenderPrimitive
    {
        const int SampleCount = 1024 - 10 - 40; // Widest graphs we can fit in 1024x768.
        const int VerticiesPerSample = 6;
        const int PrimitivesPerSample = 2;
        const int VertexCount = VerticiesPerSample * SampleCount;

        readonly VertexDeclaration VertexDeclaration;
        readonly DynamicVertexBuffer VertexBuffer;
        readonly VertexBuffer BorderVertexBuffer;
        readonly Color Color;

        int SampleIndex;
        VertexPositionColor[] Samples = new VertexPositionColor[VertexCount];

        public Vector4 GraphPos; // xy = xy position, zw = width/height
        public Vector2 Sample; // x = index, y = count

        public HUDGraphMesh(Viewer viewer, Color color, int height)
        {
            VertexDeclaration = new VertexDeclaration(viewer.GraphicsDevice, VertexPositionColor.VertexElements);
            VertexBuffer = new DynamicVertexBuffer(viewer.GraphicsDevice, VertexCount * VertexPositionColor.SizeInBytes, BufferUsage.WriteOnly);
            VertexBuffer.ContentLost += VertexBuffer_ContentLost;
            BorderVertexBuffer = new VertexBuffer(viewer.GraphicsDevice, 10 * VertexPositionColor.SizeInBytes, BufferUsage.WriteOnly);
            var borderOffset = new Vector2(1f / SampleCount, 1f / height);
            var borderColor = new Color(Color.White, 0);
            BorderVertexBuffer.SetData(new[] {
                // Bottom left
                new VertexPositionColor(new Vector3(0 - borderOffset.X, 0 - borderOffset.Y, 1), borderColor),
                new VertexPositionColor(new Vector3(0, 0, 1), borderColor),
                // Bottom right
                new VertexPositionColor(new Vector3(1 + borderOffset.X, 0 - borderOffset.Y, 0), borderColor),
                new VertexPositionColor(new Vector3(1, 0, 0), borderColor),
                // Top right
                new VertexPositionColor(new Vector3(1 + borderOffset.X, 1 + borderOffset.Y, 0), borderColor),
                new VertexPositionColor(new Vector3(1, 1, 0), borderColor),
                // Top left
                new VertexPositionColor(new Vector3(0 - borderOffset.X, 1 + borderOffset.Y, 1), borderColor),
                new VertexPositionColor(new Vector3(0, 1, 1), borderColor),
                // Bottom left
                new VertexPositionColor(new Vector3(0 - borderOffset.X, 0 - borderOffset.Y, 1), borderColor),
                new VertexPositionColor(new Vector3(0, 0, 1), borderColor),
            });
            Color = color;
            Color.A = 255;
            GraphPos.Z = SampleCount;
            GraphPos.W = height;
            Sample.Y = SampleCount;
        }

        void VertexBuffer_ContentLost(object sender, EventArgs e)
        {
            VertexBuffer.SetData(0, Samples, 0, Samples.Length, VertexPositionColor.SizeInBytes, SetDataOptions.NoOverwrite);
        }

        public void AddSample(float value)
        {
            value = MathHelper.Clamp(value, 0, 1);
            var x = Sample.X / Sample.Y;

            Samples[(int)Sample.X * VerticiesPerSample + 0] = new VertexPositionColor(new Vector3(x, value, 0), Color);
            Samples[(int)Sample.X * VerticiesPerSample + 1] = new VertexPositionColor(new Vector3(x, value, 1), Color);
            Samples[(int)Sample.X * VerticiesPerSample + 2] = new VertexPositionColor(new Vector3(x, 0, 1), Color);
            Samples[(int)Sample.X * VerticiesPerSample + 3] = new VertexPositionColor(new Vector3(x, 0, 1), Color);
            Samples[(int)Sample.X * VerticiesPerSample + 4] = new VertexPositionColor(new Vector3(x, value, 0), Color);
            Samples[(int)Sample.X * VerticiesPerSample + 5] = new VertexPositionColor(new Vector3(x, 0, 0), Color);
            VertexBuffer.SetData((int)Sample.X * VerticiesPerSample * VertexPositionColor.SizeInBytes, Samples, (int)Sample.X * VerticiesPerSample, VerticiesPerSample, VertexPositionColor.SizeInBytes, SetDataOptions.NoOverwrite);

            SampleIndex = (SampleIndex + 1) % SampleCount;
            Sample.X = SampleIndex;
        }

        public override void Draw(GraphicsDevice graphicsDevice)
        {
            graphicsDevice.VertexDeclaration = VertexDeclaration;

            // Draw border
            graphicsDevice.Vertices[0].SetSource(BorderVertexBuffer, 0, VertexPositionColor.SizeInBytes);
            graphicsDevice.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 8);

            // Draw graph area (skipping the next value to be written)
            graphicsDevice.Vertices[0].SetSource(VertexBuffer, 0, VertexPositionColor.SizeInBytes);
            if (SampleIndex > 0)
                graphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, SampleIndex * PrimitivesPerSample);
            if (SampleIndex + 1 < SampleCount)
                graphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, (SampleIndex + 1) * VerticiesPerSample, (SampleCount - SampleIndex - 1) * PrimitivesPerSample);
        }
    }

    public class HUDGraphMaterial : Material
    {
        IEnumerator<EffectPass> ShaderPassesGraph;

        public HUDGraphMaterial(Viewer viewer)
            : base(viewer, null)
        {
        }

        public override void SetState(GraphicsDevice graphicsDevice, Material previousMaterial)
        {
            var shader = Viewer.MaterialManager.DebugShader;
            shader.CurrentTechnique = shader.Techniques["Graph"];
            if (ShaderPassesGraph == null) ShaderPassesGraph = shader.Techniques["Graph"].Passes.GetEnumerator();
            shader.ScreenSize = new Vector2(Viewer.DisplaySize.X, Viewer.DisplaySize.Y);

            var rs = graphicsDevice.RenderState;
            rs.CullMode = CullMode.None;
            rs.DepthBufferEnable = false;
        }

        public override void Render(GraphicsDevice graphicsDevice, IEnumerable<RenderItem> renderItems, ref Matrix XNAViewMatrix, ref Matrix XNAProjectionMatrix)
        {
            var shader = Viewer.MaterialManager.DebugShader;

            shader.Begin();
            ShaderPassesGraph.Reset();
            while (ShaderPassesGraph.MoveNext())
            {
                ShaderPassesGraph.Current.Begin();
                foreach (var item in renderItems)
                {
                    var graphMesh = item.RenderPrimitive as HUDGraphMesh;
                    if (graphMesh != null)
                    {
                        shader.GraphPos = graphMesh.GraphPos;
                        shader.GraphSample = graphMesh.Sample;
                        shader.CommitChanges();
                    }
                    item.RenderPrimitive.Draw(graphicsDevice);
                }
                ShaderPassesGraph.Current.End();
            }
            shader.End();
        }

        public override void ResetState(GraphicsDevice graphicsDevice)
        {
            var rs = graphicsDevice.RenderState;
            rs.CullMode = CullMode.CullCounterClockwiseFace;
            rs.DepthBufferEnable = true;
        }
    }
}
