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
        private bool _networkHookInstalled;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Settings = new ConfirmSettings(Config);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded");

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
                ModRunner.Start();
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to start the runner: {ex}");
            }
        }

        private void Update()
        {
            ModRunner.Tick();
        }

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

        internal void EnsureNetworkHook()
        {
            if (_networkHookInstalled)
                return;

            _networkHookInstalled = true;
            PhotonNetwork.AddCallbackTarget(this);
        }

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

    public class ConfirmSettings
    {
        public readonly ConfigEntry<bool> ValidationAuto;
        public readonly ConfigEntry<float> ValidationAmount;

        public ConfirmSettings(ConfigFile config)
        {
            ValidationAuto = config.Bind("Validation", "Auto", true,
                "True validates against the game's own haul goal. False uses Amount instead. Changing Amount turns this off automatically. In multiplayer the host's setting always wins.");

            ValidationAmount = config.Bind("Validation", "Amount", 0f,
                "Fixed validation target, 0 to 100000. Changing this turns Auto off. Leave it at 0 to keep using the game's target. In multiplayer the host's value is what everyone uses.");

            ValidationAmount.SettingChanged += (sender, args) =>
            {
                if (ValidationAmount.Value != 0f)
                    ValidationAuto.Value = false;
            };
        }
    }

    public static class ExtractionPatches
    {
        public static bool StateSetPrefix(ExtractionPoint __instance, ExtractionPoint.State newState)
        {
            try
            {
                return !ConfirmGate.ShouldSkipTransition(__instance, newState);
            }
            catch (Exception ex)
            {
                ConfirmGate.ReportPatchError("StateSet", ex);
                return true;
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
                return true;
            }
        }

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