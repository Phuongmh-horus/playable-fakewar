# Lessons Learned

## 2026-07-17: Resources.Load is unreliable on Luna/WebGL
**Pattern**: Never use `Resources.Load<T>(path)` in code that must work on Luna/WebGL playable ad builds. Luna strips assets that aren't hard-referenced in serialized fields (ScriptableObject, MonoBehaviour). String-based dynamic loading will silently fail or return wrong assets.
**Rule**: Always use hard-reference `[SerializeField]` fields for assets needed on Luna. If a previous fix introduces `Resources.Load` as a "Luna compatibility fix", it is WRONG — remove it immediately.

## 2026-07-17: Pool systems must cache prefab references per key
**Pattern**: When implementing a local object pool keyed by tier/type, always cache the original prefab reference alongside the pool. When the pool is empty and needs to Instantiate, the cached prefab ensures the correct asset is used regardless of the caller's context.
**Rule**: `_cachedPrefabs[tier] = resolvedPrefab` during prewarm. `SpawnWeapon` checks `_cachedPrefabs[tier]` first before using the `fallbackPrefab` parameter.

## 2026-07-16: Always track lastFrameX for input even before game starts
**Pattern**: Input delta calculations must continuously update their baseline (`_lastFrameX`) even when the game hasn't started. Otherwise the first frame after start sees a massive delta jump.

## 2026-07-16: autoAddHitTextFlyEffectAtRuntime causes double effects
**Pattern**: When a prefab already has a `HitTextFlyEffect` component on a child, the parent's `autoAddHitTextFlyEffectAtRuntime = true` will add a SECOND one at runtime, causing duplicate damage text.
**Rule**: Set `autoAddHitTextFlyEffectAtRuntime = false` in Awake for any item that already has the component in its prefab hierarchy.

## 2026-07-20: Wheel-collision visuals must finish before despawn
**Pattern**: Visual effects such as `EffectType.Land` or a scale pulse need to run before the object is despawned/disposed; otherwise the effect is skipped or cut short.
**Rule**: Trigger the visual effect first, then delay the despawn call long enough for the animation to complete.

## 2026-07-27: Preview state and committed state must never both advance progression
**Pattern**: In coupled gameplay/UI flows like upgrade gates, craft queues, or card systems, a preview animation may temporarily advance visible level/progress, but the final commit step must only apply gameplay data once. If both preview and commit mutate the same progression state, levels, visuals, and rewards drift out of sync.
**Rule**: Trace the full end-to-end flow before patching. Decide which method owns preview state, which method owns committed state, and never increment the same level twice across those phases.

## 2026-07-27: Keep milestone granularity aligned with the original system contract
**Pattern**: When a system intentionally emits coarse milestones (for example, only when equipped/top-tier changes), do not silently increase milestone granularity inside core state code just to satisfy a visual bug. That couples data flow to presentation and makes later tuning harder.
**Rule**: Preserve the original milestone contract in gameplay logic. Fix visual ordering bugs inside the visual diff/animation layer unless the gameplay contract itself is proven wrong.

## 2026-07-27: For gate-upgrade flows, compute the spend plan first and drive visuals from that plan
**Pattern**: When road stretch, card reveals, and upgrade timing all depend on the same gold spend, resolving them live frame-by-frame causes drift and hard-to-reason bugs.
**Rule**: Simulate the full spend outcome first, then reuse that single plan to drive road length, progress bars, and upgrade reveal timing.

## 2026-07-27: Respect presentation ownership boundaries in UI-driven gameplay flows
**Pattern**: Some UI elements have exclusive ownership rules, like an equipped slot that should only be fed by merge transitions, or a gate canvas that should only be visible inside a trigger window. If generic fill logic touches those elements, the presentation breaks even when gameplay data is correct.
**Rule**: Treat special UI owners explicitly in code. Do not let bulk fill/refresh logic write to presentation targets that are meant to be driven by a narrower transition source.

## 2026-07-28: Max-level visual state must be treated as its own terminal UI state
**Pattern**: If a card/cell reaches max level, reusing the normal "reset progress for next upgrade" path will immediately wipe the full progress bar and active unlock styling, even though there is no next upgrade.
**Rule**: When progression enters a terminal max state, stop using next-upgrade reset logic. Preserve a dedicated full-progress and unlocked visual state until another explicit state change happens.

## 2026-07-28: Provide screen position fallback for reward triggers behind near clip plane
**Pattern**: When a trigger awards currency or triggers UI effects after the player passes through (`z <= 0` in camera space), standard `WorldToScreenPoint` checks may abort UI icon flight animations.
**Rule**: Always check `sourceScreen.z <= 0f` and fall back to a default on-screen coordinate (e.g. bottom-center where the player character is) so currency gain icon animations never fail silently.

## 2026-07-28: Ensure 3D pooled currency animations stop looping audio at gate enter trigger
**Pattern**: When flying 3D currency objects (`CoinDropItem`) trigger landing effects (`EffectType.Land`) with SFX, looping audio or persisting clips can continue playing after entering the gate.
**Rule**: Prevent `EffectType.Land` from looping in `EffectComponent` (`LoopSfx && effectType != EffectType.Land`) and explicitly call `StopEffect(EffectType.Land)` and `StopAllAudio()` when crossing the `enterTrigger`.

## 2026-07-28: Use dedicated IPoolable component for 3D currency flight & rotation animation on Luna/WebGL
**Pattern**: Relying on ad-hoc coroutines in UI scripts to animate 3D coin flight and rotation causes duplication and lacks lifecycle hooks for pooled objects.
**Rule**: Encapsulate 3D rotation (`Update()`) and Bezier/parabolic curve flight (`FlyTo`/`AnimateParabolaTo`) in a dedicated component (`CoinDropItemFlyEffect` implementing `IPoolable`). Use `CoinDropItemFlyEffect.SpawnAndFly(...)` to spawn and animate bursts cleanly without allocation or Luna/WebGL compatibility issues.

## 2026-07-28: Prefer DOTween (DOVirtual.Float / DOVirtual.DelayedCall) over Coroutines for UI and Flight Animations in Luna/WebGL
**Pattern**: Spawning dozens of `StartCoroutine` calls for currency bursts or delays causes unnecessary enumerator allocations and GC pressure. Also, `Task.CompletedTask` is unsupported in certain Luna/WebGL target profiles.
**Rule**: Use DOTween (`DG.Tweening`) via `DOVirtual.Float(0f, 1f, duration, onUpdate)` for continuous lerps and `DOVirtual.DelayedCall(delay, onComplete)` for timers. DOTween is pre-integrated and 100% supported in Luna/WebGL playable builds with zero C# coroutine allocations.

## 2026-07-28: Always randomize multi-item visual bursts (positions, rotations, durations) to avoid visual stacking
**Pattern**: When spawning a burst of visual items (e.g. `CoinDropItem`), spawning all items with identical position, rotation, duration, and curve height causes 100% visual overlap (stacking into 1 single object).
**Rule**: Apply random position offsets, random initial rotations, and random duration/arc variations (`Random.Range`) across multiple spawned items so they scatter naturally.

## 2026-07-28: Allow partial progress in currency spend eligibility checks
**Pattern**: If eligibility checks require `gold >= nextCost`, players with positive gold less than `nextCost` cannot make partial progress on upgrade gates, causing spent-currency checks and UI updates to fail silently.
**Rule**: Check `nextCost != int.MaxValue && gold > 0` for spend eligibility so partial progress can be applied cleanly.

## 2026-07-28: Ensure 3D UI flight target depth is further forward than start position to prevent visual backward flight
**Pattern**: When converting UI screen icons to 3D world targets using `ScreenToWorldPoint(x, y, depth)`, calculating `depth = referenceZ * 0.5f` places the target closer to the camera than the starting drop point. Due to perspective, moving closer along Z makes objects look like they fly backwards over the camera instead of rising upwards.
**Rule**: Always ensure the target depth is greater than or equal to the drop point's camera depth (`depth = Mathf.Max(minDepth, cam.WorldToScreenPoint(referenceWorldPos).z + forwardOffset)`) so 3D currency items visually travel upwards on screen without flying backwards.

## 2026-07-28: Distinguish 2D UI Icon Cache vs 3D Prefab presentation modes for currency gain vs spend
**Pattern**: Mixing up 3D coin prefabs and 2D UI icon cache bursts across gain and spend animations can violate intended visual hierarchy and user expectations.

## 2026-07-28: Provide screen position fallback for reward triggers behind near clip plane
**Pattern**: When a trigger awards currency or triggers UI effects after the player passes through (`z <= 0` in camera space), standard `WorldToScreenPoint` checks may abort UI icon flight animations.
**Rule**: Always check `sourceScreen.z <= 0f` and fall back to a default on-screen coordinate (e.g. bottom-center where the player character is) so currency gain icon animations never fail silently.

## 2026-07-28: Ensure 3D pooled currency animations stop looping audio at gate enter trigger
**Pattern**: When flying 3D currency objects (`CoinDropItem`) trigger landing effects (`EffectType.Land`) with SFX, looping audio or persisting clips can continue playing after entering the gate.
**Rule**: Prevent `EffectType.Land` from looping in `EffectComponent` (`LoopSfx && effectType != EffectType.Land`) and explicitly call `StopEffect(EffectType.Land)` and `StopAllAudio()` when crossing the `enterTrigger`.

## 2026-07-28: Use dedicated IPoolable component for 3D currency flight & rotation animation on Luna/WebGL
**Pattern**: Relying on ad-hoc coroutines in UI scripts to animate 3D coin flight and rotation causes duplication and lacks lifecycle hooks for pooled objects.
**Rule**: Encapsulate 3D rotation (`Update()`) and Bezier/parabolic curve flight (`FlyTo`/`AnimateParabolaTo`) in a dedicated component (`CoinDropItemFlyEffect` implementing `IPoolable`). Use `CoinDropItemFlyEffect.SpawnAndFly(...)` to spawn and animate bursts cleanly without allocation or Luna/WebGL compatibility issues.

## 2026-07-28: Prefer DOTween (DOVirtual.Float / DOVirtual.DelayedCall) over Coroutines for UI and Flight Animations in Luna/WebGL
**Pattern**: Spawning dozens of `StartCoroutine` calls for currency bursts or delays causes unnecessary enumerator allocations and GC pressure. Also, `Task.CompletedTask` is unsupported in certain Luna/WebGL target profiles.
**Rule**: Use DOTween (`DG.Tweening`) via `DOVirtual.Float(0f, 1f, duration, onUpdate)` for continuous lerps and `DOVirtual.DelayedCall(delay, onComplete)` for timers. DOTween is pre-integrated and 100% supported in Luna/WebGL playable builds with zero C# coroutine allocations.

## 2026-07-28: Always randomize multi-item visual bursts (positions, rotations, durations) to avoid visual stacking
**Pattern**: When spawning a burst of visual items (e.g. `CoinDropItem`), spawning all items with identical position, rotation, duration, and curve height causes 100% visual overlap (stacking into 1 single object).
**Rule**: Apply random position offsets, random initial rotations, and random duration/arc variations (`Random.Range`) across multiple spawned items so they scatter naturally.

## 2026-07-28: Allow partial progress in currency spend eligibility checks
**Pattern**: If eligibility checks require `gold >= nextCost`, players with positive gold less than `nextCost` cannot make partial progress on upgrade gates, causing spent-currency checks and UI updates to fail silently.
**Rule**: Check `nextCost != int.MaxValue && gold > 0` for spend eligibility so partial progress can be applied cleanly.

## 2026-07-28: Ensure 3D UI flight target depth is further forward than start position to prevent visual backward flight
**Pattern**: When converting UI screen icons to 3D world targets using `ScreenToWorldPoint(x, y, depth)`, calculating `depth = referenceZ * 0.5f` places the target closer to the camera than the starting drop point. Due to perspective, moving closer along Z makes objects look like they fly backwards over the camera instead of rising upwards.
**Rule**: Always ensure the target depth is greater than or equal to the drop point's camera depth (`depth = Mathf.Max(minDepth, cam.WorldToScreenPoint(referenceWorldPos).z + forwardOffset)`) so 3D currency items visually travel upwards on screen without flying backwards.

## 2026-07-28: Distinguish 2D UI Icon Cache vs 3D Prefab presentation modes for currency gain vs spend
**Pattern**: Mixing up 3D coin prefabs and 2D UI icon cache bursts across gain and spend animations can violate intended visual hierarchy and user expectations.
**Rule**: For currency gain (`PlayGainAnimation` from gates/rewards to UI bar), use the 2D UI Icon Cache (`AnimateIconsBurst` on Canvas). For currency spend (`PlaySpendCoinsAnimation` from player army into upgrade gates), use the 3D coin prefab (`coinDropItemPrefab` with `CoinDropItemFlyEffect.SpawnAndFly`).

## 2026-07-28: Never prematurely disable parent Canvas before UI card animations complete in gate upgrade flows
**Pattern**: Disabling a gate's Canvas (`cellCanvas.SetActive(false)`) immediately upon hitting an exit trigger aborts or hides ongoing reveal and card-flight animations (`BuffCardSystem`) that spawn from or reference that Canvas.
**Rule**: Keep the gate Canvas active when exiting the trigger if card animations are triggered. Pass a completion callback (`onComplete`) to the card animation system and only disable the Canvas (`cellCanvas.SetActive(false)`) and stop gate effects when the last card arrives at the UI bar. Also implement `OnDisable()` cleanup as a safety fallback.

## 2026-07-28: For gate card reveal animations, rotate and reveal on the gate canvas first before launching flight to UI bar
**Pattern**: Spawning a new overlay UI card that rotates while flying to the UI card bar creates a confusing visual where the card appears to open mid-flight while the original card on the gate canvas remains closed/unknown.
**Rule**: Always animate the card reveal (rotate 0->90->0, swap sprite from unknown to normal, enable icon) directly on the gate's canvas (`IncreaseElement` on `cellCanvas`) FIRST. Only after the in-place reveal completes on the canvas, spawn the flying UI card (`UI_CardInfo`) in its already-revealed state to fly smoothly to the UI card bar.
# Lessons Learned

## 2026-07-17: Resources.Load is unreliable on Luna/WebGL
**Pattern**: Never use `Resources.Load<T>(path)` in code that must work on Luna/WebGL playable ad builds. Luna strips assets that aren't hard-referenced in serialized fields (ScriptableObject, MonoBehaviour). String-based dynamic loading will silently fail or return wrong assets.
**Rule**: Always use hard-reference `[SerializeField]` fields for assets needed on Luna. If a previous fix introduces `Resources.Load` as a "Luna compatibility fix", it is WRONG — remove it immediately.

## 2026-07-17: Pool systems must cache prefab references per key
**Pattern**: When implementing a local object pool keyed by tier/type, always cache the original prefab reference alongside the pool. When the pool is empty and needs to Instantiate, the cached prefab ensures the correct asset is used regardless of the caller's context.
**Rule**: `_cachedPrefabs[tier] = resolvedPrefab` during prewarm. `SpawnWeapon` checks `_cachedPrefabs[tier]` first before using the `fallbackPrefab` parameter.

## 2026-07-16: Always track lastFrameX for input even before game starts
**Pattern**: Input delta calculations must continuously update their baseline (`_lastFrameX`) even when the game hasn't started. Otherwise the first frame after start sees a massive delta jump.

## 2026-07-16: autoAddHitTextFlyEffectAtRuntime causes double effects
**Pattern**: When a prefab already has a `HitTextFlyEffect` component on a child, the parent's `autoAddHitTextFlyEffectAtRuntime = true` will add a SECOND one at runtime, causing duplicate damage text.
**Rule**: Set `autoAddHitTextFlyEffectAtRuntime = false` in Awake for any item that already has the component in its prefab hierarchy.

## 2026-07-20: Wheel-collision visuals must finish before despawn
**Pattern**: Visual effects such as `EffectType.Land` or a scale pulse need to run before the object is despawned/disposed; otherwise the effect is skipped or cut short.
**Rule**: Trigger the visual effect first, then delay the despawn call long enough for the animation to complete.

## 2026-07-27: Preview state and committed state must never both advance progression
**Pattern**: In coupled gameplay/UI flows like upgrade gates, craft queues, or card systems, a preview animation may temporarily advance visible level/progress, but the final commit step must only apply gameplay data once. If both preview and commit mutate the same progression state, levels, visuals, and rewards drift out of sync.
**Rule**: Trace the full end-to-end flow before patching. Decide which method owns preview state, which method owns committed state, and never increment the same level twice across those phases.

## 2026-07-27: Keep milestone granularity aligned with the original system contract
**Pattern**: When a system intentionally emits coarse milestones (for example, only when equipped/top-tier changes), do not silently increase milestone granularity inside core state code just to satisfy a visual bug. That couples data flow to presentation and makes later tuning harder.
**Rule**: Preserve the original milestone contract in gameplay logic. Fix visual ordering bugs inside the visual diff/animation layer unless the gameplay contract itself is proven wrong.

## 2026-07-27: For gate-upgrade flows, compute the spend plan first and drive visuals from that plan
**Pattern**: When road stretch, card reveals, and upgrade timing all depend on the same gold spend, resolving them live frame-by-frame causes drift and hard-to-reason bugs.
**Rule**: Simulate the full spend outcome first, then reuse that single plan to drive road length, progress bars, and upgrade reveal timing.

## 2026-07-27: Respect presentation ownership boundaries in UI-driven gameplay flows
**Pattern**: Some UI elements have exclusive ownership rules, like an equipped slot that should only be fed by merge transitions, or a gate canvas that should only be visible inside a trigger window. If generic fill logic touches those elements, the presentation breaks even when gameplay data is correct.
**Rule**: Treat special UI owners explicitly in code. Do not let bulk fill/refresh logic write to presentation targets that are meant to be driven by a narrower transition source.

## 2026-07-28: Max-level visual state must be treated as its own terminal UI state
**Pattern**: If a card/cell reaches max level, reusing the normal "reset progress for next upgrade" path will immediately wipe the full progress bar and active unlock styling, even though there is no next upgrade.
**Rule**: When progression enters a terminal max state, stop using next-upgrade reset logic. Preserve a dedicated full-progress and unlocked visual state until another explicit state change happens.

## 2026-07-28: Provide screen position fallback for reward triggers behind near clip plane
**Pattern**: When a trigger awards currency or triggers UI effects after the player passes through (`z <= 0` in camera space), standard `WorldToScreenPoint` checks may abort UI icon flight animations.
**Rule**: Always check `sourceScreen.z <= 0f` and fall back to a default on-screen coordinate (e.g. bottom-center where the player character is) so currency gain icon animations never fail silently.

## 2026-07-28: Ensure 3D pooled currency animations stop looping audio at gate enter trigger
**Pattern**: When flying 3D currency objects (`CoinDropItem`) trigger landing effects (`EffectType.Land`) with SFX, looping audio or persisting clips can continue playing after entering the gate.
**Rule**: Prevent `EffectType.Land` from looping in `EffectComponent` (`LoopSfx && effectType != EffectType.Land`) and explicitly call `StopEffect(EffectType.Land)` and `StopAllAudio()` when crossing the `enterTrigger`.

## 2026-07-28: Use dedicated IPoolable component for 3D currency flight & rotation animation on Luna/WebGL
**Pattern**: Relying on ad-hoc coroutines in UI scripts to animate 3D coin flight and rotation causes duplication and lacks lifecycle hooks for pooled objects.
**Rule**: Encapsulate 3D rotation (`Update()`) and Bezier/parabolic curve flight (`FlyTo`/`AnimateParabolaTo`) in a dedicated component (`CoinDropItemFlyEffect` implementing `IPoolable`). Use `CoinDropItemFlyEffect.SpawnAndFly(...)` to spawn and animate bursts cleanly without allocation or Luna/WebGL compatibility issues.

## 2026-07-28: Prefer DOTween (DOVirtual.Float / DOVirtual.DelayedCall) over Coroutines for UI and Flight Animations in Luna/WebGL
**Pattern**: Spawning dozens of `StartCoroutine` calls for currency bursts or delays causes unnecessary enumerator allocations and GC pressure. Also, `Task.CompletedTask` is unsupported in certain Luna/WebGL target profiles.
**Rule**: Use DOTween (`DG.Tweening`) via `DOVirtual.Float(0f, 1f, duration, onUpdate)` for continuous lerps and `DOVirtual.DelayedCall(delay, onComplete)` for timers. DOTween is pre-integrated and 100% supported in Luna/WebGL playable builds with zero C# coroutine allocations.

## 2026-07-28: Always randomize multi-item visual bursts (positions, rotations, durations) to avoid visual stacking
**Pattern**: When spawning a burst of visual items (e.g. `CoinDropItem`), spawning all items with identical position, rotation, duration, and curve height causes 100% visual overlap (stacking into 1 single object).
**Rule**: Apply random position offsets, random initial rotations, and random duration/arc variations (`Random.Range`) across multiple spawned items so they scatter naturally.

## 2026-07-28: Allow partial progress in currency spend eligibility checks
**Pattern**: If eligibility checks require `gold >= nextCost`, players with positive gold less than `nextCost` cannot make partial progress on upgrade gates, causing spent-currency checks and UI updates to fail silently.
**Rule**: Check `nextCost != int.MaxValue && gold > 0` for spend eligibility so partial progress can be applied cleanly.

## 2026-07-28: Ensure 3D UI flight target depth is further forward than start position to prevent visual backward flight
**Pattern**: When converting UI screen icons to 3D world targets using `ScreenToWorldPoint(x, y, depth)`, calculating `depth = referenceZ * 0.5f` places the target closer to the camera than the starting drop point. Due to perspective, moving closer along Z makes objects look like they fly backwards over the camera instead of rising upwards.
**Rule**: Always ensure the target depth is greater than or equal to the drop point's camera depth (`depth = Mathf.Max(minDepth, cam.WorldToScreenPoint(referenceWorldPos).z + forwardOffset)`) so 3D currency items visually travel upwards on screen without flying backwards.

## 2026-07-28: Distinguish 2D UI Icon Cache vs 3D Prefab presentation modes for currency gain vs spend
**Pattern**: Mixing up 3D coin prefabs and 2D UI icon cache bursts across gain and spend animations can violate intended visual hierarchy and user expectations.

**Pattern**: When flying 3D currency objects (`CoinDropItem`) trigger landing effects (`EffectType.Land`) with SFX, looping audio or persisting clips can continue playing after entering the gate.
**Rule**: Prevent `EffectType.Land` from looping in `EffectComponent` (`LoopSfx && effectType != EffectType.Land`) and explicitly call `StopEffect(EffectType.Land)` and `StopAllAudio()` when crossing the `enterTrigger`.

## 2026-07-28: Use dedicated IPoolable component for 3D currency flight & rotation animation on Luna/WebGL
**Pattern**: Relying on ad-hoc coroutines in UI scripts to animate 3D coin flight and rotation causes duplication and lacks lifecycle hooks for pooled objects.
**Rule**: Encapsulate 3D rotation (`Update()`) and Bezier/parabolic curve flight (`FlyTo`/`AnimateParabolaTo`) in a dedicated component (`CoinDropItemFlyEffect` implementing `IPoolable`). Use `CoinDropItemFlyEffect.SpawnAndFly(...)` to spawn and animate bursts cleanly without allocation or Luna/WebGL compatibility issues.

## 2026-07-28: Prefer DOTween (DOVirtual.Float / DOVirtual.DelayedCall) over Coroutines for UI and Flight Animations in Luna/WebGL
**Pattern**: Spawning dozens of `StartCoroutine` calls for currency bursts or delays causes unnecessary enumerator allocations and GC pressure. Also, `Task.CompletedTask` is unsupported in certain Luna/WebGL target profiles.
**Rule**: Use DOTween (`DG.Tweening`) via `DOVirtual.Float(0f, 1f, duration, onUpdate)` for continuous lerps and `DOVirtual.DelayedCall(delay, onComplete)` for timers. DOTween is pre-integrated and 100% supported in Luna/WebGL playable builds with zero C# coroutine allocations.

## 2026-07-28: Always randomize multi-item visual bursts (positions, rotations, durations) to avoid visual stacking
**Pattern**: When spawning a burst of visual items (e.g. `CoinDropItem`), spawning all items with identical position, rotation, duration, and curve height causes 100% visual overlap (stacking into 1 single object).
**Rule**: Apply random position offsets, random initial rotations, and random duration/arc variations (`Random.Range`) across multiple spawned items so they scatter naturally.

## 2026-07-28: Allow partial progress in currency spend eligibility checks
**Pattern**: If eligibility checks require `gold >= nextCost`, players with positive gold less than `nextCost` cannot make partial progress on upgrade gates, causing spent-currency checks and UI updates to fail silently.
**Rule**: Check `nextCost != int.MaxValue && gold > 0` for spend eligibility so partial progress can be applied cleanly.

## 2026-07-28: Ensure 3D UI flight target depth is further forward than start position to prevent visual backward flight
**Pattern**: When converting UI screen icons to 3D world targets using `ScreenToWorldPoint(x, y, depth)`, calculating `depth = referenceZ * 0.5f` places the target closer to the camera than the starting drop point. Due to perspective, moving closer along Z makes objects look like they fly backwards over the camera instead of rising upwards.
**Rule**: Always ensure the target depth is greater than or equal to the drop point's camera depth (`depth = Mathf.Max(minDepth, cam.WorldToScreenPoint(referenceWorldPos).z + forwardOffset)`) so 3D currency items visually travel upwards on screen without flying backwards.

## 2026-07-28: Distinguish 2D UI Icon Cache vs 3D Prefab presentation modes for currency gain vs spend
**Pattern**: Mixing up 3D coin prefabs and 2D UI icon cache bursts across gain and spend animations can violate intended visual hierarchy and user expectations.
**Rule**: For currency gain (`PlayGainAnimation` from gates/rewards to UI bar), use the 2D UI Icon Cache (`AnimateIconsBurst` on Canvas). For currency spend (`PlaySpendCoinsAnimation` from player army into upgrade gates), use the 3D coin prefab (`coinDropItemPrefab` with `CoinDropItemFlyEffect.SpawnAndFly`).

## 2026-07-28: Never prematurely disable parent Canvas before UI card animations complete in gate upgrade flows
**Pattern**: Disabling a gate's Canvas (`cellCanvas.SetActive(false)`) immediately upon hitting an exit trigger aborts or hides ongoing reveal and card-flight animations (`BuffCardSystem`) that spawn from or reference that Canvas.
**Rule**: Keep the gate Canvas active when exiting the trigger if card animations are triggered. Pass a completion callback (`onComplete`) to the card animation system and only disable the Canvas (`cellCanvas.SetActive(false)`) and stop gate effects when the last card arrives at the UI bar. Also implement `OnDisable()` cleanup as a safety fallback.

## 2026-07-28: For gate card reveal animations, rotate and reveal on the gate canvas first before launching flight to UI bar
**Pattern**: Spawning a new overlay UI card that rotates while flying to the UI card bar creates a confusing visual where the card appears to open mid-flight while the original card on the gate canvas remains closed/unknown.
**Rule**: Always animate the card reveal (rotate 0->90->0, swap sprite from unknown to normal, enable icon) directly on the gate's canvas (`IncreaseElement` on `cellCanvas`) FIRST. Only after the in-place reveal completes on the canvas, spawn the flying UI card (`UI_CardInfo`) in its already-revealed state to fly smoothly to the UI card bar.

## 2026-07-29: Keep max-level progress bar full, retain active gradient during reveal, and scale road segment pullback offset for multi-card gates
**Pattern**: Resetting progress sliders via `UpdateProgress(0)` on maxed elements empties the bar, while deactivating visual state before reveal animations turn off active gradients and unlock images. Additionally, fixed pullback offsets (`-1.5f`) fail when multi-card gates extend the road across many steps, leaving visible gaps.
**Rule**: Always keep slider values full (`slider.value = 1f`) when `IsMaxLevel()` is true, and check `_isRevealing || m_levelCard > 0 || IsMaxLevel()` before toggling gradients and unlock images. For road extensions (`ExtendMainRoadSteps`), dynamically increase segment pullback offsets (`pullbackOffset = 1.5f -> 3.5f`) based on element count and upgrade steps (`increaseElements.Count >= 3` or `stepCount >= 5`).

## 2026-07-29: Zero-GC patterns for high-frequency gameplay Update loops on Luna/WebGL
**Pattern**: In core systems running every frame (`LunaUIManager`, `PlayerArmySystem`, `CapacityIncreaseGate`), using `HashSet.UnionWith`, calling `cellCanvas.SetActive(true)` on uninitialized UI, or polling tutorial/touch arrays causes massive GC allocations (26KB - 33KB per frame) and CPU spikes (13ms+).
**Rule**:
1. **Pointer Swapping over HashSet UnionWith**: Instead of calling `.Clear()` and `.UnionWith(...)` on HashSets every frame (which boxes the struct Enumerator into `IEnumerable<int>`), swap reference pointers (`var tmp = _prev; _prev = _curr; _curr = tmp;`) for 0 Bytes GC Alloc.
2. **Prewarm UI hierarchies via Canvas Toggle**: Keep UI GameObjects active during boot so `Awake()`/`OnEnable()` run once at start, and toggle visibility using `Canvas.enabled = false/true` instead of `GameObject.SetActive(false/true)` to eliminate UI activation CPU spikes and GC spikes.
3. **Disable idle Update loops & direct struct fields**: Disable MonoBehaviours (`enabled = false`) when their feature layer is inactive (e.g. `LunaUIManager` when tutorial is hidden), and store timers (`NextAttackTime`) directly on units instead of dictionary lookups.

## 2026-07-29: Tune 3D coin flight duration and random spread for smooth spend animation into increase gates
**Pattern**: Hardcoding short durations (`0.25f`) for 3D coin flight from the army into upgrade gates causes coins to zip across the screen instantly before the player even reaches the gate, creating a jarring "teleporting" look.
**Rule**: Use a configurable duration (`coinFlyDuration = 0.95f`) and a tighter random duration spread (`0.85f - 1.15f` instead of `0.75f - 1.25f`) so that a stream of 10 coins gracefully arcs into the increase gate over ~1 second as the player runs up to it.

## 2026-07-29: Do not initialize MaterialPropertyBlock in MonoBehaviour field initializers
**Pattern**: Calling `new MaterialPropertyBlock()` in an instance field initializer of a `MonoBehaviour` (e.g. `private readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();`) executes inside the class constructor before Unity initialization, throwing `UnityException: CreateImpl is not allowed to be called from a MonoBehaviour constructor (or instance field initializer)`.
**Rule**: Declare the field as `private MaterialPropertyBlock _mpb;` and initialize it lazily on first use (`if (_mpb == null) _mpb = new MaterialPropertyBlock();`) or inside `Awake()`/`Start()`.
