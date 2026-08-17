using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using OptimizedFeature.Scripts;

namespace OptimizedFeature.Editor.VATAnimator
{
    internal sealed class VATAnimatorPortMarker
    {
    }

    internal abstract class VATAnimatorNodeView : Node
    {
        protected readonly VATAnimatorGraphWindow owner;
        public readonly VATAnimatorNodeData data;
        protected readonly VisualElement body;

        protected VATAnimatorNodeView(VATAnimatorGraphWindow owner, VATAnimatorNodeData data)
        {
            this.owner = owner;
            this.data = data;
            viewDataKey = data.id.ToString();
            title = owner.GetNodeTitle(data);

            style.width = 320f;
            SetPosition(new Rect(data.position, new Vector2(320f, 180f)));
            RegisterCallback<MouseDownEvent>(evt => owner.SelectNode(data.id));

            body = new VisualElement();
            body.style.paddingLeft = 8f;
            body.style.paddingRight = 8f;
            body.style.paddingTop = 5f;
            body.style.paddingBottom = 7f;
            extensionContainer.Add(body);

            ApplyAccentColor(GetAccentColor(data.nodeType));
        }

        protected static Color GetAccentColor(VATAnimatorNodeType type)
        {
            switch (type)
            {
                case VATAnimatorNodeType.Clip:
                    return new Color(0.22f, 0.58f, 0.82f);
                case VATAnimatorNodeType.Parameter:
                    return new Color(0.76f, 0.48f, 0.18f);
                case VATAnimatorNodeType.Transition:
                    return new Color(0.31f, 0.70f, 0.38f);
                case VATAnimatorNodeType.BlendTree:
                    return new Color(0.67f, 0.38f, 0.75f);
                case VATAnimatorNodeType.Default:
                    return new Color(0.82f, 0.64f, 0.24f);
                default:
                    return Color.gray;
            }
        }

        private void ApplyAccentColor(Color color)
        {
            titleContainer.style.backgroundColor = new Color(color.r, color.g, color.b, 0.38f);
            VisualElement accentBar = new VisualElement();
            accentBar.style.height = 4f;
            accentBar.style.backgroundColor = color;
            mainContainer.Insert(0, accentBar);
        }

        protected Port CreatePort(Direction direction, Port.Capacity capacity, string portName)
        {
            return CreatePort(direction, capacity, portName, typeof(VATAnimatorPortMarker));
        }

        protected Port CreatePort(
            Direction direction,
            Port.Capacity capacity,
            string portName,
            Type portType)
        {
            Port port = InstantiatePort(
                Orientation.Horizontal,
                direction,
                capacity,
                portType ?? typeof(VATAnimatorPortMarker));
            port.portName = portName;
            return port;
        }

        protected static string GetParameterPortName(VATAnimatorParameterType type)
        {
            return type.ToString();
        }

        protected static Type GetParameterPortType(VATAnimatorParameterType type)
        {
            switch (type)
            {
                case VATAnimatorParameterType.Bool:
                case VATAnimatorParameterType.Trigger:
                    return typeof(bool);
                case VATAnimatorParameterType.Float:
                    return typeof(float);
                case VATAnimatorParameterType.Vector2:
                    return typeof(Vector2);
                default:
                    return typeof(VATAnimatorPortMarker);
            }
        }

        public abstract Port GetPort(string portName);

        public void RefreshView()
        {
            title = owner.GetNodeTitle(data);
            RefreshDynamicPorts();
            body.Clear();
            BuildBody();
            RefreshExpandedState();
            RefreshPorts();
        }

        protected virtual void RefreshDynamicPorts()
        {
        }

        protected abstract void BuildBody();

        protected Label AddLabel(string text, Color? color = null)
        {
            Label label = new Label(text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginBottom = 3f;
            if (color.HasValue) label.style.color = color.Value;
            body.Add(label);
            return label;
        }

        protected T AddField<T>(T field) where T : VisualElement
        {
            field.style.marginBottom = 3f;
            body.Add(field);
            return field;
        }

        protected Foldout AddFoldout(string text, bool expanded = true)
        {
            Foldout foldout = new Foldout
            {
                text = text,
                value = expanded
            };
            foldout.style.marginTop = 4f;
            foldout.style.marginBottom = 4f;
            body.Add(foldout);
            return foldout;
        }

        protected static void StyleInlineControl(VisualElement control)
        {
            if (control == null) return;
            control.style.minWidth = 120f;
            control.style.flexGrow = 1f;
        }

        public override void SetPosition(Rect newPos)
        {
            base.SetPosition(newPos);
            if (data != null)
            {
                data.position = newPos.position;
                if (!owner.IsRebuildingGraph) owner.MarkGraphChanged();
            }
        }
    }

    internal sealed class VATAnimatorClipNodeView : VATAnimatorNodeView
    {
        private readonly Port inputPort;
        private readonly Port outputPort;

        public VATAnimatorClipNodeView(VATAnimatorGraphWindow owner, VATAnimatorNodeData data)
            : base(owner, data)
        {
            inputPort = CreatePort(Direction.Input, Port.Capacity.Multi, "In");
            inputContainer.Add(inputPort);
            outputPort = CreatePort(Direction.Output, Port.Capacity.Multi, "Out");
            outputContainer.Add(outputPort);
            BuildBody();
            RefreshExpandedState();
        }

        protected override void BuildBody()
        {
            VATAnimatorClipData clip = owner.GraphAsset == null ? null : owner.GraphAsset.FindClipByKey(data.clipKey);
            if (clip == null)
            {
                AddLabel("Missing source clip", new Color(1f, 0.45f, 0.35f));
                return;
            }

            AddLabel("Frames: " + clip.startFrame + " - " + clip.endFrame);
            AddLabel("Length: " + clip.TotalFrames + "  |  FPS: " + clip.frameRate.ToString("0.##"));
            AddLabel(clip.isLooping ? "Looping" : "One shot", new Color(0.72f, 0.82f, 0.88f));
            AddLabel("Hash: " + clip.stateHash, new Color(0.58f, 0.62f, 0.68f));

        }

        public override Port GetPort(string portName)
        {
            if (portName == "In") return inputPort;
            if (portName == "Out") return outputPort;
            return null;
        }
    }

    internal sealed class VATAnimatorParameterNodeView : VATAnimatorNodeView
    {
        private readonly Port outputPort;

        public VATAnimatorParameterNodeView(VATAnimatorGraphWindow owner, VATAnimatorNodeData data)
            : base(owner, data)
        {
            VATAnimatorParameterData parameter = owner.GraphAsset == null
                ? null
                : owner.GraphAsset.FindParameter(data.parameterId);
            VATAnimatorParameterType type = parameter == null
                ? VATAnimatorParameterType.Trigger
                : parameter.type;
            outputPort = CreatePort(
                Direction.Output,
                Port.Capacity.Multi,
                GetParameterPortName(type),
                GetParameterPortType(type));
            outputContainer.Add(outputPort);
            BuildBody();
            RefreshExpandedState();
        }

        protected override void BuildBody()
        {
            VATAnimatorParameterData parameter = owner.GraphAsset == null
                ? null
                : owner.GraphAsset.FindParameter(data.parameterId);
            if (parameter == null)
            {
                AddLabel("Missing parameter", new Color(1f, 0.45f, 0.35f));
                return;
            }

            AddLabel("Type: " + parameter.type, new Color(0.72f, 0.82f, 0.88f));
            AddLabel("Blackboard reference", new Color(0.58f, 0.62f, 0.68f));
        }

        public override Port GetPort(string portName)
        {
            return portName == outputPort.portName || portName == "Value" ? outputPort : null;
        }
    }

    internal sealed class VATAnimatorDefaultNodeView : VATAnimatorNodeView
    {
        private readonly Port outputPort;

        public VATAnimatorDefaultNodeView(VATAnimatorGraphWindow owner, VATAnimatorNodeData data)
            : base(owner, data)
        {
            outputPort = CreatePort(Direction.Output, Port.Capacity.Single, "Out");
            outputContainer.Add(outputPort);
            BuildBody();
            RefreshExpandedState();
        }

        protected override void BuildBody()
        {
            VATAnimatorClipData clip = owner.GraphAsset == null
                ? null
                : owner.GraphAsset.FindClipByKey(owner.GraphAsset.defaultClipKey);
            AddLabel(clip == null
                ? "Connect Out to a Clip node"
                : "Default Clip: " + clip.clipName,
                clip == null ? new Color(0.72f, 0.82f, 0.88f) : (Color?)null);
        }

        public override Port GetPort(string portName)
        {
            return portName == "Out" ? outputPort : null;
        }
    }

    internal sealed class VATAnimatorTransitionNodeView : VATAnimatorNodeView
    {
        private readonly Port fromPort;
        private readonly Port toPort;
        private readonly Dictionary<int, Port> conditionPorts = new Dictionary<int, Port>();
        private Port newConditionPort;

        public VATAnimatorTransitionNodeView(VATAnimatorGraphWindow owner, VATAnimatorNodeData data)
            : base(owner, data)
        {
            fromPort = CreatePort(Direction.Input, Port.Capacity.Single, "From");
            inputContainer.Add(fromPort);
            toPort = CreatePort(Direction.Output, Port.Capacity.Single, "To");
            outputContainer.Add(toPort);
            BuildConditionPorts();
            BuildBody();
            RefreshExpandedState();
        }

        protected override void RefreshDynamicPorts()
        {
            VATAnimatorTransitionData transition = owner.GraphAsset == null
                ? null
                : owner.GraphAsset.FindTransition(data.transitionId);
            if (transition != null && transition.conditions != null &&
                newConditionPort != null && conditionPorts.Count == transition.conditions.Count)
            {
                bool unchanged = true;
                for (int i = 0; i < transition.conditions.Count; i++)
                {
                    VATAnimatorConditionData condition = transition.conditions[i];
                    if (condition == null || !conditionPorts.ContainsKey(condition.id))
                    {
                        unchanged = false;
                        break;
                    }
                }
                if (unchanged) return;
            }

            foreach (Port port in conditionPorts.Values) inputContainer.Remove(port);
            conditionPorts.Clear();
            if (newConditionPort != null) inputContainer.Remove(newConditionPort);
            newConditionPort = null;
            BuildConditionPorts();
        }

        private void BuildConditionPorts()
        {
            VATAnimatorTransitionData transition = owner.GraphAsset == null
                ? null
                : owner.GraphAsset.FindTransition(data.transitionId);
            if (transition == null || transition.conditions == null) return;

            for (int i = 0; i < transition.conditions.Count; i++)
            {
                VATAnimatorConditionData condition = transition.conditions[i];
                if (condition == null) continue;
                string portName = VATAnimatorGraphAsset.GetConditionPortName(condition.id);
                Port port = CreatePort(
                    Direction.Input,
                    Port.Capacity.Single,
                    portName,
                    typeof(VATAnimatorPortMarker));
                inputContainer.Add(port);
                conditionPorts[condition.id] = port;
            }

            newConditionPort = CreatePort(
                Direction.Input,
                Port.Capacity.Single,
                VATAnimatorGraphAsset.NewConditionPortName,
                typeof(VATAnimatorPortMarker));
            inputContainer.Add(newConditionPort);
        }

        protected override void BuildBody()
        {
            VATAnimatorTransitionData transition = owner.GraphAsset == null
                ? null
                : owner.GraphAsset.FindTransition(data.transitionId);
            if (transition == null)
            {
                AddLabel("Missing transition data", new Color(1f, 0.45f, 0.35f));
                return;
            }

            if (transition.conditions == null)
            {
                transition.conditions = new List<VATAnimatorConditionData>();
            }

            TextField titleField = new TextField("Title")
            {
                value = transition.title,
                isDelayed = true
            };
            StyleInlineControl(titleField);
            titleField.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == transition.title) return;
                owner.RecordGraphUndo("Rename VAT Animator Transition");
                transition.title = evt.newValue;
                owner.MarkGraphChanged();
                owner.RefreshNodeView(data.id);
            });
            AddField(titleField);

            Toggle autoField = new Toggle("Auto Transition")
            {
                value = transition.autoTransition
            };
            autoField.RegisterValueChangedCallback(evt =>
            {
                owner.RecordGraphUndo("Edit VAT Animator Transition");
                transition.autoTransition = evt.newValue;
                owner.MarkGraphChanged();
            });
            AddField(autoField);

            Toggle exitField = new Toggle("Has Exit Time")
            {
                value = transition.hasExitTime
            };
            exitField.RegisterValueChangedCallback(evt =>
            {
                owner.RecordGraphUndo("Edit VAT Animator Exit Time");
                transition.hasExitTime = evt.newValue;
                owner.MarkGraphChanged();
                owner.RefreshNodeView(data.id);
            });
            AddField(exitField);

            FloatField durationField = new FloatField("Duration")
            {
                value = transition.duration
            };
            StyleInlineControl(durationField);
            durationField.RegisterValueChangedCallback(evt =>
            {
                owner.RecordGraphUndo("Edit VAT Animator Transition Duration");
                transition.duration = Mathf.Max(0f, evt.newValue);
                owner.MarkGraphChanged();
            });
            AddField(durationField);

            if (transition.hasExitTime)
            {
                FloatField exitTimeField = new FloatField("Exit Time")
                {
                    value = transition.exitTime
                };
                StyleInlineControl(exitTimeField);
                exitTimeField.RegisterValueChangedCallback(evt =>
                {
                    owner.RecordGraphUndo("Edit VAT Animator Exit Time");
                    transition.exitTime = Mathf.Max(0f, evt.newValue);
                    owner.MarkGraphChanged();
                });
                AddField(exitTimeField);
            }

            Foldout conditionsFoldout = AddFoldout("Conditions (" + transition.conditions.Count + ")", true);
            for (int i = 0; i < transition.conditions.Count; i++)
            {
                VATAnimatorConditionData condition = transition.conditions[i];
                if (condition != null) BuildConditionEditor(conditionsFoldout, transition, condition);
            }

        }

        private void BuildConditionEditor(
            Foldout parent,
            VATAnimatorTransitionData transition,
            VATAnimatorConditionData condition)
        {
            VisualElement conditionBox = new VisualElement();
            conditionBox.style.marginTop = 3f;
            conditionBox.style.marginBottom = 5f;
            conditionBox.style.paddingLeft = 4f;
            conditionBox.style.paddingRight = 4f;
            conditionBox.style.paddingTop = 3f;
            conditionBox.style.paddingBottom = 3f;
            conditionBox.style.backgroundColor = new Color(0.08f, 0.10f, 0.12f, 0.75f);
            parent.Add(conditionBox);

            VisualElement conditionHeader = new VisualElement();
            conditionHeader.style.flexDirection = FlexDirection.Row;
            conditionHeader.style.alignItems = Align.Center;
            Label conditionTitle = new Label("Condition:" + condition.id);
            conditionTitle.style.flexGrow = 1f;
            conditionHeader.Add(conditionTitle);
            Button removeButton = new Button(() =>
            {
                owner.RecordGraphUndo("Remove VAT Animator Condition");
                owner.GraphAsset.RemoveTransitionCondition(transition.id, condition.id);
                owner.MarkGraphChanged();
                owner.RebuildGraphView();
            })
            {
                text = "X"
            };
            removeButton.style.width = 20f;
            removeButton.style.minWidth = 20f;
            conditionHeader.Add(removeButton);
            conditionBox.Add(conditionHeader);

            VATAnimatorParameterData parameter = owner.GraphAsset.FindParameter(condition.parameterId);
            if (parameter == null)
            {
                Label missing = new Label("Input: connect a Parameter node");
                missing.style.color = new Color(1f, 0.45f, 0.35f);
                conditionBox.Add(missing);
            }
            else
            {
                Label inputLabel = new Label("Input: " + parameter.parameterName + " (" + parameter.type + ")");
                inputLabel.style.color = new Color(0.72f, 0.82f, 0.88f);
                conditionBox.Add(inputLabel);
                if (parameter.type != VATAnimatorParameterType.Trigger)
                {
                    VATAnimatorConditionMode[] modes = GetConditionModes(parameter.type);
                    List<string> modeNames = modes.Select(mode => mode.ToString()).ToList();
                    int modeIndex = Array.IndexOf(modes, condition.mode);
                    if (modeIndex < 0) modeIndex = 0;
                    PopupField<string> modeField = new PopupField<string>("Mode", modeNames, modeIndex);
                    StyleInlineControl(modeField);
                    modeField.RegisterValueChangedCallback(evt =>
                    {
                        int index = modeNames.IndexOf(evt.newValue);
                        if (index < 0 || index >= modes.Length) return;
                        owner.RecordGraphUndo("Edit VAT Animator Condition Mode");
                        condition.mode = modes[index];
                        owner.MarkGraphChanged();
                    });
                    conditionBox.Add(modeField);
                }
                AddConditionValueField(conditionBox, condition, parameter);
            }

        }

        private void AddConditionValueField(
            VisualElement parent,
            VATAnimatorConditionData condition,
            VATAnimatorParameterData parameter)
        {
            switch (parameter.type)
            {
                case VATAnimatorParameterType.Bool:
                    Toggle boolField = new Toggle("Value")
                    {
                        value = condition.boolThreshold
                    };
                    boolField.RegisterValueChangedCallback(evt =>
                    {
                        owner.RecordGraphUndo("Edit VAT Animator Bool Condition");
                        condition.boolThreshold = evt.newValue;
                        owner.MarkGraphChanged();
                    });
                    parent.Add(boolField);
                    break;
                case VATAnimatorParameterType.Float:
                    FloatField floatField = new FloatField("Threshold")
                    {
                        value = condition.threshold
                    };
                    StyleInlineControl(floatField);
                    floatField.RegisterValueChangedCallback(evt =>
                    {
                        owner.RecordGraphUndo("Edit VAT Animator Float Condition");
                        condition.threshold = evt.newValue;
                        owner.MarkGraphChanged();
                    });
                    parent.Add(floatField);
                    break;
                case VATAnimatorParameterType.Vector2:
                    Vector2Field vectorField = new Vector2Field("Threshold")
                    {
                        value = condition.vectorThreshold
                    };
                    StyleInlineControl(vectorField);
                    vectorField.RegisterValueChangedCallback(evt =>
                    {
                        owner.RecordGraphUndo("Edit VAT Animator Vector2 Condition");
                        condition.vectorThreshold = evt.newValue;
                        owner.MarkGraphChanged();
                    });
                    parent.Add(vectorField);
                    break;
            }
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

        public override Port GetPort(string portName)
        {
            if (portName == "From") return fromPort;
            if (portName == "To") return toPort;
            if (portName == VATAnimatorGraphAsset.NewConditionPortName) return newConditionPort;
            Port conditionPort;
            if (conditionPorts.TryGetValue(ParseConditionPortId(portName), out conditionPort)) return conditionPort;
            return null;
        }

        private static int ParseConditionPortId(string portName)
        {
            int conditionId;
            return portName != null && portName.StartsWith("Condition:", StringComparison.Ordinal) &&
                   int.TryParse(portName.Substring("Condition:".Length), out conditionId)
                ? conditionId
                : -1;
        }
    }

    internal sealed class VATAnimatorBlendTreeNodeView : VATAnimatorNodeView
    {
        private readonly Port entryPort;
        private readonly Port parameterPort;
        private readonly Dictionary<int, Port> casePorts = new Dictionary<int, Port>();
        private Port newCasePort;

        public VATAnimatorBlendTreeNodeView(VATAnimatorGraphWindow owner, VATAnimatorNodeData data)
            : base(owner, data)
        {
            entryPort = CreatePort(Direction.Input, Port.Capacity.Single, "Entry");
            inputContainer.Add(entryPort);
            parameterPort = CreatePort(Direction.Input, Port.Capacity.Single, "Parameter");
            inputContainer.Add(parameterPort);
            BuildCasePorts();
            BuildBody();
            RefreshExpandedState();
        }

        protected override void RefreshDynamicPorts()
        {
            VATAnimatorBlendTreeData tree = owner.GraphAsset == null
                ? null
                : owner.GraphAsset.FindBlendTree(data.blendTreeId);
            if (tree != null && tree.children != null && newCasePort != null &&
                casePorts.Count == tree.children.Count)
            {
                bool unchanged = true;
                for (int i = 0; i < tree.children.Count; i++)
                {
                    VATAnimatorBlendChildData child = tree.children[i];
                    if (child == null || !casePorts.ContainsKey(child.id))
                    {
                        unchanged = false;
                        break;
                    }
                }
                if (unchanged) return;
            }

            foreach (Port port in casePorts.Values) outputContainer.Remove(port);
            casePorts.Clear();
            if (newCasePort != null) outputContainer.Remove(newCasePort);
            newCasePort = null;
            BuildCasePorts();
        }

        private void BuildCasePorts()
        {
            VATAnimatorBlendTreeData tree = owner.GraphAsset == null
                ? null
                : owner.GraphAsset.FindBlendTree(data.blendTreeId);
            if (tree == null) return;
            if (tree.children == null) tree.children = new List<VATAnimatorBlendChildData>();

            for (int i = 0; i < tree.children.Count; i++)
            {
                VATAnimatorBlendChildData child = tree.children[i];
                if (child == null) continue;
                string portName = VATAnimatorGraphAsset.GetBlendCasePortName(child.id);
                Port port = CreatePort(
                    Direction.Output,
                    Port.Capacity.Single,
                    portName,
                    typeof(VATAnimatorPortMarker));
                outputContainer.Add(port);
                casePorts[child.id] = port;
            }

            newCasePort = CreatePort(
                Direction.Output,
                Port.Capacity.Single,
                VATAnimatorGraphAsset.NewBlendCasePortName,
                typeof(VATAnimatorPortMarker));
            outputContainer.Add(newCasePort);
        }

        protected override void BuildBody()
        {
            VATAnimatorBlendTreeData tree = owner.GraphAsset == null
                ? null
                : owner.GraphAsset.FindBlendTree(data.blendTreeId);
            if (tree == null)
            {
                AddLabel("Missing blend tree data", new Color(1f, 0.45f, 0.35f));
                return;
            }

            if (tree.children == null)
            {
                tree.children = new List<VATAnimatorBlendChildData>();
            }

            TextField titleField = new TextField("Title")
            {
                value = tree.title,
                isDelayed = true
            };
            StyleInlineControl(titleField);
            titleField.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == tree.title) return;
                owner.RecordGraphUndo("Rename VAT Animator Blend Tree");
                tree.title = evt.newValue;
                owner.MarkGraphChanged();
                owner.RefreshNodeView(data.id);
            });
            AddField(titleField);

            VATAnimatorParameterData parameter = owner.GraphAsset.FindParameter(tree.parameterId);
            if (parameter == null)
            {
                AddLabel("Input: connect a Float or Vector2 Parameter node", new Color(1f, 0.68f, 0.30f));
            }
            else if (parameter.type != VATAnimatorParameterType.Float &&
                     parameter.type != VATAnimatorParameterType.Vector2)
            {
                AddLabel("Input: " + parameter.parameterName + " (" + parameter.type + ")", new Color(1f, 0.45f, 0.35f));
                AddLabel("BlendTree requires Float or Vector2", new Color(1f, 0.45f, 0.35f));
            }
            else
            {
                tree.mode = parameter.type == VATAnimatorParameterType.Vector2
                    ? VATAnimatorBlendTreeMode.TwoDimensional
                    : VATAnimatorBlendTreeMode.OneDimensional;
                AddLabel("Input: " + parameter.parameterName + " (" + parameter.type + ")");
                AddLabel("Mode: " + tree.mode, new Color(0.72f, 0.82f, 0.88f));
            }

            Toggle clampField = new Toggle("Clamp Input")
            {
                value = tree.clampInput
            };
            clampField.RegisterValueChangedCallback(evt =>
            {
                owner.RecordGraphUndo("Edit VAT Animator Blend Tree");
                tree.clampInput = evt.newValue;
                owner.MarkGraphChanged();
            });
            AddField(clampField);

            Foldout childrenFoldout = AddFoldout("Output Cases (" + tree.children.Count + ")", true);
            for (int i = 0; i < tree.children.Count; i++)
            {
                VATAnimatorBlendChildData child = tree.children[i];
                if (child != null) BuildBlendChildEditor(childrenFoldout, tree, child);
            }
            AddLabel("Connect [New] to a Clip node to create an output case.",
                new Color(0.58f, 0.62f, 0.68f));
        }

        private void BuildBlendChildEditor(
            Foldout parent,
            VATAnimatorBlendTreeData tree,
            VATAnimatorBlendChildData child)
        {
            VisualElement childBox = new VisualElement();
            childBox.style.marginTop = 3f;
            childBox.style.marginBottom = 5f;
            childBox.style.paddingLeft = 4f;
            childBox.style.paddingRight = 4f;
            childBox.style.paddingTop = 3f;
            childBox.style.paddingBottom = 3f;
            childBox.style.backgroundColor = new Color(0.08f, 0.10f, 0.12f, 0.75f);
            parent.Add(childBox);

            VATAnimatorClipData clip = owner.GraphAsset.FindClipByKey(child.clipKey);
            VisualElement caseHeader = new VisualElement();
            caseHeader.style.flexDirection = FlexDirection.Row;
            caseHeader.style.alignItems = Align.Center;
            Label caseTitle = new Label("Case:" + child.id + "  →  " +
                (clip == null ? "<Missing Clip>" : clip.clipName));
            caseTitle.style.flexGrow = 1f;
            caseHeader.Add(caseTitle);
            Button removeButton = new Button(() =>
            {
                owner.RecordGraphUndo("Remove VAT Animator Blend Case");
                owner.RemoveBlendTreeCase(data.blendTreeId, child.id);
            })
            {
                text = "X"
            };
            removeButton.style.width = 20f;
            removeButton.style.minWidth = 20f;
            caseHeader.Add(removeButton);
            childBox.Add(caseHeader);

            if (tree.mode == VATAnimatorBlendTreeMode.OneDimensional)
            {
                FloatField thresholdField = new FloatField("Threshold")
                {
                    value = child.threshold.x
                };
                StyleInlineControl(thresholdField);
                thresholdField.RegisterValueChangedCallback(evt =>
                {
                    owner.RecordGraphUndo("Edit VAT Animator Blend Threshold");
                    child.threshold = new Vector2(evt.newValue, 0f);
                    owner.MarkGraphChanged();
                });
                childBox.Add(thresholdField);
            }
            else
            {
                Vector2Field thresholdField = new Vector2Field("Threshold")
                {
                    value = child.threshold
                };
                StyleInlineControl(thresholdField);
                thresholdField.RegisterValueChangedCallback(evt =>
                {
                    owner.RecordGraphUndo("Edit VAT Animator Blend Threshold");
                    child.threshold = evt.newValue;
                    owner.MarkGraphChanged();
                });
                childBox.Add(thresholdField);
            }

        }

        public override Port GetPort(string portName)
        {
            if (portName == "Entry") return entryPort;
            if (portName == "Parameter") return parameterPort;
            if (portName == VATAnimatorGraphAsset.NewBlendCasePortName) return newCasePort;
            int caseId;
            if (TryParseCasePortId(portName, out caseId))
            {
                Port casePort;
                if (casePorts.TryGetValue(caseId, out casePort)) return casePort;
            }
            return null;
        }

        private static bool TryParseCasePortId(string portName, out int caseId)
        {
            caseId = -1;
            return portName != null && portName.StartsWith("Case:", StringComparison.Ordinal) &&
                   int.TryParse(portName.Substring("Case:".Length), out caseId) && caseId > 0;
        }
    }

    internal sealed class VATAnimatorGraphView : GraphView
    {
        private readonly VATAnimatorGraphWindow owner;
        private readonly Dictionary<int, VATAnimatorNodeView> nodeViews = new Dictionary<int, VATAnimatorNodeView>();

        public VATAnimatorGraphView(VATAnimatorGraphWindow owner)
        {
            this.owner = owner;
            style.flexGrow = 1f;
            style.backgroundColor = new Color(0.10f, 0.11f, 0.13f, 1f);

            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            this.AddManipulator(new ContextualMenuManipulator(BuildContextMenu));

            GridBackground grid = new GridBackground();
            Insert(0, grid);
            grid.StretchToParentSize();
            graphViewChanged += OnGraphViewChanged;

            serializeGraphElements = elements => string.Empty;
            unserializeAndPaste = (operationName, data) => { };
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            List<Port> compatible = new List<Port>();
            foreach (Port port in ports.ToList())
            {
                if (port == startPort || port.node == startPort.node || port.direction == startPort.direction)
                {
                    continue;
                }

                Port output = startPort.direction == Direction.Output ? startPort : port;
                Port input = startPort.direction == Direction.Input ? startPort : port;
                if (IsCompatible(output, input)) compatible.Add(port);
            }
            return compatible;
        }

        private bool IsCompatible(Port output, Port input)
        {
            if (output == null || input == null) return false;

            VATAnimatorNodeView outputNode = output.node as VATAnimatorNodeView;
            VATAnimatorNodeView inputNode = input.node as VATAnimatorNodeView;
            if (outputNode != null && outputNode.data.nodeType == VATAnimatorNodeType.Parameter)
            {
                VATAnimatorParameterData parameter = owner.GraphAsset == null
                    ? null
                    : owner.GraphAsset.FindParameter(outputNode.data.parameterId);
                if (parameter == null) return false;

                if (inputNode != null && inputNode.data.nodeType == VATAnimatorNodeType.Transition &&
                    (input.portName == VATAnimatorGraphAsset.NewConditionPortName ||
                     IsConditionPort(input.portName)))
                {
                    return output.portName == parameter.type.ToString();
                }

                return inputNode != null && inputNode.data.nodeType == VATAnimatorNodeType.BlendTree &&
                       input.portName == "Parameter" &&
                       (parameter.type == VATAnimatorParameterType.Float ||
                        parameter.type == VATAnimatorParameterType.Vector2) &&
                       output.portName == parameter.type.ToString();
            }

            if (outputNode != null && outputNode.data.nodeType == VATAnimatorNodeType.Clip)
            {
                return output.portName == "Out" &&
                       inputNode != null && inputNode.data.nodeType == VATAnimatorNodeType.Transition &&
                       input.portName == "From";
            }

            if (outputNode != null && outputNode.data.nodeType == VATAnimatorNodeType.Transition)
            {
                return output.portName == "To" && inputNode != null &&
                       ((inputNode.data.nodeType == VATAnimatorNodeType.Clip && input.portName == "In") ||
                        (inputNode.data.nodeType == VATAnimatorNodeType.BlendTree && input.portName == "Entry"));
            }

            if (outputNode != null && outputNode.data.nodeType == VATAnimatorNodeType.BlendTree)
            {
                return inputNode != null && inputNode.data.nodeType == VATAnimatorNodeType.Clip &&
                       input.portName == "In" &&
                       (output.portName == VATAnimatorGraphAsset.NewBlendCasePortName ||
                        IsBlendCasePort(output.portName));
            }

            if (outputNode != null && outputNode.data.nodeType == VATAnimatorNodeType.Default)
            {
                return output.portName == "Out" &&
                       inputNode != null && inputNode.data.nodeType == VATAnimatorNodeType.Clip &&
                       input.portName == "In";
            }

            return false;
        }

        private static bool IsConditionPort(string portName)
        {
            int conditionId;
            return portName != null && portName.StartsWith("Condition:", StringComparison.Ordinal) &&
                   int.TryParse(portName.Substring("Condition:".Length), out conditionId) && conditionId > 0;
        }

        private static bool IsBlendCasePort(string portName)
        {
            int caseId;
            return portName != null && portName.StartsWith("Case:", StringComparison.Ordinal) &&
                   int.TryParse(portName.Substring("Case:".Length), out caseId) && caseId > 0;
        }

        public void RebuildGraph()
        {
            owner.BeginGraphRebuild();
            try
            {
                nodeViews.Clear();
                DeleteElements(graphElements.ToList());

                if (owner.GraphAsset == null || owner.GraphAsset.sourceVATAsset == null) return;
                owner.GraphAsset.EnsureLists();

                for (int i = 0; i < owner.GraphAsset.nodes.Count; i++)
                {
                    VATAnimatorNodeData data = owner.GraphAsset.nodes[i];
                    if (data == null) continue;
                    VATAnimatorNodeView node = CreateNodeView(data);
                    if (node == null) continue;
                    nodeViews[data.id] = node;
                    AddElement(node);
                }

                for (int i = 0; i < owner.GraphAsset.edges.Count; i++)
                {
                    VATAnimatorEdgeData edgeData = owner.GraphAsset.edges[i];
                    if (edgeData == null) continue;
                    Port output = FindPort(edgeData.outputNodeId, edgeData.outputPortName);
                    Port input = FindPort(edgeData.inputNodeId, edgeData.inputPortName);
                    if (output == null || input == null) continue;
                    Edge edge = output.ConnectTo(input);
                    AddElement(edge);
                }
            }
            finally
            {
                owner.EndGraphRebuild();
            }
        }

        public void RefreshNode(int nodeId)
        {
            VATAnimatorNodeView node;
            if (nodeViews.TryGetValue(nodeId, out node)) node.RefreshView();
        }

        public void FrameAllView()
        {
            FrameAll();
        }

        public void SelectNode(int nodeId)
        {
            VATAnimatorNodeView node;
            if (!nodeViews.TryGetValue(nodeId, out node)) return;
            ClearSelection();
            AddToSelection(node);
            owner.SelectNode(nodeId);
        }

        private VATAnimatorNodeView CreateNodeView(VATAnimatorNodeData data)
        {
            switch (data.nodeType)
            {
                case VATAnimatorNodeType.Clip:
                    return new VATAnimatorClipNodeView(owner, data);
                case VATAnimatorNodeType.Parameter:
                    return new VATAnimatorParameterNodeView(owner, data);
                case VATAnimatorNodeType.Transition:
                    return new VATAnimatorTransitionNodeView(owner, data);
                case VATAnimatorNodeType.BlendTree:
                    return new VATAnimatorBlendTreeNodeView(owner, data);
                case VATAnimatorNodeType.Default:
                    return new VATAnimatorDefaultNodeView(owner, data);
                default:
                    return null;
            }
        }

        private Port FindPort(int nodeId, string portName)
        {
            VATAnimatorNodeView node;
            return nodeViews.TryGetValue(nodeId, out node) ? node.GetPort(portName) : null;
        }

        private GraphViewChange OnGraphViewChanged(GraphViewChange change)
        {
            if (owner.IsRebuildingGraph) return change;
            bool rebuildRequired = false;
            List<Edge> edgesToDiscard = new List<Edge>();

            if (change.movedElements != null)
            {
                for (int i = 0; i < change.movedElements.Count; i++)
                {
                    VATAnimatorNodeView node = change.movedElements[i] as VATAnimatorNodeView;
                    if (node != null) node.data.position = node.GetPosition().position;
                }
            }

            if (change.elementsToRemove != null)
            {
                for (int i = 0; i < change.elementsToRemove.Count; i++)
                {
                    Edge edge = change.elementsToRemove[i] as Edge;
                    if (edge != null)
                    {
                        VATAnimatorEdgeData edgeData = ToEdgeData(edge);
                        owner.RemoveEdgeData(edgeData);
                        rebuildRequired = rebuildRequired || owner.LastEdgeRemovalChangedPorts;
                    }
                }

                for (int i = 0; i < change.elementsToRemove.Count; i++)
                {
                    VATAnimatorNodeView node = change.elementsToRemove[i] as VATAnimatorNodeView;
                    if (node != null)
                    {
                        nodeViews.Remove(node.data.id);
                        owner.RemoveNodeData(node.data.id);
                    }
                }
            }

            if (change.edgesToCreate != null)
            {
                for (int i = 0; i < change.edgesToCreate.Count; i++)
                {
                    Edge edge = change.edgesToCreate[i];
                    VATAnimatorEdgeData edgeData = ToEdgeData(edge);
                    if (edgeData == null)
                    {
                        edgesToDiscard.Add(edge);
                        continue;
                    }

                    VATAnimatorNodeView outputNode = edge.output == null ? null : edge.output.node as VATAnimatorNodeView;
                    VATAnimatorNodeView inputNode = edge.input == null ? null : edge.input.node as VATAnimatorNodeView;
                    if (outputNode == null || inputNode == null)
                    {
                        edgesToDiscard.Add(edge);
                        continue;
                    }

                    if (inputNode.data.nodeType == VATAnimatorNodeType.Transition &&
                        edgeData.inputPortName == VATAnimatorGraphAsset.NewConditionPortName)
                    {
                        if (!owner.TryCreateTransitionConditionEdge(edgeData))
                        {
                            edgesToDiscard.Add(edge);
                            continue;
                        }
                        rebuildRequired = true;
                    }
                    else if (outputNode.data.nodeType == VATAnimatorNodeType.BlendTree)
                    {
                        bool wasNewCase = edgeData.outputPortName == VATAnimatorGraphAsset.NewBlendCasePortName;
                        if (!owner.TryBindBlendTreeCaseEdge(edgeData))
                        {
                            edgesToDiscard.Add(edge);
                            continue;
                        }
                        rebuildRequired = rebuildRequired || wasNewCase;
                    }
                    else if (outputNode.data.nodeType == VATAnimatorNodeType.Default)
                    {
                        if (!owner.HandleDefaultEdge(edgeData))
                        {
                            edgesToDiscard.Add(edge);
                            continue;
                        }
                    }

                    if (!owner.GraphAsset.CanAddEdge(edgeData) || !owner.AddEdgeData(edgeData))
                    {
                        edgesToDiscard.Add(edge);
                        continue;
                    }

                    if (outputNode.data.nodeType == VATAnimatorNodeType.Parameter)
                    {
                        owner.HandleParameterEdge(outputNode.data.parameterId,
                            inputNode.data.id,
                            edgeData.inputPortName);
                    }
                }
            }

            for (int i = 0; i < edgesToDiscard.Count; i++)
            {
                change.edgesToCreate.Remove(edgesToDiscard[i]);
            }

            owner.MarkGraphChanged();
            if (rebuildRequired) owner.ScheduleGraphRebuild();
            return change;
        }

        private static VATAnimatorEdgeData ToEdgeData(Edge edge)
        {
            VATAnimatorNodeView outputNode = edge == null || edge.output == null
                ? null
                : edge.output.node as VATAnimatorNodeView;
            VATAnimatorNodeView inputNode = edge == null || edge.input == null
                ? null
                : edge.input.node as VATAnimatorNodeView;
            if (outputNode == null || inputNode == null) return null;

            return new VATAnimatorEdgeData
            {
                outputNodeId = outputNode.data.id,
                outputPortName = edge.output.portName,
                inputNodeId = inputNode.data.id,
                inputPortName = edge.input.portName
            };
        }

        private void BuildContextMenu(ContextualMenuPopulateEvent evt)
        {
            Vector2 menuPosition = evt.localMousePosition;
            bool hasParameter = false;
            if (owner.GraphAsset != null && owner.GraphAsset.parameters != null)
            {
                for (int i = 0; i < owner.GraphAsset.parameters.Count; i++)
                {
                    VATAnimatorParameterData parameter = owner.GraphAsset.parameters[i];
                    if (parameter == null) continue;
                    hasParameter = true;
                    int parameterId = parameter.id;
                    evt.menu.AppendAction(
                        "Parameter/" + parameter.parameterName + " [" + parameter.id + "]",
                        action => owner.AddParameterReferenceNode(parameterId, menuPosition));
                }
            }

            if (!hasParameter)
            {
                evt.menu.AppendAction(
                    "Parameter/<Create in Blackboard>",
                    action => { },
                    DropdownMenuAction.Status.Disabled);
            }
            evt.menu.AppendAction("Add Transition", action => owner.AddTransitionNode());
            evt.menu.AppendAction("Add Blend Tree", action => owner.AddBlendTreeNode());
            evt.menu.AppendAction("Add Default", action => owner.AddDefaultNode());
            evt.menu.AppendSeparator();

            if (owner.GraphAsset == null || owner.GraphAsset.clips == null || owner.GraphAsset.clips.Count == 0)
            {
                evt.menu.AppendAction(
                    "Add Clip Node/<Sync clips first>",
                    action => { },
                    DropdownMenuAction.Status.Disabled);
            }
            else
            {
                for (int i = 0; i < owner.GraphAsset.clips.Count; i++)
                {
                    VATAnimatorClipData clip = owner.GraphAsset.clips[i];
                    if (clip == null) continue;

                    string clipKey = clip.clipKey;
                    string menuLabel = "Add Clip Node/" + clip.clipName + " [" + clip.stateHash + "]";
                    evt.menu.AppendAction(
                        menuLabel,
                        action => owner.AddClipNode(clipKey, menuPosition));
                }
            }

            evt.menu.AppendSeparator();
            evt.menu.AppendAction("Sync Clip Nodes", action => owner.SyncClips());
        }

        private bool IsNodeContext(VisualElement target)
        {
            VisualElement current = target;
            while (current != null && current != this)
            {
                if (current is Node || current is Port || current is Edge) return true;
                current = current.parent;
            }
            return false;
        }
    }
}
