using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using OptimizedFeature.Scripts;

namespace OptimizedFeature.Editor.VATAnimator
{
    /// <summary>
    /// The single authoring surface for VAT Animator parameters. Parameter graph nodes only
    /// reference entries created here; they never create or own parameter data.
    /// </summary>
    internal sealed class VATAnimatorBlackboardView : VisualElement
    {
        private readonly VATAnimatorGraphWindow owner;
        private readonly VisualElement parameterList;

        public VATAnimatorBlackboardView(VATAnimatorGraphWindow graphWindow)
        {
            owner = graphWindow;
            style.width = 290f;
            style.minWidth = 260f;
            style.flexShrink = 0f;
            style.backgroundColor = new Color(0.075f, 0.085f, 0.10f, 1f);
            style.borderRightWidth = 1f;
            style.borderRightColor = new Color(0.20f, 0.23f, 0.28f, 1f);

            VisualElement header = new VisualElement();
            header.style.paddingLeft = 8f;
            header.style.paddingRight = 8f;
            header.style.paddingTop = 8f;
            header.style.paddingBottom = 6f;

            VisualElement titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            Label title = new Label("Blackboard");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1f;
            titleRow.Add(title);

            Button addButton = new Button(ShowAddMenu)
            {
                text = "+ Parameter"
            };
            titleRow.Add(addButton);
            header.Add(titleRow);

            Label hint = new Label("Parameters are created here and referenced by graph nodes.");
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.color = new Color(0.62f, 0.68f, 0.75f);
            hint.style.marginTop = 4f;
            header.Add(hint);
            Add(header);

            parameterList = new ScrollView();
            parameterList.style.flexGrow = 1f;
            Add(parameterList);
        }

        public void ShowAddMenu()
        {
            GenericMenu menu = new GenericMenu();
            AddMenuItem(menu, VATAnimatorParameterType.Trigger);
            AddMenuItem(menu, VATAnimatorParameterType.Bool);
            AddMenuItem(menu, VATAnimatorParameterType.Float);
            AddMenuItem(menu, VATAnimatorParameterType.Vector2);
            menu.ShowAsContext();
        }

        private void AddMenuItem(GenericMenu menu, VATAnimatorParameterType type)
        {
            menu.AddItem(
                new GUIContent(type.ToString()),
                false,
                () => owner.CreateParameterFromBlackboard(type));
        }

        public void Refresh()
        {
            parameterList.Clear();

            if (owner.GraphAsset == null || owner.GraphAsset.parameters == null ||
                owner.GraphAsset.parameters.Count == 0)
            {
                Label empty = new Label("No parameters created.");
                empty.style.paddingLeft = 8f;
                empty.style.paddingTop = 8f;
                empty.style.color = new Color(0.62f, 0.68f, 0.75f);
                parameterList.Add(empty);
                return;
            }

            for (int i = 0; i < owner.GraphAsset.parameters.Count; i++)
            {
                VATAnimatorParameterData parameter = owner.GraphAsset.parameters[i];
                if (parameter != null) AddParameterRow(parameter);
            }
        }

        private void AddParameterRow(VATAnimatorParameterData parameter)
        {
            VisualElement card = new VisualElement();
            card.style.marginLeft = 6f;
            card.style.marginRight = 6f;
            card.style.marginTop = 5f;
            card.style.paddingLeft = 6f;
            card.style.paddingRight = 6f;
            card.style.paddingTop = 5f;
            card.style.paddingBottom = 5f;
            card.style.backgroundColor = new Color(0.11f, 0.125f, 0.15f, 1f);
            card.style.borderBottomWidth = 1f;
            card.style.borderBottomColor = new Color(0.22f, 0.25f, 0.30f, 1f);

            VisualElement titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            Label idLabel = new Label("#" + parameter.id);
            idLabel.style.width = 28f;
            idLabel.style.color = new Color(0.55f, 0.60f, 0.68f);
            titleRow.Add(idLabel);

            TextField nameField = new TextField
            {
                value = parameter.parameterName,
                isDelayed = true
            };
            nameField.style.flexGrow = 1f;
            nameField.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == parameter.parameterName) return;
                owner.RenameParameterFromBlackboard(parameter.id, evt.newValue);
            });
            titleRow.Add(nameField);

            Button removeButton = new Button(() => owner.RemoveParameterFromBlackboard(parameter.id))
            {
                text = "×"
            };
            removeButton.style.width = 24f;
            titleRow.Add(removeButton);
            card.Add(titleRow);

            EnumField typeField = new EnumField("Type", parameter.type);
            typeField.RegisterValueChangedCallback(evt =>
            {
                VATAnimatorParameterType nextType = (VATAnimatorParameterType)evt.newValue;
                if (nextType != parameter.type) owner.ChangeParameterTypeFromBlackboard(parameter.id, nextType);
            });
            card.Add(typeField);

            switch (parameter.type)
            {
                case VATAnimatorParameterType.Bool:
                    Toggle boolField = new Toggle("Default") { value = parameter.defaultBool };
                    boolField.RegisterValueChangedCallback(evt =>
                        owner.ChangeParameterDefault(parameter.id, evt.newValue, parameter.defaultFloat, parameter.defaultVector2));
                    card.Add(boolField);
                    break;
                case VATAnimatorParameterType.Float:
                    FloatField floatField = new FloatField("Default") { value = parameter.defaultFloat };
                    floatField.RegisterValueChangedCallback(evt =>
                        owner.ChangeParameterDefault(parameter.id, parameter.defaultBool, evt.newValue, parameter.defaultVector2));
                    card.Add(floatField);
                    break;
                case VATAnimatorParameterType.Vector2:
                    Vector2Field vectorField = new Vector2Field("Default") { value = parameter.defaultVector2 };
                    vectorField.RegisterValueChangedCallback(evt =>
                        owner.ChangeParameterDefault(parameter.id, parameter.defaultBool, parameter.defaultFloat, evt.newValue));
                    card.Add(vectorField);
                    break;
            }

            parameterList.Add(card);
        }
    }
}
