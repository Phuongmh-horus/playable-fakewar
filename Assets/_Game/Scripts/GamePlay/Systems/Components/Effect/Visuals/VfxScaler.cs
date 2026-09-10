using System;
using UnityEngine;

namespace GamePlay.Effects
{
    /// <summary>
    /// Scale từng nhóm ParticleSystem theo cấu hình <see cref="ParticleScaleData"/>:
    ///  - Mỗi entry có <see cref="ParticleScaleData.Particles"/>, <see cref="ParticleScaleData.ScaleMode"/> và <see cref="ParticleScaleData.ScaleFactor"/> riêng.
    ///  - Gọi <see cref="Scale()"/> để áp dụng, hoặc <see cref="Scale(float)"/> với multiplier thêm vào.
    /// </summary>
    public class VfxScaler : MonoBehaviour
    {
        [SerializeField] private ParticleScaleData[] particleScaleData;

        // Cache [dataIndex][particleIndex]
        private OriginalParticleData[][] _originalData;

        // ──────────────────────────────────────────────
        private struct OriginalParticleData
        {
            public float                      ShapeRadius;
            public ParticleSystem.MinMaxCurve StartSize;
            public ParticleSystem.MinMaxCurve StartSizeX;
            public ParticleSystem.MinMaxCurve StartSizeY;
            public ParticleSystem.MinMaxCurve StartSizeZ;
            public ParticleSystem.MinMaxCurve EmissionRate;
        }

        // ──────────────────────────────────────────────
        private void Awake() => CacheOriginals();

        private void CacheOriginals()
        {
            if (particleScaleData == null) return;

            _originalData = new OriginalParticleData[particleScaleData.Length][];

            for (int i = 0; i < particleScaleData.Length; i++)
            {
                var data = particleScaleData[i];
                if (data?.Particles == null) continue;

                _originalData[i] = new OriginalParticleData[data.Particles.Length];

                for (int j = 0; j < data.Particles.Length; j++)
                {
                    var ps = data.Particles[j];
                    if (ps == null) continue;

                    var main  = ps.main;
                    var shape = ps.shape;

                    _originalData[i][j] = new OriginalParticleData
                    {
                        ShapeRadius  = shape.radius,
                        StartSize    = main.startSize,
                        StartSizeX   = main.startSizeX,
                        StartSizeY   = main.startSizeY,
                        StartSizeZ   = main.startSizeZ,
                        EmissionRate = ps.emission.rateOverTime,
                    };
                }
            }
        }

        // ──────────────────────────────────────────────
        /// <summary>Áp dụng scale theo <c>ScaleFactor</c> của từng entry, nhân thêm <paramref name="multiplier"/>.</summary>
        public void Scale(float multiplier = 1f)
        {
            if (_originalData == null) CacheOriginals();

            for (int i = 0; i < particleScaleData.Length; i++)
            {
                var data = particleScaleData[i];
                if (data?.Particles == null || _originalData[i] == null) continue;

                float finalScale = data.ScaleFactor * multiplier;

                for (int j = 0; j < data.Particles.Length; j++)
                {
                    var ps = data.Particles[j];
                    if (ps == null) continue;

                    ApplyScale(ps, _originalData[i][j], data.ScaleMode, finalScale);
                }
            }
        }

        private static void ApplyScale(ParticleSystem ps, in OriginalParticleData orig, ParticleScaleMode mode, float scale)
        {
            if ((mode & ParticleScaleMode.ShapeRadius) != 0)
            {
                var shape = ps.shape;
                if (shape.enabled)
                    shape.radius = orig.ShapeRadius * scale;
            }

            var main = ps.main;

            if ((mode & ParticleScaleMode.StartSize3D) != 0)
            {
                main.startSizeX = ScaleCurve(orig.StartSizeX, scale);
                main.startSizeY = ScaleCurve(orig.StartSizeY, scale);
                main.startSizeZ = ScaleCurve(orig.StartSizeZ, scale);
            }
            else if ((mode & ParticleScaleMode.StartSize) != 0)
            {
                main.startSize = ScaleCurve(orig.StartSize, scale);
            }

            if ((mode & ParticleScaleMode.EmissionRate) != 0)
            {
                var emission = ps.emission;
                emission.rateOverTime = ScaleCurve(orig.EmissionRate, scale);
            }
        }

        private static ParticleSystem.MinMaxCurve ScaleCurve(ParticleSystem.MinMaxCurve curve, float scale)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    return new ParticleSystem.MinMaxCurve(curve.constant * scale);

                case ParticleSystemCurveMode.TwoConstants:
                    return new ParticleSystem.MinMaxCurve(curve.constantMin * scale, curve.constantMax * scale);

                case ParticleSystemCurveMode.Curve:
                case ParticleSystemCurveMode.TwoCurves:
                    var scaled = curve;
                    scaled.curveMultiplier = curve.curveMultiplier * scale;
                    return scaled;

                default:
                    return curve;
            }
        }
        
        public virtual void ScaleWithColor(float multiplier = 1f, int colorIndex = 0)
        {
            
        }
    }

    [Serializable]
    public class ParticleScaleData
    {
        public ParticleSystem[] Particles;
        public ParticleScaleMode ScaleMode;
        [Min(0f)] public float ScaleFactor = 1f;
    }

    [Flags]
    public enum ParticleScaleMode
    {
        None        = 0,
        ShapeRadius = 1 << 1,
        StartSize   = 1 << 2,
        StartSize3D = 1 << 3,
        EmissionRate  = 1 << 4,
    }
}
