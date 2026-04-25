using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Mnemos.Core;
using Mnemos.Database;
using Mnemos.Mcp;
using Mnemos.Universe;

namespace Mnemos.App;

/// <summary>
/// Main application window: initializes services, hosts the WebView2 control,
/// and responds to commands from the React frontend via <c>postMessage</c>.
/// </summary>
public partial class MainWindow : Window
{
    // ---------- Services ----------

    private MnemosDb? _db;
    private EmbeddingEngine? _embedder;
    private GraphBuilder? _graphBuilder;
    private UmapEngine? _umapEngine;
    private System.Diagnostics.Process? _mcpProcess;

    // ---------- Graph state ----------

    private GraphPayload? _graph;
    private bool _isBuildingGraph;
    private bool _webviewReady;

    // ---------- Win32 : borderless + resize ----------

    private const int WM_NCHITTEST      = 0x84;
    private const int HTLEFT = 10, HTRIGHT = 11;
    private const int HTTOP = 12, HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14, HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION       = 0x2;

    private const int GWL_STYLE   = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint WS_CAPTION        = 0x00C00000;
    private const uint WS_THICKFRAME     = 0x00040000;
    private const uint WS_BORDER         = 0x00800000;
    private const uint WS_DLGFRAME       = 0x00400000;
    private const uint WS_EX_CLIENTEDGE  = 0x00000200;
    private const uint WS_EX_WINDOWEDGE  = 0x00000100;
    private const uint SWP_FRAMECHANGED  = 0x0020;
    private const uint SWP_NOMOVE        = 0x0002;
    private const uint SWP_NOSIZE        = 0x0001;
    private const uint SWP_NOZORDER      = 0x0004;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    // ---------- Initialization ----------

    /// <summary>
    /// Initializes the window, services, WebView, and auto-configures
    /// the MCP server entry in Claude Desktop's config.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        SetThemeColors(false);
        this.Closed += (s, e) => Environment.Exit(0);
        Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude"));
        AutoConfigureMcp();

        try
        {
            string claude = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Claude");

            _db           = new MnemosDb(Path.Combine(claude, "mnemos.db"));
            _embedder     = new EmbeddingEngine(Path.Combine(claude, "Models", "minilm"), Log);
            _graphBuilder = new GraphBuilder(_db, Log);
            _umapEngine   = new UmapEngine(_db, Log);

            McpTools.Init(_db, _embedder);

            Log("Mnemos initialized");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Initialization error: {ex.Message}");
        }

        _ = InitializeWebViewAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // Strip all native Windows borders (caption, border, edge)
        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
        SetWindowLong(hwnd, GWL_STYLE, style);

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle &= ~(WS_EX_CLIENTEDGE | WS_EX_WINDOWEDGE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        // Force Windows to re-measure the non-client area
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER);

        HwndSource.FromHwnd(hwnd)?.AddHook(HwndHook);
    }

    // ---------- WebView ----------

    private async Task InitializeWebViewAsync()
    {
        await webView.EnsureCoreWebView2Async();

        string wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "mnemos.app", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
            webView.Source = new Uri("https://mnemos.app/index.html");
        }
        else
        {
            // Dev mode — Vite dev server
            webView.Source = new Uri("http://localhost:5173");
        }

        webView.CoreWebView2.WebMessageReceived += OnMessageReceived;
        _webviewReady = true;
        Log("WebView ready");

        _ = BuildGraphAsync();
        _ = ComputeUmapAsync();
    }

    // ---------- Message handler ----------

    /// <summary>
    /// Dispatches incoming messages from the React frontend to the appropriate handler.
    /// </summary>
    private void OnMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(msg)) return;

        switch (msg)
        {
            case "minimize":
                Dispatcher.Invoke(async () =>
                {
                    var anim = new DoubleAnimation(1, 0.95, TimeSpan.FromMilliseconds(150));
                    anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
                    RenderTransform = new ScaleTransform(1, 1, ActualWidth / 2, ActualHeight);
                    RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                    await Task.Delay(120);
                    WindowState = WindowState.Minimized;
                    RenderTransform = null;
                });
                return;

            case "maximize":
                Dispatcher.Invoke(ToggleMaximize);
                return;

            case "get_mcp_status":
                if (_db != null)
                {
                    var (_, msgs) = _db.GetStats();
                    Post($"mcp_status|{(msgs > 0 ? "connected" : "disconnected")}");
                }
                return;

            case "close":
                Dispatcher.Invoke(() => Application.Current.Shutdown());
                return;

            case "drag":
                DragWindow();
                return;

            case "get_filters":
                HandleGetFilters();
                return;

            case "get_graph_data":
                HandleGetGraphData();
                return;

            case "rebuild_graph":
                _ = RebuildGraphAsync();
                return;

            case "get_universe_points":
                HandleGetUniversePoints();
                return;

            case "compute_umap":
                _ = ComputeUmapAsync();
                return;

            case "get_recent_stored":
                HandleGetRecentStored();
                return;
        }

        if (msg.StartsWith("search:"))
        {
            HandleSearch(msg[7..]);
            return;
        }

        if (msg.StartsWith("theme:"))
        {
            Dispatcher.Invoke(() => SetThemeColors(msg[6..] == "dark"));
            return;
        }

        if (msg.StartsWith("load_conv:"))
        {
            HandleLoadConv(msg[10..]);
            return;
        }
    }

    // ---------- Handlers ----------

    private void HandleSearch(string content)
    {
        try
        {
            if (_db == null || _embedder == null)
                throw new InvalidOperationException("Not initialized");

            string query  = content;
            string filter = "all";

            if (content.Contains("|filter:"))
            {
                var parts = content.Split("|filter:", 2);
                query  = parts[0];
                filter = parts[1];
            }

            var raw  = McpTools.search_hybrid_filtered(query, filter, 20);
            string json = raw is string s ? s : JsonSerializer.Serialize(raw);
            Post($"search_results|{json}");
        }
        catch
        {
            Post("search_results|[]");
        }
    }

    private void HandleLoadConv(string payload)
    {
        try
        {
            if (_db == null)
                throw new InvalidOperationException("DB not initialized");

            var parts   = payload.Split('|', 2);
            var cid     = parts[0];
            var target  = parts.Length > 1 ? parts[1] : "";

            var msgs = _db.GetRecentMessages(cid, 100)
                .OrderBy(m => m.Index)
                .Select(m => new
                {
                    id       = m.Uuid,
                    role     = m.Sender == "human" ? "User" : "Assistant",
                    content  = m.Text,
                    thinking = m.Thinking,
                    date     = m.CreatedAt
                });

            Post($"conv_data|{JsonSerializer.Serialize(new
            {
                conversation_id = cid,
                target_snippet  = target,
                messages        = msgs
            })}");
        }
        catch (Exception ex)
        {
            Log($"HandleLoadConv: {ex.Message}");
        }
    }

    private void HandleGetFilters()
    {
        try
        {
            if (_db == null) throw new InvalidOperationException();
            Post($"filters_data|{JsonSerializer.Serialize(_db.GetAvailableFilters())}");
        }
        catch
        {
            Post("filters_data|[]");
        }
    }

    // ---------- Graph ----------

    private async Task BuildGraphAsync()
    {
        if (_isBuildingGraph || _graphBuilder == null) return;
        _isBuildingGraph = true;
        _graph = null;
        _graph = await _graphBuilder.BuildAsync(p => NotifyProgress(p));
        _isBuildingGraph = false;
        if (_graph != null) SendGraph();
    }

    private async Task RebuildGraphAsync()
    {
        _graph = null;
        await BuildGraphAsync();
    }

    private void SendGraph()
    {
        if (_graph == null || _db == null) return;
        var (convs, msgs) = _db.GetStats();
        var payload = _graph with
        {
            stats = new GraphStats(convs, msgs, _db.GetEmbeddedCount())
        };
        Post($"graph_data|{JsonSerializer.Serialize(payload)}");
    }

    private void HandleGetGraphData()
    {
        if (_graph != null)
        {
            SendGraph();
            return;
        }
        try
        {
            var (convs, msgs) = _db!.GetStats();
            int embedded = _db.GetEmbeddedCount();
            Post($"graph_data|{JsonSerializer.Serialize(new
            {
                nodes   = Array.Empty<object>(),
                links   = Array.Empty<object>(),
                loading = _isBuildingGraph,
                stats   = new { conversations = convs, messages = msgs, embedded }
            })}");
        }
        catch (Exception ex)
        {
            Log($"HandleGetGraphData: {ex.Message}");
        }
    }

    private void HandleGetRecentStored()
    {
        try
        {
            if (_db == null) return;
            var recent = _db.GetRecentStored(20);
            var payload = recent.Select(r => new
            {
                r.ConvUuid,
                r.Sender,
                r.Preview,
                r.ConvName
            });
            Post($"recent_stored|{JsonSerializer.Serialize(payload)}");
        }
        catch (Exception ex)
        {
            Log($"HandleGetRecentStored: {ex.Message}");
            Post("recent_stored|[]");
        }
    }

    private void HandleGetUniversePoints()
    {
        try
        {
            if (_umapEngine == null || _db == null)
                throw new InvalidOperationException("UmapEngine or DB not initialized");

            var points          = _umapEngine.GetPoints();
            var (convs, totalMsgs) = _db.GetStats();

            var payload = new
            {
                Points        = points,
                TotalMessages = totalMsgs,
                TotalConvs    = convs
            };

            Post($"universe_points|{JsonSerializer.Serialize(payload)}");
            Log($"{points.Count}/{totalMsgs} UMAP points sent");
        }
        catch (Exception ex)
        {
            Log($"HandleGetUniversePoints: {ex.Message}");
            Post("universe_points|null");
        }
    }

    private async Task ComputeUmapAsync()
    {
        if (_umapEngine == null) return;

        try
        {
            int computed = await _umapEngine.ComputeIncrementalAsync((current, total) =>
            {
                if (total > 0)
                    NotifyProgress(new BuildProgress("umap", current, total,
                        $"UMAP projection... {current}/{total}"));
            });

            if (computed > 0)
            {
                Log($"UMAP: {computed} new points computed");
                Post($"umap_ready|{computed}");
            }
        }
        catch (Exception ex)
        {
            Log($"ComputeUmapAsync: {ex.Message}");
        }
    }

    private void NotifyProgress(BuildProgress p)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_webviewReady && webView?.CoreWebView2 != null)
                Post($"graph_progress|{JsonSerializer.Serialize(p)}");
        }, DispatcherPriority.Background);
    }

    // ---------- Win32 helpers ----------

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void DragWindow()
    {
        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        var mouse = new Point(
            (int)(lParam.ToInt64() & 0xFFFF),
            (int)((lParam.ToInt64() >> 16) & 0xFFFF));
        var pt = PointFromScreen(mouse);

        var (w, h, b) = (ActualWidth, ActualHeight, 8);
        bool L = pt.X <= b, R = pt.X >= w - b,
             T = pt.Y <= b, B = pt.Y >= h - b;

        if (T && L) { handled = true; return (IntPtr)HTTOPLEFT; }
        if (T && R) { handled = true; return (IntPtr)HTTOPRIGHT; }
        if (B && L) { handled = true; return (IntPtr)HTBOTTOMLEFT; }
        if (B && R) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
        if (T)      { handled = true; return (IntPtr)HTTOP; }
        if (B)      { handled = true; return (IntPtr)HTBOTTOM; }
        if (L)      { handled = true; return (IntPtr)HTLEFT; }
        if (R)      { handled = true; return (IntPtr)HTRIGHT; }

        return IntPtr.Zero;
    }

    // ---------- Theme ----------

    private void SetThemeColors(bool isDark)
    {
        if (isDark)
        {
            webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 2, 6, 23);
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(2, 6, 23));
        }
        else
        {
            webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 248, 250, 252);
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252));
        }
    }

    // ---------- MCP server management ----------


    /// <summary>
    /// Writes the Mnemos MCP server entry to Claude Desktop's configuration file safely.
    /// </summary>
    private void AutoConfigureMcp()
    {
        try
        {
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Claude", "claude_desktop_config.json");

            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mnemos.exe");

            Log($"configPath: {configPath}");
            Log($"exePath: {exePath}");
            Log($"config exists: {File.Exists(configPath)}");
            Log($"exe exists: {File.Exists(exePath)}");

            System.Text.Json.Nodes.JsonObject configObj;

            if (File.Exists(configPath))
            {
                string rawJson = File.ReadAllText(configPath);
                Log($"raw config: {rawJson}");
            
                var documentOptions = new JsonDocumentOptions 
                { 
                    AllowTrailingCommas = true, 
                    CommentHandling = JsonCommentHandling.Skip 
                };
            
                var node = System.Text.Json.Nodes.JsonNode.Parse(rawJson, null, documentOptions);
                configObj = node?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
            }
            else
            {
                Log("config not found, creating new");
                configObj = new System.Text.Json.Nodes.JsonObject();
            }

            if (!configObj.ContainsKey("mcpServers"))
                configObj.Add("mcpServers", new System.Text.Json.Nodes.JsonObject());

            configObj["mcpServers"]!["mnemos"] = new System.Text.Json.Nodes.JsonObject
            {
                ["command"] = exePath
            };

            var writeOptions = new JsonSerializerOptions { WriteIndented = true };
            string final = configObj.ToJsonString(writeOptions);
            Log($"🔧 writing: {final}");
            File.WriteAllText(configPath, final);

            Log("MCP auto-configured");
        }
        catch (Exception ex)
        {
            Log($"AutoConfigureMcp: {ex.GetType().Name}: {ex.Message}");
            Log($"Stack: {ex.StackTrace}");
        }
    }

    // ---------- Utilities ----------

    /// <summary>
    /// Posts a string message to the WebView2 control on the UI thread.
    /// </summary>
    private void Post(string message)
    {
        if (!_webviewReady) return;

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (webView?.CoreWebView2 == null) return;
                webView.CoreWebView2.PostWebMessageAsString(message);
            }
            catch (Exception ex)
            {
                Log($"Post error: {ex.Message}");
            }
        }, DispatcherPriority.Background);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Maximized)
        {
            // Compensate for Windows overflow when maximized borderless
            webView.Margin = new Thickness(
                SystemParameters.WindowResizeBorderThickness.Left +
                SystemParameters.WindowNonClientFrameThickness.Left,
                SystemParameters.WindowResizeBorderThickness.Top +
                SystemParameters.WindowNonClientFrameThickness.Top,
                SystemParameters.WindowResizeBorderThickness.Right +
                SystemParameters.WindowNonClientFrameThickness.Right,
                SystemParameters.WindowResizeBorderThickness.Bottom +
                SystemParameters.WindowNonClientFrameThickness.Bottom
            );
        }
        else
        {
            webView.Margin = new Thickness(0);
        }
    }

    /// <summary>Writes a diagnostic message to the debug output.</summary>
    private void Log(string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[MNEMOS] {msg}");
        try
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Claude", "mnemos-app.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        }
        catch { }
    }
}