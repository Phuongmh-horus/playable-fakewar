using UnityEngine;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// Bridge component replacing a Transform attached to a bone (e.g. Weapon, Shield, Helmet).
    /// Synchronizes local position and rotation from pre-baked VAT Socket transform arrays.
    /// </summary>
    public class VAT_ObjectMesh : MonoBehaviour
    {
        [SerializeField] private string _socketName = "RightHand";
        [SerializeField] private VAT_RenderComponent _animatorBridge;

        private VATSocketTransformData _socketData;
        private Transform _cachedTransform;

        public string SocketName => _socketName;

        private void Awake()
        {
            _cachedTransform = transform;
            if (_animatorBridge == null)
            {
                _animatorBridge = GetComponentInParent<VAT_RenderComponent>();
            }
        }

        private void Start()
        {
            BindSocketData();
        }

        public void BindSocketData()
        {
            if (_animatorBridge != null)
            {
                VATAssetDataSO assetData = _animatorBridge.VatAssetData;
                if (assetData != null)
                {
                    _socketData = assetData.GetSocket(_socketName);
                }
            }
        }

        public void SynchronizeFrame(int currentFrameIndex)
        {
            if (_socketData == null || _socketData.LocalPositions == null || _socketData.LocalPositions.Length == 0)
            {
                return;
            }

            int validFrame = Mathf.Clamp(currentFrameIndex, 0, _socketData.LocalPositions.Length - 1);
            _cachedTransform.localPosition = _socketData.LocalPositions[validFrame];
            _cachedTransform.localRotation = _socketData.LocalRotations[validFrame];
        }
    }
}
