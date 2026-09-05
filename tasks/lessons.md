# Lessons

- After a user correction about intended behavior, update the implementation to cover all affected variants, not just the first target path mentioned.
- When preserving a future projectile behavior in Unity, keep the hook or reference code, but verify whether the user wants it active now or only retained as a commented example.
- For Unity API compatibility fixes, prefer the most broadly supported checks on `Component`/`Behaviour` (`enabled`, `gameObject.activeInHierarchy`) instead of assuming convenience properties exist on every type in this project setup.
- When throttling gameplay VFX/SFX in Unity, verify whether the limit should be "per frame" or "per N frames" and apply the same rule to every call path of that effect, not just one branch.
- For Luna/WebGL render fixes, avoid `Renderer.GetPropertyBlock` on paths already known to warn in Web builds; prefer clearing a reused `MaterialPropertyBlock` and writing only the properties needed for that renderer.
- When adding a new serialized gameplay value, update every matching content override and scene-capture path, including composite or multi-slot variants.
- After undoing related gameplay files, validate cross-file APIs immediately; partial undo can leave callers and implementations out of sync.
- For a persistent Unity runtime bug, do not conclude from component presence or a plausible static call path; verify the serialized data, runtime initialization order, collision registration, and the actual event that should trigger the behavior.
