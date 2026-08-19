# Optimized Feature

`Optimized-Feature` là module VAT độc lập gồm bake tool, runtime bridge, shader và dữ liệu bake mẫu.

## Ranh giới module

- `Runtime/OptimizedFeature.Runtime.asmdef`: runtime API và implementation. Các type vẫn giữ namespace `OptimizedFeature.Scripts` để bảo toàn serialized reference và API hiện tại.
- `Editor/OptimizedFeature.Editor.asmdef`: bake/setup tool, VAT Animator Graph, Animation Merge và custom inspector. Assembly này chỉ được build trên Unity Editor và tham chiếu runtime assembly.
- `Shaders/`: shader VAT và include HLSL; shader không thuộc C# assembly nhưng là dependency của material bake.
- `BakedAssets/`: output/sample data, không phải source code của module.
- `Documentation/`: hướng dẫn và nghiên cứu.

## Hợp đồng Scale khi Bake và Runtime Setup

Đây là quy ước bắt buộc để tránh lỗi kích thước/vị trí trên các sub-SkinnedMesh:

- Bake tool tìm common parent đầu tiên chứa toàn bộ `SkinnedMeshRenderer`.
- Scale được tích lũy từ common parent lên toàn bộ ancestor cho tới `Transform.root` và lưu vào `VATAssetDataSO.ModelScale` như metadata cho Runtime Setup; giá trị này không được đưa vào phép biến đổi vertex khi bake.
- Trong lúc bake và sample animation, toàn bộ chuỗi từ common parent tới `Transform.root` được tạm normalize về `(1,1,1)`. Vertex, bounds và VAT texture vì vậy không chứa scale model.
- `4. Runtime Setup` khôi phục `ModelScale` trên `Target Root GameObject`.
- Không scale riêng các sub-render, material hoặc shader VAT. Sub-render Item mới dùng tên `VAT_Item_SubRender_*` và phải giữ local position/rotation/scale trung tính dưới VAT root. Tên `VAT_Weapon_SubRender_*` vẫn được hỗ trợ cho asset cũ.

Nếu output được tạo trước khi có trường `ModelScale`, model scale khác 1 sẽ không được phục hồi tự động. Hãy chạy `Override VAT Baked Assets` rồi chạy lại `4. Runtime Setup`.

## Animator mặc định và AnimatorAsset tùy chọn

- Bake luôn ghi `VATAssetDataSO.DefaultStateName` bằng `StateHash` của clip đầu tiên trong `Clips`.
- Khi `AnimatorAsset == null`, runtime phát state hash này lúc khởi tạo.
- Khi `AnimatorAsset` tồn tại, `VATAssetAnimatorSO.DefaultStateName` và graph linked là nguồn điều khiển mặc định; field trên `VATAssetDataSO` chỉ là fallback độc lập.

## Dependency audit

Không có gameplay C# script bên ngoài `Optimized-Feature` đang tham chiếu trực tiếp các type VAT. Các consumer hiện tại là serialized asset:

- `Assets/_Game/Prefabs/SpawnableObject/Enemies/Enemy_t1_a1_v1.prefab` dùng `VAT_RenderComponent`, `VATWeaponRenderComponent` và `VATTestingController`.
- `Assets/_Game/Resources_moved/Scene/Option_4_Map2.unity` dùng `VATSystem`, các VAT renderer và baked assets.

Các script Bot/Player không thuộc module và không nên di chuyển vào đây. Nếu một assembly ngoài `Assembly-CSharp` muốn gọi API VAT, nó chỉ cần thêm reference tới `OptimizedFeature.Runtime`.

Bake tool không còn phụ thuộc trực tiếp vào Newtonsoft Json; phần đọc nhánh texture override của `luna.json` dùng `UnityEngine.JsonUtility`. Vì vậy module không cần kéo theo user script hoặc package JSON riêng.

## Cách tháo module

1. Tìm và loại bỏ các component VAT khỏi prefab/scene đang sử dụng module.
2. Xóa hoặc di chuyển baked assets và material dùng shader `OptimizedFeature/*` nếu không còn cần.
3. Xóa toàn bộ `Assets/Optimized-Feature/`.
4. Làm mới asset database và kiểm tra lại scene/prefab để không còn `Missing (Mono Script)` hoặc reference tới baked asset.

Runtime-only build có thể bỏ `Editor/` sau khi bake xong. Nếu vẫn cần bake/setup/graph thì giữ cả `Runtime/` và `Editor/`.

## API runtime chính

- `VAT_RenderComponent`: phát animation, cross-fade, parameter và cập nhật frame shader.
- `VATWeaponRenderComponent`: channel Item đồng bộ frame với Body; tên class giữ nguyên để tương thích API.
- `VATSystem`: update/culling/runtime mesh batching.
- `VATAssetDataSO` và `VATWeaponAssetSO`: dữ liệu output của bake. `WeaponAssets` là tên serialized legacy của danh sách Item VAT assets.

Trong **4. Runtime Setup**, `Default Weapon Index` đã được thay bằng danh sách toggle **Default Item** theo tên entry của `VATAssetDataSO.WeaponAssets`. Chỉ một Item được chọn để setup; khi không có Item hoặc bỏ chọn tất cả, giá trị nội bộ là `-1` và UI hiển thị `[No Item Found]`.
