using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ExtractionConfirm
{
    internal static class ModRunner
    {
        private const float PumpInterval = 0.5f;

        private static bool _subscribed;
        private static float _nextPump;

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

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                Pump();
            }
            catch (Exception ex)
            {
                ConfirmGate.ReportPatchError("SceneLoaded", ex);
            }
        }

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