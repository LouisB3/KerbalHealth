﻿using System.Collections.Generic;
using System.IO;
using UnityEngine;
using KSP.UI.Screens;

namespace KerbalHealth
{
    /// <summary>
    /// Main class for processing kerbals' health and health changes
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.FLIGHT)]
    public class KerbalHealthScenario : ScenarioModule
    {
        static double lastUpdated;  // UT at last health update
        static double nextEventTime;  // UT when (or after) next event check occurs

        ApplicationLauncherButton appLauncherButton;
        IButton toolbarButton;
        bool dirty = false, crewChanged = false;
        const int colNumMain = 7, colNumDetails = 6;  // # of columns in Health Monitor
        const int colWidth = 100;  // Width of a cell
        const int colSpacing = 10;
        const int gridWidthMain = colNumMain * (colWidth + colSpacing) - colSpacing, gridWidthDetails = colNumDetails * (colWidth + colSpacing) - colSpacing;  // Grid width
        Rect monitorPosition = new Rect(0.5f, 0.5f, gridWidthMain, 200);
        PopupDialog monitorWindow;  // Health Monitor window
        System.Collections.Generic.List<DialogGUIBase> gridContents;  // Health Monitor grid's labels
        KerbalHealthStatus selectedKHS = null;  // Currently selected kerbal for details view, null if list is shown
        int page = 1;  // Current page in the  list of kerbals

        public void Start()
        {
            if (!Core.ModEnabled) return;
            Core.Log("KerbalHealthScenario.Start", Core.LogLevel.Important);
            Core.Log(Core.Factors.Count + " factors initialized.");
            if (!Core.Loaded) Core.LoadConfig();
            Core.KerbalHealthList.RegisterKerbals();
            GameEvents.onCrewOnEva.Add(OnKerbalEva);
            GameEvents.onCrewKilled.Add(OnCrewKilled);
            GameEvents.OnCrewmemberHired.Add(OnCrewmemberHired);
            GameEvents.OnCrewmemberSacked.Add(OnCrewmemberSacked);
            GameEvents.onKerbalAdded.Add(OnKerbalAdded);
            GameEvents.onKerbalRemoved.Add(OnKerbalRemoved);
            //GameEvents.onKerbalStatusChange.Add(OnKerbalStatusChange);
            GameEvents.onKerbalNameChange.Add(OnKerbalNameChange);
            EventData<Part, ProtoCrewMember> dfEvent;
            dfEvent = GameEvents.FindEvent<EventData<Part, ProtoCrewMember>>("onKerbalFrozen");
            if (dfEvent != null) dfEvent.Add(OnKerbalFrozen);
            dfEvent = GameEvents.FindEvent<EventData<Part, ProtoCrewMember>>("onKerbalThaw");
            if (dfEvent != null) dfEvent.Add(OnKerbalThaw);
            if (ToolbarManager.ToolbarAvailable && Core.UseBlizzysToolbar)
            {
                Core.Log("Registering Blizzy's Toolbar button...", Core.LogLevel.Important);
                toolbarButton = ToolbarManager.Instance.add("KerbalHealth", "HealthMonitor");
                toolbarButton.Text = "Kerbal Health Monitor";
                toolbarButton.TexturePath = "KerbalHealth/toolbar";
                toolbarButton.ToolTip = "Kerbal Health";
                toolbarButton.OnClick += (e) => { if (monitorWindow == null) DisplayData(); else UndisplayData(); };
            }
            else
            {
                Core.Log("Registering AppLauncher button...", Core.LogLevel.Important);
                Texture2D icon = new Texture2D(38, 38);
                icon.LoadImage(System.IO.File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "icon.png")));
                appLauncherButton = ApplicationLauncher.Instance.AddModApplication(DisplayData, UndisplayData, null, null, null, null, ApplicationLauncher.AppScenes.ALWAYS, icon);
            }
            lastUpdated = Planetarium.GetUniversalTime();
            nextEventTime = lastUpdated + GetNextEventInterval();
            Core.Log("KerbalHealthScenario.Start finished.");
        }

        /// <summary>
        /// Marks the kerbal as being on EVA, to apply EVA-only effects
        /// </summary>
        /// <param name="action"></param>
        public void OnKerbalEva(GameEvents.FromToAction<Part, Part> action)
        {
            if (!Core.ModEnabled) return;
            Core.Log(action.to.protoModuleCrew[0].name + " went on EVA from " + action.from.name + ".", Core.LogLevel.Important);
            Core.KerbalHealthList.Find(action.to.protoModuleCrew[0]).IsOnEVA = true;
            UpdateKerbals(true);
        }

        public void OnCrewKilled(EventReport er)
        {
            Core.Log("OnCrewKilled(" + er.msg + "/" + er.sender + "/" + er.other + ")");
            Core.KerbalHealthList.Remove(er.sender);
            dirty = crewChanged = true;
        }

        public void OnCrewmemberHired(ProtoCrewMember pcm, int i)
        {
            Core.Log("OnCrewmemberHired(" + pcm.name + ", " + i + ")");
            dirty = crewChanged = true;
        }

        public void OnCrewmemberSacked(ProtoCrewMember pcm, int i)
        {
            Core.Log("OnCrewmemberSacked(" + pcm.name + ", " + i + ")");
            Core.KerbalHealthList.Remove(pcm.name);
            dirty = crewChanged = true;
        }

        public void OnKerbalAdded(ProtoCrewMember pcm)
        {
            Core.Log("OnKerbalAdded('" + pcm.name + "')");
            Core.KerbalHealthList.Add(pcm.name);
            dirty = crewChanged = true;
        }

        public void OnKerbalRemoved(ProtoCrewMember pcm)
        {
            Core.Log("OnKerbalRemoved('" + pcm.name + "')");
            Core.KerbalHealthList.Remove(pcm.name);
            dirty = crewChanged = true;
        }

        //public void OnKerbalStatusChange(ProtoCrewMember pcm, ProtoCrewMember.RosterStatus s1, ProtoCrewMember.RosterStatus s2)
        //{
        //    Core.Log("OnKerbalStatusChange('" + pcm.name + "', '" + s1 + "', '" + s2 + "')");
        //    if (s2 == ProtoCrewMember.RosterStatus.Dead) Core.KerbalHealthList.Remove(pcm.name);
        //    dirty = crewChanged = true;
        //}

        public void OnKerbalNameChange(ProtoCrewMember pcm, string name1, string name2)
        {
            Core.Log("OnKerbalNameChange('" + pcm.name + "', '" + name1 + "', '" + name2 + "')");
            Core.KerbalHealthList.Find(name1).Name = name2;
            dirty = true;
        }

        public void OnKerbalFrozen(Part part, ProtoCrewMember pcm)
        {
            Core.Log("OnKerbalFrozen('" + part.name + "', '" + pcm.name + "')");
            Core.KerbalHealthList.Find(pcm).AddCondition(new KerbalHealth.HealthCondition("Frozen"));
        }

        public void OnKerbalThaw(Part part, ProtoCrewMember pcm)
        {
            Core.Log("OnKerbalThaw('" + part.name + "', '" + pcm.name + "')");
            Core.KerbalHealthList.Find(pcm).RemoveCondition("Frozen");
        }

        /// <summary>
        /// Next event update is scheduled after a random period of time, between 0 and 2 days
        /// </summary>
        /// <returns></returns>
        double GetNextEventInterval()
        { return Core.rand.NextDouble() * KSPUtil.dateTimeFormatter.Day * 2; }

        void UpdateKerbals(bool forced)
        {
            double time = Planetarium.GetUniversalTime();
            double timePassed = time - lastUpdated;
            if (timePassed == 0) return;
            if (forced || ((timePassed >= Core.UpdateInterval) && (timePassed >= Core.MinUpdateInterval * TimeWarp.CurrentRate)))
            {
                Core.Log("UT is " + time + ". Updating for " + timePassed + " seconds.");
                Core.Log("DeepFreeze assembly " + (DFWrapper.AssemblyExists ? "exists" : "does NOT exist") + ".");
                if (!DFWrapper.InstanceExists)
                {
                    Core.Log("Initializing DFWrapper...");
                    DFWrapper.InitDFWrapper();
                    if (DFWrapper.InstanceExists) Core.Log("DFWrapper initialized.");
                    else Core.Log("Could not initialize DFWrapper.", Core.LogLevel.Error);
                }
                Core.KerbalHealthList.Update(timePassed);
                lastUpdated = time;
                if (Core.EventsEnabled)
                    while (time >= nextEventTime)  // Can take several turns of event processing at high time warp
                    {
                        Core.Log("Processing events...");
                        Core.KerbalHealthList.ProcessEvents();
                        nextEventTime += GetNextEventInterval();
                        Core.Log("Next event processing is scheduled at " + KSPUtil.PrintDateCompact(nextEventTime, true), Core.LogLevel.Important);
                    }
                dirty = true;
            }
        }

        public void FixedUpdate()
        { if (Core.ModEnabled) UpdateKerbals(false); }

        int LinesPerPage
        { get { return HighLogic.CurrentGame.Parameters.CustomParams<KerbalHealthGeneralSettings>().LinesPerPage; } }

        bool ShowPages
        { get { return Core.KerbalHealthList.Count > LinesPerPage; } }

        int PageCount
        { get { return (int)System.Math.Ceiling((double)(Core.KerbalHealthList.Count) / LinesPerPage); } }

        int FirstLine
        { get { return (page - 1) * LinesPerPage; } }

        int LineCount
        { get { return System.Math.Min(Core.KerbalHealthList.Count - FirstLine, LinesPerPage); } }

        void FirstPage()
        {
            dirty = page != PageCount;
            page = 1;
            if (!dirty) Invalidate();
        }

        void PageUp()
        {
            dirty = page != PageCount;
            if (page > 1) page--;
            if (!dirty) Invalidate();
        }

        void PageDown()
        {
            if (page < PageCount) page++;
            if (page == PageCount) Invalidate();
            else dirty = true;
        }

        void LastPage()
        {
            page = PageCount;
            Invalidate();
        }

        /// <summary>
        /// Shows Health monitor when the AppLauncher button is enabled
        /// </summary>
        public void DisplayData()
        {
            Core.Log("KerbalHealthScenario.DisplayData", Core.LogLevel.Important);
            UpdateKerbals(true);
            if (selectedKHS == null)
            {
                Core.Log("No kerbal selected, showing overall list.");
                DialogGUILayoutBase layout = new DialogGUIVerticalLayout(true, true);
                if (page > PageCount) page = PageCount;
                if (ShowPages) layout.AddChild(new DialogGUIHorizontalLayout(true, false,
                    new DialogGUIButton("<<", FirstPage, () => (page > 1), true),
                    new DialogGUIButton("<", PageUp, () => (page > 1), false),
                    new DialogGUIHorizontalLayout(TextAnchor.LowerCenter, new DialogGUILabel("Page " + page + "/" + PageCount)),
                    new DialogGUIButton(">", PageDown, () => (page < PageCount), false),
                    new DialogGUIButton(">>", LastPage, () => (page < PageCount), true)));
                gridContents = new List<DialogGUIBase>((Core.KerbalHealthList.Count + 1) * colNumMain);
                // Creating column titles
                gridContents.Add(new DialogGUILabel("Name", true));
                gridContents.Add(new DialogGUILabel("Condition", true));
                gridContents.Add(new DialogGUILabel("Health", true));
                gridContents.Add(new DialogGUILabel("Change/day", true));
                gridContents.Add(new DialogGUILabel("Time Left", true));
                gridContents.Add(new DialogGUILabel("Radiation", true));
                gridContents.Add(new DialogGUILabel("", true));
                // Initializing Health Monitor's grid with empty labels, to be filled in Update()
                for (int i = FirstLine; i < FirstLine + LineCount; i++)
                {
                    for (int j = 0; j < colNumMain - 1; j++)
                        gridContents.Add(new DialogGUILabel("", true));
                    gridContents.Add(new DialogGUIButton<int>("Details", (n) => { selectedKHS = Core.KerbalHealthList[n]; Invalidate(); }, i));
                }
                layout.AddChild(new DialogGUIGridLayout(new RectOffset(0, 0, 0, 0), new Vector2(colWidth, 30), new Vector2(colSpacing, 10), UnityEngine.UI.GridLayoutGroup.Corner.UpperLeft, UnityEngine.UI.GridLayoutGroup.Axis.Horizontal, TextAnchor.MiddleCenter, UnityEngine.UI.GridLayoutGroup.Constraint.FixedColumnCount, colNumMain, gridContents.ToArray()));
                monitorPosition.width = gridWidthMain + 10;
                monitorWindow = PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new MultiOptionDialog("Health Monitor", "", "Health Monitor", HighLogic.UISkin, monitorPosition, layout), false, HighLogic.UISkin, false);
            }
            else
            {
                Core.Log("Showing details for " + selectedKHS.Name + ".");
                gridContents = new List<DialogGUIBase>();
                gridContents.Add(new DialogGUILabel("Name:"));
                gridContents.Add(new DialogGUILabel(""));
                gridContents.Add(new DialogGUILabel("Level:"));
                gridContents.Add(new DialogGUILabel(""));
                gridContents.Add(new DialogGUILabel("Status:"));
                gridContents.Add(new DialogGUILabel(""));
                gridContents.Add(new DialogGUILabel("Max HP:"));
                gridContents.Add(new DialogGUILabel(""));
                gridContents.Add(new DialogGUILabel("HP:"));
                gridContents.Add(new DialogGUILabel(""));
                gridContents.Add(new DialogGUILabel("HP Change:"));
                gridContents.Add(new DialogGUILabel(""));
                if (Core.IsKerbalLoaded(selectedKHS.PCM) && !selectedKHS.HasCondition("Frozen"))
                    foreach (HealthFactor f in Core.Factors)
                    {
                        gridContents.Add(new DialogGUILabel(f.Title + ":"));
                        gridContents.Add(new DialogGUILabel(""));
                    }
                gridContents.Add(new DialogGUILabel("Marginal Bonus:"));
                gridContents.Add(new DialogGUILabel(""));
                gridContents.Add(new DialogGUILabel("Conditions:"));
                gridContents.Add(new DialogGUILabel(""));
                gridContents.Add(new DialogGUILabel("Exposure:"));
                gridContents.Add(new DialogGUILabel(""));
                gridContents.Add(new DialogGUILabel("Radiation:"));
                gridContents.Add(new DialogGUILabel(""));
                gridContents.Add(new DialogGUILabel("Accumulated Dose:"));
                gridContents.Add(new DialogGUILabel(""));
                gridContents.Add(new DialogGUILabel("Radiation HP Loss:"));
                gridContents.Add(new DialogGUILabel(""));
                monitorPosition.width = gridWidthDetails + 10;
                monitorWindow = PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new MultiOptionDialog("Health Monitor", "", "Health Details", HighLogic.UISkin, monitorPosition, new DialogGUIVerticalLayout(new DialogGUIGridLayout(new RectOffset(0, 0, 0, 0), new Vector2(colWidth, 30), new Vector2(colSpacing, 10), UnityEngine.UI.GridLayoutGroup.Corner.UpperLeft, UnityEngine.UI.GridLayoutGroup.Axis.Horizontal, TextAnchor.MiddleCenter, UnityEngine.UI.GridLayoutGroup.Constraint.FixedColumnCount, colNumDetails, gridContents.ToArray()), new DialogGUIButton("Back", () => { selectedKHS = null; Invalidate(); }, gridWidthDetails, 20, false))), false, HighLogic.UISkin, false);
            }
            dirty = true;
        }

        /// <summary>
        /// Hides the Health Monitor window
        /// </summary>
        public void UndisplayData()
        {
            if (monitorWindow != null)
            {
                Vector3 v = monitorWindow.RTrf.position;
                monitorPosition = new Rect(v.x / Screen.width + 0.5f, v.y / Screen.height + 0.5f, gridWidthMain + 20, 50);
                monitorWindow.Dismiss();
            }
        }

        void Invalidate()
        {
            UndisplayData();
            DisplayData();
        }

        public void Update()
        {
            if (!Core.ModEnabled)
            {
                if (monitorWindow != null) monitorWindow.Dismiss();
                return;
            }
            if ((monitorWindow != null) && dirty)
            {
                if (gridContents == null)
                {
                    Core.Log("KerbalHealthScenario.gridContents is null.", Core.LogLevel.Error);
                    return;
                }
                if (selectedKHS == null)
                {
                    if (crewChanged)
                    {
                        Core.KerbalHealthList.RegisterKerbals();
                        if ((page >= PageCount) || (Core.KerbalHealthList.Count == LinesPerPage + 1)) Invalidate();
                        crewChanged = false;
                    }
                    // Fill the Health Monitor's grid with kerbals' health data
                    for (int i = 0; i < LineCount; i++)
                    {
                        KerbalHealthStatus khs = Core.KerbalHealthList[FirstLine + i];
                        bool frozen = khs.HasCondition("Frozen");
                        double ch = khs.LastChangeTotal;
                        gridContents[(i + 1) * colNumMain].SetOptionText(khs.Name);
                        gridContents[(i + 1) * colNumMain + 1].SetOptionText(khs.ConditionString);
                        gridContents[(i + 1) * colNumMain + 2].SetOptionText((100 * khs.Health).ToString("F2") + "% (" + khs.HP.ToString("F2") + ")");
                        gridContents[(i + 1) * colNumMain + 3].SetOptionText((frozen ||(khs.Health >= 1)) ? "—" : (((ch > 0) ? "+" : "") + ch.ToString("F2")));
                        double b = khs.GetBalanceHP();
                        string s = "";
                        if (frozen || (b > khs.NextConditionHP())) s = "—";
                        else s = ((b > 0) ? "> " : "") + Core.ParseUT(khs.TimeToNextCondition());
                        gridContents[(i + 1) * colNumMain + 4].SetOptionText(s);
                        gridContents[(i + 1) * colNumMain + 5].SetOptionText(khs.Dose.ToString("N0") + (khs.Radiation != 0 ? " (+" + khs.Radiation.ToString("N0") + "/day)" : ""));
                    }
                }
                else
                {
                    ProtoCrewMember pcm = selectedKHS.PCM;
                    bool frozen = selectedKHS.HasCondition("Frozen");
                    gridContents[1].SetOptionText(selectedKHS.Name);
                    gridContents[3].SetOptionText(pcm.experienceLevel.ToString());
                    gridContents[5].SetOptionText(pcm.rosterStatus.ToString());
                    gridContents[7].SetOptionText(selectedKHS.MaxHP.ToString("F2"));
                    gridContents[9].SetOptionText(selectedKHS.HP.ToString("F2") + " (" + selectedKHS.Health.ToString("P2") + ")");
                    gridContents[11].SetOptionText(frozen ? "—" : selectedKHS.LastChangeTotal.ToString("F2"));
                    int i = 13;
                    if (Core.IsKerbalLoaded(selectedKHS.PCM) && !frozen)
                        foreach (HealthFactor f in Core.Factors)
                        {
                            gridContents[i].SetOptionText(selectedKHS.Factors.ContainsKey(f.Name) ? selectedKHS.Factors[f.Name].ToString("F2") : "N/A");
                            i += 2;
                        }
                    gridContents[i].SetOptionText(frozen ? "N/A" : selectedKHS.LastMarginalPositiveChange.ToString("F0") + "% (" + selectedKHS.MarginalChange.ToString("F2") + " HP/day)");
                    gridContents[i + 2].SetOptionText(selectedKHS.ConditionString);
                    gridContents[i + 4].SetOptionText(selectedKHS.Exposure.ToString("P2"));
                    gridContents[i + 6].SetOptionText(selectedKHS.Radiation.ToString("N2") + "/day");
                    gridContents[i + 8].SetOptionText(selectedKHS.Dose.ToString("N2"));
                    gridContents[i + 10].SetOptionText((1 - selectedKHS.RadiationMaxHPModifier).ToString("P2"));
                }
                dirty = false;
            }
        }

        public void OnDisable()
        {
            Core.Log("KerbalHealthScenario.OnDisable", Core.LogLevel.Important);
            UndisplayData();

            GameEvents.onCrewOnEva.Remove(OnKerbalEva);
            GameEvents.onCrewKilled.Remove(OnCrewKilled);
            GameEvents.OnCrewmemberHired.Remove(OnCrewmemberHired);
            GameEvents.OnCrewmemberSacked.Remove(OnCrewmemberSacked);
            GameEvents.onKerbalAdded.Remove(OnKerbalAdded);
            GameEvents.onKerbalRemoved.Remove(OnKerbalRemoved);
            //GameEvents.onKerbalStatusChange.Remove(OnKerbalStatusChange);
            GameEvents.onKerbalNameChange.Remove(OnKerbalNameChange);
            EventData<Part, ProtoCrewMember> dfEvent;
            dfEvent = GameEvents.FindEvent<EventData<Part, ProtoCrewMember>>("onKerbalFrozen");
            if (dfEvent != null) dfEvent.Remove(OnKerbalFrozen);
            dfEvent = GameEvents.FindEvent<EventData<Part, ProtoCrewMember>>("onKerbalThaw");
            if (dfEvent != null) dfEvent.Remove(OnKerbalThaw);

            if (toolbarButton != null) toolbarButton.Destroy();
            if ((appLauncherButton != null) && (ApplicationLauncher.Instance != null))
                ApplicationLauncher.Instance.RemoveModApplication(appLauncherButton);
            Core.Log("KerbalHealthScenario.OnDisable finished.", Core.LogLevel.Important);
        }

        public override void OnSave(ConfigNode node)
        {
            if (!Core.ModEnabled) return;
            Core.Log("KerbalHealthScenario.OnSave", Core.LogLevel.Important);
            UpdateKerbals(true);
            int i = 0;
            node.AddValue("nextEventTime", nextEventTime);
            foreach (KerbalHealthStatus khs in Core.KerbalHealthList)
            {
                Core.Log("Saving " + khs.Name + "'s health.");
                node.AddNode(khs.ConfigNode);
                i++;
            }
            Core.Log("KerbalHealthScenario.OnSave complete. " + i + " kerbal(s) saved.", Core.LogLevel.Important);
        }

        public override void OnLoad(ConfigNode node)
        {
            if (!Core.ModEnabled) return;
            Core.Log("KerbalHealthScenario.OnLoad", Core.LogLevel.Important);
            Core.KerbalHealthList.Clear();
            int i = 0;
            nextEventTime = Core.GetDouble(node, "nextEventTime", Planetarium.GetUniversalTime() + GetNextEventInterval());
            foreach (ConfigNode n in node.GetNodes("KerbalHealthStatus"))
            {
                Core.KerbalHealthList.Add(new KerbalHealthStatus(n));
                i++;
            }
            lastUpdated = Planetarium.GetUniversalTime();
            Core.Log("" + i + " kerbal(s) loaded.", Core.LogLevel.Important);
        }
    }
}
