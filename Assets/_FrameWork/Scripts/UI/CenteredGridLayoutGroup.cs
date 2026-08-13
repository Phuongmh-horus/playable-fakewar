using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FrameWork.UI
{
    [AddComponentMenu("Layout/Centered Grid Layout Group")]
    public class CenteredGridLayoutGroup : GridLayoutGroup
    {
        public enum HorizontalAlignment
        {
            Left = 0,
            Center = 1,
            Right = 2
        }

        [Header("Centered Grid Settings")]
        [SerializeField] private bool centerGrid = true;
        [SerializeField] private HorizontalAlignment alignment = HorizontalAlignment.Center;
        [SerializeField] private bool autoControlChildSize = false;

        [SerializeField] private bool centerLastRow = true;
        [SerializeField] private bool centerLastColumn = true;
        [HideInInspector] [SerializeField] private bool centerSingleItem = true;

        private List<RectTransform> m_Children = new List<RectTransform>();

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            UpdateChildren();
        }

        public override void SetLayoutHorizontal()
        {
            SetCellsAlongAxis(0);
        }

        public override void SetLayoutVertical()
        {
            SetCellsAlongAxis(1);
        }

        private void UpdateChildren()
        {
            m_Children.Clear();
            for (int i = 0; i < rectTransform.childCount; i++)
            {
                RectTransform child = rectTransform.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeInHierarchy)
                    continue;

                LayoutElement layoutElement = child.GetComponent<LayoutElement>();
                if (layoutElement != null && layoutElement.ignoreLayout)
                    continue;

                m_Children.Add(child);
            }
        }

        private void SetCellsAlongAxis(int axis)
        {
            if (m_Children.Count == 0)
                return;

            float availableWidth = Mathf.Max(0, rectTransform.rect.width - padding.horizontal);
            float availableHeight = Mathf.Max(0, rectTransform.rect.height - padding.vertical);

            int cellCountX = 1;
            int cellCountY = 1;
            float scale = 1f;

            if (m_Constraint == Constraint.FixedColumnCount)
            {
                cellCountX = m_ConstraintCount;
                cellCountY = Mathf.CeilToInt((float)m_Children.Count / cellCountX);
            }
            else if (m_Constraint == Constraint.FixedRowCount)
            {
                cellCountY = m_ConstraintCount;
                cellCountX = Mathf.CeilToInt((float)m_Children.Count / cellCountY);
            }
            else // Flexible
            {
                if (autoControlChildSize)
                {
                    float bestScale = 0;
                    int bestC = 1;
                    int bestR = 1;

                    // Duyệt cấu hình tối ưu tôn trọng StartAxis
                    for (int i = 1; i <= m_Children.Count; i++)
                    {
                        int c, r;
                        if (startAxis == Axis.Horizontal)
                        {
                            c = i; // Số cột
                            r = Mathf.CeilToInt((float)m_Children.Count / c);
                        }
                        else
                        {
                            r = i; // Số hàng
                            c = Mathf.CeilToInt((float)m_Children.Count / r);
                        }

                        float reqW = c * cellSize.x + (c - 1) * spacing.x;
                        float reqH = r * cellSize.y + (r - 1) * spacing.y;

                        float sx = (reqW > availableWidth && reqW > 0) ? availableWidth / reqW : 1f;
                        float sy = (reqH > availableHeight && reqH > 0) ? availableHeight / reqH : 1f;
                        float curScale = Mathf.Min(sx, sy, 1f);

                        if (curScale > bestScale + 0.001f)
                        {
                            bestScale = curScale;
                            bestC = c;
                            bestR = r;
                        }
                        else if (Mathf.Abs(curScale - bestScale) < 0.001f)
                        {
                            // Tie-breaker: Ưu tiên lấp đầy trục chính (fill) mà không làm giảm scale
                            if (startAxis == Axis.Horizontal)
                            {
                                // Ưu tiên số cột lớn nhất mà vẫn fit chiều rộng ban đầu (không cần thu nhỏ thêm)
                                if (c > bestC && (c * cellSize.x + (c - 1) * spacing.x) <= availableWidth + 0.1f)
                                {
                                    bestC = c;
                                    bestR = r;
                                }
                            }
                            else
                            {
                                // Ưu tiên số hàng lớn nhất mà vẫn fit chiều cao ban đầu
                                if (r > bestR && (r * cellSize.y + (r - 1) * spacing.y) <= availableHeight + 0.1f)
                                {
                                    bestR = r;
                                    bestC = c;
                                }
                            }
                        }
                    }
                    cellCountX = bestC;
                    cellCountY = bestR;
                    scale = bestScale;
                }
                else
                {
                    // Logic Flexible mặc định: Sử dụng tối đa dung lượng có thể chứa thay vì giới hạn theo số item
                    if (startAxis == Axis.Horizontal)
                    {
                        cellCountX = Mathf.Max(1, Mathf.FloorToInt((availableWidth + spacing.x + 0.001f) / (cellSize.x + spacing.x)));
                        cellCountY = Mathf.CeilToInt((float)m_Children.Count / cellCountX);
                    }
                    else
                    {
                        cellCountY = Mathf.Max(1, Mathf.FloorToInt((availableHeight + spacing.y + 0.001f) / (cellSize.y + spacing.y)));
                        cellCountX = Mathf.CeilToInt((float)m_Children.Count / cellCountY);
                    }
                }
            }

            // Tính scale cho chế độ Constraint Fixed
            if (autoControlChildSize && m_Constraint != Constraint.Flexible)
            {
                float reqW = cellCountX * cellSize.x + (cellCountX - 1) * spacing.x;
                float reqH = cellCountY * cellSize.y + (cellCountY - 1) * spacing.y;
                float sx = (reqW > availableWidth && reqW > 0) ? availableWidth / reqW : 1f;
                float sy = (reqH > availableHeight && reqH > 0) ? availableHeight / reqH : 1f;
                scale = Mathf.Min(sx, sy, 1f);
            }

            scale = Mathf.Clamp(scale, 0.01f, 1f);
            Vector2 actualCellSize = cellSize * scale;
            
            float totalGridWidth = cellCountX * actualCellSize.x + (cellCountX - 1) * spacing.x;
            float totalGridHeight = cellCountY * actualCellSize.y + (cellCountY - 1) * spacing.y;

            float baseOffsetX = 0f;
            float baseOffsetY = 0f;

            // Khi centerGrid = true, ta luôn căn giữa toàn bộ "Khung Grid" vào chính giữa container
            if (centerGrid)
            {
                baseOffsetX = (availableWidth - totalGridWidth) * 0.5f;
                baseOffsetY = (availableHeight - totalGridHeight) * 0.5f;
            }
            else
            {
                // Logic Alignment mặc định của Unity dựa trên childAlignment
                if (childAlignment == TextAnchor.UpperCenter || childAlignment == TextAnchor.MiddleCenter || childAlignment == TextAnchor.LowerCenter)
                    baseOffsetX = (availableWidth - totalGridWidth) * 0.5f;
                else if (childAlignment == TextAnchor.UpperRight || childAlignment == TextAnchor.MiddleRight || childAlignment == TextAnchor.LowerRight)
                    baseOffsetX = availableWidth - totalGridWidth;

                if (childAlignment == TextAnchor.MiddleLeft || childAlignment == TextAnchor.MiddleCenter || childAlignment == TextAnchor.MiddleRight)
                    baseOffsetY = (availableHeight - totalGridHeight) * 0.5f;
                else if (childAlignment == TextAnchor.LowerLeft || childAlignment == TextAnchor.LowerCenter || childAlignment == TextAnchor.LowerRight)
                    baseOffsetY = availableHeight - totalGridHeight;
            }

            for (int i = 0; i < m_Children.Count; i++)
            {
                int posX, posY;
                if (startAxis == Axis.Horizontal) { posX = i % cellCountX; posY = i / cellCountX; }
                else { posX = i / cellCountY; posY = i % cellCountY; }

                float rowOffsetX = 0f, colOffsetY = 0f;

                if (centerGrid)
                {
                    if (startAxis == Axis.Horizontal)
                    {
                        // Căn chỉnh nội bộ từng hàng bên trong "Khung Grid" đã căn giữa
                        int itemsInRow = Mathf.Min(cellCountX, m_Children.Count - posY * cellCountX);
                        float currentRowWidth = itemsInRow * actualCellSize.x + (itemsInRow - 1) * spacing.x;
                        
                        if (alignment == HorizontalAlignment.Center)
                            rowOffsetX = (totalGridWidth - currentRowWidth) * 0.5f;
                        else if (alignment == HorizontalAlignment.Right)
                            rowOffsetX = totalGridWidth - currentRowWidth;
                        // Mặc định Left thì rowOffsetX = 0
                    }
                    else
                    {
                        // Căn chỉnh nội bộ từng cột bên trong "Khung Grid" đã căn giữa
                        int itemsInCol = Mathf.Min(cellCountY, m_Children.Count - posX * cellCountY);
                        float currentColHeight = itemsInCol * actualCellSize.y + (itemsInCol - 1) * spacing.y;

                        // Vì yêu cầu chỉ cần Left/Center/Right (ngang), ta có thể giữ logic Vertical mặc định là Center hoặc Top
                        // Ở đây ta mặc định là dồn lên trên (Top) nếu không có yêu cầu cụ thể về dọc
                        colOffsetY = 0; 
                    }
                }
                else
                {
                    // Logic cũ cho centerLastRow/Column đơn lẻ
                    if (startAxis == Axis.Horizontal)
                    {
                        int itemsInRow = Mathf.Min(cellCountX, m_Children.Count - posY * cellCountX);
                        if (centerLastRow && posY == cellCountY - 1 && itemsInRow < cellCountX)
                            rowOffsetX = (totalGridWidth - (itemsInRow * actualCellSize.x + (itemsInRow - 1) * spacing.x)) * 0.5f;
                    }
                    else
                    {
                        int itemsInCol = Mathf.Min(cellCountY, m_Children.Count - posX * cellCountY);
                        if (centerLastColumn && posX == cellCountX - 1 && itemsInCol < cellCountY)
                            colOffsetY = (totalGridHeight - (itemsInCol * actualCellSize.y + (itemsInCol - 1) * spacing.y)) * 0.5f;
                    }
                }

                if (axis == 0)
                    SetChildAlongAxis(m_Children[i], 0, padding.left + baseOffsetX + rowOffsetX + (actualCellSize.x + spacing.x) * posX, actualCellSize.x);
                else
                    SetChildAlongAxis(m_Children[i], 1, padding.top + baseOffsetY + colOffsetY + (actualCellSize.y + spacing.y) * posY, actualCellSize.y);
            }
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            SetDirty();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            SetDirty();
        }

        [CustomEditor(typeof(CenteredGridLayoutGroup))]
        [CanEditMultipleObjects]
        public class CenteredGridLayoutGroupEditor : Editor
        {
            SerializedProperty m_Padding, m_Spacing, m_CellSize, m_StartCorner, m_StartAxis, m_ChildAlignment, m_Constraint, m_ConstraintCount;
            SerializedProperty m_CenterGrid, m_Alignment, m_AutoControlChildSize;

            protected void OnEnable() {
                m_Padding = serializedObject.FindProperty("m_Padding");
                m_Spacing = serializedObject.FindProperty("m_Spacing");
                m_CellSize = serializedObject.FindProperty("m_CellSize");
                m_StartCorner = serializedObject.FindProperty("m_StartCorner");
                m_StartAxis = serializedObject.FindProperty("m_StartAxis");
                m_ChildAlignment = serializedObject.FindProperty("m_ChildAlignment");
                m_Constraint = serializedObject.FindProperty("m_Constraint");
                m_ConstraintCount = serializedObject.FindProperty("m_ConstraintCount");

                m_CenterGrid = serializedObject.FindProperty("centerGrid");
                m_Alignment = serializedObject.FindProperty("alignment");
                m_AutoControlChildSize = serializedObject.FindProperty("autoControlChildSize");
            }

            public override void OnInspectorGUI() {
                serializedObject.Update();
                
                // Vẽ các thuộc tính cơ bản của GridLayoutGroup
                EditorGUILayout.PropertyField(m_Padding);
                EditorGUILayout.PropertyField(m_Spacing);
                EditorGUILayout.PropertyField(m_CellSize);
                EditorGUILayout.PropertyField(m_StartCorner);
                EditorGUILayout.PropertyField(m_StartAxis);
                EditorGUILayout.PropertyField(m_ChildAlignment);
                EditorGUILayout.PropertyField(m_Constraint);
                
                // Ẩn Constraint Count nếu là Flexible
                if (m_Constraint.enumValueIndex != (int)Constraint.Flexible)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(m_ConstraintCount);
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Centered Grid Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(m_CenterGrid);
                
                if (m_CenterGrid.boolValue)
                {
                    // Center last row đã được thay thế bằng Alignment cho tất cả các hàng
                    EditorGUILayout.PropertyField(m_Alignment, new GUIContent("Alignment"));
                }

                EditorGUILayout.PropertyField(m_AutoControlChildSize);

                serializedObject.ApplyModifiedProperties();
            }
        }
#endif
    }
}
