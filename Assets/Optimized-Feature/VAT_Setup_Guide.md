# Hướng Dẫn Quy Trình Sử Dụng & Thiết Lập Hệ Thống VAT

Tài liệu này hướng dẫn chi tiết các bước sử dụng công cụ **VAT Bake Tool** để xuất dữ liệu và thiết lập một nhân vật sử dụng hệ thống **VAT Bridge Components** để thay thế cho `SkinnedMeshRenderer` và `Animator` truyền thống.

---

## PHẦN 1: QUY TRÌNH SỬ DỤNG TOOL BAKE (OFFLINE PHASE)

Quy trình nướng hoạt họa diễn ra hoàn toàn tự động trên Unity Editor thông qua công cụ **VATBakeToolWindow** chuyên dụng:

### Bước 1: Mở Tool Window
Trong Unity Editor, trên thanh công cụ, chọn:
> **Tools** -> **VAT Bake Tool Simulation**

### Bước 2: Điền Cấu Hình Chung (General Baking Settings)
1. **Save Path**: Đường dẫn lưu trữ tài sản sau khi nướng (mặc định: `Assets/Optimized-Feature/BakedAssets/`).
2. **Sample FPS**: Tần số quét mẫu để nướng hoạt họa (khuyên dùng **30**).
3. **Shader Patch Mode**:
   - `AutoPatchIfMissing`: Chỉ tự động chèn mã nguồn hỗ trợ VAT (`VAT_Core.cginc`) khi Shader hiện tại của Material **chưa hỗ trợ VAT** (giúp tránh việc trùng lặp và ghi đè file Shader không cần thiết khi nướng lại nhiều lần).
   - `Ignore`: Bỏ qua quá trình xử lý Shader (chỉ nướng dữ liệu Mesh tĩnh, Texture và Socket). Khuyên dùng khi bạn đã nâng cấp Shader X hoàn chỉnh rồi.
   - `AlwaysForcePatch`: Luôn bắt buộc nướng đè/vá lại Shader.

### Bước 3: Kéo Thả Nhân Vật Và Quản Lý Dữ Liệu Input
Kéo thả nhân vật gốc vào ô **Target GameObject**. Cửa sổ sẽ tự động hiển thị:
1. **Detected Skinned Meshes**: Danh sách các lưới biến dạng trên nhân vật.
2. **Detected Materials & Shaders**: Hiển thị rõ danh sách các vật liệu kèm trạng thái:
   - `[VAT Ready]`: Shader đã hỗ trợ VAT.
   - `[No VAT Code]`: Shader gốc chưa có VAT, sẽ được xử lý dựa trên *Shader Patch Mode* đã chọn.
3. **Select Animation Clips to Bake**: Danh sách các clip hoạt họa được tìm thấy. Bạn có thể **bỏ tích chọn** các clip không dùng tới để tối ưu hóa không gian ảnh.
4. **VAT Renderer Role**: Phân loại mỗi `SkinnedMeshRenderer` thành **Body** hoặc **Weapon**. Body đi vào VAT chính; Weapon đi vào VAT sub-render tùy chọn. Hai channel được bake bằng cùng frame manifest.

### Bước 4: Nhấn nút nướng hoạt họa (Bake)
Công cụ tự động phát hiện xem các tệp tin kết quả nướng trước đây của nhân vật đó đã tồn tại trong thư mục lưu trữ hay chưa:
- **Nếu CHƯA tồn tại**: Nút bấm hiển thị **`Simulate VAT Baking Pipeline`**.
- **Nếu ĐÃ tồn tại (Trùng tên)**: Nút bấm tự động đổi tên thành **`Override VAT Baked Assets`**. Khi nhấn nút này, hệ thống sẽ thực hiện ghi đè trực tiếp dữ liệu mới vào các file cũ trên đĩa và cập nhật sạch sẽ cơ sở dữ liệu Asset của Unity, tránh tạo ra các file trùng lặp lộn xộn.

Tệp tin kết quả xuất ra gồm:
- **Baked Body Static Mesh/Texture**: Mesh và texture chứa dữ liệu Body VAT.
- **Baked Weapon Static Mesh/Texture**: Mesh và texture tùy chọn chứa dữ liệu Weapon VAT, có cùng số frame với Body.
- **Baked Material [i]**: File vật liệu thành phẩm dạng `.mat` (đặt tên đuôi `_VAT.mat` và được lưu tại thư mục chỉ định `Save Path`). Vật liệu này đã được nhân bản từ vật liệu gốc và gán sẵn Texture VAT cùng Bounding Box của nhân vật. 
- **VAT Asset Data SO**: File ScriptableObject lưu Body VAT, clip manifest, materials và tham chiếu tới `VATWeaponAssetSO` nếu có.
- **VAT Weapon Asset SO**: File ScriptableObject tùy chọn lưu Weapon mesh, texture, bounds, materials và cùng clip manifest.

---

## PHẦN 2: THIẾT LẬP NHÂN VẬT VAT RUNTIME (SETUP PHASE SAU KHI BAKE)

Ta tiến hành thiết lập runtime bằng một trong 2 cách dưới đây:

### CÁCH 1: Tự động hóa hoàn toàn bằng Tool Setup Tester (KHUYÊN DÙNG - CỰC NHANH)
1. Trong Unity Editor, chọn:
   > **Tools** -> **VAT Setup Tester Helper**
2. Cấu hình các trường thông tin trong bảng hiện ra:
   - **Target Root GameObject**: Kéo thả một GameObject rỗng đại diện cho nhân vật mới (ví dụ: `Hero_VAT_Runtime`).
   - **VAT Asset Data SO**: Kéo file dữ liệu ScriptableObject (`Normal_Mummy_VATData.asset`) nướng được ở Phân 1 vào.
   - **VAT Material (Optional)**: Trường vật liệu thành phẩm. Bạn có thể **để trống trường này** (Optional).
     - **Cơ chế tự động**: Nếu để trống, Tool sẽ tự động lấy Vật liệu thành phẩm `BakedMaterials[0]` lưu sẵn trong file `VAT Asset Data SO` để gán cho nhân vật.
   - Nếu Body asset có `DefaultWeaponAsset`, Tool tự tạo `VATWeaponRenderComponent` và đồng bộ frame với Body VAT.
   - **Equipment Attachments**: Khai báo danh sách vũ khí/trang bị đi kèm (kéo GameObject vũ khí vào và điền đúng tên khớp xương gán `Socket Name` như `RightHand`).
3. Nhấn nút **Setup VAT Runtime Character**. Tool sẽ tự động gắn và liên kết toàn bộ các component Bridge (`VAT_AnimatorComponent`, `VAT_SkinnedMeshComponent`, `VAT_ObjectMesh`) và tự tạo `VATSystem` quản lý trong Scene chỉ trong 1 giây.

---

### CÁCH 2: Thiết lập thủ công (Manual Setup)

#### Bước 1: Tạo GameObject cấu trúc VAT
1. Tạo một GameObject rỗng làm gốc nhân vật mới (ví dụ đặt tên: `Hero_VAT_Runtime`).
2. Gắn component [VAT_AnimatorComponent](file:///c:/PlayableProject/Playable_RPG_Game_Checker/Assets/Optimized-Feature/Scripts/VAT_AnimatorComponent.cs) vào GameObject này. Đây là component thay thế trực tiếp cho `Animator` cũ.

#### Bước 2: Cấu hình lưới hiển thị tĩnh
1. Tạo một GameObject con dưới `Hero_VAT_Runtime` (đặt tên ví dụ: `Mesh_Renderer`).
2. Gắn component [VAT_SkinnedMeshComponent](file:///c:/PlayableProject/Playable_RPG_Game_Checker/Assets/Optimized-Feature/Scripts/VAT_SkinnedMeshComponent.cs) vào GameObject con này.
3. Kéo thả file ScriptableObject **`VAT Asset Data SO`** đã tạo ra ở Phần 1 vào ô **Vat Asset Data** của component này.
4. Gán file vật liệu thành phẩm **`Baked Material`** (có đuôi `_VAT.mat` nằm ở thư mục kết quả của Phần 1) vào MeshRenderer.

#### Bước 3: Gắn trang bị đồng bộ (Attachments)
Nếu nhân vật có mang theo vũ khí hoặc trang bị đi kèm:
1. Đặt GameObject của Vũ khí (ví dụ cây kiếm tĩnh) làm con của `Hero_VAT_Runtime`.
2. Gắn component [VAT_ObjectMesh](file:///c:/PlayableProject/Playable_RPG_Game_Checker/Assets/Optimized-Feature/Scripts/VAT_ObjectMesh.cs) vào GameObject vũ khí.
3. Điền đúng tên khớp xương vào ô **Socket Name** (ví dụ: `RightHand`).
4. Với Weapon VAT, có thể đổi asset bằng `VAT_RenderComponent.SetWeaponAsset(...)`; sub-render dùng chung frame state với Body và không cần Animator riêng.

#### Bước 4: Đăng ký System quản lý trung tâm
Đảm bảo trong Scene có một GameObject rỗng chứa component [VATSystem](file:///c:/PlayableProject/Playable_RPG_Game_Checker/Assets/Optimized-Feature/Scripts/VATSystem.cs).
