using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

public class SawRotate : MonoBehaviour
{
    [ReadOnly, SerializeField] private bool isRotating = true;
    [SerializeField] private float rotateSpeed = 100f;
    [SerializeField] private Vector3 rotateAxis = Vector3.forward;

    private void Update()
    {
        if (!isRotating) return;

        transform.Rotate(rotateAxis, rotateSpeed * Time.deltaTime);
    }

    public void SetRotating(bool isRot) => isRotating = isRot;

}
