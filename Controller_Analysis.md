# Phân tích vòng đời Init/Pool giai đoạn đầu Playable

> Tài liệu gồm 2 phần độc lập, cùng chủ đề "vì sao 1 script lại cần nhiều hàm vòng đời
> (Awake/Initialize/Start/OnEnable...) và có thể tối ưu gì ở giai đoạn đầu Playable":
> - **Phần 1**: `CashTowerController` (đại diện cho nhóm entity **dùng chung PoolSystem, bật/tắt
>   qua `SetActive`** — Awake/Initialize/OnEnable).
> - **Phần 2**: `PlayerArmySystem` (đại diện cho nhóm entity **duy nhất 1 instance/màn chơi,
>   bị Destroy và Instantiate lại mỗi trận** — Awake/Initialize/Start/OnDestroy).
>
> So sánh 2 phần sẽ cho thấy: cùng là "vì sao cần tách Awake khỏi Initialize", nhưng lý do và
> cặp hàm "bảo vệ đối xứng" (OnEnable/OnDisable vs Start/OnDestroy) khác nhau tuỳ theo **mô hình
> vòng đời** của entity (pool tái sử dụng qua SetActive, hay Destroy/Instantiate lại từ đầu).

---

# Phần 1 — CashTowerController

> Phạm vi: `CashTowerController.cs`, `ItemUnit.cs`, `PoolEntity.cs`, `PoolSystem.cs`, `PoolExtensions.cs`,
> `MapContentGenerator.cs`, `GameplayManager.cs` (đoạn `CoBootAndIntroSequence`).
> Các class không có trong bộ file được cung cấp (`HealthComponent`, `HitComponent`, `EffectComponent`,
> `CollisionSystem`, `DropCurrencyEffect`, `CurrencyDropItem`, `BlockDebrisController`, `HitTextFlyEffect`,
> `TextUtility`) chỉ được suy luận qua cách chúng được **gọi**, không phải qua source thật — những chỗ này
> mình sẽ ghi rõ là "giả định" để bạn tự đối chiếu lại.

---

## 1. CashTowerController nằm ở đâu trong cây kế thừa

```
MonoBehaviour
 └─ PoolEntity (IPoolable)              // GamePlay.Entities
     └─ ItemUnit (IAttacker, IComponent, IHitable)   // GamePlay.Items
         └─ CashTowerController          // GamePlay.Items
```

Khác với `GoldModifierGate` / `WeaponModifierGate` / `DiamondSpike` (kế thừa từ
`StatModifierItem<TData>` — một lớp trung gian có thêm `Data`, `HandleWheelCollision` gọi
`GameplayManager.ChangeStatModifierData`...), `CashTowerController` kế thừa **trực tiếp** từ
`ItemUnit`. Nó không có `StatModifierData`, không cộng dồn "stat" — nó là cái tháp đích
(Finish Tower) của map: chịu damage, hết máu thì vỡ, rớt tiền, báo `GameplayManager.EndGame()`
(qua `IGameplayFlow`).

`EntityType` mặc định được ép về `FinishTower` ngay trong `Awake()` nếu prefab chưa gán,
điều này khớp với việc `PlayerArmySystem.ResolveFinishTowerContact()` và
`TargetMask` của army có include `EntityType.FinishTower`.

---

## 2. Luồng tổng thể lúc boot Playable (liên quan tới content/FinishTower)

Trong `GameplayManager.CoBootAndIntroSequence()`:

1. **Sinh map** (`MapGenerator.GenerateMap`) — 1 `RoadSegment` duy nhất + background.
2. **Sinh content**:
   - Nếu đang ở Editor + `autoGenerateContentInEditor` → chạy
     `contentGenerator.GenerateContentDataAsync(..., initializeItems: false, batchSize: spawnItemsPerFrame)`.
   - Ngược lại (build thật / Luna):
     - Nếu `contentGenerator.HasPrebakedContent()` → dùng luôn nội dung đã **bake sẵn trong scene**
       (`UsePrebakedContent(initializeItems: false)`), **không Instantiate gì cả**.
     - Nếu không có prebake → mới thật sự sinh runtime bằng
       `GenerateContentDataAsync(..., initializeItems: false, ...)`.
   - Cả 2 đường đều truyền `initializeItems: false` → nghĩa là **`Initialize()` KHÔNG được gọi
     tại bước spawn**, chỉ có `Awake()` (nếu là spawn runtime) chạy.
3. Spawn Army, prewarm vài prefab khác (weapon, VFX, character)...
4. **Bước init content thực sự** (đây là bước quan trọng nhất với `CashTowerController`):

```csharp
if (contentGenerator != null && contentGenerator.generatedObjects != null)
{
    int batchSize = Mathf.Max(1, initItemsPerFrame); // default 5
    for (int i = 0; i < contentGenerator.generatedObjects.Count; i++)
    {
        var item = contentGenerator.generatedObjects[i];
        if (item != null) item.Initialize();
        if ((i + 1) % batchSize == 0) yield return null; // giãn ra nhiều frame
    }
}
```

→ Đây là chỗ `CashTowerController.Initialize()` được gọi, **rải đều 5 item/frame**
để tránh giật khung hình lúc mở game — engineer đã chủ động tách "spawn" và "init nặng"
ra 2 pha, và cả 2 pha đều được batch theo frame.

### Điểm quan trọng: 2 đường dữ liệu khác hẳn nhau về chi phí

- **Đường prebaked (khuyến nghị dùng cho build thật/Luna, xem cờ `usePrebakedContentInPlayMode`)**:
  các FinishTower **đã tồn tại sẵn trong scene lúc load**, nghĩa là `Awake()` (và
  `CacheAll()` bên trong nó) đã chạy **từ lúc scene load xong**, hoàn toàn miễn phí đối với
  coroutine boot — coroutine chỉ còn tốn `Initialize()` (đã batch).
- **Đường generate runtime**: mỗi tháp phải `Instantiate()` (kéo theo `Awake()` + `OnEnable()`
  ngay lập tức trong `SpawnInternal`/`Spawn()`), rồi sau đó mới `Initialize()` ở bước 4.
  Chi phí `Awake()` (bao gồm `CacheAll()`) rơi vào lúc spawn (đã được batch theo
  `spawnItemsPerFrame`, mặc định 20/frame).

Vì vậy khi bạn hỏi "tối ưu Awake để giảm thời gian load lúc đầu" — trong build thật
(prebaked path) thì phần lớn cost của `Awake()` **đã trả trước lúc load scene**, không nằm
trong coroutine boot nữa. Cái còn thật sự "ăn" thời gian trong coroutine là vòng lặp
`Initialize()` ở bước 4.

---

## 3. Chi tiết 3 hàm vòng đời trong `CashTowerController`

### 3.1 `Awake()` — chạy **đúng 1 lần** trong toàn bộ vòng đời GameObject

```csharp
protected void Awake()
{
    if (_entityType == Entities.EntityType.None || _entityType == Entities.EntityType.Item)
        _entityType = Entities.EntityType.FinishTower;

    CacheAll();
}
```

- **Lưu ý kỹ thuật**: đây là `protected void Awake()` — **không phải** `override`, và
  **không gọi `base.Awake()`**. `PoolEntity.Awake()` (bị che khuất) có nhiệm vụ
  `if (_transform == null) _transform = transform;`. Vì `Transform` property đã có fallback
  (`_transform != null ? _transform : transform`) nên không có bug chức năng, nhưng:
  - Trường `_transform` sẽ **luôn null** cho object này (khác các class khác như
    `AttackProjectile`, `ArmyUnit` gọi `base.Awake()` đầy đủ).
  - Đây là **inconsistency** so với pattern chung của `PoolEntity`, nên sửa cho gọn/nhất quán
    (dù không gây lỗi).

- **`CacheAll()`** — cache 1 lần duy nhất, đúng tinh thần pooling ("Awake chỉ chạy 1 lần kể cả
  khi object được tái sử dụng qua pool bằng SetActive true/false"):

```csharp
private void CacheAll()
{
    _moneyItems.Clear();
    _moneyCol.Clear();
    _towerVisuals.Clear();

    var all = GetComponentsInChildren<Transform>(true);   // quét TOÀN BỘ hierarchy

    for (int i = 0; i < all.Length; i++)
    {
        var t = all[i];
        var currency = t.GetComponent<CurrencyDropItem>(); // lookup theo từng node
        if (currency != null) { _moneyItems.Add(currency); _moneyCol.Add(t.GetComponent<Collider>()); continue; }

        string n = t.name;
        if (n.StartsWith("finish_tower") || n.StartsWith("tower_m"))
            _towerVisuals.Add(t);
    }

    _dropCurrencyEffect = GetComponentInChildren<DropCurrencyEffect>(true);
}
```

  - `_moneyItems` / `_moneyCol`: danh sách các mảnh tiền (currency) sẽ được "thả ra" khi tháp vỡ
    (dùng trong `DropMoneyItems()`).
  - `_towerVisuals`: các mesh/model của tháp sẽ bị tắt đi khi tháp chết (`BreakTowerVisuals()`),
    xác định bằng **tên GameObject bắt đầu bằng** `"finish_tower"` hoặc `"tower_m"` — đây là
    cách nhận diện dựa theo *naming convention*, không dựa theo tag/component riêng.
  - `_dropCurrencyEffect`: fallback effect khi không có mảnh tiền nào để rớt (`dropped == 0`).

  → Đây là bước "chuẩn bị dữ liệu tĩnh" — không liên quan tới gameplay-state (máu, trạng thái
  chết...), chỉ liên quan tới **cấu trúc hierarchy** vốn không đổi trong suốt vòng đời prefab.
  Đó là lý do nó nằm ở `Awake()` chứ không phải `Initialize()`: nó chỉ cần chạy **1 lần cho mỗi
  instance vật lý**, không cần chạy lại mỗi khi tháp được "hồi sinh" qua pool.

### 3.2 `Initialize()` — được **gọi tường minh** bởi hệ thống quản lý (không phải Unity tự gọi)

```csharp
public override void Initialize()
{
    base.Initialize();                 // ItemUnit.Initialize()
    EnsureCollisionRegistration();

    if (healthComponent != null)
    {
        Pack.Healable = healthComponent;
        ActiveFlags |= CapabilityFlags.Heal;
        Pack.Healable.Initialize();
        RegisterEvents(false);
        RegisterEvents(true);
    }

    _deathHandled = false;
    _lastHitFxFrame = -1;

    if (hitTextFlyEffect != null) hitTextFlyEffect.enabled = true;

    _originalScale = transform.localScale;
}
```

**`base.Initialize()` (`ItemUnit.Initialize()`) làm gì trước đó:**

```csharp
public virtual void Initialize()
{
    InitComponent();     // ConfigureCollider() (no-op ở base) + BuildCapabilityPack()
                          //  + tự gán Pack.Hitable = this nếu chưa có ai implement IHitable khác
    if (_tutElement) _tutElement.Initialize();
    if (Hit flag && Pack.Hitable != this) Pack.Hitable.Initialize();     // (1)
    if (Heal flag) Pack.Healable.Initialize();                           // (2)
    if (Animator flag) Pack.Animator.Initialize();
    Pack.Oscillator?.Initialize();
    if (Effector flag) Pack.Effector.Initialize();
    if (Hit flag && Pack.Hitable != null) CollisionSystem.Register(Pack.Hitable, transform); // (3)
    RegisterEvents(true);                                                // (4)
    WarmupHitTextRuntimeCache();
}
```

`BuildCapabilityPack()` (định nghĩa ở `PoolEntity`) quét **toàn bộ MonoBehaviour trong con**
(`GetComponentsInChildren(true, s_mbBuffer)`), với mỗi component kiểm tra 8 interface
(`IMover, IAttacker, IAnimator, IHealable, IOscillator, IEffector, IHitable, IJumper`) bằng
`is`, được **guard bởi `_capabilityPackBuilt`** nên cũng chỉ thực sự chạy 1 lần/instance
(giống tinh thần `CacheAll`), nhưng chỉ được **kích hoạt lúc `Initialize()`** — nghĩa là nó
nằm trong pha do `GameplayManager` chủ động batch (`initItemsPerFrame`), **không** nằm trong
pha spawn (`spawnItemsPerFrame`). Đây là lý do vì sao game tách `Initialize()` ra khỏi
`Awake()`: để controller cấp cao (GameplayManager) **tự quyết định thời điểm và tốc độ** chạy
phần quét-component (khá tốn vì duyệt toàn bộ MonoBehaviour + nhiều phép `is`), tách biệt
khỏi lúc Instantiate.

**Vấn đề phát hiện: double-work giữa `ItemUnit.Initialize()` và
`CashTowerController.Initialize()`**

1. `EnsureCollisionRegistration()`:

```csharp
private void EnsureCollisionRegistration()
{
    if (_hitComponent == null) return;
    _hitComponent.Initialize();                                  // (A)

    if (Pack.Hitable != null && !ReferenceEquals(Pack.Hitable, _hitComponent))
        CollisionSystem.Unregister(Pack.Hitable);

    Pack.Hitable = _hitComponent;
    ActiveFlags |= CapabilityFlags.Hit;
    CollisionSystem.Register(_hitComponent, transform);           // (B)
}
```

   Nếu `BuildCapabilityPack()` (chạy trong `base.Initialize()` ngay trước đó) **đã** tìm thấy
   `_hitComponent` qua interface `IHitable` (rất có khả năng, vì `_hitComponent` là field
   serialize trỏ tới 1 component con), thì:
   - Dòng `(1)` ở trên (`Pack.Hitable.Initialize()`) **đã** gọi `_hitComponent.Initialize()` rồi.
   - Dòng `(3)` ở trên (`CollisionSystem.Register(Pack.Hitable, transform)`) **đã** đăng ký rồi.
   - Sau đó `EnsureCollisionRegistration()` gọi lại `(A)` và `(B)` **thêm 1 lần nữa** —
     `_hitComponent.Initialize()` chạy 2 lần, `CollisionSystem.Register()` chạy 2 lần cho
     cùng 1 transform (tuỳ implementation của `CollisionSystem`, đây có thể tạo entry trùng
     lặp trong danh sách va chạm, tuy không thấy source `CollisionSystem` để khẳng định
     100%, nhưng gọi `Register` 2 lần cho cùng 1 handle luôn là dấu hiệu dư thừa cần rà lại).

2. Tương tự với `healthComponent`:
   - Nếu `HealthComponent` cũng được `BuildCapabilityPack()` tìm thấy qua `IHealable`, thì
     `Pack.Healable.Initialize()` đã chạy ở dòng `(2)`.
   - `CashTowerController.Initialize()` sau đó **gán lại `Pack.Healable = healthComponent`**
     (nếu khác object, mới có ý nghĩa; nếu cùng object, gán lại vô hại) rồi **gọi
     `Pack.Healable.Initialize()` lần 2**.
   - Rồi gọi `RegisterEvents(false); RegisterEvents(true);` — vì `RegisterEvents(true)`
     ở base (dòng `(4)`) **đã đăng ký sẵn** `OnHealthChange -> HandleHealthChange` (nếu Heal
     flag đã bật lúc đó). Việc `unsub` rồi `sub` lại ở đây là **pattern phòng thủ hợp lý**
     (đảm bảo không có 2 subscription trùng nếu field `healthComponent` khác với cái
     `BuildCapabilityPack` tìm ra), nhưng nếu 2 tham chiếu là cùng 1 object (trường hợp phổ
     biến) thì đây chỉ là công thừa (unsub + sub lại đúng cái vừa có).

→ Đây **không phải là lỗi logic nghiêm trọng** (không gây sai kết quả), nhưng là **double
Initialize/double Register lặp lại y hệt pattern này ở `GoldModifierGate` và
`WeaponModifierGate`** (2 file khác cũng có) — cho thấy đây là 1 pattern copy-paste xuyên
suốt codebase, chứ không phải lỗi riêng của `CashTowerController`. Nếu muốn tối ưu, sửa 1 chỗ
(ví dụ thêm điều kiện "chỉ Init/Register nếu tham chiếu thực sự khác/mới") có thể áp dụng
template tương tự cho các script anh em kia.

**Các biến còn lại reset trong `Initialize()`:**
- `_deathHandled = false;` — cờ chống gọi `HandleDead()`/`DespawnInterval()` nhiều lần.
- `_lastHitFxFrame = -1;` — chống spam hiệu ứng hit nhiều lần trong cùng 1 frame
  (`PlayNonWheelHitEffect` check `_lastHitFxFrame != Time.frameCount`).
- `hitTextFlyEffect.enabled = true;` — bật lại component hiển thị số damage bay lên (có thể
  đã bị tắt khi tháp cũ despawn).
- `_originalScale = transform.localScale;` — lưu scale gốc để `PlayScalePulse()` (hiệu ứng
  phồng lên khi bị đánh) luôn quay về đúng scale ban đầu, quan trọng vì object có thể được
  spawn lại với transform đã bị scale dở dang từ vòng đời trước.

→ Tất cả các biến này đều là **trạng thái gameplay theo từng "vòng đời sử dụng"**
(không phải cấu trúc hierarchy tĩnh) — đúng lý do vì sao chúng nằm ở `Initialize()` chứ
không phải `Awake()`: chúng **phải được reset mỗi lần tháp được đưa vào chơi lại**, còn
`Awake()` chỉ cần chạy 1 lần cho cả vòng đời vật lý của GameObject.

### 3.3 `OnEnable()` / `OnDisable()` — Unity tự gọi mỗi lần `SetActive(true/false)`

```csharp
private void OnEnable()
{
    if (healthComponent != null)
    {
        healthComponent.OnHealthChanged += HandleHealthChanged;      // event RIÊNG cho UI
        HandleHealthChanged(healthComponent.CurrentHealth, healthComponent.MaxHealth);
    }
}

private void OnDisable()
{
    if (healthComponent != null)
        healthComponent.OnHealthChanged -= HandleHealthChanged;
    transform.DOKill();
}
```

- Đây là **event khác** với `OnHealthChange` (không có "d") dùng ở `RegisterEvents`/
  `HandleHealthChange` (gameplay logic — tính chết/sống, hiệu ứng vỡ). `OnHealthChanged` chỉ
  phục vụ **đồng bộ UI text** (`currentHpText`, `maxHpText`) qua `HandleHealthChanged`
  (khác tên, khác mục đích với `HandleHealthChange` override bên dưới).
- Vì đây dùng cặp `OnEnable`/`OnDisable` (Unity đảm bảo gọi theo cặp đối xứng mỗi lần
  Active/Deactive, **không phụ thuộc** vào việc `Initialize()`/`DespawnInterval()` có được
  gọi đúng hay không), nó là **lưới an toàn (safety net)** để đảm bảo subscribe/unsubscribe
  UI-event luôn cân bằng — tránh leak delegate hoặc cập nhật nhầm UI của object đã bị pool
  tái sử dụng, **độc lập** với luồng `Initialize()` do GameplayManager điều khiển.
- **Điểm cần lưu ý (thứ tự thời gian)**: trong luồng spawn runtime,
  `PoolSystem.Spawn()` gọi `gameObject.SetActive(true)` **ngay khi Instantiate xong**, tức
  `OnEnable()` chạy **trước** `Initialize()` (vì `Initialize()` chỉ được gọi ở bước 4, có thể
  trễ vài frame do batching). Nghĩa là:
  - `HandleHealthChanged(healthComponent.CurrentHealth, healthComponent.MaxHealth)` gọi lúc
    `OnEnable` đọc số liệu từ 1 `HealthComponent` **chưa được `Initialize()`** (vì
    `healthComponent.Initialize()` chỉ chạy bên trong `CashTowerController.Initialize()` sau
    này) → hiển thị HP text có thể dựa trên giá trị mặc định/serialize sẵn, chưa phải giá trị
    "thật" của lượt chơi này, cho tới khi `Initialize()` chạy và (giả định) `HealthComponent`
    tự bắn lại `OnHealthChanged` sau khi reset → text được sửa lại đúng. Đây là **giả định**
    vì không có source `HealthComponent`; nếu `HealthComponent.Initialize()` **không** tự bắn
    lại event `OnHealthChanged`, người chơi có thể thấy le lói số HP sai trong 1–vài frame lúc
    tháp mới xuất hiện. Đáng để kiểm tra lại `HealthComponent.cs`.

---

## 4. Vì sao cần tách riêng 3 hàm (không gộp lại)

| Hàm | Ai gọi | Tần suất/vòng đời | Vai trò chính |
|---|---|---|---|
| `Awake()` | Unity, tự động, khi GameObject được tạo | **Đúng 1 lần** cho cả đời GameObject vật lý (kể cả khi pool tái dùng qua SetActive) | Cache cấu trúc hierarchy **không đổi** (danh sách mảnh tiền, visual tháp) |
| `Initialize()` | `MapContentGenerator`/`GameplayManager` gọi **tường minh**, đúng lúc họ chọn | Có thể gọi lại **nhiều lần** trong đời GameObject (mỗi lần tháp được "hồi sinh" cho 1 lượt chơi mới) | Reset **trạng thái gameplay** (máu, cờ chết, event gameplay, đăng ký va chạm) — được **batch theo frame** để tránh giật hình lúc boot |
| `OnEnable()`/`OnDisable()` | Unity, tự động, mỗi lần `SetActive(true/false)` | Mỗi lần bật/tắt object (bao gồm mỗi lần pool cấp phát/thu hồi) | Đảm bảo **đối xứng subscribe/unsubscribe** cho 1 event UI hẹp, độc lập với thời điểm `Initialize()` chạy |

Lý do kiến trúc:
- `Awake()` không thể "biết" khi nào là lúc thích hợp để reset gameplay-state, vì nó chỉ chạy
  1 lần — không phù hợp cho những thứ cần reset mỗi lượt tái sử dụng qua pool.
- `OnEnable()` thì ngược lại: chạy *quá thường xuyên/không kiểm soát được thời điểm* để làm
  những việc nặng (quét hierarchy, quét interface) — nếu để toàn bộ logic `Initialize()`
  vào `OnEnable()`, GameplayManager sẽ **mất khả năng rải việc ra nhiều frame**, vì
  `SetActive(true)` xảy ra ngay trong `PoolSystem.Spawn()` (đồng bộ, không thể "yield" giữa
  chừng) → sẽ gây giật hình đúng vào lúc sinh map (nhiều FinishTower/Item cùng lúc).
- `Initialize()` là 1 **quy ước kiến trúc xuyên suốt cả project** (thấy lại y hệt ở
  `EnemyUnit`, `CharacterUnit`, `WeaponUnit`, `ArmyUnit`, `PlayerArmySystem`,
  `WeaponCraftSystem`...) — nó cho phép hệ thống điều khiển cấp cao (Manager) chủ động chọn
  **thời điểm + tốc độ** khởi tạo, tách biệt hoàn toàn khỏi vòng đời Unity gốc. Xoá riêng nó ở
  `CashTowerController` sẽ phá vỡ tính nhất quán với toàn bộ hệ thống pool/manager khác.

---

## 5. Tổng hợp các điểm có thể tối ưu

1. **Gọi `base.Awake()` trong `Awake()` của `CashTowerController`** — hiện thiếu, khiến field
   `_transform` của `PoolEntity` không bao giờ được gán. Không gây lỗi (nhờ fallback trong
   property `Transform`) nhưng nên sửa cho nhất quán & tránh bug tiềm ẩn nếu sau này code khác
   đọc thẳng field `_transform` thay vì property.

2. **Loại double-Init/double-Register** trong `EnsureCollisionRegistration()` và khối
   `if (healthComponent != null) { ... }`:
   - Chỉ gọi `_hitComponent.Initialize()` / `CollisionSystem.Register()` nếu
     `Pack.Hitable` **chưa từng** là `_hitComponent` (tức tránh lặp lại việc `base.Initialize()`
     vừa làm xong).
   - Chỉ gọi `Pack.Healable.Initialize()` lần nữa nếu `Pack.Healable` thực sự khác
     `healthComponent` trước đó (nghĩa là `BuildCapabilityPack()` đã gán nhầm/gán thiếu).
   - Việc này **áp dụng được luôn cho `GoldModifierGate` và `WeaponModifierGate`** vì cùng
     pattern.
   - Lợi ích: giảm số lần gọi `HealthComponent.Initialize()`/`HitComponent.Initialize()`/
     `CollisionSystem.Register()` từ 2 xuống 1 cho **mỗi FinishTower**, và tránh khả năng
     `CollisionSystem` có entry trùng lặp (ảnh hưởng tới hiệu năng vòng lặp va chạm mỗi frame
     về sau — cái này còn "đắt" hơn cả phần tiết kiệm lúc load, vì nó lặp lại **mỗi frame
     suốt ván chơi**, không chỉ lúc khởi động).

3. **Tối ưu `CacheAll()`**: thay vì
   `GetComponentsInChildren<Transform>(true)` rồi `GetComponent<CurrencyDropItem>()` cho
   từng transform (quét toàn bộ hierarchy, nhiều allocation trung gian), nên:
   - Dùng thẳng `GetComponentsInChildren<CurrencyDropItem>(true)` để lấy danh sách tiền (rồi
     lấy `Collider` từ đúng transform đó) — giảm số lần gọi `GetComponent` từ "mỗi node trong
     cây" xuống "chỉ những node có `CurrencyDropItem`".
   - Với `_towerVisuals` (so tên `"finish_tower"`/`"tower_m"`), nếu có thể, nên gom các mesh
     này dưới 1 GameObject cha riêng (`TowerVisualsRoot`) rồi chỉ lặp qua `childCount` trực
     tiếp của root đó — nhanh hơn quét toàn bộ cây + so chuỗi từng tên.
   - Vì `Awake()`/`CacheAll()` trong build thật thường đã "trả trước" lúc **load scene**
     (đường prebaked), lợi ích thực đo được sẽ nhỏ trên 1 FinishTower/level, nhưng vẫn là tối
     ưu "miễn phí", nên làm nếu có nhiều tháp/nhiều currency-piece trên 1 map.

4. **Gán `EntityType.FinishTower` trực tiếp trên prefab** (Inspector) thay vì check-and-set
   runtime trong `Awake()` — loại bỏ hoàn toàn 1 nhánh so sánh, tuy chi phí không đáng kể,
   nhưng nếu bạn muốn "0 dòng code chạy runtime cho việc này" thì đây là cách. Đổi lại sẽ
   mất luôn cơ chế tự-sửa nếu ai đó quên set trong Inspector — nên cân nhắc, không bắt buộc.

5. **Kiểm tra lại thứ tự `OnEnable` vs `Initialize`**: nếu xác nhận được `HealthComponent`
   không tự bắn lại `OnHealthChanged` sau khi `Initialize()`, nên chủ động gọi lại
   `HandleHealthChanged(...)` ở cuối `CashTowerController.Initialize()` để đảm bảo UI luôn
   đúng ngay sau khi khởi tạo xong, thay vì phụ thuộc hoàn toàn vào `OnEnable` (chạy sớm hơn,
   có thể đọc số liệu chưa chính xác).

---

## 6. Có thể loại bỏ bớt 3 hàm (Awake/Initialize/OnEnable) không?

**Không nên gộp cả 3 làm 1, vì mỗi hàm giải quyết 1 ràng buộc khác nhau:**

- **Không thể bỏ `Initialize()`**: đây là API hợp đồng của toàn bộ hệ thống pool/manager
  (`MapContentGenerator`, `GameplayManager`, `WeaponCraftSystem`... đều gọi `.Initialize()`
  tường minh trên các entity mà chúng quản lý). Bỏ nó nghĩa là phải viết lại toàn bộ cách
  batch-theo-frame lúc boot game (mất khả năng rải `initItemsPerFrame`/frame), và phá vỡ tính
  nhất quán với `EnemyUnit`, `CharacterUnit`, `WeaponUnit`,... vốn cùng theo 1 pattern.

- **Không nên bỏ `OnEnable`/`OnDisable`**: dù chỉ làm 1 việc nhỏ (đồng bộ UI text), đây là cặp
  **duy nhất được Unity đảm bảo đối xứng tuyệt đối** theo `SetActive`, nên nó là lưới an toàn
  tốt nhất cho 1 subscription mà bạn muốn "không bao giờ leak, không phụ thuộc code khác gọi
  đúng thứ tự". Gộp việc này vào `Initialize()`/`DespawnInterval()` (do người khác điều khiển
  thời điểm gọi) sẽ làm tăng rủi ro leak nếu sau này có code path nào `SetActive` object mà
  quên gọi `Initialize()`/despawn đúng cách.

- **`Awake()` có thể gộp phần "set EntityType" ra khỏi code** (đẩy lên prefab, mục 5.4), nhưng
  **`CacheAll()` nên giữ nguyên vị trí Awake** — đây chính là mẫu hình chuẩn cho pooling:
  "cái gì không đổi giữa các lần tái sử dụng → cache 1 lần ở Awake; cái gì phải reset mỗi lần
  tái sử dụng → đặt ở Initialize."

**Tóm lại**: 3 hàm đang phục vụ 3 mục đích không thể thay thế cho nhau (tần suất chạy khác
nhau, ai kiểm soát thời điểm gọi khác nhau, và mức độ "phải đối xứng" khác nhau). Phần có thể
cắt giảm thực sự không phải là *số lượng hàm vòng đời*, mà là **phần công việc trùng lặp bên
trong `Initialize()`** (double Init/double Register được liệt kê ở mục 5.2) — đó mới là chỗ
"thừa" thật sự, có thể xoá mà không đổi hành vi, và tiết kiệm được cả ở giai đoạn load lẫn
trong suốt thời gian chơi (vì `CollisionSystem` có thể phải duyệt qua entry trùng mỗi frame).

---

## 7. Giới hạn của phân tích này

Các nhận định sau đây **dựa trên suy luận từ cách gọi hàm**, không phải từ source thật (vì
các file này không nằm trong bộ tài liệu được cung cấp) — nên kiểm tra lại trước khi sửa:

- `HealthComponent`: có tự bắn `OnHealthChanged` sau `Initialize()`/`SetMaxHealth()` không?
- `HitComponent.Initialize()`: gọi 2 lần liên tiếp có gây side-effect gì (VD: reset thêm state,
  hay chỉ đơn thuần gán field) không?
- `CollisionSystem.Register()`: có tự khử trùng lặp theo instance không, hay sẽ tạo 2 entry
  cho cùng 1 transform nếu gọi 2 lần?
- `CurrencyDropItem`, `DropCurrencyEffect`: số lượng thực tế trên 1 prefab FinishTower (ảnh
  hưởng mức độ đáng-tối-ưu của `CacheAll()`).

---
---

# Phần 2 — PlayerArmySystem

> Phạm vi: `PlayerArmySystem.cs`, và các script nó gọi trực tiếp lúc boot:
> `GameplayManager.cs` (`CoBootAndIntroSequence`, `StartGame`), `CharacterUnit.cs`,
> `PlayerArmyEffectSystem.cs`, `ArmyUnit.cs` (tham chiếu chéo, không phải thành phần chính của
> `PlayerArmySystem` nhưng cùng namespace `PlayerArmy`), `PoolSystem.cs`.
> Các class không có source được cung cấp nhưng bị gọi tới (`InputManager`, `CollisionSystem`,
> `EnemyProjectileSystem`, `CombatSystem`, `CharacterListDataSO`) chỉ được suy luận qua cách gọi.

`PlayerArmySystem` **khác hẳn mô hình vòng đời của `CashTowerController`**: đây không phải là
1 trong hàng chục "item" được Pool tái sử dụng qua `SetActive(true/false)`, mà là **1 instance
duy nhất cho cả trận** (`GameplayManager.ActiveArmy`), được **Destroy hẳn và Instantiate lại**
mỗi khi bắt đầu 1 lượt chơi mới:

```csharp
// GameplayManager.CoBootAndIntroSequence() / SpawnPlayerArmy()
if (ActiveArmy != null) Destroy(ActiveArmy.gameObject);
ActiveArmy = Instantiate(playerArmyPrefab, transform);
...
ActiveArmy.Initialize();
```

Vì mô hình vòng đời khác (destroy/instantiate thay vì enable/disable qua pool), cặp hàm
"bảo đảm đối xứng" cũng khác: `CashTowerController` dùng `OnEnable`/`OnDisable` (vì bị bật/tắt
nhiều lần); `PlayerArmySystem` dùng `Start`/`OnDestroy` (vì chỉ thực sự "sống" đúng 1 lần cho
tới khi bị Destroy hẳn).

---

## 8. Vị trí trong luồng boot Playable

Trong `GameplayManager.CoBootAndIntroSequence()`, thứ tự chạy đúng như sau (rất quan trọng để
hiểu Awake/Initialize/Start chạy khi nào):

1. `ActiveArmy = Instantiate(playerArmyPrefab, transform);` → **`Awake()` chạy ngay lập tức**
   (đồng bộ, trong lệnh `Instantiate`), vì object mới tạo mặc định active.
2. `mapGenerator.BindWheelTransform(...)`, `CameraManager...SetPlayerTransform(...)`.
3. `ActiveArmy.Initialize();` → gọi **tường minh**, ngay trong cùng frame, **trước khi** Unity
   kịp gọi `Start()` tự động (Unity luôn gọi `Start()` ngay trước `Update` đầu tiên, sau khi
   toàn bộ `Awake()` của frame đó đã xong — nên `Initialize()` gọi tay ở đây chắc chắn chạy
   **trước** `Start()` tự động của Unity).
4. `seedCards = ...`; `ActiveArmy.AddCards(seedCards, CardSpawnEffectType.DropWithoutAction);`
   → **spawn ngay các unit khởi đầu, đồng bộ, không rải theo frame** (khác hẳn cách
   `MapContentGenerator`/`GameplayManager` rải `Initialize()` của content theo
   `initItemsPerFrame` ở Phần 1!). Xem mục 10 bên dưới.
5. (Các bước prewarm VFX khác...)
6. `yield return StartCoroutine(ActiveArmy.PrewarmArmyPrefabsAsync(seedCards, ...));`
   → **prewarm pool CHẠY SAU KHI đã spawn** unit khởi đầu ở bước 4. Nghĩa là: các unit đầu
   tiên xuất hiện trên tháp/army **không được hưởng lợi từ prewarm** — chúng buộc phải
   `Instantiate()` "sống" (pool rỗng lúc đó). Prewarm chỉ có tác dụng cho các lần spawn
   **về sau** (khi người chơi ăn thêm card trong lúc chơi).
7. `ActiveArmy.SetIntroRun();` rồi di chuyển tới `targetPos` bằng vòng lặp
   `while (Vector3.Distance(...) > 0.05f) { ...; yield return null; }`.
8. `ActiveArmy.SetIdle();`

→ Điểm mấu chốt: **Unity's `Start()` tự động sẽ chạy vào 1 thời điểm nào đó giữa các bước
này** (ngay trước frame Update đầu tiên xử lý xong `Awake` của frame) — do `Initialize()` đã
được gọi tay ở bước 3 (rất sớm trong cùng frame), khi `Start()` thật sự thực thi thì
`_isInitialized == true` sẵn rồi, nên `Start()` chỉ còn làm 1 việc: `SubscribeWeaponChange()`.

---

## 9. Chi tiết các hàm vòng đời trong `PlayerArmySystem`

### 9.1 `Awake()` — Unity tự gọi, đúng lúc `Instantiate`

```csharp
private void Awake()
{
    ResolveDependencies();
    CacheDefaultState();
    ResetRuntimeSpawnState();
}
```

### 9.2 `Initialize()` — gọi tường minh bởi `GameplayManager`, có cờ chống gọi lại

```csharp
private bool _isInitialized = false;

public void Initialize()
{
    if (_isInitialized) return;
    _isInitialized = true;

    ResolveDependencies();
    CacheDefaultState();
    ClearSceneUnits();      // <-- điểm khác biệt duy nhất so với Awake()
    ResetRuntimeSpawnState();
}
```

**Nhận xét quan trọng: `Initialize()` gọi lại y hệt 3/4 việc mà `Awake()` vừa làm xong**
(`ResolveDependencies()`, `CacheDefaultState()`, `ResetRuntimeSpawnState()`), chỉ thêm đúng 1
dòng mới là `ClearSceneUnits()`. Vì `Initialize()` luôn được gọi ngay sau `Instantiate` trong
cùng frame (bước 3 ở mục 8), **`Awake()` và `Initialize()` chắc chắn chạy liên tiếp nhau cho
mọi lượt chơi**, khiến phần việc trùng lặp này chạy **2 lần liên tục** mỗi khi 1 `ActiveArmy`
mới được tạo. Xét lợi/hại của từng hàm được gọi trùng:

- **`ResolveDependencies()`** — mỗi dòng đều có null-check trước khi gán
  (`if (bodyRoot == null) bodyRoot = transform;`...) nên gọi lần 2 gần như miễn phí (chỉ tốn
  vài phép so sánh null), **không phải vấn đề thực sự**.
  ```csharp
  private void ResolveDependencies()
  {
      if (bodyRoot == null) bodyRoot = transform;
      if (inputManager == null) inputManager = InputManager.Instance;
      if (effectSystem == null) effectSystem = GetComponentInChildren<PlayerArmyEffectSystem>(true);
      if (characterUnits == null) characterUnits = new List<CharacterUnit>();
  }
  ```
- **`CacheDefaultState()`** — **không có guard**, tính toán lại từ đầu mỗi lần gọi:
  ```csharp
  private void CacheDefaultState()
  {
      var root = GetBodyRoot();
      _targetX = root.localPosition.x;
      _currentForwardSpeed = fallbackForwardSpeed;
      _baseAttackInterval = Mathf.Max(0.05f, attackInterval);
      _baseProjectileDuration = Mathf.Max(0.05f, projectileDuration);
      _fireRateBonusPoints = 0;
      _baseFireRange = projectileDistance;
      _fireRangeBonus = 0f;
      _damageBonusPoints = 0;
      RefreshCombatDamage();
      RefreshFireRange();
  }
  ```
  Vì các field nguồn (`attackInterval`, `projectileDistance`, `fallbackForwardSpeed`...) không
  đổi giữa 2 lần gọi (không có code nào chỉnh chúng ở giữa `Awake()` và `Initialize()`), kết
  quả tính ra **giống hệt lần trước** → đây là phép tính **thừa hoàn toàn**, dù chi phí tuyệt
  đối rất nhỏ (vài phép gán/so sánh số thực) vì `PlayerArmySystem` chỉ có **1 instance/trận**
  (khác `CashTowerController` có thể có nhiều instance).
- **`ResetRuntimeSpawnState()`** — chỉ `Clear()` 2 collection đang rỗng, gọi lại vô hại:
  ```csharp
  private void ResetRuntimeSpawnState()
  {
      _activeSpawnedUnits.Clear();
      _nextAttackTimes.Clear();
  }
  ```

**`ClearSceneUnits()`** — việc thật sự quan trọng, **chỉ nằm trong `Initialize()`**:
```csharp
private void ClearSceneUnits()
{
    var sceneUnits = GetComponentsInChildren<CharacterUnit>(true);  // quét hierarchy con
    for (int i = 0; i < sceneUnits.Length; i++)
    {
        var unit = sceneUnits[i];
        if (unit == null) continue;
        unit.RecycleImmediate(false);   // trả từng unit có sẵn (nếu có) về pool
    }
    characterUnits.Clear();
    _activeSpawnedUnits.Clear();
    _nextAttackTimes.Clear();
}
```
Dọn sạch mọi `CharacterUnit` có sẵn trong hierarchy con (phòng trường hợp prefab được set up
sẵn vài unit mẫu trong Editor, hoặc leftover nào đó) trước khi bắt đầu spawn thật. Đây là lý
do **thực sự cần `Initialize()` tách khỏi `Awake()`**: việc "dọn dẹp trạng thái cũ trước khi
bắt đầu 1 trận mới" phải là hành động **được kiểm soát bởi `GameplayManager`** (biết chính xác
khi nào là "bắt đầu 1 trận mới thật sự"), chứ không thể phó mặc cho `Awake()` (chạy tự động
ngay khi Instantiate, không phân biệt được "đây có phải lúc nên dọn dẹp chưa").

### 9.3 `Start()` — Unity tự gọi, 1 lần duy nhất trước Update đầu tiên

```csharp
private void Start()
{
    if (!_isInitialized)
    {
        Initialize();
        currentState = PlayerArmyState.Active;
        SubscribeWeaponChange();
    }
    else
    {
        SubscribeWeaponChange();
    }
}
```

- Nhánh `if (!_isInitialized)` là **fallback** cho trường hợp `PlayerArmySystem` được dùng
  **độc lập, không qua `GameplayManager.CoBootAndIntroSequence`** (ví dụ: test scene riêng,
  hoặc kéo prefab vào scene rồi Play trực tiếp mà không có `GameplayManager` chủ động gọi
  `Initialize()`). Trong thực tế boot chuẩn (mục 8), nhánh này **không bao giờ chạy** vì
  `Initialize()` đã được gọi tay từ trước.
- `currentState = PlayerArmyState.Active;` trong nhánh fallback thật ra **trùng với giá trị
  default đã khai báo sẵn ở field**:
  ```csharp
  private PlayerArmyState currentState = PlayerArmyState.Active;
  ```
  → dòng gán lại này chỉ thật sự có ý nghĩa nếu `currentState` từng bị đổi bởi code khác
  trước khi `Start()` chạy (không thấy trường hợp nào trong code được cung cấp), nên gần như
  luôn là gán lại giá trị đã có sẵn.
- **`SubscribeWeaponChange()` được gọi ở CẢ 2 nhánh** — tức luôn chạy đúng 1 lần
  (vì `Start()` tự thân chỉ chạy 1 lần theo vòng đời Unity), **không có guard unsub-trước-sub**
  như pattern `RegisterEvents()` bên `ItemUnit`:
  ```csharp
  private void SubscribeWeaponChange()
  {
      var manager = GameplayManager.Instance;
      if (manager != null) manager.OnWeaponChange += OnWeaponChanged;
      GameEventBus.OnAddWheelCard += HandleAddArmyCardEvent;
  }
  ```
  Điều này **an toàn trong mô hình vòng đời hiện tại** (destroy/instantiate mỗi trận, `Start()`
  chỉ chạy 1 lần/instance, không có kịch bản gọi lại) nhưng **khác với triết lý phòng thủ**
  dùng ở `ItemUnit.RegisterEvents` (luôn unsub trước khi sub). Nếu sau này có ai thêm 1 code
  path gọi `SubscribeWeaponChange()` thủ công thêm lần nữa (ví dụ lúc `ContinueGame()` hay
  resume), sẽ có nguy cơ đăng ký trùng sự kiện — nên cân nhắc thêm `-=` trước `+=` cho nhất
  quán & an toàn về sau, dù hiện tại chưa phải bug.

### 9.4 `OnDestroy()` — cặp đối xứng với `Start()` (thay vì `OnEnable`/`OnDisable`)

```csharp
private void OnDestroy()
{
    UnsubscribeWeaponChange();
}

private void UnsubscribeWeaponChange()
{
    var manager = GameplayManager.Instance;
    if (manager != null) manager.OnWeaponChange -= OnWeaponChanged;
    GameEventBus.OnAddWheelCard -= HandleAddArmyCardEvent;
}
```
Vì `ActiveArmy` bị **Destroy thật sự** (không phải `SetActive(false)`) mỗi khi trận đấu kết
thúc/khởi động lại (`Destroy(ActiveArmy.gameObject)` trong `SpawnPlayerArmy`/
`CoBootAndIntroSequence`), cặp hàm bảo vệ đối xứng đúng ở đây là `Start()`/`OnDestroy()`
(mỗi cái chạy đúng 1 lần cho cả vòng đời instance), **không phải** `OnEnable`/`OnDisable` như
ở `CashTowerController` (vì đối tượng đó được tái dùng qua `SetActive`, cần cặp hàm chạy
**nhiều lần** để khớp với việc bật/tắt lặp lại). Đây là điểm khác biệt kiến trúc quan trọng
nhất giữa 2 script, minh hoạ nguyên tắc: **"chọn cặp hàm đối xứng theo đúng mô hình vòng đời
của entity"**, không phải cứ máy móc dùng `OnEnable`/`OnDisable` cho mọi thứ.

---

## 10. Điểm nóng hiệu năng thật sự: spawn unit khởi đầu KHÔNG được rải theo frame

Khác với content items ở Phần 1 (được `MapContentGenerator`/`GameplayManager` rải
`Initialize()` theo `initItemsPerFrame`), việc spawn unit khởi đầu của army lại chạy
**đồng bộ, không yield**, trong cùng 1 frame:

```
GameplayManager.CoBootAndIntroSequence()
 └─ ActiveArmy.AddCards(seedCards, ...)
     └─ SpawnUnits(level, amount, ...)          // for i in 0..amount: SpawnCharacterUnit(...)
         └─ SpawnCharacterUnit → CreateRuntimeCharacterUnit
             └─ entry.CharacterPrefab.Spawn(...)      // PoolSystem.Spawn — pool RỖNG lúc này
                 └─ InitializeRuntimeUnit(unit, level, ...)
                     └─ unit.Initialize(level, true)   // CharacterUnit tự Awake capability pack,
                                                        // RegisterEvents, CombatSystem.Register...
```

- `SpawnUnits()` lặp `for (int i = 0; i < spawnCount; i++) SpawnCharacterUnit(...)` **không có
  `yield return`** — toàn bộ card khởi đầu (`seedCards`, thường 1–3 unit tuỳ cấu hình
  `DataManager.PlayerData.WheelData.CardCount`) được tạo **trong cùng 1 frame**.
- Đây xảy ra **trước** bước `PrewarmArmyPrefabsAsync` (mục 8, bước 6), nên **pool rỗng** với
  prefab nhân vật đó → mỗi unit khởi đầu phải trả **phí `Instantiate()` đầy đủ** (bao gồm
  `Awake()` của `CharacterUnit`, `BuildCapabilityPack()` quét toàn bộ MonoBehaviour con...),
  không có instance nào có sẵn để lấy ra từ `Pool.Inactive`.
- Với số lượng unit khởi đầu nhỏ (1–3, theo config mặc định), chi phí này thường **không đáng
  kể** và không cần rải theo frame. Nhưng nếu sau này thiết kế game đổi để cho phép **nhiều
  unit khởi đầu hơn** (`CardCount` lớn, hoặc `initialCards` cấu hình `Amount` lớn trong
  Inspector), đoạn này **sẽ không tự động được bảo vệ khỏi giật hình** như content items ở
  Phần 1 — vì không có cơ chế batch tương tự `initItemsPerFrame`/`spawnItemsPerFrame` áp dụng
  cho `SpawnUnits()`.

**Đề xuất**: nếu muốn "chống giật hình" cho trường hợp số card khởi đầu lớn, có thể:
- Đảo thứ tự: gọi `PrewarmArmyPrefabsAsync` **trước** `AddCards` (đánh đổi: unit khởi đầu xuất
  hiện trễ hơn vài frame vì phải chờ prewarm xong, nhưng khi xuất hiện sẽ lấy từ pool đã
  sẵn thay vì Instantiate tươi).
- Hoặc thêm 1 biến thể `SpawnUnitsAsync`/`AddCardsAsync` có `yield return null` mỗi N unit,
  tương tự pattern `initItemsPerFrame` đã dùng cho content — áp dụng khi `amount` vượt 1
  ngưỡng nào đó (ví dụ > 8).

---

## 11. So sánh nhanh với Phần 1 (CashTowerController)

| | CashTowerController | PlayerArmySystem |
|---|---|---|
| Mô hình vòng đời | 1 trong nhiều instance, tái dùng qua Pool (`SetActive`) | Instance **duy nhất/trận**, Destroy & Instantiate lại mỗi trận |
| Hàm chạy 1 lần/instance vật lý | `Awake()` | `Awake()` |
| Hàm reset gameplay-state, do Manager gọi tay | `Initialize()` (có thể gọi lại nhiều lần lý thuyết, nhưng trong code hiện tại chỉ gọi 1 lần/instance) | `Initialize()` (được guard cứng bằng `_isInitialized`, **chỉ chạy 1 lần/instance**, không có input để gọi lại) |
| Cặp hàm bảo vệ đối xứng | `OnEnable` / `OnDisable` (khớp `SetActive` lặp lại) | `Start()` / `OnDestroy()` (khớp việc chỉ "sống" 1 lần) |
| Trùng lặp phát hiện được | Double-Init/Register (`HitComponent`, `HealthComponent`) giữa `base.Initialize()` và code riêng | Trùng 3/4 dòng giữa `Awake()` và `Initialize()` (`ResolveDependencies`, `CacheDefaultState`, `ResetRuntimeSpawnState`) |
| Điểm nóng hiệu năng lúc boot | Quét hierarchy trong `CacheAll()` + `BuildCapabilityPack()` (đã được batch theo frame bởi Manager) | Spawn unit khởi đầu **không batch theo frame**, chạy trước cả bước prewarm pool |

---

## 12. Có thể gộp/bỏ bớt Awake/Initialize/Start/OnDestroy của `PlayerArmySystem` không?

Kết luận tương tự Phần 1, nhưng lý do cụ thể hơn cho từng hàm:

- **Không nên bỏ `Initialize()`**: nó là điểm neo duy nhất mà `GameplayManager` dùng để chủ
  động chọn thời điểm "bắt đầu 1 trận mới" (bao gồm `ClearSceneUnits()` — việc mà `Awake()`
  không thể tự quyết định được nên làm hay chưa nên làm).
- **Không nên bỏ `Start()`**: dù trong luồng boot chuẩn nó gần như "không làm gì mới"
  (`_isInitialized` đã true), nó vẫn là **lưới an toàn fallback** cho việc dùng
  `PlayerArmySystem` độc lập (test scene, prefab preview...), và là nơi tự nhiên để đặt
  `SubscribeWeaponChange()` — Unity đảm bảo `Start()` luôn chạy sau khi mọi `Awake()` trong
  scene đã xong, an toàn hơn gọi `SubscribeWeaponChange()` ngay trong `Awake()`/`Initialize()`
  (lúc đó `GameplayManager.Instance` có thể chưa kịp `Awake()` xong nếu thứ tự script
  execution order không đảm bảo).
- **Không nên bỏ `OnDestroy()`**: là cặp bắt buộc để tránh leak subscription trên
  `GameplayManager.OnWeaponChange`/`GameEventBus.OnAddWheelCard` (2 sự kiện **static/singleton**
  — nếu không unsub, `ActiveArmy` cũ đã bị Destroy vẫn có thể bị gọi callback từ event tĩnh,
  gây `MissingReferenceException` hoặc null access).
- **Phần thật sự nên dọn**: gộp bớt việc **trùng lặp giữa `Awake()` và `Initialize()`** — vì
  trong luồng boot chuẩn, `Initialize()` LUÔN được gọi ngay sau `Awake()` trong cùng frame
  (mục 8, bước 1 và 3), có thể tái cấu trúc thành:
  ```csharp
  private void Awake()
  {
      ResolveDependencies();   // vẫn cần ở Awake để phục vụ OnValidate()/các hàm gọi sớm khác
  }

  public void Initialize()
  {
      if (_isInitialized) return;
      _isInitialized = true;

      ResolveDependencies();   // vẫn giữ (rẻ, đề phòng Initialize() được gọi mà chưa qua Awake — VD unit test)
      CacheDefaultState();     // chỉ cần chạy Ở ĐÂY, bỏ khỏi Awake()
      ClearSceneUnits();
      ResetRuntimeSpawnState();
  }
  ```
  tức là **bỏ `CacheDefaultState()` và `ResetRuntimeSpawnState()` ra khỏi `Awake()`**, chỉ giữ
  chúng trong `Initialize()` — vì trong thực tế 100% trường hợp boot chuẩn, `Awake()` xong là
  `Initialize()` chạy ngay, nên phần việc ở `Awake()` chưa từng "được dùng riêng" trước khi
  `Initialize()` ghi đè lại. Rủi ro duy nhất: nếu có code nào đó đọc `_targetX`/
  `_baseAttackInterval`/... **giữa lúc `Awake()` xong và trước khi `Initialize()` chạy** (ví
  dụ 1 script khác có `[DefaultExecutionOrder]` sớm hơn và đọc state của `PlayerArmySystem`
  ngay trong `Awake()` của chính nó) thì sẽ đọc phải giá trị chưa được cache — nhưng theo code
  hiện tại, không thấy trường hợp nào đọc các field này trước `Initialize()`, nên rủi ro thấp.
