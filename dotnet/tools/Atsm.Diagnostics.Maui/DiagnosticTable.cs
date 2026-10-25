using Microsoft.Maui.Controls;
using static Atsm.Diagnostics.Maui.Globals;

namespace Atsm.Diagnostics.Maui;

/// <summary>Native MAUI table. Widths survive refreshes, groups preserve children and paging never discards rows.</summary>
internal sealed class DiagnosticTable : ContentView
{
    private const int PageSize = 300;
    private readonly IDiagnosticPlatformServices _platform;
    private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single };
    private readonly Grid _surface = new() { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
    private readonly Label _position = new() { FontSize = 12, TextColor = Color.FromArgb("#52677E"), VerticalTextAlignment = TextAlignment.Center };
    private readonly Button _previous = new() { Text = T("Grid.Previous"), FontSize = 12 }, _next = new() { Text = T("Grid.Next"), FontSize = 12 };
    private readonly Dictionary<string, WidthState> _widths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private IReadOnlyList<DiagnosticRowGroup> _groups = [];
    private string[] _columns = [];
    private WidthState? _state;
    private int _page, _originalCount;
    private string _view = "";
    private int _flexibleColumn;
    public event Action<string>? DetailSelected;
    internal void VerifyAutomaticLayout()
    {
        if (_state is null || Width <= 0 || _list.Handler?.PlatformView is null || _surface.Width < Width - 1)
            throw new InvalidOperationException("ATSM-TABLE-NOT-REALIZED");
        if (_surface.X > 1) throw new InvalidOperationException("ATSM-TABLE-NOT-LEFT-ALIGNED");
        foreach (var row in _surface.Children.OfType<TableRow>()) row.VerifySelectableText();
    }

    public DiagnosticTable(IDiagnosticPlatformServices platform)
    {
        _platform = platform;
        _surface.HorizontalOptions = LayoutOptions.Start;
        _list.BackgroundColor = Colors.White;
        _list.EmptyView = new Label { Text = T("View.Empty"), Margin = 12, TextColor = Color.FromArgb("#52677E") };
        _previous.TextColor = _next.TextColor = Color.FromArgb("#38576B");
        _previous.BackgroundColor = _next.BackgroundColor = Color.FromArgb("#EAF0F6");
        SizeChanged += (_, _) => { if (_state is { Manual: false } && Width > 0) FitColumns(); else UpdateSurfaceWidth(); };
        _list.SelectionChanged += (_, args) => { if (args.CurrentSelection.FirstOrDefault() is VisibleRow row) DetailSelected?.Invoke(row.Row.Details); };
        Grid.SetRow(_list, 1); _surface.Add(_list);
        var scroll = new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _surface };
        var footer = new HorizontalStackLayout { Spacing = 8, Padding = new Thickness(6, 2), Children = { _previous, _next, _position } };
        var root = new Grid { RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)] }; root.Add(scroll); Grid.SetRow(footer, 1); root.Add(footer); Content = root;
        _previous.Clicked += (_, _) => { if (_page > 0) { _page--; Refresh(); } };
        _next.Clicked += (_, _) => { _page++; Refresh(); };
    }
    public void SetRows(string view, string[] columns, IEnumerable<DiagnosticRow> source, bool group, int flexibleColumn)
    {
        var rows = source.ToArray();
        SetGroups(view, columns, DiagnosticGrouping.Build(rows, group), flexibleColumn);
    }
    public void SetGroups(string view, string[] columns, IReadOnlyList<DiagnosticRowGroup> groups, int flexibleColumn)
    {
        if (_view != view) { _view = view; _page = 0; _expanded.Clear(); }
        _columns = columns; _flexibleColumn = flexibleColumn; _originalCount = groups.Sum(g => g.Count); _groups = groups;
        if (!_widths.TryGetValue(view, out var state)) _widths[view] = state = new(columns.Length);
        _state = state; if (!state.Manual) FitColumns();
        var header = new TableRow(state, columns, null, null, true);
        for (var column = 0; column < columns.Length; column++)
        {
            var index = column;
            var line = new BoxView { Color = Colors.Transparent, WidthRequest = 1, HeightRequest = 18, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
            var grip = new Grid { BackgroundColor = Colors.Transparent, WidthRequest = 12, HorizontalOptions = LayoutOptions.End }; grip.Add(line);
            var hover = new PointerGestureRecognizer(); hover.PointerEntered += (_, _) => line.Color = Color.FromArgb("#B8C6D1"); hover.PointerExited += (_, _) => line.Color = Colors.Transparent; grip.GestureRecognizers.Add(hover);
            Grid.SetColumn(grip, column + 1); header.Add(grip); ToolTipProperties.SetText(grip, T("Grid.Resize"));
            _platform.AttachColumnResize(grip, delta => { state.Manual = true; state.Resize(index, delta); UpdateSurfaceWidth(); }, () => FitColumn(index));
        }
        if (_surface.Children.FirstOrDefault(c => c is TableRow) is { } previousHeader) _surface.Remove(previousHeader);
        _surface.Add(header); _list.ItemTemplate = new DataTemplate(() => new TableRow(state, columns, ToggleGroup, detail => DetailSelected?.Invoke(detail)));
        Refresh(); UpdateSurfaceWidth();
    }
    public void FitColumns()
    {
        if (_state is null) return;
        var rows = _groups.SelectMany(g => g.SummaryRow is { } summary ? g.Children.Prepend(summary) : g.Children).ToArray();
        var widths = DiagnosticColumnLayout.Fit(_columns, rows, Width > 0 ? Width : 900, _flexibleColumn);
        for (var i = 0; i < widths.Length; i++) _state.Set(i, widths[i]);
        UpdateSurfaceWidth();
    }
    private void FitColumn(int column)
    {
        if (_state is null) return;
        var maximum = _groups.SelectMany(g => g.Children).Take(1500).Select(r => r.Cells[column].Length).Append(_columns[column].Length).Max();
        _state.Set(column, Math.Clamp(maximum * 6.8 + 30, 64, column == _columns.Length - 1 ? 900 : 300)); UpdateSurfaceWidth();
    }
    private void UpdateSurfaceWidth() { if (_state is { } state) _surface.WidthRequest = Math.Max(Width, DiagnosticColumnLayout.Gutter + state.Widths.Sum()); }
    private void ToggleGroup(string key) { if (!_expanded.Add(key)) _expanded.Remove(key); Refresh(); }
    private void Refresh()
    {
        var visible = new List<VisibleRow>();
        foreach (var group in _groups)
        {
            if (group.Count == 1 && group.SummaryRow is null) visible.Add(new(group.Children[0], group.Key, 1, false, false));
            else
            {
                var open = _expanded.Contains(group.Key); visible.Add(new(group.Summary(n => F("Grid.Values", n)), group.Key, group.Count, true, open));
                if (open) visible.AddRange(group.Children.Select(child => new VisibleRow(child, group.Key, 1, false, true)));
            }
        }
        var pages = Math.Max(1, (visible.Count + PageSize - 1) / PageSize); _page = Math.Clamp(_page, 0, pages - 1);
        _list.ItemsSource = visible.Skip(_page * PageSize).Take(PageSize).ToArray();
        _position.Text = F("Grid.Rows", _originalCount, _groups.Count, visible.Count == 0 ? 0 : _page * PageSize + 1, Math.Min(visible.Count, (_page + 1) * PageSize), visible.Count);
        _previous.IsEnabled = _page > 0; _next.IsEnabled = _page + 1 < pages;
    }
    private sealed record VisibleRow(DiagnosticRow Row, string Group, int Count, bool IsGroup, bool Expanded);
    private sealed class WidthState(int count)
    {
        public double[] Widths { get; } = Enumerable.Repeat(120d, count).ToArray();
        public bool Manual { get; set; }
        private readonly List<WeakReference<TableRow>> _views = [];
        public void Register(TableRow row) { _views.RemoveAll(w => !w.TryGetTarget(out _)); _views.Add(new(row)); row.ApplyWidths(Widths); }
        public void Resize(int column, double delta) => Set(column, Widths[column] + delta);
        public void Set(int column, double width)
        {
            Widths[column] = Math.Clamp(width, 48, 8000);
            foreach (var reference in _views) if (reference.TryGetTarget(out var row)) row.ApplyWidths(Widths);
        }
    }
    private sealed class TableRow : Grid
    {
        private readonly Label[] _labels;
        private readonly Button _toggle;
        private readonly Action<string>? _group;
        public TableRow(WidthState state, string[] columns, Action<string>? group, Action<string>? detail, bool header = false)
        {
            _group = group; Padding = new Thickness(4, 3); HeightRequest = header ? 40 : 36; ColumnSpacing = 0;
            BackgroundColor = Color.FromArgb(header ? "#F6F8FA" : "#FFFFFF"); ColumnDefinitions.Add(new(64));
            _toggle = new Button { Text = "", IsVisible = !header, FontSize = 11, Padding = 0, BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#0F766E"), CornerRadius = 3 };
            _toggle.Clicked += (_, _) => { if (BindingContext is VisibleRow { IsGroup: true } row) _group?.Invoke(row.Group); };
            Add(_toggle); _labels = new Label[columns.Length];
            if (header) Add(new Label { Text = T("Grid.Count"), Margin = new Thickness(6, 0), FontSize = 11, TextColor = Color.FromArgb("#64748B"), VerticalTextAlignment = TextAlignment.Center });
            for (var i = 0; i < columns.Length; i++)
            {
                ColumnDefinitions.Add(new(state.Widths[i]));
                var label = new Label { Text = header ? columns[i] : "", Margin = new Thickness(6, 0, 12, 0), FontSize = 12, TextColor = Color.FromArgb("#38576B"), VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 };
                if (header) label.FontAttributes = FontAttributes.Bold;
                Grid.SetColumn(label, i + 1); Add(label); _labels[i] = label;
            }
            var rule = new BoxView { Color = Color.FromArgb(header ? "#DCE4EB" : "#EDF1F5"), HeightRequest = 1, VerticalOptions = LayoutOptions.End };
            Grid.SetColumnSpan(rule, columns.Length + 1); Add(rule);
            if (detail is not null) { var tap = new TapGestureRecognizer(); tap.Tapped += (_, _) => { if (BindingContext is VisibleRow row) detail(row.Row.Details); }; GestureRecognizers.Add(tap); }
            state.Register(this);
        }
        public void ApplyWidths(double[] widths) { for (var i = 0; i < widths.Length; i++) ColumnDefinitions[i + 1].Width = new(widths[i]); }
        public void VerifySelectableText()
        {
            if (_labels.Any(label => label.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.TextBlock { IsTextSelectionEnabled: true }))
                throw new InvalidOperationException("ATSM-TABLE-TEXT-NOT-SELECTABLE");
        }
        protected override void OnBindingContextChanged()
        {
            base.OnBindingContextChanged(); if (BindingContext is not VisibleRow row) return;
            _toggle.Text = row.IsGroup ? (row.Expanded ? "▾ " : "▸ ") + row.Count : row.Expanded ? "↳" : "1";
            _toggle.IsEnabled = row.IsGroup;
            SemanticProperties.SetDescription(_toggle, row.IsGroup ? F(row.Expanded ? "Grid.Collapse" : "Grid.Expand", row.Count) : "");
            BackgroundColor = Color.FromArgb(row.Row.Attention ? "#FFF4DC" : row.IsGroup ? "#F1F6F8" : row.Expanded ? "#FAFBFC" : "#FFFFFF");
            for (var i = 0; i < _labels.Length; i++) { _labels[i].Text = ProtocolExplainer.SingleLine(row.Row.Cells[i]); _labels[i].FontAttributes = row.IsGroup ? FontAttributes.Bold : FontAttributes.None; }
        }
    }
}
