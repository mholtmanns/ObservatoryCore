﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Observatory.Framework;
using Observatory.Framework.Files.Journal;
using Observatory.Framework.Interfaces;

namespace Observatory.Explorer
{
    internal class Explorer
    {
        private IObservatoryCore ObservatoryCore;
        private ObservableCollection<object> Results;
        private ExplorerWorker ExplorerWorker;
        private Dictionary<ulong, Dictionary<int, Scan>> SystemBodyHistory;
        private Dictionary<ulong, Dictionary<int, FSSBodySignals>> BodySignalHistory;
        private Dictionary<ulong, Dictionary<int, ScanBaryCentre>> BarycentreHistory;
        private CustomCriteriaManager CustomCriteriaManager;
        private DateTime CriteriaLastModified;

        internal Explorer(ExplorerWorker explorerWorker, IObservatoryCore core, ObservableCollection<object> results)
        {
            SystemBodyHistory = new();
            BodySignalHistory = new();
            BarycentreHistory = new();
            ExplorerWorker = explorerWorker;
            ObservatoryCore = core;
            Results = results;
            CustomCriteriaManager = new();
            CriteriaLastModified = new DateTime(0);
        }

        public void Clear()
        {
            SystemBodyHistory.Clear();
            BodySignalHistory.Clear();
            BarycentreHistory.Clear();
        }

        public void RecordSignal(FSSBodySignals bodySignals)
        {
            if (!BodySignalHistory.ContainsKey(bodySignals.SystemAddress))
            {
                BodySignalHistory.Add(bodySignals.SystemAddress, new Dictionary<int, FSSBodySignals>());
            }

            if (!BodySignalHistory[bodySignals.SystemAddress].ContainsKey(bodySignals.BodyID))
            {
                BodySignalHistory[bodySignals.SystemAddress].Add(bodySignals.BodyID, bodySignals);
            }
        }

        public void RecordBarycentre(ScanBaryCentre scan)
        {
            if (!BarycentreHistory.ContainsKey(scan.SystemAddress))
            {
                BarycentreHistory.Add(scan.SystemAddress, new Dictionary<int, ScanBaryCentre>());
            }

            if (!BarycentreHistory[scan.SystemAddress].ContainsKey(scan.BodyID))
            {
                BarycentreHistory[scan.SystemAddress].Add(scan.BodyID, scan);
            }
        }

        public Scan ConvertBarycentre(ScanBaryCentre barycentre, Scan childScan)
        {
            string childAffix = childScan.BodyName
                .Replace(childScan.StarSystem, string.Empty);

            char childOrdinal = childAffix.ToCharArray().Last();

            bool lowChild = childScan.BodyID - barycentre.BodyID == 1;

            string baryAffix;

            if (lowChild)
            {
                baryAffix = new string(new char[] { childOrdinal, (char)(childOrdinal + 1) });
            }
            else
            {
                baryAffix = new string(new char[] { (char)(childOrdinal - 1), childOrdinal });
            }

            baryAffix = childAffix.Replace(childOrdinal.ToString(), baryAffix);

            Scan barycentreScan = new()
            {
                Timestamp = barycentre.Timestamp,
                BodyName = barycentre.StarSystem + baryAffix,
                Parents = childScan.Parents.RemoveAt(0),
                PlanetClass = "Barycentre",
                StarSystem = barycentre.StarSystem,
                SystemAddress = barycentre.SystemAddress,
                BodyID = barycentre.BodyID,
                SemiMajorAxis = barycentre.SemiMajorAxis,
                Eccentricity = barycentre.Eccentricity,
                OrbitalInclination = barycentre.OrbitalInclination,
                Periapsis = barycentre.Periapsis,
                OrbitalPeriod = barycentre.OrbitalPeriod,
                AscendingNode = barycentre.AscendingNode,
                MeanAnomaly = barycentre.MeanAnomaly,
                Json = barycentre.Json
            };

            return barycentreScan;
        }

        public void ProcessScan(Scan scanEvent, bool readAll)
        {
            if (!readAll)
            {
                string criteriaFilePath = ((ExplorerSettings)ExplorerWorker.Settings).CustomCriteriaFile;
                
                if (File.Exists(criteriaFilePath))
                {
                    DateTime fileModified = new FileInfo(criteriaFilePath).LastWriteTime;

                    if (fileModified != CriteriaLastModified)
                    {
                        try
                        {
                            CustomCriteriaManager.RefreshCriteria(criteriaFilePath);
                        }
                        catch (CriteriaLoadException e)
                        {
                            var exceptionResult = new ExplorerUIResults()
                            {
                                BodyName = "Error Reading Custom Criteria File",
                                Time = DateTime.Now.ToString("G"),
                                Description = e.Message,
                                Details = e.OriginalScript
                            };
                            ObservatoryCore.AddGridItem(ExplorerWorker, exceptionResult);
                            ((ExplorerSettings)ExplorerWorker.Settings).EnableCustomCriteria = false;
                        }
                        
                        CriteriaLastModified = fileModified;
                    }
                }
            }

            Dictionary<int, Scan> systemBodies;
            if (SystemBodyHistory.ContainsKey(scanEvent.SystemAddress))
            {
                systemBodies = SystemBodyHistory[scanEvent.SystemAddress];
                if (systemBodies.ContainsKey(scanEvent.BodyID))
                {
                    if (scanEvent.SystemAddress != 0)
                    {
                        //We've already checked this object.
                        return;
                    }
                }
            }
            else
            {
                systemBodies = new();
                SystemBodyHistory.Add(scanEvent.SystemAddress, systemBodies);
            }

            if (SystemBodyHistory.Count > 1000)
            {
                foreach (var entry in SystemBodyHistory.Where(entry => entry.Key != scanEvent.SystemAddress).ToList())
                {
                    SystemBodyHistory.Remove(entry.Key);
                }
                SystemBodyHistory.TrimExcess();
            }

            if (scanEvent.SystemAddress != 0 && !systemBodies.ContainsKey(scanEvent.BodyID))
                systemBodies.Add(scanEvent.BodyID, scanEvent);

            var results = DefaultCriteria.CheckInterest(scanEvent, SystemBodyHistory, BodySignalHistory, (ExplorerSettings)ExplorerWorker.Settings);

            if (BarycentreHistory.ContainsKey(scanEvent.SystemAddress) && scanEvent.Parent != null && BarycentreHistory[scanEvent.SystemAddress].ContainsKey(scanEvent.Parent[0].Body))
            {
                ProcessScan(ConvertBarycentre(BarycentreHistory[scanEvent.SystemAddress][scanEvent.Parent[0].Body], scanEvent), readAll);
            }

            if (((ExplorerSettings)ExplorerWorker.Settings).EnableCustomCriteria)
                results.AddRange(CustomCriteriaManager.CheckInterest(scanEvent, SystemBodyHistory, BodySignalHistory, (ExplorerSettings)ExplorerWorker.Settings));

            if (results.Count > 0)
            {
                StringBuilder notificationDetail = new();
                foreach (var result in results)
                {
                    var scanResult = new ExplorerUIResults()
                    {
                        BodyName = result.SystemWide ? scanEvent.StarSystem : scanEvent.BodyName,
                        Time = scanEvent.TimestampDateTime.ToString("G"),
                        Description = result.Description,
                        Details = result.Detail
                    };
                    ObservatoryCore.AddGridItem(ExplorerWorker, scanResult);
                    notificationDetail.AppendLine(result.Description);
                }

                string bodyAffix;

                if (scanEvent.StarSystem != null && scanEvent.BodyName.StartsWith(scanEvent.StarSystem))
                {
                    bodyAffix = scanEvent.BodyName.Replace(scanEvent.StarSystem, string.Empty);
                }
                else
                {
                    bodyAffix = string.Empty;
                }

                string bodyLabel = scanEvent.PlanetClass == "Barycentre" ? "Barycentre" : "Body";

                string spokenAffix;

                if (bodyAffix.Length > 0)
                {
                    if (bodyAffix.Contains("Ring"))
                    {
                        int ringIndex = bodyAffix.Length - 6;
                        spokenAffix =
                            "<say-as interpret-as=\"spell-out\">" + bodyAffix.Substring(0, ringIndex) +
                            "</say-as><break strength=\"weak\"/><say-as interpret-as=\"spell-out\">" +
                            bodyAffix.Substring(ringIndex, 1) + "</say-as>" + bodyAffix.Substring(ringIndex + 1, bodyAffix.Length - (ringIndex + 1));
                    }
                    else
                    {
                        spokenAffix = "<say-as interpret-as=\"spell-out\">" + bodyAffix + "</say-as>";
                    }
                }
                else
                {
                    bodyLabel = "Primary Star";
                    spokenAffix = string.Empty;
                }

                NotificationArgs args = new()
                {
                    Title = bodyLabel + bodyAffix,
                    TitleSsml = $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\"><voice name=\"\">{bodyLabel} {spokenAffix}</voice></speak>",
                    Detail = notificationDetail.ToString()
                };

                ObservatoryCore.SendNotification(args);
            }
        }

    }
}
