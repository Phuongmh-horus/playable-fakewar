using UnityEngine;

namespace GamePlay.Items
{
    [DisallowMultipleComponent]
    public sealed class SoldierBallHintArrow : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer arrowRenderer;
        [SerializeField, Min(0f)] private float bobAmplitude = 0.35f;
        [SerializeField, Min(0f)] private float bobFrequency = 2.5f;
        [SerializeField, Min(1f)] private float glowScale = 1.14f;
        [SerializeField] private Color glowColor = new Color(1f, 0.8f, 0.15f, 0.32f);

        private Vector3 _baseLocalPosition;
        private Transform _glowTransform;

        private void Awake()
        {
            if (arrowRenderer == null)
            {
                arrowRenderer = GetComponent<SpriteRenderer>();
            }

            CreateGlow();
        }

        private void OnEnable()
        {
            _baseLocalPosition = transform.localPosition;
        }

        private void OnDisable()
        {
            transform.localPosition = _baseLocalPosition;
        }

        private void Update()
        {
            float offset = Mathf.Sin(Time.time * bobFrequency) * bobAmplitude;
            transform.localPosition = _baseLocalPosition + Vector3.up * offset;
        }

        private void CreateGlow()
        {
            if (arrowRenderer == null || _glowTransform != null)
            {
                return;
            }

            GameObject glowObject = new GameObject("Glow");
            _glowTransform = glowObject.transform;
            _glowTransform.SetParent(transform, false);
            _glowTransform.localScale = Vector3.one * glowScale;

            SpriteRenderer glowRenderer = glowObject.AddComponent<SpriteRenderer>();
            glowRenderer.sprite = arrowRenderer.sprite;
            glowRenderer.sharedMaterial = arrowRenderer.sharedMaterial;
            glowRenderer.color = glowColor;
            glowRenderer.sortingLayerID = arrowRenderer.sortingLayerID;
            glowRenderer.sortingOrder = arrowRenderer.sortingOrder - 1;
            glowRenderer.maskInteraction = arrowRenderer.maskInteraction;
        }
    }
}
