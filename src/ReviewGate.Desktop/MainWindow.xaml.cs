using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ReviewGate.App;
using ReviewGate.Contracts;
using ReviewGate.Transport.Http;

namespace ReviewGate.Desktop;

public record Choice(string Label, ModelOption? Model);

// The desktop shell only collects input and shows results; every action goes through DesktopService, which uses
// the same planner, ledger, grant record and engine as the CLI. Approval is the confirmation dialog shown before a send.
public partial class MainWindow : Window
{
    readonly DesktopService service = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReviewGate"),
        Path.Combine(AppContext.BaseDirectory, "profiles"), new WindowsCredentialStore());
    QuickPreview? preview;
    CancellationTokenSource? running;

    public MainWindow() => InitializeComponent();

    void OnLoaded(object sender, RoutedEventArgs e) => Guard(() =>
    {
        SourceText.Text = DesktopService.SampleCode;
        RefreshKeys();RefreshModels();RefreshRuns();
    });

    // ---- API keys
    void RefreshKeys()
    {
        var status = service.CredentialStatus();
        foreach (var (provider, label) in new[] { ("openai", OpenAiStatus), ("together", TogetherStatus), ("google", GoogleStatus) })
        {
            label.Text = status[provider] ? "저장됨" : "없음";
            label.Foreground = status[provider] ? Brushes.DarkGreen : Brushes.Gray;
        }
    }
    PasswordBox KeyBox(string provider) => provider switch { "openai" => OpenAiKey, "together" => TogetherKey, _ => GoogleKey };
    void OnSaveKey(object sender, RoutedEventArgs e) => Guard(() =>
    {
        string provider = (string)((Button)sender).Tag;var box = KeyBox(provider);
        if (string.IsNullOrWhiteSpace(box.Password)) { Say("키를 입력하세요."); return; }
        service.SaveKey(provider, box.Password);box.Clear();RefreshKeys();Status($"{provider} 키를 저장했습니다.");
    });
    void OnDeleteKey(object sender, RoutedEventArgs e) => Guard(() =>
    {
        string provider = (string)((Button)sender).Tag;
        if (MessageBox.Show(this, $"{provider} 키를 삭제할까요?", "키 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Status(service.DeleteKey(provider) ? $"{provider} 키를 삭제했습니다." : $"{provider} 키가 없습니다.");RefreshKeys();
    });
    async void OnConnectionTest(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        string provider = (string)((Button)sender).Tag;
        string modelId = service.LoadSettings().DefaultModels[provider];
        var p = service.PrepareQuickTest(new([modelId], "code", "Find correctness defects and quote the exact source line.", DesktopService.SampleCode, null, true));
        if (Confirm(p) is not decimal maxUsd) return;
        Status($"{provider} 연결 테스트 중…");
        var result = await service.RunAsync(p.PlanId, service.Approve(p.PlanId, maxUsd));
        RefreshRuns();
        Say($"{provider} · {modelId}\n\n{Summary(result)}" + (result.Findings.Count > 0 ? $"\n\n첫 지적: {result.Findings[0].Claim} ({result.Findings[0].Evidence})" : ""), "연결 테스트 결과");
    });

    // ---- Models
    void RefreshModels()
    {
        var settings = service.LoadSettings();var models = service.Models();
        GoogleFreeTier.IsChecked = settings.GoogleFreeTier;ModelsGrid.ItemsSource = models;
        foreach (var (provider, box) in new[] { ("openai", DefaultOpenAi), ("together", DefaultTogether), ("google", DefaultGoogle) })
        {
            var options = models.Where(m => m.Provider == provider).ToList();
            box.ItemsSource = options;box.SelectedItem = options.FirstOrDefault(m => m.Id == settings.DefaultModels[provider]) ?? options.FirstOrDefault();
        }
        var choices = models.Where(m => m.Priced).Select(m => new Choice($"{m.Display} — {m.PriceText}", m)).ToList();
        ModelA.ItemsSource = choices;ModelA.SelectedItem = choices.FirstOrDefault(c => c.Model!.Id == settings.DefaultModels["google"]) ?? choices.FirstOrDefault();
        var second = new List<Choice> { new("(없음 — 단일 검수)", null) };second.AddRange(choices);
        ModelB.ItemsSource = second;ModelB.SelectedIndex = 0;
    }
    void OnSaveSettings(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var defaults = new Dictionary<string, string>();
        foreach (var (provider, box) in new[] { ("openai", DefaultOpenAi), ("together", DefaultTogether), ("google", DefaultGoogle) })
            if (box.SelectedItem is ModelOption m) defaults[provider] = m.Id;
        service.SaveSettings(service.LoadSettings() with { GoogleFreeTier = GoogleFreeTier.IsChecked == true, DefaultModels = defaults });
        RefreshModels();Status("설정을 저장했습니다.");
    });
    void OnAddCustomModel(object sender, RoutedEventArgs e) => Guard(() =>
    {
        string provider = (string)((ComboBoxItem)CustomProvider.SelectedItem).Content;bool free = CustomFree.IsChecked == true;
        decimal input = 0, output = 0;
        if (!free && !(decimal.TryParse(CustomInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out input) && decimal.TryParse(CustomOutput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out output)))
        { Say("무료가 아니면 입력·출력 가격(USD, 100만 token당)을 숫자로 입력하세요."); return; }
        var added = service.AddCustomModel(provider, CustomModelId.Text, free, input, output);
        CustomModelId.Clear();CustomInput.Clear();CustomOutput.Clear();RefreshModels();Status($"{added.Display}을(를) 추가했습니다.");
    });
    void OnRemoveCustomModel(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (ModelsGrid.SelectedItem is not ModelOption { Custom: true } m) { Say("목록에서 '사용자 추가' 모델을 선택하세요."); return; }
        service.RemoveCustomModel(m.Id);RefreshModels();Status($"{m.Display}을(를) 삭제했습니다.");
    });

    // ---- Quick test
    void OnPickFile(object sender, RoutedEventArgs e)
    { var dialog = new OpenFileDialog { Title = "검수할 파일" };if (dialog.ShowDialog(this) == true) { SourcePath.Text = dialog.FileName;UsePath.IsChecked = true; } }
    void OnPickFolder(object sender, RoutedEventArgs e)
    { var dialog = new OpenFolderDialog { Title = "검수할 폴더" };if (dialog.ShowDialog(this) == true) { SourcePath.Text = dialog.FolderName;UsePath.IsChecked = true; } }
    void OnPreview(object sender, RoutedEventArgs e) => Guard(() =>
    {
        preview = null;RunButton.IsEnabled = false;
        var reviewers = new[] { ModelA.SelectedItem, ModelB.SelectedItem }.OfType<Choice>().Where(c => c.Model is not null).Select(c => c.Model!.Id).ToList();
        if (reviewers.Count == 0) { Say("모델을 선택하세요."); return; }
        bool text = UseText.IsChecked == true;
        preview = service.PrepareQuickTest(new(reviewers, (string)((ComboBoxItem)ProfileKind.SelectedItem).Tag, Instruction.Text,
            text ? SourceText.Text : null, text ? null : SourcePath.Text, ConfirmNonSensitive.IsChecked == true));
        PreviewText.Text = Describe(preview);RunButton.IsEnabled = true;Status("미리보기를 만들었습니다. 내용을 확인한 뒤 '승인하고 실행'을 누르세요.");
    });
    async void OnRun(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (preview is null || Confirm(preview) is not decimal maxUsd) return;
        var grant = service.Approve(preview.PlanId, maxUsd);
        running = new CancellationTokenSource();RunButton.IsEnabled = PreviewButton.IsEnabled = false;CancelButton.IsEnabled = true;
        Status("실행 중… (제공자 응답을 기다리는 중)");
        try
        {
            var result = await service.RunAsync(preview.PlanId, grant, running.Token);
            Show(result);RefreshRuns();Status("실행을 마쳤습니다.");
        }
        finally { running.Dispose();running = null;PreviewButton.IsEnabled = true;CancelButton.IsEnabled = false;preview = null; }
    });
    void OnCancel(object sender, RoutedEventArgs e) { running?.Cancel();Status("취소를 요청했습니다. 이미 보낸 요청은 '미확정'으로 남을 수 있습니다."); }
    string? reportPath;
    void Show(QuickResult result)
    {
        ResultSummary.Text = Summary(result);FindingsGrid.ItemsSource = result.Findings;NotesList.ItemsSource = result.UnitNotes;
        reportPath = result.ReportPath;ReportButton.IsEnabled = reportPath is not null;
    }
    // The report is shown as text inside the app; ReviewGate never launches other programs.
    void OnShowReport(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (reportPath is null) return;
        new Window
        {
            Title = "보고서 — " + reportPath, Owner = this, Width = 900, Height = 700,
            Content = new TextBox { Text = File.ReadAllText(reportPath), IsReadOnly = true, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        }.ShowDialog();
    });

    // ---- History
    void RefreshRuns() => RunsGrid.ItemsSource = service.RecentRuns().Select(r => r with { Status = StatusLabel(r.Status) }).ToList();
    void OnRefreshRuns(object sender, RoutedEventArgs e) => Guard(RefreshRuns);
    RunView? SelectedRun() => RunsGrid.SelectedItem as RunView;
    void OnShowRun(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (SelectedRun() is not RunView run) { Say("실행을 선택하세요."); return; }
        var result = service.Result(run.RunId);HistorySummary.Text = Summary(result);HistoryFindings.ItemsSource = result.Findings;
    });
    async void OnResolveNoCharge(object sender, RoutedEventArgs e) => await Resolve("no_charge_confirmed", "무료 티어이거나 제공자 오류 응답으로 청구가 없다고 사용자가 확인함");
    async void OnResolveConservative(object sender, RoutedEventArgs e) => await Resolve("conservative_full_reservation", "청구 여부를 확인할 수 없어 예약액 전액을 비용으로 처리");
    async Task Resolve(string outcome, string basis) => await GuardAsync(async () =>
    {
        if (SelectedRun() is not RunView run) { Say("실행을 선택하세요."); return; }
        if (run.UnknownHoldUsd == 0 && !run.Status.Contains("미확정", StringComparison.Ordinal)) { Say("이 실행에는 미확정 비용이 없습니다."); return; }
        if (MessageBox.Show(this, $"선택한 실행의 미확정 요청을 정리합니다.\n\n근거: {basis}\n\n계속할까요?", "미확정 정리", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var result = await service.ResolveUnknownAsync(run.RunId, outcome, basis);
        HistorySummary.Text = Summary(result);RefreshRuns();
    });

    // ---- Helpers
    static string Describe(QuickPreview p) =>
        $"모드: {(p.Mode == "independent" ? "독립 검수 (두 모델이 서로의 결과를 보지 않음)" : "단일 검수")}\n" +
        $"보낼 곳:\n  {string.Join("\n  ", p.Recipients)}\n" +
        $"자료: 파일 {p.SourceFiles}개, {p.SourceBytes:N0} byte · 요청 {p.Requests}건\n" +
        $"예약 상한(최악의 경우): ${p.ReservationUsd.ToString("0.######", CultureInfo.InvariantCulture)} — 실제 청구는 보통 이보다 적습니다.\n" +
        (p.Warnings.Count > 0 ? "주의:\n  " + string.Join("\n  ", p.Warnings) + "\n" : "") +
        (p.Exclusions.Count > 0 ? $"제외된 파일 {p.Exclusions.Count}개: " + string.Join(", ", p.Exclusions.Take(8)) + (p.Exclusions.Count > 8 ? " …" : "") : "");
    decimal? Confirm(QuickPreview p)
    {
        decimal maxUsd = Math.Max(0.01m, Math.Ceiling(p.ReservationUsd * 100) / 100);
        var answer = MessageBox.Show(this, Describe(p) + $"\n\n최대 ${maxUsd.ToString(CultureInfo.InvariantCulture)} 한도로 위 내용을 보내도 될까요?", "전송 승인",
            MessageBoxButton.YesNo, p.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes ? maxUsd : null;
    }
    static string StatusLabel(string status) => status switch
    {
        "completed" => "완료", "partial" => "일부 완료", "failed" => "실패", "blocked" => "차단",
        "needs_reconciliation" => "비용 미확정", "cancelled" => "취소", "running" => "실행 중(중단됨)", _ => status
    };
    static string Summary(QuickResult r) =>
        $"상태: {StatusLabel(r.Status)}{(r.ErrorCode is null ? "" : $" ({Explain(r.ErrorCode)})")} · 지적 {r.Findings.Count}건 · " +
        $"정산 ${r.UsageUsd.ToString("0.######", CultureInfo.InvariantCulture)}" +
        (r.UnknownHoldUsd > 0 ? $" · 미확정 ${r.UnknownHoldUsd.ToString("0.######", CultureInfo.InvariantCulture)} (실행 기록에서 정리)" : "") +
        (r.ProcessingRatio is decimal ratio ? $" · 처리율 {ratio:P0}" : "");
    static string Explain(string code) => code switch
    {
        "credential_missing" => "이 제공자의 API 키가 등록되어 있지 않습니다",
        "unauthorized" => "제공자가 API 키를 거부했습니다",
        "price_missing" => "선택한 모델의 가격 정보가 없습니다. 모델 탭에서 가격을 입력하거나 무료로 표시하세요",
        "price_snapshot_unavailable" or "price_snapshot_expired" => "가격표 유효기간이 지났습니다. 가격표를 갱신해야 합니다",
        "input_class_confirmation_required" => "비민감 자료 확인란을 선택하세요",
        "duplicate_reviewer_model" => "두 모델이 같은 모델입니다",
        "request_context_exceeded" => "자료가 너무 커서 한 요청에 들어가지 않습니다",
        "budget_exhausted" => "예산 한도를 넘습니다",
        "reconciliation_required" => "응답을 받지 못해 비용이 미확정입니다 (시간 초과·연결 끊김·서버 혼잡)",
        "owner_conflict" => "다른 실행이 진행 중입니다",
        "partial_review" => "일부 결과만 유효합니다",
        "rate_limited" => "제공자 호출 한도를 넘었습니다. 잠시 후 다시 시도하세요",
        "error" or "provider_error" => "제공자 서버 오류",
        "unsupported_input_format" => "지원하지 않는 파일 형식입니다 (텍스트·Markdown·코드·diff만 지원)",
        "invalid_utf8" => "UTF-8 텍스트가 아닙니다",
        "no_reviewable_content" => "검수할 내용이 없습니다",
        "nothing_to_resolve" => "정리할 미확정 요청이 없습니다",
        _ => code
    };
    void Status(string text) => StatusText.Text = text;
    void Say(string text, string title = "ReviewGate") => MessageBox.Show(this, text, title);
    void Guard(Action action)
    {
        try { action(); }
        catch (ReviewError e) { Say(Explain(e.Code), "확인 필요"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say("파일을 읽거나 쓰지 못했습니다: " + e.Message, "오류"); }
    }
    async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (ReviewError e) { Say(Explain(e.Code), "확인 필요"); }
        catch (OperationCanceledException) { Status("취소했습니다."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say("파일을 읽거나 쓰지 못했습니다: " + e.Message, "오류"); }
    }
}
