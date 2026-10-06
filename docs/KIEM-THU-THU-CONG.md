# Kiểm tra thủ công

Các phần dưới đây cần chuột / bàn phím thật, tài khoản hoặc thiết bị nên không có trong `dotnet test`. Làm lần lượt, mỗi mục 1–2 phút. Đánh dấu `[x]` khi đạt; ghi lại hiện tượng nếu không đạt.

Nên chạy với thư mục dữ liệu riêng để không đụng công việc thật:

```powershell
$env:SCHEDULEAPP_DATA_DIR = "$env:TEMP\ScheduleAppManual"; .\publish\ScheduleApp.exe
```

## 1. Ghi macro

- [ ] **Ghi thao tác…** → mở Notepad, click menu *File*, gõ `Xin chào Việt Nam`, nhấn `Ctrl+S`, `Esc` → **Dừng & lưu**.
  - Click vào menu *File* thành bước **Click phần tử UI** (`Name=File…`), thông báo cuối có dòng "✔ N click được ghi theo phần tử UI".
  - Có bước *Chờ cửa sổ xuất hiện* khi chuyển cửa sổ; chữ gõ thành một bước *Gõ văn bản* đúng dấu (thử cả khi bật UniKey/EVKey).
- [ ] Bỏ tick "Ghi click thành Click phần tử UI" trên thanh đang ghi → click được ghi bằng tọa độ.
- [ ] Kéo thả một file trong Explorer → bước *Kéo thả chuột*; lăn chuột → *Cuộn chuột* (gộp nhiều nấc).
- [ ] Gõ vào ô mật khẩu (vd trang đăng nhập) → bước lưu `{{secret:MatKhau}}`, không lưu mật khẩu thật.
- [ ] Chạy thử flow vừa ghi (`F5`) → thao tác lặp lại đúng.

## 2. Soạn flow

- [ ] Xóa một khối *Lặp* → `Ctrl+Z` khôi phục cả khối; `Ctrl+Y` làm lại. Đang gõ trong ô tên công việc thì `Ctrl+Z` chỉ hoàn tác chữ trong ô.
- [ ] Sửa và **Lưu** một công việc 2 lần → mở lại → **Phiên bản cũ…** thấy 2 bản; khôi phục bản đầu → bấm *Hủy* thì không đổi, bấm *Lưu* thì đổi.
- [ ] Hướng dẫn: bấm **Hướng dẫn** ở thanh bên trái → bấm từng chủ đề, gõ "ghi kich ban" vào ô tìm → còn chủ đề ghi kịch bản; nút **＋ Thêm công việc** trong chủ đề *Bắt đầu nhanh* mở trình soạn. Mở form soạn bước *Dynamics 365* → `F1` → cửa sổ Hướng dẫn mở ở *Bước Dynamics 365*, cuộn / bấm được trong khi form soạn bước vẫn mở.

## 3. Ghi Excel trong vòng lặp

- [ ] Tạo `khach-hang.xlsx` (cột `Ma`, `Ten`, 3 dòng) → flow: *Lặp mỗi dòng Excel* → *Ghi Excel / CSV* sửa dòng `{{row.rowNumber}}` với `TrangThai=OK {{row.Ma}}` → chạy → mở bằng Excel: cột *TrangThai* đúng từng dòng, Excel **không** báo "file bị lỗi".
- [ ] Mở file trong Excel rồi chạy lại → bước báo lỗi "file đang được mở…", không làm hỏng file.

## 4. Chế độ an toàn

- [ ] ⚙ Cài đặt → bật *Chế độ an toàn* → chạy một flow dài (vd *Chờ 3 giây* ×5) → trong lúc chạy click chuột vào ứng dụng khác → flow dừng trước bước kế tiếp và hỏi *Chạy tiếp / Dừng*.

## 5. Kích hoạt

- [ ] Phím tắt `Ctrl+Alt+9` → công việc chạy khi bấm ở bất kỳ ứng dụng nào.
- [ ] File mới: theo dõi một thư mục `*.txt` → tạo 3 file liền nhau → công việc chạy 3 lần, `{{trigger.name}}` đúng từng file.
- [ ] Khóa máy (`Win+L`) rồi mở khóa → công việc "mở khóa màn hình" chạy sau ~2 giây.
- [ ] Email mới (⚙ Cài đặt → Tích hợp → *Thử hộp thư*): tự gửi cho mình thư tiêu đề "Thử ScheduleApp" có đính kèm → trong ≤ N phút công việc chạy; `{{email.subject}}`, `{{email.attachments}}` đúng; thư được đánh dấu đã đọc.

## 6. Telegram

- [ ] Cài đặt → Thông báo: nhập token + chat id → *Gửi thử* nhận được tin.
- [ ] Bật *Nhận lệnh điều khiển* → nhắn `/list`, `/run 1`, `/status`, `/history`, `/screenshot` → trả lời đúng; nhắn từ tài khoản Telegram khác → bị bỏ qua (nhật ký ghi "chat lạ").
- [ ] Gõ `/` → hiện menu lệnh tiếng Việt. `/list` → mỗi công việc có nút ▶, bấm → chạy, chạy xong bot báo ✅/❌ kèm nút 🔁 Chạy lại · 📷 Màn hình (khi lỗi) · 📜 Lịch sử (công việc tắt thông báo vẫn báo).
- [ ] Đang chạy flow dài → `/status` ghi bước N/M + nút ⏸ Tạm dừng → bấm → flow dừng trước bước kế, thanh gỡ lỗi hiện trên máy → `/status` có nút ▶ Chạy tiếp / ⏭ Một bước / ■ Dừng → bấm thử từng nút.
- [ ] `/new …` → bản nháp có nút ✔ Lưu · ▶ Lưu và chạy · ✖ Bỏ; nhắn sửa một lần rồi bấm ✔ Lưu ở tin xem trước **cũ** → bot báo "Bản nháp đã thay đổi", không lưu.
- [ ] Rút mạng vài giây lúc flow kết thúc → cắm lại → vẫn nhận được tin báo kết quả (tự gửi lại).

## 7. Gọi API / AI (cần tài khoản)

- [ ] Kết nối *Microsoft Entra ID* tới Dynamics 365 → *Thử kết nối* trả `200`; bước *Gọi API* `WhoAmI` → biến `UserId` có giá trị.
- [ ] Nhập khóa Claude → *Thử* trả "OK"; bước *Hỏi AI* kèm ảnh màn hình đọc đúng một con số đang hiển thị.

## 7b. Kiểm thử Dynamics 365 (cần môi trường D365 test)

- [ ] **Trước tiên chạy bộ tự động với D365 thật**: đặt `SCHEDULEAPP_D365_URL` (+ hồ sơ đã đăng nhập hoặc tài khoản test) rồi `dotnet test --filter FullyQualifiedName~D365RealTests` (xem README *Phát triển & kiểm thử*) → ĐẠT; đọc output: danh sách nút, subgrid, vai trò đọc được từ form thật.
- [ ] Nhập 7 mẫu "Kiểm thử Dynamics 365", sửa `d365Url` của C1 → chạy C1, đăng nhập tay lần đầu trong cửa sổ Edge → chạy lại C1 thì không phải đăng nhập.
- [ ] Chuột phải C2 → *Chạy kiểm thử & xem báo cáo* → báo cáo ĐẠT, có 4 kiểm tra; bản ghi "Test KH …" đã bị xóa khỏi D365 (dọn dữ liệu).
- [ ] Sửa Assert "Tên đã lưu đúng" của C2 thành giá trị sai → báo cáo KHÔNG ĐẠT, dòng kiểm tra có giá trị thực tế và ảnh chụp trang D365; bản ghi test vẫn được xóa.
- [ ] C3 (lưu thiếu tên) ĐẠT với form tiếng Việt và tiếng Anh (nút lưu tìm theo command id).
- [ ] Nhập lookup theo tên trùng nhau (2 bản ghi cùng tên) → bước lỗi gợi ý nhập Id.
- [ ] `Start-Process ScheduleApp.exe -ArgumentList '--test','"Mẫu kiểm thử Dynamics 365"' -Wait -PassThru` khi ScheduleApp đang mở ở khay → chạy được, `ExitCode` 0/1 đúng, có `junit.xml`; nạp `junit.xml` vào Azure DevOps *Publish Test Results* hiển thị đúng.
- [ ] C5 (subgrid) ĐẠT: liên hệ mới có sẵn công ty, subgrid *Contacts* đếm 1 dòng; nếu form account của bạn đặt tên subgrid khác, bấm *Chọn subgrid từ form…* để lấy đúng tên.
- [ ] C6 (phân quyền): đặt biến `vaiTro` bằng vai trò của tài khoản test → ĐẠT; đổi thành vai trò không có → KHÔNG ĐẠT ở bước kiểm tra vai trò và flow dừng (Khi bước lỗi = Dừng flow).
- [ ] C7 (view) ĐẠT; mở một view cá nhân bằng tên trong bước *Danh sách (view): đọc các bản ghi* → `{{view.count}}` đúng như số dòng thấy trên lưới với cùng ô tìm.
- [ ] Bước *Mở form bản ghi* với ô *Form chính* = tên một form khác của account → form đó mở ra; kiểm tra *D365: form đang mở là* ĐẠT.
- [ ] *Tạo nhanh* contact với `lastname=…` và `parentcustomerid=account:<Id>` → bản ghi được tạo, `{{d365.lastId}}` có Id, được dọn khi kết thúc.
- [ ] Kiểm tra *D365: nút trên thanh lệnh* với một nút nằm trong menu "…" (vd *Share* / *Chia sẻ*) → `visible` ĐẠT, menu tự đóng lại sau khi kiểm tra.
- [ ] Đăng nhập tự động: hồ sơ trình duyệt mới + bật bước *Đăng nhập Microsoft* của C1 với tài khoản test (mật khẩu, khóa TOTP trong Bí mật) → tự vào được app; sai mật khẩu → bước lỗi kèm câu báo của trang Microsoft. Đăng xuất D365 trong cửa sổ đó rồi chạy C2 khi tắt bước đăng nhập → bước D365 báo "Phiên đăng nhập Dynamics 365 đã hết".
- [ ] Môi trường: tạo *UAT* với `d365Url` khác → chọn trên trang Kiểm thử → chạy C2 → báo cáo ghi "môi trường UAT", mở đúng URL của UAT. `--env UAT` trên dòng lệnh cho kết quả giống.
- [ ] Thư mục kịch bản: *Xuất kịch bản ra thư mục…* vào một repo git → `git status` thấy file `Mẫu kiểm thử Dynamics 365\C2 · ….json`, `environments.json`; đổi tên C2 rồi xuất lại → file cũ bị xóa. Máy khác *Nhập / cập nhật từ thư mục…* → có đủ kịch bản; `ScheduleApp.exe --test * --test-dir <thư mục> --list` liệt kê đủ.
- [ ] Theo dữ liệu: C2 + file CSV 3 dòng (cột `Ten`), đổi tên khách thành `{{row.Ten}}` → báo cáo có 3 test case "C2 … [dòng N: …]".
- [ ] `--headless` trên máy CI (agent chạy dưới tài khoản Windows đã đăng nhập D365 trong hồ sơ) → không hiện cửa sổ, báo cáo vẫn có ảnh chụp lúc lỗi.

## 8. Lịch, ngủ máy, cập nhật

- [ ] Đặt lịch chạy sau 3 phút, bật *Đánh thức máy* → cho máy Sleep → máy tự thức và chạy (cần *Allow wake timers*).
- [ ] Tắt ScheduleApp qua giờ chạy của một công việc *Chạy bù một lần* → mở lại → chạy bù.
- [ ] Cập nhật: `.\build.ps1 -Share C:\Temp\ScheduleAppShare` với phiên bản cao hơn (sửa `<Version>`), cài bản cũ bằng bộ cài, đặt *Nguồn bản mới* = `C:\Temp\ScheduleAppShare` → *Kiểm tra ngay* → *Cập nhật ngay* → ScheduleApp đóng, mở lại với bản mới, công việc còn nguyên.
- [ ] Bộ cài: cài, chọn *Khởi động cùng Windows* → đăng nhập lại thấy biểu tượng ở khay; gỡ cài đặt → dữ liệu trong `%AppData%\ScheduleApp` vẫn còn.
