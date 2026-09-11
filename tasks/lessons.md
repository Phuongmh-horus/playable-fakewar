# Lessons

- After a user correction about intended behavior, update the implementation to cover all affected variants, not just the first target path mentioned.
- When preserving a future projectile behavior in Unity, keep the hook or reference code, but verify whether the user wants it active now or only retained as a commented example.
- For Unity API compatibility fixes, prefer the most broadly supported checks on `Component`/`Behaviour` (`enabled`, `gameObject.activeInHierarchy`) instead of assuming convenience properties exist on every type in this project setup.
- When throttling gameplay VFX/SFX in Unity, verify whether the limit should be "per frame" or "per N frames" and apply the same rule to every call path of that effect, not just one branch.
- For Luna/WebGL render fixes, avoid `Renderer.GetPropertyBlock` on paths already known to warn in Web builds; prefer clearing a reused `MaterialPropertyBlock` and writing only the properties needed for that renderer.
- When adding a new serialized gameplay value, update every matching content override and scene-capture path, including composite or multi-slot variants.
- After undoing related gameplay files, validate cross-file APIs immediately; partial undo can leave callers and implementations out of sync.
- For a persistent Unity runtime bug, do not conclude from component presence or a plausible static call path; verify the serialized data, runtime initialization order, collision registration, and the actual event that should trigger the behavior.
- Do not replace a gameplay-critical pooled `Spawn` with `TrySpawn` unless the pool readiness and exhaustion behavior have been verified in the target Luna/WebGL build; a silent null must never suppress player projectiles or rewards.
- For target-bound VFX, honor the prefab's `ParentToTarget` setting; do not add an effect-type override that silently forces world-space behavior.
- When optimizing renderer culling by grouping renderers, preserve each renderer's live visibility state and validate moving VAT enemies at near/far boundaries; a shared anchor or cached initial state must not hide active enemies incorrectly.
- For a simple prefab hierarchy issue, prefer fixing the hierarchy over adding a serialized runtime target; place UI outside a rotating mesh parent, and gate decorative ticks with the existing gameplay-start state when the requested timing matches it.
- For max-unit feedback, evaluate the current active unit count at the collision that grants the reward; do not persist a historical "was full" flag, because deaths must restore the `+ unit` result.
- FireSoldier collection intentionally keeps its `FormatDisplayValue()` (`+N`/`xN`) fly-text code commented out: army buff text already communicates the reward, and enabling both spams the screen. Preserve the commented block for a future visual pass; do not delete it during cleanup or optimization.
- Treat playable performance work as iterative: after a measured CPU/RAM improvement, re-profile the new dominant hotspots and continue system-wide instead of assuming the first optimized batch is sufficient.
