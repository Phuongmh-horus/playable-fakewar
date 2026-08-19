using System.Collections.Generic;
using UnityEngine;

public class CullingCell
{
    public int XIndex { get; set; }
    public int ZIndex { get; set; }

    public float MinX { get; set; }
    public float MaxX { get; set; }
    public float MinZ { get; set; }
    public float MaxZ { get; set; }

    public Vector3 Center { get; set; }
    public Vector3 Size { get; set; }

    public List<CullingObject> Objects { get; } = new List<CullingObject>();
    private bool _maxCullDistanceDirty = true;
    private float _maxCustomCullDistance;

    public void AddObject(CullingObject obj)
    {
        if (obj == null || Objects.Contains(obj)) return;
        Objects.Add(obj);
        _maxCullDistanceDirty = true;
    }

    public void RemoveObject(CullingObject obj)
    {
        if (!Objects.Remove(obj)) return;
        _maxCullDistanceDirty = true;
    }

    public float GetMaxCullDistance(float defaultCullDistance)
    {
        if (!_maxCullDistanceDirty)
        {
            return Mathf.Max(defaultCullDistance, _maxCustomCullDistance);
        }

        _maxCustomCullDistance = defaultCullDistance;
        for (int i = 0; i < Objects.Count; i++)
        {
            CullingObject obj = Objects[i];
            if (obj != null && obj.CustomCullDistance > _maxCustomCullDistance)
            {
                _maxCustomCullDistance = obj.CustomCullDistance;
            }
        }

        _maxCullDistanceDirty = false;
        return _maxCustomCullDistance;
    }

    public float GetSqrDistanceToPoint(float tx, float tz)
    {
        float dx = Mathf.Max(0f, Mathf.Max(MinX - tx, tx - MaxX));
        float dz = Mathf.Max(0f, Mathf.Max(MinZ - tz, tz - MaxZ));
        return dx * dx + dz * dz;
    }
}
