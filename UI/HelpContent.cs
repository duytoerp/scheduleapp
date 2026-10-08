namespace ScheduleApp.UI;

/// <summary>Nút "làm ngay" trong một chủ đề hướng dẫn — <see cref="Command"/> do màn hình chính xử lý (xem <see cref="IHelpHost"/>).</summary>
internal sealed record HelpAction(string Text, string Command);

/// <summary>
/// Một chủ đề hướng dẫn. <see cref="Body"/> dùng cú pháp gọn: <c>## tiêu đề</c>, <c>1. bước</c>, <c>- ý</c>,
/// <c>&gt; mẹo</c>, <c>! lưu ý</c>, khối <c>```</c> mã, trong dòng có <c>**đậm**</c> và <c>`mã`</c>. Mỗi dòng là một đoạn.
/// </summary>
internal sealed record HelpTopic(string Id, string Group, string Title, string Summary, string Body, params HelpAction[] Actions)
{
    // Tên mục trong danh sách (trình đọc màn hình / UI Automation đọc ToString của mục).
    public override string ToString() => Title;
}

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
            > Khi flow chạy, khung trạng thái ở góc phải dưới màn hình cho biết flow đang ở bước nào, có nút **⏸ Tạm dừng** và **■ Dừng**.
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
            "Hộp công cụ, sơ đồ flow kiểu n8n, thêm / di chuyển bước, chạy thử và gỡ lỗi.",
            """
            Trình soạn công việc có 3 vùng: **Hộp công cụ** có ô tìm kiếm (trái), **sơ đồ flow** (giữa), nút lệnh (phải). Bấm **⤢ Mở rộng sơ đồ** (`F11`) để ẩn các tab phía trên.
            ## Đọc sơ đồ
            - Flow đi từ trái sang phải: **Bắt đầu** → các bước (tên, mô tả dưới nút) → nút **+** cuối flow.
            - **Nếu**: dây **đúng** đi lên, dây **sai** đi xuống, hai nhánh gộp lại ở nút tròn *Hết Nếu*.
            - **Lặp**: dây **lặp** xuống thân vòng lặp (hàng dưới) rồi quay về nút Lặp; dây **xong** đi tiếp sang bước sau.
            - Dây nét đứt phía trên: *Nhảy tới nhãn* (tím), *khi lỗi nhảy tới nhãn* (đỏ).
            - Vị trí nút tự tính theo thứ tự bước — thứ tự chạy vẫn như danh sách bước.
            ## Ba cách nhìn: Sơ đồ / Danh sách / Cây
            - Nút **◇ Sơ đồ** / **☰ Danh sách** / **├ Cây** ở đầu khung flow; lần mở sau nhớ cách nhìn đã chọn. Cả ba là cùng một flow: cùng bước đang chọn, menu chuột phải, phím tắt.
            - **Danh sách**: mỗi bước một dòng với số thứ tự và mô tả đầy đủ, bước trong Nếu / Lặp thụt vào — đọc nhanh flow dài.
            - **Cây**: như cây thư mục, có đường nối nhánh. *Nếu* tách hai nhánh: các bước khi đúng, và nút con *Không thì (nhánh sai)* chứa các bước khi sai. *Lặp* chứa các bước con. Không có dòng *Hết Nếu* / *Hết lặp* — nhìn nhánh là biết bước thuộc khối nào.
            - Bấm `▾` / `▸` (ở cây: `⊟` / `⊞`), hoặc `←` / `→`, để thu gọn / mở khối Nếu, Lặp (ở cây thu được cả nhánh *Không thì*); khối thu gọn ghi số bước bên trong. Bước đang chạy / bị lỗi nằm trong khối thu gọn thì khối tự mở.
            - Ở danh sách và cây: thêm bước bằng `Tab` hoặc kéo thao tác từ hộp công cụ thả vào giữa hai dòng (thả xuống khoảng trống dưới cùng = cuối flow); sửa bằng nhấp đúp / `Enter`; di chuyển bằng `Ctrl+↑` / `Ctrl+↓`.
            ## Thêm và sửa bước
            1. Di chuột lên một dây → bấm **+** → gõ tìm thao tác (không dấu cũng được) → chọn. Hoặc kéo thao tác từ hộp công cụ thả lên dây (dây sẽ chèn tô xanh). Chọn một bước rồi nhấn `Tab` để chèn ngay sau bước đó.
            2. Điền thông tin trong form soạn bước → **OK**. Bấm **▶ Thử bước này** để chạy riêng bước đó ngay.
            3. Nhấp đúp nút (hoặc `Enter`) để sửa lại. Kéo nút thả lên dây khác để di chuyển — kéo *Nếu* / *Lặp* là di chuyển cả khối.
            - Thêm *Nếu* / *Lặp* sẽ tự chèn *Hết Nếu* / *Hết lặp*. Thêm vào dây "sai" khi chưa có nhánh sai sẽ tự tạo *Không thì*.
            - Di chuột lên nút: thanh công cụ ▶ chạy từ đây · ⏻ bật/tắt · 🗑 xóa · ⋯ menu.
            - Nút viền đỏ là lỗi cấu trúc (thiếu *Hết Nếu*, nhãn không tồn tại…). Di chuột lên nút để xem chi tiết.
            - Tùy chọn ít dùng (thử lại khi lỗi, xử lý lỗi, nghỉ sau bước, bật/tắt, điểm dừng) nằm trong mục **▸ Nâng cao** của form soạn bước.
            ## Chạy thử và gỡ lỗi
            - **▶ Chạy thử flow** (`F5`): nút đang chạy viền xanh dương có vòng quay, nút chạy xong có dấu ✓ xanh lá, bước lỗi viền đỏ.
            - **⤵ Chạy từ bước chọn**: bỏ qua các bước phía trên.
            - **Điểm dừng** (`F9`) và **⏭ Chạy từng bước**: flow dừng trước bước, hiện giá trị mọi biến. `F10` bước tiếp, `F5` chạy tiếp, `Shift+F5` dừng.
            - **Khung trạng thái** ở góc phải dưới màn hình (khi flow chạy, kể cả theo lịch): đang ở bước mấy / tổng số bước, chạy được bao lâu, dòng nhật ký mới nhất. **⏸ Tạm dừng** dừng trước bước kế tiếp rồi **⏭ Bước tiếp** từng bước một hoặc **▶ Chạy tiếp**; **■ Dừng** dừng ngay. Khung tự dời sang góc khác khi flow cần click vào chỗ nó che.
            - **Bật / tắt khung trạng thái:** nút **— Ẩn** trên khung ẩn cho lần chạy này (flow vẫn chạy tiếp; cả các kịch bản / dòng dữ liệu / lần chạy lại của bộ kiểm thử và flow đang chờ); chuột phải biểu tượng ScheduleApp ở khay → **Hiện khung trạng thái khi flow chạy** (hoặc *Cài đặt → Chung*) cho mọi công việc; riêng từng công việc ở tab *Lỗi · thông báo* → **Khung trạng thái góc phải khi chạy**: Theo cài đặt chung / Luôn hiện / Không hiện (vd flow trình chiếu, phát video — cũng áp dụng khi công việc này được gọi bằng *Chạy công việc*).
            - **Phiên bản cũ…**: mỗi lần lưu, bản trước được giữ lại (30 bản) để khôi phục.
            ## Ghi thao tác (macro)
            Bấm **● Ghi thao tác…**, thao tác bình thường trên ứng dụng khác rồi **Dừng & lưu** (`Ctrl+Shift+Q`). Các bước được chèn sau bước đang chọn.
            - Click vào nút / ô có tên → *Click phần tử UI*; còn lại → *Click chuột*.
            - Mỗi click được chụp **hình mẫu** quanh chỗ bấm (ảnh thu nhỏ hiện bên phải thẻ bước). Khi chạy, chỗ đó được tìm lại theo hình ảnh nên cửa sổ dời chỗ, đổi kích thước hay bố cục xê dịch vẫn click đúng; không thấy sau 5 giây mới click theo tọa độ lúc ghi.
            - Nút giống hệt nhau lặp lại (vd *Sửa* ở mỗi dòng): hình mẫu lấy rộng ra cả phần bên cạnh để phân biệt; vẫn còn chỗ giống thì chọn chỗ gần vị trí lúc ghi nhất.
            - Bấm vào vùng trống, ít chi tiết → giữ tọa độ như cũ.
            - Mở bước để xem hình mẫu (dấu chữ thập đỏ là điểm sẽ click), **✂ Chụp hình mẫu** lại, **✕ Bỏ hình mẫu**, chỉnh *Độ khớp* hoặc bấm **Thử tìm trên màn hình**.
            - **Ảnh lúc ghi**: mỗi click / kéo thả còn lưu ảnh **cả cửa sổ ứng dụng** lúc nhấn chuột, vòng đỏ là chỗ đã click — mở bước để xem, bấm vào ảnh (hoặc **Xem lớn…**) để xem to, **Bỏ ảnh** để xóa khỏi bước. Ảnh chỉ để xem lại, không ảnh hưởng cách chạy; không gửi cho AI, không đi kèm khi xuất công việc ra file. Tắt bằng ô *Lưu ảnh cả cửa sổ lúc click* trên thanh "Đang ghi".
            ## Phím tắt và chuột trên sơ đồ
            - Kéo nền (hoặc chuột giữa) để cuộn · lăn chuột cuộn dọc, `Shift` + lăn cuộn ngang · bản đồ thu nhỏ góc phải dưới
            - `Ctrl` + lăn chuột thu phóng · `1` vừa khung · `0` về 100% · nút ⊕ ⊖ góc trái dưới
            - `←` `→` chọn bước · `Ctrl+←` `Ctrl+→` di chuyển · `Tab` thêm bước sau bước chọn · `Space` bật/tắt · `F9` điểm dừng · `Delete` xóa
            - `Ctrl+C` / `Ctrl+V` sao chép bước (cả sang công việc khác) · `Ctrl+D` nhân bản · `Ctrl+Z` / `Ctrl+Y` hoàn tác / làm lại
            """,
            new HelpAction("＋ Thêm công việc", CmdNewJob)),

        new("conditions", "Bắt đầu", "Điều kiện (Nếu) & vòng lặp",
            "Cho flow tự quyết định: chỉ làm khi đúng điều kiện, làm khác khi sai, lặp lại nhiều lần.",
            """
            Bước **Nếu (điều kiện)** chia flow làm hai dây: **đúng** (đi lên) chạy khi điều kiện đúng, **sai** (đi xuống) chạy khi điều kiện sai. Hai dây gặp lại nhau ở nút tròn *Hết Nếu* rồi flow chạy tiếp.
            ## Đặt điều kiện "So sánh giá trị / biến"
            1. Thêm bước **Nếu (điều kiện)**, chọn *Điều kiện*: **So sánh giá trị / biến**.
            2. Ô **Giá trị**: thứ cần kiểm tra — thường là một biến trong ngoặc nhọn kép, vd `{{now:HH}}` (giờ hiện tại).
            3. **Phép so sánh**: bằng, khác, chứa, >, <, rỗng…
            4. Ô **So với**: giá trị mong muốn, vd `12`.
            5. Bấm **▶ Thử bước này** để xem ngay điều kiện đang ĐÚNG hay SAI → **OK**.
            6. Trên sơ đồ, bấm **+** trên dây *đúng* để thêm việc làm khi đúng; bấm **+** trên dây *sai* để thêm việc làm khi sai (bước *Không thì* tự được tạo).
            ## Ví dụ đời thường
            - Chỉ nhắc nộp báo cáo vào thứ Sáu: Giá trị `{{today:dddd}}` · **bằng** · So với `Thứ Sáu`.
            - Buổi sáng mới chạy: Giá trị `{{now:HH}}` · **<** · So với `12`.
            - Có file thì mới gửi: chọn điều kiện **File / thư mục tồn tại**, nhập đường dẫn file.
            - Bước trước lỗi thì báo: chọn điều kiện **Bước trước bị lỗi**, nhánh *đúng* thêm bước **Thông báo**.
            > Các biến hay dùng được gợi ý ngay dưới ô chữ trong form soạn bước; xem thêm chủ đề **Biến & bí mật**.
            ## Lặp
            - **Lặp N lần**: làm lại các bước bên trong đúng N lần; `{{loop.index}}` là lần thứ mấy (1, 2, 3…).
            - **Mỗi dòng của file Excel / CSV**: mỗi lần lặp là một dòng; dùng `{{row.TênCột}}`, vd `{{row.Email}}`.
            - **Mỗi file trong thư mục**, **Mỗi dòng văn bản**, **Lặp khi điều kiện đúng** (điều kiện đặt giống bước Nếu).
            - Trên sơ đồ, các bước trong vòng lặp nằm ở hàng dưới nút **Lặp**; dây quay về cho biết chúng được làm lại.
            - **Thoát vòng lặp** dừng lặp ngay; **Bỏ qua, sang lần lặp kế** bỏ phần còn lại của lần này.
            ! Lặp nhiều lần với bước click/gõ phím: thêm bước **Chờ** ngắn để ứng dụng kịp phản hồi.
            """,
            new HelpAction("＋ Thêm công việc", CmdNewJob)),

        new("variables", "Bắt đầu", "Biến & bí mật",
            "Dùng {{tên}} trong mọi ô chữ: ngày giờ, dữ liệu Excel, kết quả API, mật khẩu mã hóa.",
            """
            Mọi ô chữ của bước dùng được `{{tên}}`. Giá trị được thay vào lúc chạy.
            ## Biến có sẵn
            - Ngày giờ: `{{today}}`, `{{now}}`, `{{today-1}}`, `{{now+30m}}`, định dạng `{{today:dd/MM/yyyy}}`.
            - `{{clipboard}}`, `{{env:USERNAME}}`, `{{random:1-100}}`, `{{guid}}`.
            - Kết quả gần nhất: `{{lastOutput}}` (lệnh cmd), `{{lastError}}` (bước lỗi), `{{lastWindow}}` (cửa sổ vừa thu nhỏ; rỗng nếu lúc đó không có cửa sổ ứng dụng nào), `{{http.status}}`, `{{http.body}}` (gọi API).
            - Trong vòng lặp: `{{loop.index}}`, `{{row.TênCột}}` (mỗi dòng Excel/CSV), `{{item}}`.
            - Dynamics 365: `{{d365.lastId}}` (Id bản ghi vừa lưu/mở), `{{d365.created}}` (các bản ghi do flow tạo).
            ## Tự tạo biến
            1. Khai báo giá trị ban đầu ở tab **Biến** của công việc.
            2. Gán / đổi trong flow bằng bước **Gán biến** (giá trị, phép tính, clipboard, output lệnh, trích JSON, hỏi người dùng…).
            ## Bí mật
            Mật khẩu, token lưu trong **Bí mật** (mã hóa bằng tài khoản Windows của bạn), dùng bằng `{{secret:Tên}}`. Giá trị không hiện lại, được che `***` trong nhật ký và không bị xuất ra file khi chia sẻ công việc.
            ## Dữ liệu test ngẫu nhiên
            Giá trị biến viết như công thức Excel, bắt đầu bằng `=`, được **sinh mới ở mỗi lần chạy** (giá trị đã dùng ghi trong nhật ký để tái hiện lần chạy lỗi). Bấm **⚄ Giá trị ngẫu nhiên** ở tab Biến, màn hình Thiết lập mẫu hoặc Môi trường để chọn nhanh:
            - `=hoten()` · `=hoten(nữ)` · `=ho()` · `=ten()` — họ tên người Việt.
            - `=email()` · `=email(cty.vn)` · `=sdt()` (di động 10 số) · `=diachi()` · `=thanhpho()` · `=congty()` · `=cccd()`.
            - `=random(1, 100)` · `=random(1, 100, 2)` (2 số lẻ) · `=chuso(6)` · `=chuoi(8)` · `=guid()`.
            - `=chon(Mới; Đang xử lý; Đã đóng)` — chọn một giá trị · `=ngay(-30, 0)` · `=ngay(1, 90, yyyy-MM-dd)` — ngày so với hôm nay.
            Dùng thẳng trong ô chữ: `{{=hoten()}}` (mỗi lần thay ra một giá trị khác — muốn dùng lại cùng giá trị cho bước Kiểm tra thì đặt vào biến).
            ## Thiết lập mẫu
            Thêm từ **Mẫu có sẵn** thì màn hình *Thiết lập mẫu* hỏi một lần các giá trị của riêng bạn: biến (`tenant`, `d365Url`, tài khoản test…), bí mật, kết nối API và đường dẫn file / thư mục — của mẫu và các công việc dùng chung nó gọi tới. Giá trị còn là chữ mẫu được đánh dấu ⚠; giá trị đã điền được nhớ để tự điền cho mẫu sau.
            Sửa lại bất cứ lúc nào: chuột phải công việc → **⚙ Thiết lập biến & bí mật…**, hoặc **⋯ Thêm → Thiết lập biến & bí mật (mọi công việc)…** để đổi một giá trị cho tất cả công việc cùng lúc.
            > Định dạng giá trị: `{{ten:upper}}`, `{{ten:trim}}`, `{{duongDan:unquote}}` (bỏ dấu nháy bao quanh), `{{ten:nodiacritics}}` (bỏ dấu), `{{tien:N0}}` (1.234.568), `{{ten:json}}` (đặt vào chuỗi JSON an toàn).
            """,
            new HelpAction("Mở Bí mật", CmdSecrets)),

        new("errors", "Bắt đầu", "Xử lý lỗi, lịch sử & thông báo",
            "Thử lại khi lỗi, nhảy tới nhãn, ảnh chụp lỗi, gửi thông báo Telegram / email / Teams.",
            """
            ## Khi một bước lỗi
            Trong mục **▸ Nâng cao** của form soạn bước: *thử lại N lần cách X ms*, rồi *dừng flow* / *bỏ qua chạy tiếp* / *nhảy tới nhãn* / theo cài đặt của công việc.
            ## Tab Lỗi · thông báo của công việc
            - Dừng flow khi một bước lỗi (mặc định).
            - **Công việc chạy khi thất bại**: dọn dẹp, gửi báo cáo, có `{{failed.message}}`.
            - Khi nào gửi thông báo: chỉ khi lỗi, mỗi lần, hoặc không.
            ## Xem lại
            - **Lịch sử chạy**: mọi lần chạy, kết quả, bước lỗi, ảnh chụp màn hình lúc lỗi, thống kê theo ngày.
            - Kênh thông báo (Telegram, email SMTP, webhook Teams/Slack) khai báo trong **Cài đặt → Thông báo**, có nút *Gửi thử*.
            ## Điều khiển & tạo công việc qua Telegram
            Bật **Nhận lệnh điều khiển** trong **Cài đặt → Thông báo**, rồi nhắn cho bot trong chat riêng (chỉ đúng chat id đã cấu hình, không nhận trong nhóm). Gõ `/` trong Telegram để hiện menu lệnh.
            - `/list` (bấm **▶** cạnh công việc để chạy) · `/run 3` · `/stop` · `/status` · `/history` · `/screenshot`
            - Chạy bằng `/run` / nút ▶: chạy xong bot **luôn báo kết quả** về chat, kèm nút 🔁 Chạy lại · 📷 Màn hình (khi lỗi) · 📜 Lịch sử.
            - `/status` cho biết đang ở bước mấy / tổng số bước, kèm nút ⏸ Tạm dừng / ■ Dừng. `/pause` tạm dừng trước bước kế tiếp, `/tiep` chạy tiếp, `/buoc` chạy một bước.
            - Mạng chập chờn / Telegram giới hạn tốc độ: tin tự gửi lại, không mất thông báo.
            - `/new <mô tả>`: AI dựng công việc mới, nói giờ chạy thì đặt lịch luôn (vd `/new 8h sáng các ngày làm việc mở D:\bao-cao.xlsx, lưu rồi báo tôi`). Bot gửi bản nháp để xem trước.
            - Nhắn thêm để sửa bản nháp → `/ok` lưu (nhóm *Telegram*) · `/ok chay` lưu và chạy ngay · `/huy` bỏ — hoặc bấm nút **✔ Lưu · ▶ Lưu và chạy · ✖ Bỏ** dưới bản nháp (nút ở bản nháp cũ không lưu nhầm bản đã sửa).
            - Công việc lưu bằng `/ok` (và công việc bị sửa qua Telegram) ở trạng thái **chờ duyệt**: chỉ chạy sau khi bạn xem và bấm **✔ Duyệt…** trên máy — `/ok chay` cũng chờ duyệt (xem *An toàn, bảo mật & dữ liệu*).
            > Cần khóa Claude trong **Cài đặt → Tích hợp**.
            """,
            new HelpAction("Lịch sử chạy", CmdHistory), new HelpAction("Cài đặt", CmdSettings)),

        new("d365-overview", "Kiểm thử Dynamics 365", "Tổng quan kiểm thử D365",
            "Kịch bản kiểm thử là gì, quy trình 5 bước từ chuẩn bị tới báo cáo.",
            """
            ScheduleApp kiểm thử được app **Dynamics 365 CE / Power Apps model-driven** mà không cần viết code. Các bước gọi thẳng **Client API (Xrm)** của form trong trình duyệt, nên kịch bản không vỡ khi Microsoft đổi giao diện, và dùng luôn phiên đăng nhập của trình duyệt (MFA, SSO).
            ## Kịch bản kiểm thử
            Một **kịch bản** là một công việc được tick **Đây là kịch bản kiểm thử** (tab *Kiểm thử*). Mỗi lần chạy, ScheduleApp ghi kết quả từng bước, từng điều kiện *Kiểm tra*, chụp ảnh khi lỗi và xuất báo cáo HTML + JUnit XML.
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
            - **Subgrid** (mở dòng, đọc ô, + Mới, làm mới) và **Danh sách (view)** (đọc / tìm và mở bản ghi): xem chủ đề *Subgrid & danh sách (view)*.
            - **Tạo nhanh (quick create)**, mở **form chính** theo tên: xem *Chọn form, nút thanh lệnh, tạo nhanh*.
            - **Đăng nhập Microsoft**, **đọc người dùng & vai trò**: xem *Đăng nhập tự động & phân quyền*.
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
            > Kiểm tra cho subgrid, nút trên thanh lệnh, form đang mở, vai trò người dùng: xem các chủ đề ngay sau.
            """),

        new("d365-grids", "Kiểm thử Dynamics 365", "Subgrid & danh sách (view)",
            "Đếm / tìm dòng, đọc ô, mở bản ghi, tạo bản ghi liên quan từ subgrid; đọc bản ghi theo bộ lọc của view.",
            """
            ## Subgrid trên form
            Ô **Subgrid** là tên control trên form (vd `Contacts`) hoặc nhãn hiển thị. Bấm **Chọn subgrid từ form…** để xem các subgrid của form đang mở. Các bước tự chờ subgrid tải xong dữ liệu.
            - **Subgrid: mở bản ghi của một dòng** · **đọc giá trị một ô**: ô *Dòng* là số thứ tự (1 = dòng đầu) hoặc chữ có trong dòng (tên, email…, không phân biệt dấu). Ô *Cột* là tên logic, trống = cột tên.
            - **Subgrid: tạo bản ghi liên quan mới** như bấm **+ Mới** trên subgrid: form của bảng liên quan mở ra, field của bản ghi cha được điền sẵn theo ánh xạ của quan hệ. Bản ghi đang mở phải được lưu trước.
            - **Subgrid: làm mới** sau khi plugin / Power Automate tạo bản ghi liên quan.
            - Kiểm tra **D365: số dòng của subgrid** (tổng số bản ghi theo view của subgrid) và **D365: subgrid có dòng chứa chữ**.
            ## Danh sách (view)
            **Danh sách (view): đọc các bản ghi** chạy chính FetchXML của view qua Web API (giữ nguyên bộ lọc và sắp xếp của view), nên kiểm tra được *view lọc đúng dữ liệu*:
            - Ô **View**: tên hoặc Id view (view hệ thống hoặc view cá nhân). Trống = view mặc định của bảng.
            - Ô **Tìm theo tên** lọc thêm theo cột tên (chứa chữ), như ô tìm nhanh.
            - Kết quả: `{{view.count}}`, `{{view.ids}}`, `{{view.names}}` (mỗi dòng một bản ghi).
            **Danh sách (view): tìm và mở bản ghi** mở bản ghi đầu tiên tìm được (lỗi nếu không có).
            > Ví dụ: tạo khách hàng qua Web API, rồi kiểm tra nó hiện trong view "Khách hàng đang hoạt động của tôi" — xem mẫu C7.
            ! Đọc view qua Web API không bấm vào lưới trên màn hình: sắp xếp / lọc cột bằng chuột trên lưới chưa được tự động hóa.
            """,
            new HelpAction("Thêm mẫu kiểm thử", CmdTemplates)),

        new("d365-forms", "Kiểm thử Dynamics 365", "Chọn form, nút thanh lệnh, tạo nhanh",
            "Mở đúng form chính, kiểm tra nút hiện / bấm được / bị mờ, tạo nhanh bản ghi có điền sẵn.",
            """
            ## Mở đúng form chính
            Bước **Mở form bản ghi** có ô **Form chính**: tên form (vd `Bán hàng`) hoặc Id. Trống = form mặc định của người dùng. Kiểm tra form đang mở bằng **D365: form đang mở là**.
            ## Kiểm tra nút trên thanh lệnh
            Điều kiện **D365: nút trên thanh lệnh** theo nhãn hoặc một phần command id (`Mscrm.Form.account.Deactivate`), trạng thái:
            - `visible`: nút có trên thanh lệnh hoặc trong menu "…" (được mở ra để tìm rồi đóng lại).
            - `enabled`: hiện và bấm được. `disabled`: hiện nhưng bị mờ.
            - Tick **Đảo ngược** để kiểm tra nút bị ẩn, vd người dùng không có quyền xóa.
            > Dùng command id thay vì nhãn để kịch bản chạy được với mọi ngôn ngữ giao diện.
            ## Tạo nhanh (quick create)
            **Tạo nhanh bản ghi**: ô *Bảng* (vd `contact`), ô giá trị mỗi dòng `field=giá trị` (giá trị gốc: số cho option set, `bảng:guid` cho lookup). ScheduleApp mở form tạo nhanh, bấm **Lưu và đóng**, lấy Id bản ghi mới vào `{{d365.lastId}}` và ghi vào `{{d365.created}}` để dọn.
            ! Bảng phải bật form tạo nhanh. Nếu lưu bị chặn (thiếu field bắt buộc), bước báo lỗi kèm nội dung lỗi trên form tạo nhanh.
            """),

        new("d365-login", "Kiểm thử Dynamics 365", "Đăng nhập tự động & phân quyền",
            "Đăng nhập Microsoft bằng tài khoản test (cả MFA mã TOTP), phát hiện phiên hết hạn, kiểm thử theo vai trò.",
            """
            ## Đăng nhập tự động
            Bước **Đăng nhập Microsoft** đặt ngay sau bước *Mở trình duyệt* (mẫu C1 có sẵn, đang tắt):
            1. Lưu mật khẩu của tài khoản test trong **🔑 Bí mật**, vd tên `MatKhauTest`.
            2. Nếu tài khoản có MFA: trong trang *Thông tin bảo mật* (Security info) của tài khoản Microsoft, thêm phương thức *ứng dụng xác thực* và chọn dùng ứng dụng xác thực khác; ở màn hình mã QR bấm *Can't scan image?* để xem **khóa bí mật** (chuỗi chữ A–Z, số 2–7) rồi lưu vào Bí mật, vd `TotpTest`.
            3. Điền bước: tài khoản (email), mật khẩu `{{secret:MatKhauTest}}`, khóa TOTP `{{secret:TotpTest}}`.
            Bước tự làm: email → mật khẩu → mã 6 số (tự tính như ứng dụng Authenticator) → *Duy trì đăng nhập*. Đã đăng nhập sẵn thì bỏ qua.
            ! Không tự duyệt được thông báo đẩy trên điện thoại (Authenticator push), không làm được trang đăng nhập riêng của tổ chức (ADFS tùy biến). Với các trường hợp này đăng nhập tay một lần trong hồ sơ trình duyệt.
            ## Phiên đăng nhập hết hạn
            Khi trình duyệt bị chuyển về trang đăng nhập, bước D365 báo ngay *"Phiên đăng nhập Dynamics 365 đã hết"* thay vì chờ tới hết giờ.
            ## Kiểm thử theo vai trò
            - **Đọc người dùng & vai trò**: `{{d365.user}}`, `{{d365.roles}}`.
            - Kiểm tra **D365: người dùng có vai trò**: chắc chắn kịch bản đang chạy bằng đúng tài khoản cần thử.
            - Mỗi vai trò dùng một **hồ sơ trình duyệt** + một **tài khoản test** riêng. Đặt tài khoản / hồ sơ vào **môi trường** hoặc biến để chạy cùng kịch bản với nhiều vai trò (vd lặp từng dòng Excel: hồ sơ, tài khoản, vai trò mong đợi).
            - Kiểm tra quyền: field khóa (*D365: trạng thái field* `disabled`), nút ẩn (*D365: nút trên thanh lệnh* + Đảo ngược). Xem mẫu C6.
            ! Chỉ dùng tài khoản test riêng cho kiểm thử, không dùng tài khoản quản trị thật.
            """,
            new HelpAction("Mở Bí mật", CmdSecrets)),

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
            - Tick **Tự xóa dữ liệu Dynamics 365 do flow tạo ra** (tab *Kiểm thử*): luôn dọn sau khi chạy, kể cả khi kiểm thử thất bại.
            - Hoặc đặt bước **Xóa dữ liệu test đã tạo** ở cuối flow. Bản tạo sau được xóa trước.
            > Đặt tên dữ liệu test có dấu hiệu riêng (vd tiền tố `KH test`) để dễ nhận ra và dọn tay nếu cần.
            ! Tài khoản test cần quyền xóa trên các bảng đó, nếu không bước dọn dẹp sẽ báo "Không xóa được".
            """),

        new("data-driven", "Kiểm thử Dynamics 365", "Kiểm thử theo dữ liệu (Excel / CSV)",
            "Một kịch bản chạy với nhiều bộ dữ liệu; mỗi dòng của file là một test case riêng trong báo cáo.",
            """
            ## Cách dùng
            1. Tạo file Excel (.xlsx) hoặc CSV, dòng đầu là tiêu đề cột, vd `Ten`, `DienThoai`, `KetQuaMongDoi`.
            2. Mở kịch bản → tab *Kiểm thử* → ô **Dữ liệu kiểm thử** chọn file (và sheet nếu là Excel).
            3. Trong các bước dùng `{{row.Ten}}`, `{{row.DienThoai}}`… (giống vòng lặp Excel). Có thêm `{{row.rowNumber}}`, `{{data.index}}`, `{{data.count}}`.
            ## Khi chạy
            - Mỗi dòng chạy kịch bản một lần, là **một test case riêng** trong báo cáo và junit.xml, tên kèm dòng, vd *Tạo khách [dòng 3: KH002]*.
            - Một dòng không đạt không làm dừng các dòng khác.
            - **▶ Chạy thử flow** trong trình soạn chạy với **dòng đầu tiên** (giữ tô sáng bước, điểm dừng).
            > Đường dẫn file tương đối được tính từ thư mục kịch bản khi chạy `--test-dir`, nên để file dữ liệu cạnh kịch bản trong git.
            """),

        new("test-run", "Kiểm thử Dynamics 365", "Chạy kiểm thử & đọc báo cáo",
            "Trang Kiểm thử, chạy lại kịch bản lỗi, báo cáo HTML / JUnit và ảnh chụp lúc lỗi.",
            """
            ## Trang Kiểm thử
            - Thẻ số liệu: tổng kịch bản, đạt / không đạt / chưa chạy ở lần chạy cuối.
            - Danh sách kịch bản: tick để chọn nhiều, nhấp đúp để sửa. Cột *Chi tiết* hiện lý do không đạt gần nhất.
            - **▶ Chạy đã chọn**, **▶ Chạy tất cả**, **✖ Chạy lại các kịch bản lỗi**: chạy lần lượt rồi mở một báo cáo chung.
            - Cột **Ổn định (10 lần)**: số lần đạt trong 10 lần chạy gần nhất — kịch bản lúc đạt lúc không (chập chờn) hiện màu vàng.
            - Thanh lọc: chọn **môi trường** chạy, lọc theo **tag**, xuất / nhập **thư mục kịch bản (git)**.
            - Số kịch bản đang không đạt hiện thành huy hiệu đỏ ở mục **Kiểm thử** bên trái.
            ## Báo cáo
            Mỗi lần chạy tạo một thư mục trong `test-reports\` gồm:
            - `index.html`: từng kịch bản, từng bước với thời gian, ĐẠT / KHÔNG ĐẠT, giá trị thực tế và **ảnh chụp tab trình duyệt** lúc lỗi.
            - `junit.xml`: cho Azure DevOps, Jenkins, GitHub Actions.
            Mở báo cáo: nút **📄 Báo cáo** (kịch bản đang chọn), danh sách *Báo cáo gần đây*, hoặc **Lịch sử chạy → Mở báo cáo kiểm thử**.
            > Kịch bản chạy theo lịch cũng tự ghi báo cáo. Đặt lịch chạy đêm cho cả nhóm để sáng ra xem kết quả.
            """,
            new HelpAction("Mở trang Kiểm thử", CmdOpenTests), new HelpAction("Thư mục báo cáo", CmdReports)),

        new("environments", "Kiểm thử Dynamics 365", "Môi trường, tag & chạy lại",
            "Chạy cùng kịch bản trên Dev / Test / UAT; chọn bộ chạy theo tag; chạy lại kịch bản chập chờn.",
            """
            ## Môi trường
            Mỗi **môi trường** (Dev, Test, UAT…) là một bộ biến, vd `d365Url`, `taiKhoanTest`. Khi chạy ở môi trường nào, biến của môi trường đó **ghi đè** biến cùng tên của kịch bản.
            1. Trang **Kiểm thử** → **Môi trường: Quản lý…** → thêm môi trường và biến.
            2. Chọn môi trường trong ô **Môi trường** trên thanh lọc. Mọi lần chạy kịch bản (kể cả theo lịch) dùng môi trường này.
            3. Dòng lệnh: `--env UAT`. Tên môi trường hiện trong báo cáo và junit.xml; trong flow dùng `{{env.name}}`.
            > Để mật khẩu trong 🔑 Bí mật và ghi `{{secret:Tên}}` trong biến môi trường.
            ## Tag
            Kịch bản có ô **Tag** (tab *Kiểm thử*), vd `smoke, regression`. Lọc theo tag trên trang Kiểm thử; dòng lệnh `--tag smoke` chỉ chạy kịch bản có tag đó. Ô **Mã test case** (vd Id trong Azure DevOps Test Plans) được ghi vào báo cáo và junit.xml.
            ## Chạy lại kịch bản không đạt
            Dòng lệnh `--retry 1`: kịch bản không đạt được chạy lại tối đa 1 lần. Đạt ở lần chạy lại thì báo cáo ghi **"chạy lại 1 lần"** kèm lỗi của lần đầu — dấu hiệu kịch bản chập chờn (chờ chưa đủ, dữ liệu dùng chung…) cần sửa.
            """,
            new HelpAction("Mở trang Kiểm thử", CmdOpenTests)),

        new("test-folder", "Kiểm thử Dynamics 365", "Lưu kịch bản trong git",
            "Xuất kịch bản thành file trong repo để review, làm việc nhóm và chạy CI thẳng từ repo.",
            """
            Kịch bản bình thường nằm trong `jobs.json` trên từng máy. Để cả nhóm cùng làm, lưu chúng thành file trong một thư mục của repo git:
            1. Trang **Kiểm thử** → **🗂 Thư mục kịch bản (git) → Xuất kịch bản ra thư mục…** (kịch bản đã tick, hoặc mọi kịch bản đang hiện).
            2. Mỗi công việc thành một file `Nhóm\Tên.json`, kèm các **công việc dùng chung** mà kịch bản gọi tới (vd *Mở app D365*) và `environments.json`.
            3. Commit thư mục lên git, review thay đổi như mã nguồn. Đổi tên / nhóm thì file cũ được xóa, file mới được ghi.
            4. Máy khác: **Nhập / cập nhật từ thư mục…** sau khi `git pull` — công việc cùng Id được thay, công việc mới được thêm.
            ## Chạy CI thẳng từ repo
            ```
            ScheduleApp.exe --test * --test-dir "C:\agent\_work\1\s\tests\d365" --env UAT --report "$(Build.ArtifactStagingDirectory)"
            ```
            Không cần nhập kịch bản vào máy CI trước. Mỗi file giữ Id nên bước *Chạy công việc khác* vẫn trỏ đúng.
            ! Bí mật (`{{secret:…}}`) không nằm trong file. Trên máy CI, thêm các bí mật cùng tên trong 🔑 Bí mật của tài khoản Windows chạy agent.
            """),

        new("ci", "Kiểm thử Dynamics 365", "Chạy từ dòng lệnh / CI",
            "Chạy bộ kiểm thử không mở giao diện, lấy mã thoát và junit.xml cho pipeline.",
            """
            ```
            ScheduleApp.exe --test "Kiểm thử CRM" --report "D:\TestResults"
            ```
            - Tham số `--test`: tên nhóm, tên công việc, hoặc `*` (mọi kịch bản).
            - Mã thoát: `0` = mọi kịch bản đạt, `1` = có kịch bản không đạt, `2` = không tìm thấy kịch bản / tham số sai.
            - Chạy độc lập, không cần mở giao diện, chạy được song song với ScheduleApp đang mở ở khay.
            ## Tham số thêm
            - `--tag smoke` chỉ chạy kịch bản có tag · `--env UAT` chạy ở môi trường · `--test-dir "thư mục"` đọc kịch bản từ thư mục trong repo.
            - `--retry 1` chạy lại kịch bản không đạt · `--headless` mở trình duyệt ẩn (máy CI không cần hiện cửa sổ).
            - `--shard 1/3` chạy phần 1 trong 3 phần — 3 máy (agent) chạy song song `1/3`, `2/3`, `3/3`, mỗi máy một báo cáo.
            - `--list` chỉ liệt kê các kịch bản được chọn rồi thoát (kiểm tra bộ lọc trước khi chạy).
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
            ## "Phiên đăng nhập Dynamics 365 đã hết"
            Trình duyệt bị chuyển về trang đăng nhập Microsoft (phiên hết hạn, chính sách bảo mật đòi đăng nhập lại). Thêm bước **Đăng nhập Microsoft** sau bước mở trình duyệt, hoặc đăng nhập tay lại trong hồ sơ trình duyệt đó.
            ## Bước chạy nhầm tab (tab chào của tiện ích…)
            Tiện ích trình duyệt có thể tự mở thêm tab. Bước D365 tự ưu tiên tab Dynamics 365; với bước *Trình duyệt* hãy điền ô **Tab** (một phần URL), vd `dynamics.com`.
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

        new("security", "Khác", "An toàn, bảo mật & dữ liệu",
            "Duyệt công việc từ xa, che bí mật, dữ liệu từ ngoài vào lệnh, sao lưu dữ liệu, giới hạn thời gian chạy, cập nhật an toàn.",
            """
            ## Công việc từ nơi khác phải duyệt trước khi chạy
            - Công việc tạo / sửa qua **Telegram**, **nhập từ file** `.json` hoặc từ **thư mục kịch bản** ở trạng thái *chờ duyệt*: không chạy theo bất kỳ cách nào (lịch, kích hoạt, phím tắt, Telegram `/run`, shortcut, được công việc khác gọi, **▶ Thử bước này**) cho tới khi bạn duyệt trên máy. Màn hình duyệt hiện đầy đủ (không cắt chữ) các bước ⚠: chạy lệnh, gán biến, gọi công việc khác, truy vấn / xóa dữ liệu D365, bước dùng `{{secret:…}}`.
            - Chuột phải công việc → **✔ Duyệt…** (hoặc nút **✔ Duyệt…** trong trình soạn): xem lịch, kích hoạt, biến và từng bước. Bước cần xem kỹ có dấu ⚠; bước gọi công việc khác ghi **tên thật** của công việc sẽ chạy.
            - Trong lúc xem công việc chờ duyệt, ScheduleApp không mở đường dẫn mạng (`\\máy\thư mục`) có trong đó — mở là Windows tự gửi thông tin đăng nhập tới máy đó.
            - Nhập thư mục kịch bản: thay đổi **biến môi trường** được hỏi riêng (liệt kê từng biến, mặc định *Không*), vì biến môi trường ghi đè biến của mọi kịch bản chạy với môi trường đó.
            - Bot Telegram chỉ nhận lệnh trong **chat riêng** với đúng chat id ở **Cài đặt → Thông báo**, không nhận trong nhóm.
            ## Bí mật
            - Mật khẩu, token, khóa API, URL webhook, header của kết nối API lưu **mã hóa** (chỉ tài khoản Windows này giải mã được).
            - Giá trị bí mật được thay bằng `***` trong nhật ký, lịch sử, kết quả, báo cáo kiểm thử, thông báo và khi gửi flow cho AI. Biến tên kiểu `matKhau`, `password`, `token` được che ngay lúc gán (vd token lấy từ API đăng nhập).
            - Xác thực của kết nối API chỉ gửi tới đúng máy chủ (và cổng) của kết nối, không qua `http://` (trừ đăng nhập Windows tới máy trong mạng nội bộ); API chuyển hướng sang máy khác thì bỏ xác thực và không gửi lại nội dung.
            - Email SMTP bắt buộc SSL/TLS. Máy chủ chuyển tiếp nội bộ cũ (cổng 25): tick **Cho phép gửi không mã hóa** ở **Cài đặt → Thông báo**. Webhook bắt buộc `https://`.
            - Ảnh chụp màn hình lúc lỗi mặc định **không** gửi kèm Telegram / email (ảnh cả màn hình có thể lộ thông tin khác) — bật lại ở **Cài đặt → Thông báo** nếu cần.
            ## Dữ liệu từ ngoài đưa vào lệnh
            - Bước **Chạy lệnh**: đưa dữ liệu không tin cậy (tên file, nội dung email, ô Excel…) vào lệnh bằng `{{biến:cmd}}` — tự bọc dấu nháy, không thể thoát ra thành lệnh khác. Vd `move {{tep:cmd}} D:\luu`.
            - Kích hoạt **Có email mới**: nhập người gửi (`ketoan@congty.vn` hoặc `@congty.vn`) và tick **Chỉ nhận email đã xác thực** — tên hiển thị thì ai cũng đặt được. File đính kèm lưu xuống được đánh dấu "tải từ Internet" để Windows / Office cảnh báo khi mở. Tiêu đề / nội dung email đưa vào flow nguyên chữ: người gửi viết `{{secret:…}}` cũng không đọc được bí mật của bạn.
            - Ghi CSV: giá trị bắt đầu bằng `=` `+` `-` `@` được thêm dấu `'` để Excel không chạy nó như công thức.
            ## Chạy ổn định, không mất dữ liệu
            - **Dừng nếu chạy quá … phút** (tab *Lỗi · thông báo* của công việc): flow bị treo được dừng, không chặn các công việc khác trong hàng đợi.
            - Công việc, cài đặt, bí mật, lịch sử chạy được ghi an toàn: bản trước giữ ở file `.bak`; file hỏng được giữ nguyên thành `.broken-…` và ScheduleApp tự dùng bản `.bak`.
            - Lỗi bất ngờ được ghi ra `logs\crash-….txt` (đã che bí mật) — gửi file này khi cần hỗ trợ. Mỗi file nhật ký tối đa 20 MB, tự xóa sau 30 ngày.
            ## Cập nhật
            - Bản mới chỉ tải qua `https://`, phải khớp mã SHA-256, đúng số phiên bản và mới hơn bản đang dùng; bản đang dùng có chữ ký số thì bản mới phải cùng người ký.
            - Bản mới không khởi động được → tự quay về bản đang dùng (cả dữ liệu) và bỏ qua bản lỗi đó.
            ! Không duyệt công việc mà bạn không rõ nguồn gốc — chỉ một bước *Gõ phím* cũng có thể mở hộp Run và chạy lệnh bất kỳ.
            """,
            new HelpAction("Cài đặt", CmdSettings)),

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
