using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ExtractionConfirm
{
    /// <summary>
    /// The mod's own heartbeat.
    ///
    /// An earlier version of this was a GameObject with a MonoBehaviour created while BepInEx
    /// loaded, and it never produced a single line - a GameObject created before any scene exists
    /// does not survive the first scene load, and nothing says so. A GameObject-based heartbeat is
    /// exactly the kind of thing that fails silently, so this version has no GameObject in it at
    /// all: a static C# event and a Harmony postfix cannot be destroyed by a scene change.
    ///
    /// Two independent signals are used, because either one alone proves nothing if it stops:
    ///
    ///   SceneManager.sceneLoaded  - "a scene actually finished loading", with the scene's name.
    ///                               This is the answer to "did the level ever load", which no
    ///                               amount of guessing at the log could settle.
    ///   ChatManager.Update        - a per-frame tick while a round is running, used to keep the
    ///                               button armed and to re-scan the extraction points.
    /// </summary>
    internal static class ModRunner
    {
        /// <summary>How often the level is re-scanned while a round runs.</summary>
        private const float PumpInterval = 0.5f;

        private static bool _subscribed;
        private static float _nextPump;

        /// <summary>Hooks the scene event. Safe to call more than once.</summary>
        public static void Start()
        {
            if (_subscribed)
                return;

            _subscribed = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        public static void Stop()
        {
            if (!_subscribed)
                return;

            _subscribed = false;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        /// <summary>
        /// A scene finished loading. Fires after Awake and before Start, so the extraction points
        /// in it already exist here even though their Start has not run yet.
        /// </summary>
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                ConfirmGate.LogSceneLoaded(scene.name);
                Pump();
            }
            catch (Exception ex)
            {
                ConfirmGate.ReportPatchError("SceneLoaded", ex);
            }
        }

        /// <summary>Per-frame entry point. Scans on a timer, so cost does not scale with framerate.</summary>
        public static void Tick()
        {
            if (Time.unscaledTime < _nextPump)
                return;

            Pump();
        }

        private static void Pump()
        {
            _nextPump = Time.unscaledTime + PumpInterval;

            try
            {
                ConfirmGate.Pump();
            }
            catch (Exception ex)
            {
                ConfirmGate.ReportPatchError("ModRunner", ex);
            }
        }
    }
}