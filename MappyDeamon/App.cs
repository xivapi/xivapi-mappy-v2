using System;
using System.Windows.Forms;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using Sharlayan;
using Sharlayan.Core;
using Sharlayan.Models;
using Sharlayan.Models.ReadResults;
using WebSocketSharp;
using System.Timers;
using Timer = System.Timers.Timer;


namespace MappyDeamon
{
    public partial class App : Form
    {
        public WebSocket WebSocket;
        public ActorItem Player;
        public bool Scanning = true;
        public int WebSocketSubmitSpeed = 50;

        // Timers
        Timer ScannerDelay;
        Timer ScannerLoopTimer;
        Timer ScannerZoneTimeout;

        public App()
        {
            InitializeComponent();
        }

        private void App_Load(object sender, EventArgs e)
        {
            Introduction();

            Status("Initializing Mappy");

            // connect
            Logger("Connecting to the XIVAPI WebSocket ...");
            ConnectToWebSocket();

            // connect to the game
            Logger("Connecting to the Game Process ...");
            Logger("- Note: You must be running the DX11 version of the game.");
            ConnectToGameProcess();

            // start scanning
            Logger("Starting memory scanner ...");
            StartScanningTimerDelayed();
        }

        #region Connect to services + Connect to the game

        /// <summary>
        /// Connect to the XIVAPI Websocket
        /// </summary>
        private void ConnectToWebSocket()
        {
            WebSocket = new WebSocket("wss://xivapi.local/socket");

            // On a message from the browser
            WebSocket.OnMessage += (wsSender, wsEventArgs) => {
                Logger("WS Message: " + wsEventArgs.Data);
            };

            // Connect to the websocket
            WebSocket.Connect();
            Logger("Connection to WS successful.");
        }

        /// <summary>
        /// Connect to the game process
        /// </summary>
        private void ConnectToGameProcess()
        {
            Process[] processes = Process.GetProcessesByName("ffxiv_dx11");

            if (processes.Length > 0) {
                // supported: English, Chinese, Japanese, French, German, Korean
                string gameLanguage = "English";

                // whether to always hit API on start to get the latest sigs based on patchVersion, or use the local json cache (if the file doesn't exist, API will be hit)
                bool useLocalCache = true;

                // patchVersion of game, or latest
                string patchVersion = "latest";
                Process process = processes[0];
                ProcessModel processModel = new ProcessModel {
                    Process = process,
                    IsWin64 = true
                };
                MemoryHandler.Instance.SetProcess(processModel, gameLanguage, patchVersion, useLocalCache);

                Logger("Hooked into the FFXIV process.");
                return;
            }

            Logger("Could not find the FFXIV Game Process, please make sure you're running the game...");
            Logger("Restart the app to try again.");
        }

        #endregion

        #region Memory Scan Timers and Management

        /// <summary>
        /// This delays the start of memory reading, 
        /// just to avoid random issues with the sharlayan lib
        /// </summary>
        private void StartScanningTimerDelayed()
        {
            Scanning = false;

            ScannerDelay = new Timer(1000) {
                Enabled = true
            };

            ScannerDelay.Elapsed += new ElapsedEventHandler(StartScanningTimer);
        }

        /// <summary>
        /// Start the scanning timer
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public uint MapId = 0;
        private void StartScanningTimer(object sender, ElapsedEventArgs e)
        {
            // Enable scanning
            Scanning = true;

            // Stop the start delay
            ScannerDelay.Stop();

            // Notify of player detection
            Player = GetPlayer();
            Logger($"Character Detected: {Player.Name}");
            WebSocketSendPlayerName();

            // Create a scanning timer
            ScannerLoopTimer = new Timer() {
                Enabled = true,
                Interval = WebSocketSubmitSpeed
            };

            ScannerLoopTimer.Elapsed += (scanSender, scanEvent) => {
                // Do nothing if we're not currently scanning...
                if (Scanning == false) {
                    return;
                }

                // Always get the latest information of the player.
                Player = GetPlayer();

                // If the player has moved map, we need send this ping
                // to XIVAPI so it can update the view for the user.
                if (Player.MapID > 0 && Player.MapID != MapId) {
                    Logger("");
                    Logger($"[Zone change detected :: {MapId} --> {Player.MapID} :: Scanning paused]");
                    Status("Zoning");

                    // Track the new map id
                    MapId = Player.MapID;

                    // Start a zoning timeout
                    PauseScanningUntilZoneCountdownComplete();

                    // send map id
                    WebSocketSendMapId();

                    // if new map id is 0, inform user nothing will scan
                    if (Player.MapID == 0) {
                        Logger($"[Error: Zone Map ID read as 0, cannot scan this map. Scanning will resume when you zone to a non 0 ID map]");
                        Status("Scanning Ignored due to Map ID = 0");
                    }
                } else {
                    if (Player.MapID == 0) {
                        return;
                    }

                    // send player position
                    WebSocketSendPlayerPosition();

                    //Logger($"-- Memory Map Id: {Player.MapID} - Tracked: {MapId} --");
                }
            };

            ScannerLoopTimer.Start();
        }

        /// <summary>
        /// Start countdown when zoning, give memory time to init
        /// </summary>
        public int ZoneCountdownInterval = 3;
        public void PauseScanningUntilZoneCountdownComplete()
        {
            Scanning = false;

            ScannerZoneTimeout = new Timer(1000) {
                Enabled = true
            };

            ScannerZoneTimeout.Elapsed += (scanSender, scanEvent) => {
                // Update player
                Player = GetPlayer();

                // check countdown
                if (ZoneCountdownInterval == 0) {
                    Logger($"[Zone timeout complete :: Scanning Map ID: {Player.MapID}]");
                    Status("Scanning Memory");

                    // reset
                    ZoneCountdownInterval = 3;
                    ScannerZoneTimeout.Stop();
                    Scanning = true;
                } else {
                    Logger($"[Scanning paused for: {ZoneCountdownInterval} seconds...]");
                    ZoneCountdownInterval--;
                }
            };

            ScannerZoneTimeout.Start();
        }

        #endregion

        #region Game Memory methods

        /// <summary>
        /// Get the current player actor
        /// </summary>
        /// <returns></returns>
        public ActorItem GetPlayer()
        {
            Reader.GetActors();
            return ActorItem.CurrentUser;
        }

        /// <summary>
        /// Get the current target actor
        /// </summary>
        /// <returns></returns>
        public ActorItem GetCurrentTarget()
        {
            try {
                TargetResult readResult = Reader.GetTargetInfo();
                return readResult.TargetInfo.CurrentTarget;
            } catch (Exception ex) {
                LoggerException(ex, "GameMemory -> GetCurrentTarget");
            }

            return GetPlayer();
        }

        /// <summary>
        /// Get all monsters around the player in memory
        /// </summary>
        /// <returns></returns>
        public List<ActorItem> GetMonstersAroundPlayer()
        {
            ActorResult readResult = Reader.GetActors();
            return readResult.CurrentMonsters.Select(e => e.Value).ToList();
        }

        /// <summary>
        /// Get all npcs around the player in memory
        /// </summary>
        /// <returns></returns>
        public List<ActorItem> GetNpcsAroundPlayer()
        {
            ActorResult readResult = Reader.GetActors();
            return readResult.CurrentNPCs.Select(e => e.Value).ToList();
        }

        #endregion

        #region Logging

        /// <summary>
        /// Print the introduction text to the log
        /// </summary>
        private void Introduction()
        {
            Logger("================================================");
            Logger("FINAL FANTASY XIV MAPPY");
            Logger("Built by: Vekien");
            Logger("Discord: https://discord.gg/MFFVHWC");
            Logger("Source: https://github.com/xivapi/xivapi-mappy");
            Logger("================================================");
        }

        /// <summary>
        /// Set the status text
        /// </summary>
        /// <param name="text"></param>
        private void Status(string text)
        {
            StatusLabel.Text = text;
        }

        /// <summary>
        /// Write something to the log
        /// </summary>
        /// <param name="text"></param>
        private void Logger(string text)
        {
            DateTime now = DateTime.Now;
            string datetime = now.ToString("hh:mm:ss tt");

            logger.AppendText(
                String.Format("[{0}] {1}", datetime, text) + Environment.NewLine
            );
        }

        /// <summary>
        /// Write an exception to the log
        /// </summary>
        /// <param name="ex"></param>
        /// <param name="message"></param>
        private void LoggerException(Exception ex, string message)
        {
            var LineNumber = new StackTrace(ex, true).GetFrame(0).GetFileLineNumber();

            Logger("---[ EXCEPTION ]------------------------------------------------");
            Logger($"{LineNumber} :: {message}");
            Logger(ex.ToString());
            Logger("----------------------------------------------------------------");
        }

        #endregion

        #region WebSocket API logic

        public void WebSocketSendPlayerName()
        {
            WebSocket.Send($"PLAYER_NAME::{Player.Name}");
        }

        public void WebSocketSendMapId()
        {
            WebSocket.Send($"PLAYER_MAP_ID::{Player.MapID}");
        }

        public void WebSocketSendPlayerPosition()
        {
            double direction = Math.Abs(Player.Heading * (180 / Math.PI) - 180);
            WebSocket.Send($"PLAYER_POSITION::{Player.Coordinate.X},{Player.Coordinate.Y},{Player.Coordinate.Z},{direction}");
        }

        #endregion
    }
}
