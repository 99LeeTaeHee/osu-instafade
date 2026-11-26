using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OsuInstafade.Core;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Path = System.IO.Path;

namespace OsuInstafade.App;

public partial class MainWindow : FluentWindow
{
    private readonly List<string> _inputs = new();
    private readonly ObservableCollection<ResultItem> _results = new();
    private SkinAnalysis? _analysis;
    private Rgb? _customColor;
    private string? _outputDir;
    private bool _busy;
    private int _analysisVersion;

    // 미리보기 애니메이션
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private PreviewScene? _before, _after;

    private static readonly InstalledSkin Placeholder = new("osu! 스킨 목록에서 고르기", "");

    public MainWindow(string[] args)
    {
        InitializeComponent();
        ResultList.ItemsSource = _results;
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"osu! Instafade v{v?.Major}.{v?.Minor}.{v?.Build} · by LTH";
        Loaded += async (_, _) =>
        {
            await LoadInstalledSkinsAsync();
            if (args.Length > 0) await LoadInputsAsync(args);
            await SnapshotIfRequestedAsync();
        };
        CompositionTarget.Rendering += OnRenderFrame;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRenderFrame;
    }

    // ================= 스킨 넣기 =================

    private async Task LoadInstalledSkinsAsync()
    {
        IReadOnlyList<InstalledSkin> skins;
        try { skins = await Task.Run(OsuLocator.ListSkins); }
        catch { skins = Array.Empty<InstalledSkin>(); }
        bool osuFound = OsuLocator.FindSkinsFolders().Count > 0;
        var items = new List<InstalledSkin>
        {
            skins.Count > 0 ? Placeholder
                : new InstalledSkin(osuFound ? "osu!에 설치된 스킨 없음" : "osu! 설치를 찾지 못함", "")
        };
        items.AddRange(skins);
        InstalledSkins.ItemsSource = items;
        InstalledSkins.SelectedIndex = 0;
        InstalledSkins.IsEnabled = skins.Count > 0;
    }

    private async void OnInstalledSkinChosen(object sender, SelectionChangedEventArgs e)
    {
        if (InstalledSkins.SelectedItem is not InstalledSkin s || string.IsNullOrEmpty(s.Path)) return;
        InstalledSkins.SelectedIndex = 0;
        await LoadInputsAsync(new[] { s.Path });
    }

    private async void OnPickFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "osu! 스킨 파일 선택",
            Filter = "osu! 스킨 (*.osk;*.zip)|*.osk;*.zip|모든 파일 (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) await LoadInputsAsync(dlg.FileNames);
    }

    private async void OnPickFolders(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "osu! 스킨 폴더 선택",
            Multiselect = true,
        };
        var skinsDir = OsuLocator.FindSkinsFolders().FirstOrDefault();
        if (skinsDir != null) dlg.InitialDirectory = skinsDir;
        if (dlg.ShowDialog(this) == true) await LoadInputsAsync(dlg.FolderNames);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !_busy ? DragDropEffects.Copy : DragDropEffects.None;
        DropBorder.Stroke = e.Effects == DragDropEffects.Copy
            ? (Brush)FindResource("AccentTextFillColorPrimaryBrush")
            : (Brush)FindResource("ControlStrokeColorSecondaryBrush");
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
        => DropBorder.Stroke = (Brush)FindResource("ControlStrokeColorSecondaryBrush");

    private async void OnDrop(object sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);
        if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        await LoadInputsAsync(paths);
    }

    private void OnClearInputs(object sender, RoutedEventArgs e)
    {
        _inputs.Clear();
        _analysis = null;
        _analysisVersion++;
        SkinCard.Visibility = Visibility.Collapsed;
        NoticeBar.IsOpen = false;
        RunButton.IsEnabled = false;
        RunButtonText.Text = "잔상 제거하기";
        _before = _after = null;
    }

    private async Task LoadInputsAsync(IEnumerable<string> paths)
    {
        var valid = new List<string>();
        var rejected = new List<string>();
        foreach (var raw in paths)
        {
            var p = raw.Trim().Trim('"');
            if (Directory.Exists(p) ||
                (File.Exists(p) && (p.EndsWith(".osk", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))))
            {
                if (!valid.Contains(p, StringComparer.OrdinalIgnoreCase)) valid.Add(p);
            }
            else rejected.Add(Path.GetFileName(p));
        }
        if (valid.Count == 0)
        {
            ShowNotice(InfoBarSeverity.Error, "스킨이 아닙니다", ".osk 파일이나 스킨 폴더만 넣을 수 있습니다." +
                (rejected.Count > 0 ? $" ({string.Join(", ", rejected)})" : ""));
            return;
        }
        _inputs.Clear();
        _inputs.AddRange(valid);
        _results.Clear();
        SkinCard.Visibility = Visibility.Visible;
        QueueList.Visibility = _inputs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        QueueList.ItemsSource = _inputs.Select((p, i) => $"{i + 1}. {Path.GetFileName(p.TrimEnd('\\', '/'))}").ToList();
        RunButtonText.Text = _inputs.Count > 1 ? $"잔상 제거하기 ({_inputs.Count}개)" : "잔상 제거하기";
        await RefreshAnalysisAsync();
    }

    // ================= 분석 / 미리보기 =================

    private InstafadeOptions CurrentOptions() => new()
    {
        ShowNumbers = NumbersToggle.IsChecked == true,
        CircleColor = _customColor,
        FixComboCounter = ComboFixToggle.IsChecked == true,
        DisableHitLighting = LightingToggle.IsChecked == true,
        OutputDirectory = _outputDir,
    };

    private async Task RefreshAnalysisAsync()
    {
        if (_inputs.Count == 0) return;
        int ver = ++_analysisVersion;
        string path = _inputs[0];
        var opts = CurrentOptions();
        SkinName.Text = Path.GetFileName(path.TrimEnd('\\', '/'));
        SkinInfo.Text = "스킨을 살펴보는 중…";
        SkinPath.Text = path;
        RunButton.IsEnabled = false;

        SkinAnalysis a;
        try
        {
            a = await Task.Run(() => InstafadeEngine.Analyze(path, opts));
        }
        catch (Exception ex)
        {
            if (ver != _analysisVersion) return;
            _analysis = null;
            SkinInfo.Text = "이 스킨을 읽을 수 없습니다.";
            ShowNotice(InfoBarSeverity.Error, "스킨을 읽을 수 없습니다", FriendlyError(ex));
            RunButton.IsEnabled = _inputs.Count > 1; // 여러 개면 나머지는 처리 가능
            return;
        }
        if (ver != _analysisVersion) return;
        _analysis = a;

        SkinName.Text = string.IsNullOrWhiteSpace(a.Name) ? a.BaseName : a.Name;
        var info = new List<string>();
        if (!string.IsNullOrWhiteSpace(a.Author)) info.Add($"제작: {a.Author}");
        info.Add(a.IsHd ? "고화질(@2x)" : "일반 화질");
        info.Add(a.HasOwnNumbers ? "숫자 그림 있음" : "숫자 그림 없음");
        if (a.OverlayAnimated) info.Add("움직이는 테두리");
        info.Add(a.IsArchive ? ".osk 파일" : "스킨 폴더");
        SkinInfo.Text = string.Join(" · ", info);

        BuildSwatches(a);
        BuildPreview(a);

        // 알림
        if (a.AlreadyInstafaded)
            ShowNotice(InfoBarSeverity.Error, "이미 잔상 제거가 적용된 스킨입니다", "원래 스킨으로 다시 시도해 주세요.");
        else
        {
            var msgs = new List<string>();
            if (a.ComboCounterBroken && ComboFixToggle.IsChecked == true) msgs.Add("왼쪽 아래 콤보 숫자가 안 보이는 스킨이라 함께 고칩니다.");
            msgs.AddRange(a.Warnings);
            if (msgs.Count > 0) ShowNotice(InfoBarSeverity.Informational, "참고", string.Join("\n", msgs));
            else NoticeBar.IsOpen = false;
        }
        RunButton.IsEnabled = !_busy && (!a.AlreadyInstafaded || _inputs.Count > 1);
    }

    private void BuildSwatches(SkinAnalysis a)
    {
        SwatchPanel.Children.Clear();
        var colours = a.ComboColours.Count > 0 ? a.ComboColours : new[] { InstafadeEngine.DefaultCombo1 };
        foreach (var c in colours.Distinct())
        {
            bool selected = c == a.CircleColor;
            var b = new Button
            {
                Width = 34, Height = 34, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0),
                ToolTip = $"{c.ToHex()}  ({c})", Tag = c, Cursor = System.Windows.Input.Cursors.Hand,
                Template = SwatchTemplate(),
                Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                BorderBrush = selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(selected ? 3 : 1),
            };
            b.Click += async (_, _) => { _customColor = (Rgb)b.Tag; await RefreshAnalysisAsync(); };
            SwatchPanel.Children.Add(b);
        }
    }

    private static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        t.VisualTree = border;
        return t;
    }

    private void BuildPreview(SkinAnalysis a)
    {
        const double display = 110; // 서클이 화면에 보이는 크기(px)
        double scale = display / Math.Max(a.PreviewBefore.Width, a.PreviewBefore.Height);
        var colour = Color.FromRgb(a.CircleColor.R, a.CircleColor.G, a.CircleColor.B);

        _before = new PreviewScene(BeforeCanvas, a.PreviewApproach, colour, scale,
            new[] { a.PreviewBefore }, a.PreviewNumber, instant: false);
        _after = new PreviewScene(AfterCanvas, a.PreviewApproach, colour, scale,
            new[] { a.PreviewAfter }, null, instant: true);
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (_before == null || _after == null || SkinCard.Visibility != Visibility.Visible) return;
        double t = _fixedTime ?? _clock.Elapsed.TotalSeconds % 1.8;
        _before.Update(t);
        _after.Update(t);
    }

    private async void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        await RefreshAnalysisAsync();
    }

    private async void OnApplyCustomColor(object sender, RoutedEventArgs e)
    {
        if (!Rgb.TryParse(CustomColorBox.Text, out var c))
        {
            ShowNotice(InfoBarSeverity.Warning, "색을 알아볼 수 없습니다", "#FF66AA 또는 255,102,170 처럼 입력해 주세요.");
            return;
        }
        _customColor = c;
        await RefreshAnalysisAsync();
    }

    private async void OnResetColor(object sender, RoutedEventArgs e)
    {
        _customColor = null;
        CustomColorBox.Text = "";
        await RefreshAnalysisAsync();
    }

    private void OnPickOutputDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "결과 .osk 를 저장할 폴더" };
        if (dlg.ShowDialog(this) == true)
        {
            _outputDir = dlg.FolderName;
            OutputDirText.Text = _outputDir;
        }
    }

    private void OnResetOutputDir(object sender, RoutedEventArgs e)
    {
        _outputDir = null;
        OutputDirText.Text = "원본과 같은 폴더";
    }

    // ================= 실행 =================

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_busy || _inputs.Count == 0) return;
        _busy = true;
        RunButton.IsEnabled = false;
        BusyPanel.Visibility = Visibility.Visible;
        _results.Clear();
        var opts = CurrentOptions();
        var inputs = _inputs.ToList();
        try
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                string input = inputs[i];
                string label = Path.GetFileName(input.TrimEnd('\\', '/'));
                string prefix = inputs.Count > 1 ? $"[{i + 1}/{inputs.Count}] {label}: " : "";
                var progress = new Progress<string>(m => BusyText.Text = prefix + m);
                try
                {
                    var r = await Task.Run(() => InstafadeEngine.Run(input, opts, progress));
                    var details = new List<string>
                    {
                        $"저장 위치: {Path.GetDirectoryName(r.OutputPath)}",
                        $"서클 색 {r.Analysis.CircleColor.ToHex()} · 숫자 그림 {r.NumberImageWidthHd}×{r.NumberImageHeightHd}(고화질)",
                    };
                    if (r.ComboCounterFixed) details.Add("콤보 카운터 숫자도 고쳤습니다.");
                    _results.Add(ResultItem.Success($"완료: {Path.GetFileName(r.OutputPath)}", string.Join("\n", details), r.OutputPath, input));
                }
                catch (Exception ex)
                {
                    _results.Add(ResultItem.Failure($"실패: {label}", FriendlyError(ex)));
                }
            }
        }
        finally
        {
            _busy = false;
            BusyPanel.Visibility = Visibility.Collapsed;
            RunButton.IsEnabled = _inputs.Count > 0 && (_analysis == null || !_analysis.AlreadyInstafaded || _inputs.Count > 1);
        }
    }

    private void OnImportToOsu(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            ShowNotice(InfoBarSeverity.Warning, "osu!로 열 수 없습니다",
                "이 PC에서 .osk 파일이 osu!와 연결되어 있지 않습니다. 만든 .osk 파일을 osu! 창에 직접 끌어다 놓아 주세요.");
        }
    }

    private void OnDeleteOriginal(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ResultItem item || item.SourcePath is not string src) return;
        bool isFolder = Directory.Exists(src);
        if (!isFolder && !File.Exists(src))
        {
            item.OriginalStatus = "원본이 이미 없습니다.";
            return;
        }
        bool inOsuSkins = OsuLocator.FindSkinsFolders().Any(d =>
            Path.GetFullPath(src).StartsWith(Path.GetFullPath(d).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

        string msg = $"원본 스킨을 휴지통으로 옮길까요?\n\n{src}\n\n" +
                     (inOsuSkins ? "osu!에 설치된 스킨 목록에서도 사라집니다.\n" : "") +
                     "휴지통에서 다시 꺼낼 수 있습니다.";
        if (System.Windows.MessageBox.Show(this, msg, "원본 지우기", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Question, System.Windows.MessageBoxResult.No)
            != System.Windows.MessageBoxResult.Yes) return;

        bool ok;
        try { ok = RecycleBin.Send(src); }
        catch { ok = false; }

        if (ok)
        {
            item.OriginalStatus = $"원본을 휴지통으로 옮겼습니다. (휴지통에서 되살릴 수 있습니다)";
            // 지운 원본이 선택 목록에 있으면 빼기
            _inputs.RemoveAll(p => string.Equals(Path.GetFullPath(p).TrimEnd('\\'), Path.GetFullPath(src).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (_inputs.Count == 0) OnClearInputs(this, new RoutedEventArgs());
        }
        else
        {
            ShowNotice(InfoBarSeverity.Warning, "원본을 지우지 못했습니다",
                "다른 프로그램(osu! 등)이 이 스킨을 쓰고 있지 않은지 확인한 뒤 다시 시도해 주세요.");
        }
    }

    private void OnKeepOriginal(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ResultItem item) item.OriginalStatus = "원본 스킨을 그대로 두었습니다.";
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch { /* 무시 */ }
    }

    // ================= 개발용: 화면 캡처 (환경 변수 INSTAFADE_SNAPSHOT 이 있을 때만) =================

    private double? _fixedTime;

    private async Task SnapshotIfRequestedAsync()
    {
        string? outFile = Environment.GetEnvironmentVariable("INSTAFADE_SNAPSHOT");
        if (string.IsNullOrEmpty(outFile)) return;
        if (double.TryParse(Environment.GetEnvironmentVariable("INSTAFADE_SNAPSHOT_T"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var t)) _fixedTime = t;
        if (Environment.GetEnvironmentVariable("INSTAFADE_SNAPSHOT_RUN") == "1")
        {
            _outputDir = Environment.GetEnvironmentVariable("INSTAFADE_SNAPSHOT_OUTDIR");
            OnRun(this, new RoutedEventArgs());
            while (_busy) await Task.Delay(100);
        }
        if (Environment.GetEnvironmentVariable("INSTAFADE_SNAPSHOT_EXPAND") == "1")
            foreach (var ex in FindChildren<CardExpander>(this)) ex.IsExpanded = true;
        if (double.TryParse(Environment.GetEnvironmentVariable("INSTAFADE_SNAPSHOT_HEIGHT"), out var hh)) Height = hh;
        await Task.Delay(1200);
        var root = (FrameworkElement)Content;
        double dpi = 144;
        int w = (int)(root.ActualWidth * dpi / 96), h = (int)(root.ActualHeight * dpi / 96);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = File.Create(outFile)) enc.Save(fs);
        Close();
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) yield return t;
            foreach (var d in FindChildren<T>(c)) yield return d;
        }
    }

    // ================= 도우미 =================

    private void ShowNotice(InfoBarSeverity severity, string title, string message)
    {
        NoticeBar.Severity = severity;
        NoticeBar.Title = title;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        InstafadeException ie => ie.Message,
        UnauthorizedAccessException => "파일에 접근할 권한이 없습니다. 다른 저장 위치를 골라 보세요.",
        IOException io => "파일을 읽거나 쓰는 중 문제가 생겼습니다. 다른 프로그램(osu! 등)이 파일을 쓰고 있지 않은지 확인해 주세요.\n(" + io.Message + ")",
        _ => "처리 중 문제가 생겼습니다: " + ex.Message,
    };
}

/// <summary>결과 목록 한 줄</summary>
public sealed class ResultItem : INotifyPropertyChanged
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public string? Path { get; init; }
    /// <summary>원본 스킨(.osk 파일 또는 폴더) 경로</summary>
    public string? SourcePath { get; init; }
    public SymbolRegular Icon { get; init; }
    public Brush IconBrush { get; init; } = Brushes.White;
    public Visibility ButtonsVisibility => Path != null ? Visibility.Visible : Visibility.Collapsed;

    private string? _originalStatus;
    /// <summary>원본을 지웠거나 두기로 한 뒤 보여 줄 문구 (null 이면 아직 선택 전)</summary>
    public string? OriginalStatus
    {
        get => _originalStatus;
        set
        {
            _originalStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OriginalStatus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OriginalChoiceVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OriginalStatusVisibility)));
        }
    }
    public Visibility OriginalSectionVisibility => Path != null && SourcePath != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OriginalChoiceVisibility => _originalStatus == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OriginalStatusVisibility => _originalStatus != null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static ResultItem Success(string title, string message, string path, string sourcePath) => new()
    {
        Title = title, Message = message, Path = path, SourcePath = sourcePath, Icon = SymbolRegular.CheckmarkCircle24,
        IconBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)),
    };

    public static ResultItem Failure(string title, string message) => new()
    {
        Title = title, Message = message, Icon = SymbolRegular.ErrorCircle24,
        IconBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
    };
}

/// <summary>
/// 미리보기 한 칸: 줄어드는 원(어프로치 서클) → 타격 → 서클이 퍼지며 사라짐(변경 전) / 바로 사라짐(변경 후)
/// </summary>
internal sealed class PreviewScene
{
    private const double ApproachEnd = 0.9;   // 이 시점에 타격
    private const double FadeTime = 0.24;      // osu! 기본 잔상 시간
    private const double FadeInTime = 0.3;

    private readonly FrameworkElement _approach;
    private readonly ScaleTransform _approachScale = new();
    private readonly Image _circle;
    private readonly ScaleTransform _circleScale = new();
    private readonly Image? _number;
    private readonly bool _instant;

    public PreviewScene(Grid host, RgbaImage? approach, Color colour, double scale, RgbaImage[] layers, RgbaImage? number, bool instant)
    {
        host.Children.Clear();
        _instant = instant;

        var circleImg = layers[0];
        _circle = MakeImage(circleImg, scale);
        _circle.RenderTransform = _circleScale;

        if (approach != null)
            _approach = MakeImage(approach, scale);
        else
        {
            // 변경 전·후 그림은 화면에서 같은 크기로 보이도록 만들어져 있음
            double d = Math.Max(circleImg.Width, circleImg.Height) * scale;
            _approach = new Ellipse { Width = d, Height = d, Stroke = new SolidColorBrush(colour), StrokeThickness = 3 };
        }
        _approach.RenderTransformOrigin = new Point(0.5, 0.5);
        _approach.RenderTransform = _approachScale;
        _approach.HorizontalAlignment = HorizontalAlignment.Center;
        _approach.VerticalAlignment = VerticalAlignment.Center;

        host.Children.Add(_circle);
        if (number != null)
        {
            _number = MakeImage(number, scale);
            host.Children.Add(_number);
        }
        host.Children.Add(_approach);
    }

    private static Image MakeImage(RgbaImage img, double scale) => new()
    {
        Source = img.ToBitmapSource(),
        Width = img.Width * scale,
        Height = img.Height * scale,
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Point(0.5, 0.5),
        SnapsToDevicePixels = false,
    };

    public void Update(double t)
    {
        double appear = Math.Clamp(t / FadeInTime, 0, 1);
        if (t < ApproachEnd)
        {
            // 다가오는 중
            double p = t / ApproachEnd;
            double s = 3.0 - 2.0 * p;
            _approachScale.ScaleX = _approachScale.ScaleY = s;
            _approach.Opacity = 0.9 * appear;
            _circle.Opacity = appear;
            _circleScale.ScaleX = _circleScale.ScaleY = 1;
            if (_number != null) _number.Opacity = appear;
            return;
        }
        // 타격 이후
        _approach.Opacity = 0;
        if (_number != null) _number.Opacity = 0;
        if (_instant)
        {
            _circle.Opacity = 0;
            return;
        }
        double f = Math.Clamp((t - ApproachEnd) / FadeTime, 0, 1);
        _circle.Opacity = 1 - f;
        double grow = 1 + 0.4 * (1 - Math.Pow(1 - f, 2));
        _circleScale.ScaleX = _circleScale.ScaleY = grow;
    }
}
