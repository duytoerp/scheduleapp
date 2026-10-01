namespace ScheduleApp.UI;

/// <summary>Nút "làm ngay" trong một chủ đề hướng dẫn — <see cref="Command"/> do màn hình chính xử lý (xem <see cref="IHelpHost"/>).</summary>
internal sealed record HelpAction(string Text, string Command);

/// <summary>
/// Một chủ đề hướng dẫn. <see cref="Body"/> dùng cú pháp gọn: <c>## tiêu đề</c>, <c>1. bước</c>, <c>- ý</c>,
/// <c>&gt; mẹo</c>, <c>! lưu ý</c>, khối <c>```</c> mã, trong dòng có <c>**đậm**</c> và <c>`mã`</c>. Mỗi dòng là một đoạn.
/// </summary>
internal sealed record HelpTopic(string Id, string Group, string Title, string Summary, string Body, params HelpAction[] Actions);

internal static class HelpContent
{
    public const string Start = "start";

    // Lệnh của nút "làm ngay".
    public const string CmdNewJob = "new-job";
    public const string CmdTemplates = "templates";
    public const string CmdOpenTests = "open-tests";
    public const string CmdNewTest = "new-test";
    public const string CmdRecordD365 = "record-d365";
    public const string CmdReports = "reports";
    public const string CmdHistory = "history";
    public const string CmdSettings = "settings";
    public const string CmdSecrets = "secrets";

    public static readonly string[] Commands = [CmdNewJob, CmdTemplates, CmdOpenTests, CmdNewTest, CmdRecordD365, CmdReports, CmdHistory, CmdSettings, CmdSecrets];

    public static HelpTopic? Find(string? id) => Topics.FirstOrDefault(t => t.Id == id);

    public static readonly IReadOnlyList<HelpTopic> Topics =
    [
        new("start", "Bắt đầu", "Bắt đầu nhanh",
            "ScheduleApp làm gì và cách tạo công việc đầu tiên trong vài phút.",
            """
            ScheduleApp tự làm thay bạn các việc lặp lại trên máy tính: mở ứng dụng, nhập liệu, bấm nút, gọi API, gửi thông báo… theo lịch hoặc khi có sự kiện. Nó cũng chạy được **kịch bản kiểm thử tự động cho Dynamics 365** và xuất báo cáo.
            ## Màn hình chính
            - **Công việc**: danh sách công việc theo nhóm. Tick để bật/tắt lịch, nhấp đúp để sửa, chuột phải để xem thêm lệnh.
            - **Kiểm thử**: các kịch bản kiểm thử, kết quả lần chạy cuối và báo cáo.
            - **Lịch sử chạy**, **Bí mật**, **Cài đặt**, **Hướng dẫn** ở mục *Công cụ*.
            - Nhật ký hoạt động ở dưới danh sách công việc. Bấm `📜 Nhật ký` trên thanh trạng thái để ẩn/hiện.
            ## Tạo công việc đầu tiên
            1. Bấm **＋ Thêm công việc** (hoặc **Mẫu có sẵn…** để bắt đầu từ ví dụ).
            2. Đặt tên, nhóm và chọn lịch chạy ở tab **Lịch chạy**.
            3. Kéo các thao tác từ **Hộp công cụ** bên trái thả vào khung luồng ở giữa, điền thông tin rồi bấm OK.
            4. Bấm **▶ Chạy thử flow** (`F5`) để xem flow chạy thật, sửa nếu có bước lỗi.
            5. Bấm **Lưu**. Công việc tự chạy theo lịch khi đang được tick ở danh sách chính.
            > Bấm nút X chỉ thu ScheduleApp xuống khay hệ thống, lịch vẫn chạy. Muốn thoát hẳn: chuột phải biểu tượng ở khay → **Thoát**.
            ! Dừng khẩn cấp mọi flow bằng `Ctrl+Shift+Q` (dùng được ở bất kỳ đâu).
            > Ở bất kỳ cửa sổ nào của ScheduleApp, nhấn `F1` để mở hướng dẫn đúng chủ đề đang làm.
            """,
            new HelpAction("＋ Thêm công việc", CmdNewJob), new HelpAction("Mẫu có sẵn…", CmdTemplates)),

        new("schedule", "Bắt đầu", "Lịch chạy & kích hoạt",
            "Chạy một lần, hằng ngày, hằng tuần, hằng tháng, theo phút, hoặc khi có sự kiện.",
            """
            ## Tab Lịch chạy
            - **Một lần**, **hằng ngày**, **hằng tuần** (chọn các thứ), **hằng tháng** (ngày N, thứ X của tuần thứ N, ngày làm việc đầu / cuối tháng).
            - **Lặp theo phút** trong khung giờ, ví dụ mỗi 30 phút từ 8:00 đến 17:30, thứ 2 đến thứ 6.
            - **Bỏ qua ngày nghỉ lễ**: danh sách ngày lễ khai báo trong **Cài đặt → Ngày nghỉ**.
            - **Khi lỡ lịch** (máy tắt, ngủ): bỏ qua, chạy bù một lần hoặc hỏi bạn.
            - **Đánh thức máy** khi đang Sleep để chạy đúng giờ.
            ## Tab Kích hoạt khác
            Chạy công việc khi: nhấn phím tắt, có file mới trong thư mục, ứng dụng vừa mở/đóng, máy rảnh N phút, mở khóa màn hình, ScheduleApp khởi động, có email mới.
            > Mỗi lúc chỉ chạy một flow. Các flow đến hạn cùng lúc sẽ xếp hàng chạy lần lượt.
            """),

        new("flow", "Bắt đầu", "Dựng flow bằng kéo thả",
            "Hộp công cụ, khung luồng, sắp xếp bước, chạy thử và gỡ lỗi.",
            """
            Trình soạn công việc có 3 vùng: **Hộp công cụ** (trái), **khung luồng** Bắt đầu → các bước → Kết thúc (giữa), nút lệnh (phải).
            ## Thêm và sửa bước
            1. Kéo một thao tác từ hộp công cụ, thả vào chỗ muốn chèn (đường kẻ xanh báo vị trí). Hoặc nhấp đúp thao tác để chèn sau bước đang chọn.
            2. Điền thông tin trong form soạn bước → **OK**. Bấm **▶ Thử bước này** để chạy riêng bước đó ngay.
            3. Nhấp đúp thẻ (hoặc `Enter`) để sửa lại. Kéo thẻ lên/xuống để sắp xếp.
            - Thêm *Nếu* / *Lặp* sẽ tự chèn *Hết Nếu* / *Hết lặp*. Các bước bên trong được thụt lề.
            - Thẻ viền đỏ là lỗi cấu trúc (thiếu *Hết Nếu*, nhãn không tồn tại…). Di chuột lên thẻ để xem chi tiết.
            - Tùy chọn ít dùng (thử lại khi lỗi, xử lý lỗi, nghỉ sau bước, bật/tắt, điểm dừng) nằm trong mục **▸ Nâng cao** của form soạn bước.
            ## Chạy thử và gỡ lỗi
            - **▶ Chạy thử flow** (`F5`): thẻ đang chạy tô xanh, bước lỗi tô đỏ.
            - **⤵ Chạy từ bước chọn**: bỏ qua các bước phía trên.
            - **Điểm dừng** (`F9`) và **⏭ Chạy từng bước**: flow dừng trước bước, hiện giá trị mọi biến. `F10` bước tiếp, `F5` chạy tiếp, `Shift+F5` dừng.
            - **Phiên bản cũ…**: mỗi lần lưu, bản trước được giữ lại (30 bản) để khôi phục.
            ## Phím tắt trong khung luồng
            - `↑` `↓` chọn bước · `Ctrl+↑` `Ctrl+↓` di chuyển · `Space` bật/tắt · `Delete` xóa
            - `Ctrl+C` / `Ctrl+V` sao chép bước (cả sang công việc khác) · `Ctrl+D` nhân bản · `Ctrl+Z` / `Ctrl+Y` hoàn tác / làm lại
            """,
            new HelpAction("＋ Thêm công việc", CmdNewJob)),

        new("variables", "Bắt đầu", "Biến & bí mật",
            "Dùng {{tên}} trong mọi ô chữ: ngày giờ, dữ liệu Excel, kết quả API, mật khẩu mã hóa.",
            """
            Mọi ô chữ của bước dùng được `{{tên}}`. Giá trị được thay vào lúc chạy.
            ## Biến có sẵn
            - Ngày giờ: `{{today}}`, `{{now}}`, `{{today-1}}`, `{{now+30m}}`, định dạng `{{today:dd/MM/yyyy}}`.
            - `{{clipboard}}`, `{{env:USERNAME}}`, `{{random:1-100}}`, `{{guid}}`.
            - Kết quả gần nhất: `{{lastOutput}}` (lệnh cmd), `{{lastError}}` (bước lỗi), `{{http.status}}`, `{{http.body}}` (gọi API).
            - Trong vòng lặp: `{{loop.index}}`, `{{row.TênCột}}` (mỗi dòng Excel/CSV), `{{item}}`.
            - Dynamics 365: `{{d365.lastId}}` (Id bản ghi vừa lưu/mở), `{{d365.created}}` (các bản ghi do flow tạo).
            ## Tự tạo biến
            1. Khai báo giá trị ban đầu ở tab **Biến** của công việc.
            2. Gán / đổi trong flow bằng bước **Gán biến** (giá trị, phép tính, clipboard, output lệnh, trích JSON, hỏi người dùng…).
            ## Bí mật
            Mật khẩu, token lưu trong **Bí mật** (mã hóa bằng tài khoản Windows của bạn), dùng bằng `{{secret:Tên}}`. Giá trị không hiện lại, được che `***` trong nhật ký và không bị xuất ra file khi chia sẻ công việc.
            > Định dạng giá trị: `{{ten:upper}}`, `{{ten:trim}}`, `{{ten:nodiacritics}}` (bỏ dấu), `{{tien:N0}}` (1.234.568), `{{ten:json}}` (đặt vào chuỗi JSON an toàn).
            """,
            new HelpAction("Mở Bí mật", CmdSecrets)),

        new("errors", "Bắt đầu", "Xử lý lỗi, lịch sử & thông báo",
            "Thử lại khi lỗi, nhảy tới nhãn, ảnh chụp lỗi, gửi thông báo Telegram / email / Teams.",
            """
            ## Khi một bước lỗi
            Trong mục **▸ Nâng cao** của form soạn bước: *thử lại N lần cách X ms*, rồi *dừng flow* / *bỏ qua chạy tiếp* / *nhảy tới nhãn* / theo cài đặt của công việc.
            ## Tab Lỗi · thông báo · kiểm thử của công việc
            - Dừng flow khi một bước lỗi (mặc định).
            - **Công việc chạy khi thất bại**: dọn dẹp, gửi báo cáo, có `{{failed.message}}`.
            - Khi nào gửi thông báo: chỉ khi lỗi, mỗi lần, hoặc không.
            ## Xem lại
            - **Lịch sử chạy**: mọi lần chạy, kết quả, bước lỗi, ảnh chụp màn hình lúc lỗi, thống kê theo ngày.
            - Kênh thông báo (Telegram, email SMTP, webhook Teams/Slack) khai báo trong **Cài đặt → Thông báo**, có nút *Gửi thử*.
            """,
            new HelpAction("Lịch sử chạy", CmdHistory), new HelpAction("Cài đặt", CmdSettings)),

        new("d365-overview", "Kiểm thử Dynamics 365", "Tổng quan kiểm thử D365",
            "Kịch bản kiểm thử là gì, quy trình 5 bước từ chuẩn bị tới báo cáo.",
            """
            ScheduleApp kiểm thử được app **Dynamics 365 CE / Power Apps model-driven** mà không cần viết code. Các bước gọi thẳng **Client API (Xrm)** của form trong trình duyệt, nên kịch bản không vỡ khi Microsoft đổi giao diện, và dùng luôn phiên đăng nhập của trình duyệt (MFA, SSO).
            ## Kịch bản kiểm thử
            Một **kịch bản** là một công việc được tick **Đây là kịch bản kiểm thử** (tab *Lỗi · thông báo · kiểm thử*). Mỗi lần chạy, ScheduleApp ghi kết quả từng bước, từng điều kiện *Kiểm tra*, chụp ảnh khi lỗi và xuất báo cáo HTML + JUnit XML.
            ## Quy trình
            1. **Chuẩn bị**: một công việc dùng chung mở trình duyệt vào app D365 bằng hồ sơ riêng. Xem chủ đề *Chuẩn bị trình duyệt & đăng nhập*.
            2. **Ghi thao tác**: trang **Kiểm thử** → **⏺ Ghi kịch bản D365**, thao tác trên form, các bước được tạo tự động.
            3. **Thêm kiểm tra**: chọn field trên form để tạo bước *Kiểm tra* (giá trị, bắt buộc, khóa, thông báo lỗi…).
            4. **Chạy**: từ trang Kiểm thử, theo lịch, hoặc từ dòng lệnh / CI.
            5. **Đọc báo cáo**: ĐẠT / KHÔNG ĐẠT từng bước, giá trị thực tế, ảnh chụp khi lỗi.
            > Cách nhanh nhất để xem một kịch bản hoàn chỉnh: **Mẫu có sẵn…** → nhóm *Mẫu kiểm thử Dynamics 365* (C1 mở app, C2 tạo khách hàng, C3 kiểm thử lỗi bắt buộc, C4 dữ liệu qua Web API).
            """,
            new HelpAction("Mở trang Kiểm thử", CmdOpenTests), new HelpAction("Thêm mẫu kiểm thử", CmdTemplates)),

        new("d365-setup", "Kiểm thử Dynamics 365", "Chuẩn bị trình duyệt & đăng nhập",
            "Mở Edge/Chrome ở chế độ điều khiển với hồ sơ riêng, đăng nhập D365 một lần.",
            """
            ScheduleApp điều khiển một cửa sổ Edge/Chrome riêng (chế độ điều khiển, cổng 9222). Cửa sổ này dùng **hồ sơ riêng** nên không đụng tới trình duyệt bạn đang dùng.
            ## Lần đầu
            1. Tạo công việc dùng chung, ví dụ "Mở app D365", với bước **Trình duyệt → Mở trình duyệt ở chế độ điều khiển**.
            2. Chọn **Edge**, ô **Hồ sơ** gõ một tên, ví dụ `D365 Test`, ô URL là địa chỉ app: `https://<org>.crm5.dynamics.com/main.aspx?appid=…`
            3. Bấm **▶ Thử bước này**. Trình duyệt mở ra, bạn **đăng nhập tay một lần** (kể cả MFA).
            4. Các lần sau hồ sơ đã nhớ đăng nhập, flow chạy không cần bạn.
            ## Dùng trong kịch bản
            Ở đầu mỗi kịch bản thêm bước **Chạy công việc khác** → chọn công việc "Mở app D365". Trình ghi D365 làm việc này tự động khi tạo kịch bản mới.
            > Không cần mở trước cũng được: cửa sổ **Ghi thao tác Dynamics 365** có ô *URL app* + *Hồ sơ* để mở trình duyệt ngay.
            ! Chỉ một trình duyệt điều khiển chạy cùng lúc. Mở hồ sơ khác thì cửa sổ đang mở bị đóng trước.
            ! Hồ sơ trình duyệt giữ phiên đăng nhập D365. Chỉ dùng tài khoản test, không dùng tài khoản quản trị thật.
            """,
            new HelpAction("Thêm mẫu kiểm thử", CmdTemplates)),

        new("d365-record", "Kiểm thử Dynamics 365", "Ghi kịch bản bằng thao tác",
            "Thao tác trên form D365, ScheduleApp tự tạo các bước. Không cần nhớ tên field.",
            """
            ## Các bước
            1. Vào trang **Kiểm thử** → bấm **⏺ Ghi kịch bản D365**. Trình soạn kịch bản mới mở ra cùng cửa sổ ghi nổi bên phải màn hình.
            2. Nếu chưa có trình duyệt điều khiển: nhập **URL app**, chọn trình duyệt, hồ sơ → **Mở trình duyệt**. Đăng nhập nếu được hỏi.
            3. Thao tác bình thường trên form Dynamics 365. Mỗi thao tác hiện thành một bước trong cửa sổ ghi.
            4. Khi form có kết quả cần kiểm tra, bấm **✓ Thêm kiểm tra…**, tick các field → **Tạo bước Kiểm tra**. Giá trị hiện tại của field trở thành giá trị mong đợi.
            5. Bấm **■ Dừng & thêm vào flow**. Các bước được chèn vào kịch bản.
            6. Bấm **▶ Chạy thử flow** (`F5`) để chạy lại, rồi **Lưu**.
            ## Những gì được ghi
            - Mở form bản ghi (mới / có sẵn), mở danh sách (view).
            - Nhập giá trị field (lookup, option set, ngày, số, Có/Không…). Nhập nhiều lần cùng một field chỉ giữ giá trị cuối.
            - Lưu, bấm nút trên thanh lệnh (ribbon), chuyển tab, chuyển giai đoạn BPF, bấm nút trên hộp thoại.
            - Sau khi bấm nút làm chuyển trang, bước *Chờ form tải xong* được thêm tự động.
            > Ghi sai một thao tác: bấm **↶ Bỏ bước cuối** rồi làm lại.
            > Đang sửa một kịch bản có sẵn: trong trình soạn bấm **⏺ Ghi thao tác D365…** (các bước chèn sau bước đang chọn) hoặc **✓ Kiểm tra từ form D365…**.
            > Giá trị như tên khách hàng cố định có thể đổi thành biến sau khi ghi, ví dụ `KH test {{now:HHmmss}}`, để mỗi lần chạy tạo bản ghi khác nhau.
            ! Trình ghi chỉ đọc thao tác trong tab Dynamics 365 của trình duyệt điều khiển. Thao tác ở trình duyệt khác của bạn không được ghi.
            """,
            new HelpAction("⏺ Ghi kịch bản D365", CmdRecordD365), new HelpAction("Mở trang Kiểm thử", CmdOpenTests)),

        new("d365-pick", "Kiểm thử Dynamics 365", "Chọn field từ form đang mở",
            "Đọc form đang mở trong trình duyệt để chọn field, tab, nút — không phải gõ tên logic.",
            """
            Trong form soạn bước **Dynamics 365** hoặc **Kiểm tra**, cạnh ô field / tab / nút có nút **Chọn field từ form…** (hoặc *Chọn tab từ form…*, *Chọn nút từ form…*, *Lấy từ form đang mở*).
            ## Cách dùng
            1. Mở form cần làm trong trình duyệt điều khiển (bản ghi mới hoặc có sẵn).
            2. Bấm nút **Chọn … từ form**. Danh sách hiện nhãn, tên logic, kiểu field, giá trị hiện tại và trạng thái (bắt buộc / khóa / ẩn).
            3. Gõ vào ô tìm (không cần dấu) để lọc, tick **Chỉ field có giá trị** nếu cần, rồi nhấp đúp hoặc bấm **Chọn**.
            - Chọn field option set: các lựa chọn của nó được gợi ý sẵn ở ô giá trị.
            - Bước *Kiểm tra*: giá trị hiện tại của field được điền làm giá trị mong đợi.
            - Form vừa thay đổi: bấm **↻ Đọc lại form**.
            > Nút nằm trong menu "…" (Thêm lệnh) có thể không hiện trong danh sách. Gõ tay nhãn của nút, bước sẽ tự mở menu để tìm.
            """),

        new("d365-steps", "Kiểm thử Dynamics 365", "Bước Dynamics 365",
            "Các hành động của bước Dynamics 365 và cách ghi giá trị cho từng kiểu field.",
            """
            Bước **Dynamics 365** nằm trong nhóm *Kiểm thử & Dynamics 365* của hộp công cụ. Ô **Tab** để trống thì dùng tab đầu tiên có `main.aspx` / `dynamics.com`.
            ## Hành động
            - **Mở form bản ghi**: bảng (tên logic, vd `account`) + Id. Để trống Id = form tạo mới. Tự chờ form tải xong.
            - **Mở danh sách (view)** · **Chờ form tải xong** (dùng sau khi bấm nút chuyển trang).
            - **Nhập giá trị field**: chạy `fireOnChange` nên business rule và script của form chạy như người dùng nhập.
            - **Đọc giá trị field vào biến**: thêm `:raw` để lấy giá trị gốc, vd `parentcustomerid:raw`.
            - **Lưu bản ghi**: lỗi validate, field bắt buộc, lỗi plugin → bước lỗi kèm nội dung lỗi. Id lưu vào `{{d365.lastId}}`.
            - **Bấm nút trên thanh lệnh**: theo nhãn (`Lưu & đóng`) hoặc một phần command id (`Mscrm.Form.account.Deactivate`).
            - **Chuyển tab** · **BPF sang / về giai đoạn** · **Bấm nút trên hộp thoại** (trống = nút chính).
            - **Lấy Id bản ghi** · **Đọc thông báo / lỗi trên form** vào biến.
            - **Gọi Web API** · **Xóa dữ liệu test đã tạo** · **Chạy JavaScript** (có sẵn `formContext`, `Xrm`, dùng được `await`).
            ## Cách ghi giá trị theo kiểu field
            - **Lookup**: tên bản ghi, hoặc `bảng:tên` / `bảng:guid`, vd `account:Contoso`, `contact:{{contactId}}`.
            - **Option set**: nhãn (không phân biệt dấu) hoặc số. **Nhiều lựa chọn**: `A; B`.
            - **Ngày**: `dd/MM/yyyy` hoặc `dd/MM/yyyy HH:mm`. **Có/Không**: `có` / `không`, `true` / `false`.
            - **Số**: `1.234.567,5` hoặc `1234567.5`. **Để trống** = xóa giá trị.
            ! Field bị khóa hoặc ẩn sẽ báo lỗi như với người dùng thật. Tick ô cho phép nếu cố ý nhập vào field đó.
            """),

        new("assert", "Kiểm thử Dynamics 365", "Bước Kiểm tra (Assert)",
            "Kiểm tra một điều kiện, ghi ĐẠT / KHÔNG ĐẠT vào báo cáo kèm giá trị thực tế.",
            """
            Bước **Kiểm tra** dùng mọi điều kiện của *Nếu*, thêm các điều kiện cho Dynamics 365:
            - **D365: giá trị field**: `statuscode` bằng `Đang hoạt động`, `revenue` ≥ `1000000`.
            - **D365: trạng thái field**: `required`, `recommended`, `disabled`, `visible`, `dirty`, `empty`.
            - **D365: form có thông báo / lỗi**: có chứa chữ `bắt buộc`. Để trống = có bất kỳ thông báo nào.
            - **D365: số bản ghi (Web API)**: vd `contacts?$filter=emailaddress1 eq '{{email}}'` ≥ `1`, kiểm tra dữ liệu plugin / Power Automate tạo ra.
            - **Phần tử có trên trang web**: CSS, `xpath:` hoặc `text:`.
            ## Các ô quan trọng
            - **Đảo ngược**: kiểm tra điều ngược lại, vd field *không* bị khóa, form *không* có lỗi.
            - **Chờ tối đa** > 0: kiểm tra lại liên tục tới khi đạt, dùng khi plugin bất đồng bộ / flow chạy chậm.
            - **Mô tả kiểm tra**: câu hiện trong báo cáo, vd "Khách hàng mới có trạng thái Đang hoạt động".
            ## Kiểm tra sai thì sao?
            Mặc định bước được ghi **KHÔNG ĐẠT** rồi flow **chạy tiếp**, để một lần chạy thấy hết mọi chỗ sai. Nếu các bước sau phụ thuộc vào điều kiện này, mở **▸ Nâng cao** → *Khi bước lỗi* → **Dừng flow**.
            > Nhanh nhất: trong trình soạn bấm **✓ Kiểm tra từ form D365…**, tick các field. Mỗi field thành một bước Kiểm tra với giá trị hiện tại.
            """),

        new("test-data", "Kiểm thử Dynamics 365", "Dữ liệu test & dọn dẹp",
            "Tạo dữ liệu nhanh bằng Web API và tự xóa bản ghi do kịch bản tạo ra.",
            """
            ## Tạo dữ liệu chuẩn bị
            Bước **Dynamics 365 → Gọi Web API** dùng phiên đăng nhập của trình duyệt, không cần đăng ký ứng dụng Entra ID:
            ```
            POST  accounts
            {"name": "KH test {{now:HHmmss}}", "telephone1": "0900000000"}
            ```
            Kết quả trong `{{http.body}}`, mã trả về trong `{{http.status}}`. POST trả Id mới vào `{{d365.lastId}}`.
            ## Tự dọn dẹp
            Mọi bản ghi kịch bản tạo ra (lưu form mới hoặc POST Web API) được ghi vào `{{d365.created}}`.
            - Tick **Tự xóa dữ liệu Dynamics 365 do flow tạo ra** (tab *Lỗi · thông báo · kiểm thử*): luôn dọn sau khi chạy, kể cả khi kiểm thử thất bại.
            - Hoặc đặt bước **Xóa dữ liệu test đã tạo** ở cuối flow. Bản tạo sau được xóa trước.
            > Đặt tên dữ liệu test có dấu hiệu riêng (vd tiền tố `KH test`) để dễ nhận ra và dọn tay nếu cần.
            ! Tài khoản test cần quyền xóa trên các bảng đó, nếu không bước dọn dẹp sẽ báo "Không xóa được".
            """),

        new("test-run", "Kiểm thử Dynamics 365", "Chạy kiểm thử & đọc báo cáo",
            "Trang Kiểm thử, chạy lại kịch bản lỗi, báo cáo HTML / JUnit và ảnh chụp lúc lỗi.",
            """
            ## Trang Kiểm thử
            - Thẻ số liệu: tổng kịch bản, đạt / không đạt / chưa chạy ở lần chạy cuối.
            - Danh sách kịch bản: tick để chọn nhiều, nhấp đúp để sửa. Cột *Chi tiết* hiện lý do không đạt gần nhất.
            - **▶ Chạy đã chọn**, **▶ Chạy tất cả**, **✖ Chạy lại các kịch bản lỗi**: chạy lần lượt rồi mở một báo cáo chung.
            - Số kịch bản đang không đạt hiện thành huy hiệu đỏ ở mục **Kiểm thử** bên trái.
            ## Báo cáo
            Mỗi lần chạy tạo một thư mục trong `test-reports\` gồm:
            - `index.html`: từng kịch bản, từng bước với thời gian, ĐẠT / KHÔNG ĐẠT, giá trị thực tế và **ảnh chụp tab trình duyệt** lúc lỗi.
            - `junit.xml`: cho Azure DevOps, Jenkins, GitHub Actions.
            Mở báo cáo: nút **📄 Báo cáo** (kịch bản đang chọn), danh sách *Báo cáo gần đây*, hoặc **Lịch sử chạy → Mở báo cáo kiểm thử**.
            > Kịch bản chạy theo lịch cũng tự ghi báo cáo. Đặt lịch chạy đêm cho cả nhóm để sáng ra xem kết quả.
            """,
            new HelpAction("Mở trang Kiểm thử", CmdOpenTests), new HelpAction("Thư mục báo cáo", CmdReports)),

        new("ci", "Kiểm thử Dynamics 365", "Chạy từ dòng lệnh / CI",
            "Chạy bộ kiểm thử không mở giao diện, lấy mã thoát và junit.xml cho pipeline.",
            """
            ```
            ScheduleApp.exe --test "Kiểm thử CRM" --report "D:\TestResults"
            ```
            - Tham số `--test`: tên nhóm, tên công việc, hoặc `*` (mọi kịch bản).
            - Mã thoát: `0` = mọi kịch bản đạt, `1` = có kịch bản không đạt, `2` = không tìm thấy kịch bản.
            - Chạy độc lập, không cần mở giao diện, chạy được song song với ScheduleApp đang mở ở khay.
            ## Trong PowerShell / Azure DevOps
            ScheduleApp là ứng dụng cửa sổ nên PowerShell không tự chờ. Dùng `Start-Process -Wait`:
            ```
            $p = Start-Process ScheduleApp.exe -ArgumentList '--test', '"Kiểm thử CRM"', '--report', 'D:\TestResults' -Wait -PassThru
            exit $p.ExitCode
            ```
            Sau đó dùng task *Publish Test Results* (định dạng JUnit) trỏ tới `junit.xml`.
            ! Máy chạy cần đăng nhập Windows và đã đăng nhập D365 một lần trong hồ sơ trình duyệt dùng cho kịch bản.
            """),

        new("troubleshoot", "Kiểm thử Dynamics 365", "Xử lý sự cố thường gặp",
            "Ý nghĩa các lỗi hay gặp khi chạy bước trình duyệt / Dynamics 365 và cách sửa.",
            """
            ## "Không kết nối được trình duyệt ở cổng 9222"
            Chưa có trình duyệt điều khiển. Thêm bước **Trình duyệt → Mở trình duyệt ở chế độ điều khiển** (hoặc *Chạy công việc khác* gọi công việc mở app) ở đầu flow.
            ## "Trình duyệt không mở cổng điều khiển"
            Edge/Chrome đang chạy với cùng hồ sơ. Đóng hết cửa sổ trình duyệt đó rồi chạy lại.
            ## "Trang hiện tại không phải form Dynamics 365"
            Tab đang ở trang đăng nhập, danh sách hoặc trang khác. Thêm bước **Mở form bản ghi** hoặc **Chờ form tải xong** trước bước này. Nếu mở nhiều tab, điền ô **Tab** (một phần URL/tiêu đề).
            ## "Form không có field …"
            Sai tên logic, hoặc field không nằm trên form đang mở. Dùng **Chọn field từ form…** để chọn đúng.
            ## "Field … đang bị khóa / bị ẩn"
            Người dùng thật cũng không nhập được. Kiểm tra business rule / quyền, hoặc tick ô cho phép nếu cố ý.
            ## "Không tìm thấy bản ghi tên …" / "Có nhiều bản ghi tên …"
            Giá trị lookup phải khớp đúng một bản ghi. Ghi kèm bảng và Id: `account:{{accountId}}`.
            ## "Lưu không thành công: …"
            Nội dung sau dấu hai chấm là lỗi D365 trả về (field bắt buộc, plugin, quyền). Nếu đây là kịch bản kiểm thử lỗi có chủ đích, dùng **Bấm nút trên thanh lệnh** thay vì *Lưu* rồi *Kiểm tra* thông báo trên form.
            ## Bước hết thời gian chờ
            Form tải chậm hoặc plugin bất đồng bộ. Tăng **Chờ tối đa** của bước (mặc định 30 giây với bước Dynamics 365).
            > Mở báo cáo của lần chạy lỗi để xem ảnh chụp tab trình duyệt ngay lúc lỗi.
            """,
            new HelpAction("Lịch sử chạy", CmdHistory)),

        new("shortcuts", "Khác", "Phím tắt",
            "Các phím tắt trong màn hình chính, trình soạn công việc và khi chạy flow.",
            """
            ## Toàn hệ thống
            - `Ctrl+Shift+Q`: dừng khẩn cấp flow đang chạy và hủy các flow đang chờ.
            ## Mọi cửa sổ
            - `F1`: mở hướng dẫn đúng chủ đề đang làm.
            ## Trình soạn công việc
            - `F5` chạy thử flow · `F9` đặt / bỏ điểm dừng · `F10` bước tiếp khi chạy từng bước · `Shift+F5` dừng.
            - `Enter` sửa bước · `Space` bật/tắt · `Delete` xóa · `Ctrl+D` nhân bản.
            - `Ctrl+↑` / `Ctrl+↓` di chuyển bước · `Ctrl+C` / `Ctrl+V` sao chép · `Ctrl+Z` / `Ctrl+Y` hoàn tác / làm lại.
            """),
    ];
}
