# Hướng Dẫn Quy Trình Sử Dụng & Thiết Lập Hệ Thống VAT

Tài liệu này hướng dẫn chi tiết các bước sử dụng công cụ **VAT Bake Tool** để xuất dữ liệu và thiết lập một nhân vật sử dụng hệ thống **VAT Bridge Components** để thay thế cho `SkinnedMeshRenderer` và `Animator` truyền thống.

---

## PHẦN 0: RANH GIỚI MODULE

Feature được chia thành hai assembly để có thể tháo rời khỏi project:

- `OptimizedFeature.Runtime`: runtime component, data asset và runtime batching.
- `OptimizedFeature.Editor`: bake/setup tool, graph và custom inspector; chỉ compile trên Editor.

Namespace public vẫn là `OptimizedFeature.Scripts` để giữ compatibility với prefab/scene đã serialize. Gameplay script bên ngoài chỉ là consumer; không cần di chuyển vào feature. Chi tiết dependency và quy trình tháo module xem [README](../../README.md).

---

## PHẦN 1: QUY TRÌNH SỬ DỤNG TOOL BAKE (OFFLINE PHASE)

Quy trình nướng hoạt họa diễn ra hoàn toàn tự động trên Unity Editor thông qua công cụ **VATBakeToolWindow** chuyên dụng:

### Quy ước Scale và Coordinate Space

VAT không bake scale model trực tiếp vào dữ liệu vertex/texture. Tool thực hiện theo hợp đồng sau:

1. Tìm common parent đầu tiên chứa toàn bộ `SkinnedMeshRenderer` của input model.
2. Tích lũy `localScale` của parent này và toàn bộ ancestor phía trên cho tới `Transform.root`, sau đó lưu giá trị cuối vào `VATAssetDataSO.ModelScale` như metadata dùng cho Runtime Setup.
3. Tạm thời đưa toàn bộ chuỗi từ common parent tới `Transform.root` về `(1,1,1)` trong toàn bộ quá trình bake và sampling animation. Việc normalize này cũng được áp dụng lại sau mỗi `SampleAnimationClip` để scale curve của animation không làm thay đổi bake space.
4. Mesh, bounds và VAT texture được xuất trong local space chuẩn hóa của common parent. `ModelScale` không được dùng trong phép biến đổi vertex hoặc sampling bake.
5. Khi chạy **4. Runtime Setup**, tool gán lại `ModelScale` cho `Target Root GameObject`.

Nhờ đó, scale chỉ được áp dụng một lần ở runtime. Không được nhân thêm scale vào từng `SkinnedMeshRenderer`, material hoặc shader property của VAT. Đặc biệt, không nên giữ transform scale cũ trên các `VAT_Item_SubRender_*` hoặc sub-render legacy `VAT_Weapon_SubRender_*`; các sub-render này phải là child identity của VAT root.

Nếu `VATAssetDataSO` được tạo bởi phiên bản cũ và chưa có `ModelScale`, Runtime Setup sẽ dùng fallback `(1,1,1)`. Với model có scale khác 1, cần bake override lại để ghi scale vào asset.

### Dọn output khi Runtime Setup lại

Khi chạy lại **4. Runtime Setup** trên model đã từng setup, tool sẽ xóa toàn bộ child runtime được sinh tự động có tên `MeshRenderer_VAT`, `VAT_Item_SubRender_*` hoặc tên legacy `VAT_Weapon_SubRender_*`, bao gồm cả trường hợp child đã bị mất component VAT nhưng vẫn còn lại trong hierarchy. Các GameObject sub-render do người dùng tự tạo không bị xóa; tool chỉ gỡ binding VAT trên component của chúng.

### Quy ước Animator và Default State

- Mỗi lần bake, `VATAssetDataSO.DefaultStateName` được tự động đặt bằng `StateHash` của `Clips[0]`.
- `AnimatorAsset` là tùy chọn. Nếu `AnimatorAsset == null`, `VAT_RenderComponent` dùng `VATAssetDataSO.DefaultStateName` để phát clip mặc định khi runtime bắt đầu.
- Nếu đã gán `AnimatorAsset`, default state trong `VATAssetAnimatorSO` là giá trị authoritative; `VATAssetDataSO.DefaultStateName` chỉ giữ fallback baked cho trường hợp AnimatorAsset không được dùng.
- Khi mở VAT Animator Graph, thay đổi default state phải đi qua graph; không chỉnh field fallback để thay thế mapping của AnimatorAsset.

### Bước 1: Mở Tool Window
Trong Unity Editor, trên thanh công cụ, chọn:
> **Tools** -> **VAT** -> **VAT Bake Tool**

### Bước 2: Điền Cấu Hình Chung (General Baking Settings)
1. **Save Path**: Đường dẫn lưu trữ tài sản sau khi nướng (mặc định: `Assets/Optimized-Feature/BakedAssets/`).
2. **Sample FPS**: Tần số quét mẫu để nướng hoạt họa (khuyên dùng **30**).
3. **Shader Patch Mode**:
   - `AutoPatchIfMissing`: Chỉ tự động chèn mã nguồn hỗ trợ VAT (`VAT_Core.cginc`) khi Shader hiện tại của Material **chưa hỗ trợ VAT** (giúp tránh việc trùng lặp và ghi đè file Shader không cần thiết khi nướng lại nhiều lần).
   - `Ignore`: Bỏ qua quá trình xử lý Shader (chỉ nướng dữ liệu Mesh tĩnh và Texture). Khuyên dùng khi bạn đã nâng cấp Shader X hoàn chỉnh rồi.
   - `AlwaysForcePatch`: Luôn bắt buộc nướng đè/vá lại Shader.

### Bước 3: Kéo Thả Nhân Vật Và Quản Lý Dữ Liệu Input
Kéo thả nhân vật gốc vào ô **Target GameObject**. Cửa sổ sẽ tự động hiển thị:
1. **Detected Skinned Meshes**: Danh sách các lưới biến dạng trên nhân vật.
2. **Detected Materials & Shaders**: Hiển thị rõ danh sách các vật liệu kèm trạng thái:
   - `[VAT Ready]`: Shader đã hỗ trợ VAT.
   - `[No VAT Code]`: Shader gốc chưa có VAT, sẽ được xử lý dựa trên *Shader Patch Mode* đã chọn.
3. **Select Animation Clips to Bake**: Danh sách các clip hoạt họa được tìm thấy. Bạn có thể **bỏ tích chọn** các clip không dùng tới để tối ưu hóa không gian ảnh.
4. **VAT Renderer Role**: Phân loại mỗi `SkinnedMeshRenderer` thành **Body** hoặc **Item**. Body đi vào VAT chính; Item đi vào VAT sub-render tùy chọn. Hai channel được bake bằng cùng frame manifest.

### Bước 4: Nhấn nút nướng hoạt họa (Bake)
Công cụ tự động phát hiện xem các tệp tin kết quả nướng trước đây của nhân vật đó đã tồn tại trong thư mục lưu trữ hay chưa:
- **Nếu CHƯA tồn tại**: Nút bấm hiển thị **`Simulate VAT Baking Pipeline`**.
- **Nếu ĐÃ tồn tại (Trùng tên)**: Nút bấm tự động đổi tên thành **`Override VAT Baked Assets`**. Khi nhấn nút này, hệ thống sẽ thực hiện ghi đè trực tiếp dữ liệu mới vào các file cũ trên đĩa và cập nhật sạch sẽ cơ sở dữ liệu Asset của Unity, tránh tạo ra các file trùng lặp lộn xộn.

Tệp tin kết quả xuất ra gồm:
- **Baked Body Static Mesh/Texture**: Mesh và texture chứa dữ liệu Body VAT.
- **Baked Item Static Mesh/Texture**: Mesh và texture tùy chọn chứa dữ liệu Item VAT, có cùng số frame với Body.
- **Baked Material [i]**: File vật liệu thành phẩm dạng `.mat` (đặt tên đuôi `_VAT.mat` và được lưu tại thư mục chỉ định `Save Path`). Vật liệu này đã được nhân bản từ vật liệu gốc và gán sẵn Texture VAT cùng Bounding Box của nhân vật. 
- **VAT Asset Data SO**: File ScriptableObject lưu Body VAT, clip manifest, materials và tham chiếu tới `VATWeaponAssetSO` nếu có.
- **`ModelScale` trong VAT Asset Data SO**: Scale tích lũy từ common model parent tới `Transform.root`, chỉ dùng để đồng bộ scale khi chuyển output sang Runtime Setup; không phải scale đầu vào của bake.
- **VAT Item Asset SO**: File ScriptableObject tùy chọn lưu Item mesh, texture, bounds, materials và cùng clip manifest. Các type/field serialized có tiền tố `Weapon` vẫn được giữ để tương thích asset cũ.

---

## PHẦN 2: THIẾT LẬP NHÂN VẬT VAT RUNTIME (SETUP PHASE SAU KHI BAKE)

Ta tiến hành thiết lập runtime bằng một trong 2 cách dưới đây:

### CÁCH 1: Tự động hóa hoàn toàn bằng Tool Setup Tester (KHUYÊN DÙNG - CỰC NHANH)
1. Trong Unity Editor, mở:
   > **Tools** -> **VAT** -> **VAT Bake Tool**, sau đó chọn tab **4. Runtime Setup**
2. Cấu hình các trường thông tin trong bảng hiện ra:
   - **Target Root GameObject**: Kéo thả một GameObject rỗng đại diện cho nhân vật mới (ví dụ: `Hero_VAT_Runtime`).
   - **VAT Asset Data SO**: Kéo file dữ liệu ScriptableObject (`Normal_Mummy_VATData.asset`) nướng được ở Phân 1 vào.
   - **Model Scale**: Không cần nhập thủ công. Tool đọc `VATAssetDataSO.ModelScale` và đưa scale của target root về đúng giá trị đã lưu.
   - **VAT Material (Optional)**: Trường vật liệu thành phẩm. Bạn có thể **để trống trường này** (Optional).
     - **Cơ chế tự động**: Nếu để trống, Tool sẽ tự động lấy Vật liệu thành phẩm `BakedMaterials[0]` lưu sẵn trong file `VAT Asset Data SO` để gán cho nhân vật.
   - Nếu Body asset có Item asset được chọn, Tool tự tạo `VATWeaponRenderComponent` và đồng bộ frame với Body VAT. Component/type cũ vẫn giữ tên `VATWeapon*` để tương thích.
   - **Default Item**: Tool hiển thị toggle theo tên các entry trong `VATAssetDataSO.WeaponAssets`. Chỉ một Item được chọn để load lúc Runtime Setup; bỏ chọn tất cả sẽ dùng `-1` và hiển thị `[No Item Found]`.
3. Nhấn nút **Setup VAT Runtime Character**. Tool sẽ tự động gắn và liên kết component Bridge (`VAT_RenderComponent`, `VATWeaponRenderComponent`) và tự tạo `VATSystem` quản lý trong Scene chỉ trong 1 giây.

Sau Runtime Setup, kiểm tra `Target Root GameObject.localScale` khớp với `VATAssetDataSO.ModelScale`. Nếu model bị lệch kích thước hoặc vị trí ở các sub-SkinnedMesh, không sửa bằng cách scale riêng sub-render; hãy kiểm tra lại common parent và bake override.

---

### CÁCH 2: Thiết lập thủ công (Manual Setup)

#### Bước 1: Tạo GameObject cấu trúc VAT
1. Tạo một GameObject rỗng làm gốc nhân vật mới (ví dụ đặt tên: `Hero_VAT_Runtime`).
2. Gắn component [VAT_RenderComponent](file:///c:/PlayableProject/Playable_RPG_Game_Checker/Assets/Optimized-Feature/Runtime/Rendering/VAT_RenderComponent.cs) vào GameObject này. Đây là component hợp nhất thay thế trực tiếp cho `Animator` và `SkinnedMeshRenderer` cũ.

#### Bước 2: Cấu hình lưới hiển thị tĩnh
1. Tạo một GameObject con dưới `Hero_VAT_Runtime` (đặt tên ví dụ: `Mesh_Renderer`).
2. Cấu hình `MeshFilter` và `MeshRenderer` trên GameObject này; `VAT_RenderComponent` sẽ quản lý mesh tĩnh, material và shader properties.
3. Kéo thả file ScriptableObject **`VAT Asset Data SO`** đã tạo ra ở Phần 1 vào ô **Vat Asset Data** của component này.
4. Gán file vật liệu thành phẩm **`Baked Material`** (có đuôi `_VAT.mat` nằm ở thư mục kết quả của Phần 1) vào MeshRenderer.

#### Bước 3: Đăng ký System quản lý trung tâm
Đảm bảo trong Scene có một GameObject rỗng chứa component [VATSystem](file:///c:/PlayableProject/Playable_RPG_Game_Checker/Assets/Optimized-Feature/Runtime/Systems/VATSystem.cs).
