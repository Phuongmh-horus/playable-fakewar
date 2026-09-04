using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace OptimizedFeature.Scripts.Editor
{
    internal sealed class AnimationMergePortMarker
    {
    }

    internal abstract class AnimationMergeNodeView : Node
    {
        protected readonly AnimationMergeGraphWindow owner;
        public readonly AnimationMergeNodeData data;

        protected AnimationMergeNodeView(AnimationMergeGraphWindow owner, AnimationMergeNodeData data)
        {
            this.owner = owner;
            this.data = data;
            viewDataKey = data.id.ToString();
            title = string.IsNullOrEmpty(data.title) ? data.nodeType.ToString() : data.title;
            SetPosition(new Rect(data.position, new Vector2(230f, 120f)));
            RegisterCallback<MouseDownEvent>(OnMouseDown);
            ApplyAccentColor();
        }

        public Color AccentColor
        {
            get { return data.accentColor; }
        }

        private void OnMouseDown(MouseDownEvent evt)
        {
            owner.SelectNode(this);
        }

        protected void AddAccentBar()
        {
            VisualElement accentBar = new VisualElement();
            accentBar.style.height = 4f;
            accentBar.style.backgroundColor = AccentColor;
            mainContainer.Insert(0, accentBar);
        }

        private void ApplyAccentColor()
        {
            titleContainer.style.backgroundColor = new Color(
                AccentColor.r,
                AccentColor.g,
                AccentColor.b,
                0.32f);
        }

        protected Port CreatePort(Direction direction, Port.Capacity capacity, string portName)
        {
            Port port = InstantiatePort(
                Orientation.Horizontal,
                direction,
                capacity,
                typeof(AnimationMergePortMarker));
            port.portName = portName;
            return port;
        }

        public virtual Port GetPort(string portName)
        {
            return null;
        }

        public override void SetPosition(Rect newPos)
        {
            base.SetPosition(newPos);
            if (data != null)
            {
                data.position = newPos.position;
                if (!owner.IsRebuildingGraph)
                {
                    owner.MarkGraphChanged();
                }
            }
        }
    }

    internal sealed class AnimationMergeAnimationNodeView : AnimationMergeNodeView
    {
        public readonly Port outputPort;
        public readonly Port sourcePort;
        private readonly Toggle bakeToggle;

        public AnimationMergeAnimationNodeView(
            AnimationMergeGraphWindow owner,
            AnimationMergeNodeData data)
            : base(owner, data)
        {
            AddAccentBar();
            if (data.isGenerated)
            {
                sourcePort = CreatePort(Direction.Input, Port.Capacity.Single, "From Merge");
                inputContainer.Add(sourcePort);
            }

            outputPort = CreatePort(Direction.Output, Port.Capacity.Multi, "Animation");
            outputContainer.Add(outputPort);

            Label info = new Label(BuildInfoText());
            info.style.whiteSpace = WhiteSpace.Normal;
            info.style.marginTop = 4f;
            info.style.marginBottom = 4f;
            extensionContainer.Add(info);

            bakeToggle = new Toggle("Bake")
            {
                value = data.bake
            };
            bakeToggle.RegisterValueChangedCallback(change => owner.SetAnimationNodeBake(data, change.newValue));
            extensionContainer.Add(bakeToggle);
            RefreshExpandedState();
        }

        public void SetBakeValueWithoutNotify(bool value)
        {
            if (bakeToggle != null)
            {
                bakeToggle.SetValueWithoutNotify(value);
            }
        }

        public void RefreshContent()
        {
            title = string.IsNullOrEmpty(data.title) ? "Animation" : data.title;
        }

        private string BuildInfoText()
        {
            if (data.isGenerated)
            {
                return data.clip == null
                    ? "Generated output\nRun Merge to build the clip"
                    : "Generated output\n" + data.clip.name;
            }

            if (data.isBlendTree)
            {
                return "BlendTree\n" + data.blendTreeClips.Count + " child clip(s)\n" + data.statePath;
            }

            return (data.clip == null ? "Missing clip" : data.clip.name) + "\n" + data.statePath;
        }

        public override Port GetPort(string portName)
        {
            if (string.Equals(portName, "Animation", StringComparison.Ordinal))
            {
                return outputPort;
            }

            return string.Equals(portName, "From Merge", StringComparison.Ordinal) ? sourcePort : null;
        }
    }

    internal sealed class AnimationMergeMergeNodeView : AnimationMergeNodeView
    {
        public readonly Port inputPort;
        public readonly Port outputPort;

        public AnimationMergeMergeNodeView(
            AnimationMergeGraphWindow owner,
            AnimationMergeNodeData data)
            : base(owner, data)
        {
            AddAccentBar();
            inputPort = CreatePort(Direction.Input, Port.Capacity.Multi, "Animations (2-4)");
            inputContainer.Add(inputPort);
            outputPort = CreatePort(Direction.Output, Port.Capacity.Single, "Merged");
            outputContainer.Add(outputPort);

            Label info = new Label("Select per-bone source in Inspector");
            info.style.whiteSpace = WhiteSpace.Normal;
            info.style.marginTop = 4f;
            info.style.marginBottom = 4f;
            extensionContainer.Add(info);
            RefreshExpandedState();
        }

        public override Port GetPort(string portName)
        {
            if (string.Equals(portName, "Animations (2-4)", StringComparison.Ordinal) ||
                string.Equals(portName, "Animations (2)", StringComparison.Ordinal) ||
                string.Equals(portName, "Animations (2-3)", StringComparison.Ordinal))
            {
                return inputPort;
            }

            return string.Equals(portName, "Merged", StringComparison.Ordinal) ? outputPort : null;
        }
    }

    internal sealed class AnimationMergePreviewNodeView : AnimationMergeNodeView
    {
        public readonly Port inputPort;

        public AnimationMergePreviewNodeView(
            AnimationMergeGraphWindow owner,
            AnimationMergeNodeData data)
            : base(owner, data)
        {
            AddAccentBar();
            inputPort = CreatePort(Direction.Input, Port.Capacity.Single, "Animation");
            inputContainer.Add(inputPort);

            Button previewButton = new Button(() => owner.PlayPreview(this))
            {
                text = "Preview"
            };
            extensionContainer.Add(previewButton);
            RefreshExpandedState();
        }

        public override Port GetPort(string portName)
        {
            return string.Equals(portName, "Animation", StringComparison.Ordinal) ? inputPort : null;
        }
    }

    internal sealed class AnimationMergeLayerGroupView : Group
    {
        public readonly AnimationMergeLayerData layerData;

        public AnimationMergeLayerGroupView(AnimationMergeLayerData layer)
        {
            layerData = layer;
            title = string.IsNullOrEmpty(layer.layerName)
                ? "Layer " + layer.layerIndex
                : "Layer " + layer.layerIndex + " • " + layer.layerName;
            style.backgroundColor = new Color(
                layer.accentColor.r,
                layer.accentColor.g,
                layer.accentColor.b,
                0.25f);
            SetPosition(new Rect(
                new Vector2(20f + layer.layerIndex * 35f, 20f),
                new Vector2(920f, 580f)));
        }
    }

    internal sealed class AnimationMergeGraphView : GraphView
    {
        private readonly AnimationMergeGraphWindow owner;

        public AnimationMergeGraphView(AnimationMergeGraphWindow owner)
        {
            this.owner = owner;
            style.flexGrow = 1f;
            style.backgroundColor = new Color(0.10f, 0.11f, 0.13f, 1f);
            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            this.AddManipulator(new ContextualMenuManipulator(BuildContextMenu));
            graphViewChanged += OnGraphViewChanged;
            serializeGraphElements = elements => string.Empty;
            unserializeAndPaste = (operationName, data) => { };
        }

        private void BuildContextMenu(ContextualMenuPopulateEvent evt)
        {
            if (IsNodeContext(evt.target as VisualElement))
            {
                return;
            }

            evt.menu.AppendAction(
                "Node/Add Merge",
                action => owner.AddMergeNode());
            evt.menu.AppendAction(
                "Node/Add Preview",
                action => owner.AddPreviewNode());
        }

        private bool IsNodeContext(VisualElement target)
        {
            VisualElement current = target;
            while (current != null && current != this)
            {
                if (current is Node || current is Port || current is Edge)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        public void ClearGraph()
        {
            graphViewChanged -= OnGraphViewChanged;
            DeleteElements(graphElements.ToList());
            graphViewChanged += OnGraphViewChanged;
        }

        public void AddLayerGroup(AnimationMergeLayerGroupView group)
        {
            AddElement(group);
        }

        public AnimationMergeNodeView AddNode(AnimationMergeNodeData data)
        {
            AnimationMergeNodeView node;
            switch (data.nodeType)
            {
                case AnimationMergeNodeType.Merge:
                    node = new AnimationMergeMergeNodeView(owner, data);
                    break;
                case AnimationMergeNodeType.Preview:
                    node = new AnimationMergePreviewNodeView(owner, data);
                    break;
                default:
                    node = new AnimationMergeAnimationNodeView(owner, data);
                    break;
            }

            AddElement(node);
            return node;
        }

        public void AddNodeToLayer(AnimationMergeNodeView node, AnimationMergeLayerGroupView group)
        {
            if (group != null && node != null)
            {
                group.AddElement(node);
            }
        }

        public void AddConnection(Port output, Port input)
        {
            if (output == null || input == null)
            {
                return;
            }

            Edge edge = output.ConnectTo(input);
            AddElement(edge);
            ApplyEdgeColor(edge);
        }

        public List<AnimationMergeAnimationNodeView> GetAnimationInputs(AnimationMergeMergeNodeView mergeNode)
        {
            List<AnimationMergeAnimationNodeView> result = new List<AnimationMergeAnimationNodeView>();
            if (mergeNode == null)
            {
                return result;
            }

            foreach (Edge edge in edges.ToList())
            {
                if (edge.input == mergeNode.inputPort && edge.output != null && edge.output.node is AnimationMergeAnimationNodeView)
                {
                    result.Add((AnimationMergeAnimationNodeView)edge.output.node);
                }
            }

            return result;
        }

        public AnimationMergeAnimationNodeView GetAnimationInput(AnimationMergePreviewNodeView previewNode)
        {
            if (previewNode == null)
            {
                return null;
            }

            foreach (Edge edge in edges.ToList())
            {
                if (edge.input == previewNode.inputPort && edge.output != null && edge.output.node is AnimationMergeAnimationNodeView)
                {
                    return (AnimationMergeAnimationNodeView)edge.output.node;
                }
            }

            return null;
        }

        public IEnumerable<AnimationMergeNodeView> GetNodes()
        {
            return nodes.ToList().OfType<AnimationMergeNodeView>();
        }

        public IEnumerable<Edge> GetEdges()
        {
            return edges.ToList();
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            List<Port> compatiblePorts = new List<Port>();
            foreach (Port port in ports.ToList())
            {
                if (port == startPort || port.node == startPort.node || port.direction == startPort.direction)
                {
                    continue;
                }

                if (port.portType != startPort.portType)
                {
                    continue;
                }

                Port inputPort = port.direction == Direction.Input ? port : startPort;
                if (inputPort.node is AnimationMergeMergeNodeView &&
                    inputPort.connected &&
                    CountIncoming(inputPort) >= 4)
                {
                    continue;
                }

                compatiblePorts.Add(port);
            }

            return compatiblePorts;
        }

        private int CountIncoming(Port inputPort)
        {
            return edges.Count(edge => edge.input == inputPort);
        }

        private GraphViewChange OnGraphViewChanged(GraphViewChange change)
        {
            if (change.elementsToRemove != null && !owner.IsRebuildingGraph)
            {
                for (int i = 0; i < change.elementsToRemove.Count; i++)
                {
                    AnimationMergeNodeView removedNode = change.elementsToRemove[i] as AnimationMergeNodeView;
                    if (removedNode != null)
                    {
                        owner.RemoveNodeData(removedNode.data.id);
                    }
                }
            }

            if (change.edgesToCreate != null)
            {
                for (int i = change.edgesToCreate.Count - 1; i >= 0; i--)
                {
                    Edge edge = change.edgesToCreate[i];
                    if (edge.input != null && edge.input.node is AnimationMergeMergeNodeView && CountIncoming(edge.input) >= 4)
                    {
                        change.edgesToCreate.RemoveAt(i);
                        owner.Notify("A Merge node accepts at most 4 Animation inputs.");
                        continue;
                    }

                    ApplyEdgeColor(edge);
                }
            }

            if (!owner.IsRebuildingGraph)
            {
                owner.MarkGraphChanged();
            }
            return change;
        }

        private void ApplyEdgeColor(Edge edge)
        {
            if (edge == null || edge.edgeControl == null)
            {
                return;
            }

            AnimationMergeNodeView outputNode = edge.output == null ? null : edge.output.node as AnimationMergeNodeView;
            AnimationMergeNodeView inputNode = edge.input == null ? null : edge.input.node as AnimationMergeNodeView;
            Color outputColor = outputNode == null ? Color.white : outputNode.AccentColor;
            Color inputColor = inputNode == null ? Color.white : inputNode.AccentColor;
            edge.edgeControl.inputColor = inputColor;
            edge.edgeControl.outputColor = outputColor;
        }
    }
}
