using System;
using UnityEngine;

namespace ExtractionConfirm
{
    /// <summary>
    /// Drives the spawned confirm button: the shop's own button, moved onto the extraction point's
    /// button position so it replaces the regular one visually.
    ///
    /// The press is NOT read from the spawned object. The clone carries a StaticGrabObject, whose
    /// Start does `GetComponent<PhotonView>()` and then `photonView.TransferOwnership(...)` in
    /// multiplayer - a null reference on a clone that was never registered with Photon. So the
    /// clone's grab component is left disabled and the press is read from the extraction point's
    /// own button, which is what physically sits underneath it.
    ///
    /// The squash and glow use the extraction point's own animation curve, which is the same curve
    /// the game plays on its own buttons.
    /// </summary>
    internal sealed class ConfirmButton : MonoBehaviour
    {
        /// <summary>The squashing mesh inside the spawned button, by name.</summary>
        private const string VisualName = "Shop Button Visual";

        /// <summary>Same orange the game uses for the "READY" tube screen text.</summary>
        private static readonly Color LitColor = new Color(1f, 0.5f, 0f);

        private ExtractionPoint _extractionPoint;
        private StaticGrabObject _grabObject;
        private Transform _visual;
        private Vector3 _baseScale;
        private Material _material;
        private Action _onPress;

        private bool _wasPressed;
        private float _animEval;
        private bool _animating;

        /// <summary>Binds this button to the extraction point it confirms.</summary>
        public void Setup(ExtractionPoint extractionPoint, Transform head, StaticGrabObject grabObject, Action onPress)
        {
            _extractionPoint = extractionPoint;
            _grabObject = grabObject;
            _onPress = onPress;

            _visual = FindDeep(head, VisualName) ?? head;
            if (_visual == null)
                return;

            _baseScale = _visual.localScale;

            MeshRenderer renderer = _visual.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                // .material instantiates a private copy, so the shared shop material is untouched.
                _material = renderer.material;
                if (_material != null && _material.HasProperty("_EmissionColor"))
                    _material.SetColor("_EmissionColor", LitColor);
            }
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

        private void Update()
        {
            if (_extractionPoint == null)
                return;

            // The spawned button's own grab object in singleplayer; the extraction point's own
            // button in multiplayer, where the clone cannot be grabbed.
            bool pressed = ConfirmGate.IsLocallyGrabbed(_grabObject);
            if (pressed && !_wasPressed)
                Press();

            _wasPressed = pressed;
            Animate();
        }

        private void Press()
        {
            _animEval = 0f;
            _animating = true;

            try
            {
                if (_extractionPoint.soundButton != null)
                    _extractionPoint.soundButton.Play(_visual.position, 1f, 1f, 1f, 1f);
            }
            catch
            {
                // A missing sound must never cost the player the confirmation itself.
            }
        }

        /// <summary>Same squash-and-glow the game plays on its own button, using the game's curve.</summary>
        private void Animate()
        {
            if (!_animating || _extractionPoint == null)
                return;

            _animEval = Mathf.Clamp01(_animEval + Time.deltaTime * 2f);

            AnimationCurve curve = _extractionPoint.buttonPressAnimationCurve;
            float value = curve != null ? curve.Evaluate(_animEval) : _animEval;
            float squash = Mathf.Lerp(0.1f, value, 0.5f);

            if (_visual != null)
                _visual.localScale = new Vector3(_baseScale.x, _baseScale.y * squash, _baseScale.z);

            if (_material != null && _material.HasProperty("_EmissionColor"))
                _material.SetColor("_EmissionColor", Color.Lerp(LitColor, Color.white, value));

            if (_animEval >= 1f)
            {
                _animating = false;
                if (_visual != null)
                    _visual.localScale = _baseScale;
            }
        }
    }
}