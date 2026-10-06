# STATE — Claude đang làm tới đâu

> Cập nhật mỗi khi bắt đầu / xong một việc. Một phiên làm việc giữ toàn bộ file (không còn nhóm agent).
> Trạng thái: ⏳ đang làm · ✅ xong · ⛔ bị chặn · 💤 chờ người dùng

**Cập nhật lần cuối:** 2026-10-06 · phiên `918ad934`: sửa bảo mật theo thứ tự — CI/bộ cài, B3, B4a, B5b xong; còn B2

## Đang làm
- ✅ **Review + sửa lại B2, B3, B4a, B4b, B5b** (2026-10-05 → 06, phiên `918ad934`) — xong cả 5. Ánh xạ suy từ thứ tự commit: B2 = `962819d` · B3 = `7d661ae` · B4a = `9f7d619` · B4b = `5adf95c` · B5b = `64fe6d1`. Bản 2.2.1 (tag `v2.2.1`) đã phát hành, có B3 / B4a / B4b / B5b + tự vừa màn hình; B2 sau bản đó. Toàn bộ test Release sau B5b: **526 đạt, 0 lỗi, 17 bỏ qua**.
  - ✅ **Bảo mật CI + bộ cài** — `e8a18f7`: ci.yml `permissions: contents: read`; release.yml tên tag qua biến môi trường + kiểm dạng v1.2.3; ghim action theo mã commit; ghim Inno Setup 6.7.1; bộ cài chỉ chạy bộ cài .NET có chữ ký Microsoft (đã thử lệnh PowerShell: file Microsoft → 0, file chưa ký → 1). **Chưa kiểm:** bộ cài chưa biên dịch thử (máy không có Inno Setup — CI job `installer` sẽ kiểm khi push).
  - ✅ **B4b** — `7cbacc8` (cập nhật an toàn).
  - ✅ **B3** — `0a60879`: kết quả dò ô mật khẩu tới muộn / dò nhầm ô không bao giờ làm lộ chữ; tách bước khi tiêu điểm đổi giữa hai ô web; trình duyệt tự mở lại được nối tiếp; chuyển hồ sơ bỏ bộ nhớ đệm, hủy được; dùng tiếp trình duyệt cổng 9222 của bản cũ; chặn file:// thư mục mạng (localhost được); cho chrome://, edge://.
  - ✅ **B4a** — `be3561c`: thay đổi môi trường khi nhập thư mục kịch bản hỏi riêng (liệt kê từng biến, mặc định Không); RemotePathGate chặn \.\UNC\, \?\GLOBALROOT\, \??\UNC\ (chỉ X:\ sau tiền tố là cục bộ); tóm tắt duyệt ghi tên thật công việc được gọi / chạy khi lỗi (cảnh báo công việc ĐÃ DUYỆT có sẵn); không đọc file dữ liệu của kịch bản chờ duyệt; đếm đúng khi nhập; FlowRunner.HoldNewRuns sau khi đồng ý cho bộ cài đóng app; shortcut trỏ công việc đã xóa báo rõ; JobApproval.ImportFile dùng chung. Không đổi: Renew vẫn giữ JobRef tới công việc có sẵn (để xuất / nhập trên cùng máy) — thay bằng cảnh báo trong tóm tắt.
  - ✅ **B5b** — `d8f5d06`: ô "Cho phép gửi không mã hóa"; SettingsVersion 1 tắt gửi ảnh lỗi một lần; che token ngay khi gán biến tên bí mật (Log.MaskGuessed ≥ 6 ký tự); gửi AI không lộ SetVariable bí mật / Basic {{cred}} / key=; webhook bắt buộc https; không gửi lại nội dung khi 307/308 sang máy khác; so cả cổng; xác thực Windows qua http chỉ tới máy nội bộ (tên một chữ, .local/.lan/.internal/.corp/.intranet, IP riêng). Lịch sử chạy cũ không được che lại.
  - ✅ **B2** — (commit này): đồng hồ lùi / đổi múi giờ phía tây không chạy lại lần vừa chạy (nhớ giờ đồng hồ của lần chạy, trong 1 ngày); đồng hồ chỉnh tới (so UTC với Environment.TickCount64, lệch > 30 giây) không coi là lỡ lịch, máy ngủ dậy vẫn là lỡ lịch; lặp có khung giờ / từ 1 ngày trở lên theo giờ đồng hồ (giờ mùa hè không lệch); "một lần" tạo trong giờ lặp lại chạy ở lần hai (đã chạy ở lần đầu thì Scheduler chặn theo LastRun); StartAt lưu giờ đồng hồ không kèm múi giờ (đọc file cũ +07:00 đúng giờ đã ghi), thêm LastAliveUtc; thư mục log bị xóa thì tạo lại; kênh lệnh bắt UnauthorizedAccessException, chờ lâu dần, mỗi kiểu lỗi ghi một lần; xóa công việc chỉ xóa phiên bản cũ khi lưu được; bỏ dọn ảnh lỗi trùng lúc mở app. Test Release **539 đạt, 0 lỗi, 17 bỏ qua**. Chưa có test: kênh lệnh chờ lâu dần.
- ✅ **Kiểm tra bảo mật** (2026-10-05): 5 agent đọc code + phiên chính xác minh trong code. **7 nặng**: chèn lệnh qua "Chạy lệnh (cmd)" (mẫu A4/A6 dính), Telegram nhóm ai cũng điều khiển, Telegram tạo + chạy công việc không cần xác nhận, cập nhật không kiểm tra chữ ký, nhập file công việc là chạy ngay, mật khẩu kẹt trong clipboard, email giả người gửi. ~15 vừa, ~12 nhẹ. Không có thư viện NuGet lỗi. Danh sách đã gửi phiên `scheduleapp-0d` (đang sửa trên nhánh `nang-cap-ra-soat`); phiên này chỉ đọc.
- ✅ **Chọn màn hình phát video** (máy nhiều màn hình): bước "Phát video / nhạc" có ô *Màn hình phát* — màn hình chính / màn hình đang có chuột / màn hình số N; nút "Hiện số màn hình"; màn hình chưa cắm → phát ở màn hình chính + cảnh báo nhật ký. Test **263 đạt, 0 lỗi, 16 bỏ qua**.
- ⚠ Máy người dùng chỉ có 1 màn hình: chưa thử phát thật ở màn hình thứ 2 (đã test đặt cửa sổ đúng khung pixel, ngoài màn hình).
- 💤 Chờ người dùng: commit + push? tùy chọn "Esc = dừng bình thường" cho bước phát video?
- ⛔ **Nâng cấp theo rà soát — người dùng bảo dừng, commit + push** (2026-10-05): nhánh `nang-cap-ra-soat` đã push. Gồm: nhóm A (`badb3bd`, `d2fec14`, `95c0a5d`) + sửa review nhóm A (`e3c85c3`) + nhóm B: `b98e1e7` chèn lệnh cmd + clipboard · `962819d` nhật ký / giờ / DST / pipe · `7d661ae` trình duyệt + ghi macro · `9f7d619` Telegram + duyệt công việc từ xa / nhập file · `5adf95c` cập nhật an toàn · `baed237` bí mật lưu trên máy + D365 · `64fe6d1` che bí mật + mạng · `bcdd7e6` email + đính kèm + dữ liệu. Test sau gộp **497 đạt, 0 lỗi, 17 bỏ qua** (Release).
  - Đã review + sửa theo review: nhóm A, B1, B5a, B6. **Chưa sửa xong theo review** (phần dở chưa commit, còn nằm trong worktree, không đưa vào nhánh): B2 (`agent-a401dc1c4e5da5c41`), B3 (`agent-a39453d0c54f759a1`), B4a (`agent-abec1afd8c9b98d62`), B4b (`agent-a210ebffcda97ce51`), B5b (`agent-af1352b19534a1244`) trong `.claude/worktrees/`.
  - Tài liệu README / F1 cho nhóm A + B: ✅ 2026-10-06 (README mục "An toàn, bảo mật & dữ liệu", F1 chủ đề `security`, mục Telegram). Chưa làm: nhóm C (CI + ký số, chia file lớn, GDI, test Scheduler/Trigger/Notification/CommandServer, chế độ tối, giao diện tiếng Anh, webhook + cron). Bộ cài chưa biên dịch thử (máy không có Inno Setup; CI job `installer` sẽ kiểm). Đã merge vào `main` (`6c2429b`).
- ✅ **Rà soát "cần nâng cấp gì"** (2026-10-05, phiên khác với kiểm tra bảo mật): build Release 0 cảnh báo, test **263 đạt, 0 lỗi, 16 bỏ qua**, NuGet không lỗ hổng. Lỗi nặng đã tự xác minh trong code: settings.json hỏng bị ghi đè mặc định sau ~1 phút (SettingsStore.cs:22-34 + Scheduler.cs:90-95) · không có bắt lỗi toàn cục (Program.cs) · bộ cài không kiểm .NET 10 Desktop Runtime · cập nhật bỏ qua SHA-256 khi thiếu, nhận http:// · bước nhắc việc chờ người dùng chặn cả hàng đợi (FlowRunner.cs:16,100) · trình phát toàn màn hình giành phím khi phát ở màn hình khác (MediaPlayback.cs:191-196). Danh sách ưu tiên ở "Tiếp theo" mục 3. 💤 Chờ người dùng chọn sửa.

## Xong — chưa commit (commit cuối trên `main`: `acd4f51`)
- ✅ Bước **Thu nhỏ cửa sổ** (nhớ cửa sổ vào biến `hwnd:…`, `{{lastWindow}}`), "Kích hoạt cửa sổ" mở lại đúng cửa sổ; nhận cả cửa sổ trình chiếu PowerPoint (đã thử PowerPoint thật).
- ✅ Mẫu **15 · 15:30**: ẩn ứng dụng → video 1 → chờ 10 giây → video 2 → mở lại ứng dụng (Esc → mở lại ngay); không hiện khung trạng thái.
- ✅ Bật / tắt **khung trạng thái**: nút "— Ẩn", mục ở khay, tùy chọn riêng từng công việc.
- ✅ **Ảnh thu nhỏ video như Explorer**: dải ảnh trong ô sửa bước (kéo đổi thứ tự, nhấp đúp phát thử, chuột phải, Delete, kéo thả từ Explorer); nút "Chọn video…" + ảnh khi thiết lập mẫu; ảnh video trên nút ở sơ đồ.
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
