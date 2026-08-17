using System;
using System.Collections.Generic;
using UnityEngine;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// Runtime animator parameter types. These definitions intentionally live outside an
    /// Editor folder so a future VAT animator runtime can consume the serialized data.
    /// </summary>
    public enum VATAnimatorParameterType
    {
        Trigger,
        Bool,
        Float,
        Vector2
    }

    public enum VATAnimatorConditionMode
    {
        If,
        IfNot,
        Greater,
        Less,
        Equals,
        NotEquals,
        MagnitudeGreater,
        MagnitudeLess
    }

    public enum VATAnimatorBlendTreeMode
    {
        OneDimensional,
        TwoDimensional
    }

    [Serializable]
    public sealed class VATAnimatorParameterData
    {
        public int id;
        public string parameterName = "Parameter";
        public VATAnimatorParameterType type = VATAnimatorParameterType.Float;
        public bool defaultBool;
        public float defaultFloat;
        public Vector2 defaultVector2;
    }

    [Serializable]
    public sealed class VATAnimatorConditionData
    {
        public int id;
        public int parameterId = -1;
        public VATAnimatorConditionMode mode = VATAnimatorConditionMode.If;
        public float threshold;
        public bool boolThreshold;
        public Vector2 vectorThreshold;
    }

    [Serializable]
    public sealed class VATAnimatorTransitionData
    {
        public int id;
        public string title = "Transition";
        /// <summary>
        /// Runtime source state. A value of zero means the transition has no valid source clip.
        /// </summary>
        public int fromStateHash;
        /// <summary>
        /// Runtime target clip when the transition targets a Clip node.
        /// </summary>
        public int toStateHash;
        /// <summary>
        /// Runtime target blend tree when the transition targets a BlendTree node.
        /// </summary>
        public int toBlendTreeId = -1;
        public bool autoTransition;
        public bool hasExitTime;
        [Min(0f)] public float duration = 0.15f;
        [Min(0f)] public float exitTime = 1f;
        public List<VATAnimatorConditionData> conditions = new List<VATAnimatorConditionData>();
    }

    [Serializable]
    public sealed class VATAnimatorBlendChildData
    {
        /// <summary>
        /// Stable case id. The editor graph uses this id as the output port identity;
        /// clipKey is the runtime clip reference resolved from the connected Clip node.
        /// </summary>
        public int id;
        /// <summary>
        /// Runtime lookup hash resolved against VATAssetDataSO.Clips.
        /// </summary>
        public int stateHash;
        /// <summary>
        /// Reference key resolved against VATAssetDataSO.Clips. No clip metadata is copied here.
        /// </summary>
        public string clipKey;
        public Vector2 threshold;
    }

    [Serializable]
    public sealed class VATAnimatorBlendTreeData
    {
        public int id;
        public string title = "Blend Tree";
        public int parameterId = -1;
        public VATAnimatorBlendTreeMode mode = VATAnimatorBlendTreeMode.OneDimensional;
        public bool clampInput = true;
        public List<VATAnimatorBlendChildData> children = new List<VATAnimatorBlendChildData>();
    }

#if UNITY_EDITOR
    /// <summary>
    /// Editor-only node kinds. The runtime animator does not need graph layout information.
    /// </summary>
    public enum VATAnimatorNodeType
    {
        Clip,
        Parameter,
        Transition,
        BlendTree,
        Default
    }

    [Serializable]
    public sealed class VATAnimatorNodeData
    {
        public int id;
        public VATAnimatorNodeType nodeType;
        public string title;
        public Vector2 position;

        public string clipKey;
        public int parameterId = -1;
        public int transitionId = -1;
        public int blendTreeId = -1;
    }

    [Serializable]
    public sealed class VATAnimatorEdgeData
    {
        public int outputNodeId;
        public string outputPortName;
        public int inputNodeId;
        public string inputPortName;
    }

    /// <summary>
    /// Editor-only graph structure serialized inside VATAssetDataSO.
    /// </summary>
    [Serializable]
    public sealed class VATAnimatorGraphData
    {
        public const int CurrentSchemaVersion = 5;

        public int schemaVersion = CurrentSchemaVersion;
        public string defaultClipKey;
        public List<VATAnimatorNodeData> nodes = new List<VATAnimatorNodeData>();
        public List<VATAnimatorEdgeData> edges = new List<VATAnimatorEdgeData>();
        public int nextNodeId = 1;
        public int nextParameterId = 1;
        public int nextConditionId = 1;
        public int nextTransitionId = 1;
        public int nextBlendTreeId = 1;
        public int nextBlendChildId = 1;
    }
#endif
}
