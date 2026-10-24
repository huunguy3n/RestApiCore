using System.Text;
using Business;
using PortalApp.Core;
using PortalApp.Ui;

namespace PortalApp.Pages
{
    /// <summary>Hiệu suất theo form và log request gần đây của WebApi trên máy này (restApiLogReq / restApiLogAgg). Body đã che khóa bí mật</summary>
    public sealed class LogsPage : PageBase
    {
        private readonly ComboBox days = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        private readonly DataGridView performance = Grid(200);
        private readonly DataGridView requests = Grid(300);
        private readonly TextBox filterUser = Input(), filterForm = Input();
        private readonly TextBox detail = Input(multiline: true, height: 220);
        private readonly AppButton reload = new("Làm mới", Theme.Icon.Refresh, ButtonStyle.Primary);
        private IReadOnlyList<LogRequestSummary> rows = [];

        public LogsPage(LocalProject project, ILogSink log)
            : base(project, log, Theme.Icon.Doc, "Log & hiệu suất", "Đọc log WebApi trên DB dự án. Bấm đúp một request để xem chi tiết (body đã che password/token/privateKey…).")
        {
            days.Items.AddRange(["1 ngày", "7 ngày", "30 ngày"]);
            days.SelectedIndex = 0;
            TableLayoutPanel perf = AddSection("Hiệu suất theo form");
            AddRow(perf, "Khoảng", days);
            AddWide(perf, performance);

            TableLayoutPanel list = AddSection("Request 24 giờ gần nhất");
            AddRow(list, "User", filterUser);
            AddRow(list, "Form", filterForm);
            AddWide(list, Buttons(reload));
            AddWide(list, requests);
            AddWide(list, detail);
            detail.ReadOnly = true;

            reload.Click += async (_, _) => await RunAsync(reload, RefreshAsync);
            days.SelectedIndexChanged += async (_, _) => await RunAsync(reload, RefreshAsync);
            requests.CellDoubleClick += async (_, e) => await RunAsync(reload, () => ShowDetailAsync(e.RowIndex));
        }

        public override async Task RefreshAsync()
        {
            int span = days.SelectedIndex switch { 1 => 7, 2 => 30, _ => 1 };
            DateTime now = DateTime.Now;
            LogViewRepositoryAdapter logs = new(Project);
            IReadOnlyList<FormPerformance> perf = await logs.Repository.PerformanceAsync(now.AddDays(-span), now.AddMinutes(1));
            performance.DataSource = perf.Select(f => new
            {
                Form = f.Form.Length == 0 ? "(hệ thống)" : f.Form, Request = f.Requests, Loi = f.Errors, TB_ms = Math.Round(f.AverageMs), Max_ms = f.MaxMs,
                Duoi100 = f.Lt100, Tu100_500 = f.Lt500, Tu500_2000 = f.Lt2000, Tren2s = f.Ge2000
            }).ToList();
            LogPage page = await logs.Repository.ListAsync(new LogFilter(now.AddDays(-1), now.AddMinutes(1), LogRedaction.Clean(filterUser.Text, 32), LogRedaction.Clean(filterForm.Text, 32), null, null), 1, 200);
            rows = page.Items;
            requests.DataSource = rows.Select(r => new
            {
                ThoiGian = r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), User = r.UserName, IP = r.IpAddress, Request = $"{r.Method} {r.Path}", Form = r.Form,
                HTTP = r.StatusCode, Ma = r.ResultCode, ms = r.DurationMs, ChuKy = r.SignatureResult
            }).ToList();
        }

        private async Task ShowDetailAsync(int index)
        {
            if (index < 0 || index >= rows.Count)
            {
                return;
            }
            LogRequestDetail? item = await new LogViewRepositoryAdapter(Project).Repository.GetAsync(rows[index].ReqId, LogRedaction.MaxBodyBytes);
            if (item == null)
            {
                detail.Text = "Không còn log này (đã dọn).";
                return;
            }
            await Project.AuditAsync("logs.body.read", item.Summary.ReqId.ToString(), "SUCCESS");
            StringBuilder text = new();
            text.AppendLine($"{item.Summary.Method} {item.Summary.Path}{item.QueryString} → HTTP {item.Summary.StatusCode} {item.Summary.ResultCode} ({item.Summary.DurationMs} ms)");
            text.AppendLine("--- request ---");
            text.AppendLine(LogRedaction.Redact(item.RequestBody) ?? "(không lưu)");
            text.AppendLine("--- response ---");
            text.AppendLine(LogRedaction.Redact(item.ResponseBody) ?? "(không lưu)");
            detail.Text = text.ToString().Replace("\n", Environment.NewLine);
        }

        private sealed class LogViewRepositoryAdapter(LocalProject project)
        {
            public DataAccess.LogViewRepository Repository { get; } = project.Logs();
        }
    }
}
