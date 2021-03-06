﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

/**********************************************************
* USING NAMESPACES
**********************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /******************************************************** 
    * CLASS DEFINITIONS
    *********************************************************/
    /// <summary>
    /// Historical datafeed stream reader for processing files on a local disk.
    /// </summary>
    /// <remarks>Filesystem datafeeds are incredibly fast</remarks>
    public class FileSystemDataFeed : IDataFeed
    {
        /******************************************************** 
        * CLASS VARIABLES
        *********************************************************/
        // Set types in public area to speed up:
        private IAlgorithm _algorithm;
        private BacktestNodePacket _job;
        private bool _endOfStreams = false;
        private int _subscriptions = 0;
        private int _bridgeMax = 500000;
        private bool _exitTriggered = false;

        /******************************************************** 
        * CLASS PROPERTIES
        *********************************************************/
        /// <summary>
        /// List of the subscription the algorithm has requested. Subscriptions contain the type, sourcing information and manage the enumeration of data.
        /// </summary>
        public List<SubscriptionDataConfig> Subscriptions { get; private set; }


        public List<decimal> RealtimePrices { get; private set; } 

        /// <summary>
        /// Cross-threading queues so the datafeed pushes data into the queue and the primary algorithm thread reads it out.
        /// </summary>
        public ConcurrentQueue<List<BaseData>>[] Bridge { get; set; }

        /// <summary>
        /// Stream created from the configuration settings.
        /// </summary>
        public SubscriptionDataReader[] SubscriptionReaderManagers { get; set; }

        /// <summary>
        /// Set the source of the data we're requesting for the type-readers to know where to get data from.
        /// </summary>
        /// <remarks>Live or Backtesting Datafeed</remarks>
        public DataFeedEndpoint DataFeed { get; set; }

        /// <summary>
        /// Flag indicating the hander thread is completely finished and ready to dispose.
        /// </summary>
        public bool IsActive { get; private set; }

        /// <summary>
        /// Flag indicating the file system has loaded all files.
        /// </summary>
        public bool LoadingComplete { get; private set; }

        /// <summary>
        /// Furthest point in time that the data has loaded into the bridges.
        /// </summary>
        public DateTime LoadedDataFrontier { get; private set; }

        /// <summary>
        /// Signifying no more data across all bridges
        /// </summary>
        public bool EndOfBridges 
        {
            get 
            {
                for (var i = 0; i < Bridge.Length; i++)
                {
                    if (Bridge[i].Count != 0 || EndOfBridge[i] != true || _endOfStreams != true)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// End of Stream for Each Bridge:
        /// </summary>
        public bool[] EndOfBridge { get; set; }

        /// <summary>
        /// Frontiers for each fill forward high water mark
        /// </summary>
        public DateTime[] FillForwardFrontiers;

        /******************************************************** 
        * CLASS CONSTRUCTOR
        *********************************************************/
        /// <summary>
        /// Create a new backtesting data feed.
        /// </summary>
        /// <param name="algorithm">Instance of the algorithm</param>
        /// <param name="job">Algorithm work task</param>
        public FileSystemDataFeed(IAlgorithm algorithm, BacktestNodePacket job)
        {
            Subscriptions = algorithm.SubscriptionManager.Subscriptions;
            _subscriptions = Subscriptions.Count;

            //Public Properties:
            DataFeed = DataFeedEndpoint.FileSystem;
            IsActive = true;
            Bridge = new ConcurrentQueue<List<BaseData>>[_subscriptions];
            EndOfBridge = new bool[_subscriptions];
            SubscriptionReaderManagers = new SubscriptionDataReader[_subscriptions];
            FillForwardFrontiers = new DateTime[_subscriptions];
            RealtimePrices = new List<decimal>(_subscriptions);

            //Class Privates:
            _job = job;
            _algorithm = algorithm;
            _endOfStreams = false;
            _bridgeMax = _bridgeMax / _subscriptions; //Set the bridge maximum count:
        }

        /******************************************************** 
        * CLASS METHODS
        *********************************************************/
        /// <summary>
        /// Initialize activators to invoke types in the algorithm
        /// </summary>
        private void ResetActivators()
        {
            for (var i = 0; i < _subscriptions; i++)
            {
                //Create a new instance in the dictionary:
                Bridge[i] = new ConcurrentQueue<List<BaseData>>();
                EndOfBridge[i] = false;
                SubscriptionReaderManagers[i] = new SubscriptionDataReader(Subscriptions[i], _algorithm.Securities[Subscriptions[i].Symbol], DataFeed, _job.PeriodStart, _job.PeriodFinish);
                FillForwardFrontiers[i] = new DateTime();
            }
        }


        /// <summary>
        /// Get the number of active streams still EndOfBridge array.
        /// </summary>
        /// <returns>Count of the number of streams with data</returns>
        private int GetActiveStreams() 
        {
            //Get the number of active streams:
            var activeStreams = (from stream in EndOfBridge
                                 where stream == false
                                 select stream).Count();
            return activeStreams;
        }


        /// <summary>
        /// Calculate the minimum increment to scan for data based on the data requested.
        /// </summary>
        /// <param name="includeTick">When true the subscriptions include a tick data source, meaning there is almost no increment.</param>
        /// <returns>Timespan to jump the data source so it efficiently orders the results</returns>
        private TimeSpan CalculateIncrement(bool includeTick) 
        {
            var increment = TimeSpan.FromDays(1);
            foreach (var config in Subscriptions) 
            {
                switch (config.Resolution)
                { 
                    //Hourly TradeBars:
                    case Resolution.Hour:
                        if (increment > TimeSpan.FromHours(1)) 
                        {
                            increment = TimeSpan.FromHours(1);
                        }
                        break;

                    //Minutely TradeBars:
                    case Resolution.Minute:
                        if (increment > TimeSpan.FromMinutes(1))
                        {
                            increment = TimeSpan.FromMinutes(1);
                        }
                        break;

                    //Secondly Bars:
                    case Resolution.Second:
                        if (increment > TimeSpan.FromSeconds(1)) 
                        {
                            increment = TimeSpan.FromSeconds(1);
                        }
                        break;

                    //Ticks: No increment; just fire each data piece in as they happen.
                    case Resolution.Tick:
                        if (increment > TimeSpan.FromMilliseconds(1) && includeTick)
                        {
                            increment = new TimeSpan(0, 0, 0, 0, 1);
                        }
                        break;
                }
            }
            return increment;
        }


        
        /// <summary>
        /// Main routine for datafeed analysis.
        /// </summary>
        /// <remarks>This is a hot-thread and should be kept extremely lean. Modify with caution.</remarks>
        public void Run()
        {
            //Initialize Parameters:
            long earlyBirdTicks = 0;
            var subscriptions = SubscriptionReaderManagers.Length;
            // this is really the next frontier in the future
            var frontier = new DateTime();
            var tradeBarIncrements = TimeSpan.FromDays(1);
            var increment = TimeSpan.FromDays(1);
            var activeStreams = subscriptions;

            //Initialize Activators:
            ResetActivators();

            //Calculate the increment based on the subscriptions:
            tradeBarIncrements = CalculateIncrement(includeTick: false);     //TradeBars get fillforward, larger increments.
            increment = CalculateIncrement(includeTick: true);               //Include ticks for small increment for frontier calcs.

            //Loop over each date in the job
            foreach (var date in Time.EachTradeableDay(_algorithm.Securities, _job.PeriodStart, _job.PeriodFinish))
            {
                //Update the source-URL from the BaseData, reset the frontier to today. Update the source URL once per day.
                frontier = date.Add(increment); //Frontier is in the future and looks back to all the data produced so far.
                activeStreams = subscriptions;
                //Log.Debug("FileSystemDataFeed.Run(): Date Changed: " + date.ToShortDateString());

                //Initialize the feeds to this date:
                for (var i = 0; i < subscriptions; i++) 
                {
                    //Don't refresh source when we know the market is closed for this security:
                    //if (SubscriptionReaderManagers[i].MarketOpen(date)) { }
                    var success = SubscriptionReaderManagers[i].RefreshSource(date);

                    //If we know the market is closed for security then can declare bridge closed.
                    if (success) {
                        EndOfBridge[i] = false;
                    } else {
                        EndOfBridge[i] = true;
                    }
                }

                //Pause the DataFeed
                var bridgeFullCount = 1;
                var bridgeZeroCount = 0;
                var active = GetActiveStreams();
                //Log.Debug("FileSystemDataFeed.Run(): Active Streams: " + active);

                //Pause here while bridges are full and the 
                while (bridgeFullCount > 0 && ((subscriptions - active) == bridgeZeroCount) && !_exitTriggered) 
                {
                    bridgeFullCount = (from bridge in Bridge where bridge.Count >= _bridgeMax select bridge).Count();
                    bridgeZeroCount = (from bridge in Bridge where bridge.Count == 0 select bridge).Count();
                    Thread.Sleep(5);
                }

                if (_exitTriggered) break;

                // for each smallest resolution
                while ((frontier.Date == date.Date || frontier == date.Date.AddDays(1)) && !_exitTriggered)
                {
                    var cache = new List<BaseData>[subscriptions];
                    
                    //Reset Loop:
                    earlyBirdTicks = 0;

                    //Go over all the subscriptions, one by one add a minute of data to the bridge.
                    for (var i = 0; i < subscriptions; i++)
                    {
                        //Get the reader manager:
                        var manager = SubscriptionReaderManagers[i];

                        //End of the manager stream set flag to end bridge: also if the EOB flag set, from the refresh source method above
                        if (manager.EndOfStream || EndOfBridge[i])
                        {
                            EndOfBridge[i] = true;
                            activeStreams = GetActiveStreams();
                            if (activeStreams == 0)
                            {
                                frontier = frontier.Date + TimeSpan.FromDays(1);
                                //if (frontier.ToString("yyyy-MMM-dd") == "2013-May-03") System.Diagnostics.Debugger.Break();
                                //Log.Debug("FileSystemDataFeed.Run(): No Active Streams; moving to next day.");
                            }
                            continue;
                        }

                        //Initialize data store:
                        cache[i] = new List<BaseData>();

                        //Add the last iteration to the new list: only if it falls into this time category
                        //Log.Debug("FileSystemDataFeed.Run(): Entering cache builder: current: " + manager.Current.Time.ToLongTimeString());
                        while (manager.Current.Time < frontier) //.RoundUp(increment)
                        {
                            cache[i].Add(manager.Current);
                            //Log.Debug("FileSystemDataFeed.Run(): Adding feed.Current to cache: frontier: " +  frontier.ToLongTimeString());
                            if (!manager.MoveNext()) break;
                        }

                        //Save the next earliest time from the bridges: only if we're not filling forward.
                        if (manager.Current != null)
                        {
                            if (earlyBirdTicks == 0 || manager.Current.Time.Ticks < earlyBirdTicks)
                            {
                                earlyBirdTicks = manager.Current.Time.Ticks;
                            }
                        }
                    }

                    if (activeStreams == 0)
                    {
                        break;
                    }


                    //Add all the lists to the bridge, release the bridge
                    //we push all the data up to this frontier into the bridge at once
                    for (var i = 0; i < subscriptions; i++)
                    {
                        if (cache[i] != null && cache[i].Count > 0)
                        {
                            FillForwardFrontiers[i] = cache[i][0].Time;
                            Bridge[i].Enqueue(cache[i]);
                        }
                        ProcessFillForward(SubscriptionReaderManagers[i], i, tradeBarIncrements);
                    }

                    //This will let consumers know we have loaded data up to this date
                    //So that the data stream doesn't pull off data from the same time period in different events
                    LoadedDataFrontier = frontier;

                    if (earlyBirdTicks > 0 && earlyBirdTicks > frontier.Ticks) {
                        //Jump increment to the nearest second, in the future: Round down, add increment
                        frontier = (new DateTime(earlyBirdTicks)).RoundDown(increment) + increment;
                    }
                    else 
                    {
                        //Otherwise step one forward.
                        frontier += increment;  
                    }

                } // End of This Day.

                if (_exitTriggered) break;

            } // End of All Days:

            Log.Trace(DataFeed + ".Run(): Data Feed Completed.");
            LoadingComplete = true;

            //Make sure all bridges empty before declaring "end of bridge":
            while (!EndOfBridges && !_exitTriggered)
            {
                for (var i = 0; i < subscriptions; i++)
                {
                    if (Bridge[i].Count == 0 && SubscriptionReaderManagers[i].EndOfStream)
                    {
                        EndOfBridge[i] = true;
                    }
                    //Log.Trace("FileSystemDataFeed.Run(): Bridge: " + i + " Count: " + Bridge[i].Count + " EOS: " + SubscriptionReaderManagers[i].EndOfStream.ToString() + " EOB:" + EndOfBridge[i].ToString());
                }
                if (GetActiveStreams() == 0) _endOfStreams = true;
                Thread.Sleep(100);
            }

            //Close up all streams:
            for (var i = 0; i < Subscriptions.Count; i++)
            {
                SubscriptionReaderManagers[i].Dispose();
            }

            Log.Trace(DataFeed + ".Run(): Ending Thread... ");
            IsActive = false;
        }


        /// <summary>
        /// If this is a fillforward subscription, look at the previous time, and current time, and add new 
        /// objects to queue until current time to fill up the gaps.
        /// </summary>
        /// <param name="manager">Subscription to process</param>
        /// <param name="i">Subscription position in the bridge ( which queue are we pushing data to )</param>
        /// <param name="increment">Timespan increment to jump the fillforward results</param>
        void ProcessFillForward(SubscriptionDataReader manager, int i, TimeSpan increment)
        {
            // If previous == null cannot fill forward nothing there to move forward (e.g. cases where file not found on first file).
            if (!Subscriptions[i].FillDataForward || manager.Previous == null) return;

            //Last tradebar and the current one we're about to add to queue:
            var previous = manager.Previous;
            var current = manager.Current;

            //Initialize the frontier:
            if (FillForwardFrontiers[i].Ticks == 0) FillForwardFrontiers[i] = previous.Time;

            //Data ended before the market closed: premature ending flag - continue filling forward until market close.
            if (manager.EndOfStream && manager.MarketOpen(current.Time))
            { 
                //Premature end of stream: fill manually until market closed.
                for (var date = FillForwardFrontiers[i] + increment; manager.MarketOpen(date); date = date + increment)
                {
                    var cache = new List<BaseData>(1);
                    var fillforward = current.Clone(true);
                    fillforward.Time = date;
                    FillForwardFrontiers[i] = date;
                    cache.Add(fillforward);
                    Bridge[i].Enqueue(cache);
                }
                return;
            }

            //Once per increment, add a new cache to the Bridge: 
            //If the current.Time is before market close, (e.g. suspended trading at 2pm) the date is always greater than currentTime and fillforward never runs.
            //In this circumstance we need to keep looping till market/extended hours close even if no data. 
            for (var date = FillForwardFrontiers[i] + increment; (date < current.Time); date = date + increment)
            {
                //If we don't want aftermarket data, rewind it backwards until the market closes.
                if (!Subscriptions[i].ExtendedMarketHours) 
                {
                    if (!manager.MarketOpen(date)) 
                    {
                        // Move fill forward so we don't waste time in this tight loop.
                        //Question is where to shuffle the date? 
                        // --> If BEFORE market open, shuffle forward.
                        // --> If AFTER market close, and current.Time after market close, quit loop.
                        date = current.Time;
                        do 
                        {
                            date = date - increment;
                        } while (manager.MarketOpen(date));
                        continue;
                    }
                } 
                else  
                { 
                    //If we've asked for extended hours, and the security is no longer inside extended market hours, skip:
                    if (!manager.ExtendedMarketOpen(date))
                    {
                        continue;
                    }
                }

                var cache = new List<BaseData>(1);
                var fillforward = previous.Clone(true);
                fillforward.Time = date;
                FillForwardFrontiers[i] = date;
                cache.Add(fillforward);
                Bridge[i].Enqueue(cache);
            }
        }


        /// <summary>
        /// Send an exit signal to the thread.
        /// </summary>
        public void Exit()
        {
            _exitTriggered = true;
            PurgeData();
        }


        /// <summary>
        /// Loop over all the queues and clear them to fast-quit this thread and return to main.
        /// </summary>
        public void PurgeData()
        {
            foreach (var t in Bridge)
            {
                t.Clear();
            }
        }

    } // End FileSystem Local Feed Class:
} // End Namespace
