# Phân tích package Animation-Instancing

> Phạm vi: mã nguồn trong `Assets/ThirdParty/Animation-Instancing`. Tài liệu phản ánh implementation hiện có, không phải mô tả chung về GPU instancing.

## 1. Mục tiêu và nguyên lý

Package chuyển phần skinning của nhân vật từ `SkinnedMeshRenderer`/`Animator` chạy độc lập trên CPU sang một pipeline dựa trên **vertex animation texture (VAT)**:

1. Ở Editor, `AnimationGenerator` sample từng frame của `Animator`.
2. Mỗi bone ở mỗi frame được chuyển thành skin matrix và ghi vào texture `RGBAHalf`.
3. Metadata animation, root motion, event, extra bone và raw texture được ghi chung vào file `<PrefabName>.bytes`.
4. Ở runtime, shader đọc skin matrix từ texture, skin vertex trên GPU, còn `AnimationInstancingMgr` gom các instance cùng mesh/material/animation texture thành các lệnh `Graphics.DrawMeshInstanced`.

Kết quả là nhiều nhân vật có thể dùng chung mesh, material và bone texture. CPU không còn phải đánh giá `Animator` và `SkinnedMeshRenderer` riêng cho từng nhân vật ở runtime.

```text
Animator + SkinnedMeshRenderer (Editor)
              |
              v
AnimationGenerator: sample frame -> skin matrices -> bone texture + .bytes
              |
              v
AnimationManager: load .bytes/AssetBundle -> AnimationInfo + Texture2D
              |
              v
AnimationInstancingMgr: cập nhật frame/transform/culling/LOD -> batch arrays
              |
              v
Graphics.DrawMeshInstanced + AnimationInstancingBase.cginc
              |
              v
Vertex shader đọc ma trận bone từ texture và skinning từng vertex
```

## 2. Các thành phần chính

| Thành phần | Vai trò thực tế |
|---|---|
| `Editor/AnimationGenerator.cs` | Tool bake. Duyệt state machine, sample `Animator` ở FPS cấu hình, tạo bone texture và file nhị phân. |
| `Scripts/AnimationManager.cs` | Cache/lấy `AnimationInfo`, extra-bone info và texture animation từ file cục bộ hoặc AssetBundle. |
| `Scripts/AnimationInstancing.cs` | Component trên prefab/instance; quản lý state animation, transition, root-motion option, LOD, event và attachment. |
| `Scripts/AnimationInstancingMgr.cs` | Singleton điều phối mỗi frame: culling, LOD, root motion, batch data và gọi draw API. |
| `Shader/AnimationInstancingBase.cginc` | Vertex skinning bằng bone texture, nội suy hai frame liên tiếp và blend vị trí giữa hai animation. |
| `Scripts/runtimeHelper.cs` | Gộp skeleton/bind pose của các renderer và hỗ trợ chuyển matrix sang quaternion. |
| `Editor/buidlBundle.cs` | Build AssetBundle `AnimationTexture` rồi copy sang `StreamingAssets/AssetBundle`. |

## 3. Dữ liệu bake và định dạng `.bytes`

`AnimationGenerator.SaveAnimationInfo` ghi tuần tự các khối sau:

1. Số animation.
2. Với mỗi animation: `animationName`, vị trí frame đầu (`animationIndex`), index texture, tổng frame, FPS, root-motion flag, `WrapMode`, vận tốc tuyến tính/góc từng frame (nếu có root motion), và animation events.
3. Cờ attachment; nếu bật thì ghi tên extra bone và bind pose tương ứng.
4. Số bone texture; với mỗi texture ghi `width`, `height`, số byte và raw pixel data.

`AnimationManager.ReadAnimationInfo`, `ReadExtraBoneInfo` và `AnimationInstancingMgr.ReadTexture` phải đọc đúng thứ tự này. Định dạng không có header/version/checksum, vì vậy thay đổi schema phải đi kèm versioning hoặc bắt buộc bake lại toàn bộ dữ liệu cũ.

### Tổ chức bone texture

- Mỗi bone dùng 4 texel về mặt layout, nhưng shader chỉ lưu/đọc ba row đầu và cố định row thứ tư là `(0, 0, 0, 1)`.
- Một frame chiếm block `4 x boneCount`: `textureBlockWidth = 4`, `textureBlockHeight = boneCount`.
- `AnimationInfo.animationIndex + curFrame` là frame index toàn cục trong texture.
- Texture dùng `FilterMode.Point`; nội suy frame được shader thực hiện thủ công bằng `lerp`, không dựa vào texture filtering.
- Generator chọn texture vuông 64, 128, 256, 512 hoặc 1024. Khi không đủ, animation được trải qua nhiều texture và `textureIndex` chọn texture chứa frame tương ứng.

## 4. Luồng khởi tạo runtime

1. Prefab có `AnimationInstancing`, `prototype` trỏ về prefab đã bake và các renderer gốc.
2. `AnimationInstancing.Start` tắt `SkinnedMeshRenderer`/`MeshRenderer`; nếu dùng instancing thì cũng tắt `Animator`.
3. `InitializeAnimation` gọi `AnimationManager.FindAnimationInfo(prototype, this)`.
4. `Prepare` gộp bones của các `SkinnedMeshRenderer`, đọc extra bones (nếu có), rồi gọi `AnimationInstancingMgr.AddMeshVertex`.
5. Manager tạo hoặc tái sử dụng `VertexCache`: mesh, bone weight, bone index, material block và package draw.
6. `SetupAdditionalData` chép bone weights vào `mesh.colors`, bone indices vào UV channel 2, vì shader cần hai kênh này để skinning.
7. `PlayAnimation(0)` đặt state ban đầu.

Với build mobile/AssetBundle, `FindAnimationInfo` có thể trả `null` khi bundle chưa load xong. Manager đưa request vào hàng đợi và gọi `Prepare` sau khi asset đã tải.

## 5. Luồng mỗi frame

`AnimationInstancingMgr.Update` gọi `ApplyBoneMatrix()` rồi `Render()`.

### 5.1 `ApplyBoneMatrix`

Với mỗi instance đang phát:

- Áp dụng root motion nếu `applyRootMotion` được bật.
- `UpdateAnimation` tăng `curFrame` theo `playSpeed * FPS`, xử lý `Loop`, `PingPong`, `Once`, cập nhật attachment và gửi event.
- Cập nhật `BoundingSphere` cho `CullingGroup`.
- Nếu visible, chọn LOD và chọn `animationTextureIndex` (attachment dùng texture của parent).
- Ghi `localToWorldMatrix`, `frameIndex`, `preFrameIndex`, `transitionProgress` vào slot của package batch.

`frameIndex` là `animationIndex + curFrame`; shader lấy frame nguyên và frame kế tiếp để nội suy pose. Khi `CrossFade`, manager gửi thêm frame của animation trước và tỷ lệ transition.

### 5.2 `Render`

Manager duyệt `VertexCache -> MaterialBlock -> animation texture -> InstancingPackage`. Mỗi package gọi:

```csharp
Graphics.DrawMeshInstanced(mesh, subMeshIndex, material, matrices, count,
    propertyBlock, shadowCastingMode, receiveShadow, layer);
```

`MaterialPropertyBlock` mang các mảng per-instance: `frameIndex`, `preFrameIndex`, `transitionProgress`; mảng transform được truyền trực tiếp vào `DrawMeshInstanced`.

Mặc định một package chứa 200 instance (`InstancingSizePerPackage`). Khi đầy, manager tạo package mới. Giá trị này phải luôn không vượt giới hạn Unity của `Graphics.DrawMeshInstanced` (1023 instance mỗi lệnh).

## 6. Shader skinning và transition

`AnimationInstancingBase.cginc` đọc:

- Bone weights từ `COLOR`.
- Bone indices từ `TEXCOORD2`.
- Frame data từ uniform (fallback/GLES) hoặc Unity instancing buffer.
- Bone matrix từ `_boneTexture` qua `loadMatFromTexture`.

Ở vertex pass thường, shader:

1. Tính skin matrix có trọng số cho frame hiện tại và frame kế.
2. Nội suy position, normal và tangent theo phần thập phân của frame.
3. Nếu đang transition, blend **position** với pose animation trước theo `transitionProgress`.

Điều này là GPU skinning theo bone matrix bake sẵn, không phải mesh vertex position bake sẵn. Chất lượng pose phụ thuộc trực tiếp vào FPS bake và số bone influence được giữ (`bonePerVertex` từ 1 đến 4).

## 7. Culling, LOD, root motion và attachment

### Culling

Package dùng `CullingGroup` với `BoundingSphere[5000]` và `Camera.main`. Callback chỉ thay đổi cờ `instance.visible`; instance invisible không được đẩy vào batch draw. Culling diễn ra CPU-side theo sphere, không có GPU frustum/occlusion culling hoặc indirect draw.

### LOD

`AnimationInstancing.UpdateLod` kiểm tra mỗi 0,5 giây và dùng ngưỡng hard-code trên bình phương khoảng cách: `< 50`, `< 500`, còn lại. `LODGroup` chỉ cung cấp tập renderer cho từng level; phần trăm screen-relative transition và fade của `LODGroup` không được dùng.

### Root motion

Generator ép `Animator.applyRootMotion = true`, lưu `Animator.velocity` và `angularVelocity` của từng frame. Runtime nội suy hai velocity mẫu, nhân với `Time.deltaTime`, rồi cập nhật transform. Root motion chỉ được áp dụng khi `AnimationInstancing.applyRootMotion` là `true`.

### Attachment

`Attach(boneName, attachment)` gắn mesh tĩnh vào bone của parent. Tool bake có thể lưu extra bone/bind pose để attachment có điểm gắn ngoài skeleton chính. Code hiện tại ghi rõ attachment `SkinnedMeshRenderer` chưa được hỗ trợ đầy đủ; trường hợp ổn định là `MeshRenderer + MeshFilter` như vũ khí/phụ kiện tĩnh.

## 8. Điều kiện tích hợp

1. Có `AnimationInstancingMgr` và `AnimationManager` trong scene trước khi instance được tạo.
2. `Camera.main` phải tồn tại trước `AnimationInstancingMgr.OnEnable`.
3. Prefab có `AnimationInstancing`, `Animator`, renderer hợp lệ và `prototype` đúng prefab đã bake.
4. Material phải dùng shader có include `AnimationInstancingBase.cginc` và hỗ trợ instancing. Package có các shader Diffuse/Bumped Specular cũ.
5. Mở `AnimationInstancing/Animation Generator`, chọn prefab, chọn clip/FPS, Generate để tạo `Assets/AnimationTexture/<PrefabName>.bytes`.
6. Khi chạy qua AssetBundle: menu `Custom Editor/AssetBundle/BuildAssetBundle`, sau đó load file `StreamingAssets/AssetBundle/animationtexture` trước khi tạo instance.

## 9. Điểm mạnh và giới hạn kỹ thuật

### Phù hợp

- Crowd lớn có cùng model/skeleton/animation set: lính, NPC nền, zombie, động vật bầy đàn.
- Animation state đơn giản, lặp nhiều và không cần xử lý bone runtime riêng cho từng unit.
- Dự án Built-in Render Pipeline/Unity API cũ, nơi shader package còn tương thích.

### Không phù hợp trực tiếp

- Character chính cần Animator Controller hoàn chỉnh, IK, Avatar Mask runtime, additive layer, ragdoll, bone gameplay/hitbox chính xác hoặc animation blending phức tạp.
- Asset có Blend Tree: `AnalyzeStateMachine` chỉ bake `AnimationClip` trực tiếp; state có `BlendTree` bị bỏ qua trong đường bake chính.
- URP/HDRP/SRP hiện đại nếu chưa viết lại shader và kiểm tra lại toàn bộ pass/shadow/instancing.

## 10. Rủi ro và nợ kỹ thuật cần biết

| Vấn đề | Bằng chứng trong mã | Tác động/khuyến nghị |
|---|---|---|
| Giới hạn 5000 sphere, không kiểm tra tràn | `boundingSphere = new BoundingSphere[5000]` | Trên 5000 instance sẽ lỗi. Dùng `List`/capacity động hoặc chặn có log rõ ràng. |
| Mapping culling có thể sai sau khi destroy instance | `List.Remove` làm dịch index nhưng mảng sphere không compact đồng bộ | Visibility có thể áp vào sai instance. Dùng stable handle/index hoặc rebuild mapping khi remove. |
| Cache key dựa trên hash tên renderer/material | `name.GetHashCode()` và tổng hash tên material | Các renderer trùng tên/khác mesh hoặc hash collision có thể dùng nhầm `VertexCache`. Key phải gồm mesh/material instance ID và topology cần thiết. |
| LOD hard-code | ngưỡng bình phương `50`/`500` | Không theo `LODGroup`, khó cân chỉnh theo camera/gameplay. Nên dùng `LODGroup.GetLODs()` hoặc policy LOD riêng cấu hình được. |
| Event loop không được reset | `eventIndex` chỉ reset khi `PlayAnimation`; loop chỉ đổi `curFrame` | Event trong clip Loop thực tế chỉ chạy vòng đầu. Reset event cursor khi frame wrap. |
| Cross-fade chưa hoàn chỉnh | shader blend position, không blend normal/tangent của animation trước | Ánh sáng/specular có thể pop trong transition. Blend normal/tangent hoặc dùng animation state phù hợp. |
| Shadow chỉ skin bone đầu | `skinningShadow` chỉ gọi `bone.x` | Bóng của vertex nhiều influence có thể sai. Cần skin weighted như pass chính. |
| Nội suy frame cuối có thể chạm frame kế | shader luôn đọc `curFrame + 1` | Với frame cuối/ranh giới clip-texture có nguy cơ pose nhảy. Cần lưu next frame đúng wrap mode hoặc duplicate frame cuối/đầu khi bake. |
| Asset loading mobile cũ và blocking | vòng `while (!w.isDone) {}` với `WWW` | Block main thread, không kiểm lỗi request; thay bằng `UnityWebRequest`/async loader. |
| API và shader legacy | README nói tối thiểu Unity 5.4; dùng Surface Shader/`WWW` | Cần compatibility pass trước khi dùng trên Unity version/pipeline mới. |
| Không có version cho bytes | `BinaryWriter` tuần tự, không header | Sau thay đổi dữ liệu rất khó chẩn đoán asset cũ. Thêm magic/version/validation. |

Ngoài ra, culling radius được tính từ cạnh lớn nhất của `Bounds`, không phải bán kính chính xác; đây là cách bảo thủ nhưng có thể làm sphere lớn và giảm hiệu quả culling.

## 11. Khuyến nghị sử dụng trong dự án

- Chỉ dùng package cho **đám đông xa camera** và tách gameplay/AI khỏi bone transform.
- Chọn FPS bake theo mục tiêu: 15 FPS như mặc định tiết kiệm texture nhưng chuyển động có thể thô; tăng FPS làm tăng memory texture, thời gian bake và bandwidth shader.
- Giữ số bone thấp, kiểm tra `bonePerVertex` theo `QualitySettings.skinWeights`, và dùng mesh/material giống nhau để batch thực sự gộp.
- Đo bằng Frame Debugger/Profiler: số `DrawMeshInstanced`, batch bị tách theo mesh/material/texture, CPU trong `ApplyBoneMatrix`, GPU vertex cost và texture memory.
- Trước khi đưa vào production hiện tại, ưu tiên xử lý các lỗi culling remove, event Loop, frame cuối và shadow skinning; sau đó đánh giá lại shader cho render pipeline đang dùng.

## 12. Các file nên đọc khi cần sửa

- Bake/schema: `Assets/AniInstancing/Editor/AnimationGenerator.cs`.
- Load/schema runtime: `Assets/AniInstancing/Scripts/AnimationManager.cs`, `Assets/AniInstancing/Scripts/AnimationInfo.cs`.
- Batch/culling/cache: `Assets/AniInstancing/Scripts/AnimationInstancingMgr.cs`.
- API animation, LOD, event, attachment: `Assets/AniInstancing/Scripts/AnimationInstancing.cs`.
- Vertex skinning: `Assets/AniInstancing/Shader/AnimationInstancingBase.cginc`.

## 13. Tích hợp Prototype với `Bot_Guard_bake` và `bot_zombie_nor_men`

Prototype gameplay dùng package này cho ally, enemy và boss mà không thay đổi simulation `NativeArray`. `GameplayBootstrap` gửi `SimulationBenchmark.Allies` cho `AnimationInstancedAllyPresenter` và `SimulationBenchmark.Enemies` cho `AnimationInstancedEnemyPresenter`. Hai presenter dùng chung `AnimationInstancedUnitPresenter` theo Template Method: lớp cơ sở sở hữu pool, active state và transform; presenter enemy chỉ bổ sung scale boss. `AnimationInstancingMgr` thực hiện animation, culling và `Graphics.DrawMeshInstanced`.

Thiết lập trong `Prototype.unity`:

- GameObject `Ally Instanced Renderer` gắn `AnimationInstancedAllyPresenter`.
- GameObject `Enemy Animation Instanced Renderer` gắn `AnimationInstancedEnemyPresenter`.
- `unitPrefab` của ally trỏ đến `Assets/_Game/Resources/Bot/Bot_Guard_bake.prefab`; prefab enemy trỏ đến `Assets/_Game/Resources/Bot/bot_zombie_nor_men.prefab`.
- Dữ liệu bake được runtime Editor/Windows đọc tại `Assets/AnimationTexture/Bot_Guard_bake.bytes` qua `AnimationManager.GetAnimationTextureFilePath`.
- Dữ liệu enemy bake được runtime Editor/Windows đọc tại `Assets/AnimationTexture/bot_zombie_nor_men.bytes`.
- `Directional Light` đã có `AnimationInstancingMgr` và `AnimationManager`; `Main Camera` là `Camera.main` cho `CullingGroup`.

Tracer vẫn dùng `InstancedEntityRenderer`; enemy không còn có matrix renderer riêng. Không được chuyển position/state của ally hoặc enemy ngược vào `SimulationBenchmark`, cũng không thêm `Animator`, `SkinnedMeshRenderer`, Collider hoặc logic gameplay riêng cho từng instance pool.
