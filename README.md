# ScheduleApp — Đặt lịch, nhắc nhở & tự động thao tác

Ứng dụng desktop Windows (C# WinForms, .NET 10) để:

- **Đặt lịch chạy** công việc: một lần, hằng ngày, hằng tuần, **hằng tháng** (ngày N, thứ X tuần thứ N, ngày làm việc đầu/cuối tháng), lặp lại mỗi N phút (**có thể giới hạn trong giờ hành chính**), hoặc chỉ chạy thủ công. Bỏ qua **ngày nghỉ lễ**, **chạy bù** lịch bị lỡ, **đánh thức máy** khi đang ngủ.
- **Kích hoạt theo sự kiện**: phím tắt, có file mới trong thư mục, ứng dụng mở/đóng, máy rảnh N phút, mở khóa màn hình, khi ScheduleApp khởi động, hoặc từ **dòng lệnh**.
- **Nhắc nhở**: hiện cửa sổ nhắc trước giờ chạy N phút, hoặc dùng bước "Hiện nhắc nhở" ngay trong flow.
- **Tự động thao tác theo luồng (flow)**: mở ứng dụng, chờ cửa sổ, click/gõ/nhấn phím, cuộn & kéo thả chuột, nhận dạng hình ảnh/chữ (OCR), **phần tử UI (UI Automation)**, **điều khiển Chrome/Edge**, chạy lệnh…
- **Lập trình flow không cần code**: **biến** `{{...}}`, **Nếu / Không thì**, **vòng lặp** (N lần, khi điều kiện đúng, **mỗi dòng Excel/CSV**, mỗi dòng văn bản, mỗi file), nhãn & nhảy, gọi công việc khác.
- **Dữ liệu & tích hợp**: **ghi kết quả vào Excel / CSV** (từng dòng đang xử lý), **gọi REST API** (kết nối sẵn cho **Dynamics 365 Web API** qua Microsoft Entra ID), **hỏi AI (Claude)** để trích dữ liệu từ chữ hoặc ảnh màn hình, **kích hoạt khi có email mới** (Outlook / IMAP, lưu file đính kèm), biến **danh sách** và **JSON**.
- **Kiểm thử tự động Dynamics 365 / Power Apps model-driven**: bước **Dynamics 365** (mở form / view, nhập field mọi kiểu kể cả lookup, lưu, ribbon, BPF, hộp thoại, Web API bằng phiên đăng nhập trình duyệt), bước **Kiểm tra (Assert)**, **báo cáo HTML + JUnit XML** kèm ảnh lúc lỗi, **tự dọn dữ liệu test**, chạy **bộ kiểm thử từ dòng lệnh** cho CI.
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
| `ScheduleApp.exe --test "Nhóm" [--report "D:\BaoCao"]` | Chạy bộ kiểm thử không mở giao diện (tên nhóm, tên công việc hoặc `*`), xuất báo cáo; mã thoát `0` = đạt, `1` = không đạt, `2` = không tìm thấy / tham số sai — xem [Kiểm thử tự động Dynamics 365](#kiểm-thử-tự-động-dynamics-365-model-driven-app) |
| `… --tag smoke --env UAT --test-dir "tests\d365" --retry 1 --shard 1/3 --headless --list` | Thêm cho `--test`: lọc theo tag, chạy ở môi trường, đọc kịch bản từ thư mục trong repo, chạy lại kịch bản không đạt, chia bộ cho nhiều máy, mở trình duyệt ẩn, chỉ liệt kê kịch bản được chọn |

Chuột phải một công việc → **Tạo shortcut trên Desktop** để chạy bằng một cú nhấp đúp (gán được phím tắt trong Properties của shortcut, hoặc dùng cho Stream Deck). Biến môi trường `SCHEDULEAPP_DATA_DIR` đổi thư mục dữ liệu (bản portable).

## Cách dùng

Màn hình chính có thanh điều hướng bên trái: **Công việc** (danh sách công việc theo nhóm, nhật ký hoạt động thu gọn được ở dưới — bấm *📜 Nhật ký* trên thanh trạng thái để ẩn/hiện), **Kiểm thử** (kịch bản kiểm thử và báo cáo), cùng **Lịch sử chạy**, **Bí mật**, **Cài đặt**, **Hướng dẫn**.

**Hướng dẫn trong ứng dụng:** trang **Hướng dẫn** gồm các chủ đề từ bắt đầu nhanh, lịch chạy, dựng flow, biến tới toàn bộ quy trình kiểm thử Dynamics 365 (chuẩn bị trình duyệt, ghi kịch bản, chọn field, bước D365, Kiểm tra, dữ liệu test, báo cáo, CLI/CI, xử lý sự cố). Trang có ô tìm không dấu và nút *làm ngay* (vd **⏺ Ghi kịch bản D365**). Nhấn `F1` ở bất kỳ cửa sổ nào để mở đúng chủ đề đang làm: trong form soạn bước D365 mở *Bước Dynamics 365*, trong cửa sổ ghi mở *Ghi kịch bản bằng thao tác*… Từ hộp thoại, hướng dẫn mở trong cửa sổ riêng không chặn hộp thoại, để vừa đọc vừa làm.

1. **＋ Thêm công việc** (hoặc **Mẫu có sẵn…**) → đặt tên, nhóm, kiểu lịch / trình kích hoạt.
2. Dựng flow bằng **kéo thả** (xem bên dưới).
3. Bấm **▶ Chạy thử flow** (`F5`) để kiểm tra ngay, rồi **Lưu**.
4. Tick/bỏ tick ở danh sách chính để bật/tắt. Ô 🔍 lọc theo tên/nhóm; công việc được gom theo **nhóm**.

Bấm nút X chỉ thu ứng dụng xuống **khay hệ thống** — lịch vẫn chạy (chuột phải biểu tượng khay → *Chạy công việc* để chạy nhanh). Thoát hẳn: chuột phải biểu tượng khay → *Thoát*.
Tick **Khởi động cùng Windows** để app tự chạy (ẩn ở khay) khi đăng nhập.

**Dừng khẩn cấp:** `Ctrl+Shift+Q` (phím tắt toàn hệ thống) — dừng flow đang chạy và hủy các flow đang chờ.

**Khung trạng thái khi flow chạy:** góc phải dưới màn hình hiện công việc đang chạy, **bước mấy / tổng số bước**, mô tả bước, thời gian đã chạy (cả flow và bước hiện tại) và dòng nhật ký mới nhất. Nút **⏸ Tạm dừng** dừng flow trước bước kế tiếp (kể cả trong công việc con) → **⏭ Bước tiếp** chạy từng bước một, **▶ Chạy tiếp** chạy bình thường; **■ Dừng** dừng ngay. Khung không lấy focus (phím giả lập vẫn vào đúng ứng dụng), không lọt vào ảnh chụp màn hình (tìm theo hình ảnh, ảnh lỗi) và tự dời sang góc khác khi flow sắp click vào chỗ nó đang che; kéo để đổi chỗ. Tắt ở *Cài đặt → Chung*.

## Ví dụ mẫu

Nút **Mẫu có sẵn…** mở kho mẫu nhúng trong ứng dụng; ba file trong [Samples/](Samples/) cũng nhập được bằng **Thêm → Nhập công việc…**. Mẫu có lịch hoặc trình kích hoạt được tắt sẵn — xem lại rồi tick để bật.

**Thiết lập mẫu:** chọn mẫu xong, màn hình *Thiết lập mẫu* hỏi một lần những gì là của riêng bạn — gom từ mẫu được chọn **và các công việc dùng chung nó gọi tới** (vd C2 → C1 *Mở Dynamics 365*):

- **Biến** như `tenant`, `d365Url`, `taiKhoanTest`, `vaiTro` (kèm giải thích). Biến dùng ở nhiều công việc chỉ cần điền một lần; giá trị còn là chữ mẫu (`ten-cong-ty`, `tenorg`, appid toàn số 0) được đánh dấu ⚠. Giá trị đã điền được nhớ và **tự điền sẵn** khi thêm mẫu khác dùng cùng biến. Biến do flow tự gán (bộ đếm, danh sách) không bị hỏi.
- **Bí mật** mà mẫu dùng (`{{secret:MatKhauTest}}`…): nhập luôn tại đây, lưu mã hóa trên máy; đã có thì để trống để giữ nguyên.
- **Kết nối API** mẫu cần (vd `Dynamics365`) — báo nếu chưa khai báo, có nút mở Cài đặt.
- **Đường dẫn file / thư mục** ghi trong bước và trình kích hoạt (vd `%USERPROFILE%\Documents\khach-hang.xlsx`, thư mục Downloads được theo dõi) — có nút *Duyệt…*, phần động như `\{{today:yyyy-MM}}` được giữ nguyên.

**Dữ liệu test ngẫu nhiên:** giá trị biến viết dạng công thức `=…` (như Excel) được sinh mới ở **mỗi lần chạy** — điền ngay trong Thiết lập mẫu, tab *Biến* của công việc hoặc *Môi trường*, có nút **⚄ Ngẫu nhiên** chọn nhanh và xem trước ví dụ:

| Công thức | Ví dụ giá trị |
|---|---|
| `=hoten()` · `=hoten(nữ)` · `=ho()` · `=ten()` | Nguyễn Thị Lan · Trần · Minh |
| `=email()` · `=email(cty.vn)` | lan.nguyen482@example.com |
| `=sdt()` · `=cccd()` | 0912345678 (đầu số di động thật) · 079203004512 |
| `=diachi()` · `=thanhpho()` · `=congty()` | 125 Lê Lợi, Quận 1, TP. Hồ Chí Minh · Công ty TNHH Minh Long Thương mại |
| `=random(1, 100)` · `=random(1, 100, 2)` · `=chuso(6)` · `=chuoi(8)` · `=guid()` | 42 · 57.31 · 040213 · K7Q2MZ9A |
| `=chon(Mới; Đang xử lý; Đã đóng)` · `=ngay(-30, 0)` · `=ngay(1, 90, yyyy-MM-dd)` | Đang xử lý · 14/09/2026 · 2026-11-20 |

Giá trị sinh ra được ghi vào nhật ký (`🎲 {{hoTen}} = "…"`) để tái hiện lần chạy lỗi. Dùng thẳng trong ô chữ bằng `{{=sdt()}}` — mỗi lần thay ra một giá trị khác; cần cùng một giá trị cho bước nhập và bước *Kiểm tra* thì đặt vào biến. Tên hàm gõ có dấu cũng được (`=họtên()`, `=chọn(…)`).

Bấm *Để sau* để giữ nguyên mẫu. Mở lại bất cứ lúc nào: chuột phải công việc → **⚙ Thiết lập biến & bí mật…**, hoặc **⋯ Thêm → Thiết lập biến & bí mật (mọi công việc)…** để đổi một giá trị (vd `tenant`) cho tất cả công việc cùng lúc.

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

**Kiểm thử Dynamics 365** — [Samples/ScheduleApp-mau-kiem-thu-d365.json](Samples/ScheduleApp-mau-kiem-thu-d365.json) (đổi biến `d365Url` của C1 thành URL app của bạn, thêm cả 7 mẫu cùng lúc):

| # | Ví dụ | Minh họa |
|---|---|---|
| C1 | Mở Dynamics 365 (dùng chung) | Hồ sơ Edge riêng giữ đăng nhập, bước *Đăng nhập Microsoft* (tắt sẵn — bật khi dùng tài khoản test + MFA TOTP), *Mở danh sách* kèm thử lại tới khi app tải xong |
| C2 | Tạo khách hàng và kiểm tra dữ liệu đã lưu | *Kiểm tra* field bắt buộc, *Nhập field*, *Lưu* → `{{accountId}}`, kiểm tra form không báo lỗi, đếm bản ghi qua Web API, tự dọn dữ liệu |
| C3 | Lưu khi thiếu tên phải bị chặn | Kịch bản âm: bấm nút ribbon theo command id, kiểm tra form hiện thông báo, bản ghi không được tạo |
| C4 | Chuẩn bị dữ liệu qua Web API, sửa trên form | *Web API POST* tạo dữ liệu test, mở đúng bản ghi, xóa giá trị field, kiểm tra lại bằng `$filter … eq null` |
| C5 | Tạo liên hệ từ subgrid của khách hàng | *Subgrid: + Mới* (field cha điền sẵn theo quan hệ), đếm dòng / tìm dòng của subgrid, tag `subgrid` |
| C6 | Phân quyền: vai trò, nút trên thanh lệnh | *Đọc người dùng & vai trò*, kiểm tra vai trò, nút **bấm được** / **bị ẩn** theo command id |
| C7 | Khách hàng mới hiện trong view mặc định | *Danh sách (view): đọc bản ghi* theo FetchXML của view + tìm theo tên, *tìm và mở bản ghi* |

## Tạo flow bằng AI

Bấm **✨ Tạo bằng AI…** (thanh công cụ chính, hoặc nút bên phải trình soạn flow), mô tả việc cần làm bằng lời — Claude dựng sẵn flow để xem trước rồi áp dụng:

> *Đọc file khach-hang.xlsx trong Documents. Với mỗi dòng chưa có Trạng thái: mở Edge vào trang đăng ký, điền Họ tên, Email, bấm Đăng ký, ghi "Đã nhập" vào cột Trạng thái. Xong thì gửi thông báo số dòng đã nhập.*

- **Tạo mới / viết lại cả flow**, hoặc **chỉ thêm bước** vào sau bước đang chọn (vd *"sau khi lưu, gửi thông báo kèm ảnh màn hình"*). Flow đang có được gửi kèm nên có thể yêu cầu sửa: *"dùng Edge thay Chrome"*, *"bỏ bước đăng nhập, gọi công việc A1 có sẵn"*.
- AI biết các công việc khác (để gọi lại, vd đăng nhập), biến, kết nối API đã khai báo; tùy chọn gửi kèm danh sách cửa sổ đang mở.
- Nói luôn lúc nào chạy (*"8h sáng các ngày làm việc"*, *"mỗi 15 phút trong giờ hành chính"*, *"ngày cuối tháng"*, *"khi có email hóa đơn"*, *"phím Ctrl+Alt+7"*) → lịch chạy / kích hoạt được đặt cùng flow khi áp dụng (chế độ tạo mới / viết lại).
- Tạo từ điện thoại: lệnh `/new` qua Telegram (xem *Điều khiển từ xa qua Telegram*).
- ScheduleApp kiểm tra flow AI trả về (khối Nếu/Lặp, tổ hợp phím, bộ chọn phần tử, công việc / kết nối được tham chiếu…) và **tự gửi lỗi lại cho AI sửa** (tối đa 2 lần) trước khi hiện kết quả.
- Phần **Cần kiểm tra** liệt kê những gì phải xác nhận trước khi chạy (bộ chọn CSS, đường dẫn, bí mật cần thêm). Mật khẩu luôn là `{{secret:…}}`; các bước có hình mẫu được giữ nguyên khi AI sửa flow.
- Áp dụng xong vẫn hoàn tác được bằng **Ctrl+Z**. Nên **Chạy thử** trước khi đặt lịch.

Cần khóa API Claude (**⚙ Cài đặt → Tích hợp**). Mô tả, các bước hiện có, tên biến / công việc / kết nối API được gửi tới Anthropic; giá trị trong 🔑 Bí mật không được gửi.

## Thiết kế flow bằng kéo thả

Trình soạn công việc có 3 vùng: **Hộp công cụ** (trái) · **Khung luồng** Bắt đầu → các bước → Kết thúc (giữa) · nút lệnh (phải). Phía trên là các tab **Lịch chạy · Kích hoạt khác · Biến · Lỗi · thông báo · kiểm thử**. Trong form soạn bước, các tùy chọn ít dùng (thử lại khi lỗi, xử lý lỗi, nghỉ sau bước, bật/tắt, điểm dừng) nằm trong mục **▸ Nâng cao** — tự mở khi bước đã có giá trị khác mặc định.

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

Định dạng giá trị biến: `{{ten:upper}}` `{{ten:lower}}` `{{ten:trim}}` `{{duongDan:unquote}}` (bỏ dấu nháy bao quanh, vd đường dẫn chép bằng *Copy as path*) `{{ten:len}}` `{{ten:nodiacritics}}` (bỏ dấu) `{{ten:url}}` `{{ten:json}}` (đặt vào chuỗi JSON an toàn), số `{{tien:N0}}` (1.234.568) `{{so:000}}`, ngày `{{ngay:dd/MM/yyyy}}`.

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

## Ghi file (Excel / CSV / văn bản)

Bước **Ghi file** ghi thẳng vào `.xlsx` / `.csv` không cần mở Excel — giữ nguyên định dạng ô, công thức khác, các sheet khác và bảng (*Format as Table* được nới xuống khi thêm dòng). Mỗi dòng của ô nội dung là một ô cần ghi: `TrangThai=Đã nhập`, `MaDon={{maDon}}`, `NgayNhap={{now:dd/MM/yyyy HH:mm}}`; cột chưa có được thêm vào sau cột cuối.

- **Thêm dòng mới vào cuối** — nhật ký, sổ hóa đơn… File chưa có sẽ tự tạo (kèm dòng tiêu đề).
- **Sửa ô của một dòng có sẵn** — theo **số dòng Excel** (trong vòng lặp *Mỗi dòng Excel/CSV* dùng `{{row.rowNumber}}` để ghi kết quả vào đúng dòng đang xử lý) hoặc theo **cột khóa** `MaKH={{row.MaKH}}`.
- Số "thuần" được ghi dạng số (Excel tính toán được); mã có số 0 ở đầu (`007`) giữ dạng chữ. Số dòng vừa ghi có trong `{{lastRow}}`.
- Excel **khóa file khi đang mở** — đóng file trước khi chạy (bước báo lỗi rõ ràng; bật *Thử lại* nếu cần).

Mẫu thường dùng: lặp mỗi dòng Excel → nếu `{{row.TrangThai}}` bắt đầu bằng "Đã nhập" thì *Bỏ qua, sang lần lặp kế* → nhập liệu → ghi `TrangThai=Đã nhập` (hoặc `Lỗi: {{lastError}}` qua *khi lỗi → nhảy nhãn*). Chạy lại sau khi lỗi giữa chừng sẽ tiếp tục đúng chỗ.

**Ghi file văn bản** (cách ghi *Ghi file văn bản* / *Thêm vào cuối file văn bản*): ghi nội dung tự do nhiều dòng, có `{{biến}}`, vào `.txt` (UTF-8, tự tạo thư mục) — báo cáo, nhật ký, thông báo "đã chạy xong". Mở để xem bằng bước *Mở ứng dụng* `notepad.exe` với tham số là đường dẫn file.

## Phát video / nhạc

Bước **Phát video / nhạc** phát bằng trình phát có sẵn trong ScheduleApp (dùng bộ giải mã của Windows, không cần cài thêm): mỗi dòng một file, phát lần lượt từ trên xuống — hết file này tự sang file kế, **phát xong cả danh sách mới chạy bước sau**. Bấm **＋ Thêm file…** để chọn nhiều file, hoặc dán đường dẫn (*Copy as path* của Explorer, có dấu nháy cũng được); dòng là thư mục → phát mọi video trong đó theo tên (2 trước 10); dòng bắt đầu bằng `#` là ghi chú.

- Mặc định **toàn màn hình**; khi đang phát: `Esc` dừng (bước báo lỗi, flow dừng), `→` sang file kế, `Space` tạm dừng / phát tiếp. Âm lượng 0–100.
- File không có / không phát được → bỏ qua, ghi vào nhật ký. Số file đã phát: `{{media.played}}`.
- **Thời lượng tự tính** mỗi khi thêm / đổi file (đọc metadata của Windows, file không có thì mở thử bằng trình phát — không phát): ngay dưới ô danh sách hiện thời lượng từng file và **tổng** (file thiếu, dòng dùng biến chỉ biết khi chạy được ghi rõ); thẻ bước hiện `Phát "Clip_1.mp4" (0:12)`; cạnh nút *Chạy thử* hiện tổng của cả công việc (`4 bước · video/nhạc 0:26`). Mở công việc là tự tính lại (file có thể đã đổi).
- Khi chạy, nhật ký ghi tổng thời lượng và giờ **dự kiến xong**; màn hình phát hiện `1/2 · Clip_1.mp4 · 0:12`. Sau bước có `{{media.duration}}` (vd `0:26`) và `{{media.seconds}}` (vd `26`) — dùng được trong báo cáo, vd *Ghi file văn bản* "Đã phát {{media.played}} video, {{media.duration}}".

Ví dụ: *Phát Clip_1.mp4* → *Phát Clip_2.mp4* → *Ghi file văn bản* `Documents\ScheduleApp\da-chay-xong.txt` ("ĐÃ CHẠY XONG… {{now}}") → *Mở* `notepad.exe "…\da-chay-xong.txt"`, lịch *Hằng ngày 15:30*.

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
| `/new <mô tả>` | Tạo công việc mới bằng AI (cần khóa Claude trong ⚙ Cài đặt → Tích hợp) — trả về bản nháp để xem trước |
| *(nhắn thường)* · `/sua <yêu cầu>` | Sửa bản nháp đang có, vd "dùng Edge thay Chrome", "chỉ chạy thứ 2 và thứ 6" |
| `/ok` · `/ok chay` | Lưu bản nháp thành công việc (nhóm *Telegram*) · lưu và chạy ngay |
| `/huy` | Bỏ bản nháp |

**Tạo công việc từ điện thoại:** `/new 8h sáng các ngày làm việc mở D:\bao-cao.xlsx, làm mới dữ liệu, lưu rồi báo cho tôi` → bot báo "⏳ Đang dựng…" (20–60 giây) rồi gửi bản nháp: tên, **lịch chạy** (AI đặt luôn khi bạn nói giờ chạy — hằng ngày, các thứ trong tuần, mỗi N phút trong khung giờ, ngày cuối tháng, một lần lúc…), kích hoạt (phím tắt, khi có email / file mới…), danh sách bước, biến và những việc cần kiểm tra. Nhắn thêm để sửa — AI nhớ cả hội thoại — rồi `/ok`. Không nói giờ chạy → công việc chạy thủ công bằng `/run`. Công việc tên trùng được thêm số để `/run` theo tên không nhầm; mở trên máy để xem / sửa chi tiết như công việc thường. Các bản nháp do AI dựng nên xem kỹ trước khi `/ok chay`, nhất là bước click / gõ phím trên máy.

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

## Kiểm thử tự động Dynamics 365 (model-driven app)

Dựng kịch bản kiểm thử cho Dynamics 365 CE / Power Apps model-driven bằng kéo thả, không cần viết code. Bước **Dynamics 365** gọi **Client API (`Xrm`)** ngay trong tab trình duyệt điều khiển, thay vì click theo CSS selector. Vì vậy kịch bản không vỡ khi Microsoft đổi giao diện Unified Interface. Bước này dùng luôn phiên đăng nhập của trình duyệt (MFA, SSO), kể cả khi gọi Web API, nên không cần đăng ký ứng dụng Entra ID.

**Cách nhanh nhất — ghi thao tác:** vào trang **Kiểm thử** ở thanh bên trái → **⏺ Ghi kịch bản D365**. Nếu chưa có trình duyệt điều khiển, nhập URL app để mở. Sau đó thao tác bình thường trên form Dynamics 365: mở bản ghi, nhập field, Lưu, bấm nút ribbon, chuyển tab, BPF, hộp thoại. Mỗi thao tác hiện thành một bước trong cửa sổ ghi nổi bên cạnh. Bấm **✓ Thêm kiểm tra…** rồi tick các field cần kiểm tra để chụp giá trị hiện tại thành bước *Kiểm tra*. Bấm **■ Dừng & thêm vào flow** để chèn các bước vào kịch bản. Trong trình soạn công việc cũng có các nút **⏺ Ghi thao tác D365…** và **✓ Kiểm tra từ form D365…**.

**Không cần nhớ tên logic:** trong form soạn bước D365 / Kiểm tra, nút **Chọn field từ form…** (hoặc *Chọn tab / Chọn nút / Lấy từ form đang mở*) đọc form đang mở trong trình duyệt. Danh sách hiện nhãn tiếng Việt, tên logic, kiểu field, giá trị hiện tại và trạng thái (bắt buộc / khóa / ẩn), có ô tìm không dấu. Chọn một field thì các lựa chọn của option set cũng được gợi ý sẵn ở ô giá trị.

**Trang Kiểm thử:** thẻ số liệu (tổng kịch bản, đạt / không đạt / chưa chạy ở lần chạy cuối), danh sách kịch bản kèm kết quả và lỗi lần chạy gần nhất, và danh sách báo cáo gần đây. Từ đây chạy được kịch bản đã tick, chạy tất cả, hoặc **chạy lại các kịch bản lỗi**. Số kịch bản đang không đạt hiện thành huy hiệu đỏ trên thanh điều hướng.

**1. Chuẩn bị:** một công việc dùng chung (như mẫu C1) gồm bước *Trình duyệt → Mở trình duyệt ở chế độ điều khiển* (Edge, hồ sơ riêng, ví dụ "D365 Test") mở URL app `https://<org>.crm5.dynamics.com/main.aspx?appid=…`. Lần đầu bạn đăng nhập tay; các lần sau hồ sơ đã nhớ đăng nhập. Các kịch bản gọi công việc này bằng *Chạy công việc khác*.

**2. Bước Dynamics 365.** Ô *Tab* để trống thì dùng tab đầu tiên có `main.aspx` / `dynamics.com`.

| Hành động | Ghi chú |
|---|---|
| Mở form bản ghi | Bảng (logical name) + Id. Để trống Id thì mở form tạo mới. Bước tự chờ form mới tải xong |
| Mở danh sách (view) · Chờ form tải xong | Id view tùy chọn · chờ sau khi bấm nút chuyển trang |
| Nhập giá trị field | Chạy `fireOnChange` nên business rule và script của form chạy như khi người dùng nhập. Cách ghi theo kiểu field: **lookup** = tên bản ghi hoặc `bảng:guid` (vd `account:Contoso`, `contact:{{contactId}}`) · **option set** = nhãn (không phân biệt dấu) hoặc số · **nhiều lựa chọn** = `A; B` · **ngày** = `dd/MM/yyyy [HH:mm]` · **Có/Không** = `có`/`không`, `true`/`false` · **số** = `1.234.567,5` hoặc `1234567.5` · để trống = xóa giá trị. Field bị khóa hoặc ẩn sẽ báo lỗi như với người dùng thật (tick ô bỏ qua nếu cố ý) |
| Đọc giá trị field | Lookup trả về tên, option set trả về nhãn, ngày trả về `dd/MM/yyyy`. Thêm `:raw` để lấy giá trị gốc (Id, số, ISO), vd `parentcustomerid:raw` |
| Lưu bản ghi | Lỗi validate, field bắt buộc, lỗi plugin → bước lỗi kèm nội dung lỗi. Id lưu trong `{{d365.lastId}}`. Bản ghi mới được ghi vào `{{d365.created}}` |
| Bấm nút trên thanh lệnh | Theo nhãn (`Lưu & đóng`, `Deactivate`) hoặc một phần command id (`Mscrm.Form.account.Deactivate`). Nút nằm trong *Thêm lệnh (…)* được tự mở ra |
| Chuyển tab · BPF sang / về giai đoạn | Tab theo tên hoặc nhãn · thiếu field của giai đoạn, form chưa lưu → lỗi kèm lý do |
| Bấm nút trên hộp thoại | Chờ hộp thoại rồi bấm nút theo nhãn (để trống = nút chính). Nội dung hộp thoại lưu được vào biến |
| Lấy Id bản ghi · Đọc thông báo trên form | Thông báo gồm thanh thông báo, lỗi dưới field và hộp thoại lỗi |
| Gọi Web API | `GET/POST/PATCH/DELETE` + đường dẫn (`accounts?$select=name&$top=5`). Kết quả trong `{{http.body}}`, mã trả về trong `{{http.status}}`. POST trả Id mới và ghi bản ghi vào `{{d365.created}}` |
| Xóa dữ liệu test đã tạo | Xóa mọi bản ghi trong `{{d365.created}}`, bản tạo sau xóa trước |
| Chạy JavaScript | Có sẵn `formContext` và `Xrm`, dùng được `await`. Giá trị `return` lưu vào biến |
| Mở form bản ghi → ô *Form chính* | Tên hoặc Id form chính cần mở (bảng có nhiều form); trống = form mặc định của người dùng |
| Subgrid: mở bản ghi của một dòng · đọc giá trị một ô | Subgrid theo tên control (vd `Contacts`) hoặc nhãn; dòng = số thứ tự hoặc chữ có trong dòng (không phân biệt dấu); cột theo tên logic, trống = cột tên. Tự chờ subgrid tải xong |
| Subgrid: tạo bản ghi liên quan mới · làm mới | Như bấm **+ Mới** trên subgrid: mở form bảng liên quan với field của bản ghi cha điền sẵn theo ánh xạ quan hệ (`createFromEntity`) · tải lại subgrid |
| Danh sách (view): đọc các bản ghi · tìm và mở bản ghi | Chạy **FetchXML của chính view** (view hệ thống hoặc cá nhân, theo tên / Id, trống = view mặc định) qua Web API, tùy chọn lọc thêm theo cột tên. Kết quả `{{view.count}}`, `{{view.ids}}`, `{{view.names}}`; hoặc mở bản ghi đầu tiên |
| Tạo nhanh (quick create) | Mở form tạo nhanh với giá trị điền sẵn (mỗi dòng `field=giá trị`, lookup = `bảng:guid`), bấm *Lưu và đóng*, lấy Id từ kết quả của `Xrm.Navigation.openForm` vào `{{d365.lastId}}` / `{{d365.created}}` |
| Đăng nhập Microsoft | Tài khoản test: email → mật khẩu → mã MFA **TOTP** (tính theo RFC 6238 từ khóa bí mật Base32) → duy trì đăng nhập. Đã đăng nhập thì bỏ qua. Lưu mật khẩu / khóa trong 🔑 Bí mật. Không tự duyệt được Authenticator push |
| Đọc người dùng & vai trò | `{{d365.user}}`, `{{d365.userId}}`, `{{d365.roles}}` (mỗi dòng một vai trò) |

Khi trình duyệt bị chuyển về trang đăng nhập Microsoft (phiên hết hạn), bước D365 báo ngay *"Phiên đăng nhập Dynamics 365 đã hết"* thay vì chờ tới hết giờ.

**3. Bước Kiểm tra (Assert)** kiểm tra một điều kiện và ghi kết quả **ĐẠT / KHÔNG ĐẠT** vào báo cáo, kèm giá trị thực tế khi sai. Dùng được mọi điều kiện của *Nếu*, cộng thêm các điều kiện sau (cũng dùng được trong *Nếu* / *Lặp khi*):

| Điều kiện | Ví dụ |
|---|---|
| D365: giá trị field | `statuscode` bằng `Đang hoạt động` · `revenue` ≥ `1000000` · `primarycontactid` bằng `Nguyễn Văn A` |
| D365: trạng thái field | `telephone1` là `required` / `recommended` / `disabled` / `visible` / `dirty` / `empty`. Tick *Đảo ngược* để kiểm tra điều ngược lại |
| D365: form có thông báo / lỗi | Có chứa chữ `bắt buộc`. Để trống = có bất kỳ thông báo nào. Đảo ngược = form không báo lỗi |
| D365: số bản ghi (Web API) | `contacts?$filter=emailaddress1 eq '{{email}}'` ≥ `1`: dữ liệu thật đã được plugin / Power Automate tạo |
| D365: số dòng của subgrid · subgrid có dòng chứa chữ | `Contacts` ≥ `1` (tổng theo view của subgrid) · `Contacts` có dòng chứa `Nguyễn Văn A` |
| D365: nút trên thanh lệnh | `Mscrm.Form.account.Delete` là `visible` / `enabled` / `disabled` (tìm cả trong menu "…"). Đảo ngược = nút bị ẩn, vd người dùng không có quyền |
| D365: form đang mở là | `Bán hàng` (tên form chính đang mở) |
| D365: người dùng có vai trò | `Salesperson` — chắc chắn kịch bản đang chạy bằng đúng tài khoản / vai trò cần thử |
| Phần tử có trên trang web | CSS / `xpath:` / `text:` |

Ô *Chờ tối đa* lớn hơn 0 thì kiểm tra lại cho tới khi đạt (plugin bất đồng bộ, flow chạy chậm). Mặc định, Assert sai được ghi "không đạt" rồi flow **chạy tiếp**, để một lần chạy thấy hết mọi chỗ sai. Chọn *Khi bước lỗi → Dừng flow* nếu các bước sau phụ thuộc vào điều kiện này.

**4. Kịch bản & báo cáo.** Tick **Đây là kịch bản kiểm thử** (tab *Kiểm thử* của công việc). Từ đó mỗi lần chạy, kể cả chạy theo lịch, sẽ ghi lại từng bước (thời gian, kết quả, giá trị thực tế) và xuất `index.html` + `junit.xml` vào `test-reports\` (Lịch sử → *Mở báo cáo kiểm thử*). Bước lỗi có **ảnh chụp nội dung tab trình duyệt** qua DevTools, chụp được cả khi cửa sổ bị che hoặc chạy headless. Tick **Tự xóa dữ liệu Dynamics 365 do flow tạo ra** để luôn dọn `{{d365.created}}` sau khi chạy, kể cả khi test thất bại.

Chạy bộ kiểm thử từ giao diện: trang **Kiểm thử**, hoặc chuột phải công việc → **🧪 Chạy kiểm thử & xem báo cáo** / **🧪 Chạy nhóm "…" như bộ kiểm thử**. Các kịch bản chạy lần lượt và cuối cùng mở một báo cáo chung.

**5. Chạy trong CI / theo lịch đêm** (Azure DevOps self-hosted agent, Jenkins, Task Scheduler). Máy chạy cần đăng nhập Windows và đã đăng nhập D365 một lần trong hồ sơ trình duyệt:

```powershell
$p = Start-Process ScheduleApp.exe -ArgumentList '--test', '"Kiểm thử CRM"', '--report', 'D:\TestResults' -Wait -PassThru
exit $p.ExitCode        # 0 = mọi kịch bản đạt, 1 = có kịch bản không đạt, 2 = không tìm thấy kịch bản
```

ScheduleApp là ứng dụng cửa sổ, nên PowerShell / cmd không tự chờ nó chạy xong. Hãy dùng `Start-Process -Wait` hoặc `start /wait` như trên. Chế độ `--test` chạy độc lập (không cần mở giao diện, chạy được song song với ScheduleApp đang mở ở khay), in tiến trình ra console và ghi `junit.xml` để Azure DevOps (*Publish Test Results*, định dạng JUnit), Jenkins hoặc GitHub Actions hiển thị kết quả. Bước *Hỏi người dùng nhập* trong chế độ này dùng giá trị mặc định.

Tham số thêm: `--tag smoke` (chỉ kịch bản có tag), `--env UAT` (môi trường), `--test-dir "thư mục"` (đọc kịch bản từ thư mục trong repo, không cần nhập vào máy CI), `--retry 1` (chạy lại kịch bản không đạt; đạt ở lần chạy lại được ghi là chập chờn), `--headless` (mở trình duyệt ẩn, cửa sổ 1920×1080), `--shard 2/3` (máy thứ 2 trong 3 máy chạy song song — chia theo nhóm, tên, Id nên không trùng / sót), `--list` (chỉ liệt kê kịch bản được chọn).

**6. Môi trường, tag, chạy lại.** Trang **Kiểm thử** → *Môi trường: Quản lý…*: mỗi môi trường (Dev / Test / UAT) là bộ biến (vd `d365Url`, `taiKhoanTest`) **ghi đè** biến cùng tên của kịch bản khi chạy ở môi trường đó — chọn trên thanh lọc (áp dụng cả khi chạy theo lịch) hoặc `--env`. Tên môi trường có trong báo cáo, junit.xml và `{{env.name}}`. Tab *Kiểm thử* của công việc có **Tag** (lọc trên trang Kiểm thử, `--tag`) và **Mã test case** (vd Azure DevOps Test Plans, ghi vào junit.xml). Cột **Ổn định (10 lần)** cho thấy kịch bản chập chờn.

**7. Kịch bản trong git.** *🗂 Thư mục kịch bản (git) → Xuất kịch bản ra thư mục…*: mỗi công việc một file `Nhóm\Tên.json` (giữ Id, kèm công việc dùng chung được gọi tới và `environments.json`; không ghi kết quả lần chạy để diff gọn; đổi tên thì file cũ được xóa). Commit lên git để review; máy khác *Nhập / cập nhật từ thư mục…* (cùng Id thì thay). CI chạy thẳng từ repo với `--test-dir`. Bí mật không nằm trong file — thêm bí mật cùng tên trên máy CI.

**8. Kiểm thử theo dữ liệu.** Tab *Kiểm thử* → **Dữ liệu kiểm thử**: file Excel / CSV (dòng đầu là tiêu đề). Mỗi dòng chạy kịch bản một lần với `{{row.TênCột}}`, `{{data.index}}`, `{{data.count}}` và là **một test case riêng** trong báo cáo / junit.xml (vd *Tạo khách [dòng 3: KH002]*). Đường dẫn tương đối tính từ thư mục kịch bản khi chạy `--test-dir`. *Chạy thử flow* trong trình soạn dùng dòng đầu.

**9. Đăng nhập & phân quyền.** Bật bước *Đăng nhập Microsoft* của C1 (tài khoản test, mật khẩu `{{secret:MatKhauTest}}`, khóa TOTP `{{secret:TotpTest}}`) để máy CI tự đăng nhập lại khi phiên hết hạn. Kiểm thử theo vai trò: mỗi vai trò một hồ sơ trình duyệt + tài khoản test (đặt vào môi trường hoặc lặp theo dữ liệu), kiểm tra *D365: người dùng có vai trò*, field khóa (*trạng thái field* `disabled`) và nút bị ẩn (*nút trên thanh lệnh* + Đảo ngược) — xem C6.

**Giới hạn đã biết:** subgrid / view / quick create / vai trò dùng Client API và Web API được hỗ trợ chính thức; còn nhận diện **nút trên thanh lệnh, hộp thoại, thông báo, nút "Lưu và đóng" của form tạo nhanh và trang đăng nhập Microsoft** dựa vào cấu trúc trang (data-id, aria-label, Id của trang đăng nhập) — đã kiểm chứng trên trang giả lập dựng theo cấu trúc đó, cần chạy `D365RealTests` trên môi trường D365 test của bạn (xem *Phát triển & kiểm thử*). Chưa tự động hóa thao tác chuột trên lưới (sắp xếp, lọc cột), upload file, timeline, dashboard, PCF / canvas app nhúng. Mỗi máy chạy một trình duyệt điều khiển một lúc — chạy song song bằng nhiều máy với `--shard`.

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
- **Nhận diện bằng hình ảnh:** ngay lúc nhấn chuột (trước khi giao diện đổi), vùng quanh chỗ click được chụp và chọn làm **hình mẫu** — đúng khung nút/ô nếu UI Automation nhận ra, không thì vùng lớn dần quanh điểm click cho tới khi đủ chi tiết và không lẫn với chỗ khác (nút *Sửa* lặp ở mỗi dòng → lấy cả nhãn của dòng). Phần bị tooltip / cửa sổ khác che không đưa vào hình. Khi chạy:
  1. *Click phần tử UI*: tìm phần tử trước; không thấy → tìm theo hình mẫu → tọa độ lúc ghi.
  2. *Click chuột*: đưa chuột tới vị trí lúc ghi (để nút có hiệu ứng hover giống lúc ghi) rồi tìm hình mẫu trong cửa sổ, chờ tối đa 5 giây (cũng là chờ giao diện hiện ra); nhiều chỗ giống nhau → chọn chỗ gần vị trí lúc ghi nhất; không thấy → click theo tọa độ lúc ghi và ghi cảnh báo vào log.
  3. Hình mẫu tự co giãn khi màn hình đổi mức scale; độ khớp mặc định 80%.

  Thẻ bước hiện ảnh thu nhỏ của hình mẫu. Trong form soạn bước: xem hình (dấu chữ thập đỏ = điểm sẽ click), chụp lại, bỏ hình, chỉnh độ khớp / thời gian chờ, **Thử tìm trên màn hình**. Tắt bằng ô *Chụp hình chỗ được click* trên thanh "Đang ghi". Bấm vào vùng trống / ít chi tiết vẫn ghi theo tọa độ.
- Nhấn giữ rồi kéo → *Kéo thả chuột*; lăn bánh xe → *Cuộn chuột* (các lần cuộn liên tiếp được gộp).
- Chuyển sang cửa sổ khác → tự chèn *Chờ cửa sổ xuất hiện* trước thao tác đầu tiên trên cửa sổ đó.
- Chữ gõ liên tiếp → một bước *Gõ văn bản* (Backspace sửa chữ được tính luôn; hỗ trợ Unikey/EVKey). Gõ vào **ô mật khẩu** → lưu thành `{{secret:MatKhau}}` thay vì mật khẩu thật (thêm bí mật "MatKhau" trong 🔑 Bí mật).
- Enter, Tab, phím mũi tên, F1–F12, tổ hợp Ctrl/Alt/Win → *Nhấn phím* (phím lặp gộp thành `Tab*3`).
- Khoảng nghỉ thật giữa các thao tác được giữ lại (0,15–5 giây).

Hạn chế: với bộ gõ Telex có sẵn của Windows, chữ được ghi dạng phím gốc (vd "tieengs") — sửa lại trong bước sau khi ghi. Click không có hình mẫu (vùng trống) và kéo thả vẫn theo tọa độ nên xem lại. Nội dung quanh chỗ click thay đổi giữa các lần chạy (ô đã có chữ khác, danh sách đổi thứ tự) làm hình mẫu khó khớp → flow chậm 5 giây rồi click theo tọa độ; khi đó chụp lại hình mẫu vùng ổn định hơn hoặc bỏ hình.

## Các loại bước

| Nhóm | Bước | Mô tả |
|---|---|---|
| Ứng dụng | Mở ứng dụng / file / URL · Đóng ứng dụng · Chạy lệnh (cmd) · Phát video / nhạc | `notepad.exe`, `D:\bao-cao.xlsx`, `https://…` · theo tên tiến trình, tùy chọn kill · chạy ẩn, output vào log/biến, mã thoát ≠ 0 = lỗi · phát lần lượt danh sách, xong mới sang bước sau |
| Cửa sổ | Chờ cửa sổ xuất hiện · Kích hoạt cửa sổ | Theo tiêu đề / `exe:tiến_trình` |
| Chuột & bàn phím | Click chuột · Gõ văn bản · Nhấn phím · Cuộn chuột · Kéo thả chuột | Tọa độ màn hình hoặc tương đối theo cửa sổ; Unicode đầy đủ; `Ctrl+S`, `Tab*3`, `Ctrl+A, Delete` |
| Phần tử UI | Click / Nhập vào / Chờ phần tử UI | UI Automation — xem mục riêng |
| Trình duyệt | Trình duyệt (Chrome/Edge) | Mở, URL, click, nhập, đọc, chờ, JavaScript |
| Kiểm thử & Dynamics 365 | Dynamics 365 (model-driven) · Kiểm tra (Assert) | Xem [mục riêng](#kiểm-thử-tự-động-dynamics-365-model-driven-app) |
| Nhận dạng màn hình | Click / Chờ hình ảnh · Click / Chờ chữ (OCR) | Xem mục riêng |
| Biến & dữ liệu | Gán biến · Ghi file (Excel / CSV / văn bản) · Ghi nhật ký | Xem mục Biến, Ghi file |
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
| `test-reports\<ngày giờ>_<tên>\` | Báo cáo kiểm thử: `index.html`, `junit.xml`, `shots\` (ảnh lúc lỗi) |

Môi trường kiểm thử nằm trong `settings.json`; khi xuất thư mục kịch bản thì có thêm `environments.json` trong thư mục đó.

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
dotnet test ScheduleApp.slnx                      # ~130 bài kiểm thử (xUnit)
$env:SCHEDULEAPP_LIVE_TESTS = 1; dotnet test ScheduleApp.slnx   # thêm UI Automation, Edge headless, Excel thật, Dynamics 365 giả lập

# Dynamics 365 thật (môi trường TEST — bài kiểm thử tạo rồi xóa khách hàng / liên hệ "ScheduleApp RealTest…")
$env:SCHEDULEAPP_D365_URL = "https://<org>.crm5.dynamics.com/main.aspx?appid=…"
$env:SCHEDULEAPP_D365_PROFILE_DIR = "$env:APPDATA\ScheduleApp\browser-edge-D365 Test"   # hồ sơ "D365 Test" đã đăng nhập (đóng Edge của hồ sơ này trước), hoặc dùng 3 biến dưới
# $env:SCHEDULEAPP_D365_USER / SCHEDULEAPP_D365_PASSWORD / SCHEDULEAPP_D365_TOTP;  SCHEDULEAPP_D365_HEADLESS = 0 để xem trình duyệt
dotnet test ScheduleApp.slnx --filter FullyQualifiedName~D365RealTests
```

Tài liệu quá trình nâng cấp kiểm thử D365 (đánh giá, từng bước làm, cách kiểm chứng, giới hạn): [docs/QUA-TRINH-NANG-CAP-KIEM-THU-D365.md](docs/QUA-TRINH-NANG-CAP-KIEM-THU-D365.md).

Kiểm thử chạy với thư mục dữ liệu tạm, không đụng tới dữ liệu thật. Phạm vi gồm: biến, lịch, cấu trúc flow, engine (điều kiện, lặp, thử lại, nhãn, flow con), đọc/ghi Excel/CSV, JSON, gọi API (máy chủ HTTP giả lập: OAuth, Basic, lỗi OData), cập nhật (manifest, SHA-256, script thay file), phiên bản, mẫu nhúng. Phần kiểm thử Dynamics 365 có: Assert mềm/cứng, báo cáo HTML/JUnit, và bộ bước D365 chạy trên Edge headless với một máy chủ giả lập model-driven app (`Xrm`, lookup, business rule, BPF, ribbon, hộp thoại, Web API). Các phần cần người thật bấm (ghi macro, chế độ an toàn, Telegram, email, đánh thức máy) có danh sách kiểm tra tay trong [docs/KIEM-THU-THU-CONG.md](docs/KIEM-THU-THU-CONG.md). CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) build + chạy kiểm thử mỗi lần push.

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
Services/Testing/ TestRecorder (kết quả từng bước, ảnh lúc lỗi), TestReport (HTML + JUnit XML), TestSuite (chạy bộ kịch bản:
                  theo dữ liệu, chạy lại, tag, shard), TestFolder (kịch bản thành file cho git, môi trường), TestCli (--test cho CI)
Automation/  UiElementFinder (UI Automation), BrowserClient (Chrome DevTools Protocol, headless),
             D365Client (Dynamics 365 model-driven: Xrm Client API, subgrid, view/FetchXML, quick create, nút thanh lệnh,
             đăng nhập Microsoft, Web API bằng phiên trình duyệt, dọn dữ liệu test), Totp (mã MFA RFC 6238)
Native/      Win32 P/Invoke, InputSimulator (SendInput: click, phím, cuộn, kéo thả), WindowHelper,
             PowerHelper (giữ máy thức, hẹn giờ đánh thức, thời gian rảnh, DPI), UserInputGuard (chế độ an toàn)
Vision/      ScreenCapture, ImageMatcher (NCC + màu), ScreenOcr (Windows OCR), ScreenLocator (co giãn theo DPI)
Recording/   MacroRecorder (hook chuột/bàn phím toàn hệ thống)
UI/          MainForm (thanh điều hướng + trang Công việc), TestDashboard (trang Kiểm thử), Theme (màu, nút phẳng, renderer, NavButton, StatCard),
             D365Forms (chọn field / subgrid từ form D365, cửa sổ ghi thao tác D365), EnvironmentsForm (môi trường kiểm thử),
             HelpView + HelpContent (trang Hướng dẫn, F1), JobEditorForm, StepEditorForm, FlowDesigner, StepToolbox, StepVisuals, TriggerEditorForm,
             HistoryForm (+ thống kê), SettingsForm, ApiConnectionForm, SecretsForm, TemplatePickerForm, VersionPickerForm,
             UpdateForm, PromptForms (nhập liệu, xác nhận, thanh gỡ lỗi), ReminderForm, VisionForms, UiCommon
Samples/     Mẫu công việc (nhúng vào ứng dụng cho mục "Mẫu có sẵn…")
tests/       ScheduleApp.Tests (xUnit)
installer/   ScheduleApp.iss (Inno Setup) · build.ps1 (test + publish + bộ cài) · .github/workflows (CI, phát hành)
```
