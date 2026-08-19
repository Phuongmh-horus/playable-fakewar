using System;
using System.Collections.Generic;
using UnityEngine;

namespace OptimizedFeature.Scripts.Editor
{
    public enum AnimationMergeNodeType
    {
        Animation,
        Merge,
        Preview
    }

    [CreateAssetMenu(
        fileName = "AnimationMergeGraph",
        menuName = "Tools/Animation Merge Graph Session",
        order = 2100)]
    public class AnimationMergeGraphAsset : ScriptableObject
    {
        public GameObject sourceGameObject;
        public Animator sourceAnimator;
        public List<AnimationMergeNodeData> nodes = new List<AnimationMergeNodeData>();
        public List<AnimationMergeEdgeData> edges = new List<AnimationMergeEdgeData>();
        public List<AnimationMergeLayerData> layers = new List<AnimationMergeLayerData>();
        public int nextNodeId = 1;

        public int AllocateNodeId()
        {
            if (nextNodeId < 1)
            {
                nextNodeId = 1;
            }

            return nextNodeId++;
        }
    }

    [Serializable]
    public class AnimationMergeNodeData
    {
        public int id;
        public AnimationMergeNodeType nodeType;
        public string title;
        public Vector2 position;
        public int layerIndex = -1;
        public string layerName;
        public Color accentColor = Color.white;
        public int colorIndex;

        // Animation node data.
        public AnimationClip clip;
        public Motion motion;
        public bool isBlendTree;
        public bool isGenerated;
        public bool bake = true;
        public string statePath;
        public string motionPath;
        public List<AnimationClip> blendTreeClips = new List<AnimationClip>();
        public int generatedFromMergeNodeId = -1;

        // Merge node data.
        public List<AnimationMergeBoneChoice> boneChoices = new List<AnimationMergeBoneChoice>();
        public AnimationClip outputClip;
        public string outputClipName;
        public string lastMergeSummary;

        // Preview node data. Scene references are valid for the temporary session;
        // the window falls back to the graph source object when this is not set.
        public GameObject previewGameObject;
    }

    [Serializable]
    public class AnimationMergeEdgeData
    {
        public int outputNodeId;
        public int inputNodeId;
        public string inputPortName;
    }

    [Serializable]
    public class AnimationMergeLayerData
    {
        public int layerIndex;
        public string layerName;
        public Color accentColor = Color.white;
    }

    [Serializable]
    public class AnimationMergeBoneChoice
    {
        public string bonePath;
        public int sourceIndex;
        public bool include = true;
    }
}
