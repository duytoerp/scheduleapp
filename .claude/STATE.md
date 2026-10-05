# STATE — Claude đang làm tới đâu

> Cập nhật mỗi khi bắt đầu / xong một việc. Trưởng nhóm (phiên chính) giữ các mục chung; mỗi agent chỉ sửa đúng dòng của mình trong bảng "Agent".
> Trạng thái: ⏳ đang làm · ✅ xong · ⛔ bị chặn · 💤 chờ người dùng

**Cập nhật lần cuối:** 2026-10-05 · phiên `scheduleapp-0d` (dừng nâng cấp, commit + push nhánh `nang-cap-ra-soat`)

## Đang làm
- ✅ **Kiểm tra bảo mật** (2026-10-05): 5 agent đọc code + phiên chính xác minh trong code. **7 nặng**: chèn lệnh qua "Chạy lệnh (cmd)" (mẫu A4/A6 dính), Telegram nhóm ai cũng điều khiển, Telegram tạo + chạy công việc không cần xác nhận, cập nhật không kiểm tra chữ ký, nhập file công việc là chạy ngay, mật khẩu kẹt trong clipboard, email giả người gửi. ~15 vừa, ~12 nhẹ. Không có thư viện NuGet lỗi. Danh sách đã gửi phiên `scheduleapp-0d` (đang sửa trên nhánh `nang-cap-ra-soat`); phiên này chỉ đọc.
- ✅ **Chọn màn hình phát video** (máy nhiều màn hình): bước "Phát video / nhạc" có ô *Màn hình phát* — màn hình chính / màn hình đang có chuột / màn hình số N; nút "Hiện số màn hình"; màn hình chưa cắm → phát ở màn hình chính + cảnh báo nhật ký. Test **263 đạt, 0 lỗi, 16 bỏ qua**.
- ⚠ Máy người dùng chỉ có 1 màn hình: chưa thử phát thật ở màn hình thứ 2 (đã test đặt cửa sổ đúng khung pixel, ngoài màn hình).
- 💤 Chờ người dùng: commit + push? tùy chọn "Esc = dừng bình thường" cho bước phát video?
- ⛔ **Nâng cấp theo rà soát — người dùng bảo dừng, commit + push** (2026-10-05): nhánh `nang-cap-ra-soat` đã push. Gồm: nhóm A (`badb3bd`, `d2fec14`, `95c0a5d`) + sửa review nhóm A (`e3c85c3`) + nhóm B: `b98e1e7` chèn lệnh cmd + clipboard · `962819d` nhật ký / giờ / DST / pipe · `7d661ae` trình duyệt + ghi macro · `9f7d619` Telegram + duyệt công việc từ xa / nhập file · `5adf95c` cập nhật an toàn · `baed237` bí mật lưu trên máy + D365 · `64fe6d1` che bí mật + mạng · `bcdd7e6` email + đính kèm + dữ liệu. Test sau gộp **497 đạt, 0 lỗi, 17 bỏ qua** (Release).
  - Đã review + sửa theo review: nhóm A, B1, B5a, B6. **Chưa sửa xong theo review** (phần dở chưa commit, còn nằm trong worktree, không đưa vào nhánh): B2 (`agent-a401dc1c4e5da5c41`), B3 (`agent-a39453d0c54f759a1`), B4a (`agent-abec1afd8c9b98d62`), B4b (`agent-a210ebffcda97ce51`), B5b (`agent-af1352b19534a1244`) trong `.claude/worktrees/`.
  - Chưa làm: tài liệu README / F1 cho nhóm A + B (bản nháp nhóm A ở scratchpad phiên trước), nhóm C (CI + ký số, chia file lớn, GDI, test Scheduler/Trigger/Notification/CommandServer, chế độ tối, giao diện tiếng Anh, webhook + cron). Bộ cài chưa biên dịch thử (máy không có Inno Setup; CI job `installer` sẽ kiểm). Chưa merge vào `main`.
- ✅ **Rà soát "cần nâng cấp gì"** (2026-10-05, phiên khác với kiểm tra bảo mật): build Release 0 cảnh báo, test **263 đạt, 0 lỗi, 16 bỏ qua**, NuGet không lỗ hổng. Lỗi nặng đã tự xác minh trong code: settings.json hỏng bị ghi đè mặc định sau ~1 phút (SettingsStore.cs:22-34 + Scheduler.cs:90-95) · không có bắt lỗi toàn cục (Program.cs) · bộ cài không kiểm .NET 10 Desktop Runtime · cập nhật bỏ qua SHA-256 khi thiếu, nhận http:// · bước nhắc việc chờ người dùng chặn cả hàng đợi (FlowRunner.cs:16,100) · trình phát toàn màn hình giành phím khi phát ở màn hình khác (MediaPlayback.cs:191-196). Danh sách ưu tiên ở "Tiếp theo" mục 3. 💤 Chờ người dùng chọn sửa.

## Agent
| Agent | Trạng thái | Việc | Ghi chú |
|---|---|---|---|
| scheduleapp-implementer | ✅ | A ✅ (chưa build theo yêu cầu): dải ảnh / ô sửa bước / thiết lập mẫu — #0,11 `_stripText` + khóa dải khi chờ OK · #1 dòng lỗi thành ô đỏ, OK không kẹt · #2,15 Esc hủy kéo / dừng phát thử, thả ngoài dải = hủy · #3,12,17 ô chọn theo mục vừa chuyển / ảnh kề · #5,20 bỏ qua ≠ lỗi · #6 đếm cả thư mục · #7 vạch chèn đúng chỗ · #10 hủy tải ảnh bị bỏ · #13 biến là thư mục · #16 kích thước theo DPI · #21 tên rút gọn giữa · 5 test mới (+ mở rộng test thiết lập mẫu) | B ✅ (chưa build theo yêu cầu): #4,8,19 sơ đồ không thay biến chạy-mới-biết, nhớ dòng đã thay · #9,14,22 Thu nhỏ không có cửa sổ = bỏ qua, mẫu 15 thêm "Nếu cửa sổ còn mở" · #24 ShowWindowAsync · #25 hộp thoại → cửa sổ chủ · #26 công việc con / xử lý lỗi "Không hiện" · #18,27 "Ẩn" tới khi hết hàng đợi · #28 test · #23 không sửa (Esc = bước lỗi là thiết kế của engine), chỉ sửa mô tả mẫu |
| scheduleapp-tester | ✅ | Build + chạy toàn bộ test (phiên chính làm) | 258 đạt, 0 lỗi, 16 bỏ qua |
| scheduleapp-reviewer | ✅ | Review chọn màn hình phát video | 5 lỗi: giành phím khi phát ở màn hình khác (Space/N/Esc của người đang làm việc) · cửa sổ thường sai cỡ khi màn hình khác DPI · số DISPLAYn ≠ số trong Cài đặt, không ổn định · công việc cũ (cửa sổ thường) đổi màn hình · test chưa phủ toàn màn hình + StepExecutor |
| scheduleapp-ui-checker | 💤 | — | |
| workflow review | ✅ | Review ảnh thu nhỏ, bước Thu nhỏ cửa sổ, khung trạng thái | 29 lỗi, 28 xác nhận |
| implementer B | ✅ | Sơ đồ, Thu nhỏ cửa sổ, mẫu 15, khung trạng thái | 13/14 mục sửa, #23 giữ thiết kế |

## Xong — chưa commit (commit cuối trên `main`: `acd4f51`)
- ✅ Bước **Thu nhỏ cửa sổ** (nhớ cửa sổ vào biến `hwnd:…`, `{{lastWindow}}`), "Kích hoạt cửa sổ" mở lại đúng cửa sổ; nhận cả cửa sổ trình chiếu PowerPoint (đã thử PowerPoint thật).
- ✅ Mẫu **15 · 15:30**: ẩn ứng dụng → video 1 → chờ 10 giây → video 2 → mở lại ứng dụng (Esc → mở lại ngay); không hiện khung trạng thái.
- ✅ Bật / tắt **khung trạng thái**: nút "— Ẩn", mục ở khay, tùy chọn riêng từng công việc.
- ✅ **Ảnh thu nhỏ video như Explorer**: dải ảnh trong ô sửa bước (kéo đổi thứ tự, nhấp đúp phát thử, chuột phải, Delete, kéo thả từ Explorer); nút "Chọn video…" + ảnh khi thiết lập mẫu; ảnh video trên nút ở sơ đồ.
- ✅ Nhóm agent `.claude/agents/` + công cụ `.claude/tools/edit_helper.py`.
- ✅ Sửa 27 lỗi từ review (dải ảnh, ô sửa bước, thiết lập mẫu, sơ đồ, Thu nhỏ cửa sổ, mẫu 15, khung trạng thái).
- ✅ Chọn màn hình phát video khi có nhiều màn hình (`Native/Displays.cs`, `UI/IdentifyScreens.cs`).
- Test gần nhất: **263 đạt, 0 lỗi, 16 bỏ qua** (toàn bộ, chạy thật).

## Tiếp theo
1. 💤 Commit + push khi người dùng yêu cầu (gồm cả `.claude/`).
2. Tùy chọn: bước phát video có "Esc = dừng bình thường" (review #23).
3. Nâng cấp đề xuất (rà soát 2026-10-05, chưa sửa):
   - Nên sửa sớm: settings.json / secrets.json hỏng → giữ bản `.broken-*`, không ghi đè; bắt lỗi toàn cục + ghi crash log; bộ cài kiểm .NET 10 Desktop Runtime (hoặc publish self-contained); trình phát không giành phím khi ở màn hình khác; giới hạn thời gian chạy mỗi công việc (nhắc việc / bước treo chặn hàng đợi).
   - Nên làm: cập nhật bắt buộc SHA-256 + chỉ https + tự quay về bản cũ; khóa bước Chạy lệnh cho công việc tạo qua Telegram; nhật ký giới hạn dung lượng + xóa cũ; đặt lại giờ chạy khi đổi giờ / múi giờ (`SystemEvents.TimeChanged`); cửa sổ thường sai cỡ khi màn hình khác DPI; mật khẩu D365 + header kết nối API vẫn lưu dạng chữ thường; `Unprotect` ném lỗi khi chép dữ liệu sang máy khác.
   - Sau: ký số exe + bộ cài; chia nhỏ StepEditorForm / FlowDesigner / D365Client; giải phóng Font / ảnh GDI; test cho Scheduler, TriggerManager, NotificationService; CI lên action bản mới + Dependabot; gói test (Test.Sdk 18.10.1, xunit.runner 4.0.0); chế độ tối / tương phản cao; giao diện tiếng Anh; trình kích hoạt webhook / REST, cú pháp cron.

## Còn treo (người dùng chưa chọn)
- Dấu ✓ xanh trên sơ đồ cho bước lỗi nhưng flow vẫn chạy tiếp (Assert / OnError = Continue).
- Thử app với 3 người dùng còn lại: Minh, Thư, Tuấn.
- Test Dynamics 365 với môi trường thật (cần org).

## Chưa kiểm chứng
- Phát video ở màn hình thứ 2 / máy chiếu thật (máy chỉ có 1 màn hình); nút "Hiện số màn hình" trên nhiều màn hình.
- Mục bật/tắt khung trạng thái ở khay: chưa bấm bằng chuột thật.
- Chạy trọn mẫu 15 trên máy người dùng (bước đầu sẽ ẩn cửa sổ đang dùng).
- Nhấp đúp ảnh để phát thử: test dùng phím Enter (cửa sổ ngoài màn hình không nhận nhấp đúp).
