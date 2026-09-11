using System.Collections;
using System.Collections.Generic;
using GamePlay.Effects;
using UnityEngine;

public class VfxScalerWithColor : VfxScaler
{

    [SerializeField] private ParticleSystem[] targetParticle;
    [SerializeField] private Color[] particleColor = new Color[0];
    private Color _lastColor;
    private bool _hasAppliedColor;

    public override void ScaleWithColor(float multiplier = 1f, int colorIndex = 0)
    {
        Scale(multiplier);
        SetColor(colorIndex);
    }

    public void SetColor(int colorIndex)
    {
        if (colorIndex < 0 || colorIndex >= particleColor.Length) return;
        Color color = particleColor[colorIndex];
        ApplyColor(color);
    }

    public void ApplyColor(Color color)
    {
        if (targetParticle == null || targetParticle.Length == 0) return;
        if (_hasAppliedColor && _lastColor == color) return;

        _lastColor = color;
        _hasAppliedColor = true;

        for (int i = 0; i < targetParticle.Length; i++)
        {
            var ps = targetParticle[i];
            if (ps == null) continue;

            var main = ps.main;
            main.startColor = new ParticleSystem.MinMaxGradient(color);
        }
    }
}
