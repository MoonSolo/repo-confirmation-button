using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace ExtractionConfirm
{
    internal static class ConfirmGate
    {
        public const byte ConfirmEventCode = 177;
        public const byte ValidationEventCode = 178;

        private const float ConfirmDebounce = 0.35f;

        private const string HoldText = "CONFIRM";
        private const string ButtonHint = "Confirm extraction";

        private static readonly Color HoldColor = new Color(1f, 0.5f, 0f);
        private static readonly Vector3 StandPosition = new Vector3(2.08f, 1.03f, 2.03f);

        private static readonly HashSet<string> ReportedErrors = new HashSet<string>();
        private static readonly HashSet<string> ReportedReasons = new HashSet<string>();

        private static readonly Dictionary<ExtractionPoint, HoldState> Holds = new Dictionary<ExtractionPoint, HoldState>();

        private static FieldInfo _currentState;
        private static FieldInfo _isShop;
        private static FieldInfo _shopStation;
        private static FieldInfo _tubeTextString;
        private static FieldInfo _tubeTextColor;
        private static FieldInfo _buttonOriginalMaterial;
        private static FieldInfo _buttonDenyActive;
        private static FieldInfo _buttonDenyLerp;
        private static FieldInfo _haulCurrent;
        private static FieldInfo _roundDirectorInstance;
        private static FieldInfo _roundCurrentHaul;
        private static FieldInfo _grabbedStaticGrabObject;
        private static MethodInfo _tubeScreenTextChange;
        private static MethodInfo _stateSet;

        private static GameObject _standTemplate;

        private sealed class HoldState
        {
            public bool Confirmed;
            public bool Released;
            public bool TextApplied;
            public float LastConfirmTime = -99f;
            public string TubeTextOriginal;
            public Color TubeColorOriginal;
            public string ButtonHintOriginal;
            public ConfirmButton Button;
            public Transform Anchor;
        }

        private static ManualLogSource Log => ExtractionConfirmPlugin.Log;
        private static ConfirmSettings Settings => ExtractionConfirmPlugin.Settings;

        public static bool Resolve(Type extractionPointType)
        {
            try
            {
                _currentState = AccessTools.Field(extractionPointType, "currentState");
                _isShop = AccessTools.Field(extractionPointType, "isShop");
                _shopStation = AccessTools.Field(extractionPointType, "shopStation");
                _tubeTextString = AccessTools.Field(extractionPointType, "tubeScreenTextString");
                _tubeTextColor = AccessTools.Field(extractionPointType, "tubeScreenTextColor");
                _buttonOriginalMaterial = AccessTools.Field(extractionPointType, "buttonOriginalMaterial");
                _buttonDenyActive = AccessTools.Field(extractionPointType, "buttonDenyActive");
                _buttonDenyLerp = AccessTools.Field(extractionPointType, "buttonDenyLerp");
                _haulCurrent = AccessTools.Field(extractionPointType, "haulCurrent");
                _tubeScreenTextChange = AccessTools.Method(extractionPointType, "TubeScreenTextChange");
                _stateSet = AccessTools.Method(extractionPointType, "StateSet", new[] { typeof(ExtractionPoint.State) });

                Type grabberType = AccessTools.TypeByName("PhysGrabber");
                _grabbedStaticGrabObject = grabberType != null
                    ? AccessTools.Field(grabberType, "grabbedStaticGrabObject")
                    : null;

                Type roundDirectorType = AccessTools.TypeByName("RoundDirector");
                if (roundDirectorType != null)
                {
                    _roundDirectorInstance = AccessTools.Field(roundDirectorType, "instance");
                    _roundCurrentHaul = AccessTools.Field(roundDirectorType, "currentHaul");
                }

                bool ok = _currentState != null && _isShop != null
                          && _tubeTextString != null && _tubeTextColor != null
                          && _buttonOriginalMaterial != null && _tubeScreenTextChange != null
                          && _grabbedStaticGrabObject != null;

                if (!ok)
                {
                    Log?.LogError(
                        "Missing members: "
                        + $"currentState={_currentState != null}, isShop={_isShop != null}, "
                        + $"tubeScreenTextString={_tubeTextString != null}, "
                        + $"tubeScreenTextColor={_tubeTextColor != null}, "
                        + $"buttonOriginalMaterial={_buttonOriginalMaterial != null}, "
                        + $"TubeScreenTextChange={_tubeScreenTextChange != null}, "
                        + $"PhysGrabber.grabbedStaticGrabObject={_grabbedStaticGrabObject != null}");
                }

                return ok;
            }
            catch (Exception ex)
            {
                Log?.LogError($"Failed to resolve game members: {ex}");
                return false;
            }
        }

        public static bool ShouldSkipTransition(ExtractionPoint ep, ExtractionPoint.State newState)
        {
            if (ep == null || _currentState == null)
                return false;

            HoldState hold = Get(ep);

            if (newState == ExtractionPoint.State.Warning)
            {
                if (!IsHolding(ep))
                {
                    RestoreTubeText(ep, hold);
                    return false;
                }

                if (hold.Confirmed)
                {
                    hold.Confirmed = false;
                    RestoreTubeText(ep, hold);
                    return false;
                }

                ApplyHoldVisuals(ep, hold);
                return true;
            }

            if (newState == ExtractionPoint.State.Extracting
                || newState == ExtractionPoint.State.Complete
                || newState == ExtractionPoint.State.Cancel
                || newState == ExtractionPoint.State.Idle
                || newState == ExtractionPoint.State.TaxReturn)
            {
                hold.Confirmed = false;
                RestoreTubeText(ep, hold);
            }

            return false;
        }

        public static bool HandleButtonPress(ExtractionPoint ep)
        {
            if (ep == null || _currentState == null)
                return false;

            if (IsHolding(ep))
            {
                ConfirmExtraction(ep);
                return true;
            }

            if (!IsRequirementMet(ep))
                DenyPress(ep);

            return false;
        }

        public static void ConfirmExtraction(ExtractionPoint ep)
        {
            if (ep == null || _currentState == null)
                return;

            HoldState hold = Get(ep);

            if (Time.time - hold.LastConfirmTime < ConfirmDebounce)
                return;

            hold.LastConfirmTime = Time.time;

            if (!IsHolding(ep))
                return;

            if (SemiFunc.IsMultiplayer() && SemiFunc.IsNotMasterClient())
            {
                if (RaiseConfirmRequest(ep))
                    Log?.LogInfo("Confirmation press - requested the countdown from the host");
                else
                    hold.Confirmed = true;
            }
            else
            {
                hold.Confirmed = true;
                Log?.LogInfo("Extraction confirmed - countdown starting");
            }
        }

        public static bool IsHolding(ExtractionPoint ep)
        {
            if (ep == null || IsShop(ep))
                return false;

            ExtractionPoint.State state = (ExtractionPoint.State)_currentState.GetValue(ep);
            return state == ExtractionPoint.State.Success || state == ExtractionPoint.State.Surplus;
        }

        public static bool IsLocallyGrabbed(StaticGrabObject grabObject)
        {
            try
            {
                PhysGrabber grabber = PhysGrabber.instance;
                if (grabber == null || grabObject == null)
                    return false;

                return ReferenceEquals(_grabbedStaticGrabObject.GetValue(grabber), grabObject);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsShop(ExtractionPoint ep) => (bool)_isShop.GetValue(ep);

        public static void Tick(ExtractionPoint ep)
        {
            if (ep == null)
                return;

            HoldState hold = Get(ep);

            EnsureButton(ep);

            if (!IsHolding(ep))
            {
                if (hold.Confirmed)
                    hold.Confirmed = false;

                hold.Released = false;
                RestoreTubeText(ep, hold);
                return;
            }

            ApplyHoldVisuals(ep, hold);
            ReleaseHold(ep, hold);
            ArmButton(ep);
            WatchPress(ep, hold);
        }

        private static void ReleaseHold(ExtractionPoint ep, HoldState hold)
        {
            if (!hold.Confirmed || hold.Released || _stateSet == null)
                return;

            try
            {
                hold.Released = true;
                _stateSet.Invoke(ep, new object[] { ExtractionPoint.State.Warning });

                if (hold.Confirmed)
                    hold.Released = false;
            }
            catch (Exception ex)
            {
                ReportPatchError("ReleaseHold", ex);
                hold.Released = false;
            }
        }

        private static void ArmButton(ExtractionPoint ep)
        {
            try
            {
                StaticGrabObject grab = ep.buttonGrabObject;
                if (grab != null && !grab.enabled)
                    grab.enabled = true;

                if (ep.buttonLight != null && !ep.buttonLight.enabled)
                    ep.buttonLight.enabled = true;

                MeshRenderer renderer = ep.button;
                Material original = _buttonOriginalMaterial?.GetValue(ep) as Material;
                if (renderer != null && original != null && renderer.material != original)
                    renderer.material = original;
            }
            catch (Exception ex)
            {
                ReportPatchError("ArmButton", ex);
            }
        }

        private static void WatchPress(ExtractionPoint ep, HoldState hold)
        {
            if (hold.Confirmed)
                return;

            try
            {
                if (IsLocallyGrabbed(ep.buttonGrabObject))
                    ConfirmExtraction(ep);
            }
            catch (Exception ex)
            {
                ReportPatchError("WatchPress", ex);
            }
        }

        public static void Pump()
        {
            if (_currentState == null)
                return;

            ExtractionPoint[] points;
            try
            {
                points = UnityEngine.Object.FindObjectsOfType<ExtractionPoint>(true);
            }
            catch (Exception ex)
            {
                ReportPatchError("FindObjectsOfType", ex);
                return;
            }

            foreach (ExtractionPoint ep in points)
            {
                if (ep == null)
                    continue;

                EnsureButton(ep);
                Tick(ep);
            }

            if (SemiFunc.IsMultiplayer() && PhotonNetwork.IsMasterClient)
                PushValidationSetting();
        }

        public static void PreparePoint(ExtractionPoint ep)
        {
            try
            {
                if (ep == null)
                    return;

                EnsureButton(ep);
            }
            catch (Exception ex)
            {
                ReportPatchError("PreparePoint", ex);
            }
        }

        public static void EnsureButton(ExtractionPoint ep)
        {
            if (ep == null)
                return;

            HoldState hold = Get(ep);
            if (hold.Button != null)
                return;

            if (SemiFunc.RunIsShop())
                return;

            GameObject template = EnsureStandTemplate(ep);
            if (template == null)
                return;

            try
            {
                GameObject clone = UnityEngine.Object.Instantiate(template, ep.transform, false);
                clone.name = "ExtractionConfirmStand";
                clone.SetActive(true);

                Transform head = FindDeep(clone.transform, "Shop Button");
                if (head == null)
                {
                    Log?.LogWarning("The shop stand has no 'Shop Button'; keeping the regular button.");
                    UnityEngine.Object.Destroy(clone);
                    return;
                }

                Neutralise(clone.transform);

                Transform source = ep.button != null ? ep.button.transform : null;
                if (source == null)
                {
                    Log?.LogWarning("The extraction point has no button to move; keeping the stand where it spawned.");
                    UnityEngine.Object.Destroy(clone);
                    return;
                }

                Vector3 target = StandPosition;

                if (hold.Anchor == null)
                {
                    var anchor = new GameObject("ExtractionConfirmAnchor");
                    anchor.transform.SetParent(ep.transform, false);
                    anchor.transform.localPosition = target - source.localPosition;
                    anchor.transform.localRotation = Quaternion.identity;
                    hold.Anchor = anchor.transform;

                    Transform carrier = ep.buttonDenyTransform != null ? ep.buttonDenyTransform : source.parent;
                    if (carrier != null)
                        carrier.SetParent(anchor.transform, false);
                }

                Vector3 expected = ep.transform.TransformPoint(target);
                if (Vector3.Distance(source.position, expected) > 0.05f)
                {
                    Log?.LogWarning(
                        $"The button did not move to the corner: at {source.position}, expected {expected}. "
                        + "buttonDenyTransform is probably no longer the button's parent.");
                }

                Vector3 headInStation = head.localPosition;

                clone.transform.SetParent(ep.transform, false);
                clone.transform.localPosition = target - headInStation;
                clone.transform.localRotation = Quaternion.identity;

                ep.button.enabled = false;

                ConfirmButton button = head.gameObject.AddComponent<ConfirmButton>();
                button.Setup(ep, head, ep.buttonGrabObject, () => ConfirmExtraction(ep));

                hold.Button = button;
            }
            catch (Exception ex)
            {
                ReportPatchError("EnsureButton", ex);
            }
        }

        private static GameObject EnsureStandTemplate(ExtractionPoint ep)
        {
            if (_standTemplate != null)
                return _standTemplate;

            Transform station = _shopStation?.GetValue(ep) as Transform;
            if (station == null && ep != null)
                station = ep.transform.Find("Scale/Shop Station");

            if (station == null)
            {
                ReportReason("this extraction point has no Shop Station and none was cached yet");
                return null;
            }

            GameObject clone = UnityEngine.Object.Instantiate(station.gameObject);
            clone.name = "ExtractionConfirmTemplate";
            clone.SetActive(false);

            clone.transform.position = new Vector3(0f, -5000f, 0f);
            UnityEngine.Object.DontDestroyOnLoad(clone);

            _standTemplate = clone;
            return _standTemplate;
        }

        private static void Neutralise(Transform root)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                foreach (MonoBehaviour behaviour in child.GetComponents<MonoBehaviour>())
                    behaviour.enabled = false;

                foreach (AudioSource audio in child.GetComponents<AudioSource>())
                    audio.enabled = false;
            }
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
                return null;

            foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
            {
                if (candidate != null && candidate.name == name)
                    return candidate;
            }

            return null;
        }

        private static void ReportReason(string reason)
        {
            if (ReportedReasons.Add(reason))
                Log?.LogWarning("No confirm button: " + reason);
        }

        private static void ApplyHoldVisuals(ExtractionPoint ep, HoldState hold)
        {
            if (hold.TextApplied)
                return;

            try
            {
                hold.TubeTextOriginal = _tubeTextString.GetValue(ep) as string;
                hold.TubeColorOriginal = (Color)_tubeTextColor.GetValue(ep);
                hold.TextApplied = true;

                SetTubeText(ep, HoldText, HoldColor);

                StaticGrabObject grab = ep.buttonGrabObject;
                if (grab != null)
                {
                    hold.ButtonHintOriginal = grab.hoverText;
                    grab.hoverText = ButtonHint;
                }

                if (SemiFunc.IsMultiplayer())
                    ExtractionConfirmPlugin.Instance?.EnsureNetworkHook();
            }
            catch (Exception ex)
            {
                hold.TextApplied = false;
                ReportPatchError("ApplyHoldVisuals", ex);
            }
        }

        private static void RestoreTubeText(ExtractionPoint ep, HoldState hold)
        {
            if (hold == null || !hold.TextApplied)
                return;

            hold.TextApplied = false;

            try
            {
                if (hold.TubeTextOriginal != null)
                    SetTubeText(ep, hold.TubeTextOriginal, hold.TubeColorOriginal);

                if (hold.ButtonHintOriginal != null && ep.buttonGrabObject != null)
                    ep.buttonGrabObject.hoverText = hold.ButtonHintOriginal;

                hold.ButtonHintOriginal = null;
            }
            catch (Exception ex)
            {
                ReportPatchError("RestoreTubeText", ex);
            }
        }

        private static void SetTubeText(ExtractionPoint ep, string text, Color color)
        {
            _tubeScreenTextChange.Invoke(ep, new object[] { text ?? string.Empty, color });
        }

        private static HoldState Get(ExtractionPoint ep)
        {
            if (Holds.Count > 16)
                PruneDestroyed();

            HoldState hold;
            if (!Holds.TryGetValue(ep, out hold))
            {
                hold = new HoldState();
                Holds.Add(ep, hold);
            }

            return hold;
        }

        private static void PruneDestroyed()
        {
            List<ExtractionPoint> dead = null;

            foreach (KeyValuePair<ExtractionPoint, HoldState> pair in Holds)
            {
                if (pair.Key == null)
                {
                    if (dead == null)
                        dead = new List<ExtractionPoint>();
                    dead.Add(pair.Key);
                }
            }

            if (dead == null)
                return;

            foreach (ExtractionPoint key in dead)
                Holds.Remove(key);
        }

        public static int RequiredHaul(ExtractionPoint ep)
        {
            if (_hostSettingKnown && SemiFunc.IsMultiplayer() && !SemiFunc.IsMasterClient())
                return _hostUsesAuto ? ep.haulGoal : _hostAmount;

            bool auto = Settings.ValidationAuto.Value && Settings.ValidationAmount.Value == 0f;

            if (auto)
                return ep.haulGoal;

            return Mathf.Clamp(Mathf.RoundToInt(Settings.ValidationAmount.Value), 0, 100000);
        }

        public static bool IsRequirementMet(ExtractionPoint ep)
        {
            try
            {
                int current = CurrentHaul(ep);
                return current >= RequiredHaul(ep);
            }
            catch (Exception ex)
            {
                ReportPatchError("IsRequirementMet", ex);
                return true;
            }
        }

        private static int CurrentHaul(ExtractionPoint ep)
        {
            if (_haulCurrent != null)
            {
                object value = _haulCurrent.GetValue(ep);
                if (value != null)
                    return Convert.ToInt32(value);
            }

            object director = _roundDirectorInstance?.GetValue(null);
            if (director != null)
            {
                object value = _roundCurrentHaul?.GetValue(director);
                if (value != null)
                    return Convert.ToInt32(value);
            }

            return 0;
        }

        private static void DenyPress(ExtractionPoint ep)
        {
            try
            {
                _buttonDenyLerp?.SetValue(ep, 0f);
                _buttonDenyActive?.SetValue(ep, true);

                if (ep.button != null && ep.buttonDenyMaterial != null)
                    ep.button.material = ep.buttonDenyMaterial;

                if (ep.buttonLight != null)
                    ep.buttonLight.enabled = true;

                if (ep.soundCancel != null)
                    ep.soundCancel.Play(ep.transform.position);

                HoldState hold = Get(ep);
                if (hold.Button != null)
                    hold.Button.Flash();

                Log?.LogInfo(
                    $"Denied: haul {CurrentHaul(ep)} of {RequiredHaul(ep)} required.");
            }
            catch (Exception ex)
            {
                ReportPatchError("DenyPress", ex);
            }
        }

        private static bool _hostSettingKnown;
        private static bool _hostUsesAuto = true;
        private static int _hostAmount;
        private static float _nextValidationPush;

        private static void PushValidationSetting()
        {
            if (Time.unscaledTime < _nextValidationPush)
                return;

            _nextValidationPush = Time.unscaledTime + 5f;

            try
            {
                bool auto = Settings.ValidationAuto.Value && Settings.ValidationAmount.Value == 0f;
                int amount = Mathf.Clamp(Mathf.RoundToInt(Settings.ValidationAmount.Value), 0, 100000);

                _hostUsesAuto = auto;
                _hostAmount = amount;
                _hostSettingKnown = true;

                PhotonNetwork.RaiseEvent(
                    ValidationEventCode,
                    new object[] { auto ? "Auto" : "Fixed", amount },
                    new RaiseEventOptions(),
                    SendOptions.SendReliable);
            }
            catch (Exception ex)
            {
                ReportPatchError("PushValidationSetting", ex);
            }
        }

        private static void ApplyHostSetting(object[] payload)
        {
            if (payload == null || payload.Length < 2)
                return;

            _hostUsesAuto = Convert.ToString(payload[0]).Trim()
                                .Equals("Auto", StringComparison.OrdinalIgnoreCase);
            _hostAmount = Convert.ToInt32(payload[1]);
            _hostSettingKnown = true;

            Log?.LogInfo($"Using the host's validation setting: "
                         + (_hostUsesAuto ? "Auto" : "Fixed " + _hostAmount));
        }

        private static bool RaiseConfirmRequest(ExtractionPoint ep)
        {
            PhotonView view = ep.GetComponent<PhotonView>();
            if (view == null)
            {
                Log?.LogWarning("Extraction point has no PhotonView; cannot reach the host");
                return false;
            }

            PhotonNetwork.RaiseEvent(
                ConfirmEventCode,
                new object[] { view.ViewID },
                new RaiseEventOptions(),
                SendOptions.SendReliable);

            return true;
        }

        public static void OnPhotonEvent(EventData eventData)
        {
            try
            {
                if (eventData == null)
                    return;

                if (eventData.Code == ValidationEventCode)
                {
                    ApplyHostSetting(eventData.CustomData as object[]);
                    return;
                }

                if (eventData.Code != ConfirmEventCode)
                    return;

                if (!PhotonNetwork.IsMasterClient)
                    return;

                object[] payload = eventData.CustomData as object[];
                if (payload == null || payload.Length < 1)
                    return;

                int viewId = Convert.ToInt32(payload[0]);
                PhotonView view = PhotonView.Find(viewId);
                if (view == null)
                    return;

                ExtractionPoint ep = view.GetComponent<ExtractionPoint>();
                if (ep == null || !IsHolding(ep))
                    return;

                Get(ep).Confirmed = true;
                Log?.LogInfo("Confirmation received from a player - countdown starting");
            }
            catch (Exception ex)
            {
                ReportPatchError("OnPhotonEvent", ex);
            }
        }

        public static void ReportPatchError(string where, Exception ex)
        {
            if (ReportedErrors.Add(where))
                Log?.LogError($"Patch error in {where}: {ex}");
        }
    }
}