using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Agents;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.Agents;

/// <summary>
/// The Coding agents card on the Focus Wall, on the rail only while a Claude Code session is known: a tile with
/// how many sessions are live as a poster number, and a detail listing each session's folder, what it is doing
/// and for how long. A row brings that session's window forward when Shammy knows which one it is.
/// </summary>
internal sealed class AgentsCard
{
    static readonly Brush Backdrop = MakeField();

    readonly AgentsModule _module;
    readonly WallTile _tile = new();
    readonly TrackedText _count = CardKit.Poster("0", 34), _rest = CardKit.Label("Claude Code");
    readonly TextBlock _title, _line;
    Detail? _detail;

    public AgentsCard(AgentsModule module, Action open)
    {
        _module = module;
        _tile.Field = Backdrop;
        _tile.Art.Children.Add(CardKit.At(_count, HorizontalAlignment.Left, VerticalAlignment.Top, WallTile.Pad, WallTile.Pad));
        _tile.Rest.Children.Add(WallTile.BottomLeft(_rest));
        var words = WallTile.Words("Coding agents", "", out _title, out _line);
        _tile.Lit.Children.Add(CardKit.At(words, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, WallTile.Pad, 0, WallTile.Pad, 10));
        _tile.Lit.Children.Add(WallTile.OpenButton("Open coding agents", open));
        Open = open;
        Refresh();
    }

    public Action Open { get; }

    public FrameworkElement Compact => _tile;

    /// <summary>Waiting on the user outranks a song and a timer; plain working sits below them.</summary>
    public int Live
    {
        get
        {
            var sessions = _module.Sessions;
            return sessions.Any(s => s.State == AgentState.Waiting) ? 85 : sessions.Any(s => s.State == AgentState.Working) ? 35 : 0;
        }
    }

    public WallPeek? Peek()
    {
        var sessions = _module.Sessions;
        if (sessions.Count == 0) return null;
        var (working, waiting, done) = Counts(sessions);
        return new WallPeek("Claude Code", AgentText.Sessions(sessions.Count), AgentText.Summary(working, waiting, done), Backdrop, new WallAction("Sessions", Open));
    }

    static (int Working, int Waiting, int Done) Counts(IReadOnlyList<AgentSession> sessions) =>
        (sessions.Count(s => s.State == AgentState.Working), sessions.Count(s => s.State == AgentState.Waiting), sessions.Count(s => s.State == AgentState.Done));

    /// <summary>The sessions changed (one came, went, or changed what it is doing).</summary>
    public void Refresh()
    {
        var sessions = _module.Sessions;
        var (working, waiting, done) = Counts(sessions);
        _count.Text = (working + waiting).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _line.Text = AgentText.Summary(working, waiting, done);
        CardKit.Name(_tile, $"Coding agents: {AgentText.Sessions(sessions.Count)}, {AgentText.Summary(working, waiting, done)}");
        if (_detail is { IsLoaded: true } detail) detail.Show();
    }

    public string Describe()
    {
        var (working, waiting, done) = Counts(_module.Sessions);
        return $"tile=\"{_count.Text}\" line=\"{_line.Text}\" detail={(_detail is { IsLoaded: true } ? "open" : "closed")} summary=\"{AgentText.Summary(working, waiting, done)}\"";
    }

    static Brush MakeField()
    {
        var b = new LinearGradientBrush(CardKit.Color("#2A2430"), CardKit.Color("#131217"), new Point(0.2, 0), new Point(0.8, 1));
        b.Freeze();
        return b;
    }

    public FrameworkElement BuildDetail()
    {
        var d = new Detail(_module);
        d.Unloaded += (_, _) =>
        {
            if (_detail == d) _detail = null;
        };
        _detail = d;
        return d;
    }

    /// <summary>The list of sessions; the lines with a running time are refreshed once a second while it is on screen.</summary>
    sealed class Detail : WallDetail
    {
        readonly AgentsModule _module;
        readonly TrackedText _count = CardKit.Poster("0", 72), _under = CardKit.Label("live");
        readonly StackPanel _rows = new();
        readonly List<(string Id, TextBlock State)> _lines = new();
        string _shown = "";

        public Detail(AgentsModule module) : base(Backdrop, "Claude Code", "", "")
        {
            _module = module;
            _under.Margin = new Thickness(0, 8, 0, 0);
            ArtBottomLeft(_count, _under);
            Body.Children.Add(Scroll(_rows));
            Show();
            CardKit.TickWhileVisible(this, TimeSpan.FromSeconds(1), Tick);
        }

        public void Show()
        {
            var sessions = _module.Sessions;
            var (working, waiting, done) = Counts(sessions);
            _count.Text = (working + waiting).ToString(System.Globalization.CultureInfo.InvariantCulture);
            TitleText.Text = AgentText.Sessions(sessions.Count);
            Line.Text = AgentText.Summary(working, waiting, done);

            // Rebuild only when the set or a state changed; the clocks just get new text.
            string key = string.Join("|", sessions.Select(s => $"{s.Id}:{s.State}:{s.Failed}:{_module.CanOpen(s.Id)}"));
            if (key != _shown)
            {
                _shown = key;
                _rows.Children.Clear();
                _lines.Clear();
                foreach (var s in sessions)
                {
                    string folder = AgentText.Folder(s.Cwd);
                    var name = CardKit.Text(folder.Length > 0 ? folder : "Claude Code", 13, FontWeights.SemiBold);
                    name.TextTrimming = TextTrimming.CharacterEllipsis;
                    name.VerticalAlignment = VerticalAlignment.Center;
                    var state = CardKit.Text("", 12.5, FontWeights.Medium, secondary: s.State != AgentState.Waiting);
                    var row = ListRow(Bullet(), name, state);
                    string id = s.Id;
                    if (_module.CanOpen(id))
                    {
                        row.Cursor = Cursors.Hand;
                        row.ToolTip = "Bring it forward";
                        row.MouseLeftButtonUp += (_, _) => _module.OpenWindow(id);
                    }
                    _rows.Children.Add(row);
                    _lines.Add((id, state));
                }
            }
            Tick();
        }

        void Tick()
        {
            var now = _module.Now;
            foreach (var (id, state) in _lines)
            {
                if (_module.Session(id) is not { } s) continue;
                string text = AgentText.StateLine(s, now);
                if (state.Text != text) state.Text = text;
            }
        }
    }
}
