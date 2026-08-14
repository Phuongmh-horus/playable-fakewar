using UnityEngine;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// Test component to interactively trigger VAT animations via string names and FNV-1a hashes.
    /// Supports GUI buttons and key bindings for easy runtime debugging.
    /// </summary>
    [RequireComponent(typeof(VAT_RenderComponent))]
    public class VATTestingController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private VAT_RenderComponent _vatComponent;

        [Header("Transition Settings")]
        [SerializeField, Range(0f, 2f)] private float _crossFadeDuration = 0.15f;

        [Header("Quick Switch List (Auto-populated if empty)")]
        [SerializeField] private string[] _quickStates;

        // Runtime states exposed for the Custom Editor
        public VAT_RenderComponent VatComponent => _vatComponent;
        public float CrossFadeDuration => _crossFadeDuration;
        public string[] QuickStates => _quickStates;

        private void Reset()
        {
            _vatComponent = GetComponent<VAT_RenderComponent>();
            AutoPopulateClips();
        }

        private void Start()
        {
            if (_vatComponent == null)
            {
                _vatComponent = GetComponent<VAT_RenderComponent>();
            }

            if (_quickStates == null || _quickStates.Length == 0)
            {
                AutoPopulateClips();
            }
        }

        private void Update()
        {
            // Keyboard quick controls: alpha keys 1-9 to trigger clips
            if (_quickStates != null && _quickStates.Length > 0)
            {
                for (int i = 0; i < Mathf.Min(_quickStates.Length, 9); i++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                    {
                        bool shiftPressed = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                        if (shiftPressed)
                        {
                            // Shift + Key -> CrossFade
                            CrossFadeByName(_quickStates[i]);
                        }
                        else
                        {
                            // Key -> Play
                            PlayByName(_quickStates[i]);
                        }
                    }
                }
            }
        }

        public void AutoPopulateClips()
        {
            if (_vatComponent != null && _vatComponent.VatAssetData != null && _vatComponent.VatAssetData.Clips != null)
            {
                var clips = _vatComponent.VatAssetData.Clips;
                _quickStates = new string[clips.Count];
                for (int i = 0; i < clips.Count; i++)
                {
                    _quickStates[i] = clips[i].ClipName;
                }
            }
        }

        public void PlayByName(string stateName)
        {
            if (_vatComponent == null) return;
            Debug.Log($"[VATTester] Play State (String): '{stateName}'");
            _vatComponent.Play(stateName);
        }

        public void PlayByHash(string stateName)
        {
            if (_vatComponent == null) return;
            int hash = VATClipInfo.GenerateHash(stateName);
            Debug.Log($"[VATTester] Play State (Hash): '{stateName}' (hash: {hash})");
            _vatComponent.Play(hash);
        }

        public void CrossFadeByName(string stateName)
        {
            if (_vatComponent == null) return;
            Debug.Log($"[VATTester] CrossFade State (String): '{stateName}' over {_crossFadeDuration}s");
            _vatComponent.CrossFade(stateName, _crossFadeDuration);
        }

        public void CrossFadeByHash(string stateName)
        {
            if (_vatComponent == null) return;
            int hash = VATClipInfo.GenerateHash(stateName);
            Debug.Log($"[VATTester] CrossFade State (Hash): '{stateName}' (hash: {hash}) over {_crossFadeDuration}s");
            _vatComponent.CrossFade(hash, _crossFadeDuration);
        }
    }
}
