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
// A reviewer as the user picks it: a provider with its active key, primary model and optional fallback model.
public record ReviewerChoice(string Label, string Provider, ModelOption? Primary, ModelOption? Fallback);

// The desktop shell only collects input and shows results; every action goes through DesktopService, which uses
// the same planner, ledger, grant record and engine as the CLI. Approval is the confirmation dialog shown before a send.
public partial class MainWindow : Window
{
    readonly DesktopService service = new(
        StatePaths.DefaultRoot,
        Path.Combine(AppContext.BaseDirectory, "profiles"), new KeyRing(new WindowsCredentialVault()));
    readonly Dictionary<string, (ComboBox Primary, ComboBox Fallback)> modelBoxes = [];
    List<ModelOption> models = [];
    QuickPreview? preview;
    CancellationTokenSource? running;
    string? reportPath;

    public MainWindow() => InitializeComponent();

    void OnLoaded(object sender, RoutedEventArgs e) => Guard(() =>
    {
        SourceText.Text = DesktopService.SampleCode;
        CustomProvider.ItemsSource = ProviderCatalog.All;CustomProvider.SelectedIndex = 0;
        RefreshKeys();RefreshModels();RefreshRuns();
        var price = RefreshPriceStatus();
        if (price.NoticeDue && MessageBox.Show(this, price.Text + "\n\n지금 가격표를 갱신할까요?", "가격표 갱신", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Dispatcher.BeginInvoke(async () => await RefreshPrices());
    });

    // ---- Price snapshot: status strip, refresh by two CLIs, user approval.
    PriceStatus RefreshPriceStatus()
    {
        var status = service.PriceStatus();
        PriceBannerText.Text = status.Text + (status.NoticeDue ? "  — 갱신이 필요합니다." : "");
        PriceBanner.Background = status.NoticeDue ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE)) : Brushes.Transparent;
        return status;
    }
    async void OnRefreshPrices(object sender, RoutedEventArgs e) => await RefreshPrices();
    async Task RefreshPrices() => await GuardAsync(async () =>
    {
        var sources = service.PriceSources();var targets = service.PriceTargets();
        string ask = "Claude Code와 Codex가 각각 공식 가격 페이지를 읽어 모델별 단가를 찾습니다.\n\n" +
            string.Join("\n", sources.Pages.Select(p => $"  {p.Key}: {p.Value}")) + $"\n\n대상 모델 {targets.Count}개: " + string.Join(", ", targets.Select(t => t.ModelId)) +
            "\n\n- 두 CLI는 이 작업에서만 웹 조회 도구를 켭니다(파일 수정 도구는 켜지 않음).\n- 각 CLI의 구독 사용량을 씁니다. 몇 분 걸릴 수 있습니다.\n- 결과는 바로 적용되지 않고, 비교 화면에서 승인해야 적용됩니다.\n\n진행할까요?";
        if (MessageBox.Show(this, ask, "가격표 갱신", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Status("가격 조회 중… (Claude Code와 Codex가 가격 페이지를 읽는 중)");IsEnabled = false;
        PriceProposal proposal;
        try { proposal = await service.LookupPricesAsync(); }
        finally { IsEnabled = true; }
        Status("가격 조회를 마쳤습니다. 비교 화면에서 확인하세요.");
        var review = new PriceReviewWindow(proposal, service.CurrentFreeTierModels()) { Owner = this };
        if (review.ShowDialog() != true) { service.CancelPrices(proposal);Status($"가격표를 바꾸지 않았습니다. 조회 기록: {proposal.RecordId}"); return; }
        var status = service.ApprovePrices(proposal, review.FreeTierModels);
        RefreshPriceStatus();RefreshModels();Status("새 가격표를 적용했습니다: " + status.Text);
    });

    // ---- API keys: one panel per provider; names only, never values.
    void RefreshKeys()
    {
        KeyPanels.Children.Clear();
        foreach (var group in service.Keys())
        {
            string provider = group.Provider.Id;
            if (group.Provider.IsCli)
            {
                // Agent CLIs use their own login; there is no key to store, only the executable to find.
                var cliBody = new StackPanel();
                cliBody.Children.Add(new TextBlock { Style = (Style)FindResource("Hint"), Text = group.CliPath is null
                    ? $"{group.Provider.Endpoint}를 찾지 못했습니다. CLI를 설치하고 한 번 로그인하세요."
                    : "이 CLI에 로그인한 계정(구독)으로 실행됩니다. ReviewGate는 키를 저장하지 않습니다. 샌드박스 없이 매번 빈 임시 폴더에서 실행합니다." });
                var cliTest = new Button { Content = "연결 테스트", IsEnabled = group.CliPath is not null, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                cliTest.Click += async (_, _) => await ConnectionTest(provider);cliBody.Children.Add(cliTest);
                KeyPanels.Children.Add(new GroupBox
                {
                    Header = new TextBlock { Text = $"{group.Provider.DisplayName} — {group.Status}", FontWeight = FontWeights.SemiBold, Foreground = group.Ready ? Brushes.DarkGreen : Brushes.Gray },
                    Content = cliBody
                });
                continue;
            }
            var list = new ListBox { Height = 86, Margin = new Thickness(0, 4, 0, 6) };
            foreach (var key in group.Keys)
                list.Items.Add(new ListBoxItem
                {
                    Tag = key.Id, FontWeight = key.Active ? FontWeights.SemiBold : FontWeights.Normal,
                    Content = $"{(key.Active ? "● 사용 중   " : "○ ")}{key.Name}{(key.Saved is { } saved ? $"     (저장 {saved.LocalDateTime:yyyy-MM-dd HH:mm})" : "")}"
                });
            string? SelectedKey() => (list.SelectedItem as ListBoxItem)?.Tag as string;
            var name = new TextBox { Width = 170, VerticalContentAlignment = VerticalAlignment.Center };
            var secret = new PasswordBox { Width = 340, Margin = new Thickness(6, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Button("선택한 키 사용", () =>
            {
                if (SelectedKey() is not string id) { Say("목록에서 사용할 키를 선택하세요."); return; }
                service.ActivateKey(provider, id);RefreshKeys();RefreshModels();Status($"{group.Provider.DisplayName}: 선택한 키를 사용합니다.");
            }));
            buttons.Children.Add(Button("선택한 키 삭제", () =>
            {
                if (SelectedKey() is not string id) { Say("목록에서 삭제할 키를 선택하세요."); return; }
                if (MessageBox.Show(this, "선택한 키를 Windows 자격 증명 관리자에서 삭제할까요?", "키 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                service.RemoveKey(provider, id);RefreshKeys();RefreshModels();Status($"{group.Provider.DisplayName}: 키를 삭제했습니다.");
            }));
            var test = new Button { Content = "연결 테스트" };test.Click += async (_, _) => await ConnectionTest(provider);buttons.Children.Add(test);
            var add = new StackPanel { Orientation = Orientation.Horizontal };
            add.Children.Add(new TextBlock { Text = "새 키 이름 ", VerticalAlignment = VerticalAlignment.Center });add.Children.Add(name);
            add.Children.Add(new TextBlock { Text = "   키 ", VerticalAlignment = VerticalAlignment.Center });add.Children.Add(secret);
            add.Children.Add(Button("키 추가", () =>
            {
                if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(secret.Password)) { Say("키 이름과 키를 모두 입력하세요."); return; }
                service.AddKey(provider, name.Text, secret.Password);secret.Clear();name.Clear();RefreshKeys();RefreshModels();
                Status($"{group.Provider.DisplayName}: 키를 추가했고 새 키를 사용합니다.");
            }));
            var body = new StackPanel();body.Children.Add(list);body.Children.Add(buttons);
            body.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });body.Children.Add(add);
            KeyPanels.Children.Add(new GroupBox
            {
                Header = new TextBlock { Text = $"{group.Provider.DisplayName} — {group.Status}", FontWeight = FontWeights.SemiBold, Foreground = group.Ready ? Brushes.DarkGreen : Brushes.Gray },
                Content = body
            });
        }
    }
    Button Button(string text, Action action) { var b = new Button { Content = text };b.Click += (_, _) => Guard(action);return b; }
    async Task ConnectionTest(string provider) => await GuardAsync(async () =>
    {
        var choice = ReviewerChoices().SingleOrDefault(c => c.Provider == provider);
        if (choice?.Primary is null) { Say("이 제공자의 대표 모델이 없거나 가격 정보가 없습니다. 모델 탭을 확인하세요.");return; }
        var p = service.PrepareQuickTest(new([choice.Primary.Id], "code", "Find correctness defects and quote the exact source line.", DesktopService.SampleCode, null, true));
        if (Confirm(p) is not decimal maxUsd) return;
        Status($"{provider} 연결 테스트 중…");
        var result = await service.RunAsync(p.PlanId, service.Approve(p.PlanId, maxUsd));
        RefreshRuns();
        Say($"{choice.Label}\n\n{Summary(result)}" + (result.Findings.Count > 0 ? $"\n\n첫 지적: {result.Findings[0].Claim} ({result.Findings[0].Evidence})" : ""), "연결 테스트 결과");
    });

    // ---- Models: per provider a primary and an optional fallback.
    void RefreshModels()
    {
        var settings = service.LoadSettings();models = service.Models();
        GoogleFreeTier.IsChecked = settings.GoogleFreeTier;ModelsGrid.ItemsSource = models;
        ModelGrid.Children.Clear();ModelGrid.RowDefinitions.Clear();modelBoxes.Clear();
        AddRow(new TextBlock { Text = "제공자", FontWeight = FontWeights.SemiBold }, new TextBlock { Text = "대표 모델", FontWeight = FontWeights.SemiBold }, new TextBlock { Text = "서브 모델 (응답이 없을 때)", FontWeight = FontWeights.SemiBold });
        var priced = models.Where(m => m.Priced).ToList();
        foreach (var provider in ProviderCatalog.All)
        {
            var own = models.Where(m => m.Provider == provider.Id).ToList();
            var primary = new ComboBox { ItemsSource = own, DisplayMemberPath = "Display", Margin = new Thickness(0, 2, 0, 2) };
            primary.SelectedItem = own.FirstOrDefault(m => m.Id == settings.DefaultModels.GetValueOrDefault(provider.Id)) ?? own.FirstOrDefault();
            var fallbackChoices = new List<Choice> { new("(없음)", null) };fallbackChoices.AddRange(priced.Select(m => new Choice($"{m.Display} — {m.PriceText}", m)));
            var fallback = new ComboBox { ItemsSource = fallbackChoices, DisplayMemberPath = "Label", Margin = new Thickness(0, 2, 0, 2) };
            string? fallbackId = settings.FallbackModels?.GetValueOrDefault(provider.Id);
            fallback.SelectedItem = fallbackChoices.FirstOrDefault(c => c.Model?.Id == fallbackId) ?? fallbackChoices[0];
            AddRow(new TextBlock { Text = provider.DisplayName, VerticalAlignment = VerticalAlignment.Center }, primary, fallback);
            modelBoxes[provider.Id] = (primary, fallback);
        }
        var choices = ReviewerChoices();
        ReviewerA.ItemsSource = choices;ReviewerA.SelectedItem = choices.FirstOrDefault(c => c.Primary is not null && c.Label.Contains("사용 중 키", StringComparison.Ordinal)) ?? choices.FirstOrDefault();
        var second = new List<ReviewerChoice> { new("(없음 — 단일 검수)", "", null, null) };second.AddRange(choices);
        ReviewerB.ItemsSource = second;ReviewerB.SelectedIndex = 0;
    }
    void AddRow(UIElement provider, UIElement primary, UIElement fallback)
    {
        int row = ModelGrid.RowDefinitions.Count;ModelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var (element, column) in new[] { (provider, 0), (primary, 1), (fallback, 3) }) { Grid.SetRow(element, row);Grid.SetColumn(element, column);ModelGrid.Children.Add(element); }
    }
    List<ReviewerChoice> ReviewerChoices()
    {
        var settings = service.LoadSettings();var keys = service.CredentialStatus();
        return ProviderCatalog.All.Select(p =>
        {
            var primary = models.FirstOrDefault(m => m.Id == settings.DefaultModels.GetValueOrDefault(p.Id) && m.Priced);
            var fallback = models.FirstOrDefault(m => m.Id == settings.FallbackModels?.GetValueOrDefault(p.Id) && m.Priced);
            string label = primary is null ? $"{p.DisplayName} — 대표 모델 없음" :
                $"{p.DisplayName} — {primary.ModelId}{(fallback is null ? "" : $" (서브 {fallback.ModelId})")} — {(keys[p.Id] ? (p.IsCli ? "CLI 사용 가능" : "사용 중 키 있음") : (p.IsCli ? "CLI 없음" : "키 없음"))}";
            return new ReviewerChoice(label, p.Id, primary, fallback);
        }).ToList();
    }
    void OnSaveSettings(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var defaults = new Dictionary<string, string>();var fallbacks = new Dictionary<string, string>();
        foreach (var (provider, boxes) in modelBoxes)
        {
            if (boxes.Primary.SelectedItem is ModelOption m) defaults[provider] = m.Id;
            if (boxes.Fallback.SelectedItem is Choice { Model: { } f })
            {
                if (boxes.Primary.SelectedItem is ModelOption p && p.Id == f.Id) { Say($"{provider}: 서브 모델은 대표 모델과 달라야 합니다.");return; }
                fallbacks[provider] = f.Id;
            }
        }
        service.SaveSettings(service.LoadSettings() with { GoogleFreeTier = GoogleFreeTier.IsChecked == true, DefaultModels = defaults, FallbackModels = fallbacks });
        RefreshModels();Status("설정을 저장했습니다.");
    });
    void OnAddCustomModel(object sender, RoutedEventArgs e) => Guard(() =>
    {
        string provider = ((ProviderInfo)CustomProvider.SelectedItem).Id;bool free = CustomFree.IsChecked == true;
        decimal input = 0, output = 0;
        if (!free && !(decimal.TryParse(CustomInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out input) && decimal.TryParse(CustomOutput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out output)))
        { Say("무료가 아니면 입력·출력 가격(USD, 100만 token당)을 숫자로 입력하세요.");return; }
        var added = service.AddCustomModel(provider, CustomModelId.Text, free, input, output);
        CustomModelId.Clear();CustomInput.Clear();CustomOutput.Clear();RefreshModels();Status($"{added.Display}을(를) 추가했습니다.");
    });
    void OnRemoveCustomModel(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (ModelsGrid.SelectedItem is not ModelOption { Custom: true } m) { Say("목록에서 '사용자 추가' 모델을 선택하세요.");return; }
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
        var picked = new[] { ReviewerA.SelectedItem, ReviewerB.SelectedItem }.OfType<ReviewerChoice>().Where(c => c.Provider.Length > 0).ToList();
        if (picked.Count == 0) { Say("검수자를 선택하세요.");return; }
        if (picked.FirstOrDefault(c => c.Primary is null) is { } missing) { Say($"{missing.Label}\n모델 탭에서 대표 모델을 정하세요.");return; }
        bool text = UseText.IsChecked == true, fallback = UseFallback.IsChecked == true;
        preview = service.PrepareQuickTest(new(picked.Select(c => c.Primary!.Id).ToList(), (string)((ComboBoxItem)ProfileKind.SelectedItem).Tag, Instruction.Text,
            text ? SourceText.Text : null, text ? null : SourcePath.Text, ConfirmNonSensitive.IsChecked == true,
            fallback ? picked.Select(c => c.Fallback?.Id).ToList() : null));
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
        if (SelectedRun() is not RunView run) { Say("실행을 선택하세요.");return; }
        var result = service.Result(run.RunId);HistorySummary.Text = Summary(result);HistoryFindings.ItemsSource = result.Findings;
    });
    async void OnResolveNoCharge(object sender, RoutedEventArgs e) => await Resolve("no_charge_confirmed", "무료 티어이거나 제공자 오류 응답으로 청구가 없다고 사용자가 확인함");
    async void OnResolveConservative(object sender, RoutedEventArgs e) => await Resolve("conservative_full_reservation", "청구 여부를 확인할 수 없어 예약액 전액을 비용으로 처리");
    async Task Resolve(string outcome, string basis) => await GuardAsync(async () =>
    {
        if (SelectedRun() is not RunView run) { Say("실행을 선택하세요.");return; }
        if (MessageBox.Show(this, $"선택한 실행의 미확정 요청을 정리합니다.\n\n근거: {basis}\n\n계속할까요?", "미확정 정리", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var result = await service.ResolveUnknownAsync(run.RunId, outcome, basis);
        HistorySummary.Text = Summary(result);RefreshRuns();
    });

    // ---- Helpers
    static string Describe(QuickPreview p) =>
        $"모드: {(p.Mode == "independent" ? "독립 검수 (두 검수자가 서로의 결과를 보지 않음)" : "단일 검수")}\n" +
        $"보낼 곳:\n  {string.Join("\n  ", p.Recipients)}\n" +
        $"자료: 파일 {p.SourceFiles}개, {p.SourceBytes:N0} byte · 요청 {p.Requests}건 (서브 모델 요청 포함)\n" +
        $"예약 상한(최악의 경우): ${p.ReservationUsd.ToString("0.######", CultureInfo.InvariantCulture)} — 실제 청구는 보통 이보다 적습니다.\n" +
        (p.Warnings.Count > 0 ? "주의:\n  " + string.Join("\n  ", p.Warnings.Distinct()) + "\n" : "") +
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
        "credential_missing" => "사용 중인 API 키가 없거나 CLI가 설치되어 있지 않습니다. API 키 탭을 확인하세요",
        "cli_not_found" => "CLI 실행 파일을 찾지 못했습니다",
        "no_agreed_prices" => "두 CLI가 일치한 가격이 하나도 없어 가격표를 만들 수 없습니다",
        "unauthorized" => "제공자가 API 키를 거부했습니다",
        "price_missing" => "선택한 모델의 가격 정보가 없습니다. 모델 탭에서 가격을 입력하거나 무료로 표시하세요",
        "price_snapshot_unavailable" or "price_snapshot_expired" => "가격표 유효기간이 지났습니다. 가격표를 갱신해야 합니다",
        "input_class_confirmation_required" => "비민감 자료 확인란을 선택하세요",
        "duplicate_reviewer_model" => "같은 모델이 두 번 선택되었습니다 (대표·서브·두 검수자 모두 서로 달라야 합니다)",
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
        "duplicate_key_name" => "같은 이름의 키가 이미 있습니다",
        "invalid_key_name" => "키 이름은 1~64자로 입력하세요",
        "invalid_credential_value" => "키 값에 공백을 넣을 수 없습니다",
        "unknown_key" => "선택한 키를 찾을 수 없습니다",
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
