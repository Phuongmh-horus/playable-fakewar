using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace OptimizedFeature.Scripts.Editor
{
    public sealed class AnimationMergeExportRecord
    {
        public int layerIndex;
        public string layerName;
        public string statePath;
        public string motionPath;
        public Motion motion;
        public AnimationClip clip;
        public bool isBlendTree;
        public List<AnimationClip> blendTreeClips = new List<AnimationClip>();
    }

    public static class AnimationMergeClipUtility
    {
        public static List<AnimationMergeExportRecord> ExtractAnimatorAnimations(
            Animator animator,
            List<AnimationMergeLayerData> layers)
        {
            List<AnimationMergeExportRecord> records = new List<AnimationMergeExportRecord>();
            if (layers != null)
            {
                layers.Clear();
            }

            if (animator == null || animator.runtimeAnimatorController == null)
            {
                return records;
            }

            AnimatorController controller = animator.runtimeAnimatorController as AnimatorController;
            AnimatorOverrideController overrideController = animator.runtimeAnimatorController as AnimatorOverrideController;
            if (controller == null && overrideController != null)
            {
                controller = overrideController.runtimeAnimatorController as AnimatorController;
            }

            if (controller == null)
            {
                return records;
            }

            Dictionary<AnimationClip, AnimationClip> overrides = BuildOverrideMap(overrideController);
            for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
            {
                AnimatorControllerLayer layer = controller.layers[layerIndex];
                string layerName = string.IsNullOrEmpty(layer.name) ? "Layer " + layerIndex : layer.name;
                if (layers != null)
                {
                    layers.Add(new AnimationMergeLayerData
                    {
                        layerIndex = layerIndex,
                        layerName = layerName,
                        accentColor = GetLayerColor(layerIndex)
                    });
                }

                CollectStateMachine(
                    layer.stateMachine,
                    layerIndex,
                    layerName,
                    string.Empty,
                    string.Empty,
                    overrides,
                    records);
            }

            return records;
        }

        private static Dictionary<AnimationClip, AnimationClip> BuildOverrideMap(
            AnimatorOverrideController overrideController)
        {
            Dictionary<AnimationClip, AnimationClip> result = new Dictionary<AnimationClip, AnimationClip>();
            if (overrideController == null)
            {
                return result;
            }

            List<KeyValuePair<AnimationClip, AnimationClip>> pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            overrideController.GetOverrides(pairs);
            for (int i = 0; i < pairs.Count; i++)
            {
                KeyValuePair<AnimationClip, AnimationClip> pair = pairs[i];
                if (pair.Key != null && pair.Value != null)
                {
                    result[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        private static void CollectStateMachine(
            AnimatorStateMachine stateMachine,
            int layerIndex,
            string layerName,
            string stateMachinePath,
            string parentMotionPath,
            Dictionary<AnimationClip, AnimationClip> overrides,
            List<AnimationMergeExportRecord> records)
        {
            if (stateMachine == null)
            {
                return;
            }

            for (int i = 0; i < stateMachine.states.Length; i++)
            {
                AnimatorState state = stateMachine.states[i].state;
                if (state == null || state.motion == null)
                {
                    continue;
                }

                string statePath = string.IsNullOrEmpty(stateMachinePath)
                    ? state.name
                    : stateMachinePath + "/" + state.name;
                string motionPath = string.IsNullOrEmpty(parentMotionPath)
                    ? state.name
                    : parentMotionPath + "/" + state.name;
                CollectMotion(
                    state.motion,
                    layerIndex,
                    layerName,
                    statePath,
                    motionPath,
                    overrides,
                    records);
            }

            for (int i = 0; i < stateMachine.stateMachines.Length; i++)
            {
                ChildAnimatorStateMachine child = stateMachine.stateMachines[i];
                if (child.stateMachine == null)
                {
                    continue;
                }

                string childPath = string.IsNullOrEmpty(stateMachinePath)
                    ? child.stateMachine.name
                    : stateMachinePath + "/" + child.stateMachine.name;
                CollectStateMachine(
                    child.stateMachine,
                    layerIndex,
                    layerName,
                    childPath,
                    parentMotionPath,
                    overrides,
                    records);
            }
        }

        private static void CollectMotion(
            Motion motion,
            int layerIndex,
            string layerName,
            string statePath,
            string motionPath,
            Dictionary<AnimationClip, AnimationClip> overrides,
            List<AnimationMergeExportRecord> records)
        {
            AnimationClip clip = motion as AnimationClip;
            if (clip != null)
            {
                records.Add(new AnimationMergeExportRecord
                {
                    layerIndex = layerIndex,
                    layerName = layerName,
                    statePath = statePath,
                    motionPath = motionPath,
                    motion = motion,
                    clip = ResolveOverride(clip, overrides),
                    isBlendTree = false
                });
                return;
            }

            BlendTree tree = motion as BlendTree;
            if (tree == null)
            {
                return;
            }

            List<AnimationClip> treeClips = new List<AnimationClip>();
            CollectBlendTreeClips(tree, overrides, treeClips);
            records.Add(new AnimationMergeExportRecord
            {
                layerIndex = layerIndex,
                layerName = layerName,
                statePath = statePath,
                motionPath = motionPath,
                motion = motion,
                isBlendTree = true,
                blendTreeClips = treeClips
            });

            // A blend tree is retained as a first-class exported node, while its
            // concrete clips are also exposed as mergeable Animation nodes.
            for (int i = 0; i < treeClips.Count; i++)
            {
                AnimationClip childClip = treeClips[i];
                records.Add(new AnimationMergeExportRecord
                {
                    layerIndex = layerIndex,
                    layerName = layerName,
                    statePath = statePath + "/BlendTree",
                    motionPath = motionPath + "/Child " + i,
                    motion = childClip,
                    clip = childClip,
                    isBlendTree = false
                });
            }
        }

        private static void CollectBlendTreeClips(
            BlendTree tree,
            Dictionary<AnimationClip, AnimationClip> overrides,
            List<AnimationClip> output)
        {
            if (tree == null)
            {
                return;
            }

            ChildMotion[] children = tree.children;
            for (int i = 0; i < children.Length; i++)
            {
                AnimationClip clip = children[i].motion as AnimationClip;
                if (clip != null)
                {
                    AnimationClip resolved = ResolveOverride(clip, overrides);
                    if (resolved != null && !output.Contains(resolved))
                    {
                        output.Add(resolved);
                    }
                    continue;
                }

                CollectBlendTreeClips(children[i].motion as BlendTree, overrides, output);
            }
        }

        private static AnimationClip ResolveOverride(
            AnimationClip clip,
            Dictionary<AnimationClip, AnimationClip> overrides)
        {
            AnimationClip overrideClip;
            return overrides != null && overrides.TryGetValue(clip, out overrideClip) && overrideClip != null
                ? overrideClip
                : clip;
        }

        public static List<string> GetBonePaths(
            GameObject root,
            List<AnimationClip> clips)
        {
            List<string> paths = new List<string>();
            if (root != null)
            {
                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    string path = AnimationUtility.CalculateTransformPath(transforms[i], root.transform);
                    if (!paths.Contains(path))
                    {
                        paths.Add(path);
                    }
                }
            }

            if (clips != null)
            {
                for (int i = 0; i < clips.Count; i++)
                {
                    AnimationClip clip = clips[i];
                    if (clip == null)
                    {
                        continue;
                    }

                    EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
                    for (int j = 0; j < bindings.Length; j++)
                    {
                        if (!paths.Contains(bindings[j].path))
                        {
                            paths.Add(bindings[j].path);
                        }
                    }

                    EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                    for (int j = 0; j < objectBindings.Length; j++)
                    {
                        if (!paths.Contains(objectBindings[j].path))
                        {
                            paths.Add(objectBindings[j].path);
                        }
                    }
                }
            }

            paths.Sort(CompareHierarchyPaths);
            return paths;
        }

        public static int CountBindings(AnimationClip clip, string path)
        {
            if (clip == null)
            {
                return 0;
            }

            int count = 0;
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                if (string.Equals(bindings[i].path, path, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                if (string.Equals(objectBindings[i].path, path, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        public static bool HasMotionData(AnimationClip clip, string path)
        {
            return CountBindings(clip, path) > 0;
        }

        public static List<string> GetDifferingBonePaths(AnimationClip left, AnimationClip right)
        {
            return GetDifferingBonePaths(new List<AnimationClip> { left, right });
        }

        public static List<string> GetDifferingBonePaths(IList<AnimationClip> clips)
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.Ordinal);
            if (clips != null)
            {
                for (int i = 0; i < clips.Count; i++)
                {
                    AddBindingPaths(clips[i], paths);
                }
            }

            List<string> differingPaths = new List<string>();
            foreach (string path in paths)
            {
                if (HasPathMotionDifference(clips, path))
                {
                    differingPaths.Add(path);
                }
            }

            differingPaths.Sort(CompareHierarchyPaths);
            return differingPaths;
        }

        private static bool HasPathMotionDifference(IList<AnimationClip> clips, string path)
        {
            if (clips == null || clips.Count < 2)
            {
                return false;
            }

            AnimationClip reference = clips[0];
            for (int i = 1; i < clips.Count; i++)
            {
                if (!ArePathMotionEqual(reference, clips[i], path))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddBindingPaths(AnimationClip clip, HashSet<string> paths)
        {
            if (clip == null)
            {
                return;
            }

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                paths.Add(bindings[i].path);
            }

            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                paths.Add(objectBindings[i].path);
            }
        }

        private static bool ArePathMotionEqual(AnimationClip left, AnimationClip right, string path)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            Dictionary<string, EditorCurveBinding> leftCurves = GetCurveBindingsByPath(left, path);
            Dictionary<string, EditorCurveBinding> rightCurves = GetCurveBindingsByPath(right, path);
            if (leftCurves.Count != rightCurves.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, EditorCurveBinding> pair in leftCurves)
            {
                EditorCurveBinding rightBinding;
                if (!rightCurves.TryGetValue(pair.Key, out rightBinding))
                {
                    return false;
                }

                AnimationCurve leftCurve = AnimationUtility.GetEditorCurve(left, pair.Value);
                AnimationCurve rightCurve = AnimationUtility.GetEditorCurve(right, rightBinding);
                if (!AreCurvesEqual(leftCurve, rightCurve))
                {
                    return false;
                }
            }

            Dictionary<string, EditorCurveBinding> leftObjectCurves = GetObjectBindingsByPath(left, path);
            Dictionary<string, EditorCurveBinding> rightObjectCurves = GetObjectBindingsByPath(right, path);
            if (leftObjectCurves.Count != rightObjectCurves.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, EditorCurveBinding> pair in leftObjectCurves)
            {
                EditorCurveBinding rightBinding;
                if (!rightObjectCurves.TryGetValue(pair.Key, out rightBinding) ||
                    !AreObjectCurvesEqual(
                        AnimationUtility.GetObjectReferenceCurve(left, pair.Value),
                        AnimationUtility.GetObjectReferenceCurve(right, rightBinding)))
                {
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<string, EditorCurveBinding> GetCurveBindingsByPath(AnimationClip clip, string path)
        {
            Dictionary<string, EditorCurveBinding> result = new Dictionary<string, EditorCurveBinding>(StringComparer.Ordinal);
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                if (string.Equals(bindings[i].path, path, StringComparison.Ordinal))
                {
                    result[GetBindingIdentity(bindings[i])] = bindings[i];
                }
            }
            return result;
        }

        private static Dictionary<string, EditorCurveBinding> GetObjectBindingsByPath(AnimationClip clip, string path)
        {
            Dictionary<string, EditorCurveBinding> result = new Dictionary<string, EditorCurveBinding>(StringComparer.Ordinal);
            EditorCurveBinding[] bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                if (string.Equals(bindings[i].path, path, StringComparison.Ordinal))
                {
                    result[GetBindingIdentity(bindings[i])] = bindings[i];
                }
            }
            return result;
        }

        private static string GetBindingIdentity(EditorCurveBinding binding)
        {
            string typeName = binding.type == null ? string.Empty : binding.type.FullName;
            return typeName + "|" + binding.propertyName;
        }

        private static bool AreCurvesEqual(AnimationCurve left, AnimationCurve right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            if (left.preWrapMode != right.preWrapMode ||
                left.postWrapMode != right.postWrapMode ||
                left.length != right.length)
            {
                return false;
            }

            Keyframe[] leftKeys = left.keys;
            Keyframe[] rightKeys = right.keys;
            for (int i = 0; i < leftKeys.Length; i++)
            {
                Keyframe a = leftKeys[i];
                Keyframe b = rightKeys[i];
                if (!Mathf.Approximately(a.time, b.time) ||
                    !Mathf.Approximately(a.value, b.value) ||
                    !Mathf.Approximately(a.inTangent, b.inTangent) ||
                    !Mathf.Approximately(a.outTangent, b.outTangent) ||
                    !Mathf.Approximately(a.inWeight, b.inWeight) ||
                    !Mathf.Approximately(a.outWeight, b.outWeight) ||
                    a.weightedMode != b.weightedMode)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreObjectCurvesEqual(
            ObjectReferenceKeyframe[] left,
            ObjectReferenceKeyframe[] right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!Mathf.Approximately(left[i].time, right[i].time) || left[i].value != right[i].value)
                {
                    return false;
                }
            }

            return true;
        }

        public static AnimationClip CreateMergedClip(
            IList<AnimationClip> sources,
            IList<AnimationMergeBoneChoice> choices,
            string clipName)
        {
            if (sources == null || sources.Count < 2)
            {
                throw new ArgumentException("At least two AnimationClip sources are required.");
            }

            List<AnimationClip> validSources = sources.Where(source => source != null).ToList();
            if (validSources.Count < 2)
            {
                throw new ArgumentException("At least two valid AnimationClip sources are required.");
            }

            Dictionary<string, AnimationMergeBoneChoice> choicesByPath = new Dictionary<string, AnimationMergeBoneChoice>(StringComparer.Ordinal);
            if (choices != null)
            {
                for (int i = 0; i < choices.Count; i++)
                {
                    AnimationMergeBoneChoice choice = choices[i];
                    if (choice != null && !choicesByPath.ContainsKey(choice.bonePath))
                    {
                        choicesByPath.Add(choice.bonePath, choice);
                    }
                }
            }

            AnimationClip output = new AnimationClip();
            output.name = string.IsNullOrEmpty(clipName) ? "Merged Animation" : clipName;
            output.frameRate = validSources.Max(source => source.frameRate);
            output.wrapMode = validSources[0].wrapMode;

            HashSet<string> copiedBindings = new HashSet<string>(StringComparer.Ordinal);
            for (int sourceIndex = 0; sourceIndex < validSources.Count; sourceIndex++)
            {
                AnimationClip source = validSources[sourceIndex];
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(source);
                for (int i = 0; i < bindings.Length; i++)
                {
                    EditorCurveBinding binding = bindings[i];
                    string bindingKey = GetBindingKey(binding, false);
                    if (copiedBindings.Contains(bindingKey) || !ShouldUseSource(binding.path, sourceIndex, choicesByPath))
                    {
                        continue;
                    }

                    int selectedSource = GetSelectedSource(binding.path, choicesByPath, sourceIndex);
                    if (selectedSource != sourceIndex || !TryGetCurve(validSources, selectedSource, binding, out AnimationCurve curve))
                    {
                        continue;
                    }

                    AnimationCurve copiedCurve = new AnimationCurve(curve.keys)
                    {
                        preWrapMode = curve.preWrapMode,
                        postWrapMode = curve.postWrapMode
                    };
                    AnimationUtility.SetEditorCurve(output, binding, copiedCurve);
                    copiedBindings.Add(bindingKey);
                }

                EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(source);
                for (int i = 0; i < objectBindings.Length; i++)
                {
                    EditorCurveBinding binding = objectBindings[i];
                    string bindingKey = GetBindingKey(binding, true);
                    if (copiedBindings.Contains(bindingKey) || !ShouldUseSource(binding.path, sourceIndex, choicesByPath))
                    {
                        continue;
                    }

                    int selectedSource = GetSelectedSource(binding.path, choicesByPath, sourceIndex);
                    if (selectedSource != sourceIndex)
                    {
                        continue;
                    }

                    ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(source, binding);
                    if (keys != null)
                    {
                        AnimationUtility.SetObjectReferenceCurve(output, binding, keys);
                        copiedBindings.Add(bindingKey);
                    }
                }
            }

            AnimationEvent[] events = validSources[0].events;
            if (events != null && events.Length > 0)
            {
                output.events = events;
            }

            return output;
        }

        private static bool ShouldUseSource(
            string path,
            int sourceIndex,
            Dictionary<string, AnimationMergeBoneChoice> choicesByPath)
        {
            AnimationMergeBoneChoice choice;
            if (!choicesByPath.TryGetValue(path, out choice))
            {
                return sourceIndex == 0;
            }

            return choice.sourceIndex == sourceIndex;
        }

        private static int GetSelectedSource(
            string path,
            Dictionary<string, AnimationMergeBoneChoice> choicesByPath,
            int fallback)
        {
            AnimationMergeBoneChoice choice;
            return choicesByPath.TryGetValue(path, out choice) ? choice.sourceIndex : fallback;
        }

        private static bool TryGetCurve(
            List<AnimationClip> sources,
            int sourceIndex,
            EditorCurveBinding binding,
            out AnimationCurve curve)
        {
            curve = null;
            if (sourceIndex < 0 || sourceIndex >= sources.Count)
            {
                return false;
            }

            curve = AnimationUtility.GetEditorCurve(sources[sourceIndex], binding);
            return curve != null;
        }

        private static string GetBindingKey(EditorCurveBinding binding, bool objectReference)
        {
            string typeName = binding.type == null ? string.Empty : binding.type.FullName;
            return (objectReference ? "object" : "curve") + "|" + binding.path + "|" + typeName + "|" + binding.propertyName;
        }

        private static int CompareHierarchyPaths(string left, string right)
        {
            int leftDepth = string.IsNullOrEmpty(left) ? 0 : left.Split('/').Length;
            int rightDepth = string.IsNullOrEmpty(right) ? 0 : right.Split('/').Length;
            int depthCompare = leftDepth.CompareTo(rightDepth);
            return depthCompare != 0 ? depthCompare : string.Compare(left, right, StringComparison.Ordinal);
        }

        public static Color GetLayerColor(int index)
        {
            Color[] palette =
            {
                new Color(0.22f, 0.58f, 0.95f),
                new Color(0.94f, 0.45f, 0.26f),
                new Color(0.43f, 0.78f, 0.37f),
                new Color(0.73f, 0.42f, 0.86f),
                new Color(0.92f, 0.72f, 0.20f),
                new Color(0.25f, 0.78f, 0.78f),
                new Color(0.91f, 0.36f, 0.57f),
                new Color(0.57f, 0.67f, 0.28f)
            };
            return palette[Mathf.Abs(index) % palette.Length];
        }

        public static Color GetNodeColor(int index)
        {
            float hue = Mathf.Repeat(Mathf.Abs(index) * 0.61803395f, 1f);
            return Color.HSVToRGB(hue, 0.68f, 0.92f);
        }
    }
}
