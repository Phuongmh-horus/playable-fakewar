using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "CrowdEnhancedData", menuName = "GameVariables/Wheels/CrowdEnhancedData")]
public class CrowdEnhancedData : ScriptableObject
{
    [Header("Crowd Compactness")]
    [Tooltip("Số unit thường bắt đầu kích hoạt cơ chế co cụm")]
    [Min(1)] public int CompactStartCount = 10;
    [Tooltip("Số unit thường đạt mức co cụm tối đa")]
    [Min(2)] public int CompactFullCount = 60;
    [Tooltip("Hệ số spacing nhỏ nhất khi co cụm tối đa")]
    [Range(0.5f, 1f)] public float MinCrowdSpacingMultiplier = 0.78f;
    [Tooltip("Độ cong co cụm: >1 co chậm lúc đầu, <1 co nhanh lúc đầu")]
    [Range(0.5f, 3f)] public float CompactnessCurve = 1.25f;

    [Header("Unit Rearrangement")]
    [Tooltip("Thời gian chờ trước khi bắt đầu sắp xếp lại đơn vị")]
    public float CrowdRearrangeDelay = 0.25f;
    [Tooltip("Thời gian di chuyển khi sắp xếp lại đơn vị")]
    public float RearrangeMoveDuration = 0.25f;
    [Tooltip("Thời gian trễ giữa các đơn vị khi sắp xếp lại")]
    public float RearrangeStaggerPerUnit = 0.01f;
    [Tooltip("Số lượng đơn vị tối thiểu để kích hoạt sắp xếp lại")]
    public int MinUnitsForRearrangement = 4;
}
