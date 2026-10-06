# STATE — Claude đang làm tới đâu

> Cập nhật mỗi khi bắt đầu / xong một việc. Một phiên làm việc giữ toàn bộ file (không còn nhóm agent).
> Trạng thái: ⏳ đang làm · ✅ xong · ⛔ bị chặn · 💤 chờ người dùng

**Cập nhật lần cuối:** 2026-10-05 · phiên `918ad934`: dừng sửa theo review theo yêu cầu — B4b xong (`7cbacc8`), B3 / B5b dở ở nhánh `wip/…`, B2 / B4a chưa sửa

## Đang làm
- ⛔ **Review + sửa lại B2, B3, B4a, B4b, B5b** (2026-10-05, phiên `918ad934`) — **người dùng bảo dừng, commit**. Worktree cũ không có trên máy này → review lại từ đầu. Ánh xạ suy từ thứ tự commit: B2 = `962819d` · B3 = `7d661ae` · B4a = `9f7d619` · B4b = `5adf95c` · B5b = `64fe6d1`. Đã review xong cả 5 (5 agent, chỉ đọc).
  - ✅ **B4b xong** — `7cbacc8` trên `main` (chưa push): chữ ký hết hạn / không tin cậy vẫn là "đã ký" (không lùi về chỉ SHA-256) · so người ký theo Subject (gia hạn chứng chỉ không khóa cập nhật) · không đóng được hộp thoại khi đang cập nhật · quay về bản cũ chép trả jobs/settings/secrets · phiên bản khác mở giữa chừng (`.second`) không bị coi là hỏng · treo trước Main → dừng theo đường dẫn file · không thay được file → ghi chú "locked" · bỏ qua bản vừa hỏng. Test cập nhật **26 đạt**. Chưa làm: release.yml chưa ký số (đã ghi chú trong file); CI chỉ phát bản framework-dependent (bản cài self-contained trên máy không có .NET sẽ luôn quay về bản cũ).
  - ⏸ **B3** dở — nhánh `wip/review-b3` (`40a41c4`, chưa build/test/review): đã sửa một phần BrowserClient, BrowserProfiles, MacroRecorder, test. Việc cần: (1 NẶNG) mật khẩu ô web vẫn có thể lưu chữ thường — kết quả dò ô mật khẩu đến muộn / dò nhầm ô đang focus vẫn bỏ che, Unknown→Unknown không tách bước; test `PendingPasswordCheckIsResolvedBeforeCommit` đang khẳng định hành vi sai · (2) trình duyệt tự mở lại (cập nhật Chrome, bản portable) → bước lỗi, cần TryReattach · (3) chuyển hồ sơ cũ chép cả cache, không hủy được · (4) trình duyệt mở bởi bản cũ (cổng 9222) không nối lại được · (5) `file://host/share` lọt (lộ NTLM) · (6) chrome://, edge://, about: bị chặn.
  - ⏸ **B5b** dở — nhánh `wip/review-b5b` (`53d2e2d`, chưa build/test/review): đã sửa một phần AppSettings, ApiClient, FlowContext, Log, NotificationService, SecretHider, SettingsStore, StepExecutor, SettingsForm. Việc cần: (1) lưu Cài đặt làm mất `AllowNoTls` · (2) máy cũ vẫn gửi ảnh lỗi (cần chuyển đổi một lần) · (3) token tạo lúc chạy ghi log chữ thường trước khi được che · (4) gửi AI còn lộ: SetVariable tên bí mật, `Basic {{cred}}`, `key=` trong query · (5) webhook http · (6) chuyển hướng 307/308 sang máy khác gửi lại body · (7) so máy chủ không so cổng · (8) che giá trị quá ngắn · (9) Windows auth qua http nội bộ bị chặn · (10) test chưa đúng như tên.
  - ⏸ **B2** chưa sửa (agent dừng trước khi sửa code): (1) đồng hồ lùi / đổi múi giờ về phía tây ngay sau khi chạy → chạy lại lần 2 · (2) chỉnh đồng hồ tiến → mọi công việc bị coi là lỡ lịch · (3) lặp N phút có khung giờ lệch 1 giờ khi đổi giờ mùa hè · (4) xóa thư mục log khi đang chạy → mất log tới nửa đêm · (5) StartAt lưu kèm "+07:00", LastAlive giờ địa phương · (6) Once tạo trong giờ lặp lại (lùi giờ) không chạy · (7) pipe lệnh: UnauthorizedAccessException, log mỗi 500 ms · (8) xóa công việc xóa phiên bản cũ cả khi lưu lỗi · (9) test.
  - ⏸ **B4a** chưa sửa (agent dừng, không còn thay đổi): (1 VỪA-NẶNG) nhập thư mục kiểm thử thay môi trường không cần duyệt (đổi biến của công việc đã duyệt) · (2) `\.\UNC\`, `\?\GLOBALROOT\`, `\??\UNC\` lọt khỏi RemotePathGate · (3) bản tóm tắt duyệt hiện nhãn CallJob giả, không liệt kê CallJob/OnFailure; Renew giữ JobRef ngoài file · (4) chạy bộ kiểm thử đọc DataFile của kịch bản chưa duyệt · (5) hộp xác nhận nhập đếm sai · (6) sau khi đồng ý cho bộ cài đóng app vẫn có thể bắt đầu flow · (7) shortcut theo Id báo Guid thô · (8) test chép logic thay vì gọi code thật.
- ✅ **Kiểm tra bảo mật** (2026-10-05): 5 agent đọc code + phiên chính xác minh trong code. **7 nặng**: chèn lệnh qua "Chạy lệnh (cmd)" (mẫu A4/A6 dính), Telegram nhóm ai cũng điều khiển, Telegram tạo + chạy công việc không cần xác nhận, cập nhật không kiểm tra chữ ký, nhập file công việc là chạy ngay, mật khẩu kẹt trong clipboard, email giả người gửi. ~15 vừa, ~12 nhẹ. Không có thư viện NuGet lỗi. Danh sách đã gửi phiên `scheduleapp-0d` (đang sửa trên nhánh `nang-cap-ra-soat`); phiên này chỉ đọc.
- ✅ **Chọn màn hình phát video** (máy nhiều màn hình): bước "Phát video / nhạc" có ô *Màn hình phát* — màn hình chính / màn hình đang có chuột / màn hình số N; nút "Hiện số màn hình"; màn hình chưa cắm → phát ở màn hình chính + cảnh báo nhật ký. Test **263 đạt, 0 lỗi, 16 bỏ qua**.
- ⚠ Máy người dùng chỉ có 1 màn hình: chưa thử phát thật ở màn hình thứ 2 (đã test đặt cửa sổ đúng khung pixel, ngoài màn hình).
- 💤 Chờ người dùng: commit + push? tùy chọn "Esc = dừng bình thường" cho bước phát video?
- ⛔ **Nâng cấp theo rà soát — người dùng bảo dừng, commit + push** (2026-10-05): nhánh `nang-cap-ra-soat` đã push. Gồm: nhóm A (`badb3bd`, `d2fec14`, `95c0a5d`) + sửa review nhóm A (`e3c85c3`) + nhóm B: `b98e1e7` chèn lệnh cmd + clipboard · `962819d` nhật ký / giờ / DST / pipe · `7d661ae` trình duyệt + ghi macro · `9f7d619` Telegram + duyệt công việc từ xa / nhập file · `5adf95c` cập nhật an toàn · `baed237` bí mật lưu trên máy + D365 · `64fe6d1` che bí mật + mạng · `bcdd7e6` email + đính kèm + dữ liệu. Test sau gộp **497 đạt, 0 lỗi, 17 bỏ qua** (Release).
  - Đã review + sửa theo review: nhóm A, B1, B5a, B6. **Chưa sửa xong theo review** (phần dở chưa commit, còn nằm trong worktree, không đưa vào nhánh): B2 (`agent-a401dc1c4e5da5c41`), B3 (`agent-a39453d0c54f759a1`), B4a (`agent-abec1afd8c9b98d62`), B4b (`agent-a210ebffcda97ce51`), B5b (`agent-af1352b19534a1244`) trong `.claude/worktrees/`.
  - Chưa làm: tài liệu README / F1 cho nhóm A + B (bản nháp nhóm A ở scratchpad phiên trước), nhóm C (CI + ký số, chia file lớn, GDI, test Scheduler/Trigger/Notification/CommandServer, chế độ tối, giao diện tiếng Anh, webhook + cron). Bộ cài chưa biên dịch thử (máy không có Inno Setup; CI job `installer` sẽ kiểm). Đã merge vào `main` (`6c2429b`).
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
