# ScheduleApp — Đặt lịch, nhắc nhở & tự động thao tác

Ứng dụng desktop Windows (C# WinForms, .NET 10) để:

- **Đặt lịch chạy** công việc: một lần, hằng ngày, hằng tuần, **hằng tháng** (ngày N, thứ X tuần thứ N, ngày làm việc đầu/cuối tháng), lặp lại mỗi N phút (**có thể giới hạn trong giờ hành chính**), hoặc chỉ chạy thủ công. Bỏ qua **ngày nghỉ lễ**, **chạy bù** lịch bị lỡ, **đánh thức máy** khi đang ngủ.
- **Kích hoạt theo sự kiện**: phím tắt, có file mới trong thư mục, ứng dụng mở/đóng, máy rảnh N phút, mở khóa màn hình, khi ScheduleApp khởi động, hoặc từ **dòng lệnh**.
- **Nhắc nhở**: hiện cửa sổ nhắc trước giờ chạy N phút, hoặc dùng bước "Hiện nhắc nhở" ngay trong flow.
- **Tự động thao tác theo luồng (flow)**: mở ứng dụng, chờ cửa sổ, click/gõ/nhấn phím, cuộn & kéo thả chuột, nhận dạng hình ảnh/chữ (OCR), **phần tử UI (UI Automation)**, **điều khiển Chrome/Edge**, chạy lệnh…
- **Lập trình flow không cần code**: **biến** `{{...}}`, **Nếu / Không thì**, **vòng lặp** (N lần, khi điều kiện đúng, **mỗi dòng Excel/CSV**, mỗi dòng văn bản, mỗi file), nhãn & nhảy, gọi công việc khác.
- **Chạy tin cậy**: **thử lại** từng bước, xử lý lỗi (bỏ qua / nhảy nhãn / chạy công việc dọn dẹp), **chụp màn hình khi lỗi**, **lịch sử chạy**, **thông báo Telegram / email / webhook**, **gỡ lỗi từng bước & điểm dừng**, **chế độ an toàn**.
- **Bảo mật**: mật khẩu/token lưu trong kho **bí mật mã hóa DPAPI**, che `***` trong nhật ký, không nằm trong file xuất.

## Chạy ứng dụng

```powershell
dotnet run                      # chạy bản debug
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
# -> publish\ScheduleApp.exe
```

Cần .NET 10 Desktop Runtime (hoặc publish với `--self-contained true` để không cần cài runtime).

**Dòng lệnh**

| Lệnh | Tác dụng |
|---|---|
| `ScheduleApp.exe --minimized` | Khởi động ẩn ở khay hệ thống |
| `ScheduleApp.exe --run "Tên công việc"` | Chạy ngay một công việc (gửi tới phiên bản đang chạy nếu có; khớp đúng tên hoặc một phần tên duy nhất) |
| `ScheduleApp.exe --stop` | Dừng flow đang chạy |

Chuột phải một công việc → **Tạo shortcut trên Desktop** để chạy bằng một cú nhấp đúp (gán được phím tắt trong Properties của shortcut, hoặc dùng cho Stream Deck). Biến môi trường `SCHEDULEAPP_DATA_DIR` đổi thư mục dữ liệu (bản portable).

## Cách dùng

1. **＋ Thêm công việc** (hoặc **Mẫu có sẵn…**) → đặt tên, nhóm, kiểu lịch / trình kích hoạt.
2. Dựng flow bằng **kéo thả** (xem bên dưới).
3. Bấm **▶ Chạy thử flow** (`F5`) để kiểm tra ngay, rồi **Lưu**.
4. Tick/bỏ tick ở danh sách chính để bật/tắt. Ô 🔍 lọc theo tên/nhóm; công việc được gom theo **nhóm**.

Bấm nút X chỉ thu ứng dụng xuống **khay hệ thống** — lịch vẫn chạy (chuột phải biểu tượng khay → *Chạy công việc* để chạy nhanh). Thoát hẳn: chuột phải biểu tượng khay → *Thoát*.
Tick **Khởi động cùng Windows** để app tự chạy (ẩn ở khay) khi đăng nhập.

**Dừng khẩn cấp:** `Ctrl+Shift+Q` (phím tắt toàn hệ thống) — dừng flow đang chạy và hủy các flow đang chờ.

## Ví dụ mẫu

Nút **Mẫu có sẵn…** mở kho mẫu nhúng trong ứng dụng; hai file trong [Samples/](Samples/) cũng nhập được bằng **Thêm → Nhập công việc…**. Mẫu có lịch hoặc trình kích hoạt được tắt sẵn — xem lại rồi tick để bật.

**Cơ bản** — [Samples/ScheduleApp-vi-du-mau.json](Samples/ScheduleApp-vi-du-mau.json) (14 mẫu): mở Notepad gõ tiếng Việt, nhắc nghỉ giải lao, nhắc họp, mở CRM bằng Chrome profile, OCR, click hình ảnh, gõ mẫu email, phím tắt, sao lưu robocopy, PowerShell, đóng ứng dụng…

**Nâng cao** — [Samples/ScheduleApp-mau-nang-cao.json](Samples/ScheduleApp-mau-nang-cao.json):

| # | Ví dụ | Minh họa |
|---|---|---|
| A1 | Đăng nhập Dynamics 365 (dùng chung) | Trình duyệt, `{{secret:…}}`, *Nếu bước trước lỗi* (đã đăng nhập sẵn thì bỏ qua) |
| A2 | Nhập liệu hàng loạt từ Excel vào form web | *Chạy công việc khác* (A1), **lặp mỗi dòng Excel** `{{row.Email}}`, Nếu/Không thì, bộ đếm, thử lại |
| A3 | Cảnh báo ổ C sắp đầy | Gán biến từ output PowerShell, so sánh số, *Dừng flow (tính là lỗi)* → gửi thông báo, bỏ qua ngày lễ, chạy bù |
| A4 | Tự chuyển hóa đơn PDF mới tải về | Kích hoạt **khi có file mới** `{{trigger.file}}`, `{{today:yyyy-MM}}` |
| A5 | Ghi chú nhanh `Ctrl+Alt+N` | Kích hoạt **phím tắt**, *Hỏi người dùng nhập* |
| A6 | Đánh số thứ tự file ảnh | **Lặp mỗi file**, `{{loop.index:000}}` |
| A7 | Mở lại Outlook/Teams khi mở khóa máy | Kích hoạt **mở khóa màn hình**, điều kiện *tiến trình đang chạy* (đảo ngược) |
| A8 | Chờ có mạng rồi đồng bộ | **Thử lại 5 lần**, *khi lỗi → nhảy tới nhãn* |
| A9 | Gõ vào Notepad bằng UI Automation | *Nhập vào phần tử UI*, phương án dự phòng khi lỗi |
| A10 | Ngày làm việc cuối tháng: chốt sổ | Lịch **hằng tháng**, đánh thức máy, hỏi chạy bù |
| A11 | Mỗi 30 phút trong giờ hành chính | Lặp theo phút **trong khung giờ 8:30–17:30, T2–T6** |

## Thiết kế flow bằng kéo thả

Trình soạn công việc có 3 vùng: **Hộp công cụ** (trái) · **Khung luồng** Bắt đầu → các bước → Kết thúc (giữa) · nút lệnh (phải). Phía trên là các tab **Lịch chạy · Kích hoạt khác · Biến · Lỗi & thông báo**.

| Thao tác | Cách làm |
|---|---|
| Thêm bước | Kéo một thao tác từ hộp công cụ, thả vào vị trí mong muốn (đường kẻ xanh báo chỗ chèn), điền thông tin → OK. Hoặc nhấp đúp thao tác để chèn sau bước đang chọn. Thêm *Nếu* / *Lặp* tự chèn luôn *Hết Nếu* / *Hết lặp* |
| Mở ứng dụng nhanh | Kéo file `.exe` / shortcut / tài liệu từ Explorer thả vào luồng |
| Sắp xếp | Kéo thẻ lên/xuống (hoặc `Ctrl+↑/↓`) — kéo thẻ *Nếu*/*Lặp* là di chuyển cả khối |
| Sửa | Nhấp đúp thẻ hoặc `Enter` |
| Bật/tắt, nhân bản, xóa | Chuột phải thẻ, hoặc `Space` / `Ctrl+D` / `Delete` (tắt *Nếu*/*Lặp* = bỏ qua cả khối) |
| Sao chép giữa các công việc | `Ctrl+C` / `Ctrl+V` |
| Điểm dừng | `F9` hoặc bấm vào lề trái thẻ (chấm đỏ) |

Các bước trong khối *Nếu* / *Lặp* được thụt lề, có thanh màu bên trái. Thẻ viền đỏ = lỗi cấu trúc (thiếu *Hết Nếu*, nhãn không tồn tại…), di chuột lên thẻ để xem chi tiết.

Màu thẻ theo nhóm: xanh dương = ứng dụng, tím = cửa sổ, cam = chuột & bàn phím, hồng = phần tử UI, xanh đậm = trình duyệt, xanh ngọc = nhận dạng màn hình, chàm = biến, xanh lá = điều kiện / lặp / điều khiển luồng, vàng = nhắc nhở.

## Biến

Mọi ô chữ của bước (đường dẫn, văn bản gõ, lệnh, bộ chọn, URL…) dùng được `{{tên}}`:

| Biến | Ý nghĩa |
|---|---|
| `{{today}}` `{{yesterday}}` `{{tomorrow}}` `{{now}}` `{{time}}` | Ngày / giờ hiện tại. Cộng trừ: `{{today-1}}`, `{{today+1M}}` (tháng), `{{now+30m}}`, `{{today+2w}}`; định dạng: `{{today:dd/MM/yyyy}}`, `{{now:HH:mm}}`, `{{today:dddd}}` (Thứ Năm) |
| `{{clipboard}}` · `{{env:USERNAME}}` · `{{secret:Tên}}` | Nội dung clipboard · biến môi trường · bí mật đã mã hóa |
| `{{random:1-100}}` · `{{guid}}` · `{{newline}}` · `{{tab}}` | Số ngẫu nhiên · mã GUID · xuống dòng · Tab |
| `{{job.name}}` `{{computer}}` `{{user}}` `{{run.trigger}}` `{{run.start}}` | Thông tin lần chạy |
| `{{lastOutput}}` · `{{lastError}}` | Output của lệnh cmd gần nhất · thông báo lỗi của bước lỗi gần nhất |
| `{{loop.index}}` `{{loop.count}}` · `{{row.TênCột}}` `{{row.1}}` · `{{item}}` `{{item.name}}` | Biến của vòng lặp |
| `{{trigger.file}}` `{{trigger.name}}` | File vừa xuất hiện (kích hoạt "có file mới") |

Định dạng giá trị biến: `{{ten:upper}}` `{{ten:lower}}` `{{ten:trim}}` `{{ten:len}}` `{{ten:nodiacritics}}` (bỏ dấu) `{{ten:url}}`, số `{{tien:N0}}` (1.234.568) `{{so:000}}`, ngày `{{ngay:dd/MM/yyyy}}`.
Khai báo giá trị ban đầu ở tab **Biến** của công việc; gán/đổi trong flow bằng bước **Gán biến**:

| Nguồn | Ví dụ |
|---|---|
| Giá trị | `Báo cáo {{today:dd-MM}}` |
| Phép tính | `{{dem}} + 1`, `{{tong}} * 1.1` |
| Clipboard / output lệnh / file văn bản / chữ OCR trong cửa sổ / giá trị phần tử UI | kèm **regex trích xuất** tùy chọn, vd `Mã đơn:\s*(\d+)` (lấy nhóm đầu tiên) |
| Hỏi người dùng | hộp thoại nhập (tùy chọn ẩn ký tự cho mật khẩu) |

Bước **Chạy lệnh** cũng lưu được output vào biến; bước **Trình duyệt → Đọc chữ** / **Chạy JavaScript** lưu kết quả vào biến.

## Điều kiện & vòng lặp

- **Nếu** … (**Không thì** …) **Hết Nếu** — điều kiện: so sánh giá trị (`=`, khác, chứa, bắt đầu bằng, `>`, `≥`, `<`, `≤`, rỗng, regex — tự so số nếu cả hai là số), cửa sổ đang mở, tiến trình đang chạy, file tồn tại, **hình ảnh / chữ có trên màn hình**, **phần tử UI tồn tại**, **bước trước bị lỗi**. Tick *Đảo ngược* để có "KHÔNG …". *Chờ tối đa* > 0 thì đợi tới khi điều kiện đúng.
- **Lặp** … **Hết lặp** — N lần · khi điều kiện đúng · **mỗi dòng file Excel (.xlsx) / CSV** (dòng đầu là tiêu đề; đọc được cả khi file đang mở trong Excel, chọn sheet) · mỗi dòng văn bản (file hoặc `{{biến}}`) · mỗi file trong thư mục (`*.pdf;*.xlsx`).
- **Thoát vòng lặp**, **Nhãn** + **Nhảy tới nhãn**, **Dừng flow** (tùy chọn tính là thất bại), **Chạy công việc khác** (flow con dùng chung biến — vd "Đăng nhập CRM" dùng cho nhiều công việc).

## Xử lý lỗi, lịch sử & thông báo

- Mỗi bước có **Khi bước lỗi**: *thử lại N lần cách X ms*, rồi *dừng flow* / *bỏ qua chạy tiếp* / *nhảy tới nhãn* / theo cài đặt của công việc.
- Tab **Lỗi & thông báo** của công việc: dừng khi lỗi, **công việc chạy khi thất bại** (dọn dẹp, gửi báo cáo — có `{{failed.message}}`), khi nào gửi thông báo (chỉ khi lỗi / mỗi lần / không).
- **Chụp màn hình khi lỗi** (`logs\screenshots\ngày\`), tự xóa ảnh cũ theo cài đặt.
- **📋 Lịch sử**: mọi lần chạy (kích hoạt bởi gì, thời lượng, kết quả, bước lỗi), lọc theo công việc / chỉ lần lỗi, xem ảnh lỗi ngay trong cửa sổ, mở nhật ký ngày đó.
- **⚙ Cài đặt → Thông báo**: Telegram bot (kèm ảnh lỗi), email SMTP (đính kèm ảnh), webhook Teams/Slack/Discord/Google Chat — có nút *Gửi thử*. Chạy thử từ trình soạn không gửi thông báo.

## Gỡ lỗi flow

- **▶ Chạy thử flow** (`F5`): thẻ đang chạy được tô xanh, bước lỗi tô đỏ sau khi chạy.
- **⤵ Chạy từ bước chọn** (hoặc chuột phải thẻ): bỏ qua các bước phía trên.
- **⏭ Chạy từng bước** / **điểm dừng** (`F9`): flow dừng trước bước, thanh gỡ lỗi hiện bước sắp chạy + giá trị mọi biến; `F10` bước tiếp, `F5` chạy tiếp, `Shift+F5` dừng.
- **▶ Thử bước này** trong trình soạn bước: chạy riêng bước đang soạn (gán biến, điều kiện, vòng lặp, phần tử UI, trình duyệt, lệnh) và hiện kết quả/giá trị biến.
- Bước **Ghi nhật ký** in giá trị biến ra nhật ký.

## Phần tử UI (UI Automation) — ổn định nhất cho ứng dụng Windows

**Click phần tử UI**, **Nhập vào phần tử UI**, **Chờ phần tử UI**, điều kiện *Phần tử UI tồn tại*, gán biến từ *giá trị phần tử*. Tìm nút / ô nhập theo thuộc tính thay vì tọa độ — không phụ thuộc vị trí cửa sổ, độ phân giải, DPI hay zoom; click bằng InvokePattern (không cần di chuột, phần tử bị che vẫn bấm được), nhập bằng ValuePattern (không bị bộ gõ UniKey làm sai chữ).

Bấm **◎ Bắt phần tử (3 giây)** rồi trỏ chuột vào nút/ô cần thao tác → bộ chọn được điền tự động, ví dụ `AutomationId=txtUser; ControlType=Edit` hoặc `Name=Lưu; ControlType=Button; Index=2`. Có thể sửa tay: `Name~=Đăng` (chứa), `ClassName=…`, `Index=N` (phần tử thứ N khi trùng). Ứng dụng không hỗ trợ UI Automation (một số app vẽ giao diện riêng, game) → dùng *Click vào hình ảnh*.

## Trình duyệt (Chrome / Edge)

Bước **Trình duyệt** điều khiển trang web qua DevTools Protocol — chính xác hơn nhiều so với OCR/tọa độ:

1. **Mở trình duyệt ở chế độ điều khiển** (Chrome hoặc Edge, kèm URL): dùng hồ sơ riêng của ScheduleApp (`%AppData%\ScheduleApp\browser-chrome`) — lần đầu đăng nhập các trang web, các lần sau giữ phiên đăng nhập.
2. Các hành động: **Mở URL**, **Click**, **Nhập giá trị** (kích hoạt sự kiện input/change — chạy được với form React/Angular/Dynamics 365), **Đọc chữ vào biến**, **Chờ phần tử**, **Chạy JavaScript**.
3. Bộ chọn: CSS (`#email`, `input[name=q]`, `button[type=submit]`), `xpath://button[.='Lưu']` hoặc `text:Đăng nhập`. Ô *Tab* chọn tab theo một phần URL/tiêu đề (trống = tab đầu tiên).

Mẹo: chuột phải phần tử trên trang → *Inspect* → chuột phải dòng HTML → *Copy → Copy selector*. Cổng điều khiển mặc định 9222 (đổi trong Cài đặt).

## Nhận dạng màn hình

Dùng khi vị trí nút bấm thay đổi (trang web, cửa sổ đổi kích thước…) — ổn định hơn click theo tọa độ.

**Click vào hình ảnh / Chờ hình ảnh xuất hiện**
1. Bấm **✂ Chụp hình mẫu** → màn hình đóng băng → kéo chọn vùng cần tìm (vd nút *Lưu*). Hình mẫu lưu luôn trong flow.
2. **Thử tìm trên màn hình**: khung đỏ khoanh vị trí tìm được, con trỏ được đưa tới điểm sẽ click, kèm % độ khớp.
3. *Độ khớp tối thiểu* (mặc định 85%): giảm nếu không tìm thấy, tăng nếu click nhầm. Điểm khớp tính cả hình dạng lẫn màu, nên nút bị mờ (disabled) khác màu sẽ không bị nhận nhầm.
4. *Lệch khỏi tâm X, Y*: click lệch so với tâm hình (vd chụp nhãn "Tên đăng nhập", lệch Y = 30 để click vào ô nhập bên dưới).

Hình mẫu ghi nhớ mức scale màn hình lúc chụp và **tự co giãn** khi chạy trên màn hình có scale khác (100% ↔ 125% ↔ 150%). Vẫn nên giữ nguyên mức zoom trình duyệt như lúc chụp; tránh chụp vùng một màu hoặc chứa chữ/số hay thay đổi.

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
- Nhấn giữ rồi kéo → *Kéo thả chuột*; lăn bánh xe → *Cuộn chuột* (các lần cuộn liên tiếp được gộp).
- Chuyển sang cửa sổ khác → tự chèn *Chờ cửa sổ xuất hiện* trước thao tác đầu tiên trên cửa sổ đó.
- Chữ gõ liên tiếp → một bước *Gõ văn bản* (Backspace sửa chữ được tính luôn; hỗ trợ Unikey/EVKey). Gõ vào **ô mật khẩu** → lưu thành `{{secret:MatKhau}}` thay vì mật khẩu thật (thêm bí mật "MatKhau" trong 🔑 Bí mật).
- Enter, Tab, phím mũi tên, F1–F12, tổ hợp Ctrl/Alt/Win → *Nhấn phím* (phím lặp gộp thành `Tab*3`).
- Khoảng nghỉ thật giữa các thao tác được giữ lại (0,15–5 giây).

Hạn chế: với bộ gõ Telex có sẵn của Windows, chữ được ghi dạng phím gốc (vd "tieengs") — sửa lại trong bước sau khi ghi. Nên thay các click quan trọng bằng *Click phần tử UI* / *Click vào hình ảnh*.

## Các loại bước

| Nhóm | Bước | Mô tả |
|---|---|---|
| Ứng dụng | Mở ứng dụng / file / URL · Đóng ứng dụng · Chạy lệnh (cmd) | `notepad.exe`, `D:\bao-cao.xlsx`, `https://…` · theo tên tiến trình, tùy chọn kill · chạy ẩn, output vào log/biến, mã thoát ≠ 0 = lỗi |
| Cửa sổ | Chờ cửa sổ xuất hiện · Kích hoạt cửa sổ | Theo tiêu đề / `exe:tiến_trình` |
| Chuột & bàn phím | Click chuột · Gõ văn bản · Nhấn phím · Cuộn chuột · Kéo thả chuột | Tọa độ màn hình hoặc tương đối theo cửa sổ; Unicode đầy đủ; `Ctrl+S`, `Tab*3`, `Ctrl+A, Delete` |
| Phần tử UI | Click / Nhập vào / Chờ phần tử UI | UI Automation — xem mục riêng |
| Trình duyệt | Trình duyệt (Chrome/Edge) | Mở, URL, click, nhập, đọc, chờ, JavaScript |
| Nhận dạng màn hình | Click / Chờ hình ảnh · Click / Chờ chữ (OCR) | Xem mục riêng |
| Biến & dữ liệu | Gán biến · Ghi nhật ký | Xem mục Biến |
| Điều kiện & lặp | Nếu · Không thì · Lặp · Thoát vòng lặp · Nhãn · Nhảy tới nhãn | Xem mục riêng |
| Điều khiển luồng | Chờ (delay) · Hiện nhắc nhở · Chạy công việc khác · Dừng flow | Nhắc nhở có thể tạm dừng flow chờ xác nhận |

**Khớp cửa sổ:** nhập một phần tiêu đề (không phân biệt hoa thường) hoặc tên tiến trình, vd `Notepad`, `chrome`, `EXCEL`. Nút **↻ Làm mới** liệt kê các cửa sổ đang mở.

**Lấy tọa độ click:** bấm *Lấy tọa độ (3 giây)*, các cửa sổ ScheduleApp tạm ẩn đi, di chuột tới vị trí cần click. Nên bật *tọa độ tương đối theo cửa sổ* để click đúng kể cả khi cửa sổ bị di chuyển. *Xem vị trí* đưa con trỏ tới điểm đã lưu để kiểm tra.

## Lịch chạy & kích hoạt

- **Hằng tháng**: ngày N (tháng ít ngày hơn → ngày cuối tháng), *Thứ X của tuần thứ N / tuần cuối*, ngày cuối tháng, **ngày làm việc đầu / cuối tháng** (bỏ T7, CN và ngày lễ).
- **Lặp theo phút + khung giờ**: vd mỗi 30 phút, chỉ 8:00–17:30 các ngày T2–T6.
- **Ngày nghỉ lễ** (⚙ Cài đặt → Ngày nghỉ): `dd/MM` lặp hằng năm (01/01, 30/04, 01/05, 02/09 có sẵn) hoặc `dd/MM/yyyy` cho ngày cụ thể (Tết âm lịch, Giỗ Tổ, nghỉ bù). Tick *Bỏ qua ngày nghỉ lễ* ở công việc.
- **Khi lỡ lịch** (máy tắt / ngủ / ScheduleApp không chạy): bỏ qua, **chạy bù một lần**, hoặc **hỏi tôi**. Phát hiện cả các lần lỡ trong lúc ScheduleApp tắt (tối đa 7 ngày).
- **Đánh thức máy** khi đang Sleep 1 phút trước giờ chạy (cần bật *Allow wake timers* trong Power Options; không áp dụng khi máy tắt hẳn). Khi flow chạy, máy không tự ngủ / tắt màn hình (tắt được trong Cài đặt).
- **Kích hoạt khác** (tab *Kích hoạt khác*): phím tắt toàn hệ thống · có file mới trong thư mục (mỗi file chạy một lần, chờ file ghi xong; nhận cả file trình duyệt vừa tải xong) · ứng dụng vừa mở / vừa đóng · máy rảnh N phút · mở khóa màn hình · khi ScheduleApp khởi động.

## Bảo mật

- **🔑 Bí mật**: lưu mật khẩu / token mã hóa bằng Windows DPAPI (chỉ tài khoản Windows của bạn trên máy này giải mã được), dùng trong flow bằng `{{secret:Tên}}`. Giá trị không hiển thị lại, được che `***` trong nhật ký, không nằm trong `jobs.json` và không bị xuất ra file khi chia sẻ công việc.
- Mật khẩu email và token Telegram trong Cài đặt cũng được mã hóa DPAPI.
- Hỏi người dùng nhập với tùy chọn *ẩn ký tự* cũng không ghi giá trị vào log.

## Chế độ an toàn

⚙ Cài đặt → *Chế độ an toàn*: khi flow đang chạy mà bạn bấm chuột / gõ phím vào ứng dụng khác, flow **tạm dừng trước bước kế tiếp** và hỏi *Chạy tiếp* hay *Dừng* — tránh flow gõ nhầm vào chỗ bạn đang làm. Thao tác do ScheduleApp giả lập và thao tác trên cửa sổ của ScheduleApp không bị tính.

## Lưu ý khi tự động hóa

- **Bộ gõ tiếng Việt (UniKey, EVKey, OpenKey…)** xử lý lại cả phím do app gửi vào nên có thể làm sai chữ (vd "gõ" → "õ"). Bước *Gõ văn bản* ở chế độ *Tự động* sẽ dán qua clipboard khi phát hiện bộ gõ; *Nhập vào phần tử UI* và *Trình duyệt → Nhập giá trị* không bị ảnh hưởng.
- Notepad Windows 11 mở **cửa sổ mới** mỗi lần chạy `notepad.exe`; bước *Đóng ứng dụng* với `notepad` có thể chạm tới cả cửa sổ Notepad khác của bạn.
- Máy phải **đang mở khóa màn hình** để giả lập chuột/phím (Windows chặn khi khóa máy). Các bước *Trình duyệt*, *Chạy lệnh*, *Gán biến* không cần màn hình.
- Muốn điều khiển ứng dụng chạy quyền **Administrator** thì ScheduleApp cũng phải chạy quyền Administrator.
- Mỗi lúc chỉ chạy một flow; các flow đến hạn cùng lúc sẽ xếp hàng chạy lần lượt.

## Dữ liệu

Thư mục `%AppData%\ScheduleApp` (hoặc `SCHEDULEAPP_DATA_DIR`):

| File | Nội dung |
|---|---|
| `jobs.json` | Công việc (có thể **Xuất/Nhập** để sao lưu hoặc chuyển máy — tham chiếu giữa các công việc được giữ đúng khi nhập) |
| `settings.json` | Cài đặt chung, ngày nghỉ, kênh thông báo (mật khẩu mã hóa) |
| `secrets.json` | Bí mật (mã hóa DPAPI) |
| `history.jsonl` | Lịch sử chạy (2.000 lần gần nhất) |
| `logs\yyyy-MM-dd.log` · `logs\screenshots\` | Nhật ký theo ngày · ảnh chụp màn hình lỗi |
| `browser-chrome\` · `browser-edge\` | Hồ sơ trình duyệt dùng cho bước *Trình duyệt* |

## Cấu trúc mã nguồn

```
Models/      Job, ActionStep, ScheduleConfig (tính lần chạy tới), JobTrigger, AppSettings,
             FlowStructure (ghép khối Nếu/Lặp, nhãn, kiểm tra lỗi cấu trúc)
Services/    Scheduler (tick mỗi giây, chạy bù, đánh thức máy), FlowRunner (hàng đợi, dừng, lịch sử, thông báo),
             StepExecutor (thực thi từng loại bước), TriggerManager (phím tắt, file mới, tiến trình, rảnh, mở khóa),
             CommandServer (dòng lệnh qua named pipe), NotificationService, RunHistory, SettingsStore,
             SecretStore + Protector (DPAPI), JobStore (JSON), Log, StartupManager
Services/Engine/  FlowEngine (bộ đếm chương trình: điều kiện, vòng lặp, nhãn, thử lại, xử lý lỗi, gỡ lỗi),
                  FlowContext (biến, tùy chọn chạy), VariableExpander ({{…}}), ConditionEvaluator, LoopFrame
Services/Data/    TabularReader (đọc .xlsx / .csv không cần Excel)
Automation/  UiElementFinder (UI Automation), BrowserClient (Chrome DevTools Protocol)
Native/      Win32 P/Invoke, InputSimulator (SendInput: click, phím, cuộn, kéo thả), WindowHelper,
             PowerHelper (giữ máy thức, hẹn giờ đánh thức, thời gian rảnh, DPI), UserInputGuard (chế độ an toàn)
Vision/      ScreenCapture, ImageMatcher (NCC + màu), ScreenOcr (Windows OCR), ScreenLocator (co giãn theo DPI)
Recording/   MacroRecorder (hook chuột/bàn phím toàn hệ thống)
UI/          MainForm, JobEditorForm, StepEditorForm, FlowDesigner, StepToolbox, StepVisuals, TriggerEditorForm,
             HistoryForm, SettingsForm, SecretsForm, TemplatePickerForm, PromptForms (nhập liệu, xác nhận, thanh gỡ lỗi),
             ReminderForm, VisionForms, UiCommon
Samples/     Mẫu công việc (nhúng vào ứng dụng cho mục "Mẫu có sẵn…")
```
