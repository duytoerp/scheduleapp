# ScheduleApp — Đặt lịch, nhắc nhở & tự động thao tác

Ứng dụng desktop Windows (C# WinForms, .NET 10) để:

- **Đặt lịch chạy** công việc: một lần, hằng ngày, hằng tuần, **hằng tháng** (ngày N, thứ X tuần thứ N, ngày làm việc đầu/cuối tháng), lặp lại mỗi N phút (**có thể giới hạn trong giờ hành chính**), hoặc chỉ chạy thủ công. Bỏ qua **ngày nghỉ lễ**, **chạy bù** lịch bị lỡ, **đánh thức máy** khi đang ngủ.
- **Kích hoạt theo sự kiện**: phím tắt, có file mới trong thư mục, ứng dụng mở/đóng, máy rảnh N phút, mở khóa màn hình, khi ScheduleApp khởi động, hoặc từ **dòng lệnh**.
- **Nhắc nhở**: hiện cửa sổ nhắc trước giờ chạy N phút, hoặc dùng bước "Hiện nhắc nhở" ngay trong flow.
- **Tự động thao tác theo luồng (flow)**: mở ứng dụng, chờ cửa sổ, click/gõ/nhấn phím, cuộn & kéo thả chuột, nhận dạng hình ảnh/chữ (OCR), **phần tử UI (UI Automation)**, **điều khiển Chrome/Edge**, chạy lệnh…
- **Lập trình flow không cần code**: **biến** `{{...}}`, **Nếu / Không thì**, **vòng lặp** (N lần, khi điều kiện đúng, **mỗi dòng Excel/CSV**, mỗi dòng văn bản, mỗi file), nhãn & nhảy, gọi công việc khác.
- **Dữ liệu & tích hợp**: **ghi kết quả vào Excel / CSV** (từng dòng đang xử lý), **gọi REST API** (kết nối sẵn cho **Dynamics 365 Web API** qua Microsoft Entra ID), **hỏi AI (Claude)** để trích dữ liệu từ chữ hoặc ảnh màn hình, **kích hoạt khi có email mới** (Outlook / IMAP, lưu file đính kèm), biến **danh sách** và **JSON**.
- **Chạy tin cậy**: **thử lại** từng bước, xử lý lỗi (bỏ qua / nhảy nhãn / chạy công việc dọn dẹp), **chụp màn hình khi lỗi**, **lịch sử chạy + thống kê**, **thông báo Telegram / email / webhook**, **điều khiển từ xa qua Telegram**, **gỡ lỗi từng bước & điểm dừng**, **chế độ an toàn**.
- **Soạn flow an toàn**: **hoàn tác / làm lại** (`Ctrl+Z` / `Ctrl+Y`), **lưu phiên bản cũ** của từng công việc để khôi phục.
- **Bảo mật**: mật khẩu/token lưu trong kho **bí mật mã hóa DPAPI**, che `***` trong nhật ký, không nằm trong file xuất.
- **Phát hành**: bộ cài không cần quyền admin, **tự cập nhật** từ GitHub Releases hoặc thư mục dùng chung.

## Chạy ứng dụng

```powershell
dotnet run --project ScheduleApp.csproj    # chạy bản debug
.\build.ps1                                # kiểm thử + publish\ScheduleApp.exe + version.json + bộ cài
```

Bản publish mặc định cần .NET 10 Desktop Runtime (`.\build.ps1 -SelfContained` để không cần cài runtime, file lớn hơn). Bộ cài `publish\ScheduleApp-Setup-x.y.z.exe` (cần [Inno Setup 6](https://jrsoftware.org/isdl.php) trên máy build) cài vào `%LocalAppData%\Programs\ScheduleApp`, không cần quyền Administrator, tùy chọn tạo biểu tượng Desktop và khởi động cùng Windows.

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

Nút **Mẫu có sẵn…** mở kho mẫu nhúng trong ứng dụng; ba file trong [Samples/](Samples/) cũng nhập được bằng **Thêm → Nhập công việc…**. Mẫu có lịch hoặc trình kích hoạt được tắt sẵn — xem lại rồi tick để bật.

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

**Tích hợp** — [Samples/ScheduleApp-mau-tich-hop.json](Samples/ScheduleApp-mau-tich-hop.json):

| # | Ví dụ | Minh họa |
|---|---|---|
| B1 | Nhập khách hàng từ Excel vào Dynamics 365 (web) + ghi kết quả vào từng dòng | **Ghi Excel** `{{row.rowNumber}}`, *Bỏ qua, sang lần lặp kế* (chạy lại thì bỏ dòng đã nhập), *khi lỗi → nhảy nhãn* ghi lỗi vào Excel, *Gửi thông báo* |
| B2 | Tạo liên hệ Dynamics 365 qua **Web API** từ CSV | **Gọi API** với kết nối Entra ID, `$filter`, `value.length`, POST JSON `{{row.Tên:json}}`, ghi `ContactId` ngược vào CSV |
| B3 | Email hóa đơn → AI trích số liệu → sổ Excel + Telegram | Kích hoạt **email mới**, **Hỏi AI** trả JSON, *Trích từ JSON*, `{{email.attachments:first}}` |
| B4 | AI đọc số liệu đang hiện trên màn hình | **Hỏi AI kèm ảnh chụp màn hình** (phần mềm không copy được chữ) |
| B5 | Theo dõi tỷ giá USD/VND mỗi giờ | Gọi API công khai, ghi lịch sử vào CSV, cảnh báo khi vượt ngưỡng |
| B6 | Gom danh sách file PDF rồi gửi một tin | Biến **danh sách**: *Thêm vào cuối danh sách*, `{{ds:count}}`, `{{ds:sort}}` |

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
| Hoàn tác / làm lại | `Ctrl+Z` / `Ctrl+Y` hoặc nút **↶ Hoàn tác** / **↷ Làm lại** (tối đa 100 thay đổi) |
| Khôi phục bản cũ | **Phiên bản cũ…**: mỗi lần lưu thay đổi, bản trước được giữ lại (30 bản gần nhất / công việc); xem các bước của từng bản rồi khôi phục |

Các bước trong khối *Nếu* / *Lặp* được thụt lề, có thanh màu bên trái. Thẻ viền đỏ = lỗi cấu trúc (thiếu *Hết Nếu*, nhãn không tồn tại…), di chuột lên thẻ để xem chi tiết.

Màu thẻ theo nhóm: xanh dương = ứng dụng, tím = cửa sổ, cam = chuột & bàn phím, hồng = phần tử UI, xanh đậm = trình duyệt, xanh ngọc = nhận dạng màn hình, chàm = biến & ghi dữ liệu, xanh lục = tích hợp (API, AI, thông báo), xanh lá = điều kiện / lặp / điều khiển luồng, vàng = nhắc nhở.

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
| `{{email.subject}}` `{{email.from}}` `{{email.body}}` `{{email.attachments}}` | Email vừa nhận (kích hoạt "có email mới") |
| `{{http.status}}` `{{http.body}}` · `{{ai.answer}}` · `{{lastRow}}` | Kết quả lần gọi API gần nhất · câu trả lời AI · dòng Excel/CSV vừa ghi |

Định dạng giá trị biến: `{{ten:upper}}` `{{ten:lower}}` `{{ten:trim}}` `{{ten:len}}` `{{ten:nodiacritics}}` (bỏ dấu) `{{ten:url}}` `{{ten:json}}` (đặt vào chuỗi JSON an toàn), số `{{tien:N0}}` (1.234.568) `{{so:000}}`, ngày `{{ngay:dd/MM/yyyy}}`.

**Danh sách** = biến có mỗi phần tử một dòng (tạo bằng *Gán biến → Thêm vào cuối danh sách / Tách chuỗi*, hoặc từ `value[*].name` của JSON): `{{ds:count}}` `{{ds:first}}` `{{ds:last}}` `{{ds:item(2)}}` `{{ds:item(-1)}}` `{{ds:join(, )}}` `{{ds:sort}}` `{{ds:unique}}`; lặp qua bằng *Lặp → Mỗi dòng văn bản* với `{{ds}}`.
Khai báo giá trị ban đầu ở tab **Biến** của công việc; gán/đổi trong flow bằng bước **Gán biến**:

| Nguồn | Ví dụ |
|---|---|
| Giá trị | `Báo cáo {{today:dd-MM}}` |
| Phép tính | `{{dem}} + 1`, `{{tong}} * 1.1` |
| Clipboard / output lệnh / file văn bản / chữ OCR trong cửa sổ / giá trị phần tử UI | kèm **regex trích xuất** tùy chọn, vd `Mã đơn:\s*(\d+)` (lấy nhóm đầu tiên) |
| Hỏi người dùng | hộp thoại nhập (tùy chọn ẩn ký tự cho mật khẩu) |
| Thêm vào cuối danh sách · Tách chuỗi | `{{f.name}}` · `a@x.com; b@y.com` tách theo `;` |
| Trích từ JSON | `{{http.body}}` với đường dẫn `value[0].name`, `items[*].id` (mọi phần tử), `value[0]["@odata.etag"]`, `value.length` |

Bước **Chạy lệnh** cũng lưu được output vào biến; bước **Trình duyệt → Đọc chữ** / **Chạy JavaScript** lưu kết quả vào biến.

## Điều kiện & vòng lặp

- **Nếu** … (**Không thì** …) **Hết Nếu** — điều kiện: so sánh giá trị (`=`, khác, chứa, bắt đầu bằng, `>`, `≥`, `<`, `≤`, rỗng, regex — tự so số nếu cả hai là số), cửa sổ đang mở, tiến trình đang chạy, file tồn tại, **hình ảnh / chữ có trên màn hình**, **phần tử UI tồn tại**, **bước trước bị lỗi**. Tick *Đảo ngược* để có "KHÔNG …". *Chờ tối đa* > 0 thì đợi tới khi điều kiện đúng.
- **Lặp** … **Hết lặp** — N lần · khi điều kiện đúng · **mỗi dòng file Excel (.xlsx) / CSV** (dòng đầu là tiêu đề; đọc được cả khi file đang mở trong Excel, chọn sheet) · mỗi dòng văn bản (file hoặc `{{biến}}`) · mỗi file trong thư mục (`*.pdf;*.xlsx`).
- **Thoát vòng lặp**, **Bỏ qua, sang lần lặp kế**, **Nhãn** + **Nhảy tới nhãn**, **Dừng flow** (tùy chọn tính là thất bại), **Chạy công việc khác** (flow con dùng chung biến — vd "Đăng nhập CRM" dùng cho nhiều công việc).

## Ghi Excel / CSV

Bước **Ghi Excel / CSV** ghi thẳng vào `.xlsx` / `.csv` không cần mở Excel — giữ nguyên định dạng ô, công thức khác, các sheet khác và bảng (*Format as Table* được nới xuống khi thêm dòng). Mỗi dòng của ô nội dung là một ô cần ghi: `TrangThai=Đã nhập`, `MaDon={{maDon}}`, `NgayNhap={{now:dd/MM/yyyy HH:mm}}`; cột chưa có được thêm vào sau cột cuối.

- **Thêm dòng mới vào cuối** — nhật ký, sổ hóa đơn… File chưa có sẽ tự tạo (kèm dòng tiêu đề).
- **Sửa ô của một dòng có sẵn** — theo **số dòng Excel** (trong vòng lặp *Mỗi dòng Excel/CSV* dùng `{{row.rowNumber}}` để ghi kết quả vào đúng dòng đang xử lý) hoặc theo **cột khóa** `MaKH={{row.MaKH}}`.
- Số "thuần" được ghi dạng số (Excel tính toán được); mã có số 0 ở đầu (`007`) giữ dạng chữ. Số dòng vừa ghi có trong `{{lastRow}}`.
- Excel **khóa file khi đang mở** — đóng file trước khi chạy (bước báo lỗi rõ ràng; bật *Thử lại* nếu cần).

Mẫu thường dùng: lặp mỗi dòng Excel → nếu `{{row.TrangThai}}` bắt đầu bằng "Đã nhập" thì *Bỏ qua, sang lần lặp kế* → nhập liệu → ghi `TrangThai=Đã nhập` (hoặc `Lỗi: {{lastError}}` qua *khi lỗi → nhảy nhãn*). Chạy lại sau khi lỗi giữa chừng sẽ tiếp tục đúng chỗ.

## Gọi API (REST) — Dynamics 365 Web API, Microsoft Graph, API nội bộ

**⚙ Cài đặt → Kết nối API**: khai báo một lần URL gốc + cách xác thực, có nút *Thử kết nối* và *Mẫu Dynamics 365*:

| Xác thực | Dùng cho |
|---|---|
| Microsoft Entra ID (client credentials) | **Dynamics 365 / Dataverse**, Microsoft Graph — Tenant ID, Client ID, Client secret; scope tự lấy từ URL gốc (`https://<org>.crm5.dynamics.com/.default`) |
| Tài khoản Windows (NTLM/Kerberos) | Dynamics 365 on-premises, SharePoint / API nội bộ trong domain |
| Bearer token · Basic · Khóa API trong header · OAuth 2.0 client credentials | API khác |

Dynamics 365: đăng ký ứng dụng trong Azure Portal (*App registrations*) → tạo *Client secret* → thêm ứng dụng làm *Application User* trong Power Platform admin center và gán security role.

Bước **Gọi API**: chọn phương thức (GET/POST/PATCH/PUT/DELETE) và kết nối, nhập phần sau URL gốc (`contacts?$select=fullname&$filter=emailaddress1 eq '{{row.Email:url}}'`), header thêm, body JSON (dùng `{{biến:json}}` để thoát dấu nháy). Kết quả: `{{http.status}}`, `{{http.body}}`, hoặc trích thẳng vào biến bằng đường dẫn JSON (`value[0].contactid`, `value.length`). Mã trả về ≥ 400 là bước lỗi, kèm thông điệp lỗi OData — tick *Không báo lỗi khi API trả mã lỗi* để tự kiểm tra `{{http.status}}`. Token được lấy một lần và dùng lại tới khi hết hạn.

## Hỏi AI (Claude)

Bước **Hỏi AI** gửi yêu cầu cho Claude (nhập khóa API trong **⚙ Cài đặt → Tích hợp**, chọn mô hình), câu trả lời lưu vào biến — AI được dặn chỉ trả đúng kết quả để dùng làm giá trị biến:

- Trích dữ liệu: *"Trích số hóa đơn, ngày, tổng tiền từ email sau, trả về JSON {…}: {{email.body}}"* → *Gán biến → Trích từ JSON*.
- Phân loại / tóm tắt / dịch nội dung, chuẩn hóa địa chỉ, tên…
- **Kèm ảnh chụp màn hình** (cả màn hình hoặc một cửa sổ): đọc số liệu trên phần mềm không copy được chữ, kiểm tra trạng thái hiển thị.

Nội dung yêu cầu (và ảnh nếu bật) được gửi tới Anthropic để xử lý — không gửi dữ liệu nhạy cảm nếu chính sách công ty không cho phép.

## Xử lý lỗi, lịch sử & thông báo

- Mỗi bước có **Khi bước lỗi**: *thử lại N lần cách X ms*, rồi *dừng flow* / *bỏ qua chạy tiếp* / *nhảy tới nhãn* / theo cài đặt của công việc.
- Tab **Lỗi & thông báo** của công việc: dừng khi lỗi, **công việc chạy khi thất bại** (dọn dẹp, gửi báo cáo — có `{{failed.message}}`), khi nào gửi thông báo (chỉ khi lỗi / mỗi lần / không).
- **Chụp màn hình khi lỗi** (`logs\screenshots\ngày\`), tự xóa ảnh cũ theo cài đặt.
- **📋 Lịch sử**: mọi lần chạy (kích hoạt bởi gì, thời lượng, kết quả, bước lỗi), lọc theo công việc / chỉ lần lỗi, xem ảnh lỗi ngay trong cửa sổ, mở nhật ký ngày đó.
- **📋 Lịch sử → Thống kê**: theo 7 / 30 / 90 ngày hoặc toàn bộ — số lần chạy, tỉ lệ thành công, thời gian trung bình / lâu nhất, **bước hay lỗi nhất**, lỗi gần nhất của từng công việc, tổng thời gian máy tự làm, biểu đồ thành công / lỗi theo ngày.
- **⚙ Cài đặt → Thông báo**: Telegram bot (kèm ảnh lỗi), email SMTP (đính kèm ảnh), webhook Teams/Slack/Discord/Google Chat — có nút *Gửi thử*. Chạy thử từ trình soạn không gửi thông báo.
- Bước **Gửi thông báo** gửi tin tùy ý ngay trong flow (vd "Đã xử lý {{loop.count}} dòng"), tùy chọn kèm ảnh màn hình.

## Điều khiển từ xa qua Telegram

**⚙ Cài đặt → Thông báo → Nhận lệnh điều khiển**: nhắn cho bot từ điện thoại (chỉ đúng chat id đã cấu hình mới điều khiển được; lệnh gửi lúc máy tắt bị bỏ qua):

| Lệnh | Tác dụng |
|---|---|
| `/list` | Danh sách công việc kèm số thứ tự và lần chạy tới |
| `/run 3` · `/run Báo cáo` | Chạy công việc theo số thứ tự hoặc tên |
| `/stop` | Dừng flow đang chạy |
| `/status` | Đang chạy gì, 5 lịch sắp tới |
| `/history 10` | 10 lần chạy gần nhất |
| `/screenshot` | Chụp màn hình máy tính gửi về điện thoại |

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
   - Ô **Hồ sơ (profile)**: mỗi tên là một hồ sơ riêng với đăng nhập riêng (vd "Kế toán", "Tài khoản test") — gõ tên mới để tạo, trống = hồ sơ mặc định. Chỉ một trình duyệt điều khiển chạy cùng lúc: mở hồ sơ khác thì trình duyệt điều khiển đang mở được đóng trước.
   - **Sao chép hồ sơ thật…**: chép một hồ sơ Chrome/Edge bạn đang dùng (vd hồ sơ "Tai") sang ScheduleApp — giữ đăng nhập, mật khẩu đã lưu, tiện ích, dấu trang; không chép bộ nhớ đệm, không đổi hồ sơ gốc. Chrome/Edge không cho điều khiển trực tiếp hồ sơ đang dùng (từ bản 136), nên ScheduleApp dùng bản sao. Cần đóng Chrome/Edge đang mở hồ sơ đó trong lúc chép.
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

- Click vào nút / ô nhập / mục menu có tên → **Click phần tử UI** (bộ chọn UI Automation, không phụ thuộc tọa độ — tắt được bằng ô trên thanh "Đang ghi"); phần tử không nhận diện được → *Click chuột* với tọa độ tương đối theo cửa sổ (`exe:…`). Hai click nhanh cùng chỗ → double-click.
- Nhấn giữ rồi kéo → *Kéo thả chuột*; lăn bánh xe → *Cuộn chuột* (các lần cuộn liên tiếp được gộp).
- Chuyển sang cửa sổ khác → tự chèn *Chờ cửa sổ xuất hiện* trước thao tác đầu tiên trên cửa sổ đó.
- Chữ gõ liên tiếp → một bước *Gõ văn bản* (Backspace sửa chữ được tính luôn; hỗ trợ Unikey/EVKey). Gõ vào **ô mật khẩu** → lưu thành `{{secret:MatKhau}}` thay vì mật khẩu thật (thêm bí mật "MatKhau" trong 🔑 Bí mật).
- Enter, Tab, phím mũi tên, F1–F12, tổ hợp Ctrl/Alt/Win → *Nhấn phím* (phím lặp gộp thành `Tab*3`).
- Khoảng nghỉ thật giữa các thao tác được giữ lại (0,15–5 giây).

Hạn chế: với bộ gõ Telex có sẵn của Windows, chữ được ghi dạng phím gốc (vd "tieengs") — sửa lại trong bước sau khi ghi. Click còn ghi theo tọa độ nên xem lại, thay bằng *Click vào hình ảnh* nếu cần.

## Các loại bước

| Nhóm | Bước | Mô tả |
|---|---|---|
| Ứng dụng | Mở ứng dụng / file / URL · Đóng ứng dụng · Chạy lệnh (cmd) | `notepad.exe`, `D:\bao-cao.xlsx`, `https://…` · theo tên tiến trình, tùy chọn kill · chạy ẩn, output vào log/biến, mã thoát ≠ 0 = lỗi |
| Cửa sổ | Chờ cửa sổ xuất hiện · Kích hoạt cửa sổ | Theo tiêu đề / `exe:tiến_trình` |
| Chuột & bàn phím | Click chuột · Gõ văn bản · Nhấn phím · Cuộn chuột · Kéo thả chuột | Tọa độ màn hình hoặc tương đối theo cửa sổ; Unicode đầy đủ; `Ctrl+S`, `Tab*3`, `Ctrl+A, Delete` |
| Phần tử UI | Click / Nhập vào / Chờ phần tử UI | UI Automation — xem mục riêng |
| Trình duyệt | Trình duyệt (Chrome/Edge) | Mở, URL, click, nhập, đọc, chờ, JavaScript |
| Nhận dạng màn hình | Click / Chờ hình ảnh · Click / Chờ chữ (OCR) | Xem mục riêng |
| Biến & dữ liệu | Gán biến · Ghi Excel / CSV · Ghi nhật ký | Xem mục Biến, Ghi Excel |
| Tích hợp | Gọi API (HTTP / REST) · Hỏi AI (Claude) · Gửi thông báo | Xem mục riêng |
| Điều kiện & lặp | Nếu · Không thì · Lặp · Thoát vòng lặp · Bỏ qua, sang lần lặp kế · Nhãn · Nhảy tới nhãn | Xem mục riêng |
| Điều khiển luồng | Chờ (delay) · Hiện nhắc nhở · Chạy công việc khác · Dừng flow | Nhắc nhở có thể tạm dừng flow chờ xác nhận |

**Khớp cửa sổ:** nhập một phần tiêu đề (không phân biệt hoa thường) hoặc tên tiến trình, vd `Notepad`, `chrome`, `EXCEL`. Nút **↻ Làm mới** liệt kê các cửa sổ đang mở.

**Lấy tọa độ click:** bấm *Lấy tọa độ (3 giây)*, các cửa sổ ScheduleApp tạm ẩn đi, di chuột tới vị trí cần click. Nên bật *tọa độ tương đối theo cửa sổ* để click đúng kể cả khi cửa sổ bị di chuyển. *Xem vị trí* đưa con trỏ tới điểm đã lưu để kiểm tra.

## Lịch chạy & kích hoạt

- **Hằng tháng**: ngày N (tháng ít ngày hơn → ngày cuối tháng), *Thứ X của tuần thứ N / tuần cuối*, ngày cuối tháng, **ngày làm việc đầu / cuối tháng** (bỏ T7, CN và ngày lễ).
- **Lặp theo phút + khung giờ**: vd mỗi 30 phút, chỉ 8:00–17:30 các ngày T2–T6.
- **Ngày nghỉ lễ** (⚙ Cài đặt → Ngày nghỉ): `dd/MM` lặp hằng năm (01/01, 30/04, 01/05, 02/09 có sẵn) hoặc `dd/MM/yyyy` cho ngày cụ thể (Tết âm lịch, Giỗ Tổ, nghỉ bù). Tick *Bỏ qua ngày nghỉ lễ* ở công việc.
- **Khi lỡ lịch** (máy tắt / ngủ / ScheduleApp không chạy): bỏ qua, **chạy bù một lần**, hoặc **hỏi tôi**. Phát hiện cả các lần lỡ trong lúc ScheduleApp tắt (tối đa 7 ngày).
- **Đánh thức máy** khi đang Sleep 1 phút trước giờ chạy (cần bật *Allow wake timers* trong Power Options; không áp dụng khi máy tắt hẳn). Khi flow chạy, máy không tự ngủ / tắt màn hình (tắt được trong Cài đặt).
- **Kích hoạt khác** (tab *Kích hoạt khác*): phím tắt toàn hệ thống · có file mới trong thư mục (mỗi file chạy một lần, chờ file ghi xong; nhận cả file trình duyệt vừa tải xong) · ứng dụng vừa mở / vừa đóng · máy rảnh N phút · mở khóa màn hình · khi ScheduleApp khởi động · **có email mới**.
- **Có email mới**: kiểm tra mỗi N phút các thư **chưa đọc** khớp *tiêu đề chứa* / *người gửi chứa* (không phân biệt hoa thường, dấu) → chạy công việc một lần cho mỗi thư rồi đánh dấu đã đọc. Hộp thư (⚙ Cài đặt → Tích hợp): **Outlook trên máy** (bản classic, không cần mật khẩu, chọn được thư mục con) hoặc **IMAP** (Gmail, Outlook.com… dùng App password). File đính kèm lưu trong `email\` của thư mục dữ liệu — `{{email.attachments}}` là danh sách đường dẫn.

## Bảo mật

- **🔑 Bí mật**: lưu mật khẩu / token mã hóa bằng Windows DPAPI (chỉ tài khoản Windows của bạn trên máy này giải mã được), dùng trong flow bằng `{{secret:Tên}}`. Giá trị không hiển thị lại, được che `***` trong nhật ký, không nằm trong `jobs.json` và không bị xuất ra file khi chia sẻ công việc.
- Mật khẩu email / IMAP, token Telegram, secret của kết nối API, khóa API Claude và token GitHub trong Cài đặt cũng được mã hóa DPAPI.
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
| `versions\<id>\` | Phiên bản cũ của từng công việc (30 bản gần nhất) |
| `logs\yyyy-MM-dd.log` · `logs\screenshots\` | Nhật ký theo ngày · ảnh chụp màn hình lỗi |
| `email\` | File đính kèm của email đã xử lý |
| `browser-chrome\` · `browser-edge\` | Hồ sơ trình duyệt dùng cho bước *Trình duyệt* |

## Cập nhật phiên bản

**⚙ Cài đặt → Chung → Nguồn bản mới** (ScheduleApp tự kiểm tra khi khởi động, báo ở khay hệ thống; hoặc **Thêm → Kiểm tra cập nhật…**):

| Nguồn | Cách phát hành bản mới |
|---|---|
| `github:chủ/repo` | Đẩy tag `v2.1.0` → GitHub Actions ([.github/workflows/release.yml](.github/workflows/release.yml)) tự kiểm thử, build `ScheduleApp.exe`, bộ cài và `version.json` lên **Releases**. Repo riêng tư: nhập token GitHub (fine-grained, quyền *Contents: Read*) |
| `\\máy-chủ\ScheduleApp` (thư mục dùng chung) | `.\build.ps1 -Notes "Có gì mới" -Share \\máy-chủ\ScheduleApp` — chép exe + `version.json` lên thư mục, cả phòng được báo bản mới |
| `https://…/version.json` | Đặt `version.json` (`{"version":"2.1.0","url":"ScheduleApp.exe","notes":"…","sha256":"…"}`) và exe lên web nội bộ |

Bấm **Cập nhật ngay**: tải bản mới (kiểm tra SHA-256), đóng ScheduleApp, thay file rồi tự mở lại — dữ liệu và lịch giữ nguyên, bản cũ giữ tạm thành `ScheduleApp.exe.old`. Cần chạy từ thư mục ghi được (bộ cài mặc định đã như vậy).

## Phát triển & kiểm thử

```powershell
dotnet test ScheduleApp.slnx                      # ~80 bài kiểm thử (xUnit)
$env:SCHEDULEAPP_LIVE_TESTS = 1; dotnet test ScheduleApp.slnx   # thêm UI Automation, Edge headless, Excel thật
```

Kiểm thử chạy với thư mục dữ liệu tạm, không đụng tới dữ liệu thật; gồm biến, lịch, cấu trúc flow, engine (điều kiện, lặp, thử lại, nhãn, flow con), đọc/ghi Excel/CSV, JSON, gọi API (máy chủ HTTP giả lập: OAuth, Basic, lỗi OData), cập nhật (manifest, SHA-256, script thay file), phiên bản, mẫu nhúng. Các phần cần người thật bấm (ghi macro, chế độ an toàn, Telegram, email, đánh thức máy) có danh sách kiểm tra tay trong [docs/KIEM-THU-THU-CONG.md](docs/KIEM-THU-THU-CONG.md). CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) build + chạy kiểm thử mỗi lần push.

## Cấu trúc mã nguồn

```
Models/      Job, ActionStep, ScheduleConfig (tính lần chạy tới), JobTrigger, AppSettings,
             FlowStructure (ghép khối Nếu/Lặp, nhãn, kiểm tra lỗi cấu trúc)
Services/    Scheduler (tick mỗi giây, chạy bù, đánh thức máy), FlowRunner (hàng đợi, dừng, lịch sử, thông báo),
             StepExecutor (thực thi từng loại bước), TriggerManager (phím tắt, file mới, tiến trình, rảnh, mở khóa, email),
             CommandServer (dòng lệnh qua named pipe), NotificationService, RunHistory, SettingsStore,
             SecretStore + Protector (DPAPI), JobStore + JobVersions (JSON, phiên bản cũ), Log, StartupManager,
             ApiClient (REST, Entra ID/OAuth), AiClient (Claude), MailWatcher (Outlook COM / IMAP),
             TelegramBot (lệnh điều khiển), UpdateService (kiểm tra / tải / thay exe)
Services/Engine/  FlowEngine (bộ đếm chương trình: điều kiện, vòng lặp, nhãn, thử lại, xử lý lỗi, gỡ lỗi),
                  FlowContext (biến, tùy chọn chạy), VariableExpander ({{…}}), ConditionEvaluator, LoopFrame
Services/Data/    TabularReader / TabularWriter (đọc, ghi .xlsx / .csv không cần Excel), JsonPath
Automation/  UiElementFinder (UI Automation), BrowserClient (Chrome DevTools Protocol)
Native/      Win32 P/Invoke, InputSimulator (SendInput: click, phím, cuộn, kéo thả), WindowHelper,
             PowerHelper (giữ máy thức, hẹn giờ đánh thức, thời gian rảnh, DPI), UserInputGuard (chế độ an toàn)
Vision/      ScreenCapture, ImageMatcher (NCC + màu), ScreenOcr (Windows OCR), ScreenLocator (co giãn theo DPI)
Recording/   MacroRecorder (hook chuột/bàn phím toàn hệ thống)
UI/          MainForm, JobEditorForm, StepEditorForm, FlowDesigner, StepToolbox, StepVisuals, TriggerEditorForm,
             HistoryForm (+ thống kê), SettingsForm, ApiConnectionForm, SecretsForm, TemplatePickerForm, VersionPickerForm,
             UpdateForm, PromptForms (nhập liệu, xác nhận, thanh gỡ lỗi), ReminderForm, VisionForms, UiCommon
Samples/     Mẫu công việc (nhúng vào ứng dụng cho mục "Mẫu có sẵn…")
tests/       ScheduleApp.Tests (xUnit)
installer/   ScheduleApp.iss (Inno Setup) · build.ps1 (test + publish + bộ cài) · .github/workflows (CI, phát hành)
```
