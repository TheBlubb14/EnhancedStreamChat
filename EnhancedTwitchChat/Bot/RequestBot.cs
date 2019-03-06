﻿//#define PRIVATE 

using CustomUI.BeatSaber;
using EnhancedTwitchChat.Chat;
using EnhancedTwitchChat.Textures;
using EnhancedTwitchChat.UI;
using EnhancedTwitchChat.Utils;
using HMUI;
using SimpleJSON;
using SongBrowserPlugin;
using SongLoaderPlugin;
using SongLoaderPlugin.OverrideClasses;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;
using VRUI;
using Image = UnityEngine.UI.Image;
using Toggle = UnityEngine.UI.Toggle;

namespace EnhancedTwitchChat.Bot
{
    public partial class RequestBot : MonoBehaviour
    {
        [Flags]
        public enum RequestStatus
        {
            Invalid,
            Queued,
            Blacklisted,
            Skipped,
            Played
        }

        private static readonly Regex _digitRegex = new Regex("^[0-9]+$", RegexOptions.Compiled);
        private static readonly Regex _beatSaverRegex = new Regex("^[0-9]+-[0-9]+$", RegexOptions.Compiled);
        private static readonly Regex _alphaNumericRegex = new Regex("^[0-9A-Za-z]+$", RegexOptions.Compiled);
        private static readonly Regex _RemapRegex = new Regex("^[0-9]+,[0-9]+$", RegexOptions.Compiled);


        public static RequestBot Instance;
        public static ConcurrentQueue<RequestInfo> UnverifiedRequestQueue = new ConcurrentQueue<RequestInfo>();
        public static ConcurrentQueue<KeyValuePair<SongRequest, bool>> BlacklistQueue = new ConcurrentQueue<KeyValuePair<SongRequest, bool>>();
        public static Dictionary<string, RequestUserTracker> RequestTracker = new Dictionary<string, RequestUserTracker>();

        private static Button _requestButton;
        private static bool _refreshQueue = false;

        private static FlowCoordinator _levelSelectionFlowCoordinator;
        private static DismissableNavigationController _levelSelectionNavigationController;
        private static Queue<string> _botMessageQueue = new Queue<string>();
        //private static Dictionary<string, Action<TwitchUser, string>> Commands = new Dictionary<string, Action<TwitchUser, string>>();

        private static Dictionary<string, BOTCOMMAND> NewCommands = new Dictionary<string, BOTCOMMAND>(); // This will replace command dictionary

        static public bool QueueOpen = false;
        bool mapperwhiteliston = false;
        bool mapperblackliston = false;

        private static System.Random generator = new System.Random();

        public static List<JSONObject> played = new List<JSONObject>(); // Played list

        private static StringListManager mapperwhitelist = new StringListManager(); // Lists because we need to interate them per song
        private static StringListManager mapperblacklist = new StringListManager(); // Lists because we need to interate them per song

        private static HashSet<string> duplicatelist = new HashSet<string>();
        private static Dictionary<string, string> songremap = new Dictionary<string, string>();
        private static Dictionary<string, string> deck = new Dictionary<string, string>(); // deck name/content

        public static string datapath;

        private static CustomMenu _songRequestMenu = null;
        private static RequestBotListViewController _songRequestListViewController = null;


        public static void OnLoad()
        {
            _levelSelectionFlowCoordinator = Resources.FindObjectsOfTypeAll<SoloFreePlayFlowCoordinator>().First();
            if (_levelSelectionFlowCoordinator)
                _levelSelectionNavigationController = _levelSelectionFlowCoordinator.GetPrivateField<DismissableNavigationController>("_navigationController");

            if (_levelSelectionNavigationController)
            {
                _requestButton = BeatSaberUI.CreateUIButton(_levelSelectionNavigationController.rectTransform, "QuitButton", new Vector2(60f, 36.8f),
                    new Vector2(15.0f, 5.5f), () => { _requestButton.interactable = false; _songRequestMenu.Present(); _requestButton.interactable = true; }, "Song Requests");

                _requestButton.gameObject.GetComponentInChildren<TextMeshProUGUI>().enableWordWrapping = false;
                _requestButton.SetButtonTextSize(2.0f);
                BeatSaberUI.AddHintText(_requestButton.transform as RectTransform, $"{(!Config.Instance.SongRequestBot ? "To enable the song request bot, look in the Enhanced Twitch Chat settings menu." : "Manage the current request queue")}");
                Plugin.Log("Created request button!");
            }

            if (_songRequestListViewController == null)
                _songRequestListViewController = BeatSaberUI.CreateViewController<RequestBotListViewController>();

            if (_songRequestMenu == null)
            {
                _songRequestMenu = BeatSaberUI.CreateCustomMenu<CustomMenu>("Song Request Queue");
                _songRequestMenu.SetMainViewController(_songRequestListViewController, true);
            }

            SongListUtils.Initialize();

            datapath = Path.Combine(Environment.CurrentDirectory, "UserData", "EnhancedTwitchChat");
            if (!Directory.Exists(datapath))
                Directory.CreateDirectory(datapath);

            WriteQueueSummaryToFile();
            WriteQueueStatusToFile(QueueOpen ? "Queue is open" : "Queue is closed");


            if (Instance) return;
            new GameObject("EnhancedTwitchChatRequestBot").AddComponent<RequestBot>();
        }
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Instance = this;

            string filesToDelete = Path.Combine(Environment.CurrentDirectory, "FilesToDelete");
            if (Directory.Exists(filesToDelete))
                Utilities.EmptyDirectory(filesToDelete);

            RequestQueue.Read();
            RequestHistory.Read();
            SongBlacklist.Read();

            UpdateRequestButton();
            InitializeCommands();

            StartCoroutine(ProcessRequestQueue());
            StartCoroutine(ProcessBlacklistRequests());
        }

        private void FixedUpdate()
        {
            if (_botMessageQueue.Count > 0)
                SendChatMessage(_botMessageQueue.Dequeue());

            if (_refreshQueue)
            {
                if (RequestBotListViewController.Instance.isActivated)
                    RequestBotListViewController.Instance.UpdateRequestUI(true);
                _refreshQueue = false;
            }
        }

        private IEnumerator ProcessBlacklistRequests()
        {
            WaitUntil waitForBlacklistRequest = new WaitUntil(() => BlacklistQueue.Count > 0);
            while (!Plugin.Instance.IsApplicationExiting)
            {
                yield return waitForBlacklistRequest;

                if (BlacklistQueue.Count > 0 && BlacklistQueue.TryDequeue(out var request))
                {
                    bool silence = request.Value;
                    string songId = request.Key.song["id"].Value;
                    using (var web = UnityWebRequest.Get($"https://beatsaver.com/api/songs/detail/{songId}"))
                    {
                        yield return web.SendWebRequest();
                        if (web.isNetworkError || web.isHttpError)
                        {
                            if (!silence) QueueChatMessage($"Invalid BeatSaver ID \"{songId}\" specified.");
                            continue;
                        }

                        JSONNode result = JSON.Parse(web.downloadHandler.text);

                        if (result["songs"].IsArray && result["total"].AsInt == 0)
                        {
                            if (!silence) QueueChatMessage($"BeatSaver ID \"{songId}\" does not exist.");
                            continue;
                        }
                        yield return null;

                        request.Key.song = result["song"].AsObject;
                        SongBlacklist.Songs.Add(songId, request.Key);
                        SongBlacklist.Write();

                        if (!silence) QueueChatMessage($"{request.Key.song["songName"].Value} by {request.Key.song["authorName"].Value} ({songId}) added to the blacklist.");
                    }
                }
            }
        }

        private void SendChatMessage(string message)
        {
            try
            {
                Plugin.Log($"Sending message: \"{message}\"");
                TwitchWebSocketClient.SendMessage($"PRIVMSG #{Config.Instance.TwitchChannelName} :{message}");
                TwitchMessage tmpMessage = new TwitchMessage();
                tmpMessage.user = TwitchWebSocketClient.OurTwitchUser;
                MessageParser.Parse(new ChatMessage(message, tmpMessage));
            }
            catch (Exception e)
            {
                Plugin.Log($"Exception was caught when trying to send bot message. {e.ToString()}");
            }
        }

        public void QueueChatMessage(string message)
        {
            _botMessageQueue.Enqueue(message);
        }

        private string GetStarRating(ref JSONObject song, bool mode = true)
        {
            if (!mode) return "";
            string stars = "******";
            float rating = song["rating"].AsFloat;
            if (rating < 0 || rating > 100) rating = 0;
            string starrating = stars.Substring(0, (int)(rating / 17)); // 17 is used to produce a 5 star rating from 80ish to 100.
            return starrating;
        }

        private IEnumerator ProcessRequestQueue()
        {
            var waitForRequests = new WaitUntil(() => UnverifiedRequestQueue.Count > 0);
            while (!Plugin.Instance.IsApplicationExiting)
            {
                yield return waitForRequests;

                if (UnverifiedRequestQueue.TryDequeue(out var requestInfo))
                    yield return CheckRequest(requestInfo);
            }
        }

        private IEnumerator CheckRequest(RequestInfo requestInfo)
        {
            TwitchUser requestor = requestInfo.requestor;

            string request = requestInfo.request;

            // Special code for numeric searches
            if (requestInfo.isBeatSaverId)
            {
                // Remap song id if entry present. This is one time, and not correct as a result. No recursion right now, could be confusing to the end user.
                string[] requestparts = request.Split(new char[] { '-' }, 2);

                if (requestparts.Length > 0 && songremap.ContainsKey(requestparts[0]) && isNotModerator(requestor))
                {
                    request = songremap[requestparts[0]];
                    QueueChatMessage($"Remapping request {requestInfo.request} to {request}");
                }

                string requestcheckmessage = IsRequestInQueue(request);               // Check if requested ID is in Queue  
                if (requestcheckmessage != "")
                {
                    QueueChatMessage(requestcheckmessage);
                    yield break;
                }
            }

            // Get song query results from beatsaver.com

            string requestUrl = requestInfo.isBeatSaverId ? "https://beatsaver.com/api/songs/detail" : "https://beatsaver.com/api/songs/search/song";
            using (var web = UnityWebRequest.Get($"{requestUrl}/{request}"))
            {
                yield return web.SendWebRequest();
                if (web.isNetworkError || web.isHttpError)
                {
                    Plugin.Log($"Error {web.error} occured when trying to request song {request}!");
                    QueueChatMessage($"Invalid BeatSaver ID \"{request}\" specified.");
                    yield break;
                }

                JSONNode result = JSON.Parse(web.downloadHandler.text);

                if (result["songs"].IsArray && result["total"].AsInt == 0)
                {
                    QueueChatMessage($"No results found for request \"{request}\"");
                    yield break;
                }
                yield return null;

                List<JSONObject> songs = new List<JSONObject>();                 // Load resulting songs into a list 

                string errormessage = "";

                if (result["songs"].IsArray)
                {
                    foreach (JSONObject currentSong in result["songs"].AsArray)
                    {
                        errormessage = SongSearchFilter(currentSong, false);
                        if (errormessage == "") songs.Add(currentSong);
                    }
                }
                else
                {
                    songs.Add(result["song"].AsObject);
                }

                // Filter out too many or too few results
                if (songs.Count == 0)
                {
                    if (errormessage == "") errormessage = $"No results found for request \"{request}\"";
                }
                else if (!Config.Instance.AutopickFirstSong && songs.Count >= 4)
                    errormessage = $"Request for '{request}' produces {songs.Count} results, narrow your search by adding a mapper name, or use https://beatsaver.com to look it up.";
                else if (!Config.Instance.AutopickFirstSong && songs.Count > 1 && songs.Count < 4)
                {
                    string songlist = $"@{requestor.displayName}, please choose: ";
                    foreach (var eachsong in songs) songlist += $"{eachsong["songName"].Value}-{eachsong["songSubName"].Value}-{eachsong["authorName"].Value} ({eachsong["version"].Value}), ";
                    errormessage = songlist.Substring(0, songlist.Length - 2); // Remove trailing ,'s
                }
                else
                {
                    if (isNotModerator(requestor) || !requestInfo.isBeatSaverId) errormessage = SongSearchFilter(songs[0], false);
                }

                // Display reason why chosen song was rejected, if filter is triggered. Do not add filtered songs
                if (errormessage != "")
                {
                    QueueChatMessage(errormessage);
                    yield break;
                }

                var song = songs[0];

                RequestTracker[requestor.id].numRequests++;
                duplicatelist.Add(song["id"].Value);

                RequestQueue.Songs.Add(new SongRequest(song, requestor, requestInfo.requestTime, RequestStatus.Queued));
                RequestQueue.Write();

                Writedeck(requestor, "savedqueue"); // Might not be needed.. logic around saving and loading deck state needs to be reworked
                QueueChatMessage($"Request {song["songName"].Value} by {song["authorName"].Value} {GetStarRating(ref song, Config.Instance.ShowStarRating)} ({song["version"].Value}) added to queue.");

                UpdateRequestButton();

                _refreshQueue = true;
            }
        }

        private static IEnumerator ProcessSongRequest(int index, bool fromHistory = false)
        {
            if ((RequestQueue.Songs.Count > 0 && !fromHistory) || (RequestHistory.Songs.Count > 0 && fromHistory))
            {
                SongRequest request = null;
                if (!fromHistory)
                {
                    SetRequestStatus(index, RequestStatus.Played);
                    request = DequeueRequest(index);
                }
                else
                {
                    request = RequestHistory.Songs.ElementAt(index);
                }

                if (request == null)
                {
                    Plugin.Log("Can't process a null request! Aborting!");
                    yield break;
                }
                else
                    Plugin.Log($"Processing song request {request.song["songName"].Value}");

                bool retried = false;
                string songIndex = request.song["version"].Value, songName = request.song["songName"].Value;
                string currentSongDirectory = Path.Combine(Environment.CurrentDirectory, "CustomSongs", songIndex);
                string songHash = request.song["hashMd5"].Value.ToUpper();

            retry:
                CustomLevel[] levels = SongLoader.CustomLevels.Where(l => l.levelID.StartsWith(songHash)).ToArray();
                if (levels.Length == 0)
                {
                    Utilities.EmptyDirectory(".requestcache", false);

                    if (Directory.Exists(currentSongDirectory))
                    {
                        Utilities.EmptyDirectory(currentSongDirectory, true);
                        Plugin.Log($"Deleting {currentSongDirectory}");
                    }

                    string localPath = Path.Combine(Environment.CurrentDirectory, ".requestcache", $"{songIndex}.zip");
                    yield return Utilities.DownloadFile(request.song["downloadUrl"].Value, localPath);
                    yield return Utilities.ExtractZip(localPath, currentSongDirectory);
                    yield return SongListUtils.RefreshSongs(false, false, true);


                    Utilities.EmptyDirectory(".requestcache", true);
                    levels = SongLoader.CustomLevels.Where(l => l.levelID.StartsWith(songHash)).ToArray();
                }
                else
                {
                    Plugin.Log($"Song {songName} already exists!");
                }

                if (levels.Length > 0)
                {
                    Plugin.Log($"Scrolling to level {levels[0].levelID}");
                    if (!SongListUtils.ScrollToLevel(levels[0].levelID) && !retried)
                    {
                        retried = true;
                        goto retry;
                    }
                }
                else
                {
                    Plugin.Log("Failed to find new level!");
                }


                var song = request.song;

                // BUG: Songs status chat messages need to be configurable.

                Instance.QueueChatMessage($"{song["songName"].Value} by {song["authorName"].Value} {GetSongLink(ref song, 2)} is next.");

                _songRequestMenu.Dismiss();
            }
        }


        public static string GetSongLink(ref JSONObject song, int formatindex)
        {
            string[] link ={
                    $"({song["version"].Value})",
                    $"https://beatsaver.com/browse/detail/{song["version"].Value}",
                    $"https://bsaber.com/songs/{song["id"].Value}"
                    };

            if (formatindex >= link.Length) return "";

            return link[formatindex];
        }

        private static void UpdateRequestButton()
        {
            try
            {
                RequestBot.WriteQueueSummaryToFile(); // Write out queue status to file, do it first

                if (RequestQueue.Songs.Count == 0)
                {
                    _requestButton.gameObject.GetComponentInChildren<Image>().color = Color.red;
                }
                else
                {
                    _requestButton.gameObject.GetComponentInChildren<Image>().color = Color.green;
                }

            }
            catch
            {

            }
        }

        public static void DequeueRequest(SongRequest request)
        {
            RequestHistory.Songs.Insert(0, request);
            if (RequestHistory.Songs.Count > Config.Instance.RequestHistoryLimit)
            {
                int diff = RequestHistory.Songs.Count - Config.Instance.RequestHistoryLimit;
                RequestHistory.Songs.RemoveRange(RequestHistory.Songs.Count - diff - 1, diff);
            }
            RequestQueue.Songs.Remove(request);
            RequestHistory.Write();
            RequestQueue.Write();

            // Decrement the requestors request count, since their request is now out of the queue
            if (RequestTracker.ContainsKey(request.requestor.id)) RequestTracker[request.requestor.id].numRequests--;

            UpdateRequestButton();
            _refreshQueue = true;
        }

        public static SongRequest DequeueRequest(int index)
        {
            SongRequest request = RequestQueue.Songs.ElementAt(index);

            if (request != null)
                DequeueRequest(request);
            return request;
        }

        public static void SetRequestStatus(int index, RequestStatus status, bool fromHistory = false)
        {
            if (!fromHistory)
                RequestQueue.Songs[index].status = status;
            else
                RequestHistory.Songs[index].status = status;
        }

        public static void Blacklist(int index, bool fromHistory, bool skip)
        {
            // Add the song to the blacklist
            SongRequest request = fromHistory ? RequestHistory.Songs.ElementAt(index) : RequestQueue.Songs.ElementAt(index);

            SongBlacklist.Songs.Add(request.song["id"].Value, new SongRequest(request.song, request.requestor, DateTime.UtcNow, RequestStatus.Blacklisted));
            SongBlacklist.Write();

            Instance.QueueChatMessage($"{request.song["songName"].Value} by {request.song["authorName"].Value} ({request.song["id"].Value}) added to the blacklist.");

            if (!fromHistory)
            {
                if (skip)
                    Skip(index, RequestStatus.Blacklisted);
            }
            else
                SetRequestStatus(index, RequestStatus.Blacklisted, fromHistory);
        }

        public static void Skip(int index, RequestStatus status = RequestStatus.Skipped)
        {
            // Set the final status of the request
            SetRequestStatus(index, status);

            // Then dequeue it
            DequeueRequest(index);
        }

        public static void Process(int index, bool fromHistory)
        {
            Instance?.StartCoroutine(ProcessSongRequest(index, fromHistory));
        }

        public static void Next()
        {
            Instance?.StartCoroutine(ProcessSongRequest(0));
        }


        // Some of these are just ideas, putting them all down, can filter them out later
        [Flags] public enum CmdFlags
        {
            None = 0,
            Everyone = 1,
            Sub = 2,
            Mod = 4,
            Broadcaster = 8,
            VIP=16,
            PermitUser=32,  // If this is enabled, users on a list are allowed to use a command (this is an OR, so leave restrictions to Broadcaster if you want ONLY users on a list)  
            ShowRestrictions = 64, // Using the command without the right access level will show permissions error. Mostly used for commands that can be unlocked at different tiers.
            UsageHelp=128, // Enable usage help for blank / invalid command
            LongHelp=256, // Enable ? operation, showing a longer explanation in stream (try to limit it to one message)
            HelpLink=512, // Enable link to web documentation

            WhisperReply=1024, // Reply in a whisper to the user (future feature?). Allow commands to send the results to the user, avoiding channel spam
    
            Timeout=2048, // Applies a timeout to regular users after a command is succesfully invoked this is just a concept atm
            TimeoutSub=4096, // Applies a timeout to Subs
            TimeoutVIP=8192, // Applies a timeout to VIP's
            TimeoutMod=16384, // Applies a timeout to MOD's. A way to slow spamming of channel for overused commands. 

            NoLinks=32768, // Turn off any links that the command may normally generate
            Silence=65536, // Command produces no output at all - but still executes
            Verbose=131072, // Turn off command output limits, This can result in excessive channel spam
            Log=262144, // Log every use of the command to a file
            RegEx, // Enable regex check

            Enabled = 1<<30, // If off, the command will not be added to the alias list at all.
        }

        const CmdFlags Everyone = CmdFlags.Enabled |  CmdFlags.Everyone;
        const CmdFlags Broadcasteronly = CmdFlags.Enabled | CmdFlags.Broadcaster;
        const CmdFlags Modonly = CmdFlags.Enabled | CmdFlags.Broadcaster | CmdFlags.Mod;
            
        // Prototype code only
        public struct BOTCOMMAND
        {
            public Action<TwitchUser, string> Method;  // Method to call
            public CmdFlags cmdflags;                  // flags
            public string ShortHelp;                   // short help text (on failing preliminary check
            public List<string> aliases;               // list of command aliases
            public List<string> regexfilters;          // reg ex filters to apply If Any match, you're good.

            public string LongHelp; // Long help text
            public string HelpLink; // Help website link
            StringListManager permittedusers; // List of users permitted to use the command, uses list manager.

            public BOTCOMMAND(Action<TwitchUser, string> method, CmdFlags flags,string shorthelptext,string [] alias)
                {
                Method = method;
                cmdflags = flags;
                ShortHelp = shorthelptext;
                aliases = alias.ToList();
                LongHelp = "";
                HelpLink = "";
                permittedusers = null;
                regexfilters = null;
                foreach (var entry in aliases) NewCommands.Add(entry, this);                
                }
        }

        public static List<BOTCOMMAND> cmdlist = new List<BOTCOMMAND>() ; 

        public void AddCommand ( string[] alias,Action<TwitchUser, string> method, CmdFlags flags=Broadcasteronly, string shorthelptext="usage: %x")
            {
            cmdlist.Add(new BOTCOMMAND(method, flags, shorthelptext,alias));
            }

        public void AddCommand(string alias, Action<TwitchUser, string> method, CmdFlags flags = Broadcasteronly, string shorthelptext = "usage: %x")
        {
            string [] list = new string[] { alias };
            cmdlist.Add(new BOTCOMMAND(method, Broadcasteronly, "", list));
        }

        private void InitializeCommands()
        {

            // Note: Default permissions are broadcaster only, so don't need to set them

            foreach (string c in Config.Instance.RequestCommandAliases.Split(',').Distinct())
            {
                AddCommand(c, ProcessSongRequest,Everyone);
                Plugin.Log($"Added command alias \"{c}\" for song requests.");
            }

            ReadRemapList();
   
            // Testing prototype code now
            AddCommand("queue", ListQueue,Everyone);
            AddCommand("unblock", Unban,Modonly);
            AddCommand("block", Ban,Modonly);
            AddCommand("remove", DequeueSong,Modonly);
            AddCommand("clearqueue", Clearqueue,Broadcasteronly);
            AddCommand("mtt", MoveRequestToTop,Modonly);
            AddCommand("remap", Remap);
            AddCommand("unmap", Unmap);
            AddCommand(new string [] { "lookup","find"}, lookup,Modonly | CmdFlags.Sub );
            AddCommand(new string[] { "last", "demote", "later" }, MoveRequestToBottom);
            AddCommand(new string[] { "wrongsong", "wrong", "oops" }, WrongSong,Everyone);
            AddCommand("blist", ShowBanList);
            AddCommand("open", OpenQueue);
            AddCommand("close", CloseQueue);
            AddCommand("restore", restoredeck);
            AddCommand("commandlist", showCommandlist,Everyone);
            AddCommand("played", ShowSongsplayed,Modonly);
            AddCommand("readdeck", Readdeck);
            AddCommand("writedeck", Writedeck);
            AddCommand("clearalreadyplayed", ClearDuplicateList); // Needs a better name

            AddCommand("link", ShowSongLink,Everyone);


            // Whitelists mappers and add new songs, this code is being refactored and transitioned to testing

            AddCommand("mapperwhitelist", mapperWhitelist,Broadcasteronly);  // this interface will change shortly.
            AddCommand("mapperblacklist", mapperBlacklist,Broadcasteronly);  // Subject to change

            AddCommand(new string[] { "addnew", "addlatest" }, addNewSongs,Modonly);
            AddCommand("addsongs", addSongs,Broadcasteronly); // Basically search all, need to decide if its useful


            LoadList(TwitchWebSocketClient.OurTwitchUser, "mapper.list"); // BUG: There are 2 calls, will unify shortly
            mapperWhitelist(TwitchWebSocketClient.OurTwitchUser, "mapper.list");


            // Temporary commands for testing
            AddCommand("load", LoadList);
            AddCommand("unload", UnloadList);
            AddCommand("clearlist", ClearList);
            AddCommand("write", writelist);
            AddCommand("list", ListList);
            AddCommand("lists", showlists);


#if PRIVATE
            AddCommand("deck",createdeck);
            AddCommand("unloaddeck",unloaddeck);      
            AddCommand("loaddecks",loaddecks);
            AddCommand("decklist",decklist);
            AddCommand("mapper", addsongsbymapper); // This is actually most useful if we send it straight to list

            loaddecks (TwitchWebSocketClient.OurTwitchUser,"");
#endif

            var msg = new QueueLongMessage();
            msg.Header("New command table: ");
            foreach (var entry in cmdlist)
                foreach (var alias in entry.aliases)
                    msg.Add(alias, ", ");
            msg.end("...", "No new commands defined");

        }

        private void lookup(TwitchUser requestor, string request)
        {
            if (isNotModerator(requestor) && !requestor.isSub)
            {
                QueueChatMessage($"lookup command is limited to Subscribers and moderators.");
                return;
            }
            StartCoroutine(LookupSongs(requestor, request));
        }
        
        private string GetBeatSaverId(string request)
        {
            if (_digitRegex.IsMatch(request)) return request;
            if (_beatSaverRegex.IsMatch(request))
            {
                string[] requestparts = request.Split(new char[] { '-' }, 2);
                return requestparts[0];
            }
            return "";
        }

        private void ProcessSongRequest(TwitchUser requestor, string request)
        {
            try
            {
                if (QueueOpen == false && isNotModerator(requestor))
                {
                    QueueChatMessage($"Queue is currently closed.");
                    return;
                }

                if (request == "")
                {
                    // Would be nice if it was configurable
                    QueueChatMessage($"usage: bsr <song id> or <part of song name and mapper if known>");
                    return;
                }

                if (!RequestTracker.ContainsKey(requestor.id))
                    RequestTracker.Add(requestor.id, new RequestUserTracker());

                int limit = Config.Instance.RequestLimit;
                if (requestor.isSub) limit = Math.Max(limit, Config.Instance.SubRequestLimit);
                if (requestor.isMod) limit = Math.Max(limit, Config.Instance.ModRequestLimit);
                if (requestor.isVip) limit += Config.Instance.VipBonus; // Current idea is to give VIP's a bonus over their base subscription class, you can set this to 0 if you like

                /*
                // Currently using simultaneous request limits, will be introduced later / or activated if time mode is on.
                // Only rate limit users who aren't mods or the broadcaster - 
                if (!requestor.isMod && !requestor.isBroadcaster)
                {
                    if (_requestTracker[requestor.id].resetTime <= DateTime.Now)
                    {
                        _requestTracker[requestor.id].resetTime = DateTime.Now.AddMinutes(Config.Instance.RequestCooldownMinutes);
                        _requestTracker[requestor.id].numRequests = 0;
                    }
                    if (_requestTracker[requestor.id].numRequests >= Config.Instance.RequestLimit)
                    {
                        var time = (_requestTracker[requestor.id].resetTime - DateTime.Now);
                        QueueChatMessage($"{requestor.displayName}, you can make another request in{(time.Minutes > 0 ? $" {time.Minutes} minute{(time.Minutes > 1 ? "s" : "")}" : "")} {time.Seconds} second{(time.Seconds > 1 ? "s" : "")}.");
                        return;
                    }
                }
                */

                if (!requestor.isBroadcaster)
                {
                    if (RequestTracker[requestor.id].numRequests >= limit)
                    {
                        QueueChatMessage($"You already have {RequestTracker[requestor.id].numRequests} on the queue. You can add another once one is played. Subscribers are limited to {Config.Instance.SubRequestLimit}.");
                        return;
                    }
                }

                RequestInfo newRequest = new RequestInfo(requestor, request, DateTime.UtcNow, _digitRegex.IsMatch(request) || _beatSaverRegex.IsMatch(request));
                if (!newRequest.isBeatSaverId && request.Length < 3)
                    QueueChatMessage($"Request \"{request}\" is too short- Beat Saver searches must be at least 3 characters!");
                else if (!UnverifiedRequestQueue.Contains(newRequest))
                    UnverifiedRequestQueue.Enqueue(newRequest);

            }
            catch (Exception ex)
            {
                Plugin.Log(ex.ToString());

            }
        }

        public static void Process(int index)
        {
            Instance?.StartCoroutine(ProcessSongRequest(index));
        }

        public static void Parse(TwitchUser user, string request)
        {
            if (!Instance) return;
            if (!request.StartsWith("!")) return;

            string[] parts = request.Split(new char[] { ' ' }, 2);

            if (parts.Length <= 0) return;

            string command = parts[0].Substring(1)?.ToLower();
            if (NewCommands.ContainsKey(command))
            {
                string param = parts.Length > 1 ? parts[1] : "";
                if (deck.ContainsKey(command))
                {
                    param = command;
                    if (parts.Length > 1) param += " " + parts[1];
                }
                //Commands[command]?.Invoke(user, param);
                NewCommands[command].Method(user, param);
            }
        }



    }
}



