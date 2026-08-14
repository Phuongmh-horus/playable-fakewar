# Nghiên Cứu Tối Ưu Hóa Hệ Thống Kết Xuất UnityRender Cho Luna Playable (WebGL 2.0)

## Tác giả & Mục đích
- **Dự án**: Playable RPG Game (Luna Playworks Engine / WebGL 2.0 Target).
- **Mục tiêu**: Nghiên cứu tính khả thi và giải pháp kỹ thuật triệt để cho 2 bài toán đồ họa cốt lõi:
  1. Thay thế `SkinnedMeshRenderer` / `Animator` bằng **VAT (Vertex Animation Texture)**.
  2. Xử lý triệt để hiện tượng trần trập kết xuất (**Overdraw**) của VFX & Particle System.
- **Ràng buộc**: Dung lượng file export $< 5\text{MB}$, không dùng `Mecanim`, không dùng `Compute Shader`, không dùng `Physics2D/3D`, tuân thủ kiến trúc System-Component-Structure.

---

## BÁO CÁO NGHIÊN CỨU 1: Vertex Animation Texture (VAT) Trên Luna Playable

### 1. Cơ chế hoạt động của VAT
VAT là kỹ thuật nướng (bake) tọa độ đỉnh ($\Delta Pos$), pháp tuyến ($\text{Normal}$), hoặc hướng tiếp tuyến ($\text{Tangent}$) của các frame hoạt họa Mesh/SkinnedMesh vào một Texture 2D ($N \times M$ với $N$ là số đỉnh, $M$ là số frame).
Trong quá trình render:
- Đã loại bỏ hoàn toàn `SkinnedMeshRenderer`, `Bone Transform Hierarchy` và component `Animator`.
- Đối tượng được thay thế bằng một static `MeshFilter` + `MeshRenderer` kết hợp với Custom Shader.
- Đỉnh của Mesh được di chuyển trực tiếp trên GPU ở giai đoạn Vertex Shader bằng cách tra cứu vị trí tương ứng từ VAT Texture theo `Vertex ID` hoặc tọa độ `UV2` thứ hai.

### 2. Đánh giá tính khả thi trên Luna Playable (WebGL 2.0)
| Tiêu chí | Đánh giá tính khả thi trên Luna | Chi tiết kỹ thuật |
| :--- | :--- | :--- |
| **Hỗ trợ WebGL 2.0** | **RẤT CAO (Khả thi 100%)** | WebGL 2.0 hỗ trợ đầy đủ `tex2Dlod` trong Vertex Shader và Float Textures (`RGBA16F`, `RGBA32F` hoặc `RGBA8` nén). |
| **Bỏ qua Mecanim** | **TỐT NHẤT** | `luna.json` mặc định tắt `mecanim-wasm`. VAT loại bỏ hoàn toàn Animator, giúp tránh crash và loại bỏ hoàn toàn tải tính toán CPU Bone skinning trên JS thread. |
| **GPU Instancing** | **TỐT NHẤT** | Tất cả các nhân vật cùng mesh/VAT share chung 1 Material. Chỉ cần truyền `FrameIndex` hoặc `TimeOffset` qua `MaterialPropertyBlock`, cho phép render **hàng trăm đám đông/monster chỉ với 1 Draw Call**. |
| **Dung lượng 5MB** | **TỐI ƯU** | Một nhân vật 1,000 vertex x 30 frame chỉ tạo ra Texture kích thước $1024 \times 32$ pixels. Khi nén PNG-8/RGBA16F, dung lượng chỉ từ **15KB - 40KB** per animation. |

### 3. Phân tích Ưu & Nhược điểm của giải pháp VAT

#### Ưu điểm:
1. **Hiệu năng GPU/CPU cực cao**: Loại bỏ 100% chi phí tính toán ma trận ma trận xương (Bone Matrix Math) trên CPU JavaScript đơn luồng.
2. **Siêu Gom Draw Call (GPU Instancing Batching)**: Cho phép xuất hiện hàng trăm quái vật/hero xuất hiện đồng thời trên màn hình di động yếu mà vẫn giữ 60 FPS.
3. **Loại bỏ Animator Bloat**: Tiết kiệm đáng kể dung lượng bộ nhớ C# Heap và tránh các lỗi crash cơ chế chuyển State Animation trong Luna.

#### Nhược điểm & Thách thức trên Luna:
1. **Không hỗ trợ Dynamic Blending phức tạp**: Không thể làm Blend Tree tự động giữa các Animation State như Mecanim (chỉ có thể cross-fade đơn giản bằng cách sample 2 row texture trong shader).
2. **Giới hạn biến dạng trang bị (Attachment/Equipment)**: Nếu gắn vũ khí vào tay nhân vật, phải tính toán vị trí socket bằng ma trận nướng sẵn hoặc truyền vị trí Bone chính qua C# Script.
3. **Cơ chế nén Texture trên WebGL**: Một số dòng máy Android cũ không hỗ trợ Half-Float Texture (`OES_texture_float`) trong Vertex Stage nếu rơi vào fallback WebGL 1.0 (tuy nhiên dự án này strict WebGL 2.0 nên rủi ro rất thấp). Có thể giải quyết bằng cách lưu tọa độ Bounding Box `[Min, Max]` và nén giá trị tọa độ về dải `[0, 1]` lưu trong 8-bit RGBA Texture.

---

## BÁO CÁO NGHIÊN CỨU 2: Xử Lý Đè Trồng Kết Xuất (Overdraw) VFX & Particle System

### 1. Nguyên nhân gây drop FPS từ Overdraw trên Mobile WebGL
Trần trập kết xuất (Overdraw) xảy ra khi một pixel trên màn hình phải thực hiện tính toán vẽ lại (Shading/Blending) nhiều lần trong cùng một frame.
Đối với Particle System và VFX trong Playable Ads:
- Các hạt particle thường dùng Billboard Quad có kích thước lớn, chứa phần diện tích Alpha trong suốt lớn.
- Hàng trăm hạt đè lên nhau khiến Fill-Rate bị quá tải, sụt giảm FPS cực nặng trên chip GPU di động (nhất là Mali/Adreno đời thấp).
- Hơn nữa, Shuriken Particle System trong Luna gặp nhiều hạn chế: không hỗ trợ *Soft Particles*, *Sub-emitters*, hay gặp lỗi *SetSizeOverLifetime*.

### 2. Các Case Giải Pháp & So Sánh Chi Tiết

#### Case A: Baking Particle System thành Flipbook / Sprite Sheet Animation
- **Ý tưởng**: Nướng toàn bộ hiệu ứng Particle thành chuỗi ảnh Sprite Sheet (Flipbook 4x4, 8x8) và chạy bằng Single Quad Unlit Shader hoặc SpriteRenderer.
- **Ưu điểm**:
  - Tải CPU bằng 0 (loại bỏ hoàn toàn vòng lặp hạt của Shuriken).
  - Đảm bảo 100% hiển thị chính xác như trong Unity Editor, không lo lỗi module Luna Particle.
- **Nhược điểm**: Kích thước Texture lớn nếu hiệu ứng dài hoặc độ phân giải cao (cần nén chặt để giữ dưới 5MB).
- **Đánh giá**: **Khuyên dùng 90% trường hợp VFX cố định (Chém, Nổ, Hit Effect)**.

#### Case B: Mesh Cutout Geometry Particles (Tight Mesh Quad)
- **Ý tưởng**: Thay vì dùng Quads hình chữ nhật ($2 \times 2$ triangle) có 60% vùng trong suốt bao quanh hạt, tạo ra các Mesh polygon vừa vặn khít theo viền hạt (Hexagon / Octagon Mesh).
- **Ưu điểm**: Giảm từ 50% - 80% diện tích Overdraw pixel trong suốt trên màn hình.
- **Nhược điểm**: Tăng nhẹ số lượng Vertex/Triangle (tuy nhiên GPU di động xử lý Vertex rất nhanh, vượt trội so với xử lý Fill-rate/Pixel Overdraw).
- **Đánh giá**: **Khuyên dùng khi bắt buộc phải dùng Shuriken Particle động (Mưa tiền, Khói bay)**.

#### Case C: Early-Z Cutout & Stencil Buffer Thresholding
- **Ý tưởng**: Chuyển các hiệu ứng VFX dạng khối (Sparks, Slash, Energy) từ mode Additive / Alpha Blend sang Alpha Test (`clip(col.a - cutoff)`) kết hợp Z-Write On / Stencil Mask.
- **Ưu điểm**: Cho phép card đồ họa tận dụng **Early-Z Rejection** (loại bỏ pixel bị che lấp trước khi tính Fragment Shader), giảm overdraw xuống gần bằng 0.
- **Nhược điểm**: Ranh giới viền VFX có thể hơi cứng (Hard Edge), không mềm mịn như Alpha Blending.
- **Đánh giá**: Cho hiệu ứng phong cách Stylized / Cartoon rất phù hợp.

#### Case D: Custom GPU Particle Pooling & Single-Buffer Batching
- **Ý tưởng**: Tự viết hệ thống quản lý Hạt bằng C# truyền mảng `Vector4[]` dữ liệu vị trí/kích thước vào MaterialPropertyBlock render qua `Graphics.DrawMeshInstanced`.
- **Ưu điểm**: Kiểm soát 100% quy trình Draw Call (1 Draw Call duy nhất cho toàn bộ hiệu ứng hạt cùng loại), hoàn toàn chủ động thứ tự Render Queue.
- **Nhược điểm**: Cần lập trình tỉ mỉ toán học vị trí hạt C#.

---

## KẾT LUẬN & ĐỀ XUẤT KIẾN TRÚC TỔNG THỂ

1. **VAT Mesh Rendering System**:
   - Áp dụng VAT cho toàn bộ nhân vật Quái vật (Mobs/Creeps), Hero, NPC trong game.
   - Sử dụng kiến trúc **System-Component-Structure**: `VATAnimationSystem` quản lý cập nhật frame theo thời gian; `VATAnimationComponent` chứa dữ liệu Mesh, Material, Texture; `VATFrameData` là lớp `class` mô hình hóa dữ liệu giao tiếp.

2. **VFX Overdraw Optimization**:
   - 70% VFX (Kỹ năng nổ, chém, buff): Chuyển hoàn toàn sang **Flipbook Sprite Sheet Baked**.
   - 30% VFX động (Bay theo quỹ đạo, thu thập item): Chuyển sang **Mesh Cutout Geometry** và quản lý bằng **Custom Instancing Manager**.

---
