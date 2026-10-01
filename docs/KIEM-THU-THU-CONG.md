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

## 7. Gọi API / AI (cần tài khoản)

- [ ] Kết nối *Microsoft Entra ID* tới Dynamics 365 → *Thử kết nối* trả `200`; bước *Gọi API* `WhoAmI` → biến `UserId` có giá trị.
- [ ] Nhập khóa Claude → *Thử* trả "OK"; bước *Hỏi AI* kèm ảnh màn hình đọc đúng một con số đang hiển thị.

## 8. Lịch, ngủ máy, cập nhật

- [ ] Đặt lịch chạy sau 3 phút, bật *Đánh thức máy* → cho máy Sleep → máy tự thức và chạy (cần *Allow wake timers*).
- [ ] Tắt ScheduleApp qua giờ chạy của một công việc *Chạy bù một lần* → mở lại → chạy bù.
- [ ] Cập nhật: `.\build.ps1 -Share C:\Temp\ScheduleAppShare` với phiên bản cao hơn (sửa `<Version>`), cài bản cũ bằng bộ cài, đặt *Nguồn bản mới* = `C:\Temp\ScheduleAppShare` → *Kiểm tra ngay* → *Cập nhật ngay* → ScheduleApp đóng, mở lại với bản mới, công việc còn nguyên.
- [ ] Bộ cài: cài, chọn *Khởi động cùng Windows* → đăng nhập lại thấy biểu tượng ở khay; gỡ cài đặt → dữ liệu trong `%AppData%\ScheduleApp` vẫn còn.
