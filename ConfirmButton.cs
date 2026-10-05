using System;
using UnityEngine;

namespace ExtractionConfirm
{
    internal sealed class ConfirmButton : MonoBehaviour
    {
        private const string VisualName = "Shop Button Visual";

        private static readonly Color LitColor = new Color(1f, 0.5f, 0f);
        private static readonly Color DenyColor = new Color(1f, 0f, 0f, 1f);

        private ExtractionPoint _extractionPoint;
        private StaticGrabObject _grabObject;
        private Transform _visual;
        private Vector3 _baseScale;
        private Material _material;
        private Action _onPress;

        private bool _wasPressed;
        private float _animEval;
        private bool _animating;
        private float _flashTimer;

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
                _material = renderer.material;
                if (_material != null && _material.HasProperty("_EmissionColor"))
                    _material.SetColor("_EmissionColor", LitColor);
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

        private void Update()
        {
            if (_extractionPoint == null)
                return;

            bool pressed = ConfirmGate.IsLocallyGrabbed(_grabObject);
            if (pressed && !_wasPressed)
                Press();

            _wasPressed = pressed;
            Animate();
            TickFlash();
        }

        private void TickFlash()
        {
            if (_flashTimer <= 0f)
                return;

            _flashTimer -= Time.deltaTime;

            if (_flashTimer > 0f)
                return;

            if (_material != null && _material.HasProperty("_EmissionColor"))
                _material.SetColor("_EmissionColor", LitColor);
        }

        public void Flash()
        {
            _flashTimer = 0.7f;

            if (_material != null && _material.HasProperty("_EmissionColor"))
                _material.SetColor("_EmissionColor", DenyColor);
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
            }
        }

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