using System.Collections;
using UnityEngine;
using UnityEngine.UI;
public class PulseAnimationn : MonoBehaviour
{

    private Coroutine _pulseRoutine;

    // void Start()
    // {
    //     _pulseRoutine = StartCoroutine(PulseRoutine());
        
    // }
    
    private void OnEnable()
    {
        _pulseRoutine = StartCoroutine(PulseRoutine());
    }

    [SerializeField] private float buttonPulseDuration = 0.6f;
    [SerializeField] private float ctaOnlyPulseScale = 1.1f;
    [SerializeField] private float ctaOnlyStartScale = 0.9f;
    private IEnumerator PulseRoutine()
    {
        // transform.localRotation = Quaternion.identity;

        float duration = Mathf.Max(0.05f, buttonPulseDuration);
        float elapsed = 0f;

        while (true)
        {
            elapsed += Time.unscaledDeltaTime;

            float t = Mathf.PingPong(elapsed / duration, 1f);
            t = t * t * (3f - 2f * t); // SmoothStep

            float scale = Mathf.Lerp(
                ctaOnlyStartScale,
                ctaOnlyPulseScale,
                t);

            transform.localScale = Vector3.one * scale;

            yield return null;
        }
    }
}
