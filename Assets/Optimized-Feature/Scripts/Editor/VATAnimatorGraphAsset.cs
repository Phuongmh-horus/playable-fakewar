using System;
using System.Collections.Generic;
using UnityEngine;
using OptimizedFeature.Scripts;

namespace OptimizedFeature.Editor.VATAnimator
{
    public enum VATAnimatorNodeType
    {
        Clip,
        Parameter,
        Transition,
        BlendTree
    }

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
    public sealed class VATAnimatorClipData
    {
        [Tooltip("Stable editor key. It is kept when a source clip is renamed but keeps the same state hash.")]
        public string clipKey;
        public string clipName;
        public int stateHash;
        public int startFrame;
        public int endFrame;
        public float frameRate = 30f;
        public bool isLooping = true;

        public int TotalFrames
        {
            get { return Mathf.Max(0, endFrame - startFrame + 1); }
        }

        public void SyncFrom(VATClipInfo source)
        {
            if (source == null) return;

            clipName = source.ClipName;
            stateHash = source.StateHash;
            startFrame = source.StartFrame;
            endFrame = source.EndFrame;
            frameRate = source.FrameRate;
            isLooping = source.IsLooping;
        }
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
        public bool autoTransition;
        public bool hasExitTime;
        [Min(0f)] public float duration = 0.15f;
        [Min(0f)] public float exitTime = 1f;
        public List<VATAnimatorConditionData> conditions = new List<VATAnimatorConditionData>();
    }

    [Serializable]
    public sealed class VATAnimatorBlendChildData
    {
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
    /// Editor-only graph definition for VAT animation.
    ///
    /// This asset intentionally references VATAssetDataSO without modifying it. The source
    /// clips are copied into <see cref="clips"/> so the graph remains inspectable and can keep
    /// stable editor keys while the bake asset is refreshed.
    /// </summary>
    [CreateAssetMenu(
        fileName = "VATAnimatorGraph",
        menuName = "VAT/VAT Animator Graph",
        order = 2100)]
    public sealed class VATAnimatorGraphAsset : ScriptableObject
    {
        public const int CurrentSchemaVersion = 1;

        public VATAssetDataSO sourceVATAsset;
        public string defaultClipKey;
        public int schemaVersion = CurrentSchemaVersion;

        public List<VATAnimatorClipData> clips = new List<VATAnimatorClipData>();
        public List<VATAnimatorParameterData> parameters = new List<VATAnimatorParameterData>();
        public List<VATAnimatorTransitionData> transitions = new List<VATAnimatorTransitionData>();
        public List<VATAnimatorBlendTreeData> blendTrees = new List<VATAnimatorBlendTreeData>();
        public List<VATAnimatorNodeData> nodes = new List<VATAnimatorNodeData>();
        public List<VATAnimatorEdgeData> edges = new List<VATAnimatorEdgeData>();

        public int nextNodeId = 1;
        public int nextParameterId = 1;
        public int nextTransitionId = 1;
        public int nextBlendTreeId = 1;

        [NonSerialized] private Dictionary<string, VATAnimatorClipData> _clipByKey;
        [NonSerialized] private Dictionary<int, VATAnimatorClipData> _clipByHash;

        private void OnEnable()
        {
            EnsureLists();
            RebuildClipMapping();
        }

        public void EnsureLists()
        {
            if (clips == null) clips = new List<VATAnimatorClipData>();
            if (parameters == null) parameters = new List<VATAnimatorParameterData>();
            if (transitions == null) transitions = new List<VATAnimatorTransitionData>();
            if (blendTrees == null) blendTrees = new List<VATAnimatorBlendTreeData>();
            if (nodes == null) nodes = new List<VATAnimatorNodeData>();
            if (edges == null) edges = new List<VATAnimatorEdgeData>();
            if (schemaVersion <= 0) schemaVersion = CurrentSchemaVersion;
        }

        public static string MakeClipKey(VATClipInfo clip)
        {
            if (clip == null) return string.Empty;
            return clip.StateHash.ToString() + ":" + (clip.ClipName ?? string.Empty);
        }

        public void RebuildClipMapping()
        {
            EnsureLists();
            _clipByKey = new Dictionary<string, VATAnimatorClipData>(StringComparer.Ordinal);
            _clipByHash = new Dictionary<int, VATAnimatorClipData>();

            for (int i = 0; i < clips.Count; i++)
            {
                VATAnimatorClipData clip = clips[i];
                if (clip == null) continue;
                if (!string.IsNullOrEmpty(clip.clipKey)) _clipByKey[clip.clipKey] = clip;
                _clipByHash[clip.stateHash] = clip;
            }
        }

        public VATAnimatorClipData FindClipByKey(string clipKey)
        {
            if (_clipByKey == null) RebuildClipMapping();
            if (string.IsNullOrEmpty(clipKey)) return null;

            VATAnimatorClipData result;
            return _clipByKey.TryGetValue(clipKey, out result) ? result : null;
        }

        public VATAnimatorClipData FindClipByHash(int stateHash)
        {
            if (_clipByHash == null) RebuildClipMapping();

            VATAnimatorClipData result;
            return _clipByHash.TryGetValue(stateHash, out result) ? result : null;
        }

        public VATAnimatorParameterData FindParameter(int id)
        {
            EnsureLists();
            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i] != null && parameters[i].id == id) return parameters[i];
            }
            return null;
        }

        public VATAnimatorTransitionData FindTransition(int id)
        {
            EnsureLists();
            for (int i = 0; i < transitions.Count; i++)
            {
                if (transitions[i] != null && transitions[i].id == id) return transitions[i];
            }
            return null;
        }

        public VATAnimatorBlendTreeData FindBlendTree(int id)
        {
            EnsureLists();
            for (int i = 0; i < blendTrees.Count; i++)
            {
                if (blendTrees[i] != null && blendTrees[i].id == id) return blendTrees[i];
            }
            return null;
        }

        public VATAnimatorClipData FindDefaultClip()
        {
            VATAnimatorClipData clip = FindClipByKey(defaultClipKey);
            if (clip != null) return clip;
            return clips.Count > 0 ? clips[0] : null;
        }

        /// <summary>
        /// Resolves the nearest clip case for a BlendTree using the parameter value.
        /// This is an editor-side mapping helper; runtime integration can later use the same
        /// serialized thresholds without changing VATAssetDataSO.
        /// </summary>
        public VATAnimatorClipData ResolveBlendTreeClip(int blendTreeId, float floatValue, Vector2 vectorValue)
        {
            VATAnimatorBlendTreeData tree = FindBlendTree(blendTreeId);
            if (tree == null || tree.children == null || tree.children.Count == 0) return null;

            VATAnimatorParameterData parameter = FindParameter(tree.parameterId);
            bool useVector2 = tree.mode == VATAnimatorBlendTreeMode.TwoDimensional ||
                (parameter != null && parameter.type == VATAnimatorParameterType.Vector2);
            Vector2 input = useVector2 ? vectorValue : new Vector2(floatValue, 0f);

            if (!useVector2 && tree.clampInput)
            {
                float min = float.PositiveInfinity;
                float max = float.NegativeInfinity;
                for (int i = 0; i < tree.children.Count; i++)
                {
                    VATAnimatorBlendChildData child = tree.children[i];
                    if (child == null) continue;
                    min = Mathf.Min(min, child.threshold.x);
                    max = Mathf.Max(max, child.threshold.x);
                }
                if (min != float.PositiveInfinity)
                {
                    input.x = Mathf.Clamp(input.x, min, max);
                }
            }

            VATAnimatorBlendChildData closest = null;
            float closestDistance = float.PositiveInfinity;
            for (int i = 0; i < tree.children.Count; i++)
            {
                VATAnimatorBlendChildData child = tree.children[i];
                if (child == null) continue;

                float distance = useVector2
                    ? (child.threshold - input).sqrMagnitude
                    : Mathf.Abs(child.threshold.x - input.x);
                if (closest == null || distance < closestDistance)
                {
                    closest = child;
                    closestDistance = distance;
                }
            }

            return closest == null ? null : FindClipByKey(closest.clipKey);
        }

        public VATAnimatorNodeData FindNode(int id)
        {
            EnsureLists();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] != null && nodes[i].id == id) return nodes[i];
            }
            return null;
        }

        public int AllocateNodeId()
        {
            if (nextNodeId < 1) nextNodeId = 1;
            return nextNodeId++;
        }

        public int AllocateParameterId()
        {
            if (nextParameterId < 1) nextParameterId = 1;
            return nextParameterId++;
        }

        public int AllocateTransitionId()
        {
            if (nextTransitionId < 1) nextTransitionId = 1;
            return nextTransitionId++;
        }

        public int AllocateBlendTreeId()
        {
            if (nextBlendTreeId < 1) nextBlendTreeId = 1;
            return nextBlendTreeId++;
        }

        /// <summary>
        /// Copies every VATAssetDataSO clip field into the graph and creates one Clip node
        /// per source clip. Existing nodes keep their positions and stable keys.
        /// </summary>
        public int SyncFromVATAssetData(bool removeMissingClips = true)
        {
            EnsureLists();
            if (sourceVATAsset == null) return 0;

            Dictionary<string, VATAnimatorClipData> oldByKey = new Dictionary<string, VATAnimatorClipData>(StringComparer.Ordinal);
            Dictionary<int, VATAnimatorClipData> oldByHash = new Dictionary<int, VATAnimatorClipData>();
            for (int i = 0; i < clips.Count; i++)
            {
                VATAnimatorClipData oldClip = clips[i];
                if (oldClip == null) continue;
                if (!string.IsNullOrEmpty(oldClip.clipKey)) oldByKey[oldClip.clipKey] = oldClip;
                oldByHash[oldClip.stateHash] = oldClip;
            }

            List<VATAnimatorClipData> refreshedClips = new List<VATAnimatorClipData>();
            HashSet<string> validKeys = new HashSet<string>(StringComparer.Ordinal);
            if (sourceVATAsset.Clips != null)
            {
                for (int i = 0; i < sourceVATAsset.Clips.Count; i++)
                {
                    VATClipInfo sourceClip = sourceVATAsset.Clips[i];
                    if (sourceClip == null || string.IsNullOrEmpty(sourceClip.ClipName)) continue;

                    string sourceKey = MakeClipKey(sourceClip);
                    VATAnimatorClipData graphClip;
                    if (!oldByKey.TryGetValue(sourceKey, out graphClip))
                    {
                        oldByHash.TryGetValue(sourceClip.StateHash, out graphClip);
                    }
                    if (graphClip == null)
                    {
                        graphClip = new VATAnimatorClipData { clipKey = sourceKey };
                    }

                    if (string.IsNullOrEmpty(graphClip.clipKey)) graphClip.clipKey = sourceKey;
                    graphClip.SyncFrom(sourceClip);
                    refreshedClips.Add(graphClip);
                    validKeys.Add(graphClip.clipKey);
                }
            }

            clips = refreshedClips;
            if (string.IsNullOrEmpty(defaultClipKey) || !validKeys.Contains(defaultClipKey))
            {
                defaultClipKey = clips.Count > 0 ? clips[0].clipKey : string.Empty;
            }

            if (removeMissingClips)
            {
                nodes.RemoveAll(node => node == null ||
                    (node.nodeType == VATAnimatorNodeType.Clip && !validKeys.Contains(node.clipKey)));

                for (int i = 0; i < blendTrees.Count; i++)
                {
                    VATAnimatorBlendTreeData tree = blendTrees[i];
                    if (tree == null || tree.children == null) continue;
                    tree.children.RemoveAll(child => child == null || !validKeys.Contains(child.clipKey));
                }
            }

            HashSet<string> existingClipNodes = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < nodes.Count; i++)
            {
                VATAnimatorNodeData node = nodes[i];
                if (node != null && node.nodeType == VATAnimatorNodeType.Clip)
                {
                    existingClipNodes.Add(node.clipKey);
                }
            }

            for (int i = 0; i < clips.Count; i++)
            {
                VATAnimatorClipData clip = clips[i];
                if (clip == null || existingClipNodes.Contains(clip.clipKey)) continue;

                int column = i % 4;
                int row = i / 4;
                nodes.Add(new VATAnimatorNodeData
                {
                    id = AllocateNodeId(),
                    nodeType = VATAnimatorNodeType.Clip,
                    title = clip.clipName,
                    clipKey = clip.clipKey,
                    position = new Vector2(40f + column * 290f, 80f + row * 190f)
                });
            }

            PruneDanglingEdges();
            RebuildClipMapping();
            return clips.Count;
        }

        public VATAnimatorNodeData AddParameterNode(string parameterName = null)
        {
            EnsureLists();
            int parameterId = AllocateParameterId();
            VATAnimatorParameterData parameter = new VATAnimatorParameterData
            {
                id = parameterId,
                parameterName = string.IsNullOrEmpty(parameterName) ? "Parameter " + parameterId : parameterName
            };
            parameters.Add(parameter);

            VATAnimatorNodeData node = new VATAnimatorNodeData
            {
                id = AllocateNodeId(),
                nodeType = VATAnimatorNodeType.Parameter,
                title = parameter.parameterName,
                parameterId = parameterId,
                position = new Vector2(60f + (parameters.Count - 1) * 30f, 680f)
            };
            nodes.Add(node);
            return node;
        }

        public VATAnimatorNodeData AddTransitionNode()
        {
            EnsureLists();
            int transitionId = AllocateTransitionId();
            transitions.Add(new VATAnimatorTransitionData
            {
                id = transitionId,
                title = "Transition " + transitionId
            });

            VATAnimatorNodeData node = new VATAnimatorNodeData
            {
                id = AllocateNodeId(),
                nodeType = VATAnimatorNodeType.Transition,
                title = "Transition " + transitionId,
                transitionId = transitionId,
                position = new Vector2(580f + (transitions.Count - 1) * 30f, 400f)
            };
            nodes.Add(node);
            return node;
        }

        public VATAnimatorNodeData AddBlendTreeNode()
        {
            EnsureLists();
            int blendTreeId = AllocateBlendTreeId();
            VATAnimatorBlendTreeData blendTree = new VATAnimatorBlendTreeData
            {
                id = blendTreeId,
                title = "Blend Tree " + blendTreeId
            };
            if (clips.Count > 0)
            {
                blendTree.children.Add(new VATAnimatorBlendChildData
                {
                    clipKey = clips[0].clipKey,
                    threshold = Vector2.zero
                });
            }
            blendTrees.Add(blendTree);

            VATAnimatorNodeData node = new VATAnimatorNodeData
            {
                id = AllocateNodeId(),
                nodeType = VATAnimatorNodeType.BlendTree,
                title = blendTree.title,
                blendTreeId = blendTreeId,
                position = new Vector2(820f + (blendTrees.Count - 1) * 30f, 400f)
            };
            nodes.Add(node);
            return node;
        }

        public void RemoveNode(int nodeId)
        {
            VATAnimatorNodeData node = FindNode(nodeId);
            if (node == null) return;

            nodes.Remove(node);
            edges.RemoveAll(edge => edge == null || edge.outputNodeId == nodeId || edge.inputNodeId == nodeId);

            if (node.nodeType == VATAnimatorNodeType.Parameter)
            {
                parameters.RemoveAll(parameter => parameter == null || parameter.id == node.parameterId);
                for (int i = 0; i < transitions.Count; i++)
                {
                    VATAnimatorTransitionData transition = transitions[i];
                    if (transition == null || transition.conditions == null) continue;
                    transition.conditions.RemoveAll(condition => condition == null || condition.parameterId == node.parameterId);
                }
                for (int i = 0; i < blendTrees.Count; i++)
                {
                    if (blendTrees[i] != null && blendTrees[i].parameterId == node.parameterId)
                    {
                        blendTrees[i].parameterId = -1;
                    }
                }
            }
            else if (node.nodeType == VATAnimatorNodeType.Transition)
            {
                transitions.RemoveAll(transition => transition == null || transition.id == node.transitionId);
            }
            else if (node.nodeType == VATAnimatorNodeType.BlendTree)
            {
                blendTrees.RemoveAll(tree => tree == null || tree.id == node.blendTreeId);
            }
        }

        public bool AddEdge(VATAnimatorEdgeData edge)
        {
            if (edge == null || FindNode(edge.outputNodeId) == null || FindNode(edge.inputNodeId) == null)
            {
                return false;
            }

            for (int i = 0; i < edges.Count; i++)
            {
                VATAnimatorEdgeData existing = edges[i];
                if (existing != null && existing.outputNodeId == edge.outputNodeId &&
                    existing.outputPortName == edge.outputPortName &&
                    existing.inputNodeId == edge.inputNodeId &&
                    existing.inputPortName == edge.inputPortName)
                {
                    return false;
                }
            }

            edges.Add(edge);
            return true;
        }

        public void RemoveEdge(VATAnimatorEdgeData edge)
        {
            if (edge != null) edges.Remove(edge);
        }

        public void PruneDanglingEdges()
        {
            edges.RemoveAll(edge => edge == null || FindNode(edge.outputNodeId) == null || FindNode(edge.inputNodeId) == null);
        }

        public void HandleParameterEdge(int parameterId, int targetNodeId, string targetPortName)
        {
            VATAnimatorNodeData targetNode = FindNode(targetNodeId);
            if (targetNode == null) return;

            if (targetNode.nodeType == VATAnimatorNodeType.BlendTree && targetPortName == "Parameter")
            {
                VATAnimatorBlendTreeData tree = FindBlendTree(targetNode.blendTreeId);
                if (tree != null) tree.parameterId = parameterId;
            }
            else if (targetNode.nodeType == VATAnimatorNodeType.Transition && targetPortName == "Conditions")
            {
                VATAnimatorTransitionData transition = FindTransition(targetNode.transitionId);
                if (transition == null) return;

                bool alreadyPresent = false;
                for (int i = 0; i < transition.conditions.Count; i++)
                {
                    if (transition.conditions[i] != null && transition.conditions[i].parameterId == parameterId)
                    {
                        alreadyPresent = true;
                        break;
                    }
                }
                if (!alreadyPresent)
                {
                    transition.conditions.Add(new VATAnimatorConditionData { parameterId = parameterId });
                }
            }
        }

        public bool ValidateGraph(List<string> messages)
        {
            EnsureLists();
            if (messages == null) throw new ArgumentNullException("messages");
            messages.Clear();

            if (sourceVATAsset == null) messages.Add("No VATAssetDataSO input is assigned.");
            if (clips.Count == 0) messages.Add("The graph has no source clips. Sync it from VATAssetDataSO.Clips.");
            if (string.IsNullOrEmpty(defaultClipKey) && clips.Count > 0) messages.Add("No default clip is selected.");

            HashSet<int> hashes = new HashSet<int>();
            for (int i = 0; i < clips.Count; i++)
            {
                VATAnimatorClipData clip = clips[i];
                if (clip == null) continue;
                if (!hashes.Add(clip.stateHash)) messages.Add("Duplicate clip state hash: " + clip.stateHash);
                if (clip.endFrame < clip.startFrame) messages.Add("Invalid frame range: " + clip.clipName);
                if (clip.frameRate <= 0f) messages.Add("Frame rate must be greater than zero: " + clip.clipName);
            }

            for (int i = 0; i < transitions.Count; i++)
            {
                VATAnimatorTransitionData transition = transitions[i];
                if (transition == null) continue;
                if (transition.duration < 0f) messages.Add("Transition duration cannot be negative: " + transition.title);
                if (transition.conditions.Count == 0 && !transition.autoTransition && !transition.hasExitTime)
                {
                    messages.Add("Transition has no condition, auto mode, or exit time: " + transition.title);
                }
                for (int c = 0; c < transition.conditions.Count; c++)
                {
                    VATAnimatorConditionData condition = transition.conditions[c];
                    if (condition == null || FindParameter(condition.parameterId) == null)
                    {
                        messages.Add("Transition contains a condition with a missing parameter: " + transition.title);
                    }
                }
            }

            for (int i = 0; i < blendTrees.Count; i++)
            {
                VATAnimatorBlendTreeData tree = blendTrees[i];
                if (tree == null) continue;
                VATAnimatorParameterData parameter = FindParameter(tree.parameterId);
                if (parameter == null)
                {
                    messages.Add("Blend tree has no parameter: " + tree.title);
                }
                else if (parameter.type != VATAnimatorParameterType.Float && parameter.type != VATAnimatorParameterType.Vector2)
                {
                    messages.Add("Blend tree parameter must be Float or Vector2: " + tree.title);
                }
                if (tree.children == null || tree.children.Count == 0)
                {
                    messages.Add("Blend tree has no child clips: " + tree.title);
                }
                else
                {
                    for (int c = 0; c < tree.children.Count; c++)
                    {
                        if (tree.children[c] == null || FindClipByKey(tree.children[c].clipKey) == null)
                        {
                            messages.Add("Blend tree contains a missing child clip: " + tree.title);
                        }
                    }
                }
            }

            return messages.Count == 0;
        }
    }
}
