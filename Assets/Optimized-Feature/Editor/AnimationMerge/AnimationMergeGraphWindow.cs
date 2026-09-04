using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace OptimizedFeature.Scripts.Editor
{
    public sealed class AnimationMergeBakeSelection
    {
        public Animator SourceAnimator;
        public readonly List<AnimationClip> Candidates = new List<AnimationClip>();
        public readonly List<AnimationClip> Selected = new List<AnimationClip>();
    }

    public class AnimationMergeGraphWindow : EditorWindow
    {
        private const float BoneColumnWidth = 215f;
        private const float BoneIndentPerDepth = 8f;
        private const float ClipToggleColumnWidth = 58f;

        [SerializeField] private AnimationMergeGraphAsset graphAsset;
        [SerializeField] private GameObject sourceGameObject;

        private AnimationMergeGraphView graphView;
        private IMGUIContainer inspectorContainer;
        private Label statusLabel;
        private AnimationMergeNodeView selectedNode;
        private bool sessionDirty;
        private bool rebuildingGraphView;
        private string mergeFilter = string.Empty;
        private bool includeChildFilter;
        private Vector2 boneMergeScrollPosition;
        private AnimationMergePreviewNodeView activePreviewNode;
        private GameObject previewTarget;
        private AnimationClip previewClip;
        private double previewStartedAt;
        private float previewTime;
        private int previewLoopCount;
        private GUIStyle mergeSectionTitleStyle;
        private bool vatBakeMode;
        private Action<AnimationMergeBakeSelection> vatBakeSelectionChanged;
        private bool embeddedHost;
        private bool previewUpdateRegistered;

        internal bool IsRebuildingGraph
        {
            get { return rebuildingGraphView; }
        }

        [MenuItem("Window/Animation Merge Tool")]
        public static void OpenWindow()
        {
            AnimationMergeGraphWindow window = GetWindow<AnimationMergeGraphWindow>();
            window.titleContent = new GUIContent("Animation Merge");
            window.minSize = new Vector2(920f, 540f);
            if (Selection.activeGameObject != null &&
                Selection.activeGameObject.GetComponentInChildren<Animator>(true) != null)
            {
                window.SetSourceGameObject(Selection.activeGameObject);
            }
            window.Show();
        }

        public sealed class EmbeddedGraphHandle : IDisposable
        {
            private readonly AnimationMergeGraphWindow host;
            private readonly Action exitRequested;
            private bool disposed;

            internal EmbeddedGraphHandle(
                AnimationMergeGraphWindow host,
                VisualElement root,
                Action exitRequested)
            {
                this.host = host;
                this.exitRequested = exitRequested;
                Root = root;
            }

            public VisualElement Root { get; private set; }

            public bool IsDisposed
            {
                get { return disposed || host == null; }
            }

            public void LoadAnimator(Animator animator)
            {
                if (host == null || animator == null)
                {
                    return;
                }

                host.SetSourceGameObject(animator.gameObject);
                host.ExportAnimations();
                host.Notify("Animator graph loaded. Connect nodes and configure merge outputs in this embedded graph.");
            }

            public void SetClipBakeSelection(AnimationClip clip, bool bake)
            {
                if (!IsDisposed)
                {
                    host.SetAnimationNodeBakeForClip(clip, bake);
                }
            }

            public void Show(VisualElement parent)
            {
                if (IsDisposed || parent == null || Root == null)
                {
                    return;
                }

                if (Root.parent == null)
                {
                    parent.Add(Root);
                }

                host.RegisterPreviewUpdate();
            }

            public void Exit()
            {
                if (IsDisposed)
                {
                    return;
                }

                Hide();
                if (exitRequested != null)
                {
                    exitRequested();
                }
            }

            private void Hide()
            {
                host.StopPreview();
                host.UnregisterPreviewUpdate();
                if (Root != null && Root.parent != null)
                {
                    Root.RemoveFromHierarchy();
                }
            }

            public void Dispose()
            {
                if (IsDisposed)
                {
                    return;
                }

                Hide();

                if (host.graphAsset != null && !AssetDatabase.Contains(host.graphAsset))
                {
                    DestroyImmediate(host.graphAsset);
                }

                DestroyImmediate(host);
                Root = null;
                disposed = true;
            }
        }

        public static EmbeddedGraphHandle CreateEmbeddedGraph(
            Animator animator,
            Action<AnimationMergeBakeSelection> selectionChanged,
            Action exitRequested)
        {
            AnimationMergeGraphWindow host = CreateInstance<AnimationMergeGraphWindow>();
            host.hideFlags = HideFlags.HideAndDontSave;
            host.embeddedHost = true;
            host.vatBakeMode = true;
            host.vatBakeSelectionChanged = selectionChanged;
            host.EnsureSession();
            host.SetSourceGameObject(animator == null ? null : animator.gameObject);

            VisualElement root = new VisualElement();
            root.style.flexGrow = 1f;
            EmbeddedGraphHandle handle = null;
            host.BuildToolbar(root, () =>
            {
                if (handle != null)
                {
                    handle.Exit();
                }
            });
            host.BuildGraphContent(root);
            host.RebuildGraphView();
            host.RegisterPreviewUpdate();
            handle = new EmbeddedGraphHandle(host, root, exitRequested);
            return handle;
        }

        /// <summary>
        /// Opens the merge tool with the VAT Bake Tool's detected Animator as its input.
        /// Generated merge outputs are sent back as soon as a merge is executed or saved.
        /// </summary>
        public static AnimationMergeGraphWindow OpenForVATBake(
            Animator animator,
            Action<AnimationMergeBakeSelection> selectionChanged)
        {
            AnimationMergeGraphWindow window = GetWindow<AnimationMergeGraphWindow>();
            window.titleContent = new GUIContent("Animation Merge → VAT Bake");
            window.minSize = new Vector2(920f, 540f);
            window.vatBakeMode = true;
            window.vatBakeSelectionChanged = selectionChanged;
            window.SetSourceGameObject(animator == null ? null : animator.gameObject);
            window.Show();

            // CreateGUI may not have run when this method is called from another
            // EditorWindow. Export after the merge window has finished creating its UI.
            EditorApplication.delayCall += () =>
            {
                if (window == null || window.graphAsset == null)
                {
                    return;
                }

                window.ExportAnimations();
                window.Notify("Animator clips are ready for preprocessing. Merge an output to send it to VAT Bake.");
            };
            return window;
        }

        [MenuItem("CONTEXT/Animator/Open Animation Merge Tool")]
        private static void OpenFromAnimator(MenuCommand command)
        {
            Animator animator = command.context as Animator;
            AnimationMergeGraphWindow window = GetWindow<AnimationMergeGraphWindow>();
            window.titleContent = new GUIContent("Animation Merge");
            window.minSize = new Vector2(920f, 540f);
            window.SetSourceGameObject(animator == null ? null : animator.gameObject);
            window.Show();
        }

        private void OnEnable()
        {
            EnsureSession();
            RegisterPreviewUpdate();
        }

        private void OnDisable()
        {
            UnregisterPreviewUpdate();
            StopPreview();

            if (!embeddedHost && sessionDirty && !EditorApplication.isCompiling)
            {
                bool save = EditorUtility.DisplayDialog(
                    "Animation Merge has unsaved changes",
                    "Save the current graph session before closing?",
                    "Save",
                    "Discard");
                if (save)
                {
                    SaveSession();
                }
            }

            if (graphAsset != null && !AssetDatabase.Contains(graphAsset))
            {
                DestroyImmediate(graphAsset);
            }
        }

        private void RegisterPreviewUpdate()
        {
            if (previewUpdateRegistered)
            {
                return;
            }

            EditorApplication.update += UpdatePreview;
            previewUpdateRegistered = true;
        }

        private void UnregisterPreviewUpdate()
        {
            if (!previewUpdateRegistered)
            {
                return;
            }

            EditorApplication.update -= UpdatePreview;
            previewUpdateRegistered = false;
        }

        private void CreateGUI()
        {
            EnsureSession();
            BuildToolbar(rootVisualElement, null);
            BuildGraphContent(rootVisualElement);
            RebuildGraphView();
        }

        private void BuildGraphContent(VisualElement parent)
        {
            TwoPaneSplitView splitView = new TwoPaneSplitView(
                1,
                350f,
                TwoPaneSplitViewOrientation.Horizontal);
            graphView = new AnimationMergeGraphView(this);
            splitView.Add(graphView);

            inspectorContainer = new IMGUIContainer(DrawInspector);
            inspectorContainer.style.paddingLeft = 8f;
            inspectorContainer.style.paddingRight = 8f;
            inspectorContainer.style.paddingTop = 8f;
            inspectorContainer.style.paddingBottom = 8f;
            splitView.Add(inspectorContainer);
            parent.Add(splitView);
        }

        private void BuildToolbar(VisualElement parent, Action exitAction)
        {
            Toolbar toolbar = new Toolbar();
            ObjectField sourceField = new ObjectField("Animator GameObject")
            {
                objectType = typeof(GameObject),
                allowSceneObjects = true,
                value = sourceGameObject
            };
            sourceField.style.minWidth = 250f;
            sourceField.RegisterValueChangedCallback(change => SetSourceGameObject(change.newValue as GameObject));
            toolbar.Add(sourceField);

            ToolbarButton exportButton = new ToolbarButton(ExportAnimations)
            {
                text = "Export Animator"
            };
            toolbar.Add(exportButton);
            toolbar.Add(new ToolbarButton(SaveSession) { text = "Save Session SO" });
            toolbar.Add(new ToolbarButton(LoadSession) { text = "Load Session SO" });
            if (exitAction != null)
            {
                toolbar.Add(new ToolbarButton(exitAction) { text = "Exit Graph" });
            }

            statusLabel = new Label("Select a GameObject containing an Animator.");
            statusLabel.style.flexGrow = 1f;
            statusLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            toolbar.Add(statusLabel);
            parent.Add(toolbar);
        }

        private void EnsureSession()
        {
            if (graphAsset != null)
            {
                if (sourceGameObject == null)
                {
                    sourceGameObject = graphAsset.sourceGameObject;
                }
                return;
            }

            graphAsset = CreateInstance<AnimationMergeGraphAsset>();
            graphAsset.hideFlags = HideFlags.HideAndDontSave;
            sourceGameObject = null;
        }

        public void SetSourceGameObject(GameObject value)
        {
            sourceGameObject = value;
            EnsureSession();
            graphAsset.sourceGameObject = value;
            graphAsset.sourceAnimator = value == null
                ? null
                : value.GetComponentInChildren<Animator>(true);
            sessionDirty = true;
            SetStatus(graphAsset.sourceAnimator == null
                ? "The selected GameObject has no Animator."
                : "Animator ready: " + graphAsset.sourceAnimator.name);
            RepaintInspector();
        }

        internal void SelectNode(AnimationMergeNodeView node)
        {
            selectedNode = node;
            mergeFilter = string.Empty;
            includeChildFilter = false;
            RepaintInspector();
        }

        public void MarkGraphChanged()
        {
            sessionDirty = true;
            RepaintInspector();
        }

        internal void SetAnimationNodeBake(AnimationMergeNodeData node, bool bake)
        {
            if (node == null)
            {
                return;
            }

            SetAnimationNodeBakeForClip(node.clip, bake);
        }

        private void SetAnimationNodeBakeForClip(AnimationClip clip, bool bake)
        {
            if (graphAsset == null || clip == null)
            {
                return;
            }

            for (int i = 0; i < graphAsset.nodes.Count; i++)
            {
                AnimationMergeNodeData node = graphAsset.nodes[i];
                if (node != null && node.nodeType == AnimationMergeNodeType.Animation && node.clip == clip)
                {
                    node.bake = bake;
                }
            }

            if (graphView != null)
            {
                foreach (AnimationMergeAnimationNodeView nodeView in graphView.GetNodes().OfType<AnimationMergeAnimationNodeView>())
                {
                    if (nodeView.data.clip == clip)
                    {
                        nodeView.SetBakeValueWithoutNotify(bake);
                    }
                }
            }

            MarkGraphChanged();
            PublishVATBakeSelection();
        }

        public void RemoveNodeData(int nodeId)
        {
            if (graphAsset == null || rebuildingGraphView)
            {
                return;
            }

            graphAsset.nodes.RemoveAll(node => node.id == nodeId);
            if (selectedNode != null && selectedNode.data.id == nodeId)
            {
                selectedNode = null;
            }
            MarkGraphChanged();
            PublishVATBakeSelection();
        }

        public void Notify(string message)
        {
            SetStatus(message);
            RepaintInspector();
        }

        private void SetStatus(string message)
        {
            if (statusLabel != null)
            {
                statusLabel.text = message;
            }
        }

        private void ExportAnimations()
        {
            EnsureSession();
            if (graphAsset.sourceAnimator == null)
            {
                SetSourceGameObject(sourceGameObject);
            }

            if (graphAsset.sourceAnimator == null || graphAsset.sourceAnimator.runtimeAnimatorController == null)
            {
                Notify("Assign a GameObject with an Animator Controller first.");
                return;
            }

            StopPreview();
            graphAsset.nodes.Clear();
            graphAsset.edges.Clear();
            graphAsset.layers.Clear();
            graphAsset.nextNodeId = 1;

            List<AnimationMergeExportRecord> records = AnimationMergeClipUtility.ExtractAnimatorAnimations(
                graphAsset.sourceAnimator,
                graphAsset.layers);
            for (int i = 0; i < records.Count; i++)
            {
                AnimationMergeExportRecord record = records[i];
                AnimationMergeNodeData data = new AnimationMergeNodeData
                {
                    id = graphAsset.AllocateNodeId(),
                    nodeType = AnimationMergeNodeType.Animation,
                    title = record.isBlendTree
                        ? "BlendTree • " + record.statePath
                        : "Animation • " + (record.clip == null ? "Missing" : record.clip.name),
                    position = GetExportPosition(i),
                    layerIndex = record.layerIndex,
                    layerName = record.layerName,
                    accentColor = GetNodeColor(i),
                    colorIndex = i,
                    clip = record.clip,
                    motion = record.motion,
                    isBlendTree = record.isBlendTree,
                    statePath = record.statePath,
                    motionPath = record.motionPath,
                    blendTreeClips = record.blendTreeClips
                };
                graphAsset.nodes.Add(data);
            }

            RebuildGraphView();
            selectedNode = null;
            sessionDirty = true;
            PublishVATBakeSelection();
            Notify("Exported " + records.Count + " Animation node(s) across " + graphAsset.layers.Count + " layer(s).");
        }

        private Vector2 GetExportPosition(int index)
        {
            return new Vector2(
                40f + (index % 4) * 245f,
                80f + (index / 4) * 145f);
        }

        internal void AddMergeNode()
        {
            EnsureSession();
            AnimationMergeNodeData data = CreateNodeData(AnimationMergeNodeType.Merge, "Merge • choose bones");
            data.position = new Vector2(520f, 80f + graphAsset.nodes.Count * 12f);
            graphAsset.nodes.Add(data);
            if (graphView != null)
            {
                graphView.AddNode(data);
            }
            selectedNode = FindNodeView(data.id);
            Notify("Drag 2-4 Animation outputs into the Merge input port.");
        }

        internal void AddPreviewNode()
        {
            EnsureSession();
            AnimationMergeNodeData data = CreateNodeData(AnimationMergeNodeType.Preview, "Preview Animation");
            data.position = new Vector2(820f, 100f + graphAsset.nodes.Count * 12f);
            data.previewGameObject = graphAsset.sourceGameObject;
            graphAsset.nodes.Add(data);
            if (graphView != null)
            {
                graphView.AddNode(data);
            }
            selectedNode = FindNodeView(data.id);
            Notify("Connect an Animation output to Preview.");
        }

        private AnimationMergeNodeData CreateNodeData(AnimationMergeNodeType type, string title)
        {
            int colorIndex = GetNextColorIndex();
            return new AnimationMergeNodeData
            {
                id = graphAsset.AllocateNodeId(),
                nodeType = type,
                title = title,
                accentColor = GetNodeColor(colorIndex),
                colorIndex = colorIndex,
                position = Vector2.zero
            };
        }

        private int GetNextColorIndex()
        {
            if (graphAsset == null || graphAsset.nodes.Count == 0)
            {
                return 0;
            }

            return graphAsset.nodes.Max(node => node.colorIndex) + 1;
        }

        private Color GetNodeColor(int index)
        {
            float hue = Mathf.Repeat(Mathf.Abs(index) * 0.61803395f, 1f);
            return Color.HSVToRGB(hue, 0.68f, 0.92f);
        }

        private AnimationMergeNodeView FindNodeView(int id)
        {
            if (graphView == null)
            {
                return null;
            }

            return graphView.GetNodes().FirstOrDefault(node => node.data.id == id);
        }

        private void RebuildGraphView()
        {
            if (graphView == null || graphAsset == null)
            {
                return;
            }

            rebuildingGraphView = true;
            try
            {
                graphView.ClearGraph();
                Dictionary<int, AnimationMergeNodeView> views = new Dictionary<int, AnimationMergeNodeView>();
                Dictionary<int, AnimationMergeLayerGroupView> groups = new Dictionary<int, AnimationMergeLayerGroupView>();

                for (int i = 0; i < graphAsset.layers.Count; i++)
                {
                    AnimationMergeLayerData layer = graphAsset.layers[i];
                    AnimationMergeLayerGroupView group = new AnimationMergeLayerGroupView(layer);
                    graphView.AddLayerGroup(group);
                    groups[layer.layerIndex] = group;
                }

                for (int i = 0; i < graphAsset.nodes.Count; i++)
                {
                    AnimationMergeNodeData data = graphAsset.nodes[i];
                    if (data.accentColor == Color.white)
                    {
                        data.accentColor = GetNodeColor(data.colorIndex);
                    }

                    AnimationMergeNodeView view = graphView.AddNode(data);
                    views[data.id] = view;
                    AnimationMergeLayerGroupView group;
                    if (data.nodeType == AnimationMergeNodeType.Animation && groups.TryGetValue(data.layerIndex, out group))
                    {
                        graphView.AddNodeToLayer(view, group);
                    }
                }

                for (int i = 0; i < graphAsset.edges.Count; i++)
                {
                    AnimationMergeEdgeData edgeData = graphAsset.edges[i];
                    AnimationMergeNodeView outputNode;
                    AnimationMergeNodeView inputNode;
                    if (!views.TryGetValue(edgeData.outputNodeId, out outputNode) ||
                        !views.TryGetValue(edgeData.inputNodeId, out inputNode))
                    {
                        continue;
                    }

                    graphView.AddConnection(outputNode.GetPort("Animation") ?? outputNode.GetPort("Merged"), inputNode.GetPort(edgeData.inputPortName));
                }

                if (selectedNode != null)
                {
                    selectedNode = FindNodeView(selectedNode.data.id);
                }
            }
            finally
            {
                rebuildingGraphView = false;
            }
        }

        private void DrawInspector()
        {
            EditorGUILayout.LabelField("Animation Merge Inspector", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Animation nodes are exported from Animator layers. Connect 2-4 Animation outputs to Merge, choose the source per bone, then create a generated Animation node. Preview samples the clip on the selected GameObject.",
                MessageType.Info);
            if (vatBakeMode)
            {
                EditorGUILayout.HelpBox(
                    "VAT Bake integration is active. Generated Animation Merge outputs will appear in VAT Bake Tool > 2. Inputs as clips to bake.",
                    MessageType.Info);
            }

            if (selectedNode == null)
            {
                EditorGUILayout.LabelField("No node selected.");
                return;
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(selectedNode.title, EditorStyles.boldLabel);
            if (selectedNode is AnimationMergeMergeNodeView)
            {
                DrawMergeInspector((AnimationMergeMergeNodeView)selectedNode);
            }
            else if (selectedNode is AnimationMergePreviewNodeView)
            {
                DrawPreviewInspector((AnimationMergePreviewNodeView)selectedNode);
            }
            else
            {
                DrawAnimationInspector((AnimationMergeAnimationNodeView)selectedNode);
            }
        }

        private void DrawAnimationInspector(AnimationMergeAnimationNodeView animationNode)
        {
            AnimationMergeNodeData data = animationNode.data;
            EditorGUILayout.LabelField("Layer", data.layerIndex + " • " + data.layerName);
            EditorGUILayout.LabelField("State", data.statePath);
            EditorGUILayout.LabelField("Motion", data.motionPath);
            EditorGUILayout.ObjectField("Clip", data.clip, typeof(AnimationClip), false);
            if (data.isBlendTree)
            {
                EditorGUILayout.HelpBox(
                    "This node stores the BlendTree motion and its child clips. Connect one of the exposed child Animation nodes to Merge when you need a concrete clip.",
                    MessageType.None);
                for (int i = 0; i < data.blendTreeClips.Count; i++)
                {
                    EditorGUILayout.ObjectField("Child " + i, data.blendTreeClips[i], typeof(AnimationClip), false);
                }
            }
            if (data.isGenerated)
            {
                EditorGUILayout.HelpBox(
                    data.clip == null ? "Output is transient until saved as an AnimationClip asset." : "Generated clip is ready for preview or save.",
                    data.clip == null ? MessageType.Warning : MessageType.Info);
            }
        }

        private void DrawPreviewInspector(AnimationMergePreviewNodeView previewNode)
        {
            AnimationMergeNodeData data = previewNode.data;
            GameObject newPreviewObject = (GameObject)EditorGUILayout.ObjectField(
                "Preview GameObject",
                data.previewGameObject == null ? graphAsset.sourceGameObject : data.previewGameObject,
                typeof(GameObject),
                true);
            if (newPreviewObject != data.previewGameObject)
            {
                data.previewGameObject = newPreviewObject;
                MarkGraphChanged();
            }

            AnimationMergeAnimationNodeView input = graphView.GetAnimationInput(previewNode);
            EditorGUILayout.LabelField("Input", input == null ? "Not connected" : input.title);
            if (input != null && input.data.clip != null)
            {
                EditorGUILayout.ObjectField("Clip", input.data.clip, typeof(AnimationClip), false);
                if (GUILayout.Button(activePreviewNode == previewNode ? "Stop Preview" : "Play Preview"))
                {
                    PlayPreview(previewNode);
                }
                DrawPreviewStatus(input.data.clip, activePreviewNode == previewNode);
            }
            else
            {
                EditorGUILayout.HelpBox("Connect an Animation node with a concrete AnimationClip.", MessageType.Warning);
            }
        }

        private void DrawPreviewStatus(AnimationClip clip, bool isPlaying)
        {
            float duration = Mathf.Max(0.01f, clip.length);
            float currentTime = isPlaying ? Mathf.Clamp(previewTime, 0f, duration) : 0f;
            float progress = isPlaying ? Mathf.Clamp01(currentTime / duration) : 0f;
            Rect progressRect = EditorGUILayout.GetControlRect(false, 18f);
            string progressLabel = string.Format(
                "{0:0.00}s / {1:0.00}s",
                currentTime,
                duration);
            EditorGUI.ProgressBar(progressRect, progress, progressLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Status", isPlaying ? "Playing • Looping" : "Stopped");
            EditorGUILayout.LabelField("Frame Rate", clip.frameRate.ToString("0.##"));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Loop", "Enabled (automatic)");
            EditorGUILayout.LabelField(
                "Loop Count",
                isPlaying ? (previewLoopCount + 1).ToString() : "—");
        }

        private void DrawMergeInspector(AnimationMergeMergeNodeView mergeNode)
        {
            List<AnimationMergeAnimationNodeView> inputs = graphView.GetAnimationInputs(mergeNode);
            List<AnimationClip> clips = inputs.Select(input => input.data.clip).ToList();
            EditorGUILayout.LabelField("Inputs", inputs.Count + " / 4");
            for (int i = 0; i < inputs.Count; i++)
            {
                EditorGUILayout.LabelField("Clip " + (i + 1), inputs[i].data.isBlendTree ? "BlendTree (not mergeable directly)" : (clips[i] == null ? "Missing clip" : clips[i].name));
            }

            if (inputs.Count < 2 || inputs.Count > 4)
            {
                EditorGUILayout.HelpBox("Merge requires 2 to 4 concrete Animation nodes.", MessageType.Warning);
                return;
            }

            if (clips.Any(clip => clip == null) || inputs.Any(input => input.data.isBlendTree))
            {
                EditorGUILayout.HelpBox("BlendTree parent nodes are metadata only. Use their exposed child Animation nodes as Merge inputs.", MessageType.Warning);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            string newFilter = EditorGUILayout.TextField("Bone filter", mergeFilter);
            bool newIncludeChildFilter = GUILayout.Toggle(
                includeChildFilter,
                "Child",
                EditorStyles.toolbarButton,
                GUILayout.Width(100f));
            EditorGUILayout.EndHorizontal();
            if (!string.Equals(newFilter, mergeFilter, StringComparison.Ordinal))
            {
                mergeFilter = newFilter;
                RepaintInspector();
            }
            if (newIncludeChildFilter != includeChildFilter)
            {
                includeChildFilter = newIncludeChildFilter;
                RepaintInspector();
            }

            EnsureBoneChoices(mergeNode.data, clips);
            EditorGUILayout.Space(4f);
            DrawBoneMergeTable(mergeNode.data, clips);

            if (mergeNode.data.boneChoices.Count == 0)
            {
                EditorGUILayout.HelpBox("The input clips have no differing bone motion. No override rows are needed.", MessageType.Info);
            }

            string outputName = EditorGUILayout.TextField(
                "Output name",
                string.IsNullOrEmpty(mergeNode.data.outputClipName) ? "Merged_Animation" : mergeNode.data.outputClipName);
            if (!string.Equals(outputName, mergeNode.data.outputClipName, StringComparison.Ordinal))
            {
                mergeNode.data.outputClipName = outputName;
                MarkGraphChanged();
            }
            if (GUILayout.Button("Merge selected bone data"))
            {
                ExecuteMerge(mergeNode, inputs, clips);
            }

            if (mergeNode.data.outputClip != null)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.ObjectField("Current output", mergeNode.data.outputClip, typeof(AnimationClip), false);
                if (GUILayout.Button("Save output AnimationClip to file"))
                {
                    SaveOutputClip(mergeNode);
                }
            }

            if (!string.IsNullOrEmpty(mergeNode.data.lastMergeSummary))
            {
                EditorGUILayout.HelpBox(mergeNode.data.lastMergeSummary, MessageType.Info);
            }
        }

        private void EnsureBoneChoices(AnimationMergeNodeData mergeData, List<AnimationClip> clips)
        {
            List<string> paths = AnimationMergeClipUtility.GetDifferingBonePaths(clips);
            Dictionary<string, AnimationMergeBoneChoice> oldChoices = mergeData.boneChoices.ToDictionary(
                choice => choice.bonePath,
                choice => choice,
                StringComparer.Ordinal);
            mergeData.boneChoices.Clear();

            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                AnimationMergeBoneChoice choice;
                if (!oldChoices.TryGetValue(path, out choice))
                {
                    choice = new AnimationMergeBoneChoice
                    {
                        bonePath = path,
                        sourceIndex = FindFirstSourceWithMotionData(clips, path),
                        include = true
                    };
                }

                int firstSourceWithData = FindFirstSourceWithMotionData(clips, path);
                if (!AnimationMergeClipUtility.HasMotionData(clips[Mathf.Clamp(choice.sourceIndex, 0, clips.Count - 1)], path))
                {
                    choice.sourceIndex = firstSourceWithData;
                }
                else
                {
                    choice.sourceIndex = Mathf.Clamp(choice.sourceIndex, 0, clips.Count - 1);
                }

                choice.include = true;
                mergeData.boneChoices.Add(choice);
            }
        }

        private int FindFirstSourceWithMotionData(List<AnimationClip> clips, string path)
        {
            for (int i = 0; i < clips.Count; i++)
            {
                if (AnimationMergeClipUtility.HasMotionData(clips[i], path))
                {
                    return i;
                }
            }

            return 0;
        }

        private void DrawBoneMergeTable(AnimationMergeNodeData mergeData, List<AnimationClip> clips)
        {
            EditorGUILayout.Space(3f);
            EditorGUILayout.LabelField("Bone motion overrides", GetMergeSectionTitleStyle());
            boneMergeScrollPosition = EditorGUILayout.BeginScrollView(
                boneMergeScrollPosition,
                true,
                false);
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Bone", EditorStyles.boldLabel, GUILayout.Width(BoneColumnWidth));
            for (int clipIndex = 0; clipIndex < clips.Count; clipIndex++)
            {
                GUILayout.Label(
                    GetClipColumnContent(clips[clipIndex]),
                    EditorStyles.boldLabel,
                    GUILayout.Width(ClipToggleColumnWidth));
            }
            EditorGUILayout.EndHorizontal();

            List<string> displayedPaths = mergeData.boneChoices.Select(choice => choice.bonePath).ToList();
            for (int i = 0; i < mergeData.boneChoices.Count; i++)
            {
                AnimationMergeBoneChoice choice = mergeData.boneChoices[i];
                string displayPath = GetCompactBonePath(choice.bonePath, displayedPaths, i);
                if (!MatchesBoneFilter(choice.bonePath))
                {
                    continue;
                }

                EditorGUILayout.BeginHorizontal();
                float indentWidth = GetBoneDepth(choice.bonePath) * BoneIndentPerDepth;
                GUILayout.Space(indentWidth);
                GUILayout.Label(
                    displayPath,
                    GUILayout.Width(Mathf.Max(42f, BoneColumnWidth - indentWidth)));
                for (int clipIndex = 0; clipIndex < clips.Count; clipIndex++)
                {
                    DrawBoneSourceToggle(choice, clips[clipIndex], clipIndex);
                }
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(1f);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawBoneSourceToggle(AnimationMergeBoneChoice choice, AnimationClip clip, int sourceIndex)
        {
            bool hasMotionData = AnimationMergeClipUtility.HasMotionData(clip, choice.bonePath);
            bool selected = choice.sourceIndex == sourceIndex;
            EditorGUI.BeginDisabledGroup(!hasMotionData);
            bool toggled = GUILayout.Toggle(
                selected,
                GUIContent.none,
                EditorStyles.toolbarButton,
                GUILayout.Width(ClipToggleColumnWidth),
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUI.EndDisabledGroup();

            if (hasMotionData && toggled && choice.sourceIndex != sourceIndex)
            {
                choice.sourceIndex = sourceIndex;
                choice.include = true;
                MarkGraphChanged();
            }
        }

        private string GetClipColumnLabel(AnimationClip clip)
        {
            return clip == null ? "Missing clip" : clip.name;
        }

        private GUIContent GetClipColumnContent(AnimationClip clip)
        {
            string fullName = GetClipColumnLabel(clip);
            return new GUIContent(fullName, "Full clip name: " + fullName);
        }

        private GUIStyle GetMergeSectionTitleStyle()
        {
            if (mergeSectionTitleStyle == null)
            {
                mergeSectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = EditorStyles.boldLabel.fontSize + 1,
                    margin = new RectOffset(2, 0, 2, 3)
                };
            }

            return mergeSectionTitleStyle;
        }

        private bool MatchesBoneFilter(string path)
        {
            string filter = NormalizeBoneFilter(mergeFilter);
            if (string.IsNullOrEmpty(filter))
            {
                return true;
            }

            string[] segments = string.IsNullOrEmpty(path) ? new string[0] : path.Split('/');
            if (segments.Length == 0)
            {
                return string.Equals(filter, "root", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(filter, "root", StringComparison.OrdinalIgnoreCase) && includeChildFilter)
            {
                return true;
            }

            string currentPath = string.Empty;
            for (int i = 0; i < segments.Length; i++)
            {
                currentPath = string.IsNullOrEmpty(currentPath)
                    ? segments[i]
                    : currentPath + "/" + segments[i];

                bool isMatch = string.Equals(segments[i], filter, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(currentPath, filter, StringComparison.OrdinalIgnoreCase);
                if (!isMatch)
                {
                    isMatch = segments[i].IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                }

                if (!isMatch)
                {
                    continue;
                }

                if (i == segments.Length - 1 || includeChildFilter)
                {
                    return true;
                }
            }

            return false;
        }

        private string NormalizeBoneFilter(string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return string.Empty;
            }

            string normalized = filter.Trim();
            while (normalized.StartsWith("../", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(3);
            }

            return normalized.Trim('/');
        }

        private string GetCompactBonePath(string path, List<string> displayedPaths, int currentIndex)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "root";
            }

            string ancestor = string.Empty;
            bool hasAncestor = false;
            for (int i = 0; i < currentIndex; i++)
            {
                string candidate = displayedPaths[i];
                bool isRootAncestor = string.IsNullOrEmpty(candidate);
                bool isPathAncestor = !isRootAncestor && path.IndexOf(candidate + "/", StringComparison.Ordinal) == 0;
                if (!isRootAncestor && !isPathAncestor)
                {
                    continue;
                }

                if (!hasAncestor || candidate.Length > ancestor.Length)
                {
                    ancestor = candidate;
                    hasAncestor = true;
                }
            }

            if (!hasAncestor)
            {
                return path;
            }

            string suffix = string.IsNullOrEmpty(ancestor)
                ? path
                : path.Substring(ancestor.Length + 1);
            return "../" + suffix;
        }

        private int GetBoneDepth(string path)
        {
            // AnimationUtility uses an empty path for the animated root. A
            // direct child such as "Root_M" is therefore already one level deep.
            return string.IsNullOrEmpty(path) ? 0 : path.Split('/').Length;
        }

        private void ExecuteMerge(
            AnimationMergeMergeNodeView mergeNode,
            List<AnimationMergeAnimationNodeView> inputs,
            List<AnimationClip> clips)
        {
            try
            {
                AnimationClip merged = AnimationMergeClipUtility.CreateMergedClip(
                    clips,
                    mergeNode.data.boneChoices,
                    mergeNode.data.outputClipName);
                merged.hideFlags = HideFlags.HideAndDontSave;
                if (mergeNode.data.outputClip != null && !EditorUtility.IsPersistent(mergeNode.data.outputClip))
                {
                    DestroyImmediate(mergeNode.data.outputClip);
                }
                mergeNode.data.outputClip = merged;
                mergeNode.data.lastMergeSummary = "Merged " + clips.Count + " clips across " + mergeNode.data.boneChoices.Count + " bone paths.";

                AnimationMergeAnimationNodeView outputNode = FindGeneratedOutput(mergeNode.data.id);
                if (outputNode == null)
                {
                    AnimationMergeNodeData outputData = CreateNodeData(
                        AnimationMergeNodeType.Animation,
                        "Animation • " + merged.name);
                    outputData.isGenerated = true;
                    outputData.generatedFromMergeNodeId = mergeNode.data.id;
                    outputData.clip = merged;
                    outputData.position = mergeNode.GetPosition().position + new Vector2(330f, 0f);
                    graphAsset.nodes.Add(outputData);
                    outputNode = (AnimationMergeAnimationNodeView)graphView.AddNode(outputData);
                    graphView.AddConnection(((AnimationMergeMergeNodeView)mergeNode).outputPort, outputNode.sourcePort);
                }
                else
                {
                    outputNode.data.clip = merged;
                    outputNode.data.title = "Animation • " + merged.name;
                    outputNode.RefreshContent();
                }

                MarkGraphChanged();
                PublishVATBakeSelection();
                Notify("Merge succeeded. The generated Animation node is ready.");
            }
            catch (Exception exception)
            {
                Notify("Merge failed: " + exception.Message);
            }
        }

        private AnimationMergeAnimationNodeView FindGeneratedOutput(int mergeId)
        {
            return graphView.GetNodes().OfType<AnimationMergeAnimationNodeView>().FirstOrDefault(
                node => node.data.isGenerated && node.data.generatedFromMergeNodeId == mergeId);
        }

        private void SaveOutputClip(AnimationMergeMergeNodeView mergeNode)
        {
            if (mergeNode.data.outputClip == null)
            {
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject(
                "Save merged AnimationClip",
                string.IsNullOrEmpty(mergeNode.data.outputClipName) ? "Merged_Animation" : mergeNode.data.outputClipName,
                "anim",
                "Choose where to save the merged AnimationClip.");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null)
            {
                Notify("An asset already exists at that path. Choose another path.");
                return;
            }

            AnimationClip savedClip = Instantiate(mergeNode.data.outputClip);
            savedClip.hideFlags = HideFlags.None;
            savedClip.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(savedClip, path);
            AssetDatabase.SaveAssets();
            mergeNode.data.outputClip = savedClip;

            AnimationMergeAnimationNodeView outputNode = FindGeneratedOutput(mergeNode.data.id);
            if (outputNode != null)
            {
                outputNode.data.clip = savedClip;
                outputNode.data.title = "Animation • " + savedClip.name;
                outputNode.RefreshContent();
            }
            MarkGraphChanged();
            PublishVATBakeSelection();
            Notify("Saved merged clip to " + path);
        }

        private void PublishVATBakeSelection()
        {
            if (!vatBakeMode || vatBakeSelectionChanged == null || graphAsset == null)
            {
                return;
            }

            AnimationMergeBakeSelection selection = new AnimationMergeBakeSelection();
            selection.SourceAnimator = graphAsset.sourceAnimator;
            for (int i = 0; i < graphAsset.nodes.Count; i++)
            {
                AnimationMergeNodeData node = graphAsset.nodes[i];
                if (node == null || node.nodeType != AnimationMergeNodeType.Animation ||
                    node.clip == null || selection.Candidates.Contains(node.clip))
                {
                    continue;
                }

                selection.Candidates.Add(node.clip);
                if (node.bake)
                {
                    selection.Selected.Add(node.clip);
                }
            }

            vatBakeSelectionChanged(selection);
        }

        internal void PlayPreview(AnimationMergePreviewNodeView previewNode)
        {
            if (activePreviewNode == previewNode)
            {
                StopPreview();
                return;
            }

            AnimationMergeAnimationNodeView input = graphView.GetAnimationInput(previewNode);
            GameObject target = previewNode.data.previewGameObject == null
                ? graphAsset.sourceGameObject
                : previewNode.data.previewGameObject;
            if (input == null || input.data.clip == null || target == null)
            {
                Notify("Preview needs a connected AnimationClip and a preview GameObject.");
                return;
            }

            StopPreview();
            activePreviewNode = previewNode;
            previewTarget = target;
            previewClip = input.data.clip;
            previewStartedAt = EditorApplication.timeSinceStartup;
            previewTime = 0f;
            previewLoopCount = 0;
            AnimationMode.StartAnimationMode();
            Selection.activeGameObject = previewTarget;
            SamplePreview(0f);
            Notify("Previewing " + previewClip.name + ".");
        }

        private void UpdatePreview()
        {
            if (activePreviewNode == null || previewTarget == null || previewClip == null)
            {
                return;
            }

            float duration = Mathf.Max(0.01f, previewClip.length);
            float elapsed = Mathf.Max(0f, (float)(EditorApplication.timeSinceStartup - previewStartedAt));
            previewLoopCount = Mathf.FloorToInt(elapsed / duration);
            previewTime = elapsed - previewLoopCount * duration;
            SamplePreview(previewTime);
        }

        private void SamplePreview(float time)
        {
            if (!AnimationMode.InAnimationMode() || previewTarget == null || previewClip == null)
            {
                return;
            }

            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(previewTarget, previewClip, time);
            AnimationMode.EndSampling();
            SceneView.RepaintAll();
            RepaintInspector();
        }

        private void StopPreview()
        {
            if (AnimationMode.InAnimationMode())
            {
                AnimationMode.StopAnimationMode();
            }
            activePreviewNode = null;
            previewTarget = null;
            previewClip = null;
            previewTime = 0f;
            previewLoopCount = 0;
        }

        private void SaveSession()
        {
            if (graphAsset == null)
            {
                return;
            }

            SyncAssetFromView();
            if (AssetDatabase.Contains(graphAsset))
            {
                EditorUtility.SetDirty(graphAsset);
                AssetDatabase.SaveAssets();
                sessionDirty = false;
                Notify("Saved session asset.");
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject(
                "Save Animation Merge session",
                "AnimationMergeGraph",
                "asset",
                "Choose where to save the graph session ScriptableObject.");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<AnimationMergeGraphAsset>(path) != null)
            {
                Notify("A graph session already exists at that path. Choose another path.");
                return;
            }

            graphAsset.hideFlags = HideFlags.None;
            AssetDatabase.CreateAsset(graphAsset, path);
            AssetDatabase.SaveAssets();
            sessionDirty = false;
            Notify("Saved graph session to " + path);
        }

        private void LoadSession()
        {
            string absolutePath = EditorUtility.OpenFilePanel(
                "Load Animation Merge session",
                Application.dataPath,
                "asset");
            if (string.IsNullOrEmpty(absolutePath))
            {
                return;
            }

            string projectRelativePath = FileUtil.GetProjectRelativePath(absolutePath);
            AnimationMergeGraphAsset loaded = AssetDatabase.LoadAssetAtPath<AnimationMergeGraphAsset>(projectRelativePath);
            if (loaded == null)
            {
                Notify("The selected file is not an Animation Merge session asset.");
                return;
            }

            if (graphAsset != null && !AssetDatabase.Contains(graphAsset))
            {
                DestroyImmediate(graphAsset);
            }
            graphAsset = loaded;
            sourceGameObject = graphAsset.sourceGameObject;
            selectedNode = null;
            sessionDirty = false;
            RebuildGraphView();
            PublishVATBakeSelection();
            Notify("Loaded graph session.");
        }

        private void SyncAssetFromView()
        {
            if (graphAsset == null || graphView == null)
            {
                return;
            }

            graphAsset.sourceGameObject = sourceGameObject;
            graphAsset.sourceAnimator = sourceGameObject == null
                ? graphAsset.sourceAnimator
                : sourceGameObject.GetComponentInChildren<Animator>(true);

            graphAsset.edges.Clear();
            foreach (Edge edge in graphView.GetEdges())
            {
                AnimationMergeNodeView output = edge.output == null ? null : edge.output.node as AnimationMergeNodeView;
                AnimationMergeNodeView input = edge.input == null ? null : edge.input.node as AnimationMergeNodeView;
                if (output == null || input == null)
                {
                    continue;
                }
                graphAsset.edges.Add(new AnimationMergeEdgeData
                {
                    outputNodeId = output.data.id,
                    inputNodeId = input.data.id,
                    inputPortName = edge.input.portName
                });
            }

            foreach (AnimationMergeNodeView node in graphView.GetNodes())
            {
                node.data.position = node.GetPosition().position;
            }
        }

        private void RepaintInspector()
        {
            if (inspectorContainer != null)
            {
                inspectorContainer.MarkDirtyRepaint();
            }
        }

    }

}
