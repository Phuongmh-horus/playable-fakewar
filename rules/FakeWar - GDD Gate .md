

# **FAKE WAR GAME DESIGN DOCUMENT: MULTIPLIER GATES**

## **I. THÔNG TIN TỔNG QUAN**

### 

| Trường | Nội dung |
| :---- | :---- |
| Tên tính năng | Cổng tăng lính (MULTIPLIER GATES) |
| Loại feature | in-game Mechanic / Combat Buff & Obstacle.  |
| Gắn với hệ thống | Buff System |
| Người viết | Hà Tiến Hải |
| Ngày cập nhật | 18 / 06/ 2026 |

* **Mục tiêu thiết kế:** \* Tạo sự đa dạng, biến hóa cho kịch bản thiết kế màn chơi (Level Design).  
  * Cung cấp cơ chế tương tác động, cho phép người chơi chủ động thay đổi cục diện trận đấu bằng hỏa lực (DPS).  
  * Kích thích phản xạ tư duy toán học và hình học của người chơi thông qua cơ chế co giãn diện tích cửa dynamic dưới áp lực thời gian thực.

## **II. CƠ CHẾ HOẠT ĐỘNG CỐT LÕI (CORE MECHANICS)**

### **1\. Tương tác tăng giảm lính**

**Số lính sau khi đi qua cổng \= số lính trước khi qua cổng \+/x số giá trị trên cổng**

**(kết quả được làm tròn đến hàng đơn vị)**

### **2\. Cấu trúc Phép toán và Logic Đổi màu theo Giá trị Đại số**

Mỗi ô cửa cổng được cấu thành từ 2 yếu tố: **Toán tử gốc** (Cố định) và **Giá trị hiển thị** (Biến thiếnh dựa trên sát thương).

* **Toán tử gốc:** Gồm 2 loại cố định là **Phép Cộng (+)** và **Phép Nhân (\*)**  
* **Giá trị hiển thị A:** Số đại số (có thể âm, dương hoặc số thập phân) hiển thị trên mặt cổng. Số A này thay đổi theo thời gian thực dựa trên DPS của người chơi dội vào cổng.

Hệ thống tự động quét giá trị của kết quả phép toán để thay đổi trực quan màu sắc hiển thị của cổng (Shader/Material) theo thời gian thực:

#### **A. Trạng thái Cổng Màu Đỏ (Cổng Suy Giảm / Phạt)**

Kích hoạt khi phép toán trên cổng làm **giảm** số lượng lính của Đám đông (Crowd):

* **Đối với Phép Cộng (+):** Giá trị số A là **Số Âm (A \< 0\)**. Ví dụ: \+ (-50) lính.  
* **Đối với Phép Nhân (x):** Giá trị số A là **Số thập phân nhỏ hơn 1 (0 \< A \< 1\)**. Ví dụ: x 0.5 lính.

#### **B. Trạng thái Cổng Màu Xanh (Cổng Tăng Trưởng / Thưởng)**

Kích hoạt ngay khi giá trị biến đổi làm **tăng** số lượng lính của Đám đông:

* **Đối với Phép Cộng (+):** Giá trị số A là **Số Dương (A \> 0\)**. Ví dụ: \+ 20 lính.  
* **Đối với Phép Nhân (x):** Giá trị số A là **Số lớn hơn 1 (A \> 1\)**. Ví dụ: x 2.5 lính.

### **3\. Logic Bắn Nâng Cấp Số trên Cổng (DPS-driven Value Progression)**

Người chơi có thể điều khiển Hero hoặc các đơn vị lính bắn vào mặt cổng để nâng cấp giá trị toán học của cổng đó:

* **Giá trị khởi tạo (A):** Số ban đầu hiển thị trên cổng khi mới xuất hiện (Ví dụ: \+ (-100) hoặc x 0.2).  
* **Bước nhảy cấu hình (X):** Giá trị tăng tiến mỗi nấc của cổng. Khi cổng đủ điều kiện tăng số, giá trị mới tuân theo công thức:  
* **Giá trị Amới \= Aban đầu \+ (X x n)**  
  *(với n là số lần cổng được cộng dồn nấc thành công)*.  
    
* **Tốc độ tăng số (P \- số lần nhảy số/giây):** Tính toán động dựa trên tổng lượng Sát thương mỗi giây (DPS) thực tế người chơi gây lên cổng:  
  * **Ngưỡng tối thiểu (P\_min):** Kích hoạt khi DPS người chơi đạt mốc DPS\_min. Nếu DPS\_Player \< DPS\_min \-\> P \= 0 (Không nhảy số).  
  * **Ngưỡng tối đa (P\_max):** Đạt được khi tổng DPS người chơi ge DPS\_config. Dù DPS có tăng thêm, tốc độ nhảy số cũng giữ nguyên ở mốc P\_max.  
  * **Khi DPS người chơi gây lên cổng trong khoảng Pmin \- Pmax thì giá trị P sẽ được tính theo phương pháp nội suy**

### **4\. Phân loại Cấu trúc Cổng (Gate Structural Types)**

#### **4.1 Cổng Đơn (Single Gate)**

![][image1]

* **Cấu trúc:** Chỉ gồm 1 cửa duy nhất chắn một phần đường. Người chơi bắn vào để nâng cấp giá trị hoặc chủ động bẻ lái né tránh hoàn toàn nếu đó là cổng phạt.

#### **4.2 Cổng Nhiều Cửa (Multi-Slot Dynamic Gate)**

[REF HOẠT ĐỘNG](https://drive.horusvn.com/s/YFy6Ff4HZ4C8zpy)

* **Cấu trúc:** Tổ hợp gồm **2 hoặc 3 cửa** dính liền nhau, chắn toàn bộ chiều ngang đường hành quân.  
* **Cấu hình Độc lập:** Mỗi cửa thành phần (i) trong tổ hợp sở hữu các thông số cấu hình riêng biệt hoàn toàn (Toán tử gốc, giá trị ban đầu A\_i, bước nhảy X\_i, tốc độ nhảy số P\_i và diện tích tối thiểu S\_min\_i).  
* **Trạng thái ban đầu:** Diện tích (Chiều rộng tính theo % tổng chiều rộng cổng) của các cửa chia đều bằng nhau tuyệt đối khi vừa Spawn (Tổ hợp 2 cửa \= 50% mỗi cửa; Tổ hợp 3 cửa \= 33.33% mỗi cửa).  
* **Logic Co giãn Diện tích Động khi có Tương tác chiến đấu:**  
  * Hệ thống ghi nhận phân phối hỏa lực, cửa nào nhận được **DPS cao hơn** sẽ trở thành **"Cửa chủ động"** (Active Slot). Các cửa còn lại sẽ trở thành **"Cửa bị động"** (Passive Slots).  
  * Toàn bộ Phép toán và Giá trị số hiển thị trên các cửa bị động sẽ **đóng băng hoàn toàn, giữ nguyên**.  
  * Cứ mỗi lần Cửa chủ động thỏa mãn điều kiện để **tăng thêm 1 nấc giá trị (X\_i)**:  
    * Diện tích (Chiều rộng %) của Cửa chủ động lập tức **tăng thêm một lượng S%** (Cấu hình được).  
    * Đồng thời, diện tích của các cửa bị động liền kề sẽ **giảm đi một lượng tổng tương đương S%** để bù trừ.  
* **Quy tắc Chặn giới hạn diện tích tối thiểu (S\_min Guard):**  
  * Khi diện tích của một cửa bị động bị co hẹp xuống chạm mốc giới hạn **S\_min\_i**, hệ thống sẽ khóa diện tích cửa đó lại, tuyệt đối không cho giảm thêm.  
  * **Trường hợp tới hạn:** Nếu **tất cả các cửa bị động liền kề đều đã đạt mốc tối thiểu S\_min**, hệ thống lập tức **đóng băng** Cửa chủ động. Kể từ lúc này, dù người chơi có gây bao nhiêu sát thương, Cửa chủ động đó cũng **KHÔNG được tăng thêm giá trị số (X) và KHÔNG được mở rộng diện tích (S%) nữa**.

### **5\. Quy tắc Vòng lặp Vận hành trong trận**

* **Cơ chế một lần (Single-use):** Mỗi tổ hợp cổng chỉ có tác dụng 1 lần duy nhất cho cả đợt di chuyển.  
* **Hủy kích hoạt (Deactivation):** Ngay khi rìa sau của đám đông lính đi qua hết tọa độ của cổng, cổng sẽ kích hoạt VFX sụp đổ hoặc chuyển sang màu xám tối (Used State) và tắt toàn bộ Collider vật lý để tránh lỗi trùng lặp logic khi lính đi lùi.

## **III. THÔNG SỐ CÂN BẰNG & CONFIG (BALANCING PARAMETERS)**

Hệ thống quản lý dữ liệu cho một tổ hợp cổng (Ví dụ cấu hình mẫu cho 1 cổng 2 cửa song song):

| Danh mục biến | Tên biến (Parameter) | Kiểu dữ liệu | Giá trị mẫu | Giải thích chức năng |
| :---- | :---- | :---- | :---- | :---- |
| **Hệ thống chung** | Gate\_Crowd\_Hard\_Cap | INT | 250 | Giới hạn trần số lượng lính tối đa trong trận để bảo vệ CPU. |
| **Cửa số 1 (Cổng Nhân)** | Slot1\_Operator | STRING | x | Toán tử gốc của Cửa 1\. |
|  | Slot1\_Initial\_A | FLOAT | 0.2 | Giá trị khởi tạo ban đầu (Cổng đỏ vì \< 1). |
|  | Slot1\_Step\_X | FLOAT | 0.1 | Mỗi lần nhảy số cộng thêm \+0.1 vào giá trị cũ. |
|  | Slot1\_DPS\_Config | FLOAT | 1000.0 | Mốc DPS để cửa đạt tốc độ nhảy số tối đa. |
|  | Slot1\_S | FLOAT | 4.0 | Mỗi lần nhảy số, Cửa 1 rộng thêm \+4% diện tích. |
|  | Slot1\_S\_min | FLOAT | 10.0 | Diện tích tối thiểu không thể giảm thêm của Cửa 1 là 10%. |
| **Cửa số 2 (Cổng Cộng)** | Slot2\_Operator | STRING | \+ | Toán tử gốc của Cửa 2\. |
|  | Slot2\_Initial\_A | FLOAT | \-50.0 | Giá trị khởi tạo ban đầu (Cổng đỏ vì \< 0). |
|  | Slot2\_Step\_X | FLOAT | 5.0 | Mỗi lần nhảy số cộng thêm \+5 vào giá trị cũ. |
|  | Slot2\_DPS\_Config | FLOAT | 600.0 | Mốc DPS để cửa đạt tốc độ nhảy số tối đa. |
|  | Slot2\_S | FLOAT | 5.0 | Mỗi lần nhảy số, Cửa 2 rộng thêm \+5% diện tích. |
|  | Slot2\_S\_min | FLOAT | 12.0 | Diện tích tối thiểu không thể giảm thêm của Cửa 2 là 12%. |

## **IV. YÊU CẦU ART / ANIMATION / SFX**

### **1\. Đồ họa & Hiệu ứng trực quan (Art/VFX)**

* **Vật liệu Đổi màu Động (Dynamic Material):** Khung và lõi năng lượng của cổng phải chuyển đổi sắc độ mượt mà dựa trên trạng thái đại số (Đỏ rực khi là số phạt, Xanh Neon phát sáng khi là số thưởng).  
* **UI Text trên cổng:** Chữ số phép toán hiển thị to, rõ ràng ở chính giữa ô cửa, tự động cập nhật số mới kèm hiệu ứng nảy nhẹ (Scale pop up) mỗi khi tăng giá trị thành công.  
* **Animation co giãn:** Việc phình to/thu nhỏ chiều rộng của các cửa cổng phải diễn ra mượt mà dựa trên các hàm nội suy tuyến tính (Lerp), không giật cục.

### **2\. Âm thanh (SFX)**

* sfx\_gate\_hit\_bullet: Tiếng đạn bắn chan chát vào bề mặt cổng năng lượng.  
* sfx\_gate\_value\_up: Âm thanh "Keng" hoặc tiếng sạc năng lượng cao vút mỗi khi số trên cổng nhảy tăng thêm một nấc X.  
* sfx\_gate\_expanding: Tiếng rít từ trường trầm kéo dài trong lúc diện tích một cửa đang phình to chiếm không gian.  
* sfx\_gate\_shatter: Tiếng vỡ vụn cơ khí khi toàn bộ cổng chuyển sang trạng thái Used.
