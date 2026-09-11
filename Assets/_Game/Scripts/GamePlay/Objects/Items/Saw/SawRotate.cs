using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

public class SawRotate : MonoBehaviour
{
    [ReadOnly, SerializeField] private bool isRotating = true;
    [SerializeField] private float rotateSpeed = 100f;
    [SerializeField] private Vector3 rotateAxis = Vector3.left;

    public static readonly List<SawRotate> ActiveSaws = new List<SawRotate>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        ActiveSaws.Clear();
    }

    public static void TickActiveSaws(float deltaTime)
    {
        for (int index = ActiveSaws.Count - 1; index >= 0; index--)
        {
            SawRotate saw = ActiveSaws[index];
            if (saw == null)
            {
                ActiveSaws.RemoveAt(index);
                continue;
            }

            saw.Tick(deltaTime);
        }
    }

    private void OnEnable()
    {
        ActiveSaws.Add(this);
    }

    private void OnDisable()
    {
        ActiveSaws.Remove(this);
    }

    public void Tick(float dt)
    {
        if (!isRotating || rotateAxis.sqrMagnitude <= 0.0001f) return;

        transform.Rotate(rotateAxis, rotateSpeed * dt);
    }

    public void SetRotating(bool isRot) => isRotating = isRot;

}
