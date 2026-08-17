using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using OptimizedFeature.Scripts;

namespace OptimizedFeature.Editor.VATAnimator
{
    /// <summary>
    /// Transient Editor projection of one VATClipInfo. It is never serialized; the source clip
    /// remains VATAssetDataSO.Clips and all runtime animator data remains on VATAssetDataSO.
    /// </summary>
    internal sealed class VATAnimatorClipData
    {
        private readonly VATClipInfo source;

        public VATAnimatorClipData(VATClipInfo sourceClip)
        {
            source = sourceClip;
        }

        public string clipKey
        {
            get { return VATAnimatorGraphAsset.MakeClipKey(source); }
        }

        public string clipName
        {
            get { return source == null ? string.Empty : source.ClipName; }
        }

        public int stateHash
        {
            get { return source == null ? 0 : source.StateHash; }
        }

        public int startFrame
        {
            get { return source == null ? 0 : source.StartFrame; }
        }

        public int endFrame
        {
            get { return source == null ? 0 : source.EndFrame; }
        }

        public float frameRate
        {
            get { return source == null ? 0f : source.FrameRate; }
        }

        public bool isLooping
        {
            get { return source != null && source.IsLooping; }
        }

        public int TotalFrames
        {
            get { return Mathf.Max(0, endFrame - startFrame + 1); }
        }
    }

    /// <summary>
    /// Editor adapter over VATAssetDataSO.
    ///
    /// This class is intentionally not a persisted graph asset. It exists only to keep the
    /// GraphView code readable while every serialized value is read from or written to the
    /// selected VATAssetDataSO.
    /// </summary>
    internal sealed class VATAnimatorGraphAsset : ScriptableObject
    {
        [SerializeField] private VATAssetDataSO _sourceVATAsset;
        private readonly List<VATAnimatorClipData> _clipProjections = new List<VATAnimatorClipData>();

        public VATAssetDataSO sourceVATAsset
        {
            get { return _sourceVATAsset; }
            set { Attach(value); }
        }

        public string defaultClipKey
        {
            get { return Graph == null ? string.Empty : Graph.defaultClipKey; }
            set
            {
                if (Graph != null) Graph.defaultClipKey = value;
            }
        }

        public int schemaVersion
        {
            get { return Graph == null ? VATAnimatorGraphData.CurrentSchemaVersion : Graph.schemaVersion; }
            set
            {
                if (Graph != null) Graph.schemaVersion = value;
            }
        }

        public List<VATAnimatorClipData> clips
        {
            get
            {
                RebuildClipProjections();
                return _clipProjections;
            }
        }

        public List<VATAnimatorParameterData> parameters
        {
            get { return _sourceVATAsset == null ? null : _sourceVATAsset.AnimatorParameters; }
        }

        public List<VATAnimatorTransitionData> transitions
        {
            get { return _sourceVATAsset == null ? null : _sourceVATAsset.AnimatorTransitions; }
        }

        public List<VATAnimatorBlendTreeData> blendTrees
        {
            get { return _sourceVATAsset == null ? null : _sourceVATAsset.AnimatorBlendTrees; }
        }

        public List<VATAnimatorNodeData> nodes
        {
            get { return Graph == null ? null : Graph.nodes; }
        }

        public List<VATAnimatorEdgeData> edges
        {
            get { return Graph == null ? null : Graph.edges; }
        }

        private VATAnimatorGraphData Graph
        {
            get { return _sourceVATAsset == null ? null : _sourceVATAsset.AnimatorGraph; }
        }

        private void OnEnable()
        {
            EnsureLists();
        }

        public void Attach(VATAssetDataSO source)
        {
            _sourceVATAsset = source;
            EnsureLists();
            RebuildClipProjections();
        }

        public void EnsureLists()
        {
            if (_sourceVATAsset == null) return;

            if (_sourceVATAsset.Clips == null) _sourceVATAsset.Clips = new List<VATClipInfo>();
            if (_sourceVATAsset.AnimatorParameters == null)
            {
                _sourceVATAsset.AnimatorParameters = new List<VATAnimatorParameterData>();
            }
            if (_sourceVATAsset.AnimatorTransitions == null)
            {
                _sourceVATAsset.AnimatorTransitions = new List<VATAnimatorTransitionData>();
            }
            if (_sourceVATAsset.AnimatorBlendTrees == null)
            {
                _sourceVATAsset.AnimatorBlendTrees = new List<VATAnimatorBlendTreeData>();
            }

            if (_sourceVATAsset.AnimatorGraph == null)
            {
                _sourceVATAsset.AnimatorGraph = new VATAnimatorGraphData();
            }
            if (Graph.nodes == null) Graph.nodes = new List<VATAnimatorNodeData>();
            if (Graph.edges == null) Graph.edges = new List<VATAnimatorEdgeData>();
            if (Graph.schemaVersion < VATAnimatorGraphData.CurrentSchemaVersion)
            {
                Graph.schemaVersion = VATAnimatorGraphData.CurrentSchemaVersion;
            }

            if (Graph.nextNodeId < 1) Graph.nextNodeId = 1;
            if (Graph.nextParameterId < 1) Graph.nextParameterId = 1;
            if (Graph.nextConditionId < 1) Graph.nextConditionId = 1;
            if (Graph.nextTransitionId < 1) Graph.nextTransitionId = 1;
            if (Graph.nextBlendTreeId < 1) Graph.nextBlendTreeId = 1;
            if (Graph.nextBlendChildId < 1) Graph.nextBlendChildId = 1;

            for (int i = 0; i < _sourceVATAsset.AnimatorParameters.Count; i++)
            {
                VATAnimatorParameterData parameter = _sourceVATAsset.AnimatorParameters[i];
                if (parameter == null) continue;
                if (parameter.id <= 0) parameter.id = Graph.nextParameterId++;
                if (parameter.id >= Graph.nextParameterId) Graph.nextParameterId = parameter.id + 1;
            }

            for (int i = 0; i < _sourceVATAsset.AnimatorTransitions.Count; i++)
            {
                VATAnimatorTransitionData transition = _sourceVATAsset.AnimatorTransitions[i];
                if (transition == null) continue;
                if (transition.id <= 0) transition.id = Graph.nextTransitionId++;
                if (transition.id >= Graph.nextTransitionId) Graph.nextTransitionId = transition.id + 1;
            }

            for (int i = 0; i < _sourceVATAsset.AnimatorBlendTrees.Count; i++)
            {
                VATAnimatorBlendTreeData blendTree = _sourceVATAsset.AnimatorBlendTrees[i];
                if (blendTree == null) continue;
                if (blendTree.id <= 0) blendTree.id = Graph.nextBlendTreeId++;
                if (blendTree.id >= Graph.nextBlendTreeId) Graph.nextBlendTreeId = blendTree.id + 1;
                if (blendTree.children == null) blendTree.children = new List<VATAnimatorBlendChildData>();
                for (int c = 0; c < blendTree.children.Count; c++)
                {
                    VATAnimatorBlendChildData child = blendTree.children[c];
                    if (child == null) continue;
                    if (child.id <= 0) child.id = Graph.nextBlendChildId++;
                    if (child.id >= Graph.nextBlendChildId) Graph.nextBlendChildId = child.id + 1;
                    VATAnimatorClipData childClip = FindClipByKey(child.clipKey);
                    if (childClip != null) child.stateHash = childClip.stateHash;
                }
            }

            for (int i = 0; i < Graph.nodes.Count; i++)
            {
                VATAnimatorNodeData node = Graph.nodes[i];
                if (node == null) continue;
                if (node.id <= 0) node.id = Graph.nextNodeId++;
                if (node.id >= Graph.nextNodeId) Graph.nextNodeId = node.id + 1;
            }

            EnsureDefaultNode();

            for (int i = 0; i < _sourceVATAsset.AnimatorTransitions.Count; i++)
            {
                VATAnimatorTransitionData transition = _sourceVATAsset.AnimatorTransitions[i];
                if (transition == null || transition.conditions == null) continue;
                for (int c = 0; c < transition.conditions.Count; c++)
                {
                    VATAnimatorConditionData condition = transition.conditions[c];
                    if (condition == null) continue;
                    if (condition.id <= 0) condition.id = AllocateConditionId();
                    if (condition.id >= Graph.nextConditionId) Graph.nextConditionId = condition.id + 1;
                }
            }

            for (int i = 0; i < Graph.edges.Count; i++)
            {
                VATAnimatorEdgeData edge = Graph.edges[i];
                if (edge == null) continue;

                VATAnimatorNodeData outputNode = FindNode(edge.outputNodeId);
                if (outputNode != null && outputNode.nodeType == VATAnimatorNodeType.Parameter &&
                    edge.outputPortName == "Value")
                {
                    VATAnimatorParameterData parameter = FindParameter(outputNode.parameterId);
                    if (parameter != null) edge.outputPortName = parameter.type.ToString();
                }

                VATAnimatorNodeData inputNode = FindNode(edge.inputNodeId);
                if (inputNode != null && inputNode.nodeType == VATAnimatorNodeType.Transition &&
                    edge.inputPortName == "Conditions")
                {
                    VATAnimatorTransitionData transition = FindTransition(inputNode.transitionId);
                    if (transition != null && transition.conditions != null && transition.conditions.Count > 0)
                    {
                        VATAnimatorConditionData condition = transition.conditions[0];
                        if (condition != null) edge.inputPortName = GetConditionPortName(condition.id);
                    }
                }
            }

        }

        public static string MakeClipKey(VATClipInfo clip)
        {
            if (clip == null) return string.Empty;
            return clip.StateHash.ToString() + ":" + (clip.ClipName ?? string.Empty);
        }

        private void RebuildClipProjections()
        {
            _clipProjections.Clear();
            if (_sourceVATAsset == null || _sourceVATAsset.Clips == null) return;

            for (int i = 0; i < _sourceVATAsset.Clips.Count; i++)
            {
                VATClipInfo clip = _sourceVATAsset.Clips[i];
                if (clip != null && !string.IsNullOrEmpty(clip.ClipName))
                {
                    _clipProjections.Add(new VATAnimatorClipData(clip));
                }
            }
        }

        public VATAnimatorClipData FindClipByKey(string clipKey)
        {
            if (string.IsNullOrEmpty(clipKey)) return null;
            List<VATAnimatorClipData> allClips = clips;
            for (int i = 0; i < allClips.Count; i++)
            {
                if (allClips[i].clipKey == clipKey) return allClips[i];
            }
            return null;
        }

        public VATAnimatorClipData FindClipByHash(int stateHash)
        {
            List<VATAnimatorClipData> allClips = clips;
            for (int i = 0; i < allClips.Count; i++)
            {
                if (allClips[i].stateHash == stateHash) return allClips[i];
            }
            return null;
        }

        public VATAnimatorParameterData FindParameter(int id)
        {
            if (parameters == null) return null;
            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i] != null && parameters[i].id == id) return parameters[i];
            }
            return null;
        }

        public VATAnimatorTransitionData FindTransition(int id)
        {
            if (transitions == null) return null;
            for (int i = 0; i < transitions.Count; i++)
            {
                if (transitions[i] != null && transitions[i].id == id) return transitions[i];
            }
            return null;
        }

        public VATAnimatorBlendTreeData FindBlendTree(int id)
        {
            if (blendTrees == null) return null;
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
                if (min != float.PositiveInfinity) input.x = Mathf.Clamp(input.x, min, max);
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
            if (nodes == null) return null;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] != null && nodes[i].id == id) return nodes[i];
            }
            return null;
        }

        public VATAnimatorNodeData FindDefaultNode()
        {
            if (nodes == null) return null;
            for (int i = 0; i < nodes.Count; i++)
            {
                VATAnimatorNodeData node = nodes[i];
                if (node != null && node.nodeType == VATAnimatorNodeType.Default) return node;
            }
            return null;
        }

        private VATAnimatorNodeData EnsureDefaultNode()
        {
            VATAnimatorNodeData existing = FindDefaultNode();
            if (existing != null) return existing;

            VATAnimatorNodeData node = new VATAnimatorNodeData
            {
                id = AllocateNodeId(),
                nodeType = VATAnimatorNodeType.Default,
                title = "Default",
                position = new Vector2(-320f, 80f)
            };
            nodes.Add(node);
            return node;
        }

        public int AllocateNodeId()
        {
            if (Graph.nextNodeId < 1) Graph.nextNodeId = 1;
            return Graph.nextNodeId++;
        }

        public int AllocateParameterId()
        {
            if (Graph.nextParameterId < 1) Graph.nextParameterId = 1;
            return Graph.nextParameterId++;
        }

        public int AllocateConditionId()
        {
            if (Graph.nextConditionId < 1) Graph.nextConditionId = 1;
            return Graph.nextConditionId++;
        }

        public int AllocateTransitionId()
        {
            if (Graph.nextTransitionId < 1) Graph.nextTransitionId = 1;
            return Graph.nextTransitionId++;
        }

        public int AllocateBlendTreeId()
        {
            if (Graph.nextBlendTreeId < 1) Graph.nextBlendTreeId = 1;
            return Graph.nextBlendTreeId++;
        }

        public int AllocateBlendChildId()
        {
            if (Graph.nextBlendChildId < 1) Graph.nextBlendChildId = 1;
            return Graph.nextBlendChildId++;
        }

        public int SyncFromVATAssetData(bool removeMissingClips = true)
        {
            EnsureLists();
            if (_sourceVATAsset == null) return 0;

            HashSet<string> validKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _sourceVATAsset.Clips.Count; i++)
            {
                VATClipInfo clip = _sourceVATAsset.Clips[i];
                if (clip != null && !string.IsNullOrEmpty(clip.ClipName))
                {
                    string clipKey = MakeClipKey(clip);
                    validKeys.Add(clipKey);
                }
            }

            if (!string.IsNullOrEmpty(defaultClipKey) && !validKeys.Contains(defaultClipKey))
            {
                defaultClipKey = string.Empty;
            }

            if (removeMissingClips)
            {
                nodes.RemoveAll(node => node == null ||
                    (node.nodeType == VATAnimatorNodeType.Clip && !validKeys.Contains(node.clipKey)) ||
                    (node.nodeType == VATAnimatorNodeType.Parameter && FindParameter(node.parameterId) == null));

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

            List<VATAnimatorClipData> sourceClips = clips;
            for (int i = 0; i < sourceClips.Count; i++)
            {
                VATAnimatorClipData clip = sourceClips[i];
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
            EnsureDefaultEdge(validKeys);
            PruneBlendChildrenWithoutEdges();
            SyncRuntimeMappingsFromGraph();
            return sourceClips.Count;
        }

        private void EnsureDefaultEdge(HashSet<string> validKeys)
        {
            VATAnimatorNodeData defaultNode = FindDefaultNode();
            if (defaultNode == null) return;

            VATAnimatorEdgeData existingEdge = edges.FirstOrDefault(edge => edge != null &&
                edge.outputNodeId == defaultNode.id && edge.outputPortName == "Out");
            if (existingEdge != null)
            {
                VATAnimatorNodeData targetNode = FindNode(existingEdge.inputNodeId);
                if (targetNode != null && targetNode.nodeType == VATAnimatorNodeType.Clip &&
                    validKeys.Contains(targetNode.clipKey))
                {
                    defaultClipKey = targetNode.clipKey;
                    SyncRuntimeDefaultState();
                }
                else
                {
                    edges.Remove(existingEdge);
                    defaultClipKey = string.Empty;
                    _sourceVATAsset.DefaultStateName = 0;
                }
                return;
            }

            if (string.IsNullOrEmpty(defaultClipKey)) return;
            VATAnimatorNodeData clipNode = nodes.FirstOrDefault(node => node != null &&
                node.nodeType == VATAnimatorNodeType.Clip && node.clipKey == defaultClipKey);
            if (clipNode == null) return;

            edges.Add(new VATAnimatorEdgeData
            {
                outputNodeId = defaultNode.id,
                outputPortName = "Out",
                inputNodeId = clipNode.id,
                inputPortName = "In"
            });
            SyncRuntimeDefaultState();
        }

        private void SyncRuntimeDefaultState()
        {
            if (_sourceVATAsset == null) return;
            VATAnimatorClipData clip = FindClipByKey(defaultClipKey);
            _sourceVATAsset.DefaultStateName = clip == null ? 0 : clip.stateHash;
        }

        public void SyncRuntimeMappingsFromGraph()
        {
            if (_sourceVATAsset == null) return;

            for (int i = 0; i < transitions.Count; i++)
            {
                VATAnimatorTransitionData transition = transitions[i];
                if (transition == null) continue;
                transition.fromStateHash = 0;
                transition.toStateHash = 0;
                transition.toBlendTreeId = -1;
            }

            bool hasDefaultEdge = false;
            for (int i = 0; i < edges.Count; i++)
            {
                VATAnimatorEdgeData edge = edges[i];
                ApplyRuntimeEdgeMapping(edge);
                if (edge != null && edge.outputPortName == "Out" && edge.inputPortName == "In")
                {
                    VATAnimatorNodeData outputNode = FindNode(edge.outputNodeId);
                    VATAnimatorNodeData inputNode = FindNode(edge.inputNodeId);
                    hasDefaultEdge = hasDefaultEdge ||
                        outputNode != null && outputNode.nodeType == VATAnimatorNodeType.Default &&
                        inputNode != null && inputNode.nodeType == VATAnimatorNodeType.Clip;
                }
            }

            if (!hasDefaultEdge)
            {
                defaultClipKey = string.Empty;
                _sourceVATAsset.DefaultStateName = 0;
            }
        }

        public bool ApplyRuntimeEdgeMapping(VATAnimatorEdgeData edge)
        {
            if (edge == null) return false;
            VATAnimatorNodeData outputNode = FindNode(edge.outputNodeId);
            VATAnimatorNodeData inputNode = FindNode(edge.inputNodeId);
            if (outputNode == null || inputNode == null) return false;

            if (outputNode.nodeType == VATAnimatorNodeType.Clip &&
                inputNode.nodeType == VATAnimatorNodeType.Transition &&
                edge.outputPortName == "Out" && edge.inputPortName == "From")
            {
                VATAnimatorTransitionData transition = FindTransition(inputNode.transitionId);
                VATAnimatorClipData clip = FindClipByKey(outputNode.clipKey);
                if (transition == null || clip == null) return false;
                transition.fromStateHash = clip.stateHash;
                return true;
            }

            if (outputNode.nodeType == VATAnimatorNodeType.Transition &&
                edge.outputPortName == "To")
            {
                VATAnimatorTransitionData transition = FindTransition(outputNode.transitionId);
                if (transition == null) return false;

                if (inputNode.nodeType == VATAnimatorNodeType.Clip && edge.inputPortName == "In")
                {
                    VATAnimatorClipData clip = FindClipByKey(inputNode.clipKey);
                    if (clip == null) return false;
                    transition.toStateHash = clip.stateHash;
                    transition.toBlendTreeId = -1;
                    return true;
                }

                if (inputNode.nodeType == VATAnimatorNodeType.BlendTree && edge.inputPortName == "Entry")
                {
                    transition.toStateHash = 0;
                    transition.toBlendTreeId = inputNode.blendTreeId;
                    return true;
                }
            }

            if (outputNode.nodeType == VATAnimatorNodeType.Default &&
                inputNode.nodeType == VATAnimatorNodeType.Clip &&
                edge.outputPortName == "Out" && edge.inputPortName == "In")
            {
                defaultClipKey = inputNode.clipKey;
                SyncRuntimeDefaultState();
                return true;
            }

            return false;
        }

        public bool ClearRuntimeEdgeMapping(VATAnimatorEdgeData edge)
        {
            if (edge == null) return false;
            VATAnimatorNodeData outputNode = FindNode(edge.outputNodeId);
            VATAnimatorNodeData inputNode = FindNode(edge.inputNodeId);
            if (outputNode == null || inputNode == null) return false;

            if (outputNode.nodeType == VATAnimatorNodeType.Clip &&
                inputNode.nodeType == VATAnimatorNodeType.Transition &&
                edge.outputPortName == "Out" && edge.inputPortName == "From")
            {
                VATAnimatorTransitionData transition = FindTransition(inputNode.transitionId);
                if (transition == null) return false;
                transition.fromStateHash = 0;
                return true;
            }

            if (outputNode.nodeType == VATAnimatorNodeType.Transition &&
                inputNode.nodeType == VATAnimatorNodeType.Clip &&
                edge.outputPortName == "To" && edge.inputPortName == "In")
            {
                VATAnimatorTransitionData transition = FindTransition(outputNode.transitionId);
                if (transition == null) return false;
                transition.toStateHash = 0;
                transition.toBlendTreeId = -1;
                return true;
            }

            if (outputNode.nodeType == VATAnimatorNodeType.Transition &&
                inputNode.nodeType == VATAnimatorNodeType.BlendTree &&
                edge.outputPortName == "To" && edge.inputPortName == "Entry")
            {
                VATAnimatorTransitionData transition = FindTransition(outputNode.transitionId);
                if (transition == null) return false;
                transition.toStateHash = 0;
                transition.toBlendTreeId = -1;
                return true;
            }

            if (outputNode.nodeType == VATAnimatorNodeType.Default &&
                inputNode.nodeType == VATAnimatorNodeType.Clip &&
                edge.outputPortName == "Out" && edge.inputPortName == "In")
            {
                defaultClipKey = string.Empty;
                if (_sourceVATAsset != null) _sourceVATAsset.DefaultStateName = 0;
                return true;
            }

            return false;
        }

        public VATAnimatorNodeData AddClipNode(string clipKey, Vector2 position)
        {
            EnsureLists();
            VATAnimatorClipData clip = FindClipByKey(clipKey);
            if (clip == null) return null;

            VATAnimatorNodeData node = new VATAnimatorNodeData
            {
                id = AllocateNodeId(),
                nodeType = VATAnimatorNodeType.Clip,
                title = clip.clipName,
                clipKey = clipKey,
                position = position
            };
            nodes.Add(node);
            return node;
        }

        public VATAnimatorNodeData AddDefaultNode()
        {
            EnsureLists();
            return EnsureDefaultNode();
        }

        public VATAnimatorParameterData CreateParameter(VATAnimatorParameterType type, string parameterName = null)
        {
            EnsureLists();
            int parameterId = AllocateParameterId();
            VATAnimatorParameterData parameter = new VATAnimatorParameterData
            {
                id = parameterId,
                parameterName = string.IsNullOrEmpty(parameterName)
                    ? type + " " + parameterId
                    : parameterName,
                type = type
            };
            parameters.Add(parameter);
            return parameter;
        }

        public bool RenameParameter(int parameterId, string parameterName)
        {
            VATAnimatorParameterData parameter = FindParameter(parameterId);
            if (parameter == null) return false;
            parameter.parameterName = string.IsNullOrWhiteSpace(parameterName)
                ? parameter.type + " " + parameter.id
                : parameterName;
            return true;
        }

        public bool ChangeParameterType(int parameterId, VATAnimatorParameterType type)
        {
            VATAnimatorParameterData parameter = FindParameter(parameterId);
            if (parameter == null) return false;
            if (parameter.type == type) return false;

            parameter.type = type;
            string parameterPortName = type.ToString();
            for (int i = 0; i < nodes.Count; i++)
            {
                VATAnimatorNodeData node = nodes[i];
                if (node == null || node.nodeType != VATAnimatorNodeType.Parameter ||
                    node.parameterId != parameterId) continue;
                for (int e = 0; e < edges.Count; e++)
                {
                    if (edges[e] != null && edges[e].outputNodeId == node.id)
                    {
                        edges[e].outputPortName = parameterPortName;
                    }
                }
            }

            for (int i = 0; i < transitions.Count; i++)
            {
                VATAnimatorTransitionData transition = transitions[i];
                if (transition == null || transition.conditions == null) continue;
                for (int c = 0; c < transition.conditions.Count; c++)
                {
                    VATAnimatorConditionData condition = transition.conditions[c];
                    if (condition != null && condition.parameterId == parameterId)
                    {
                        NormalizeCondition(condition, type);
                    }
                }
            }

            for (int i = 0; i < blendTrees.Count; i++)
            {
                VATAnimatorBlendTreeData tree = blendTrees[i];
                if (tree == null || tree.parameterId != parameterId) continue;
                if (type != VATAnimatorParameterType.Float && type != VATAnimatorParameterType.Vector2)
                {
                    tree.parameterId = -1;
                    tree.mode = VATAnimatorBlendTreeMode.OneDimensional;
                    int blendTreeNodeId = FindNodeForBlendTree(tree.id);
                    edges.RemoveAll(edge => edge != null && edge.inputNodeId == blendTreeNodeId &&
                        edge.inputPortName == "Parameter");
                }
                else
                {
                    tree.mode = type == VATAnimatorParameterType.Vector2
                        ? VATAnimatorBlendTreeMode.TwoDimensional
                        : VATAnimatorBlendTreeMode.OneDimensional;
                }
            }
            return true;
        }

        private int FindNodeForBlendTree(int blendTreeId)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                VATAnimatorNodeData node = nodes[i];
                if (node != null && node.nodeType == VATAnimatorNodeType.BlendTree &&
                    node.blendTreeId == blendTreeId)
                {
                    return node.id;
                }
            }
            return -1;
        }

        public VATAnimatorNodeData AddParameterReferenceNode(int parameterId, Vector2 position)
        {
            EnsureLists();
            VATAnimatorParameterData parameter = FindParameter(parameterId);
            if (parameter == null) return null;

            VATAnimatorNodeData node = new VATAnimatorNodeData
            {
                id = AllocateNodeId(),
                nodeType = VATAnimatorNodeType.Parameter,
                title = parameter.parameterName,
                parameterId = parameterId,
                position = position
            };
            nodes.Add(node);
            return node;
        }

        public bool RemoveParameter(int parameterId)
        {
            EnsureLists();
            VATAnimatorParameterData parameter = FindParameter(parameterId);
            if (parameter == null) return false;

            parameters.Remove(parameter);
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                VATAnimatorNodeData node = nodes[i];
                if (node != null && node.nodeType == VATAnimatorNodeType.Parameter &&
                    node.parameterId == parameterId)
                {
                    RemoveNode(node.id);
                }
            }

            for (int i = 0; i < transitions.Count; i++)
            {
                VATAnimatorTransitionData transition = transitions[i];
                if (transition == null || transition.conditions == null) continue;
                transition.conditions.RemoveAll(condition => condition == null || condition.parameterId == parameterId);
            }

            for (int i = 0; i < blendTrees.Count; i++)
            {
                if (blendTrees[i] != null && blendTrees[i].parameterId == parameterId)
                {
                    blendTrees[i].parameterId = -1;
                    blendTrees[i].mode = VATAnimatorBlendTreeMode.OneDimensional;
                }
            }

            PruneDanglingEdges();
            return true;
        }

        public VATAnimatorConditionData AddTransitionCondition(int transitionId)
        {
            EnsureLists();
            VATAnimatorTransitionData transition = FindTransition(transitionId);
            if (transition == null) return null;
            if (transition.conditions == null) transition.conditions = new List<VATAnimatorConditionData>();

            VATAnimatorConditionData condition = new VATAnimatorConditionData
            {
                id = AllocateConditionId(),
                parameterId = -1
            };
            transition.conditions.Add(condition);
            return condition;
        }

        public VATAnimatorConditionData FindCondition(int transitionId, int conditionId)
        {
            VATAnimatorTransitionData transition = FindTransition(transitionId);
            if (transition == null || transition.conditions == null) return null;
            for (int i = 0; i < transition.conditions.Count; i++)
            {
                VATAnimatorConditionData condition = transition.conditions[i];
                if (condition != null && condition.id == conditionId) return condition;
            }
            return null;
        }

        public bool RemoveTransitionCondition(int transitionId, int conditionId)
        {
            VATAnimatorTransitionData transition = FindTransition(transitionId);
            if (transition == null || transition.conditions == null) return false;
            int removed = transition.conditions.RemoveAll(condition => condition == null || condition.id == conditionId);
            edges.RemoveAll(edge => edge != null && edge.inputNodeId == FindNodeForTransition(transitionId) &&
                edge.inputPortName == GetConditionPortName(conditionId));
            return removed > 0;
        }

        private int FindNodeForTransition(int transitionId)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                VATAnimatorNodeData node = nodes[i];
                if (node != null && node.nodeType == VATAnimatorNodeType.Transition &&
                    node.transitionId == transitionId)
                {
                    return node.id;
                }
            }
            return -1;
        }

        public static string GetConditionPortName(int conditionId)
        {
            return "Condition:" + conditionId;
        }

        public const string NewConditionPortName = "[New]";

        public static string GetBlendCasePortName(int childId)
        {
            return "Case:" + childId;
        }

        public const string NewBlendCasePortName = "[New]";

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

        public VATAnimatorBlendChildData FindBlendChild(int blendTreeId, int childId)
        {
            VATAnimatorBlendTreeData tree = FindBlendTree(blendTreeId);
            if (tree == null || tree.children == null) return null;
            for (int i = 0; i < tree.children.Count; i++)
            {
                VATAnimatorBlendChildData child = tree.children[i];
                if (child != null && child.id == childId) return child;
            }
            return null;
        }

        public bool TryCreateTransitionConditionEdge(VATAnimatorEdgeData edge)
        {
            if (edge == null || edge.inputPortName != NewConditionPortName) return false;

            VATAnimatorNodeData outputNode = FindNode(edge.outputNodeId);
            VATAnimatorNodeData inputNode = FindNode(edge.inputNodeId);
            if (outputNode == null || inputNode == null ||
                outputNode.nodeType != VATAnimatorNodeType.Parameter ||
                inputNode.nodeType != VATAnimatorNodeType.Transition)
            {
                return false;
            }

            VATAnimatorParameterData parameter = FindParameter(outputNode.parameterId);
            if (parameter == null) return false;
            VATAnimatorConditionData condition = AddTransitionCondition(inputNode.transitionId);
            if (condition == null) return false;

            condition.parameterId = parameter.id;
            NormalizeCondition(condition, parameter.type);
            edge.inputPortName = GetConditionPortName(condition.id);
            return true;
        }

        public bool TryBindBlendTreeCaseEdge(VATAnimatorEdgeData edge)
        {
            if (edge == null || edge.inputPortName != "In") return false;

            VATAnimatorNodeData outputNode = FindNode(edge.outputNodeId);
            VATAnimatorNodeData inputNode = FindNode(edge.inputNodeId);
            if (outputNode == null || inputNode == null ||
                outputNode.nodeType != VATAnimatorNodeType.BlendTree ||
                inputNode.nodeType != VATAnimatorNodeType.Clip)
            {
                return false;
            }

            VATAnimatorBlendTreeData tree = FindBlendTree(outputNode.blendTreeId);
            if (tree == null) return false;
            if (tree.children == null) tree.children = new List<VATAnimatorBlendChildData>();

            VATAnimatorBlendChildData child = null;
            if (edge.outputPortName == NewBlendCasePortName)
            {
                child = new VATAnimatorBlendChildData
                {
                    id = AllocateBlendChildId(),
                    clipKey = inputNode.clipKey,
                    stateHash = GetClipStateHash(inputNode.clipKey),
                    threshold = GetNextBlendThreshold(tree)
                };
                tree.children.Add(child);
                edge.outputPortName = GetBlendCasePortName(child.id);
            }
            else
            {
                int childId;
                if (!TryParseBlendCasePort(edge.outputPortName, out childId)) return false;
                child = FindBlendChild(tree.id, childId);
                if (child == null) return false;
                child.clipKey = inputNode.clipKey;
                child.stateHash = GetClipStateHash(inputNode.clipKey);
            }

            return true;
        }

        private int GetClipStateHash(string clipKey)
        {
            VATAnimatorClipData clip = FindClipByKey(clipKey);
            return clip == null ? 0 : clip.stateHash;
        }

        private static Vector2 GetNextBlendThreshold(VATAnimatorBlendTreeData tree)
        {
            if (tree == null || tree.children == null) return Vector2.zero;
            float next = tree.children.Count;
            return tree.mode == VATAnimatorBlendTreeMode.TwoDimensional
                ? new Vector2(next, 0f)
                : new Vector2(next, 0f);
        }

        public bool RemoveBlendChildForEdge(VATAnimatorEdgeData edge)
        {
            if (edge == null || edge.inputPortName != "In") return false;
            VATAnimatorNodeData outputNode = FindNode(edge.outputNodeId);
            if (outputNode == null || outputNode.nodeType != VATAnimatorNodeType.BlendTree) return false;

            int childId;
            if (!TryParseBlendCasePort(edge.outputPortName, out childId)) return false;
            VATAnimatorBlendTreeData tree = FindBlendTree(outputNode.blendTreeId);
            return tree != null && tree.children != null &&
                tree.children.RemoveAll(child => child == null || child.id == childId) > 0;
        }

        private static bool TryParseBlendCasePort(string portName, out int childId)
        {
            childId = -1;
            return !string.IsNullOrEmpty(portName) &&
                   portName.StartsWith("Case:", StringComparison.Ordinal) &&
                   int.TryParse(portName.Substring("Case:".Length), out childId) &&
                   childId > 0;
        }

        private void PruneBlendChildrenWithoutEdges()
        {
            for (int i = 0; i < blendTrees.Count; i++)
            {
                VATAnimatorBlendTreeData tree = blendTrees[i];
                if (tree == null || tree.children == null) continue;
                int nodeId = FindNodeForBlendTree(tree.id);
                tree.children.RemoveAll(child => child == null || nodeId < 0 ||
                    !edges.Any(edge => edge != null && edge.outputNodeId == nodeId &&
                        edge.outputPortName == GetBlendCasePortName(child.id)));
            }
        }

        public void RemoveNode(int nodeId)
        {
            VATAnimatorNodeData node = FindNode(nodeId);
            if (node == null) return;

            nodes.Remove(node);
            for (int i = edges.Count - 1; i >= 0; i--)
            {
                VATAnimatorEdgeData edge = edges[i];
                if (edge == null) continue;
                if (edge.outputNodeId == nodeId || edge.inputNodeId == nodeId)
                {
                    RemoveBlendChildForEdge(edge);
                    ClearRuntimeEdgeMapping(edge);
                    ClearDefaultEdge(edge);
                    ClearParameterEdge(edge);
                    edges.RemoveAt(i);
                }
            }

            if (node.nodeType == VATAnimatorNodeType.Parameter)
            {
                // A Parameter node is only a visual reference. Its data is owned by the Blackboard.
            }
            else if (node.nodeType == VATAnimatorNodeType.Transition)
            {
                transitions.RemoveAll(transition => transition == null || transition.id == node.transitionId);
            }
            else if (node.nodeType == VATAnimatorNodeType.BlendTree)
            {
                blendTrees.RemoveAll(tree => tree == null || tree.id == node.blendTreeId);
            }
            else if (node.nodeType == VATAnimatorNodeType.Default)
            {
                defaultClipKey = string.Empty;
                if (_sourceVATAsset != null) _sourceVATAsset.DefaultStateName = 0;
            }

            SyncRuntimeMappingsFromGraph();
        }

        public bool AddEdge(VATAnimatorEdgeData edge)
        {
            if (!CanAddEdge(edge))
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

        public bool CanAddEdge(VATAnimatorEdgeData edge)
        {
            if (edge == null) return false;
            VATAnimatorNodeData outputNode = FindNode(edge.outputNodeId);
            VATAnimatorNodeData inputNode = FindNode(edge.inputNodeId);
            if (outputNode == null || inputNode == null || outputNode.id == inputNode.id) return false;

            if (outputNode.nodeType == VATAnimatorNodeType.Parameter)
            {
                VATAnimatorParameterData parameter = FindParameter(outputNode.parameterId);
                if (parameter == null) return false;
                if (inputNode.nodeType == VATAnimatorNodeType.Transition &&
                    (edge.inputPortName == NewConditionPortName ||
                     IsConditionPortName(edge.inputPortName)))
                {
                    return edge.outputPortName == parameter.type.ToString() || edge.outputPortName == "Value";
                }

                return inputNode.nodeType == VATAnimatorNodeType.BlendTree &&
                       edge.inputPortName == "Parameter" &&
                       (parameter.type == VATAnimatorParameterType.Float ||
                        parameter.type == VATAnimatorParameterType.Vector2) &&
                       (edge.outputPortName == parameter.type.ToString() || edge.outputPortName == "Value");
            }

            if (outputNode.nodeType == VATAnimatorNodeType.Clip &&
                inputNode.nodeType == VATAnimatorNodeType.Transition)
            {
                return edge.outputPortName == "Out" && edge.inputPortName == "From";
            }

            if (outputNode.nodeType == VATAnimatorNodeType.Transition &&
                (inputNode.nodeType == VATAnimatorNodeType.Clip ||
                 inputNode.nodeType == VATAnimatorNodeType.BlendTree))
            {
                return edge.outputPortName == "To" &&
                       ((inputNode.nodeType == VATAnimatorNodeType.Clip && edge.inputPortName == "In") ||
                        (inputNode.nodeType == VATAnimatorNodeType.BlendTree && edge.inputPortName == "Entry"));
            }

            if (outputNode.nodeType == VATAnimatorNodeType.BlendTree &&
                inputNode.nodeType == VATAnimatorNodeType.Clip)
            {
                return edge.inputPortName == "In" &&
                       (edge.outputPortName == NewBlendCasePortName ||
                        IsBlendCasePortName(edge.outputPortName));
            }

            if (outputNode.nodeType == VATAnimatorNodeType.Default &&
                inputNode.nodeType == VATAnimatorNodeType.Clip)
            {
                return edge.outputPortName == "Out" && edge.inputPortName == "In";
            }

            return false;
        }

        public bool HandleDefaultEdge(VATAnimatorEdgeData edge)
        {
            if (edge == null || edge.outputPortName != "Out" || edge.inputPortName != "In") return false;
            VATAnimatorNodeData outputNode = FindNode(edge.outputNodeId);
            VATAnimatorNodeData inputNode = FindNode(edge.inputNodeId);
            if (outputNode == null || inputNode == null ||
                outputNode.nodeType != VATAnimatorNodeType.Default ||
                inputNode.nodeType != VATAnimatorNodeType.Clip)
            {
                return false;
            }

            defaultClipKey = inputNode.clipKey;
            SyncRuntimeDefaultState();
            return true;
        }

        public bool ClearDefaultEdge(VATAnimatorEdgeData edge)
        {
            if (edge == null || edge.outputPortName != "Out" || edge.inputPortName != "In") return false;
            VATAnimatorNodeData outputNode = FindNode(edge.outputNodeId);
            VATAnimatorNodeData inputNode = FindNode(edge.inputNodeId);
            if (outputNode == null || inputNode == null ||
                outputNode.nodeType != VATAnimatorNodeType.Default ||
                inputNode.nodeType != VATAnimatorNodeType.Clip)
            {
                return false;
            }

            bool changed = !string.IsNullOrEmpty(defaultClipKey);
            defaultClipKey = string.Empty;
            return changed;
        }

        public void HandleParameterEdge(int parameterId, int targetNodeId, string targetPortName)
        {
            VATAnimatorNodeData targetNode = FindNode(targetNodeId);
            if (targetNode == null) return;

            VATAnimatorParameterData parameter = FindParameter(parameterId);
            if (parameter == null) return;

            if (targetNode.nodeType == VATAnimatorNodeType.BlendTree && targetPortName == "Parameter")
            {
                VATAnimatorBlendTreeData tree = FindBlendTree(targetNode.blendTreeId);
                if (tree != null)
                {
                    tree.parameterId = parameterId;
                    tree.mode = parameter.type == VATAnimatorParameterType.Vector2
                        ? VATAnimatorBlendTreeMode.TwoDimensional
                        : VATAnimatorBlendTreeMode.OneDimensional;
                }
            }
            else if (targetNode.nodeType == VATAnimatorNodeType.Transition &&
                     targetPortName.StartsWith("Condition:", StringComparison.Ordinal))
            {
                VATAnimatorTransitionData transition = FindTransition(targetNode.transitionId);
                if (transition == null) return;
                if (transition.conditions == null) transition.conditions = new List<VATAnimatorConditionData>();

                int conditionId;
                if (int.TryParse(targetPortName.Substring("Condition:".Length), out conditionId))
                {
                    VATAnimatorConditionData condition = FindCondition(targetNode.transitionId, conditionId);
                    if (condition != null)
                    {
                        condition.parameterId = parameterId;
                        NormalizeCondition(condition, parameter.type);
                    }
                }
            }
        }

        public bool ClearParameterEdge(VATAnimatorEdgeData edge)
        {
            if (edge == null) return false;
            VATAnimatorNodeData targetNode = FindNode(edge.inputNodeId);
            if (targetNode == null) return false;

            if (targetNode.nodeType == VATAnimatorNodeType.BlendTree && edge.inputPortName == "Parameter")
            {
                VATAnimatorBlendTreeData tree = FindBlendTree(targetNode.blendTreeId);
                if (tree != null)
                {
                    bool changed = tree.parameterId != -1;
                    tree.parameterId = -1;
                    tree.mode = VATAnimatorBlendTreeMode.OneDimensional;
                    return changed;
                }
            }
            else if (targetNode.nodeType == VATAnimatorNodeType.Transition &&
                     edge.inputPortName.StartsWith("Condition:", StringComparison.Ordinal))
            {
                int conditionId;
                if (int.TryParse(edge.inputPortName.Substring("Condition:".Length), out conditionId))
                {
                    VATAnimatorTransitionData transition = FindTransition(targetNode.transitionId);
                    if (transition == null || transition.conditions == null) return false;

                    int conditionIndex = -1;
                    for (int i = 0; i < transition.conditions.Count; i++)
                    {
                        VATAnimatorConditionData condition = transition.conditions[i];
                        if (condition != null && condition.id == conditionId)
                        {
                            conditionIndex = i;
                            break;
                        }
                    }

                    // Keep the first condition slot as the reusable anchor. Conditions
                    // created after it are represented entirely by their connected edges.
                    if (conditionIndex > 0)
                    {
                        transition.conditions.RemoveAll(condition => condition == null || condition.id == conditionId);
                        return true;
                    }
                    else
                    {
                        VATAnimatorConditionData condition = FindCondition(targetNode.transitionId, conditionId);
                        if (condition != null)
                        {
                            bool changed = condition.parameterId != -1;
                            condition.parameterId = -1;
                            return changed;
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsConditionPortName(string portName)
        {
            int conditionId;
            return portName != null && portName.StartsWith("Condition:", StringComparison.Ordinal) &&
                   int.TryParse(portName.Substring("Condition:".Length), out conditionId) && conditionId > 0;
        }

        private static bool IsBlendCasePortName(string portName)
        {
            int childId;
            return TryParseBlendCasePort(portName, out childId);
        }

        private static void NormalizeCondition(
            VATAnimatorConditionData condition,
            VATAnimatorParameterType parameterType)
        {
            VATAnimatorConditionMode[] modes = GetConditionModes(parameterType);
            if (Array.IndexOf(modes, condition.mode) < 0) condition.mode = modes[0];
        }

        private static VATAnimatorConditionMode[] GetConditionModes(VATAnimatorParameterType type)
        {
            switch (type)
            {
                case VATAnimatorParameterType.Trigger:
                    return new[] { VATAnimatorConditionMode.If };
                case VATAnimatorParameterType.Bool:
                    return new[] { VATAnimatorConditionMode.If, VATAnimatorConditionMode.IfNot };
                case VATAnimatorParameterType.Float:
                    return new[]
                    {
                        VATAnimatorConditionMode.Greater,
                        VATAnimatorConditionMode.Less,
                        VATAnimatorConditionMode.Equals,
                        VATAnimatorConditionMode.NotEquals
                    };
                case VATAnimatorParameterType.Vector2:
                    return new[]
                    {
                        VATAnimatorConditionMode.MagnitudeGreater,
                        VATAnimatorConditionMode.MagnitudeLess,
                        VATAnimatorConditionMode.Equals,
                        VATAnimatorConditionMode.NotEquals
                    };
                default:
                    return new[] { VATAnimatorConditionMode.If };
            }
        }

        public void PruneDanglingEdges()
        {
            if (edges == null) return;
            edges.RemoveAll(edge => edge == null || FindNode(edge.outputNodeId) == null || FindNode(edge.inputNodeId) == null);
        }

        public bool ValidateGraph(List<string> messages)
        {
            EnsureLists();
            if (messages == null) throw new ArgumentNullException("messages");
            messages.Clear();

            if (_sourceVATAsset == null) messages.Add("No VATAssetDataSO input is assigned.");
            if (clips.Count == 0) messages.Add("The VATAssetDataSO has no source clips.");
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
                if (transition.conditions == null) transition.conditions = new List<VATAnimatorConditionData>();
                if (transition.duration < 0f) messages.Add("Transition duration cannot be negative: " + transition.title);
                if (transition.fromStateHash == 0)
                {
                    messages.Add("Transition has no source Clip edge: " + transition.title);
                }
                if (transition.toStateHash == 0 && transition.toBlendTreeId <= 0)
                {
                    messages.Add("Transition has no target Clip or BlendTree edge: " + transition.title);
                }
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

            for (int i = 0; i < edges.Count; i++)
            {
                VATAnimatorEdgeData edge = edges[i];
                if (edge == null || !CanAddEdge(edge))
                {
                    messages.Add("Graph contains an incompatible edge.");
                }
            }

            return messages.Count == 0;
        }
    }
}
