using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using System.Text;
using Velopack;
using Velopack.Sources;

namespace ARC_Sight
{
    public partial class MainWindow : Window
    {
        public static string AppVersion { get; } = "1.3.2";

        private const string NOTE_URL = "https://raw.githubusercontent.com/rodafux/ARC-Sight/refs/heads/Default/msg.ini";
        private const string API_URL = "https://metaforge.app/api/arc-raiders/events-schedule";
        private const string HEARTBEAT_URL = "https://arc-sight-stats-viewer.onrender.com/ping";
        private const string GITHUB_REPO_URL = "https://github.com/rodafux/ARC-Sight";
        private const string GITHUB_RELEASE_API = "https://api.github.com/repos/rodafux/ARC-Sight/releases/tags/";

        public static string AppDataPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ARC-Sight");
        public static string ConfigFile { get; } = Path.Combine(AppDataPath, "config.ini");
        public static string LanguagesDir { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "languages");

        private readonly HttpClient _client = new HttpClient()
        {
            DefaultRequestHeaders = { { "User-Agent", "ARC-Sight/1.0" } }
        };
        private DispatcherTimer? _uiTimer;
        private DispatcherTimer? _apiTimer;
        private IntPtr _windowHandle;
        private DateTime? _lastSuccessfulFetchUtc;
        private bool _lastFetchHadError = false;
        private bool _isFetchInProgress = false;
        private const int ApiStaleSeconds = 90;

        private static MediaPlayer _mediaPlayer = new MediaPlayer();
        private static readonly HttpClient _webhookClient = new HttpClient()
        {
            DefaultRequestHeaders = { { "User-Agent", "ARC-Sight/1.0" } }
        };
        private Velopack.UpdateInfo? _updateInfo;
        private bool _isWindowLocked = true;

        private bool _isDragging = false;
        private Point _dragOffset;
        private string _plannerSortMode = "active";

        private TabControl? MainTabControlRef => FindName("MainTabControl") as TabControl;
        private ComboBox? PlannerSortComboRef => FindName("PlannerSortCombo") as ComboBox;
        private TextBlock? StatusTextRef => FindName("StatusText") as TextBlock;
        private TextBlock? ApiHealthTextRef => FindName("ApiHealthText") as TextBlock;
        private TextBlock? NoteTextRef => FindName("NoteText") as TextBlock;
        private Button? LockBtnRef => FindName("LockBtn") as Button;
        private Grid? LoadingPanelRef => FindName("LoadingPanel") as Grid;
        private Hardcodet.Wpf.TaskbarNotification.TaskbarIcon? TrayIconRef => FindName("TrayIcon") as Hardcodet.Wpf.TaskbarNotification.TaskbarIcon;

        public ObservableCollection<TabViewModel> Tabs { get; set; } = new ObservableCollection<TabViewModel>();

        public ImageSource? AppLogo { get; set; }

        public static string Hotkey { get; set; } = "F9";
        public static int NotifySeconds { get; set; } = 300;
        public static bool SoundEnabled { get; set; } = true;
        public static bool ShowLocalTime { get; set; } = false;
        public static string CurrentLanguage { get; set; } = "en";
        public static string LastSeenVersion { get; set; } = "v0.0.0";
        public static string DiscordWebhookUrl { get; set; } = "";
        public static bool DiscordWebhookEnabled { get; set; } = false;

        public static readonly Dictionary<string, string> Translations = new Dictionary<string, string>();

        public MainWindow()
        {
            Application.LoadComponent(this, new Uri("MainWindow.xaml", UriKind.Relative));
            LoadConfig();
            LoadLanguage();
            LoadSoundFile();
            LoadLogoSafe();
            LoadTrayIcon();

            this.DataContext = this;

            if (MainTabControlRef != null) MainTabControlRef.ItemsSource = Tabs;
            this.Loaded += MainWindow_Loaded;

            this.MouseMove += MainWindow_MouseMove;
            this.MouseLeftButtonUp += MainWindow_MouseLeftButtonUp;

            if (PlannerSortComboRef?.SelectedItem is ComboBoxItem selected)
            {
                _plannerSortMode = selected.Tag?.ToString() ?? "active";
            }
        }

        private void LoadTrayIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "logo.ico");
                if (File.Exists(iconPath))
                {
                    if (TrayIconRef != null) TrayIconRef.Icon = new System.Drawing.Icon(iconPath);
                }
            }
            catch { }
        }

        private void LoadLogoSafe()
        {
            try
            {
                AppLogo = new BitmapImage(new Uri("pack://application:,,,/assets/logo.png"));
            }
            catch
            {
                try
                {
                    string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "logo.png");
                    if (File.Exists(localPath))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(localPath, UriKind.Absolute);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        AppLogo = bitmap;
                    }
                }
                catch { }
            }
        }

        private void LoadSoundFile()
        {
            try
            {
                string assetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");
                string mp3Path = Path.Combine(assetsPath, "Notif.mp3");
                string wavPath = Path.Combine(assetsPath, "Notif.wav");

                string finalPath = "";
                if (File.Exists(mp3Path)) finalPath = mp3Path;
                else if (File.Exists(wavPath)) finalPath = wavPath;

                if (!string.IsNullOrEmpty(finalPath))
                {
                    _mediaPlayer.Open(new Uri(finalPath, UriKind.Absolute));
                }
            }
            catch { }
        }

        private async Task StartHeartbeat()
        {
            while (true)
            {
                try
                {
                    var payload = new { version = AppVersion };
                    var json = JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var request = new HttpRequestMessage(HttpMethod.Post, HEARTBEAT_URL);
                    request.Headers.Add("User-Agent", "ARC-Sight-Desktop-Client/1.0");
                    request.Content = content;

                    await _client.SendAsync(request);
                }
                catch { }
                await Task.Delay(60000);
            }
        }

        private async Task FetchNote()
        {
            try
            {
                string url = $"{NOTE_URL}?t={DateTime.UtcNow.Ticks}";
                var content = await _client.GetStringAsync(url);

                if (!string.IsNullOrWhiteSpace(content))
                {
                    var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    string targetKey = CurrentLanguage.ToUpper() + "=";
                    string message = "";

                    foreach (var line in lines)
                    {
                        if (line.StartsWith(targetKey))
                        {
                            message = line.Substring(targetKey.Length).Trim();
                            break;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        string header = GetTrans("note_header", "UI");
                        if (string.IsNullOrEmpty(header)) header = "NOTE IMPORTANTE :";
                        if (NoteTextRef != null)
                        {
                            NoteTextRef.Text = $"{header} {message}";
                            NoteTextRef.Visibility = Visibility.Visible;
                        }
                    }
                    else
                    {
                        if (NoteTextRef != null) NoteTextRef.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch { if (NoteTextRef != null) NoteTextRef.Visibility = Visibility.Collapsed; }
        }

        private async Task CheckForUpdates()
        {
            try
            {
#if DEBUG
#else
                var mgr = new UpdateManager(new GithubSource(GITHUB_REPO_URL, null, false));
                var newVersion = await mgr.CheckForUpdatesAsync();
                if (newVersion != null)
                {
                    _updateInfo = newVersion;
                    this.Dispatcher.Invoke(() =>
                    {
                        UpdateBtn.Content = GetTrans("update_available_button", "UI");
                        UpdateBtn.Visibility = Visibility.Visible;
                    });
                }
#endif
            }
            catch { }
        }

        private async void UpdateBtn_Click(object sender, RoutedEventArgs e)
        {
#if DEBUG
            MessageBox.Show("Update simulation in DEBUG mode.");
#else
            if (_updateInfo == null) return;
            try
            {
                UpdateBtn.Visibility = Visibility.Collapsed;
                UpdateProgressPanel.Visibility = Visibility.Visible;
                UpdateBtn.IsEnabled = false;

                var mgr = new UpdateManager(new GithubSource(GITHUB_REPO_URL, null, false));
                Action<int> progressAction = percent => this.Dispatcher.Invoke(() => UpdateProgressBar.Value = percent);

                await mgr.DownloadUpdatesAsync(_updateInfo, progressAction);
                mgr.ApplyUpdatesAndRestart(_updateInfo);
            }
            catch
            {
                UpdateBtn.Content = GetTrans("update_error", "UI");
                UpdateBtn.IsEnabled = true;
                UpdateBtn.Visibility = Visibility.Visible;
                UpdateProgressPanel.Visibility = Visibility.Collapsed;
            }
#endif
        }

        public static void TriggerNotification(AlertNotification notification)
        {
            string title = notification.Title;
            string message = notification.Message;

            if (SoundEnabled) { try { _mediaPlayer.Stop(); _mediaPlayer.Play(); } catch { } }
            Application.Current.Dispatcher.Invoke(() => { try { new ToastWindow(title, message).Show(); } catch { } });

            if (DiscordWebhookEnabled && !string.IsNullOrWhiteSpace(DiscordWebhookUrl))
            {
                _ = SendDiscordWebhookAsync(notification);
            }
        }

        private static async Task SendDiscordWebhookAsync(AlertNotification notification)
        {
            try
            {
                if (!Uri.TryCreate(DiscordWebhookUrl.Trim(), UriKind.Absolute, out var webhookUri)) return;

                var localStart = DateTimeOffset.FromUnixTimeMilliseconds(notification.RawEvent.startTime).LocalDateTime;
                var utcStart = DateTimeOffset.FromUnixTimeMilliseconds(notification.RawEvent.startTime).UtcDateTime;

                var fields = new object[]
                {
                    new { name = "Event", value = notification.EventName, inline = true },
                    new { name = "Map", value = notification.MapName, inline = true },
                    new { name = "Starts (Local)", value = localStart.ToString("yyyy-MM-dd HH:mm:ss"), inline = false },
                    new { name = "Starts (UTC)", value = utcStart.ToString("yyyy-MM-dd HH:mm:ss"), inline = false }
                };

                var embed = new Dictionary<string, object?>
                {
                    ["title"] = notification.Title,
                    ["description"] = notification.Message,
                    ["color"] = 0xFF5500,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["fields"] = fields,
                    ["footer"] = new { text = "ARC-Sight Alert" }
                };

                if (!string.IsNullOrWhiteSpace(notification.ImageUrl) &&
                    Uri.TryCreate(notification.ImageUrl, UriKind.Absolute, out _))
                {
                    embed["thumbnail"] = new { url = notification.ImageUrl };
                }

                var payload = new
                {
                    username = "ARC-Sight",
                    embeds = new[] { embed }
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _webhookClient.PostAsync(webhookUri, content);
            }
            catch { }
        }

        public static async Task<bool> SendTestDiscordWebhookAsync(string webhookUrl)
        {
            try
            {
                if (!Uri.TryCreate(webhookUrl.Trim(), UriKind.Absolute, out var webhookUri)) return false;

                var payload = new
                {
                    username = "ARC-Sight",
                    embeds = new[]
                    {
                        new
                        {
                            title = "ARC-Sight Webhook Test",
                            description = "Webhook is configured correctly.",
                            color = 0xFF5500,
                            timestamp = DateTimeOffset.UtcNow.ToString("O"),
                            fields = new object[]
                            {
                                new { name = "Status", value = "Connected", inline = true },
                                new { name = "App Version", value = AppVersion, inline = true }
                            },
                            footer = new { text = "ARC-Sight Test" }
                        }
                    }
                };

                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _webhookClient.PostAsync(webhookUri, content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            this.Left = 0; this.Top = 0; this.Width = SystemParameters.PrimaryScreenWidth;
            UpdateLocalizedUI();

            _windowHandle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(_windowHandle, GWL_STYLE);
            SetWindowLong(_windowHandle, GWL_STYLE, style & ~WS_MAXIMIZEBOX);

            HwndSource? source = HwndSource.FromHwnd(_windowHandle);
            source?.AddHook(HwndHook);
            RegisterHotKey(_windowHandle, 1, 0, GetVkCode(Hotkey));

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += (s, ev) => { UpdateAllTimers(); UpdateApiHealthIndicator(); };
            _uiTimer.Start();

            _apiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _apiTimer.Tick += async (s, ev) => { await FetchData(); await FetchNote(); };
            _apiTimer.Start();

            await InitialLoad();
            _ = StartHeartbeat();
            _ = CheckForUpdates();
        }

        private async Task InitialLoad()
        {
            if (StatusTextRef != null) StatusTextRef.Text = "Loading...";
            await FetchData();
            await FetchNote();
            await CheckAndShowChangelog();
        }

        private async Task CheckAndShowChangelog()
        {
            if (AppVersion != LastSeenVersion)
            {
                await FetchAndShowChangelogData(AppVersion);
                LastSeenVersion = AppVersion;
                SaveConfig();
            }
        }

        public async Task FetchAndShowChangelogData(string versionTag)
        {
            try
            {
                string url = $"{GITHUB_RELEASE_API}{versionTag}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "ARC-Sight-App");

                var response = await _client.SendAsync(request);
                string notes = "No details available.";

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("body", out var bodyElement))
                        {
                            notes = bodyElement.GetString() ?? "No content.";
                        }
                    }
                }
                else
                {
                    notes = $"Could not fetch patch notes for {versionTag}.\n(GitHub API limit or invalid tag)";
                }

                ChangelogWindow cw = new ChangelogWindow(notes);
                cw.Owner = this;
                cw.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error fetching notes: {ex.Message}");
            }
        }

        private void UpdateLocalizedUI()
        {
            string tooltip = GetTrans("lock_tooltip", "UI");
            if (string.IsNullOrEmpty(tooltip)) tooltip = "Lock / Unlock window position";
            if (LockBtnRef != null) LockBtnRef.ToolTip = tooltip;
        }

        private void ListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ListBox listBox && e.Delta != 0)
            {
                var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
                if (scrollViewer != null)
                {
                    double scrollAmount = 200; // pixels per scroll
                    double targetOffset = scrollViewer.HorizontalOffset + (e.Delta > 0 ? -scrollAmount : scrollAmount);
                    targetOffset = Math.Max(0, Math.Min(targetOffset, scrollViewer.ScrollableWidth));

                    // Animate smooth scrolling
                    AnimateScroll(scrollViewer, targetOffset);
                    e.Handled = true;
                }
            }
        }

        private void AnimateScroll(ScrollViewer scrollViewer, double targetOffset)
        {
            var animation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = scrollViewer.HorizontalOffset,
                To = targetOffset,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            var storyboard = new System.Windows.Media.Animation.Storyboard();
            storyboard.Children.Add(animation);
            System.Windows.Media.Animation.Storyboard.SetTarget(animation, scrollViewer);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(animation,
                new PropertyPath(ScrollViewerBehavior.HorizontalOffsetProperty));
            storyboard.Begin();
        }

        private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null) return childOfChild;
            }
            return null;
        }

        private async Task FetchData()
        {
            _isFetchInProgress = true;
            UpdateApiHealthIndicator();

            try
            {
                if (StatusTextRef != null) StatusTextRef.Text = "Updating...";
                if (Tabs.Count == 0)
                {
                    if (LoadingPanelRef != null) LoadingPanelRef.Visibility = Visibility.Visible;
                    if (MainTabControlRef != null) MainTabControlRef.Visibility = Visibility.Collapsed;
                }

                var json = await _client.GetStringAsync(API_URL);

                var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                List<ScheduleEvent>? rawEvents = null;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    rawEvents = JsonSerializer.Deserialize<List<ScheduleEvent>>(json);
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("events", out var eventsElem))
                        rawEvents = JsonSerializer.Deserialize<List<ScheduleEvent>>(eventsElem.GetRawText());
                    else if (root.TryGetProperty("data", out var dataElem))
                        rawEvents = JsonSerializer.Deserialize<List<ScheduleEvent>>(dataElem.GetRawText());
                }

                if (rawEvents != null && rawEvents.Count > 0)
                {
                    ProcessScheduleData(rawEvents);
                    if (StatusTextRef != null) StatusTextRef.Text = "";
                    _lastSuccessfulFetchUtc = DateTime.UtcNow;
                    _lastFetchHadError = false;
                }
                else
                {
                    if (StatusTextRef != null) StatusTextRef.Text = "No events found";
                    _lastSuccessfulFetchUtc = DateTime.UtcNow;
                    _lastFetchHadError = false;
                }

                if (LoadingPanelRef != null) LoadingPanelRef.Visibility = Visibility.Collapsed;
                if (MainTabControlRef != null) MainTabControlRef.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                if (StatusTextRef != null) StatusTextRef.Text = "API Error";
                _lastFetchHadError = true;
                if (LoadingPanelRef != null) LoadingPanelRef.Visibility = Visibility.Collapsed;
                if (MainTabControlRef != null) MainTabControlRef.Visibility = Visibility.Visible;
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
            finally
            {
                _isFetchInProgress = false;
                UpdateApiHealthIndicator();
            }
        }

        private void UpdateApiHealthIndicator()
        {
            if (ApiHealthTextRef == null) return;

            string text;
            Brush color;

            if (_isFetchInProgress)
            {
                text = "API: syncing...";
                color = Brushes.DeepSkyBlue;
            }
            else if (_lastSuccessfulFetchUtc == null)
            {
                if (_lastFetchHadError)
                {
                    text = "API: error";
                    color = Brushes.OrangeRed;
                }
                else
                {
                    text = "API: waiting...";
                    color = Brushes.Gray;
                }
            }
            else
            {
                int ageSeconds = Math.Max(0, (int)(DateTime.UtcNow - _lastSuccessfulFetchUtc.Value).TotalSeconds);

                if (_lastFetchHadError)
                {
                    text = $"API: error • last ok {ageSeconds}s ago";
                    color = Brushes.OrangeRed;
                }
                else if (ageSeconds <= ApiStaleSeconds)
                {
                    text = $"API: live • updated {ageSeconds}s ago";
                    color = Brushes.LightGreen;
                }
                else
                {
                    text = $"API: stale • updated {ageSeconds}s ago";
                    color = Brushes.Gold;
                }
            }

            ApiHealthTextRef.Text = text;
            ApiHealthTextRef.Foreground = color;
        }

        private void ProcessScheduleData(List<ScheduleEvent> schedule)
        {
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var processedData = new List<EventDisplayData>();

            var groups = schedule.GroupBy(e => new { e.name, e.map });

            foreach (var group in groups)
            {
                var active = group.FirstOrDefault(e => e.startTime <= nowUnix && e.endTime > nowUnix);

                if (active != null)
                {
                    processedData.Add(new EventDisplayData(active));
                }
                else
                {
                    var next = group.Where(e => e.startTime > nowUnix).OrderBy(e => e.startTime).FirstOrDefault();
                    if (next != null)
                    {
                        processedData.Add(new EventDisplayData(next));
                    }
                }
            }

            UpdateUiWithProcessedData(processedData);
        }

        private void UpdateUiWithProcessedData(List<EventDisplayData> data)
        {
            var allTab = Tabs.FirstOrDefault(t => t.Header == "ALL");
            if (allTab == null) { allTab = new TabViewModel("ALL"); Tabs.Insert(0, allTab); }

            MergeCards(allTab.Cards, data);
            ApplyPlannerSort(allTab);

            var grouped = data.GroupBy(e => e.Raw.name).OrderBy(g => g.Key);
            foreach (var group in grouped)
            {
                string tabName = GetTrans(group.Key ?? "Unknown", "TABS");
                var tab = Tabs.FirstOrDefault(t => t.Header == tabName);
                if (tab == null) { tab = new TabViewModel(tabName); Tabs.Add(tab); }
                MergeCards(tab.Cards, group.ToList());
                ApplyPlannerSort(tab);
            }
        }

        private void PlannerSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlannerSortComboRef?.SelectedItem is ComboBoxItem selected)
            {
                _plannerSortMode = selected.Tag?.ToString() ?? "active";
                ApplyPlannerSortToAllTabs();
            }
        }

        private void ApplyPlannerSortToAllTabs()
        {
            foreach (var tab in Tabs)
            {
                ApplyPlannerSort(tab);
            }
        }

        private void ApplyPlannerSort(TabViewModel tab)
        {
            if (tab?.SortedCards == null) return;

            tab.SortedCards.SortDescriptions.Clear();

            if (_plannerSortMode == "soonest")
            {
                tab.SortedCards.SortDescriptions.Add(new SortDescription(nameof(CardViewModel.TargetTime), ListSortDirection.Ascending));
                tab.SortedCards.SortDescriptions.Add(new SortDescription(nameof(CardViewModel.IsActive), ListSortDirection.Descending));
            }
            else if (_plannerSortMode == "map")
            {
                tab.SortedCards.SortDescriptions.Add(new SortDescription(nameof(CardViewModel.MapSortKey), ListSortDirection.Ascending));
                tab.SortedCards.SortDescriptions.Add(new SortDescription(nameof(CardViewModel.TargetTime), ListSortDirection.Ascending));
            }
            else
            {
                tab.SortedCards.SortDescriptions.Add(new SortDescription(nameof(CardViewModel.IsActive), ListSortDirection.Descending));
                tab.SortedCards.SortDescriptions.Add(new SortDescription(nameof(CardViewModel.TargetTime), ListSortDirection.Ascending));
            }

            tab.SortedCards.Refresh();
        }

        private void MergeCards(ObservableCollection<CardViewModel> collection, List<EventDisplayData> newEvents)
        {
            // Build lookup dictionaries for O(1) access
            var newEventKeys = new HashSet<(string?, string?)>(
                newEvents.Select(e => (e.Raw.name, e.Raw.map)));
            var existingCards = collection.ToDictionary(
                c => (c.RawData.name, c.RawData.map), c => c);

            // Remove cards not in new events
            for (int i = collection.Count - 1; i >= 0; i--)
            {
                var card = collection[i];
                if (!newEventKeys.Contains((card.RawData.name, card.RawData.map)))
                {
                    collection.RemoveAt(i);
                }
            }

            // Update existing or add new cards
            foreach (var evt in newEvents)
            {
                var key = (evt.Raw.name, evt.Raw.map);
                if (existingCards.TryGetValue(key, out var existing))
                {
                    existing.UpdateData(evt.Raw);
                }
                else
                {
                    var newCard = new CardViewModel(evt.Raw);
                    newCard.RequestNotification += TriggerNotification;
                    collection.Add(newCard);
                }
            }
        }

        private void UpdateAllTimers()
        {
            foreach (var tab in Tabs) foreach (var card in tab.Cards) card.UpdateTimer();
        }

        public static string GetTrans(string key, string section)
        {
            if (string.IsNullOrEmpty(key)) return "";

            string k = key.Replace(" ", "_").ToLower().Trim();

            return Translations.TryGetValue(k, out var value) ? value : key.ToUpper();
        }

        private void LoadConfig()
        {
            if (File.Exists(ConfigFile))
            {
                foreach (var line in File.ReadAllLines(ConfigFile))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    if (key == "hotkey") Hotkey = value;
                    if (key == "language") CurrentLanguage = value;
                    if (key == "notify_minutes" && int.TryParse(value, out int m)) NotifySeconds = m * 60;
                    if (key == "sound_enabled" && bool.TryParse(value, out bool s)) SoundEnabled = s;
                    if (key == "show_local_time" && bool.TryParse(value, out bool sl)) ShowLocalTime = sl;
                    if (key == "last_seen_version") LastSeenVersion = value;
                    if (key == "discord_webhook_url") DiscordWebhookUrl = value;
                    if (key == "discord_webhook_enabled" && bool.TryParse(value, out bool dwe)) DiscordWebhookEnabled = dwe;
                }
            }
        }

        public static void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(AppDataPath);
                string[] lines = {
                    $"hotkey={Hotkey}",
                    $"notify_minutes={NotifySeconds/60}",
                    $"language={CurrentLanguage}",
                    $"sound_enabled={SoundEnabled}",
                    $"show_local_time={ShowLocalTime}",
                    $"last_seen_version={LastSeenVersion}",
                    $"discord_webhook_enabled={DiscordWebhookEnabled}",
                    $"discord_webhook_url={DiscordWebhookUrl}"
                };
                File.WriteAllLines(ConfigFile, lines);
            }
            catch { }
        }

        public static string CurrentLanguageAuthor { get; set; } = "Unknown";

        public static void LoadLanguage()
        {
            Translations.Clear();
            CurrentLanguageAuthor = "Unknown";

            string path = Path.Combine(LanguagesDir, $"lang_{CurrentLanguage}.ini");
            if (!File.Exists(path)) path = Path.Combine(LanguagesDir, "lang_en.ini");

            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.Contains("="))
                    {
                        var p = line.Split(new[] { '=' }, 2);
                        if (p.Length > 1)
                        {
                            string key = p[0].Trim().ToLower();
                            string value = p[1].Trim();

                            if (key == "author")
                            {
                                CurrentLanguageAuthor = value;
                            }

                            Translations[key] = value;
                        }
                    }
                }
            }
        }

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int WM_HOTKEY = 0x0312; private const int GWL_STYLE = -16; private const int WS_MAXIMIZEBOX = 0x10000;

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == 1) { if (Visibility == Visibility.Visible) Hide(); else { Show(); Activate(); } handled = true; }
            return IntPtr.Zero;
        }

        public static uint GetVkCode(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0x78;
            if (key.StartsWith("F") && int.TryParse(key.Substring(1), out int n)) return (uint)(0x70 + n - 1);
            char c = key.ToUpper()[0];
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z')) return (uint)c;
            return 0x78;
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is TabControl tabControl && tabControl.SelectedContent != null)
            {
                // Find the content presenter
                var contentPresenter = FindVisualChild<ContentPresenter>(tabControl);
                if (contentPresenter != null)
                {
                    // Apply fade-in animation
                    var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 0,
                        To = 1,
                        Duration = TimeSpan.FromMilliseconds(200)
                    };
                    contentPresenter.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
            }
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow sw = new SettingsWindow(); sw.Owner = this;
            if (sw.ShowDialog() == true)
            {
                UnregisterHotKey(_windowHandle, 1); RegisterHotKey(_windowHandle, 1, 0, GetVkCode(Hotkey));
                Tabs.Clear(); _ = InitialLoad(); UpdateLocalizedUI();
            }
        }

        private void CopyNextEvent_Click(object sender, RoutedEventArgs e)
        {
            CopyNextEventSummaryToClipboard();
        }

        private void TrayCopyNext_Click(object sender, RoutedEventArgs e)
        {
            CopyNextEventSummaryToClipboard();
        }

        private void CopyNextEventSummaryToClipboard()
        {
            var nextCard = GetNextUpcomingCard();
            if (nextCard == null)
            {
                ShowLocalToast("ARC-Sight", "No upcoming event to copy.");
                return;
            }

            var startLocal = DateTimeOffset.FromUnixTimeMilliseconds(nextCard.RawData.startTime).LocalDateTime;
            var diffMinutes = Math.Max(1, (int)Math.Ceiling((nextCard.RawData.startTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 60000.0));
            string summary = $"Next ARC Raiders event: {nextCard.Title} | {nextCard.Map} | {startLocal:HH:mm} ({diffMinutes}m)";

            try
            {
                Clipboard.SetText(summary);
                ShowLocalToast("ARC-Sight", "Next event copied to clipboard.");
            }
            catch
            {
                ShowLocalToast("ARC-Sight", "Could not access clipboard.");
            }
        }

        private CardViewModel? GetNextUpcomingCard()
        {
            var allTab = Tabs.FirstOrDefault(t => t.Header == "ALL");
            if (allTab == null || allTab.Cards.Count == 0) return null;

            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return allTab.Cards
                .Where(card => card.RawData.startTime > nowUnix)
                .OrderBy(card => card.RawData.startTime)
                .FirstOrDefault();
        }

        private static void ShowLocalToast(string title, string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                try { new ToastWindow(title, message).Show(); } catch { }
            });
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ConfirmationWindow(GetTrans("exit_confirm_title", "UI"), GetTrans("exit_confirm_msg", "UI"), GetTrans("yes_btn", "UI"), GetTrans("no_btn", "UI"));
            dialog.Owner = this;
            if (dialog.ShowDialog() == true) Application.Current.Shutdown();
        }

        private void ToggleLock_Click(object sender, RoutedEventArgs e)
        {
            _isWindowLocked = !_isWindowLocked;
            // Segoe MDL2 Assets: E72E = Lock, E785 = Unlock
            if (LockBtnRef != null)
            {
                LockBtnRef.Content = _isWindowLocked ? "\uE72E" : "\uE785";
                LockBtnRef.Foreground = _isWindowLocked ? new SolidColorBrush(Color.FromRgb(255, 85, 0)) : Brushes.White;
            }
            this.ResizeMode = _isWindowLocked ? ResizeMode.NoResize : ResizeMode.CanResize;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (!_isWindowLocked) { _isDragging = true; _dragOffset = e.GetPosition(this); this.CaptureMouse(); } }
        private void MainWindow_MouseMove(object sender, MouseEventArgs e) { if (_isDragging) { var diff = e.GetPosition(this) - _dragOffset; this.Left += diff.X; this.Top += diff.Y; } }
        private void MainWindow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { if (_isDragging) { _isDragging = false; this.ReleaseMouseCapture(); } }

        #region System Tray

        private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
        {
            ShowAndActivate();
        }

        private void TrayOpen_Click(object sender, RoutedEventArgs e)
        {
            ShowAndActivate();
        }

        private void TrayHide_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }

        private void TrayMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu)
            {
                var showItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Name == "TrayShowItem");
                var hideItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Name == "TrayHideItem");
                if (showItem == null || hideItem == null) return;

                bool isVisible = this.Visibility == Visibility.Visible;
                showItem.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
                hideItem.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void TrayRestart_Click(object sender, RoutedEventArgs e)
        {
            // Get the current executable path
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                // Start a new instance
                System.Diagnostics.Process.Start(exePath);
                // Close current instance
                Application.Current.Shutdown();
            }
        }

        private void TrayExit_Click(object sender, RoutedEventArgs e)
        {
            // Dispose tray icon and exit
            TrayIconRef?.Dispose();
            Application.Current.Shutdown();
        }

        private void ShowAndActivate()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            this.Focus();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Dispose tray icon when closing
            TrayIconRef?.Dispose();
            base.OnClosing(e);
        }

        #endregion
    }

    public class ScheduleEvent
    {
        public string? name { get; set; }
        public string? map { get; set; }
        public string? icon { get; set; }
        public long startTime { get; set; }
        public long endTime { get; set; }
    }

    public class EventDisplayData
    {
        public ScheduleEvent Raw { get; set; }
        public EventDisplayData(ScheduleEvent raw) { Raw = raw; }
    }

    public class AlertNotification
    {
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string EventName { get; set; } = "";
        public string MapName { get; set; } = "";
        public string? ImageUrl { get; set; }
        public ScheduleEvent RawEvent { get; set; } = new ScheduleEvent();
    }

    public class TabViewModel
    {
        public string Header { get; set; }
        public ObservableCollection<CardViewModel> Cards { get; set; }
        public ICollectionView SortedCards { get; set; }

        public TabViewModel(string header)
        {
            Header = header;
            Cards = new ObservableCollection<CardViewModel>();
            SortedCards = CollectionViewSource.GetDefaultView(Cards);
            SortedCards.SortDescriptions.Add(new SortDescription(nameof(CardViewModel.IsActive), ListSortDirection.Descending));
            SortedCards.SortDescriptions.Add(new SortDescription(nameof(CardViewModel.TargetTime), ListSortDirection.Ascending));

            var liveView = (ICollectionViewLiveShaping)SortedCards;
            if (liveView.CanChangeLiveSorting) { liveView.IsLiveSorting = true; liveView.LiveSortingProperties.Add(nameof(CardViewModel.IsActive)); liveView.LiveSortingProperties.Add(nameof(CardViewModel.TargetTime)); }
        }
    }

    public class CardViewModel : INotifyPropertyChanged
    {
        private static readonly Dictionary<string, BitmapImage> _imageCache = new();

        public ScheduleEvent RawData;
        public event Action<AlertNotification>? RequestNotification;
        public string Title => MainWindow.GetTrans(RawData.name ?? "", "TABS");
        public string Map => MainWindow.GetTrans(RawData.map ?? "", "MAPS");
        public string MapSortKey => RawData.map ?? "";
        public string AlertLabel => MainWindow.GetTrans("alert_button_label", "UI");
        public ImageSource? BackgroundImage { get; private set; }

        private bool _isActive = false;
        public bool IsActive { get => _isActive; set { if (_isActive != value) { _isActive = value; OnPropertyChanged(nameof(IsActive)); } } }

        private DateTime _targetTime = DateTime.MaxValue;
        public DateTime TargetTime { get => _targetTime; set { if (_targetTime != value) { _targetTime = value; OnPropertyChanged(nameof(TargetTime)); } } }

        private string _timerText = "--:--";
        public string TimerText { get => _timerText; set { if (_timerText != value) { _timerText = value; OnPropertyChanged(nameof(TimerText)); } } }

        private string _timerPrefix = "";
        public string TimerPrefix { get => _timerPrefix; set { if (_timerPrefix != value) { _timerPrefix = value; OnPropertyChanged(nameof(TimerPrefix)); } } }

        private string _localTimeText = "";
        public string LocalTimeText { get => _localTimeText; set { if (_localTimeText != value) { _localTimeText = value; OnPropertyChanged(nameof(LocalTimeText)); } } }

        private Brush _timerColor = Brushes.White;
        public Brush TimerColor { get => _timerColor; set { if (_timerColor != value) { _timerColor = value; OnPropertyChanged(nameof(TimerColor)); } } }

        private Brush _borderColor = Brushes.Transparent;
        public Brush BorderColor { get => _borderColor; set { if (_borderColor != value) { _borderColor = value; OnPropertyChanged(nameof(BorderColor)); } } }

        private bool _isInAlertState = false;
        public bool IsInAlertState { get => _isInAlertState; set { if (_isInAlertState != value) { _isInAlertState = value; OnPropertyChanged(nameof(IsInAlertState)); } } }

        private bool _isAlertEnabled = false;
        public bool IsAlertEnabled { get => _isAlertEnabled; set { _isAlertEnabled = value; OnPropertyChanged(nameof(IsAlertEnabled)); if (!value) HasNotified = false; } }

        private Visibility _alertVisibility = Visibility.Visible;
        public Visibility AlertVisibility { get => _alertVisibility; set { if (_alertVisibility != value) { _alertVisibility = value; OnPropertyChanged(nameof(AlertVisibility)); } } }

        private double _localTimeFontSize = 20;
        public double LocalTimeFontSize { get => _localTimeFontSize; set { if (_localTimeFontSize != value) { _localTimeFontSize = value; OnPropertyChanged(nameof(LocalTimeFontSize)); } } }

        private bool HasNotified = false;

        public CardViewModel(ScheduleEvent data)
        {
            RawData = data;
            BorderColor = new SolidColorBrush(Color.FromRgb(60, 60, 60));
            LoadImage();
            UpdateTimer();
        }

        public void UpdateData(ScheduleEvent newData)
        {
            if (RawData.startTime != newData.startTime || RawData.endTime != newData.endTime)
            {
                RawData = newData;
                HasNotified = false;
                UpdateTimer();
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Map));
            }
        }

        private void LoadImage()
        {
            string mapName = RawData.map ?? "";
            string imgFile = "Barrage.png";
            if (mapName.Contains("Dam")) imgFile = "Barrage.png";
            else if (mapName.Contains("Spaceport")) imgFile = "Port_spatial.png";
            else if (mapName.Contains("Buried")) imgFile = "Ville_enfouie.png";
            else if (mapName.Contains("Gate")) imgFile = "Portail_bleu.png";
            else if (mapName.Contains("Stella")) imgFile = "Stella_montis.png";

            // Check cache first
            if (_imageCache.TryGetValue(imgFile, out var cachedImage))
            {
                BackgroundImage = cachedImage;
                return;
            }

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", imgFile);
            if (File.Exists(path))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze(); // Make it cross-thread accessible
                    _imageCache[imgFile] = bitmap;
                    BackgroundImage = bitmap;
                }
                catch { }
            }
        }

        public void UpdateTimer()
        {
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            bool isActive = (nowUnix >= RawData.startTime && nowUnix < RawData.endTime);
            IsActive = isActive;

            AlertVisibility = IsActive ? Visibility.Collapsed : Visibility.Visible;

            string startTxt = MainWindow.GetTrans("timer_start_prefix", "UI");
            string endTxt = MainWindow.GetTrans("timer_end_prefix", "UI");

            if (startTxt == "TIMER_START_PREFIX") startTxt = "STARTS IN";
            if (endTxt == "TIMER_END_PREFIX") endTxt = "ENDS IN";

            TimeSpan diff;

            if (isActive)
            {
                long diffMs = RawData.endTime - nowUnix;
                diff = TimeSpan.FromMilliseconds(diffMs);
                TargetTime = DateTimeOffset.FromUnixTimeMilliseconds(RawData.endTime).LocalDateTime;

                TimerPrefix = endTxt;
                TimerColor = Brushes.OrangeRed;
                BorderColor = Brushes.OrangeRed;
                IsAlertEnabled = false;
                IsInAlertState = false;
                LocalTimeText = "";
            }
            else
            {
                long diffMs = RawData.startTime - nowUnix;
                diff = TimeSpan.FromMilliseconds(diffMs);
                TargetTime = DateTimeOffset.FromUnixTimeMilliseconds(RawData.startTime).LocalDateTime;

                TimerPrefix = startTxt;

                if (MainWindow.ShowLocalTime)
                    LocalTimeText = TargetTime.ToString("HH:mm");
                else
                    LocalTimeText = "";

                if (diff.TotalSeconds <= MainWindow.NotifySeconds && diff.TotalSeconds > 0)
                {
                    TimerColor = Brushes.Yellow;
                    BorderColor = Brushes.Yellow;
                    IsInAlertState = true;

                    if (IsAlertEnabled && !HasNotified)
                    {
                        string msgPattern = MainWindow.GetTrans("notify_message", "UI");
                        if (string.IsNullOrEmpty(msgPattern) || msgPattern == "NOTIFY_MESSAGE")
                            msgPattern = "STARTING IN {minutes} MIN - {map_name}";

                        int minutes = Math.Max(1, (int)Math.Ceiling(diff.TotalMinutes));
                        string msg = msgPattern.Replace("{minutes}", minutes.ToString())
                                               .Replace("{map_name}", Map);

                        RequestNotification?.Invoke(new AlertNotification
                        {
                            Title = Title,
                            Message = msg,
                            EventName = Title,
                            MapName = Map,
                            ImageUrl = RawData.icon,
                            RawEvent = RawData
                        });
                        HasNotified = true;
                    }
                }
                else
                {
                    TimerColor = Brushes.White;
                    BorderColor = new SolidColorBrush(Color.FromRgb(60, 60, 60));
                    IsInAlertState = false;
                    HasNotified = false;
                }
            }

            if (diff.TotalHours >= 1)
                TimerText = $"{(int)diff.TotalHours}h {diff.Minutes}m";
            else if (diff.TotalSeconds > 0)
                TimerText = $"{diff.Minutes:D2}:{diff.Seconds:D2}";
            else
                TimerText = "00:00";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Helper class for animating ScrollViewer horizontal offset
    /// </summary>
    public static class ScrollViewerBehavior
    {
        public static readonly DependencyProperty HorizontalOffsetProperty =
            DependencyProperty.RegisterAttached(
                "HorizontalOffset",
                typeof(double),
                typeof(ScrollViewerBehavior),
                new PropertyMetadata(0.0, OnHorizontalOffsetChanged));

        public static double GetHorizontalOffset(DependencyObject obj) =>
            (double)obj.GetValue(HorizontalOffsetProperty);

        public static void SetHorizontalOffset(DependencyObject obj, double value) =>
            obj.SetValue(HorizontalOffsetProperty, value);

        private static void OnHorizontalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer scrollViewer)
            {
                scrollViewer.ScrollToHorizontalOffset((double)e.NewValue);
            }
        }
    }
}