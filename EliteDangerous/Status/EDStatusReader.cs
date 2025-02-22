﻿/*
 * Copyright © 2016 - 2021 EDDiscovery development team
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this
 * file except in compliance with the License. You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software distributed under
 * the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF
 * ANY KIND, either express or implied. See the License for the specific language
 * governing permissions and limitations under the License.
 * 
 * EDDiscovery is not affiliated with Frontier Developments plc.
 */

using BaseUtils.JSON;
using System;
using System.Collections.Generic;
using System.IO;

namespace EliteDangerousCore
{
    [System.Diagnostics.DebuggerDisplay("{WatcherFolder}")]
    public class StatusReader
    {
        public string StatusFolder { get; set; }

        private string statusfile;
    
        public StatusReader(string datapath)
        {
            StatusFolder = datapath;
            statusfile = Path.Combine(StatusFolder, "status.json");
        }

        long? prev_flags = null;        // force at least one out here by invalid values
        int prev_guifocus = -1;                 
        int prev_firegroup = -1;
        double prev_curfuel = -1;
        double prev_curres = -1;
        string prev_legalstatus = null;
        int prev_cargo = -1;
        UIEvents.UIPips.Pips prev_pips = new UIEvents.UIPips.Pips();
        UIEvents.UIPosition.Position prev_pos = new UIEvents.UIPosition.Position();     // default is MinValue
        double prev_heading = UIEvents.UIPosition.InvalidValue;    // this forces a pos report
        double prev_jpradius = UIEvents.UIPosition.InvalidValue;    // this forces a pos report

        private enum StatusFlagsShip                        // PURPOSELY PRIVATE - don't want users to get into low level detail of BITS
        {
            Docked = 0, // (on a landing pad)
            Landed = 1, // (on planet surface)
            LandingGear = 2,
            Supercruise = 4,
            FlightAssist = 5,
            HardpointsDeployed = 6,
            InWing = 7,
            CargoScoopDeployed = 9,
            SilentRunning = 10,
            ScoopingFuel = 11,
            FsdMassLocked = 16,
            FsdCharging = 17,
            FsdCooldown = 18,
            OverHeating = 20,
            BeingInterdicted = 23,
            HUDInAnalysisMode = 27,     // 3.3
        }

        private enum StatusFlagsSRV
        {
            SrvHandbrake = 12,
            SrvTurret = 13,
            SrvUnderShip = 14,
            SrvDriveAssist = 15,
        }

        private enum StatusFlagsAll
        {
            ShieldsUp = 3,
            Lights = 8,
            LowFuel = 19,
            HasLatLong = 21,
            IsInDanger = 22,
            NightVision = 28,             // 3.3
        }

        private enum StatusFlagsShipType
        {
            InMainShip = 24,        // -> Degenerates to UIShipType
            InFighter = 25,
            InSRV = 26,
            ShipMask = (1<< InMainShip) | (1<< InFighter) | (1<< InSRV),
        }

        private enum StatusFlagsReportedInOtherEvents       // reported via other mechs than flags 
        {
            AltitudeFromAverageRadius = 29, // 3.4, via position
        }

        string prev_text = null;

        public List<UIEvent> Scan()
        {
          //  System.Diagnostics.Debug.WriteLine(Environment.TickCount % 100000 + "Check " + statusfile);

            if (File.Exists(statusfile))
            {
                JObject jo = null;

                Stream stream = null;
                try
                {
                    stream = File.Open(statusfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                    StreamReader reader = new StreamReader(stream);

                    string text = reader.ReadLine();

                    stream.Close();

                    if (text != null && (prev_text == null || !text.Equals(prev_text)))        // if text not null, and prev text is null OR not equal
                    {
                        jo = JObject.ParseThrowCommaEOL(text);  // and of course the json could be crap
                        prev_text = text;       // set after successful parse
                    }
                }
                catch
                { }
                finally
                {
                    if (stream != null)
                        stream.Dispose();
                }

                if (jo != null)
                {
                    DateTime EventTimeUTC = jo["timestamp"].DateTimeUTC();
                            
                    List<UIEvent> events = new List<UIEvent>();

                    long curflags = jo["Flags"].Long();

                    bool fireoverall = false;
                    bool fireoverallrefresh = prev_guifocus == -1;     //meaning its a refresh

                    if (prev_flags == null || curflags != prev_flags.Value)
                    {
                        if (prev_flags == null)
                            prev_flags = (long)StatusFlagsShipType.ShipMask;      // set an impossible ship type to start the ball rolling

                        UIEvents.UIShipType.Shiptype prevshiptype = ShipType(prev_flags.Value);
                        UIEvents.UIShipType.Shiptype curtype = ShipType(curflags);

                        bool refresh = prevshiptype == UIEvents.UIShipType.Shiptype.None;   // refresh if prev ship was none..

                        if (prevshiptype != curtype)
                        {
                            events.Add(new UIEvents.UIShipType(curtype, EventTimeUTC, refresh));        // CHANGE of ship
                            prev_flags = ~curflags;       // force re-reporting
                            refresh = true;
                        }

                        if (curtype == UIEvents.UIShipType.Shiptype.MainShip)
                            events.AddRange(ReportFlagState(typeof(StatusFlagsShip), curflags, prev_flags.Value, EventTimeUTC, refresh));
                        else if (curtype == UIEvents.UIShipType.Shiptype.SRV)
                            events.AddRange(ReportFlagState(typeof(StatusFlagsSRV), curflags, prev_flags.Value, EventTimeUTC, refresh));

                        if (curtype != UIEvents.UIShipType.Shiptype.None)
                            events.AddRange(ReportFlagState(typeof(StatusFlagsAll), curflags, prev_flags.Value, EventTimeUTC, refresh));

                        prev_flags = curflags;
                        fireoverall = true;
                    }

                    int curguifocus = jo["GuiFocus"].Int();
                    if (curguifocus != prev_guifocus)
                    {
                        events.Add(new UIEvents.UIGUIFocus(curguifocus, EventTimeUTC, prev_guifocus == -1));
                        prev_guifocus = curguifocus;
                        fireoverall = true;
                    }

                    int[] pips = jo["Pips"]?.ToObjectQ<int[]>();

                    if (pips != null)
                    {
                        double sys = pips[0] / 2.0;     // convert to normal, instead of half pips
                        double eng = pips[1] / 2.0;
                        double wep = pips[2] / 2.0;
                        if (sys != prev_pips.Systems || wep != prev_pips.Weapons || eng != prev_pips.Engines)
                        {
                            UIEvents.UIPips.Pips newpips = new UIEvents.UIPips.Pips() { Systems = sys, Engines = eng, Weapons = wep };
                            events.Add(new UIEvents.UIPips(newpips, EventTimeUTC, prev_pips.Engines < 0));
                            prev_pips = newpips;
                            fireoverall = true;
                        }
                    }
                    else if ( prev_pips.Valid )     // missing pips, if we are valid.. need to clear them
                    {
                        UIEvents.UIPips.Pips newpips = new UIEvents.UIPips.Pips();
                        events.Add(new UIEvents.UIPips(newpips, EventTimeUTC, prev_pips.Engines < 0));
                        prev_pips = newpips;
                        fireoverall = true;
                    }

                    int? curfiregroup = jo["FireGroup"].IntNull();      // may appear/disappear.

                    if (curfiregroup != null && curfiregroup != prev_firegroup)
                    {
                        events.Add(new UIEvents.UIFireGroup(curfiregroup.Value + 1, EventTimeUTC, prev_firegroup == -1));
                        prev_firegroup = curfiregroup.Value;
                        fireoverall = true;
                    }

                    JToken jfuel = jo["Fuel"];

                    if (jfuel != null && jfuel.IsObject)        // because they changed its type in 3.3.2
                    {
                        double? curfuel = jfuel["FuelMain"].DoubleNull();
                        double? curres = jfuel["FuelReservoir"].DoubleNull();
                        if (curfuel != null && curres != null)
                        {
                            if (Math.Abs(curfuel.Value - prev_curfuel) >= 0.1 || Math.Abs(curres.Value - prev_curres) >= 0.01)  // don't fire if small changes
                            {
                                //System.Diagnostics.Debug.WriteLine("UIEvent Fuel " + curfuel.Value + " " + prev_curfuel + " Res " + curres.Value + " " + prev_curres);
                                events.Add(new UIEvents.UIFuel(curfuel.Value, curres.Value, ShipType(prev_flags.Value), EventTimeUTC, prev_firegroup == -1));
                                prev_curfuel = curfuel.Value;
                                prev_curres = curres.Value;
                                fireoverall = true;
                            }
                        }
                    }

                    int? curcargo = jo["Cargo"].IntNull();      // may appear/disappear and only introduced for 3.3
                    if (curcargo != null && curcargo.Value != prev_cargo)
                    {
                        events.Add(new UIEvents.UICargo(curcargo.Value, ShipType(prev_flags.Value), EventTimeUTC, prev_firegroup == -1));
                        prev_cargo = curcargo.Value;
                        fireoverall = true;
                    }

                    double jlat = jo["Latitude"].Double(UIEvents.UIPosition.InvalidValue);       // if not there, min value
                    double jlon = jo["Longitude"].Double(UIEvents.UIPosition.InvalidValue);
                    double jalt = jo["Altitude"].Double(UIEvents.UIPosition.InvalidValue);
                    double jheading = jo["Heading"].Double(UIEvents.UIPosition.InvalidValue);
                    double jpradius = jo["PlanetRadius"].Double(UIEvents.UIPosition.InvalidValue);       // 3.4

                    if (jlat != prev_pos.Latitude || jlon != prev_pos.Longitude || jalt != prev_pos.Altitude || jheading != prev_heading || jpradius != prev_jpradius)
                    {
                        UIEvents.UIPosition.Position newpos = new UIEvents.UIPosition.Position()
                        {
                            Latitude = jlat, Longitude = jlon,
                            Altitude = jalt, AltitudeFromAverageRadius = (curflags & (1 << (int)StatusFlagsReportedInOtherEvents.AltitudeFromAverageRadius)) != 0
                        };

                        events.Add(new UIEvents.UIPosition(newpos, jheading, jpradius, EventTimeUTC, prev_pos.ValidPosition == false));
                        prev_pos = newpos;
                        prev_heading = jheading;
                        prev_jpradius = jpradius;
                        fireoverall = true;
                    }

                    string cur_legalstatus = jo["LegalState"].StrNull();

                    if ( cur_legalstatus != prev_legalstatus )
                    {
                        events.Add(new UIEvents.UILegalStatus(cur_legalstatus,EventTimeUTC,prev_legalstatus == null));
                        prev_legalstatus = cur_legalstatus;
                        fireoverall = true;
                    }

                    if ( fireoverall )
                    {
                        List<UITypeEnum> flagsset = ReportFlagState(typeof(StatusFlagsShip), curflags);
                        flagsset.AddRange(ReportFlagState(typeof(StatusFlagsSRV), curflags));
                        flagsset.AddRange(ReportFlagState(typeof(StatusFlagsAll), curflags));

                        events.Add(new UIEvents.UIOverallStatus(ShipType(curflags), flagsset, prev_guifocus, prev_pips, prev_firegroup, 
                                                                prev_curfuel,prev_curres, prev_cargo, prev_pos, prev_heading, prev_jpradius, prev_legalstatus,
                                                                EventTimeUTC, fireoverallrefresh));        // overall list of flags set
                    }

                    return events;
                }
            }

            return new List<UIEvent>();
        }

        List<UITypeEnum> ReportFlagState(Type enumtype, long curflags)
        {
            List<UITypeEnum> flags = new List<UITypeEnum>();
            foreach (string n in Enum.GetNames(enumtype))
            {
                int v = (int)Enum.Parse(enumtype, n);

                bool flag = ((curflags >> v) & 1) != 0;
                if (flag)
                    flags.Add((UITypeEnum)Enum.Parse(typeof(UITypeEnum), n));
            }

            return flags;
        }

        List<UIEvent> ReportFlagState(Type enumtype, long curflags, long prev_flags, DateTime EventTimeUTC, bool refresh)
        {
            List<UIEvent> events = new List<UIEvent>();
            long delta = curflags ^ prev_flags;

            //System.Diagnostics.Debug.WriteLine("Flags changed to {0:x} from {1:x} delta {2:x}", curflags, prev_flags , delta);

            foreach (string n in Enum.GetNames(enumtype))
            {
                int v = (int)Enum.Parse(enumtype, n);

                bool flag = ((curflags >> v) & 1) != 0;

                if (((delta >> v) & 1) != 0)
                {
                    //  System.Diagnostics.Debug.WriteLine("..Flag " + n + " changed to " + flag);
                    events.Add(UIEvent.CreateEvent(n, EventTimeUTC, refresh, flag));
                }
            }

            return events;
        }


        static public UIEvents.UIShipType.Shiptype ShipType(long shiptype)
        {
            shiptype &= (long)StatusFlagsShipType.ShipMask; // isolate flags

            var x = shiptype == 1L << (int)StatusFlagsShipType.InMainShip ? UIEvents.UIShipType.Shiptype.MainShip :
                                shiptype == 1L << (int)StatusFlagsShipType.InSRV ? UIEvents.UIShipType.Shiptype.SRV :
                                shiptype == 1L << (int)StatusFlagsShipType.InFighter ? UIEvents.UIShipType.Shiptype.Fighter :
                                UIEvents.UIShipType.Shiptype.None;
            return x;
        }

    }
}
