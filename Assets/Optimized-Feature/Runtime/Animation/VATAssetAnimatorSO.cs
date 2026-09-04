using System;
using System.Collections.Generic;
using UnityEngine;

namespace OptimizedFeature.Scripts
{
    /// <summary>
    /// Independent runtime and editor graph payload for a VAT character.
    /// A VATAssetDataSO owns baked clips/geometry; this asset owns how those
    /// clips are driven by parameters, transitions and blend trees.
    /// </summary>
    [CreateAssetMenu(fileName = "VATAssetAnimator", menuName = "VAT/VAT Asset Animator")]
    public class VATAssetAnimatorSO : ScriptableObject
    {
        public int DefaultStateName;
        public List<VATAnimatorParameterData> AnimatorParameters = new List<VATAnimatorParameterData>();
        public List<VATAnimatorTransitionData> AnimatorTransitions = new List<VATAnimatorTransitionData>();
        public List<VATAnimatorBlendTreeData> AnimatorBlendTrees = new List<VATAnimatorBlendTreeData>();

#if UNITY_EDITOR
        public VATAnimatorGraphData AnimatorGraph = new VATAnimatorGraphData();
#endif

        private void OnEnable()
        {
            EnsureLists();
        }

        private void OnValidate()
        {
            EnsureLists();
        }

        public void EnsureLists()
        {
            if (AnimatorParameters == null) AnimatorParameters = new List<VATAnimatorParameterData>();
            if (AnimatorTransitions == null) AnimatorTransitions = new List<VATAnimatorTransitionData>();
            if (AnimatorBlendTrees == null) AnimatorBlendTrees = new List<VATAnimatorBlendTreeData>();
#if UNITY_EDITOR
            if (AnimatorGraph == null) AnimatorGraph = new VATAnimatorGraphData();
            if (AnimatorGraph.nodes == null) AnimatorGraph.nodes = new List<VATAnimatorNodeData>();
            if (AnimatorGraph.edges == null) AnimatorGraph.edges = new List<VATAnimatorEdgeData>();
#endif
        }

#if UNITY_EDITOR
        public void CopyFromLegacy(
            int defaultStateName,
            List<VATAnimatorParameterData> parameters,
            List<VATAnimatorTransitionData> transitions,
            List<VATAnimatorBlendTreeData> blendTrees,
            VATAnimatorGraphData graph)
        {
            DefaultStateName = defaultStateName;
            AnimatorParameters = CloneParameters(parameters);
            AnimatorTransitions = CloneTransitions(transitions);
            AnimatorBlendTrees = CloneBlendTrees(blendTrees);
            AnimatorGraph = CloneGraph(graph);
            EnsureLists();
        }

        private static List<VATAnimatorParameterData> CloneParameters(
            List<VATAnimatorParameterData> source)
        {
            List<VATAnimatorParameterData> result = new List<VATAnimatorParameterData>();
            if (source == null) return result;

            for (int i = 0; i < source.Count; i++)
            {
                VATAnimatorParameterData item = source[i];
                if (item == null) continue;
                result.Add(new VATAnimatorParameterData
                {
                    id = item.id,
                    parameterName = item.parameterName,
                    type = item.type,
                    defaultBool = item.defaultBool,
                    defaultFloat = item.defaultFloat,
                    defaultVector2 = item.defaultVector2
                });
            }

            return result;
        }

        private static List<VATAnimatorTransitionData> CloneTransitions(
            List<VATAnimatorTransitionData> source)
        {
            List<VATAnimatorTransitionData> result = new List<VATAnimatorTransitionData>();
            if (source == null) return result;

            for (int i = 0; i < source.Count; i++)
            {
                VATAnimatorTransitionData item = source[i];
                if (item == null) continue;
                VATAnimatorTransitionData copy = new VATAnimatorTransitionData
                {
                    id = item.id,
                    title = item.title,
                    fromStateHash = item.fromStateHash,
                    toStateHash = item.toStateHash,
                    toBlendTreeId = item.toBlendTreeId,
                    autoTransition = item.autoTransition,
                    hasExitTime = item.hasExitTime,
                    duration = item.duration,
                    exitTime = item.exitTime,
                    conditions = new List<VATAnimatorConditionData>()
                };
                if (item.conditions != null)
                {
                    for (int c = 0; c < item.conditions.Count; c++)
                    {
                        VATAnimatorConditionData condition = item.conditions[c];
                        if (condition == null) continue;
                        copy.conditions.Add(new VATAnimatorConditionData
                        {
                            id = condition.id,
                            parameterId = condition.parameterId,
                            mode = condition.mode,
                            threshold = condition.threshold,
                            boolThreshold = condition.boolThreshold,
                            vectorThreshold = condition.vectorThreshold
                        });
                    }
                }

                result.Add(copy);
            }

            return result;
        }

        private static List<VATAnimatorBlendTreeData> CloneBlendTrees(
            List<VATAnimatorBlendTreeData> source)
        {
            List<VATAnimatorBlendTreeData> result = new List<VATAnimatorBlendTreeData>();
            if (source == null) return result;

            for (int i = 0; i < source.Count; i++)
            {
                VATAnimatorBlendTreeData item = source[i];
                if (item == null) continue;
                VATAnimatorBlendTreeData copy = new VATAnimatorBlendTreeData
                {
                    id = item.id,
                    title = item.title,
                    parameterId = item.parameterId,
                    mode = item.mode,
                    clampInput = item.clampInput,
                    children = new List<VATAnimatorBlendChildData>()
                };
                if (item.children != null)
                {
                    for (int c = 0; c < item.children.Count; c++)
                    {
                        VATAnimatorBlendChildData child = item.children[c];
                        if (child == null) continue;
                        copy.children.Add(new VATAnimatorBlendChildData
                        {
                            id = child.id,
                            stateHash = child.stateHash,
                            clipKey = child.clipKey,
                            threshold = child.threshold
                        });
                    }
                }

                result.Add(copy);
            }

            return result;
        }

        private static VATAnimatorGraphData CloneGraph(VATAnimatorGraphData source)
        {
            VATAnimatorGraphData copy = new VATAnimatorGraphData();
            if (source == null) return copy;

            copy.schemaVersion = source.schemaVersion;
            copy.defaultClipKey = source.defaultClipKey;
            copy.nextNodeId = source.nextNodeId;
            copy.nextParameterId = source.nextParameterId;
            copy.nextConditionId = source.nextConditionId;
            copy.nextTransitionId = source.nextTransitionId;
            copy.nextBlendTreeId = source.nextBlendTreeId;
            copy.nextBlendChildId = source.nextBlendChildId;
            copy.nodes = new List<VATAnimatorNodeData>();
            if (source.nodes != null)
            {
                for (int i = 0; i < source.nodes.Count; i++)
                {
                    VATAnimatorNodeData node = source.nodes[i];
                    if (node == null) continue;
                    copy.nodes.Add(new VATAnimatorNodeData
                    {
                        id = node.id,
                        nodeType = node.nodeType,
                        title = node.title,
                        position = node.position,
                        clipKey = node.clipKey,
                        parameterId = node.parameterId,
                        transitionId = node.transitionId,
                        blendTreeId = node.blendTreeId
                    });
                }
            }

            copy.edges = new List<VATAnimatorEdgeData>();
            if (source.edges != null)
            {
                for (int i = 0; i < source.edges.Count; i++)
                {
                    VATAnimatorEdgeData edge = source.edges[i];
                    if (edge == null) continue;
                    copy.edges.Add(new VATAnimatorEdgeData
                    {
                        outputNodeId = edge.outputNodeId,
                        outputPortName = edge.outputPortName,
                        inputNodeId = edge.inputNodeId,
                        inputPortName = edge.inputPortName
                    });
                }
            }

            return copy;
        }
#endif
    }
}
