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
    /// <summary>
    /// Holds the extraction before the 3-2-1 countdown until the physical button is pressed.
    ///
    /// Vanilla flow this sits in:
    ///   Idle --physical press--> Active --haulGoal reached--> Success [--Surplus-->] Warning --> Extracting
    ///
    /// THE BUTTON
    /// ----------
    /// The confirm button is the extraction point's own button. That is not a shortcut, it is what
    /// the assets say the button already is: the shop's "Shop Button" and the extraction point's
    /// "Button" are the same prefab object - identical component set (Transform, MeshFilter,
    /// BoxCollider and three MonoBehaviours), identical mesh ("Button"), identical scale. The shop
    /// does not have its own kind of button; it has the same button standing on a counter
    /// ("Meshtownusa" + "Cube (1)" + "Extraction Point Side Button") that you pay to press.
    ///
    /// So what changes between the shop and a level is only what the button DOES. In the shop it
    /// buys. Here it extracts - which is exactly what the extraction point's own button does in a
    /// level, so it is used as-is: no cloning, no placement, no template, nothing to go missing on
    /// a map whose extraction point has no shop station.
    ///
    /// One thing has to be undone for it: the game switches the button off the moment the point
    /// leaves Idle (ExtractionPoint.ButtonToggle sets buttonGrabObject.enabled = false unless the
    /// state is Idle), so while the extraction is being held the mod turns the button back on.
    /// </summary>
    internal static class ConfirmGate
    {
        /// <summary>Custom Photon event code used to forward a non-host button press to the host.</summary>
        public const byte ConfirmEventCode = 177;

        /// <summary>Same orange the game uses for the "READY" tube screen text.</summary>
        private static readonly Color HoldColor = new Color(1f, 0.5f, 0f);

        /// <summary>Guards against the OnClick hook and the grab watch firing on the same press.</summary>
        private const float ConfirmDebounce = 0.35f;

        private static readonly HashSet<string> ReportedErrors = new HashSet<string>();

        /// <summary>Per extraction point state: what the hold changed, and when it was confirmed.</summary>
        private static readonly Dictionary<ExtractionPoint, HoldState> Holds = new Dictionary<ExtractionPoint, HoldState>();

        private static FieldInfo _currentState;
        private static FieldInfo _isShop;
        private static FieldInfo _shopStation;
        private static FieldInfo _tubeTextString;
        private static FieldInfo _tubeTextColor;
        private static FieldInfo _buttonOriginalMaterial;
        private static FieldInfo _grabbedStaticGrabObject;
        private static MethodInfo _tubeScreenTextChange;

        /// <summary>
        /// The game's own list of started extraction points (RoundDirector.extractionPointList).
        /// Read for the log only: it is the one count nobody can argue with, because the game fills
        /// it from ExtractionPoint.Start.
        /// </summary>
        private static FieldInfo _roundDirectorInstance;
        private static FieldInfo _extractionPointList;

        /// <summary>Last thing Pump() reported, so the log only changes when the situation does.</summary>
        private static string _lastReport = string.Empty;

        /// <summary>
        /// A spare, inactive copy of the shop's purchase stand, kept out of sight in a
        /// DontDestroyOnLoad scene.
        ///
        /// Not every extraction point prefab carries one - the shop station only exists on some -
        /// so the first stand the mod sees becomes the template and every later extraction point
        /// is built from it. Without that, a level whose extraction points have no station of
        /// their own would silently get nothing.
        /// </summary>
        private static GameObject _standTemplate;

        /// <summary>Reasons a button was not built, logged once each so a silent no-op is visible.</summary>
        private static readonly HashSet<string> ReportedReasons = new HashSet<string>();

        /// <summary>Set once the first extraction point has been seen, to prove the code runs at all.</summary>
        private static bool _announcedFirstPoint;

        private sealed class HoldState
        {
            public bool Confirmed;
            public bool TextApplied;
            public bool Announced;
            public bool Ticking;
            public float LastConfirmTime = -99f;
            public string TubeTextOriginal;
            public Color TubeColorOriginal;
            public string ButtonHintOriginal;
            public ConfirmButton Button;
            public Transform Anchor;
        }

        private static ManualLogSource Log => ExtractionConfirmPlugin.Log;
        private static ConfirmSettings Settings => ExtractionConfirmPlugin.Settings;

        /// <summary>Resolves the private game members this mod depends on. False = do not patch.</summary>
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
                _tubeScreenTextChange = AccessTools.Method(extractionPointType, "TubeScreenTextChange");

                Type grabberType = AccessTools.TypeByName("PhysGrabber");
                _grabbedStaticGrabObject = grabberType != null
                    ? AccessTools.Field(grabberType, "grabbedStaticGrabObject")
                    : null;

                Type roundDirectorType = AccessTools.TypeByName("RoundDirector");
                if (roundDirectorType != null)
                {
                    _roundDirectorInstance = AccessTools.Field(roundDirectorType, "instance");
                    _extractionPointList = AccessTools.Field(roundDirectorType, "extractionPointList");
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

        // ------------------------------------------------------------------ gate

        /// <summary>
        /// Prefix helper for ExtractionPoint.StateSet: returns true when the vanilla transition
        /// must be skipped, i.e. while the countdown is being held.
        /// </summary>
        public static bool ShouldSkipTransition(ExtractionPoint ep, ExtractionPoint.State newState)
        {
            if (ep == null || _currentState == null || Settings == null || !Settings.Enabled.Value)
                return false;

            HoldState hold = Get(ep);

            if (newState == ExtractionPoint.State.Warning && IsHolding(ep))
            {
                if (hold.Confirmed)
                {
                    hold.Confirmed = false;
                    RestoreTubeText(ep, hold); // the countdown owns the screen again
                    return false;             // confirmed: let the 3-2-1 countdown start
                }

                ApplyHoldVisuals(ep, hold); // idempotent, re-asserts the hint
                return true;                // held: keep the extraction frozen
            }

            // Any other transition (Extracting, Complete, Cancel, ...) ends the hold.
            if (hold.Confirmed)
                hold.Confirmed = false;

            RestoreTubeText(ep, hold);
            return false;
        }

        /// <summary>
        /// Prefix helper for ExtractionPoint.OnClick: returns true when the vanilla press should
        /// be consumed as a confirmation.
        /// </summary>
        public static bool HandleButtonPress(ExtractionPoint ep)
        {
            if (ep == null || _currentState == null || Settings == null || !Settings.Enabled.Value)
                return false;

            if (!IsHolding(ep))
                return false; // not holding: let the vanilla button logic run untouched

            ConfirmExtraction(ep);
            return true; // consume the press; vanilla would early-return here anyway
        }

        /// <summary>Applies a confirmation from the confirm button (or the original button).</summary>
        public static void ConfirmExtraction(ExtractionPoint ep)
        {
            if (ep == null || _currentState == null)
                return;

            HoldState hold = Get(ep);

            // Both the OnClick hook and the grab watch can see the same physical press.
            if (Time.time - hold.LastConfirmTime < ConfirmDebounce)
                return;

            hold.LastConfirmTime = Time.time;

            if (!IsHolding(ep))
                return; // not holding: the vanilla press already did the right thing

            if (SemiFunc.IsMultiplayer() && SemiFunc.IsNotMasterClient())
            {
                // State changes are host-authoritative, so ask the host to start the countdown.
                if (RaiseConfirmRequest(ep))
                    Log?.LogInfo("Confirmation press - requested the countdown from the host");
                else
                    hold.Confirmed = true; // best effort fallback
            }
            else
            {
                hold.Confirmed = true;
                Log?.LogInfo("Extraction confirmed - countdown starting");
            }
        }

        /// <summary>
        /// True while this point is waiting for the countdown, i.e. exactly in the two states the
        /// game passes through on its way into Warning.
        ///
        /// Warning itself is deliberately NOT included. The hold ends the moment the countdown is
        /// released: from there on the game owns the tube screen, the lights and the button, and
        /// re-asserting the hold visuals in Warning would put "CONFIRM" back on the screen for the
        /// whole countdown and rewrite it every frame.
        /// </summary>
        public static bool IsHolding(ExtractionPoint ep)
        {
            if (ep == null || IsShop(ep))
                return false;

            ExtractionPoint.State state = (ExtractionPoint.State)_currentState.GetValue(ep);
            return state == ExtractionPoint.State.Success || state == ExtractionPoint.State.Surplus;
        }

        /// <summary>True while the local player is holding down the given grabbable button.</summary>
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

        // ------------------------------------------------------------------ per frame

        /// <summary>
        /// Keeps the confirm button armed while the extraction is held, and watches for the press.
        ///
        /// The game turns the button off as soon as the point leaves Idle
        /// (ExtractionPoint.ButtonToggle: "if (currentState != State.Idle) buttonGrabObject.enabled
        /// = false"), so without this the only button in the level cannot be pressed at exactly the
        /// moment it is needed.
        /// </summary>
        public static void Tick(ExtractionPoint ep)
        {
            if (ep == null || Settings == null || !Settings.Enabled.Value)
                return;

            HoldState hold = Get(ep);

            if (!hold.Ticking)
            {
                // Proves the Update postfix is attached to this instance, independently of whether
                // the Start prefix ever fired.
                hold.Ticking = true;
                Log?.LogInfo($"Tick: '{ep.name}' is alive (state {_currentState.GetValue(ep)}), mod attached.");
            }

            EnsureButton(ep);

            if (!IsHolding(ep))
            {
                if (hold.Confirmed)
                    hold.Confirmed = false;

                RestoreTubeText(ep, hold);
                return;
            }

            ApplyHoldVisuals(ep, hold);
            ArmButton(ep);
            WatchPress(ep, hold);
        }

        /// <summary>Puts the extraction point's own button back into its pressed-this-is-live look.</summary>
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

        /// <summary>
        /// Confirms on the grab itself. The OnClick hook normally gets there first, but reading the
        /// grab directly means the mod still works if the game's click wiring ever changes.
        /// </summary>
        private static void WatchPress(ExtractionPoint ep, HoldState hold)
        {
            if (!Settings.SpawnButton.Value || hold.Confirmed)
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

        /// <summary>
        /// Fallback discovery, used by ModRunner so the mod works even if a patch fails to attach.
        /// Also the only place that reports what the level actually contains.
        /// </summary>
        public static void Pump()
        {
            if (Settings == null || !Settings.Enabled.Value || _currentState == null)
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

            int holding = 0;
            int usable = 0;

            foreach (ExtractionPoint ep in points)
            {
                if (ep == null)
                    continue;

                if (ep.buttonGrabObject != null)
                    usable++;

                if (IsHolding(ep))
                    holding++;

                EnsureButton(ep);
                Tick(ep);
            }

            Report(points.Length, usable, holding);
        }

        /// <summary>Logs the level contents, but only when something about them changed.</summary>
        private static void Report(int points, int usableButtons, int holding)
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            string line = $"scene '{scene}': {points} extraction point(s) in the scene, "
                          + $"{usableButtons} with a grabbable button, {holding} holding, "
                          + $"{StartedPointCount()} started by the game";

            if (line == _lastReport)
                return;

            _lastReport = line;
            Log?.LogInfo(line);
        }

        /// <summary>
        /// A scene finished loading. This one line settles whether the level ever loaded, which is
        /// otherwise the thing the whole log cannot answer.
        /// </summary>
        public static void LogSceneLoaded(string sceneName)
        {
            // Force the next scan to report, even if the numbers are identical to the last scene's.
            _lastReport = string.Empty;
            Log?.LogInfo($"Scene loaded: '{sceneName}' - the mod is running inside it.");
        }

        /// <summary>How many extraction points the game itself has started, or -1 if unknown.</summary>
        private static int StartedPointCount()
        {
            try
            {
                object director = _roundDirectorInstance?.GetValue(null);
                if (director == null)
                    return -1;

                var list = _extractionPointList?.GetValue(director) as System.Collections.ICollection;
                return list?.Count ?? -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Prefix helper for ExtractionPoint.Start. Nothing has to be built here any more, but it is
        /// the earliest point at which an extraction point exists, so it is where the mod says what
        /// it can see - which is the difference between "it did not work" and "it never ran".
        /// </summary>
        public static void PreparePoint(ExtractionPoint ep)
        {
            try
            {
                if (ep == null || Settings == null || !Settings.Enabled.Value)
                    return;

                if (!_announcedFirstPoint)
                {
                    _announcedFirstPoint = true;
                    Log?.LogInfo("ExtractionPoint.Start reached - the mod is attached to a live extraction point.");
                }

                HoldState hold = Get(ep);
                if (hold.Announced)
                    return;

                hold.Announced = true;

                MeshFilter filter = ep.button != null ? ep.button.GetComponent<MeshFilter>() : null;
                Mesh mesh = filter != null ? filter.sharedMesh : null;

                Log?.LogInfo(
                    $"Confirm button ready at {ep.transform.position}: "
                    + $"button mesh '{(mesh != null ? mesh.name : "none")}', "
                    + $"grabbable={(ep.buttonGrabObject != null)}, "
                    + $"shop={SemiFunc.RunIsShop()}");

                // Build it here, while vanilla Start is still one instruction away from destroying
                // the shop station this is cloned from.
                EnsureButton(ep);
            }
            catch (Exception ex)
            {
                ReportPatchError("PreparePoint", ex);
            }
        }

        // ------------------------------------------------------------------ spawning

        /// <summary>
        /// Puts the shop's own button where the regular one is.
        ///
        /// The assets say the shop's button and the extraction point's button are the same prefab
        /// object (same component set, same "Button" mesh), so there is nothing to swap visually -
        /// what this does is move the shop's button head out of its counter and onto the extraction
        /// point's button, then hide the regular one. The result is a single button at the
        /// extraction point, lit orange and squash-animating, and it is the shop's button.
        ///
        /// The stand's own grab component is left disabled: a cloned StaticGrabObject has no
        /// PhotonView and would throw in multiplayer. The press is read from the extraction point's
        /// own button, which physically sits underneath the clone in the same place.
        /// </summary>
        public static void EnsureButton(ExtractionPoint ep)
        {
            if (ep == null || Settings == null || !Settings.Enabled.Value || !Settings.SpawnButton.Value)
                return;

            HoldState hold = Get(ep);
            if (hold.Button != null)
                return; // already built for this extraction point

            if (SemiFunc.RunIsShop())
                return; // the real shop keeps its own working purchase button

            GameObject template = EnsureStandTemplate(ep);
            if (template == null)
                return; // EnsureStandTemplate already said why

            try
            {
                // The station is authored at the extraction point's own origin, so parenting the
                // clone there reproduces the shop's layout exactly, whatever the machine's rotation.
                GameObject clone = UnityEngine.Object.Instantiate(template, ep.transform, false);
                clone.name = "ExtractionConfirmStand";

                // The template is kept inactive, and Instantiate copies activeSelf - so without this
                // the spawn would be invisible and its MonoBehaviour would never tick. The template
                // itself stays inactive, which is what keeps it out of play.
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

                // PLACEMENT. Two traps here, both of which look like "it spawned in the wrong place":
                //
                // 1. The station is authored at the extraction point's origin but its button head
                //    sits 2.74 m to the side, on top of the counter, so the station root is moved by
                //    the negative of the head's offset inside it.
                // 2. The extraction point's button is not a child of the extraction point. It is
                //    under "Extraction Tube", which vanilla Start drops from y 5.59 to 0 and which
                //    rises again when the point opens - so anything parented there flies with it.
                Vector3 target = new Vector3(
                    Settings.PlaceX.Value,
                    Settings.PlaceY.Value,
                    Settings.PlaceZ.Value);

                // THE BUTTON ITSELF IS THE PRESS TARGET, and it is moved rather than replaced.
                //
                // A cloned StaticGrabObject cannot do this job: StaticGrabObject.Start does
                // GetComponent<PhotonView>() and then photonView.TransferOwnership(...) in
                // multiplayer - a null reference on a clone Photon never registered - and the grab
                // area beside it (PhysGrabObjectGrabArea) collects its colliders in its own Start,
                // so disabling it to stop it buying leaves the button with no grabbing zone at all.
                //
                // So the game's own button is moved instead. It keeps its collider, its grab object,
                // its networking and its press animation, and it works in singleplayer and
                // multiplayer with nothing added.
                //
                // It cannot simply be parked, because ExtractionPoint rewrites
                // buttonDenyTransform.localPosition every frame:
                //
                //   buttonDenyTransform.localPosition = new Vector3(0f, 0f, -0.06f * curve);
                //
                // A static anchor is therefore inserted between the extraction point and the deny
                // transform. The anchor never moves; the game's own nudge still happens, locally,
                // inside it.
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

                // The whole move rests on buttonDenyTransform actually being the Button's parent.
                // If a future update repoints it, the button would stay where it was and the mod
                // would look broken with nothing in the log - so say so loudly.
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

                // The shop's button is drawn over the game's own, which is what the player presses.
                ep.button.enabled = false;

                ConfirmButton button = head.gameObject.AddComponent<ConfirmButton>();
                button.Setup(ep, head, ep.buttonGrabObject, () => ConfirmExtraction(ep));

                hold.Button = button;
                Log?.LogInfo(
                    $"Confirm button spawned: shop head at {head.position}, "
                    + $"press target at {source.position}, "
                    + $"mesh drawn={IsDrawn(head)}, "
                    + $"grabbable={(ep.buttonGrabObject != null)}.");
            }
            catch (Exception ex)
            {
                ReportPatchError("EnsureButton", ex);
            }
        }

        /// <summary>
        /// Returns the shared shop stand, creating it from the first extraction point that has one.
        ///
        /// Must run inside the ExtractionPoint.Start prefix: vanilla Start destroys the whole
        /// shopStation hierarchy in any level that is not a shop, so after it returns there is
        /// nothing left to clone.
        /// </summary>
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

            // Parked far below the map and kept across scene changes: it is only ever a source to
            // instantiate from, never something the player can see or touch.
            clone.transform.position = new Vector3(0f, -5000f, 0f);
            UnityEngine.Object.DontDestroyOnLoad(clone);

            _standTemplate = clone;
            Log?.LogInfo($"Cached the shop stand as the confirm button template (head: {(FindDeep(clone.transform, "Shop Button") != null)}).");
            return _standTemplate;
        }

        /// <summary>
        /// Makes the cloned stand inert: every behaviour off, so nothing in it can buy anything, and
        /// the cloned grab components never run. Renderers, lights and colliders are left alone so
        /// the stand still looks like a stand and still stands on the floor.
        ///
        /// The stand is purely a picture: the press is handled by the extraction point's own button,
        /// which is moved to the stand rather than replaced by it.
        /// </summary>
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

        /// <summary>True when something in the subtree is actually being drawn.</summary>
        private static bool IsDrawn(Transform root)
        {
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                    return true;
            }

            return false;
        }

        /// <summary>Finds a descendant by name, at any depth, inactive objects included.</summary>
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

        /// <summary>Logs a reason for not building a button, once per distinct reason.</summary>
        private static void ReportReason(string reason)
        {
            if (ReportedReasons.Add(reason))
                Log?.LogWarning("No confirm button: " + reason);
        }

        // ------------------------------------------------------------------ tube text

        private static void ApplyHoldVisuals(ExtractionPoint ep, HoldState hold)
        {
            if (hold.TextApplied)
                return;

            try
            {
                hold.TubeTextOriginal = _tubeTextString.GetValue(ep) as string;
                hold.TubeColorOriginal = (Color)_tubeTextColor.GetValue(ep);
                hold.TextApplied = true;

                // Re-asserted every frame in case the game overwrites it, but only announced the first time:
                // the state machine re-enters Success more than once per extraction.
                bool announcing = !hold.TextApplied;

                SetTubeText(ep, Settings.HoldText.Value, HoldColor);

                // The button is the game's own, so its hover text is too. Saying what the press
                // does is the only instruction the player gets, so it is set for the hold and put
                // back afterwards.
                StaticGrabObject grab = ep.buttonGrabObject;
                if (grab != null)
                {
                    hold.ButtonHintOriginal = grab.hoverText;
                    grab.hoverText = Settings.ButtonHint.Value;
                }

                if (announcing)
                    Log?.LogInfo("Extraction held - press the confirm button to start the countdown");

                // A hold can only happen inside a live networked level, and the event hook is
                // installed here rather than at startup.
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

        // ------------------------------------------------------------------ plumbing

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

        // ------------------------------------------------------------------ multiplayer

        /// <summary>Asks the host (who owns the state machine) to accept the confirmation.</summary>
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

        /// <summary>Host side: apply a confirmation raised by another player.</summary>
        public static void OnPhotonEvent(EventData eventData)
        {
            try
            {
                if (eventData == null || eventData.Code != ConfirmEventCode)
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

        /// <summary>Logs each distinct failure once, so a glitch cannot spam the log.</summary>
        public static void ReportPatchError(string where, Exception ex)
        {
            if (ReportedErrors.Add(where))
                Log?.LogError($"Patch error in {where}: {ex}");
        }
    }
}