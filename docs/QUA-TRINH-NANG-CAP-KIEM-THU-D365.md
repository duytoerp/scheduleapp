# Quá trình nâng cấp kiểm thử tự động Dynamics 365 (model-driven app)

Tài liệu ghi lại toàn bộ quá trình nâng cấp: đánh giá ban đầu, những gì đã làm theo từng bước, cách kiểm chứng (có thể chạy lại), kết quả, và những gì **chưa** kiểm chứng được. Đọc trước khi dùng ScheduleApp làm công cụ kiểm thử hồi quy cho dự án D365, hoặc khi tiếp tục phát triển phần này.

Ngày thực hiện: 02/10/2026. Nhánh: `main`.

## 1. Đánh giá ban đầu

Trước đợt nâng cấp, ScheduleApp đã có: bước *Dynamics 365* gọi Client API (`Xrm`) trong trình duyệt điều khiển (mở form, nhập field mọi kiểu, lưu, nút ribbon, tab, BPF, hộp thoại, Web API bằng phiên trình duyệt, dọn dữ liệu test), bước *Kiểm tra* (giá trị / trạng thái field, thông báo, đếm bản ghi), báo cáo HTML + JUnit, chạy dòng lệnh `--test`, trình ghi thao tác D365.

Đánh giá từ góc nhìn kiểm thử D365 model-driven: **đủ cho luồng "mở form → nhập → lưu → kiểm tra", chưa đủ làm bộ hồi quy chính** vì thiếu:

| Mức | Thiếu | Ảnh hưởng |
|---|---|---|
| 1 | Lưới / subgrid, kiểm tra nút ribbon, chọn form chính / quick create, test theo vai trò, đăng nhập không người trông | Phần lớn test case thực tế; CI hỏng khi phiên đăng nhập hết hạn |
| 1 | Chưa chạy trên D365 thật | Phần nhận diện trang (nút, hộp thoại, thông báo) chỉ đúng trên giả lập |
| 2 | Kịch bản nằm trong `jobs.json` của từng máy; không có môi trường Dev/UAT; không tag; không chạy lại; báo cáo gộp khi lặp dữ liệu; không headless / chia máy | Không làm việc nhóm được, CI khó cấu hình, bộ lớn chạy lâu |

Đề xuất 5 bước: (1) chạy trên D365 thật, (2) subgrid / view / ribbon / form, (3) đăng nhập & vai trò, (4) kịch bản trong git, môi trường, tag, (5) báo cáo theo dữ liệu, headless, chia máy.

## 2. Nguyên tắc khi làm

1. **Ưu tiên API được Microsoft hỗ trợ**: subgrid dùng `formContext.getControl(...).getGrid()`, view dùng FetchXML của chính view qua `Xrm.WebApi`, form chính dùng `Xrm.Navigation.openForm({ formId })` + `formSelector`, tạo nhanh dùng `openForm({ useQuickCreateForm: true }, formParameters)` và lấy Id từ `savedEntityReference`, vai trò dùng `userSettings.roles`, bản ghi liên quan dùng `createFromEntity`.
2. **Chỉ dựa vào cấu trúc trang khi không có API**: trạng thái nút thanh lệnh (`data-id`, `aria-disabled`, menu "…"), nút *Lưu và đóng* của form tạo nhanh (`quickCreateSaveAndCloseBtn`), trang đăng nhập Microsoft (`loginfmt`, `passwd`, `otc`, `idSIButton9`, `idSubmit_SAOTCC_Continue`, `DontShowAgain`). Những chỗ này được ghi rõ là cần kiểm chứng trên D365 thật.
3. **Test thật, không giả kết quả**: logic thuần (TOTP, chọn shard, gộp biến, đọc/ghi thư mục) có unit test; phần chạy trong trình duyệt được chạy **thật trên Edge headless** với máy chủ D365 giả lập; phần cần D365 thật có bộ test riêng, tự bỏ qua khi chưa có môi trường (không đánh dấu "đạt" khi chưa chạy).
4. Mỗi đợt build sạch, chạy toàn bộ test (cả test chạy thật) rồi mới commit.

## 3. Các bước đã làm

### Bước 1 — Kiểm chứng trên Dynamics 365 thật

- **Không thực hiện được trong đợt này**: máy phát triển không có môi trường D365 / tài khoản.
- Đã chuẩn bị bộ test `tests/ScheduleApp.Tests/D365RealTests.cs` (thuộc tính `[RealD365Fact]`, chỉ chạy khi đặt `SCHEDULEAPP_D365_URL`). Bộ này đi trọn: đăng nhập (nếu có tài khoản test), đọc người dùng / vai trò, đọc cấu trúc form account, kiểm tra nút theo command id (`Mscrm.Form.account.Save` hiện, `…Delete` ẩn trên form mới), form selector, lưu thiếu field bắt buộc, lưu, đếm qua Web API, view mặc định + tìm theo tên + mở từ view, subgrid *Contacts* (+ Mới, đếm, tìm dòng, mở dòng), tạo nhanh contact có lookup, rồi dọn dữ liệu.
- Mở rộng máy chủ giả lập (`FakeD365Server.cs`) để các phần dựa vào cấu trúc trang được kiểm thử theo đúng các `data-id` / Id công khai của trang thật.
- **Việc cần làm tiếp**: chạy `D365RealTests` trên môi trường D365 test của dự án (lệnh ở mục 5), sửa nếu có chỗ khác cấu trúc.

### Bước 2 — Subgrid, view, chọn form, nút ribbon, tạo nhanh (commit `ad16c1a`)

| Tính năng | Cách làm | File |
|---|---|---|
| Subgrid: mở dòng, đọc ô, + Mới, làm mới | Tìm control theo tên hoặc nhãn (báo lỗi kèm danh sách subgrid có trên form); đọc `getRows()`, chờ dữ liệu tải; dòng theo số thứ tự hoặc chữ có trong dòng (không phân biệt dấu); + Mới bằng `openForm({ createFromEntity })` | `Automation/D365Client.cs` (`__grid`, `__gridRows`, `__gridRow`) |
| Danh sách (view): đọc / tìm và mở bản ghi | Lấy `savedquery` / `userquery` theo tên / Id / mặc định, chèn thêm cột Id + tên nếu view thiếu, gói bộ lọc gốc + điều kiện tìm vào một filter `and`, chạy `?fetchXml=` | `ViewQueryAsync` |
| Mở form chính theo tên / Id | Tra `systemform` (type 2) theo tên, mở bằng `formId`, chờ form selector đúng form | `__formId`, `__formNow`, `WaitFormAsync` |
| Tạo nhanh | `formParameters` (lookup `bảng:guid` → `field`, `fieldname`, `fieldtype`), bấm *Lưu và đóng*, lấy Id từ promise; lỗi validate trên form tạo nhanh được báo lại | `QuickCreateAsync` |
| Kiểm tra mới | Số dòng / dòng chứa chữ của subgrid, nút thanh lệnh `visible` / `enabled` / `disabled` (mở menu "…" để tìm rồi đóng lại), form đang mở, vai trò | `ConditionEvaluator.cs`, `CommandStateAsync` |
| Giao diện | Ô phụ trong form soạn bước (Form chính / Dòng / Tìm theo tên / Khóa TOTP), nút *Chọn subgrid từ form…* | `UI/StepEditorForm.cs`, `UI/D365Forms.cs` |

### Bước 3 — Đăng nhập tự động & phân quyền (commit `ad16c1a`)

- Bước **Đăng nhập Microsoft**: email → mật khẩu → mã MFA **TOTP** → *Duy trì đăng nhập*; chọn tài khoản trong màn hình nhiều tài khoản; báo lỗi rõ khi sai mật khẩu, thiếu khóa TOTP, tài khoản đòi duyệt Authenticator push, hoặc trang không chuyển bước. Mã TOTP tính theo RFC 6238 (`Automation/Totp.cs`), tránh gửi mã sắp hết hạn.
- **Phiên hết hạn**: khi tab bị chuyển về trang đăng nhập (login.microsoftonline.com, ADFS), bước D365 báo ngay "Phiên đăng nhập Dynamics 365 đã hết" thay vì chờ tới hết giờ.
- **Vai trò**: bước *Đọc người dùng & vai trò* (`{{d365.user}}`, `{{d365.roles}}`) và kiểm tra *D365: người dùng có vai trò*. Kiểm thử theo vai trò = mỗi vai trò một hồ sơ trình duyệt + tài khoản test (đặt trong môi trường hoặc lặp dữ liệu), kiểm tra field khóa / nút ẩn.

### Bước 4 — Kịch bản trong git, môi trường, tag, chạy lại (commit `25933bf`)

- **Thư mục kịch bản** (`Services/Testing/TestFolder.cs`): mỗi công việc một file `Nhóm\Tên.json`, giữ Id, kèm công việc được gọi tới (Chạy công việc khác, công việc xử lý lỗi) và `environments.json`; không ghi kết quả lần chạy (diff gọn); đổi tên / nhóm thì xóa file cũ; nhập lại thì cùng Id được thay. Hai file cùng Id hoặc file hỏng → báo rõ tên file.
- **Môi trường** (`AppSettings.Environments`, `UI/EnvironmentsForm.cs`): bộ biến ghi đè biến của kịch bản; chọn trên trang Kiểm thử (áp dụng cả khi chạy theo lịch) hoặc `--env`; tên môi trường vào báo cáo / junit.xml / `{{env.name}}`.
- **Tag** và **mã test case** (tab *Kiểm thử* của công việc), lọc tag trên trang Kiểm thử, `--tag`.
- **Chạy lại** `--retry N`: đạt ở lần chạy lại được đánh dấu *chập chờn* kèm lỗi lần đầu (bước lỗi + giá trị thực tế). Cột **Ổn định (10 lần)** trên trang Kiểm thử.

### Bước 5 — Theo dữ liệu, headless, chia máy (commit `25933bf`)

- **Kiểm thử theo dữ liệu**: file Excel / CSV của kịch bản, mỗi dòng là một test case riêng (`{{row.Cột}}`, `{{data.index}}`, `{{data.count}}`); file hỏng thành một test case lỗi rõ ràng; chạy theo lịch / nút Chạy cũng chạy từng dòng; *Chạy thử flow* trong trình soạn dùng dòng đầu.
- **Headless**: tùy chọn *Chạy ẩn* của bước Mở trình duyệt và `--headless` cho cả lần chạy CI; cửa sổ 1920×1080 để thanh lệnh D365 không bị thu gọn vào menu.
- **Chia máy** `--shard i/n`: chia theo nhóm, tên, Id nên các máy cùng danh sách kịch bản không trùng / sót. `--list` để xem trước. Song song trong một máy chưa làm (một cổng điều khiển trình duyệt / một flow một lúc).

### Hoàn thiện (commit cuối)

- 3 kịch bản mẫu mới: C5 (subgrid), C6 (phân quyền), C7 (view); C1 có bước *Đăng nhập Microsoft* tắt sẵn.
- 6 chủ đề mới trong trang **Hướng dẫn** (F1 mở đúng chủ đề theo hành động / điều kiện đang soạn), README, danh sách kiểm thử tay `docs/KIEM-THU-THU-CONG.md` mục 7b.
- Tách tab *Kiểm thử* riêng trong trình soạn công việc (hàng tab chỉ cao 205 px, các ô mới bị che khi để chung tab *Lỗi · thông báo*).

## 4. Kiểm chứng đã chạy

| Bộ | Chạy thế nào | Kết quả |
|---|---|---|
| Unit test | `dotnet test ScheduleApp.slnx` | 127 đạt, 12 bỏ qua (test cần màn hình / trình duyệt / D365 thật) |
| Toàn bộ, có test chạy thật | `$env:SCHEDULEAPP_LIVE_TESTS=1; dotnet test ScheduleApp.slnx` | 138 đạt, 1 bỏ qua (`D365RealTests` — chưa có môi trường) |
| D365 thật | `D365RealTests` | **Chưa chạy** |

Các bài test chính của đợt này:

- `D365FeatureTests`: TOTP theo vector chuẩn RFC 4226 / RFC 6238 (6 và 8 chữ số), Base32, mô tả bước, so khớp dòng / vai trò / trạng thái nút, trường Form qua lưu và thay biến.
- `D365AdvancedLiveTests` (Edge headless + máy chủ giả lập): subgrid tải chậm, đọc ô, lỗi kèm danh sách cột / subgrid, mở dòng, + Mới có field cha; form chính theo tên / Id / không dấu; nút bấm được / mờ / trong menu "…" (menu đóng lại); view mặc định / theo tên / theo Id / view cá nhân / bộ lọc "or" / tìm thêm vẫn giữ bộ lọc gốc; tạo nhanh có lookup, lỗi bắt buộc; vai trò. Đăng nhập: phiên hết hạn, sai mật khẩu, không MFA, MFA thiếu khóa, MFA đúng mã, màn hình chọn tài khoản, khóa TOTP sai định dạng. Mẫu C6 chạy trọn qua công việc dùng chung C1 ở chế độ ẩn với URL lấy từ biến môi trường.
- `TestSuiteFeatureTests`: xuất / nhập thư mục (phụ thuộc, đổi tên, file trùng Id / hỏng, gộp), tag, shard (không trùng / sót, không phụ thuộc thứ tự), độ ổn định, môi trường ghi đè biến (cả khi chạy theo lịch), chạy lại & đánh dấu chập chờn, theo dữ liệu (3 test case, junit có thuộc tính), dòng lệnh (mã thoát 0/1/2, `--list`, `--shard`, `--env` sai, `--retry` sai, thư mục không có).
- `HeadlessLiveTests`: bước Mở trình duyệt ẩn mở Edge headless thật, cửa sổ 1920 px.
- `StepEditorD365Tests`: form soạn bước mở rồi lưu giữ đúng các ô mới (không mất dữ liệu).
- `HelpTests`: mọi chủ đề hướng dẫn hiển thị được, chủ đề mà F1 gọi tới đều tồn tại.

Giao diện mới (trang Kiểm thử có thanh lọc, form soạn bước với các hành động mới, hộp thoại môi trường, tab Kiểm thử) đã được chụp ảnh màn hình và xem lại khi phát triển.

## 5. Cách chạy lại / kiểm chứng tiếp

```powershell
# Toàn bộ test (cần Edge, màn hình đang mở khóa)
$env:SCHEDULEAPP_LIVE_TESTS = 1; dotnet test ScheduleApp.slnx

# Với Dynamics 365 thật — dùng môi trường TEST
$env:SCHEDULEAPP_D365_URL = "https://<org>.crm5.dynamics.com/main.aspx?appid=<id app>"
$env:SCHEDULEAPP_D365_PROFILE_DIR = "$env:APPDATA\ScheduleApp\browser-edge-D365 Test"   # hồ sơ đã đăng nhập, đóng Edge của hồ sơ này trước
# hoặc: $env:SCHEDULEAPP_D365_USER / SCHEDULEAPP_D365_PASSWORD / SCHEDULEAPP_D365_TOTP
# xem trình duyệt khi chạy: $env:SCHEDULEAPP_D365_HEADLESS = 0
dotnet test ScheduleApp.slnx --filter FullyQualifiedName~D365RealTests --logger "console;verbosity=detailed"
```

Output của `D365RealTests` in ra danh sách nút, subgrid, vai trò đọc được từ form thật — dùng để đối chiếu nếu có bước không đạt. Sau đó làm danh sách kiểm thử tay mục 7b trong `docs/KIEM-THU-THU-CONG.md`.

Quy trình nhóm đề xuất:

1. Ghi kịch bản bằng **⏺ Ghi kịch bản D365**, thêm kiểm tra, gắn tag (`smoke`, `regression`), đặt môi trường.
2. **Xuất kịch bản ra thư mục** trong repo, commit, review như mã nguồn.
3. Pipeline (agent Windows self-hosted, tài khoản đã đăng nhập D365 trong hồ sơ hoặc dùng bước Đăng nhập Microsoft với Bí mật trên agent):
   `Start-Process ScheduleApp.exe -ArgumentList '--test','*','--test-dir','tests\d365','--env','UAT','--tag','smoke','--retry','1','--headless','--report','$(Build.ArtifactStagingDirectory)' -Wait -PassThru`
   rồi *Publish Test Results* (JUnit). Bộ lớn: 2–3 agent với `--shard 1/3`, `2/3`, `3/3`.

## 6. Giới hạn còn lại (chưa làm hoặc chưa kiểm chứng)

- **Chưa chạy trên D365 thật** (bước 1). Các phần dựa vào cấu trúc trang: trạng thái nút thanh lệnh, nút *Lưu và đóng* của form tạo nhanh, hộp thoại, thông báo, trang đăng nhập Microsoft. Nếu Microsoft đổi các `data-id` / Id này, cần cập nhật `D365Client.cs` (các hàm `__cmd*`, `LoginProbe`, `QuickCreateAsync`).
- Đăng nhập tự động không làm được: duyệt Authenticator push, trang đăng nhập tùy biến của tổ chức (ADFS riêng), Conditional Access yêu cầu thiết bị tuân thủ.
- View được đọc qua Web API theo FetchXML của view, **không** thao tác chuột trên lưới (sắp xếp, lọc cột, chọn nhiều dòng, sửa trên lưới).
- Chưa có: upload file / ảnh vào field, timeline / ghi chú, dashboard / biểu đồ, PCF control và canvas app nhúng (nhập giá trị qua `setValue` kiểm tra được logic nghiệp vụ nhưng không kiểm tra giao diện của control).
- Mỗi máy chỉ một trình duyệt điều khiển và một flow chạy cùng lúc; chạy song song bằng nhiều máy (`--shard`).
- Liên kết Azure DevOps Test Plans: mới ghi *mã test case* vào junit.xml (thuộc tính `testcaseid`), chưa tự cập nhật kết quả vào Test Plans.

## 7. Vấn đề gặp khi làm và cách xử lý

| Vấn đề | Nguyên nhân | Xử lý |
|---|---|---|
| Test đăng nhập lúc đạt lúc không | Tiện ích SelectorsHub cài sẵn trên máy tự mở thêm tab chào; bước chọn nhầm tab đầu tiên | Bước Đăng nhập và bước D365 ưu tiên tab trang đăng nhập / ADFS / Dynamics 365; test chỉ điều khiển tab `localhost`, mở Edge với `--disable-extensions`. Trường hợp này có thể gặp với người dùng thật nên được sửa trong ứng dụng, không chỉ trong test |
| Headless: vùng trang 1896 px thay vì 1920 | `--window-size` là kích thước cửa sổ kể cả khung | Test kiểm tra `outerWidth` = 1920 và vùng trang > 1800 |
| Test form soạn bước đọc ra ô trống | `BuildStep` đọc `Visible` của các ô — form chưa hiện thì luôn sai | Test hiện form ngoài màn hình trước khi đọc |
| Lần chạy đầu của kịch bản chập chờn chỉ ghi "Xong, 1 bước lỗi" | Thông điệp tổng của flow | Thêm `FailureSummary` (bước lỗi đầu tiên + giá trị thực tế), dùng trong báo cáo và dòng lệnh |
| Các ô mới của tab kiểm thử bị che | Hàng tab trong trình soạn cố định 205 px | Tách tab *Kiểm thử* riêng |
| Emoji hiện thành ô vuông trong nhãn | Nhãn WinForms (GDI) không vẽ emoji màu | Bỏ emoji khỏi nhãn mới thêm |
| Nút "? Hướng dẫn" của trang Kiểm thử bị đẩy vào menu tràn | Chạy file exe thật ở kích thước cửa sổ mặc định: thanh lệnh đã kín | Chuyển nút xuống thanh lọc (môi trường / tag), kiểm tra lại bằng ảnh chụp ứng dụng thật |

## 8. Các commit

| Commit | Nội dung |
|---|---|
| `ad16c1a` | D365: subgrid, view, chọn form, nút ribbon, tạo nhanh, đăng nhập Microsoft, vai trò |
| `25933bf` | Kiểm thử: thư mục kịch bản cho git, môi trường, tag, chạy lại, theo dữ liệu, headless, shard |
| commit cuối của đợt | Mẫu C5–C7, bộ test D365 thật, hướng dẫn trong ứng dụng, README, tài liệu này |
