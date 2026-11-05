using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReviewGate.App;

namespace ReviewGate.Desktop;

// Shows what the two CLIs found next to the prices in use. Only rows both agreed on (numbers present in their quotes)
// enter the new snapshot; the user picks which Google models are used on the free tier and approves or cancels.
public sealed class PriceReviewWindow : Window
{
    sealed record Line(PriceProposalRow Row, CheckBox? Free)
    {
        public string Model => $"{Row.Target.Provider} · {Row.Target.ModelId}";
        public string State => Row.Status switch
        {
            "agreed" => "일치 → 적용", "disagree" => "두 CLI 값 다름 → 제외", "unverified_quote" => "인용에 숫자 없음 → 제외", _ => "찾지 못함 → 제외"
        };
        public string Current => Row.Current is null ? "없음" : Row.Current.Tier == "free" ? "무료" : $"{P(Row.Current.InputPerMillion)} / {P(Row.Current.CachedInputPerMillion)} / {P(Row.Current.OutputPerMillion)}";
        public string New => Row.Agreed ? $"{P(Row.InputPerMillion)} / {P(Row.CachedInputPerMillion)} / {P(Row.OutputPerMillion)}" : "-";
        public string Next => Row.NextChange?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";
        public string Notes => string.Join("; ", Row.Notes);
        static string P(decimal? v) => v is decimal d ? "$" + d.ToString("0.##########", CultureInfo.InvariantCulture) : "-";
    }
    readonly List<Line> lines;
    public IReadOnlySet<string> FreeTierModels => lines.Where(l => l.Free?.IsChecked == true).Select(l => l.Row.Target.CanonicalModelKey).ToHashSet();

    public PriceReviewWindow(PriceProposal proposal, IReadOnlySet<string> currentlyFree)
    {
        Title = "가격표 갱신 — 비교 후 승인";Width = 1100;Height = 640;FontFamily = new FontFamily("Malgun Gothic");FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        lines = proposal.Rows.Select(r => new Line(r, r.Target.Provider == "google" ? new CheckBox
        {
            IsEnabled = r.FreeTierAvailable, IsChecked = r.FreeTierAvailable && (r.FreeTier == "yes" || currentlyFree.Contains(r.Target.CanonicalModelKey)),
            ToolTip = r.FreeTierAvailable ? "이 모델을 Google 무료 티어로 사용 (계정에서 무료 사용이 되는지 직접 확인한 경우만)" : "두 CLI 모두 무료 티어가 없다고 확인함",
            VerticalAlignment = VerticalAlignment.Center
        } : null)).ToList();

        var root = new DockPanel { Margin = new Thickness(12) };
        var failed = proposal.Lookups.Where(l => l.Status != "completed").ToList();
        var head = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = $"조회: {string.Join(", ", proposal.Lookups.Select(l => l.Cli + (l.Status == "completed" ? " 완료" : $" 실패({l.Error})")))} · " +
                   $"적용 {proposal.Rows.Count(r => r.Agreed)}개 / 제외 {proposal.Rows.Count(r => !r.Agreed)}개. 값은 입력 / 캐시 입력 / 출력, USD per 1M token.\n" +
                   "제외된 모델은 다음 갱신까지 '가격 없음'이 되어 유료로 실행할 수 없습니다(무료 티어로 체크한 Google 모델은 무료 가격표에서 계속 사용 가능)." +
                   (failed.Count > 0 ? "\n한 CLI라도 실패하면 교차 확인을 할 수 없어 모든 모델이 제외됩니다. 취소하고 나중에 다시 시도하세요." : "")
        };
        if (failed.Count > 0) head.Foreground = Brushes.DarkRed;
        DockPanel.SetDock(head, Dock.Top);root.Children.Add(head);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var approve = new Button { Content = "승인하고 적용", Padding = new Thickness(12, 3, 12, 3), IsEnabled = proposal.Rows.Any(r => r.Agreed), Margin = new Thickness(0, 0, 8, 0) };
        approve.Click += (_, _) =>
        {
            int big = proposal.Rows.Count(r => r.Agreed && r.Notes.Any(n => n.StartsWith("큰 변경", StringComparison.Ordinal)));
            if (big > 0 && MessageBox.Show(this, $"가격이 2배 이상 바뀐 모델이 {big}개 있습니다. 인용 문구를 확인했나요?", "확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            DialogResult = true;
        };
        var cancel = new Button { Content = "취소", Padding = new Thickness(12, 3, 12, 3), IsCancel = true };
        buttons.Children.Add(approve);buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);root.Children.Add(buttons);

        var quotes = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(quotes, Dock.Bottom);root.Children.Add(quotes);

        var grid = new Grid();
        string[] headers = ["모델", "결과", "현재 가격", "새 가격", "다음 변경일", "무료 티어", "메모"];
        double[] widths = [190, 150, 170, 170, 90, 70, 0];
        for (int c = 0; c < headers.Length; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = widths[c] == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[c]) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < headers.Length; c++) Put(grid, new TextBlock { Text = headers[c], FontWeight = FontWeights.Bold, Margin = new Thickness(2, 0, 6, 4) }, 0, c);
        int row = 1;
        foreach (var line in lines)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var model = new TextBlock { Text = line.Model, Margin = new Thickness(2, 2, 6, 2), Cursor = System.Windows.Input.Cursors.Hand, TextDecorations = TextDecorations.Underline };
            model.MouseLeftButtonUp += (_, _) => quotes.Text = line.Model + "\n" + (line.Row.Quotes.Count == 0 ? "(인용 없음)" : string.Join("\n\n", line.Row.Quotes));
            Put(grid, model, row, 0);
            Put(grid, new TextBlock { Text = line.State, Foreground = line.Row.Agreed ? Brushes.DarkGreen : Brushes.DarkRed, Margin = new Thickness(2, 2, 6, 2) }, row, 1);
            Put(grid, new TextBlock { Text = line.Current, Margin = new Thickness(2, 2, 6, 2) }, row, 2);
            Put(grid, new TextBlock { Text = line.New, Margin = new Thickness(2, 2, 6, 2) }, row, 3);
            Put(grid, new TextBlock { Text = line.Next, Margin = new Thickness(2, 2, 6, 2) }, row, 4);
            if (line.Free is not null) Put(grid, line.Free, row, 5);
            Put(grid, new TextBlock { Text = line.Notes, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 6, 2) }, row, 6);
            row++;
        }
        root.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        quotes.Text = "모델 이름을 누르면 두 CLI가 인용한 가격 페이지 문구가 여기에 표시됩니다.";
        Content = root;
    }
    static void Put(Grid grid, UIElement element, int row, int column) { Grid.SetRow(element, row);Grid.SetColumn(element, column);grid.Children.Add(element); }
}
