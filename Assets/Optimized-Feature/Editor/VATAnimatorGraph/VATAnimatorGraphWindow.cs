using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using OptimizedFeature.Scripts;

namespace OptimizedFeature.Editor.VATAnimator
{
    /// <summary>
    /// Editor graph authoring tool for VATAssetDataSO clips.
    /// VATAssetDataSO is the only persisted object. The graph adapter used by GraphView is
    /// transient and only exists to keep the editor UI independent from serialized storage.
    /// </summary>
    public sealed class VATAnimatorGraphWindow : EditorWindow
    {
        private ObjectField sourceAssetField;
        private ObjectField animatorAssetField;
        private Button createAnimatorAssetButton;
        private Button blackboardToggleButton;
        private Label statusLabel;
        private VATAnimatorBlackboardView blackboardView;
        private VATAnimatorGraphView graphView;
        private VATAnimatorGraphAsset graphAsset;
        [SerializeField] private VATAssetDataSO sourceAsset;
        private int selectedNodeId = -1;
        private bool rebuildingGraph;
        private bool graphRebuildScheduled;
        private bool isBlackboardPopupVisible = true;

        internal VATAnimatorGraphAsset GraphAsset
        {
            get { return graphAsset; }
        }

        internal bool IsRebuildingGraph
        {
            get { return rebuildingGraph; }
        }

        internal bool LastEdgeRemovalChangedPorts { get; private set; }

        [MenuItem("Tools/VAT/VAT Animator Graph")]
        public static void OpenWindow()
        {
            VATAnimatorGraphWindow window = GetWindow<VATAnimatorGraphWindow>();
            window.titleContent = new GUIContent("VAT Animator Graph");
            window.minSize = new Vector2(1120f, 640f);
            window.Show();
        }

        /// <summary>
        /// Opens the animator graph for a VAT asset. Inspector callers can ask
        /// this shared entry point to perform the same Create New flow that the
        /// graph toolbar uses when no VATAssetAnimatorSO has been linked yet.
        /// </summary>
        public static void OpenForAsset(VATAssetDataSO source, bool createAnimatorIfMissing)
        {
            if (source == null)
            {
                return;
            }

            VATAnimatorGraphWindow window = GetWindow<VATAnimatorGraphWindow>();
            window.titleContent = new GUIContent("VAT Animator Graph");
            window.minSize = new Vector2(1120f, 640f);
            window.Show();
            window.SetSourceAsset(source);

            if (createAnimatorIfMissing && source.AnimatorAsset == null)
            {
                window.CreateAnimatorAsset();
                // Creating from an Inspector click happens after the first
                // source refresh. Refresh once more so the read-only Animator
                // Data field always reflects the newly linked asset.
                window.SetSourceAsset(source);
            }

            window.Focus();
        }

        [MenuItem("CONTEXT/VATAssetDataSO/Open VAT Animator Graph")]
        private static void OpenFromContext(MenuCommand command)
        {
            VATAssetDataSO source = command.context as VATAssetDataSO;
            OpenForAsset(source, false);
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("VAT Animator Graph");
            ConstructUI();
            if (sourceAsset != null)
            {
                EnsureGraphAdapter();
                bool createdDefaultNode = CreateDefaultNodeForEmptyAnimator();
                sourceAssetField.SetValueWithoutNotify(sourceAsset);
                RefreshAnimatorAssetField();
                graphView.RebuildGraph();
                blackboardView.Refresh();
                ShowMissingClipDialogIfNeeded();
                UpdateStatus();
                if (createdDefaultNode) FrameGraphOnNextLayout();
            }
        }

        private void OnDisable()
        {
            SaveGraph();
            if (graphAsset != null) DestroyImmediate(graphAsset);
            graphAsset = null;
        }

        private void ConstructUI()
        {
            rootVisualElement.Clear();

            Toolbar toolbar = new Toolbar();
            sourceAssetField = new ObjectField("VAT Input")
            {
                objectType = typeof(VATAssetDataSO),
                allowSceneObjects = false
            };
            sourceAssetField.style.width = 360f;
            sourceAssetField.RegisterValueChangedCallback(evt => SetSourceAsset(evt.newValue as VATAssetDataSO));
            toolbar.Add(sourceAssetField);

            animatorAssetField = new ObjectField("Animator Data")
            {
                objectType = typeof(VATAssetAnimatorSO),
                allowSceneObjects = false
            };
            animatorAssetField.style.width = 330f;
            animatorAssetField.SetEnabled(false);
            animatorAssetField.tooltip =
                "Read-only reference resolved from VAT Input.AnimatorAsset.";
            toolbar.Add(animatorAssetField);

            createAnimatorAssetButton = new Button(CreateAnimatorAsset)
            {
                text = "Create New"
            };
            toolbar.Add(createAnimatorAssetButton);

            toolbar.Add(new ToolbarSpacer { flex = true });
            toolbar.Add(new Button(SyncClips) { text = "Sync Clips" });
            toolbar.Add(new Button(SaveGraph) { text = "Save" });
            toolbar.Add(new Button(ValidateGraph) { text = "Validate" });
            toolbar.Add(new Button(() => graphView?.FrameAllView()) { text = "Frame All" });
            blackboardToggleButton = new Button(ToggleBlackboardPopup)
            {
                text = "Blackboard"
            };
            toolbar.Add(blackboardToggleButton);
            rootVisualElement.Add(toolbar);

            statusLabel = new Label();
            statusLabel.style.paddingLeft = 8f;
            statusLabel.style.paddingTop = 4f;
            statusLabel.style.paddingBottom = 4f;
            statusLabel.style.color = new Color(0.70f, 0.76f, 0.82f);
            rootVisualElement.Add(statusLabel);

            // Keep GraphView at the workspace origin. GraphView's built-in
            // RectangleSelector calculates against that origin; placing the
            // Blackboard in a left flex column offsets its selection rectangle.
            VisualElement graphHost = new VisualElement();
            graphHost.style.flexGrow = 1f;
            graphHost.style.position = Position.Relative;

            graphView = new VATAnimatorGraphView(this);
            graphView.style.flexGrow = 1f;
            graphHost.Add(graphView);

            blackboardView = new VATAnimatorBlackboardView(this);
            blackboardView.style.position = Position.Absolute;
            blackboardView.style.left = 10f;
            blackboardView.style.top = 10f;
            blackboardView.style.bottom = 10f;
            blackboardView.style.borderLeftWidth = 1f;
            blackboardView.style.borderTopWidth = 1f;
            blackboardView.style.borderBottomWidth = 1f;
            Color blackboardBorderColor = new Color(0.28f, 0.32f, 0.38f, 1f);
            blackboardView.style.borderLeftColor = blackboardBorderColor;
            blackboardView.style.borderTopColor = blackboardBorderColor;
            blackboardView.style.borderBottomColor = blackboardBorderColor;
            graphHost.Add(blackboardView);
            // The Blackboard is an overlay and therefore does not shift the GraphView's
            // coordinate system. Keep it open by default for every tool session.
            SetBlackboardPopupVisible(true);

            rootVisualElement.Add(graphHost);

            blackboardView.Refresh();
            UpdateStatus();
            if (graphAsset != null) graphView.RebuildGraph();
        }

        private void ToggleBlackboardPopup()
        {
            SetBlackboardPopupVisible(!isBlackboardPopupVisible);
        }

        private void SetBlackboardPopupVisible(bool visible)
        {
            isBlackboardPopupVisible = visible;
            if (blackboardView != null)
            {
                blackboardView.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (blackboardToggleButton != null)
            {
                blackboardToggleButton.text = visible ? "Hide Blackboard" : "Blackboard";
            }
        }

        internal void BeginGraphRebuild()
        {
            rebuildingGraph = true;
        }

        internal void EndGraphRebuild()
        {
            rebuildingGraph = false;
            UpdateStatus();
        }

        internal string GetNodeTitle(VATAnimatorNodeData node)
        {
            if (node == null) return "VAT Node";
            if (graphAsset == null) return node.nodeType.ToString();

            switch (node.nodeType)
            {
                case VATAnimatorNodeType.Clip:
                    VATAnimatorClipData clip = graphAsset.FindClipByKey(node.clipKey);
                    return "Clip: " + (clip == null ? "Missing" : clip.clipName);
                case VATAnimatorNodeType.Parameter:
                    VATAnimatorParameterData parameter = graphAsset.FindParameter(node.parameterId);
                    return parameter == null
                        ? "Parameter: Missing"
                        : parameter.type + ": " + parameter.parameterName;
                case VATAnimatorNodeType.Transition:
                    VATAnimatorTransitionData transition = graphAsset.FindTransition(node.transitionId);
                    return transition == null ? "Transition: Missing" : transition.title;
                case VATAnimatorNodeType.BlendTree:
                    VATAnimatorBlendTreeData blendTree = graphAsset.FindBlendTree(node.blendTreeId);
                    return blendTree == null ? "Blend Tree: Missing" : blendTree.title;
                case VATAnimatorNodeType.Default:
                    return "Default";
                default:
                    return node.nodeType.ToString();
            }
        }

        internal void SelectNode(int nodeId)
        {
            selectedNodeId = nodeId;
        }

        internal void RecordGraphUndo(string actionName)
        {
            if (sourceAsset != null)
            {
                Undo.RecordObject(sourceAsset, actionName);
            }
            if (sourceAsset != null && sourceAsset.AnimatorAsset != null)
            {
                Undo.RecordObject(sourceAsset.AnimatorAsset, actionName);
            }
        }

        internal void RefreshNodeView(int nodeId)
        {
            graphView?.RefreshNode(nodeId);
        }

        internal void RebuildGraphView()
        {
            graphView?.RebuildGraph();
            blackboardView?.Refresh();
        }

        internal void ScheduleGraphRebuild()
        {
            if (graphRebuildScheduled) return;
            graphRebuildScheduled = true;
            EditorApplication.delayCall += () =>
            {
                graphRebuildScheduled = false;
                if (this == null) return;
                RebuildGraphView();
            };
        }

        internal void MarkGraphChanged()
        {
            if (sourceAsset != null && sourceAsset.AnimatorAsset != null)
            {
                EditorUtility.SetDirty(sourceAsset.AnimatorAsset);
            }
            UpdateStatus();
        }

        private void EnsureGraphAdapter()
        {
            if (sourceAsset == null || sourceAsset.AnimatorAsset == null)
            {
                return;
            }

            if (graphAsset == null)
            {
                graphAsset = CreateInstance<VATAnimatorGraphAsset>();
                graphAsset.hideFlags = HideFlags.HideAndDontSave;
            }
            graphAsset.Attach(sourceAsset);
            graphAsset.EnsureLists();
        }

        private void RefreshAnimatorAssetField()
        {
            VATAssetAnimatorSO animatorAsset = sourceAsset == null
                ? null
                : sourceAsset.AnimatorAsset;
            if (animatorAssetField != null)
            {
                animatorAssetField.SetValueWithoutNotify(animatorAsset);
            }

            if (createAnimatorAssetButton != null)
            {
                bool needsAnimatorAsset = sourceAsset != null && animatorAsset == null;
                createAnimatorAssetButton.SetEnabled(needsAnimatorAsset);
                createAnimatorAssetButton.style.display = needsAnimatorAsset
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }
        }

        private void DisposeGraphAdapter()
        {
            if (graphAsset != null)
            {
                DestroyImmediate(graphAsset);
                graphAsset = null;
            }
        }

        private void SetSourceAsset(VATAssetDataSO value)
        {
            if (sourceAsset == value && graphAsset != null)
            {
                bool refreshedDefaultNode = CreateDefaultNodeForEmptyAnimator();
                RefreshAnimatorAssetField();
                graphView?.RebuildGraph();
                ShowMissingClipDialogIfNeeded();
                UpdateStatus();
                if (refreshedDefaultNode) FrameGraphOnNextLayout();
                return;
            }

            sourceAsset = value;
            if (sourceAssetField != null) sourceAssetField.SetValueWithoutNotify(sourceAsset);
            RefreshAnimatorAssetField();

            if (sourceAsset == null || sourceAsset.AnimatorAsset == null)
            {
                DisposeGraphAdapter();
                selectedNodeId = -1;
                graphView?.RebuildGraph();
                blackboardView?.Refresh();
                UpdateStatus();
                return;
            }

            EnsureGraphAdapter();
            bool createdDefaultNode = CreateDefaultNodeForEmptyAnimator();
            selectedNodeId = -1;
            RecordGraphUndo("Assign VAT Animator Input");

            ShowMissingClipDialogIfNeeded();

            graphView?.RebuildGraph();
            blackboardView?.Refresh();
            UpdateStatus();
            if (createdDefaultNode) FrameGraphOnNextLayout();
        }

        /// <summary>
        /// A blank VATAssetAnimatorSO starts with its structural Default node.
        /// This belongs to the editor-open flow rather than generic graph list
        /// initialization, so its creation is undoable and immediately visible.
        /// </summary>
        private bool CreateDefaultNodeForEmptyAnimator()
        {
            if (graphAsset == null || graphAsset.nodes == null || graphAsset.nodes.Count != 0)
            {
                return false;
            }

            RecordGraphUndo("Create VAT Animator Default Node");
            VATAnimatorNodeData node = graphAsset.AddDefaultNode();
            if (node == null)
            {
                return false;
            }

            MarkGraphChanged();
            return true;
        }

        private void FrameGraphOnNextLayout()
        {
            if (graphView == null)
            {
                return;
            }

            graphView.schedule.Execute(() => graphView.FrameAllView()).ExecuteLater(0);
        }

        private void ShowMissingClipDialogIfNeeded()
        {
            if (graphAsset == null || sourceAsset == null || sourceAsset.AnimatorAsset == null)
            {
                return;
            }

            List<string> missingClipKeys = graphAsset.GetMissingClipKeys();
            if (missingClipKeys.Count == 0)
            {
                return;
            }

            EditorUtility.DisplayDialog(
                "VAT Animator Missing Clips",
                "The linked VATAssetAnimatorSO references clip(s) that do not exist in the selected VATAssetDataSO:\n\n" +
                string.Join("\n", missingClipKeys) +
                "\n\nThe graph was not changed. Re-bake matching clips or use Sync Clips to remove missing references.",
                "OK");
        }

        private void CreateAnimatorAsset()
        {
            if (sourceAsset == null || sourceAsset.AnimatorAsset != null)
            {
                return;
            }

            string sourcePath = AssetDatabase.GetAssetPath(sourceAsset);
            if (string.IsNullOrEmpty(sourcePath))
            {
                EditorUtility.DisplayDialog(
                    "VAT Animator",
                    "Save the VATAssetDataSO as an asset before creating its VATAssetAnimatorSO.",
                    "OK");
                return;
            }

            string directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            string animatorPath = AssetDatabase.GenerateUniqueAssetPath(
                Path.Combine(directory ?? "Assets", sourceAsset.name + "_Animator.asset").Replace('\\', '/'));
            VATAssetAnimatorSO animatorAsset = CreateInstance<VATAssetAnimatorSO>();
            AssetDatabase.CreateAsset(animatorAsset, animatorPath);

            Undo.RecordObject(sourceAsset, "Create VAT Asset Animator");
            sourceAsset.AnimatorAsset = animatorAsset;
            if (sourceAsset.HasLegacyAnimatorData())
            {
                sourceAsset.MigrateLegacyAnimatorDataTo(animatorAsset);
            }
            else
            {
                animatorAsset.EnsureLists();
            }

            EditorUtility.SetDirty(sourceAsset);
            EditorUtility.SetDirty(animatorAsset);
            AssetDatabase.SaveAssets();

            DisposeGraphAdapter();
            EnsureGraphAdapter();
            bool createdDefaultNode = CreateDefaultNodeForEmptyAnimator();
            MarkGraphChanged();
            RefreshAnimatorAssetField();
            selectedNodeId = -1;
            graphView?.RebuildGraph();
            blackboardView?.Refresh();
            SaveGraph();
            if (createdDefaultNode) FrameGraphOnNextLayout();
        }

        internal void SyncClips()
        {
            if (graphAsset == null)
            {
                EditorUtility.DisplayDialog("VAT Animator", "Assign a VATAssetDataSO as VAT Input first.", "OK");
                return;
            }

            if (sourceAsset == null)
            {
                EditorUtility.DisplayDialog("VAT Animator", "Assign a VATAssetDataSO as VAT Input first.", "OK");
                return;
            }

            EnsureGraphAdapter();
            ShowMissingClipDialogIfNeeded();
            RecordGraphUndo("Sync VAT Animator Clips");
            int clipCount = graphAsset.SyncFromVATAssetData();
            MarkGraphChanged();
            selectedNodeId = -1;
            graphView?.RebuildGraph();
            blackboardView?.Refresh();
            SaveGraph();
            if (statusLabel != null)
            {
                statusLabel.text = "Synced " + clipCount + " VAT clip(s) into the graph.";
            }
        }

        private void SaveGraph()
        {
            if (sourceAsset == null || sourceAsset.AnimatorAsset == null) return;
            EditorUtility.SetDirty(sourceAsset.AnimatorAsset);
            AssetDatabase.SaveAssets();
            UpdateStatus();
        }

        private void ValidateGraph()
        {
            if (graphAsset == null)
            {
                EditorUtility.DisplayDialog("VAT Animator Validation", "No VATAssetDataSO is selected.", "OK");
                return;
            }

            List<string> messages = new List<string>();
            bool valid = graphAsset.ValidateGraph(messages);
            if (valid)
            {
                EditorUtility.DisplayDialog("VAT Animator Validation", "Graph is valid.", "OK");
                return;
            }

            string body = string.Join("\n• ", messages.Take(24));
            if (messages.Count > 24) body += "\n• ... and " + (messages.Count - 24) + " more";
            EditorUtility.DisplayDialog("VAT Animator Validation", "Found " + messages.Count + " issue(s):\n\n• " + body, "OK");
        }

        internal void AddParameterReferenceNode(int parameterId, Vector2 position)
        {
            if (graphAsset == null || sourceAsset == null)
            {
                EditorUtility.DisplayDialog("VAT Animator", "Create a parameter in the Blackboard and assign a VATAssetDataSO first.", "OK");
                return;
            }
            if (graphAsset.FindParameter(parameterId) == null) return;

            RecordGraphUndo("Add VAT Animator Parameter Reference");
            VATAnimatorNodeData node = graphAsset.AddParameterReferenceNode(parameterId, position);
            if (node == null) return;
            MarkGraphChanged();
            graphView.RebuildGraph();
            graphView.SelectNode(node.id);
        }

        internal void CreateParameterFromBlackboard(VATAnimatorParameterType type)
        {
            if (graphAsset == null || sourceAsset == null) return;
            RecordGraphUndo("Create VAT Animator " + type + " Parameter");
            graphAsset.CreateParameter(type);
            MarkGraphChanged();
            blackboardView?.Refresh();
        }

        internal void RenameParameterFromBlackboard(int parameterId, string parameterName)
        {
            if (graphAsset == null || sourceAsset == null) return;
            RecordGraphUndo("Rename VAT Animator Parameter");
            if (!graphAsset.RenameParameter(parameterId, parameterName)) return;
            MarkGraphChanged();
            graphView?.RebuildGraph();
            blackboardView?.Refresh();
        }

        internal void ChangeParameterTypeFromBlackboard(int parameterId, VATAnimatorParameterType type)
        {
            if (graphAsset == null || sourceAsset == null) return;
            RecordGraphUndo("Change VAT Animator Parameter Type");
            if (!graphAsset.ChangeParameterType(parameterId, type)) return;
            MarkGraphChanged();
            graphView?.RebuildGraph();
            blackboardView?.Refresh();
        }

        internal void ChangeParameterDefault(
            int parameterId,
            bool defaultBool,
            float defaultFloat,
            Vector2 defaultVector2)
        {
            if (graphAsset == null || sourceAsset == null) return;
            VATAnimatorParameterData parameter = graphAsset.FindParameter(parameterId);
            if (parameter == null) return;
            RecordGraphUndo("Edit VAT Animator Parameter Default");
            parameter.defaultBool = defaultBool;
            parameter.defaultFloat = defaultFloat;
            parameter.defaultVector2 = defaultVector2;
            MarkGraphChanged();
        }

        internal void RemoveParameterFromBlackboard(int parameterId)
        {
            if (graphAsset == null || sourceAsset == null) return;
            RecordGraphUndo("Remove VAT Animator Parameter");
            if (!graphAsset.RemoveParameter(parameterId)) return;
            MarkGraphChanged();
            graphView?.RebuildGraph();
            blackboardView?.Refresh();
        }

        internal void AddClipNode(string clipKey, Vector2 position)
        {
            if (graphAsset == null)
            {
                EditorUtility.DisplayDialog("VAT Animator", "Assign a VATAssetDataSO as VAT Input first.", "OK");
                return;
            }

            VATAnimatorClipData clip = graphAsset.FindClipByKey(clipKey);
            if (clip == null) return;

            RecordGraphUndo("Add VAT Animator Clip Node Reference");
            VATAnimatorNodeData node = graphAsset.AddClipNode(clipKey, position);
            if (node == null) return;

            MarkGraphChanged();
            graphView.RebuildGraph();
            graphView.SelectNode(node.id);
        }

        internal void AddTransitionNode(Vector2? position = null)
        {
            if (graphAsset == null)
            {
                EditorUtility.DisplayDialog("VAT Animator", "Assign a VATAssetDataSO as VAT Input first.", "OK");
                return;
            }
            RecordGraphUndo("Add VAT Animator Transition");
            VATAnimatorNodeData node = graphAsset.AddTransitionNode(position);
            MarkGraphChanged();
            graphView.RebuildGraph();
            graphView.SelectNode(node.id);
        }

        internal void AddBlendTreeNode(Vector2? position = null)
        {
            if (graphAsset == null)
            {
                EditorUtility.DisplayDialog("VAT Animator", "Assign a VATAssetDataSO as VAT Input first.", "OK");
                return;
            }
            RecordGraphUndo("Add VAT Animator Blend Tree");
            VATAnimatorNodeData node = graphAsset.AddBlendTreeNode(position);
            MarkGraphChanged();
            graphView.RebuildGraph();
            graphView.SelectNode(node.id);
        }

        internal void AddDefaultNode()
        {
            if (graphAsset == null)
            {
                EditorUtility.DisplayDialog("VAT Animator", "Assign a VATAssetDataSO as VAT Input first.", "OK");
                return;
            }
            RecordGraphUndo("Add VAT Animator Default Node");
            VATAnimatorNodeData node = graphAsset.AddDefaultNode();
            MarkGraphChanged();
            graphView.RebuildGraph();
            graphView.SelectNode(node.id);
        }

        internal void RemoveNodeData(int nodeId)
        {
            if (graphAsset == null) return;
            RecordGraphUndo("Remove VAT Animator Node");
            graphAsset.RemoveNode(nodeId);
            MarkGraphChanged();
            selectedNodeId = -1;
            graphView?.RebuildGraph();
        }

        internal bool AddEdgeData(VATAnimatorEdgeData edge)
        {
            if (graphAsset == null || edge == null) return false;
            RecordGraphUndo("Add VAT Animator Edge");
            if (!graphAsset.AddEdge(edge)) return false;
            graphAsset.ApplyRuntimeEdgeMapping(edge);
            MarkGraphChanged();
            return true;
        }

        internal void RemoveEdgeData(VATAnimatorEdgeData edge)
        {
            LastEdgeRemovalChangedPorts = false;
            if (graphAsset == null || edge == null) return;
            for (int i = graphAsset.edges.Count - 1; i >= 0; i--)
            {
                VATAnimatorEdgeData current = graphAsset.edges[i];
                if (current != null && current.outputNodeId == edge.outputNodeId &&
                    current.outputPortName == edge.outputPortName &&
                    current.inputNodeId == edge.inputNodeId &&
                    current.inputPortName == edge.inputPortName)
                {
                    RecordGraphUndo("Remove VAT Animator Edge");
                    bool blendChanged = graphAsset.RemoveBlendChildForEdge(edge);
                    bool parameterChanged = graphAsset.ClearParameterEdge(edge);
                    bool defaultChanged = graphAsset.ClearDefaultEdge(edge);
                    bool runtimeChanged = graphAsset.ClearRuntimeEdgeMapping(edge);
                    LastEdgeRemovalChangedPorts = blendChanged || parameterChanged ||
                        defaultChanged || runtimeChanged;
                    graphAsset.edges.RemoveAt(i);
                    MarkGraphChanged();
                    if (!LastEdgeRemovalChangedPorts)
                    {
                        graphView?.RefreshNode(edge.inputNodeId);
                    }
                }
            }
        }

        internal bool TryCreateTransitionConditionEdge(VATAnimatorEdgeData edge)
        {
            if (graphAsset == null || edge == null) return false;
            RecordGraphUndo("Create VAT Animator Transition Condition");
            return graphAsset.TryCreateTransitionConditionEdge(edge);
        }

        internal bool TryBindBlendTreeCaseEdge(VATAnimatorEdgeData edge)
        {
            if (graphAsset == null || edge == null) return false;
            RecordGraphUndo("Bind VAT Animator Blend Tree Case");
            return graphAsset.TryBindBlendTreeCaseEdge(edge);
        }

        internal bool HandleDefaultEdge(VATAnimatorEdgeData edge)
        {
            if (graphAsset == null || edge == null) return false;
            RecordGraphUndo("Set VAT Animator Default Clip");
            bool handled = graphAsset.HandleDefaultEdge(edge);
            if (handled)
            {
                MarkGraphChanged();
                graphView?.RefreshNode(edge.outputNodeId);
            }
            return handled;
        }

        internal void RemoveBlendTreeCase(int blendTreeId, int childId)
        {
            if (graphAsset == null) return;
            VATAnimatorBlendTreeData tree = graphAsset.FindBlendTree(blendTreeId);
            if (tree == null || tree.children == null) return;
            if (graphAsset.FindBlendChild(blendTreeId, childId) == null) return;

            RecordGraphUndo("Remove VAT Animator Blend Tree Case");
            tree.children.RemoveAll(child => child == null || child.id == childId);
            graphAsset.edges.RemoveAll(edge => edge != null &&
                edge.outputNodeId == graphAsset.nodes.FirstOrDefault(node =>
                    node != null && node.nodeType == VATAnimatorNodeType.BlendTree &&
                    node.blendTreeId == blendTreeId)?.id &&
                edge.outputPortName == VATAnimatorGraphAsset.GetBlendCasePortName(childId));
            MarkGraphChanged();
            RebuildGraphView();
        }

        internal void HandleParameterEdge(int parameterId, int targetNodeId, string targetPortName)
        {
            if (graphAsset == null || targetNodeId < 0) return;
            RecordGraphUndo("Assign VAT Animator Parameter");
            graphAsset.HandleParameterEdge(parameterId, targetNodeId, targetPortName);
            MarkGraphChanged();
            graphView?.RefreshNode(targetNodeId);
        }

        private void DrawInspector()
        {
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField("VAT Animator Inspector", EditorStyles.boldLabel);
            EditorGUILayout.Space(3f);

            if (graphAsset == null)
            {
                EditorGUILayout.HelpBox("Select a VATAssetDataSO, then create its linked VATAssetAnimatorSO to edit animator data.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField("Source", graphAsset.sourceVATAsset == null ? "<none>" : graphAsset.sourceVATAsset.name);
            EditorGUILayout.LabelField("Clips / Nodes / Edges",
                graphAsset.clips.Count + " / " + graphAsset.nodes.Count + " / " + graphAsset.edges.Count);
            EditorGUILayout.Space(8f);

            VATAnimatorNodeData node = graphAsset.FindNode(selectedNodeId);
            if (node == null)
            {
                EditorGUILayout.HelpBox("Select a node to edit its mapping. Clip nodes are generated from VATAssetDataSO.Clips.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField(GetNodeTitle(node), EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Node ID", node.id.ToString());
            EditorGUILayout.Space(4f);

            switch (node.nodeType)
            {
                case VATAnimatorNodeType.Clip:
                    DrawClipInspector(node);
                    break;
                case VATAnimatorNodeType.Parameter:
                    DrawParameterInspector(node);
                    break;
                case VATAnimatorNodeType.Transition:
                    DrawTransitionInspector(node);
                    break;
                case VATAnimatorNodeType.BlendTree:
                    DrawBlendTreeInspector(node);
                    break;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawClipInspector(VATAnimatorNodeData node)
        {
            VATAnimatorClipData clip = graphAsset.FindClipByKey(node.clipKey);
            if (clip == null)
            {
                EditorGUILayout.HelpBox("The source clip no longer exists. Use Sync Clips.", MessageType.Error);
                return;
            }

            EditorGUILayout.LabelField("Clip Name", clip.clipName);
            EditorGUILayout.LabelField("State Hash", clip.stateHash.ToString());
            EditorGUILayout.LabelField("Frame Range", clip.startFrame + " - " + clip.endFrame);
            EditorGUILayout.LabelField("Total Frames", clip.TotalFrames.ToString());
            EditorGUILayout.LabelField("Frame Rate", clip.frameRate.ToString("0.##"));
            EditorGUILayout.LabelField("Loop", clip.isLooping ? "Yes" : "No");
            EditorGUILayout.LabelField("Mapping Key", clip.clipKey);
        }

        private void DrawParameterInspector(VATAnimatorNodeData node)
        {
            VATAnimatorParameterData parameter = graphAsset.FindParameter(node.parameterId);
            if (parameter == null)
            {
                EditorGUILayout.HelpBox("The parameter data is missing.", MessageType.Error);
                return;
            }

            string nextName = EditorGUILayout.TextField("Name", parameter.parameterName);
            if (nextName != parameter.parameterName)
            {
                RecordGraphUndo("Rename VAT Animator Parameter");
                parameter.parameterName = nextName;
                MarkGraphChanged();
                graphView.RefreshNode(node.id);
            }

            VATAnimatorParameterType nextType = (VATAnimatorParameterType)EditorGUILayout.EnumPopup("Type", parameter.type);
            if (nextType != parameter.type)
            {
                RecordGraphUndo("Change VAT Animator Parameter Type");
                parameter.type = nextType;
                MarkGraphChanged();
                graphView.RefreshNode(node.id);
            }

            switch (parameter.type)
            {
                case VATAnimatorParameterType.Bool:
                    bool nextBool = EditorGUILayout.Toggle("Default", parameter.defaultBool);
                    if (nextBool != parameter.defaultBool)
                    {
                        RecordGraphUndo("Edit VAT Animator Bool Default");
                        parameter.defaultBool = nextBool;
                        MarkGraphChanged();
                        graphView.RefreshNode(node.id);
                    }
                    break;
                case VATAnimatorParameterType.Float:
                    float nextFloat = EditorGUILayout.FloatField("Default", parameter.defaultFloat);
                    if (!Mathf.Approximately(nextFloat, parameter.defaultFloat))
                    {
                        RecordGraphUndo("Edit VAT Animator Float Default");
                        parameter.defaultFloat = nextFloat;
                        MarkGraphChanged();
                        graphView.RefreshNode(node.id);
                    }
                    break;
                case VATAnimatorParameterType.Vector2:
                    Vector2 nextVector = EditorGUILayout.Vector2Field("Default", parameter.defaultVector2);
                    if (nextVector != parameter.defaultVector2)
                    {
                        RecordGraphUndo("Edit VAT Animator Vector2 Default");
                        parameter.defaultVector2 = nextVector;
                        MarkGraphChanged();
                        graphView.RefreshNode(node.id);
                    }
                    break;
            }
        }

        private void DrawTransitionInspector(VATAnimatorNodeData node)
        {
            VATAnimatorTransitionData transition = graphAsset.FindTransition(node.transitionId);
            if (transition == null)
            {
                EditorGUILayout.HelpBox("The transition data is missing.", MessageType.Error);
                return;
            }

            string nextTitle = EditorGUILayout.TextField("Title", transition.title);
            if (nextTitle != transition.title)
            {
                RecordGraphUndo("Rename VAT Animator Transition");
                transition.title = nextTitle;
                MarkGraphChanged();
                graphView.RefreshNode(node.id);
            }

            bool auto = EditorGUILayout.Toggle("Auto Transition", transition.autoTransition);
            if (auto != transition.autoTransition)
            {
                RecordGraphUndo("Edit VAT Animator Transition");
                transition.autoTransition = auto;
                MarkGraphChanged();
                graphView.RefreshNode(node.id);
            }

            bool hasExit = EditorGUILayout.Toggle("Has Exit Time", transition.hasExitTime);
            if (hasExit != transition.hasExitTime)
            {
                RecordGraphUndo("Edit VAT Animator Exit Time");
                transition.hasExitTime = hasExit;
                MarkGraphChanged();
            }

            float duration = EditorGUILayout.FloatField("Duration", transition.duration);
            if (!Mathf.Approximately(duration, transition.duration))
            {
                RecordGraphUndo("Edit VAT Animator Transition Duration");
                transition.duration = Mathf.Max(0f, duration);
                MarkGraphChanged();
                graphView.RefreshNode(node.id);
            }

            if (transition.hasExitTime)
            {
                float exitTime = EditorGUILayout.FloatField("Exit Time", transition.exitTime);
                if (!Mathf.Approximately(exitTime, transition.exitTime))
                {
                    RecordGraphUndo("Edit VAT Animator Exit Time");
                    transition.exitTime = Mathf.Max(0f, exitTime);
                    MarkGraphChanged();
                }
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Conditions", EditorStyles.boldLabel);
            if (transition.conditions == null) transition.conditions = new List<VATAnimatorConditionData>();
            for (int i = transition.conditions.Count - 1; i >= 0; i--)
            {
                VATAnimatorConditionData condition = transition.conditions[i];
                if (condition == null)
                {
                    transition.conditions.RemoveAt(i);
                    continue;
                }
                DrawCondition(transition, condition, i);
            }

            EditorGUILayout.HelpBox("Connect the [New] input to a Parameter node to create a condition.", MessageType.Info);
        }

        private void DrawCondition(VATAnimatorTransitionData transition, VATAnimatorConditionData condition, int conditionIndex)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Condition:" + condition.id, EditorStyles.boldLabel);
            if (GUILayout.Button("X", GUILayout.Width(20f)))
            {
                RecordGraphUndo("Remove VAT Animator Condition");
                graphAsset.RemoveTransitionCondition(transition.id, condition.id);
                MarkGraphChanged();
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            VATAnimatorParameterData parameter = graphAsset.FindParameter(condition.parameterId);
            if (parameter != null)
            {
                EditorGUILayout.LabelField("Parameter", parameter.parameterName + " (" + parameter.type + ")");
                if (parameter.type != VATAnimatorParameterType.Trigger)
                {
                    VATAnimatorConditionMode[] modes = GetConditionModes(parameter.type);
                    int modeIndex = Array.IndexOf(modes, condition.mode);
                    if (modeIndex < 0) modeIndex = 0;
                    string[] modeNames = modes.Select(mode => mode.ToString()).ToArray();
                    int nextModeIndex = EditorGUILayout.Popup("Mode", modeIndex, modeNames);
                    if (nextModeIndex != modeIndex && nextModeIndex >= 0 && nextModeIndex < modes.Length)
                    {
                        RecordGraphUndo("Edit VAT Animator Condition Mode");
                        condition.mode = modes[nextModeIndex];
                        MarkGraphChanged();
                    }
                }

                switch (parameter.type)
                {
                    case VATAnimatorParameterType.Bool:
                        bool nextBool = EditorGUILayout.Toggle("Value", condition.boolThreshold);
                        if (nextBool != condition.boolThreshold)
                        {
                            RecordGraphUndo("Edit VAT Animator Bool Condition");
                            condition.boolThreshold = nextBool;
                            MarkGraphChanged();
                        }
                        break;
                    case VATAnimatorParameterType.Float:
                        float nextFloat = EditorGUILayout.FloatField("Threshold", condition.threshold);
                        if (!Mathf.Approximately(nextFloat, condition.threshold))
                        {
                            RecordGraphUndo("Edit VAT Animator Float Condition");
                            condition.threshold = nextFloat;
                            MarkGraphChanged();
                        }
                        break;
                    case VATAnimatorParameterType.Vector2:
                        Vector2 nextVector = EditorGUILayout.Vector2Field("Threshold", condition.vectorThreshold);
                        if (nextVector != condition.vectorThreshold)
                        {
                            RecordGraphUndo("Edit VAT Animator Vector2 Condition");
                            condition.vectorThreshold = nextVector;
                            MarkGraphChanged();
                        }
                        break;
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Missing parameter", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawBlendTreeInspector(VATAnimatorNodeData node)
        {
            VATAnimatorBlendTreeData tree = graphAsset.FindBlendTree(node.blendTreeId);
            if (tree == null)
            {
                EditorGUILayout.HelpBox("The blend tree data is missing.", MessageType.Error);
                return;
            }

            string nextTitle = EditorGUILayout.TextField("Title", tree.title);
            if (nextTitle != tree.title)
            {
                RecordGraphUndo("Rename VAT Animator Blend Tree");
                tree.title = nextTitle;
                MarkGraphChanged();
                graphView.RefreshNode(node.id);
            }

            VATAnimatorParameterData parameter = graphAsset.FindParameter(tree.parameterId);
            if (parameter == null)
            {
                EditorGUILayout.HelpBox("Connect a Float or Vector2 Parameter node to the Parameter input.", MessageType.Warning);
            }
            else if (parameter.type != VATAnimatorParameterType.Float && parameter.type != VATAnimatorParameterType.Vector2)
            {
                EditorGUILayout.HelpBox("Only Float and Vector2 parameters are supported by VAT BlendTree.", MessageType.Error);
            }
            else
            {
                EditorGUILayout.LabelField("Parameter", parameter.parameterName + " (" + parameter.type + ")");
                EditorGUILayout.LabelField("Mode", tree.mode.ToString());
            }

            bool clamp = EditorGUILayout.Toggle("Clamp Input", tree.clampInput);
            if (clamp != tree.clampInput)
            {
                RecordGraphUndo("Edit VAT Animator Blend Tree");
                tree.clampInput = clamp;
                MarkGraphChanged();
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Output Cases", EditorStyles.boldLabel);
            if (tree.children == null) tree.children = new List<VATAnimatorBlendChildData>();
            for (int i = tree.children.Count - 1; i >= 0; i--)
            {
                VATAnimatorBlendChildData child = tree.children[i];
                if (child == null)
                {
                    tree.children.RemoveAt(i);
                    continue;
                }
                DrawBlendChild(tree, child, i);
            }
            EditorGUILayout.HelpBox("Connect the [New] output to a Clip node to create a case.", MessageType.Info);
        }

        private void DrawBlendChild(VATAnimatorBlendTreeData tree, VATAnimatorBlendChildData child, int childIndex)
        {
            EditorGUILayout.BeginVertical("box");
            VATAnimatorClipData clip = graphAsset.FindClipByKey(child.clipKey);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Case:" + child.id + "  →  " +
                (clip == null ? "<Missing Clip>" : clip.clipName), EditorStyles.boldLabel);
            if (GUILayout.Button("X", GUILayout.Width(20f)))
            {
                RemoveBlendTreeCase(tree.id, child.id);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            if (tree.mode == VATAnimatorBlendTreeMode.OneDimensional)
            {
                float threshold = EditorGUILayout.FloatField("Threshold", child.threshold.x);
                if (!Mathf.Approximately(threshold, child.threshold.x))
                {
                    RecordGraphUndo("Edit VAT Animator Blend Threshold");
                    child.threshold.x = threshold;
                    child.threshold.y = 0f;
                    MarkGraphChanged();
                }
            }
            else
            {
                Vector2 threshold = EditorGUILayout.Vector2Field("Threshold", child.threshold);
                if (threshold != child.threshold)
                {
                    RecordGraphUndo("Edit VAT Animator Blend Threshold");
                    child.threshold = threshold;
                    MarkGraphChanged();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private int[] GetParameterOptions(out string[] names)
        {
            List<int> ids = new List<int> { -1 };
            List<string> labels = new List<string> { "<Missing / None>" };
            for (int i = 0; i < graphAsset.parameters.Count; i++)
            {
                VATAnimatorParameterData parameter = graphAsset.parameters[i];
                if (parameter == null) continue;
                ids.Add(parameter.id);
                labels.Add(parameter.parameterName + " (" + parameter.type + ")");
            }
            names = labels.ToArray();
            return ids.ToArray();
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

        private void UpdateStatus()
        {
            if (statusLabel == null) return;
            if (graphAsset == null || graphAsset.sourceVATAsset == null)
            {
                statusLabel.text = sourceAsset == null
                    ? "No VATAssetDataSO selected. Select a VAT asset to edit its animator data."
                    : "No VATAssetAnimatorSO is linked. Click Create New to create one beside the VATAssetDataSO.";
                return;
            }

            graphAsset.EnsureLists();
            string sourceName = graphAsset.sourceVATAsset == null ? "<none>" : graphAsset.sourceVATAsset.name;
            int parameterCount = graphAsset.parameters == null ? 0 : graphAsset.parameters.Count;
            int transitionCount = graphAsset.transitions == null ? 0 : graphAsset.transitions.Count;
            int blendTreeCount = graphAsset.blendTrees == null ? 0 : graphAsset.blendTrees.Count;
            int nodeCount = graphAsset.nodes == null ? 0 : graphAsset.nodes.Count;
            int edgeCount = graphAsset.edges == null ? 0 : graphAsset.edges.Count;
            statusLabel.text = "Input: " + sourceName +
                "  |  Clips: " + graphAsset.clips.Count +
                "  |  Parameters: " + parameterCount +
                "  |  Transitions: " + transitionCount +
                "  |  Blend Trees: " + blendTreeCount +
                "  |  Nodes: " + nodeCount +
                "  |  Edges: " + edgeCount;
        }
    }
}
