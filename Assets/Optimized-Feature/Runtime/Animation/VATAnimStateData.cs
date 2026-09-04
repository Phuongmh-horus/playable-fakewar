using System;
using UnityEngine;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// Structure class holding animation state transition data.
    /// Declared as class (not struct) to prevent GC allocations during Luna JS transpilation.
    /// </summary>
    public class VATAnimStateData
    {
        public string StateName;
        public int StateHash;
        public int StartFrame;
        public int EndFrame;
        public float FrameRate;
        public bool IsLooping;
        public float CurrentFrameNormalized;

        public VATAnimStateData(string stateName, int stateHash, int startFrame, int endFrame, float frameRate = 30f, bool isLooping = true)
        {
            StateName = stateName;
            StateHash = stateHash;
            StartFrame = startFrame;
            EndFrame = endFrame;
            FrameRate = frameRate;
            IsLooping = isLooping;
            CurrentFrameNormalized = 0f;
        }

        public int TotalFrames => Mathf.Max(1, EndFrame - StartFrame + 1);

        public int CalculateFrameIndex(float time)
        {
            float safeFrameRate = Mathf.Max(0.0001f, FrameRate);
            float totalTime = TotalFrames / safeFrameRate;
            float timeInClip = IsLooping ? Mathf.Repeat(time, totalTime) : Mathf.Clamp(time, 0f, totalTime);
            int frameOffset = Mathf.FloorToInt(timeInClip * safeFrameRate) % TotalFrames;
            return StartFrame + frameOffset;
        }

        /// <summary>
        /// Reconfigure this instance with new clip data, avoiding GC allocation from creating new instances.
        /// </summary>
        public void Configure(string stateName, int stateHash, int startFrame, int endFrame, float frameRate = 30f, bool isLooping = true)
        {
            StateName = stateName;
            StateHash = stateHash;
            StartFrame = startFrame;
            EndFrame = endFrame;
            FrameRate = frameRate;
            IsLooping = isLooping;
            CurrentFrameNormalized = 0f;
        }
    }
}
