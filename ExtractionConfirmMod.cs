using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace ExtractionConfirm
{
    /// <summary>
    /// Entry point.
    ///
    /// Reading the config, installing four patches on ExtractionPoint, and starting one runner that
    /// reports what the level contains. Nothing subscribes to Photon at startup: the multiplayer
    /// event hook is installed lazily, and only once an extraction is actually being held.
    ///
    /// The types are resolved by name at startup so a future game update that renames something
    /// logs an error and leaves the game unpatched instead of failing to load.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("REPO.exe")]
    public class ExtractionConfirmPlugin : BaseUnityPlugin, IOnEventCallback
    {
        public const string PluginGuid = "com.repo.extractionconfirm";
        public const string PluginName = "ExtractionConfirm";
        public const string PluginVersion = "1.0.0";

        public static ExtractionConfirmPlugin Instance;
        public static ManualLogSource Log;
        public static ConfirmSettings Settings;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Settings = new ConfirmSettings(Config);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded");

            if (!Settings.Enabled.Value)
            {
                Log.LogInfo("Disabled in config - no patches installed");
                return;
            }

            try
            {
                _harmony = new Harmony(PluginGuid);
                ApplyPatches(_harmony);
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to initialise: {ex}");
            }

            try
            {
                // Independent of every patch: a scene event and a per-frame hook, neither of which
                // a scene change can destroy. This is what makes a silent log impossible to
                // explain away.
                ModRunner.Start();
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to start the runner: {ex}");
            }
        }

        /// <summary>
        /// BaseUnityPlugin is a MonoBehaviour, so Unity calls this every frame for as long as the
        /// plugin object is alive.
        ///
        /// It is the one per-frame hook this mod owns end to end, which makes it the liveness
        /// probe: Harmony patches and a static event can both be installed and still stop firing,
        /// and the only way to tell "the hook never fired" from "everything registered here was
        /// torn down" is to have something that reports from inside the plugin itself.
        /// </summary>
        private void Update()
        {
            _updateCount++;

            // Loudly for the first few seconds, then quietly - enough to see a dead plugin without
            // filling the log for a whole round.
            if (_updateCount > 30)
            {
                if (Time.unscaledTime < _nextQuietLog)
                    return;

                _nextQuietLog = Time.unscaledTime + 15f;
            }

            Log?.LogInfo(
                $"Alive #{_updateCount}: frame {Time.frameCount}, "
                + $"scene '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}', "
                + $"plugin object {(gameObject != null && gameObject.activeInHierarchy ? "active" : "INACTIVE")}, "
                + $"patches={(_harmony != null)}");

            // Drive the mod from here too. This is the one hook that belongs to the plugin itself,
            // so whatever happens to the Harmony patches, this still finds the extraction points,
            // re-arms the button and reports what it sees.
            ModRunner.Tick();
        }

        private void OnDestroy()
        {
            // Loud, because this is the one event that would explain every silent symptom at
            // once: a destroyed plugin object takes the Harmony patches, the scene subscription
            // and the whole static state with it.
            Log?.LogError(
                $"Plugin object destroyed after {_updateCount} updates - "
                + "every patch and subscription this mod installed is now gone.");

            // Deliberately NOT calling UnpatchSelf here. At process exit it changes nothing, and if
            // the object is ever destroyed early it would silently remove every hook the mod has.
            // BepInEx tears the domain down anyway.
        }

        private int _updateCount;
        private float _nextQuietLog;

        /// <summary>
        /// Four patches on ExtractionPoint:
        /// - StateSet: holds the Success/Surplus -> Warning (countdown) transition.
        /// - OnClick:  a press of the extraction point's own button acts as a confirmation.
        /// - Start:    reports what the mod can see of this extraction point.
        /// - Update:   re-arms the button and watches for the press, once per frame.
        ///
        /// None of them is load-bearing on its own: ModRunner does the same work every half second
        /// whatever these do, so a patch that fails to attach costs speed, never function.
        /// </summary>
        private static void ApplyPatches(Harmony harmony)
        {
            Type type = AccessTools.TypeByName("ExtractionPoint");
            if (type == null)
            {
                Log.LogError("ExtractionPoint type not found - ExtractionConfirm is inactive.");
                return;
            }

            if (!ConfirmGate.Resolve(type))
            {
                Log.LogError("ExtractionPoint members missing - ExtractionConfirm is inactive.");
                return;
            }

            MethodInfo stateSet = AccessTools.Method(type, "StateSet");
            if (stateSet == null)
            {
                Log.LogError("ExtractionPoint.StateSet not found - ExtractionConfirm is inactive.");
                return;
            }

            harmony.Patch(stateSet,
                prefix: new HarmonyMethod(typeof(ExtractionPatches), nameof(ExtractionPatches.StateSetPrefix)));

            MethodInfo onClick = AccessTools.Method(type, "OnClick");

            MethodInfo start = AccessTools.Method(type, "Start", Type.EmptyTypes);
            MethodInfo update = AccessTools.Method(type, "Update", Type.EmptyTypes);

            // Report what was actually installed. An unconditional "patched ..." line would claim
            // a hook the mod never got, and that is exactly the kind of thing that sends you
            // looking for the bug in the wrong place when a log stays quiet.
            List<string> patched = new List<string> { "ExtractionPoint.StateSet" };

            if (onClick != null)
            {
                harmony.Patch(onClick,
                    prefix: new HarmonyMethod(typeof(ExtractionPatches), nameof(ExtractionPatches.OnClickPrefix)));
                patched.Add("ExtractionPoint.OnClick");
            }
            else
            {
                Log.LogWarning("ExtractionPoint.OnClick not found - a press of the extraction point's own button may not register.");
            }

            if (start != null)
            {
                harmony.Patch(start,
                    prefix: new HarmonyMethod(typeof(ExtractionPatches), nameof(ExtractionPatches.StartPrefix)));
                patched.Add("ExtractionPoint.Start");
            }
            else
            {
                Log.LogWarning("ExtractionPoint.Start not found - no per-point startup report.");
            }

            if (update != null)
            {
                harmony.Patch(update,
                    postfix: new HarmonyMethod(typeof(ExtractionPatches), nameof(ExtractionPatches.UpdatePostfix)));
                patched.Add("ExtractionPoint.Update");
            }

            // The heartbeat. ChatManager.Update runs every frame while a round is running, in both
            // singleplayer and multiplayer, and is present in the level - so it is the one hook that
            // is certain to tick where the mod has work to do.
            Type chatType = AccessTools.TypeByName("ChatManager");
            MethodInfo chatUpdate = chatType != null ? AccessTools.Method(chatType, "Update", Type.EmptyTypes) : null;
            if (chatUpdate != null)
            {
                harmony.Patch(chatUpdate,
                    postfix: new HarmonyMethod(typeof(ExtractionPatches), nameof(ExtractionPatches.ChatUpdatePostfix)));
                patched.Add("ChatManager.Update");
            }
            else
            {
                Log.LogWarning("ChatManager.Update not found - the mod keeps no per-frame heartbeat.");
            }

            Log.LogInfo("Patched " + string.Join(", ", patched.ToArray()));
        }

        /// <summary>
        /// Installs the Photon event hook on demand. This is called the first time an extraction is
        /// held - never during startup - and is what lets a non-host player's button press reach
        /// the host, which owns the extraction state machine.
        /// </summary>
        internal void EnsureNetworkHook()
        {
            if (_networkHookInstalled)
                return;

            _networkHookInstalled = true;
            PhotonNetwork.AddCallbackTarget(this);
        }

        private bool _networkHookInstalled;

        /// <summary>Receives the multiplayer confirmation event raised by non-host clients.</summary>
        public void OnEvent(EventData eventData)
        {
            try
            {
                ConfirmGate.OnPhotonEvent(eventData);
            }
            catch (Exception ex)
            {
                ConfirmGate.ReportPatchError("OnEvent", ex);
            }
        }
    }

    /// <summary>Live configuration, bound to the BepInEx config file on startup.</summary>
    public class ConfirmSettings
    {
        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<bool> SpawnButton;
        public readonly ConfigEntry<string> HoldText;
        public readonly ConfigEntry<string> ButtonHint;
        public readonly ConfigEntry<float> PlaceX;
        public readonly ConfigEntry<float> PlaceY;
        public readonly ConfigEntry<float> PlaceZ;

        public ConfirmSettings(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true,
                "Hold the extraction before the 3-2-1 countdown until the physical extraction point button is pressed. Set to false and restart to disable the mod entirely.");

            SpawnButton = config.Bind("Button", "Enabled", true,
                "Re-arm the extraction point's own button while the extraction is held and let that press confirm it. In the shop the same button stands on a counter and buys; here it extracts.");

            HoldText = config.Bind("Visuals", "HoldText", "CONFIRM",
                "Text shown on the extraction tube screen while waiting for the button press.");

            ButtonHint = config.Bind("Visuals", "ButtonHint", "Confirm extraction",
                "Hint shown when looking at the confirm button.");

            // Where the shop stand goes, in the extraction point's own frame: the south-west corner
            // of the extraction square, outside the tube. The walls bounding that square are at
            // x -2.108 / +2.080 and z -1.989 / +2.026, and +z is the south (front) border the
            // regular button sits in the middle of. Negate any of these to move to another corner.
            PlaceX = config.Bind("Placement", "X", 2.08f,
                "Left/right position of the spawned shop stand, in the extraction point's local frame. The extraction square spans -2.108 to +2.080; the default is the south-west corner.");

            PlaceY = config.Bind("Placement", "Y", 1.03f,
                "Height of the spawned button, matching the extraction point's own button. Lower it if the stand floats.");

            PlaceZ = config.Bind("Placement", "Z", 2.03f,
                "Front/back position of the spawned shop stand, in the extraction point's local frame. The extraction square spans -1.989 to +2.026; +z is the south (front) border.");
        }
    }

    /// <summary>
    /// The Harmony patch bodies. Every body fails open: if anything unexpected happens the
    /// vanilla code runs instead, so a bug in the mod can never break the extraction itself.
    /// </summary>
    public static class ExtractionPatches
    {
        /// <summary>
        /// Harmony prefixes return true to RUN the vanilla method and false to skip it, so the
        /// gate's "block it" answer is negated here.
        /// </summary>
        public static bool StateSetPrefix(ExtractionPoint __instance, ExtractionPoint.State newState)
        {
            try
            {
                return !ConfirmGate.ShouldSkipTransition(__instance, newState);
            }
            catch (Exception ex)
            {
                ConfirmGate.ReportPatchError("StateSet", ex);
                return true; // fail open: let the vanilla transition through
            }
        }

        public static bool OnClickPrefix(ExtractionPoint __instance)
        {
            try
            {
                return !ConfirmGate.HandleButtonPress(__instance);
            }
            catch (Exception ex)
            {
                ConfirmGate.ReportPatchError("OnClick", ex);
                return true; // fail open: let the vanilla button logic run
            }
        }

        /// <summary>Reports what this extraction point offers the mod, once, when it comes alive.</summary>
        public static void StartPrefix(ExtractionPoint __instance)
        {
            try
            {
                ConfirmGate.PreparePoint(__instance);
            }
            catch (Exception ex)
            {
                ConfirmGate.ReportPatchError("Start", ex);
            }
        }

        /// <summary>Re-arms the button and watches for the press, once per frame.</summary>
        public static void UpdatePostfix(ExtractionPoint __instance)
        {
            try
            {
                ConfirmGate.Tick(__instance);
            }
            catch (Exception ex)
            {
                ConfirmGate.ReportPatchError("Update", ex);
            }
        }

        /// <summary>
        /// The heartbeat. Runs every frame while a round is running and re-scans the level on a
        /// timer, so the mod keeps working even if every other patch fails to attach.
        /// </summary>
        public static void ChatUpdatePostfix()
        {
            try
            {
                ModRunner.Tick();
            }
            catch (Exception ex)
            {
                ConfirmGate.ReportPatchError("ChatUpdate", ex);
            }
        }
    }
}
