# ScheduleApp — Đặt lịch, nhắc nhở & tự động thao tác

Ứng dụng desktop Windows (C# WinForms, .NET 10) để:

- **Đặt lịch chạy** công việc: một lần, hằng ngày, hằng tuần (chọn thứ), lặp lại mỗi N phút, hoặc chỉ chạy thủ công.
- **Nhắc nhở**: hiện cửa sổ nhắc trước giờ chạy N phút, hoặc dùng bước "Hiện nhắc nhở" ngay trong flow (có thể tạm dừng flow chờ bạn xác nhận).
- **Tự động thao tác theo luồng (flow)**: mở ứng dụng, chờ cửa sổ, kích hoạt cửa sổ, click chuột, gõ văn bản (có dấu tiếng Việt), nhấn tổ hợp phím, chạy lệnh cmd, đóng ứng dụng…

## Chạy ứng dụng

```powershell
dotnet run                      # chạy bản debug
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
# -> publish\ScheduleApp.exe
```

Cần .NET 10 Desktop Runtime (hoặc publish với `--self-contained true` để không cần cài runtime).

## Cách dùng

1. **＋ Thêm công việc** → đặt tên, chọn kiểu lịch, giờ chạy, (tùy chọn) số phút nhắc trước.
2. Dựng flow bằng **kéo thả** (xem bên dưới).
3. Bấm **▶ Chạy thử flow** để kiểm tra ngay, rồi **Lưu**.
4. Tick/bỏ tick ở danh sách chính để bật/tắt lịch. Cột "Còn lại" đếm ngược tới lần chạy tới.

Bấm nút X chỉ thu ứng dụng xuống **khay hệ thống** — lịch vẫn chạy. Thoát hẳn: chuột phải biểu tượng khay → *Thoát*.
Tick **Khởi động cùng Windows** để app tự chạy (ẩn ở khay) khi đăng nhập.

**Dừng khẩn cấp:** `Ctrl+Shift+Q` (phím tắt toàn hệ thống) — dừng flow đang chạy và hủy các flow đang chờ.

## Ví dụ mẫu

File [Samples/ScheduleApp-vi-du-mau.json](Samples/ScheduleApp-vi-du-mau.json) gồm 14 công việc mẫu phủ mọi loại lịch và loại bước.
Nhập bằng nút **Nhập…** trên màn hình chính. Các công việc có lịch được tắt sẵn — tick để bật khi muốn dùng.

| # | Ví dụ | Minh họa |
|---|---|---|
| 01 | Mở Notepad và gõ chữ tiếng Việt | Mở app, chờ/kích hoạt cửa sổ, gõ văn bản, nhấn phím |
| 02 | Nhắc nghỉ giải lao mỗi 60 phút | Lịch lặp theo phút, nhắc nhở không chặn flow |
| 03 | Nhắc họp (chạy một lần) | Lịch một lần, nhắc trước 10 phút, chờ xác nhận |
| 04 | 8:00 T2–T6 mở CRM bằng Chrome profile | Lịch hằng tuần, `--profile-directory` |
| 05 | Mở nhiều trang cùng lúc | Nhiều URL trong một lệnh mở Chrome |
| 06 | CRM: tìm kiếm bằng OCR | Chờ chữ / click chữ "Search" rồi gõ từ khóa |
| 07 | OCR trong Notepad | Tìm chữ vừa gõ và double-click |
| 08 | Mẫu click hình ảnh | Khung sẵn — tự chụp hình mẫu rồi bật bước |
| 09 | Gõ nhanh mẫu email | Gõ đoạn văn nhiều dòng vào ô đang chọn |
| 10 | Phím tắt chụp màn hình | Tổ hợp `Win+Shift+S` |
| 11 | Sao lưu Documents 17:30 | Lịch hằng ngày, robocopy + xử lý mã thoát |
| 12 | Ghi dung lượng ổ đĩa | Chạy PowerShell, output vào nhật ký |
| 13 | Mở thư mục Downloads | Mở Explorer với biến môi trường |
| 14 | 18:00 nhắc lưu rồi đóng Notepad | Nhắc chờ xác nhận + đóng ứng dụng |

## Thiết kế flow bằng kéo thả

Trình soạn công việc có 3 vùng: **Hộp công cụ** (trái) · **Khung luồng** Bắt đầu → các bước → Kết thúc (giữa) · nút lệnh (phải).

| Thao tác | Cách làm |
|---|---|
| Thêm bước | Kéo một thao tác từ hộp công cụ, thả vào vị trí mong muốn (đường kẻ xanh báo chỗ chèn), điền thông tin → OK. Hoặc nhấp đúp thao tác để chèn sau bước đang chọn |
| Mở ứng dụng nhanh | Kéo file `.exe` / shortcut / tài liệu từ Explorer thả vào luồng |
| Sắp xếp | Kéo thẻ lên/xuống (hoặc `Ctrl+↑/↓`) |
| Sửa | Nhấp đúp thẻ hoặc `Enter` |
| Bật/tắt, nhân bản, xóa | Chuột phải thẻ, hoặc `Space` / `Delete` |

Màu thẻ theo nhóm: xanh dương = ứng dụng, tím = cửa sổ, cam = chuột & bàn phím, xanh lá / vàng = điều khiển luồng / nhắc nhở.

## Nhận dạng màn hình

Dùng khi vị trí nút bấm thay đổi (trang web, cửa sổ đổi kích thước…) — ổn định hơn click theo tọa độ.

**Click vào hình ảnh / Chờ hình ảnh xuất hiện**
1. Bấm **✂ Chụp hình mẫu** → màn hình đóng băng → kéo chọn vùng cần tìm (vd nút *Lưu*). Hình mẫu lưu luôn trong flow.
2. **Thử tìm trên màn hình**: khung đỏ khoanh vị trí tìm được, con trỏ được đưa tới điểm sẽ click, kèm % độ khớp.
3. *Độ khớp tối thiểu* (mặc định 85%): giảm nếu không tìm thấy, tăng nếu click nhầm. Điểm khớp tính cả hình dạng lẫn màu, nên nút bị mờ (disabled) khác màu sẽ không bị nhận nhầm.
4. *Lệch khỏi tâm X, Y*: click lệch so với tâm hình (vd chụp nhãn "Tên đăng nhập", lệch Y = 30 để click vào ô nhập bên dưới).

Lưu ý: cần giữ nguyên độ phân giải, mức scale (100%/125%…) và mức zoom trình duyệt như lúc chụp. Tránh chụp vùng một màu hoặc chứa chữ/số hay thay đổi.

**Click vào chữ / Chờ chữ xuất hiện (OCR)** — tìm chữ hiển thị trên màn hình bằng Windows OCR:
- So sánh không phân biệt hoa thường, bỏ qua dấu và dấu câu, chấp nhận sai ~1 ký tự trên 5 (OCR hay đọc nhầm chữ có dấu).
- *Lần xuất hiện thứ*: chọn vị trí thứ N khi chữ xuất hiện nhiều lần (tính từ trên xuống).
- Nhập *Chỉ tìm trong cửa sổ* để nhanh và chính xác hơn.
- Để OCR đọc tiếng Việt tốt nhất, cài gói OCR tiếng Việt (PowerShell **Run as Administrator**):
  `Add-WindowsCapability -Online -Name "Language.OCR~~~vi-VN~0.0.1.0"` — nếu chưa cài, app dùng OCR tiếng Anh.

**Cửa sổ theo tiến trình:** ô cửa sổ nhận `exe:tên_tiến_trình` (vd `exe:chrome`) để khớp theo tiến trình thay vì tiêu đề — hữu ích khi tiêu đề thay đổi theo trang.

## Ghi macro thao tác

Trong trình soạn công việc bấm **● Ghi thao tác…** → các cửa sổ ScheduleApp tạm ẩn, thanh "Đang ghi" hiện ở trên cùng → thao tác bình thường → **Dừng & lưu** (hoặc `Ctrl+Shift+Q`). Các bước được chèn sau bước đang chọn:

- Click → *Click chuột* với tọa độ tương đối theo cửa sổ (`exe:…`); hai click nhanh cùng chỗ → double-click.
- Chữ gõ liên tiếp → một bước *Gõ văn bản* (Backspace sửa chữ được tính luôn; hỗ trợ Unikey/EVKey).
- Enter, Tab, phím mũi tên, F1–F12, tổ hợp Ctrl/Alt/Win → *Nhấn phím* (phím lặp gộp thành `Tab*3`).
- Khoảng nghỉ thật giữa các thao tác được giữ lại (0,15–5 giây).

Hạn chế: chưa ghi cuộn chuột và kéo-thả; với bộ gõ Telex có sẵn của Windows, chữ được ghi dạng phím gốc (vd "tieengs") — sửa lại trong bước sau khi ghi. **Mật khẩu gõ khi ghi sẽ được lưu dạng chữ thường trong jobs.json.** Sau khi ghi nên thêm bước *Chờ cửa sổ / Chờ hình ảnh* ở chỗ ứng dụng cần thời gian tải.

## Các loại bước

| Bước | Mô tả |
|---|---|
| Mở ứng dụng / file / URL | `notepad.exe`, `C:\Tools\app.exe`, `D:\bao-cao.xlsx`, `https://...` + tham số |
| Hiện nhắc nhở | Cửa sổ nhắc góc phải màn hình + âm báo; tùy chọn tạm dừng flow chờ xác nhận |
| Chờ (delay) | Nghỉ N ms |
| Chờ cửa sổ xuất hiện | Chờ tới khi có cửa sổ khớp (lỗi nếu hết timeout) |
| Kích hoạt cửa sổ | Đưa cửa sổ lên trên cùng, khôi phục nếu đang thu nhỏ |
| Click chuột | Trái/phải/giữa, double-click; tọa độ màn hình hoặc **tương đối theo cửa sổ** |
| Gõ văn bản | Unicode đầy đủ (tiếng Việt, emoji), xuống dòng = Enter |
| Nhấn phím | `Enter`, `Ctrl+S`, `Alt+F4`, `Win+R`, `Tab*3`, nhiều tổ hợp: `Ctrl+A, Delete` |
| Chạy lệnh (cmd) | Chạy ẩn, ghi output vào log; mã thoát ≠ 0 = lỗi; timeout 0 = không chờ |
| Đóng ứng dụng | Theo tên tiến trình (`notepad`, `EXCEL`); tùy chọn buộc đóng (kill) |
| Click vào hình ảnh / Chờ hình ảnh | Tìm hình mẫu đã chụp trên màn hình rồi click / chờ tới khi xuất hiện |
| Click vào chữ / Chờ chữ (OCR) | Tìm chữ hiển thị trên màn hình rồi click / chờ tới khi xuất hiện |

**Khớp cửa sổ:** nhập một phần tiêu đề (không phân biệt hoa thường) hoặc tên tiến trình, vd `Notepad`, `chrome`, `EXCEL`. Nút **↻ Làm mới** liệt kê các cửa sổ đang mở.

**Lấy tọa độ click:** bấm *Lấy tọa độ (3 giây)*, các cửa sổ ScheduleApp tạm ẩn đi, di chuột tới vị trí cần click. Nên bật *tọa độ tương đối theo cửa sổ* để click đúng kể cả khi cửa sổ bị di chuyển. *Xem vị trí* đưa con trỏ tới điểm đã lưu để kiểm tra.

## Lưu ý khi tự động hóa

- **Bộ gõ tiếng Việt (UniKey, EVKey, OpenKey…)** xử lý lại cả phím do app gửi vào nên có thể làm sai chữ (vd "gõ" → "õ"). Bước *Gõ văn bản* ở chế độ *Tự động* sẽ dán qua clipboard khi phát hiện bộ gõ (clipboard dạng chữ được khôi phục sau đó); có thể chọn cố định *Gõ từng phím* hoặc *Dán qua clipboard*.
- Notepad Windows 11 mở **cửa sổ mới** mỗi lần chạy `notepad.exe`; bước *Đóng ứng dụng* với `notepad` có thể chạm tới cả cửa sổ Notepad khác của bạn — luôn để bước nhắc nhở "lưu bài" trước đó.
- Máy phải **đang mở khóa màn hình** (Windows chặn giả lập chuột/phím khi khóa máy hoặc ở màn hình đăng nhập).
- Muốn điều khiển ứng dụng chạy quyền **Administrator** thì ScheduleApp cũng phải chạy quyền Administrator.
- Luôn thêm bước *Chờ cửa sổ* sau khi mở ứng dụng và nhập *Cửa sổ đích* cho bước gõ/nhấn phím để thao tác không bị gõ nhầm chỗ.
- Mỗi lúc chỉ chạy một flow; các flow đến hạn cùng lúc sẽ xếp hàng chạy lần lượt.
- Nếu máy tắt/ngủ quá 2 phút sau giờ chạy, lần đó được ghi log là *bỏ lỡ* và không chạy bù.

## Dữ liệu

- Công việc: `%AppData%\ScheduleApp\jobs.json` (có thể **Xuất/Nhập** để sao lưu hoặc chuyển máy).
- Log theo ngày: `%AppData%\ScheduleApp\logs\yyyy-MM-dd.log` (nút *Thư mục log*).

## Cấu trúc mã nguồn

```
Models/     Job, ScheduleConfig (tính lần chạy tới), ActionStep
Services/   Scheduler (tick mỗi giây), FlowRunner (hàng đợi, dừng), StepExecutor,
            JobStore (JSON), Log, StartupManager (registry Run)
Native/     Win32 P/Invoke, InputSimulator (SendInput), WindowHelper
Vision/     ScreenCapture, ImageMatcher (NCC + màu), ScreenOcr (Windows OCR), ScreenLocator
Recording/  MacroRecorder (hook chuột/bàn phím toàn hệ thống)
UI/         MainForm, JobEditorForm, StepEditorForm, ReminderForm, UiCommon,
            FlowDesigner (khung kéo thả), StepToolbox (hộp công cụ), StepVisuals (màu/icon)
```
