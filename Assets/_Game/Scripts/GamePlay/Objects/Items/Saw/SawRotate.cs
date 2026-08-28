using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

public class SawRotate : MonoBehaviour
{
    [ReadOnly, SerializeField] private bool isRotating = true;
    [SerializeField] private float rotateSpeed = 100f;
    [SerializeField] private Vector3 rotateAxis = Vector3.forward;

    public static readonly List<SawRotate> ActiveSaws = new List<SawRotate>();

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
        if (!isRotating) return;

        transform.Rotate(rotateAxis, rotateSpeed * dt);
    }

    public void SetRotating(bool isRot) => isRotating = isRot;

}
