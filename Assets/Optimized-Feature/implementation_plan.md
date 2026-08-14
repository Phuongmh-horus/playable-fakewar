# VAT StateMachine + Bake Tool Redesign — Implementation Plan

## Cập Nhật Quan Trọng
- **Đổi tên**: `VATStateMachineData` -> `VATStateMachineDataSO` (để phân biệt cấu trúc theo tên và đồng bộ với `VATAssetDataSO`).

---

## Phân Tích Cấu Trúc File Hiện Tại & Đề Xuất Tách File

```
CẤU TRÚC HIỆN TẠI (Monolithic Bake Tool & Simple Runtime):
├── VATBakeToolWindow.cs (851 lines - GUI + logic bake mesh/texture/materials)
├── VATAssetDataSO.cs (metadata baked clips)
└── VAT_RenderComponent.cs (chỉ có Play/CrossFade thủ công)

CẤU TRÚC MỚI SAU KHI PHÁT TRIỂN STATEMACHINE:
├── VATStateMachineDataSO.cs [NEW] (ScriptableObject định nghĩa States/Transitions/Parameters)
├── VATStateMachine.cs [NEW] (Bộ điều khiển runtime zero-alloc, class thuần)
├── VAT_RenderComponent.cs [MODIFY] (Tích hợp StateMachine & API SetBool/SetTrigger)
├── VATAssetDataSO.cs [MODIFY] (Liên kết tham chiếu đến VATStateMachineDataSO)
└── Editor/
    ├── VATBakeToolWindow.cs [MODIFY] (Chỉ hiển thị GUI Bake Tool đơn giản hóa)
    ├── VATBaker.cs [NEW] (Tách toàn bộ logic baking mesh/texture/material từ Window ra đây)
    ├── VATStateMachineImporter.cs [NEW] (Đọc Unity AnimatorController chuyển sang VATStateMachineDataSO)
    └── VATStateMachineDataSOEditor.cs [NEW] (Custom Inspector cho VATStateMachineDataSO)
```

---

## Cấu Trúc Chi Tiết Các File Phát Triển

### 1. Dữ Liệu State Machine (ScriptableObject)
#### [NEW] [`VATStateMachineDataSO.cs`](file:///C:/UnityProjects/PLA_RPG_Game/Assets/Optimized-Feature/Scripts/VATStateMachineDataSO.cs)

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace OptimizedFeature.Scripts
{
    public enum VATParamType { Bool, Int, Float, Trigger }
    public enum VATConditionMode { Equals, NotEquals, Greater, Less, IsTrue, IsFalse }

    [Serializable]
    public class VATParameter
    {
        public string Name;
        public VATParamType Type;
        public float DefaultValue;
    }

    [Serializable]
    public class VATCondition
    {
        public string ParameterName;
        public VATConditionMode Mode;
        public float Threshold;
    }

    [Serializable]
    public class VATTransition
    {
        public string FromState; // Rỗng "" đại diện cho Any State
        public string ToState;
        public float Duration = 0.15f;
        public bool HasExitTime;
        public float ExitTime = 1.0f; // Normalized (0-1)
        public List<VATCondition> Conditions = new List<VATCondition>();
    }

    [Serializable]
    public class VATAnimState
    {
        public string StateName;
        public AnimationClip SourceClip; // Editor only, dùng khi bake
        public float SpeedMultiplier = 1.0f;
        public bool IsLooping = true;
    }

    [CreateAssetMenu(fileName = "VATStateMachineData", menuName = "VAT/VAT State Machine Data SO")]
    public class VATStateMachineDataSO : ScriptableObject
    {
        public string DefaultStateName = "Idle";
        public List<VATParameter> Parameters = new List<VATParameter>();
        public List<VATAnimState> States = new List<VATAnimState>();
        public List<VATTransition> Transitions = new List<VATTransition>();

        public List<AnimationClip> GetRequiredClips()
        {
            List<AnimationClip> clips = new List<AnimationClip>();
            for (int i = 0; i < States.Count; i++)
            {
                if (States[i].SourceClip != null && !clips.Contains(States[i].SourceClip))
                {
                    clips.Add(States[i].SourceClip);
                }
            }
            return clips;
        }
    }
}
```

---

### 2. Runtime State Machine Engine (Zero-Alloc Class)
#### [NEW] [`VATStateMachine.cs`](file:///C:/UnityProjects/PLA_RPG_Game/Assets/Optimized-Feature/Scripts/VATStateMachine.cs)

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace OptimizedFeature.Scripts
{
    public class VATStateMachine
    {
        private VATStateMachineDataSO _data;
        private VATAssetDataSO _assetData;

        // Pre-allocated runtime values
        private float[] _paramValues;
        private bool[] _triggerConsumed;
        private VATClipInfo[] _stateClipCache;

        private int _currentStateIndex = -1;
        private float _stateTime;

        private int _targetStateIndex = -1;
        private float _targetStateTime;
        private float _transitionTimer;
        private float _transitionDuration;
        private bool _isTransitioning;

        public VATStateMachine(VATStateMachineDataSO data, VATAssetDataSO assetData)
        {
            _data = data;
            _assetData = assetData;
            
            _paramValues = new float[data.Parameters.Count];
            _triggerConsumed = new bool[data.Parameters.Count];
            _stateClipCache = new VATClipInfo[data.States.Count];

            for (int i = 0; i < data.Parameters.Count; i++)
            {
                _paramValues[i] = data.Parameters[i].DefaultValue;
            }

            for (int i = 0; i < data.States.Count; i++)
            {
                _stateClipCache[i] = assetData.GetClip(data.States[i].StateName);
            }

            _currentStateIndex = FindStateIndex(data.DefaultStateName);
            _stateTime = 0f;
        }

        public void SetBool(string name, bool value)
        {
            int idx = FindParamIndex(name);
            if (idx >= 0) _paramValues[idx] = value ? 1f : 0f;
        }

        public void SetTrigger(string name)
        {
            int idx = FindParamIndex(name);
            if (idx >= 0) { _paramValues[idx] = 1f; _triggerConsumed[idx] = false; }
        }

        public void SetFloat(string name, float value)
        {
            int idx = FindParamIndex(name);
            if (idx >= 0) _paramValues[idx] = value;
        }

        public void SetInteger(string name, int value)
        {
            int idx = FindParamIndex(name);
            if (idx >= 0) _paramValues[idx] = value;
        }

        public void Update(float deltaTime, float speed)
        {
            if (_currentStateIndex < 0) return;

            float speedMul = _data.States[_currentStateIndex].SpeedMultiplier;
            float scaledDelta = deltaTime * speed * speedMul;
            _stateTime += scaledDelta;

            if (!_isTransitioning)
            {
                EvaluateTransitions();
            }

            if (_isTransitioning)
            {
                _targetStateTime += scaledDelta;
                _transitionTimer += scaledDelta;

                if (_transitionTimer >= _transitionDuration)
                {
                    _currentStateIndex = _targetStateIndex;
                    _stateTime = _targetStateTime;
                    _targetStateIndex = -1;
                    _isTransitioning = false;
                }
            }
        }

        public int GetCurrentFrame()
        {
            if (_currentStateIndex < 0 || _stateClipCache[_currentStateIndex] == null) return 0;
            return CalculateFrame(_stateClipCache[_currentStateIndex], _stateTime);
        }

        public int GetTargetFrame()
        {
            if (!_isTransitioning || _targetStateIndex < 0 || _stateClipCache[_targetStateIndex] == null) return GetCurrentFrame();
            return CalculateFrame(_stateClipCache[_targetStateIndex], _targetStateTime);
        }

        public float GetBlendWeight()
        {
            if (!_isTransitioning) return 0f;
            return Mathf.Clamp01(_transitionTimer / _transitionDuration);
        }

        public string GetCurrentStateName()
        {
            if (_currentStateIndex >= 0 && _currentStateIndex < _data.States.Count)
                return _data.States[_currentStateIndex].StateName;
            return string.Empty;
        }

        private int CalculateFrame(VATClipInfo clip, float time)
        {
            float totalTime = clip.TotalFrames / clip.FrameRate;
            float timeInClip = clip.IsLooping ? Mathf.Repeat(time, totalTime) : Mathf.Clamp(time, 0f, totalTime);
            int frameOffset = Mathf.FloorToInt(timeInClip * clip.FrameRate) % clip.TotalFrames;
            return clip.StartFrame + frameOffset;
        }

        private void EvaluateTransitions()
        {
            int transCount = _data.Transitions.Count;
            string currentName = _data.States[_currentStateIndex].StateName;

            for (int i = 0; i < transCount; i++)
            {
                var t = _data.Transitions[i];
                bool fromMatch = t.FromState == currentName || t.FromState == "";
                if (!fromMatch) continue;

                if (t.HasExitTime)
                {
                    VATClipInfo clip = _stateClipCache[_currentStateIndex];
                    float duration = clip.TotalFrames / clip.FrameRate;
                    float normalized = _stateTime / (duration > 0 ? duration : 1f);
                    if (normalized < t.ExitTime) continue;
                }

                if (!EvaluateConditions(t.Conditions)) continue;

                StartTransition(t);
                return;
            }
        }

        private bool EvaluateConditions(List<VATCondition> conditions)
        {
            for (int i = 0; i < conditions.Count; i++)
            {
                var cond = conditions[i];
                int pIdx = FindParamIndex(cond.ParameterName);
                if (pIdx < 0) return false;

                float val = _paramValues[pIdx];
                bool met = false;
                switch (cond.Mode)
                {
                    case VATConditionMode.Equals: met = Mathf.Approximately(val, cond.Threshold); break;
                    case VATConditionMode.NotEquals: met = !Mathf.Approximately(val, cond.Threshold); break;
                    case VATConditionMode.Greater: met = val > cond.Threshold; break;
                    case VATConditionMode.Less: met = val < cond.Threshold; break;
                    case VATConditionMode.IsTrue: met = val > 0.5f; break;
                    case VATConditionMode.IsFalse: met = val < 0.5f; break;
                }

                if (!met) return false;
            }

            // Consume triggers
            for (int i = 0; i < conditions.Count; i++)
            {
                var cond = conditions[i];
                int pIdx = FindParamIndex(cond.ParameterName);
                if (pIdx >= 0 && _data.Parameters[pIdx].Type == VATParamType.Trigger)
                {
                    _paramValues[pIdx] = 0f;
                    _triggerConsumed[pIdx] = true;
                }
            }

            return true;
        }

        private void StartTransition(VATTransition t)
        {
            int targetIdx = FindStateIndex(t.ToState);
            if (targetIdx < 0) return;

            _targetStateIndex = targetIdx;
            _targetStateTime = 0f;
            _transitionDuration = Mathf.Max(0.01f, t.Duration);
            _transitionTimer = 0f;
            _isTransitioning = true;
        }

        private int FindParamIndex(string name)
        {
            for (int i = 0; i < _data.Parameters.Count; i++)
                if (_data.Parameters[i].Name == name) return i;
            return -1;
        }

        private int FindStateIndex(string name)
        {
            for (int i = 0; i < _data.States.Count; i++)
                if (_data.States[i].StateName == name) return i;
            return -1;
        }
    }
}
```

---

### 3. Tích hợp ScriptableObject tham chiếu
#### [MODIFY] [`VATAssetDataSO.cs`](file:///C:/UnityProjects/PLA_RPG_Game/Assets/Optimized-Feature/Scripts/VATAssetDataSO.cs)

```diff
     public class VATAssetDataSO : ScriptableObject
     {
         public Texture2D VATTexture;
         public Mesh BakedStaticMesh;
         public Vector3 BoundingMin;
         public Vector3 BoundingMax;
         public int TotalVertices;
         public int TotalFrames;
 
         public List<VATClipInfo> Clips = new List<VATClipInfo>();
         public List<VATSocketTransformData> Sockets = new List<VATSocketTransformData>();
         public List<Material> BakedMaterials = new List<Material>();
+
+        /// <summary>
+        /// State Machine config. Null = Manual Play/Crossfade mode.
+        /// </summary>
+        public VATStateMachineDataSO StateMachine;
```

---

### 4. Tích hợp Component render chính
#### [MODIFY] [`VAT_RenderComponent.cs`](file:///C:/UnityProjects/PLA_RPG_Game/Assets/Optimized-Feature/Scripts/VAT_RenderComponent.cs)

```diff
         // --- Visibility & Attachments ---
         private List<VAT_ObjectMesh> _attachedObjectMeshes = new List<VAT_ObjectMesh>();
         private bool _isVisible = true;
         private readonly List<Renderer> _childRenderers = new List<Renderer>();
+
+        // --- State Machine ---
+        private VATStateMachine _stateMachine;
 
         // --- Public API ---
         public VATAssetDataSO VatAssetData => _vatAssetData;
         public MeshRenderer Renderer => _meshRenderer;
         public float Speed { get => _speed; set => _speed = value; }
-        public string CurrentStateName => _currentState != null ? _currentState.StateName : string.Empty;
+        public string CurrentStateName => _stateMachine != null ? _stateMachine.GetCurrentStateName() : (_currentState != null ? _currentState.StateName : string.Empty);
         public bool IsBlending => _isBlending;
         public bool IsVisible { get => _isVisible; set => _isVisible = value; }
+
+        // --- Animator-like API ---
+        public void SetBool(string name, bool value) => _stateMachine?.SetBool(name, value);
+        public void SetTrigger(string name) => _stateMachine?.SetTrigger(name);
+        public void SetFloat(string name, float value) => _stateMachine?.SetFloat(name, value);
+        public void SetInteger(string name, int value) => _stateMachine?.SetInteger(name, value);
 
         private void Awake()
         {
             if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();
             if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();
 
             InitializeShaderPropertyIds();
             ApplyVATAssetData();
 
             // Pre-allocate 2 state instances to avoid GC allocations during Play/CrossFade
             _stateA = new VATAnimStateData(string.Empty, 0, 0);
             _stateB = new VATAnimStateData(string.Empty, 0, 0);
 
             GetComponentsInChildren(true, _attachedObjectMeshes);
+
+            // Initialize State Machine if data SO reference exists
+            if (_vatAssetData != null && _vatAssetData.StateMachine != null)
+            {
+                _stateMachine = new VATStateMachine(_vatAssetData.StateMachine, _vatAssetData);
+            }
```
Và cập nhật hàm update:
```diff
         public void ManualUpdate(float deltaTime, bool updateRenderer = true)
         {
+            if (_stateMachine != null)
+            {
+                _stateMachine.Update(deltaTime, _speed);
+                if (updateRenderer)
+                {
+                    UpdateShaderFrames(_stateMachine.GetCurrentFrame(), _stateMachine.GetTargetFrame(), _stateMachine.GetBlendWeight());
+                }
+                return;
+            }
+
             if (_currentState == null) return;
```

---

### 5. Importer từ Unity AnimatorController
#### [NEW] [`VATStateMachineImporter.cs`](file:///C:/UnityProjects/PLA_RPG_Game/Assets/Optimized-Feature/Scripts/Editor/VATStateMachineImporter.cs)

```csharp
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace OptimizedFeature.Scripts.Editor
{
    public static class VATStateMachineImporter
    {
        public static VATStateMachineDataSO Import(AnimatorController controller, string saveDirectory, int layerIndex = 0)
        {
            if (controller == null) return null;

            VATStateMachineDataSO data = ScriptableObject.CreateInstance<VATStateMachineDataSO>();
            var layer = controller.layers[layerIndex];
            var stateMachine = layer.stateMachine;

            // Import Parameters
            foreach (var param in controller.parameters)
            {
                data.Parameters.Add(new VATParameter
                {
                    Name = param.name,
                    Type = ConvertType(param.type),
                    DefaultValue = param.defaultFloat // triggers / bools work on float conversion too
                });
            }

            // Import States
            foreach (var stateWrapper in stateMachine.states)
            {
                var state = stateWrapper.state;
                var clip = state.motion as AnimationClip;
                data.States.Add(new VATAnimState
                {
                    StateName = state.name,
                    SourceClip = clip,
                    SpeedMultiplier = state.speed,
                    IsLooping = clip != null && clip.isLooping
                });
            }

            // Import Transitions from states
            foreach (var stateWrapper in stateMachine.states)
            {
                var state = stateWrapper.state;
                foreach (var trans in state.transitions)
                {
                    data.Transitions.Add(ConvertTransition(state.name, trans));
                }
            }

            // Import AnyState Transitions
            foreach (var trans in stateMachine.anyStateTransitions)
            {
                data.Transitions.Add(ConvertTransition("", trans));
            }

            if (stateMachine.defaultState != null)
            {
                data.DefaultStateName = stateMachine.defaultState.name;
            }

            // Save SO Asset file
            if (!Directory.Exists(saveDirectory))
            {
                Directory.CreateDirectory(saveDirectory);
            }
            string assetPath = Path.Combine(saveDirectory, controller.name + "_StateMachine.asset");
            AssetDatabase.CreateAsset(data, assetPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[VATStateMachineImporter] Successfully imported AnimatorController '{controller.name}' to '{assetPath}'");
            return AssetDatabase.LoadAssetAtPath<VATStateMachineDataSO>(assetPath);
        }

        private static VATParamType ConvertType(AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool: return VATParamType.Bool;
                case AnimatorControllerParameterType.Float: return VATParamType.Float;
                case AnimatorControllerParameterType.Int: return VATParamType.Int;
                case AnimatorControllerParameterType.Trigger: return VATParamType.Trigger;
            }
            return VATParamType.Bool;
        }

        private static VATTransition ConvertTransition(string fromState, AnimatorStateTransition trans)
        {
            var vatTrans = new VATTransition
            {
                FromState = fromState,
                ToState = trans.destinationState != null ? trans.destinationState.name : "",
                Duration = trans.duration,
                HasExitTime = trans.hasExitTime,
                ExitTime = trans.exitTime
            };

            foreach (var cond in trans.conditions)
            {
                vatTrans.Conditions.Add(new VATCondition
                {
                    ParameterName = cond.parameter,
                    Mode = ConvertMode(cond.mode),
                    Threshold = cond.threshold
                });
            }

            return vatTrans;
        }

        private static VATConditionMode ConvertMode(AnimatorConditionMode mode)
        {
            switch (mode)
            {
                case AnimatorConditionMode.Equals: return VATConditionMode.Equals;
                case AnimatorConditionMode.NotEqual: return VATConditionMode.NotEquals;
                case AnimatorConditionMode.Greater: return VATConditionMode.Greater;
                case AnimatorConditionMode.Less: return VATConditionMode.Less;
                case AnimatorConditionMode.If: return VATConditionMode.IsTrue;
                case AnimatorConditionMode.IfNot: return VATConditionMode.IsFalse;
            }
            return VATConditionMode.IsTrue;
        }
    }
}
```

---

### 6. Tích hợp vào VATBakeToolWindow & VATBaker
Để đơn giản hóa `VATBakeToolWindow.cs`, ta sẽ **tách toàn bộ logic tính toán nướng mesh/vật liệu/texture** ra một helper class là `VATBaker.cs`. Window chỉ thực hiện phần thu nhận input của UI.

#### [NEW] [`VATBaker.cs`](file:///C:/UnityProjects/PLA_RPG_Game/Assets/Optimized-Feature/Scripts/Editor/VATBaker.cs)

Chứa method chính thực hiện quy trình nướng:

```csharp
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OptimizedFeature.Scripts.Editor
{
    public static class VATBaker
    {
        public static void Bake(
            GameObject targetPrefab,
            List<SkinnedMeshRenderer> activeMeshes,
            List<AnimationClip> clipsToBake,
            List<string> clipStateNames,
            int sampleFrameRate,
            string savePath,
            List<Transform> selectedSocketBones,
            VATStateMachineDataSO stateMachineSO,
            VATAssetDataSO existingSO,
            bool overrideMode)
        {
            // [Thực hiện logic Bake Mesh, Texture2D, Materials và VATAssetDataSO giống như code cũ, nhưng được tối ưu hóa]
            // - Pass 1: sample lấy bounding box
            // - Pass 2: sample ghi texture pixels + socket data
            // - Save assets & apply TextureImporter
            // - Gán assetData.StateMachine = stateMachineSO
        }
    }
}
```

#### [MODIFY] [`VATBakeToolWindow.cs`](file:///C:/UnityProjects/PLA_RPG_Game/Assets/Optimized-Feature/Scripts/Editor/VATBakeToolWindow.cs)
Cập nhật Window để hiển thị trường chọn `VATStateMachineDataSO`, tự động điền list clips từ SO nếu được gán, và chuyển tiếp tham số cho `VATBaker.Bake`.

---

## Verification Plan

### Automated Tests (Editor scripts)
1. Chạy quá trình Import test với một Controller có sẵn trong project.
2. Kiểm tra log cảnh báo/lỗi biên dịch của Unity sau khi hoàn thành.

### Manual Verification
1. Gán `VATStateMachineDataSO` cho nhân vật và chọn `Tools -> VAT Setup Tester Helper` để cấu hình.
2. Chạy Runtime, gọi `SetBool("isWalking", true)` và xem nhân vật tự chuyển từ `Idle` sang `Walk`.
3. Gọi `SetTrigger("attack")` kiểm tra Any State transition sang `Attack` rồi tự động quay lại `Idle` bằng `ExitTime`.
