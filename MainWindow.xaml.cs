using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShareManager.Models;
using ShareManager.Services;

namespace ShareManager;

public partial class MainWindow : Window
{
    private readonly PowerShellRemoteService _psService = new();
    private AppConfig _config = new();
    private bool _isLoadingSettingsUi;
    private DispatcherTimer? _autoRefreshTimer;

    private readonly ObservableCollection<ServerNode> _servers = new();
    private List<SmbShareItem> _allServerShares = new();
    private List<SmbSessionItem> _allServerSessions = new();
    private List<SmbOpenFileItem> _allServerOpenFiles = new();

    private ServerNode? _currentServer;
    private SmbShareItem? _selectedShare;

    // Share Editor Modal State
    private bool _isEditingExistingShare;
    private SmbShareItem? _editingShareItem;
    private readonly ObservableCollection<SmbPermissionItem> _modalSmbPermissions = new();
    private NtfsAclInfo? _modalNtfsAcl;

    // Remote Folder Browser State
    private string _currentRemoteBrowsePath = @"C:\";

    // Principal Picker State ("SMB" or "NTFS")
    private string _principalTargetMode = "SMB";

    // Server Modal State
    private bool _isEditingServer;

    // DFS Namespaces State
    private readonly ObservableCollection<DfsNamespaceRoot> _dfsRoots = new();
    private List<DfsFolderItem> _currentDfsFolders = new();
    private DfsNamespaceRoot? _selectedDfsRoot;
    private DfsFolderItem? _selectedDfsFolder;
    private bool _isAddingTargetToExistingDfsFolder;

    private CancellationTokenSource? _toastCts;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _psService.Dispose();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Show current Windows / AD identity
        try
        {
            string user = WindowsIdentity.GetCurrent().Name;
            CurrentUserText.Text = user;
        }
        catch
        {
            CurrentUserText.Text = Environment.UserName;
        }

        // Check local process elevation
        bool isElevated = PowerShellRemoteService.IsCurrentProcessElevated();
        if (isElevated)
        {
            ElevationStatusText.Text = "🛡 Права: Администратор";
            ElevationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            RestartAsAdminButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ElevationStatusText.Text = "⚠ Обычный режим (без UAC)";
            ElevationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            RestartAsAdminButton.Visibility = Visibility.Visible;
        }

        // Load saved configuration
        _config = ConfigService.Load();
        ShowSystemSharesToggle.IsChecked = _config.ShowSystemShares;

        PopulateSettingsUiFromConfig();
        ApplyThemeAndGlass(_config.ThemeName, _config.EnableGlassEffects);
        _psService.DefaultTimeoutMs = Math.Clamp(_config.WinRmTimeoutSeconds, 10, 120) * 1000;
        ConfigureAutoRefreshTimer(_config.AutoRefreshIntervalSeconds);

        _servers.Clear();
        foreach (var s in _config.Servers)
        {
            _servers.Add(new ServerNode
            {
                Id = s.Id,
                Hostname = s.Hostname,
                DisplayName = s.DisplayName,
                Description = s.Description,
                IsLocal = s.IsLocal
            });
        }

        ServersListBox.ItemsSource = _servers;
        ModalSmbPermissionsList.ItemsSource = _modalSmbPermissions;
        DfsRootsListBox.ItemsSource = _dfsRoots;

        _dfsRoots.Clear();
        if (_config.DfsRoots != null)
        {
            foreach (var r in _config.DfsRoots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _dfsRoots.Add(new DfsNamespaceRoot
                {
                    RootPath = DfsService.NormalizeUncPath(r),
                    Flavor = "Saved",
                    Description = "Сохраненное пространство имен"
                });
            }
        }
        UpdateTabCounters();

        // Select last used server or first
        var initialServer = _servers.FirstOrDefault(x => x.Id == _config.LastSelectedServerId)
                            ?? _servers.FirstOrDefault();

        if (initialServer != null)
        {
            ServersListBox.SelectedItem = initialServer;
        }

        // Background ping other servers & auto-discover DFS roots
        _ = CheckAllServersStatusInBackgroundAsync();
        _ = AutoDiscoverDfsRootsInBackgroundAsync();
    }

    private async Task AutoDiscoverDfsRootsInBackgroundAsync()
    {
        try
        {
            var hostnames = _servers.Select(s => s.Hostname).ToList();
            var discovered = await DfsService.DiscoverNamespacesAsync(hostnames);
            bool changed = false;
            foreach (var d in discovered)
            {
                if (!_dfsRoots.Any(x => x.RootPath.Equals(d.RootPath, StringComparison.OrdinalIgnoreCase)))
                {
                    _dfsRoots.Add(d);
                    changed = true;
                }
            }
            if (changed)
            {
                SaveServersToConfig();
                UpdateTabCounters();
                if (DfsRootsListBox.SelectedItem == null && _dfsRoots.Count > 0)
                {
                    DfsRootsListBox.SelectedIndex = 0;
                }
            }
        }
        catch { }
    }

    private async Task CheckAllServersStatusInBackgroundAsync()
    {
        foreach (var srv in _servers.ToList())
        {
            if (srv == _currentServer) continue;
            try
            {
                srv.Status = ServerConnectionStatus.Connecting;
                var ov = await _psService.TestAndGetServerOverviewAsync(srv.Hostname);
                if (ov.IsOnline)
                {
                    srv.Status = ServerConnectionStatus.Online;
                    srv.OsInfo = ov.OsCaption;
                    srv.DomainInfo = ov.Domain;
                    srv.ShareCount = ov.ShareCount;
                }
                else
                {
                    srv.Status = ServerConnectionStatus.Offline;
                    srv.LastError = ov.ErrorMessage;
                }
            }
            catch
            {
                srv.Status = ServerConnectionStatus.Offline;
            }
        }
    }

    private void SaveServersToConfig()
    {
        _config.ShowSystemShares = ShowSystemSharesToggle.IsChecked == true;
        _config.LastSelectedServerId = _currentServer?.Id;
        _config.Servers = _servers.Select(s => new SavedServerEntry
        {
            Id = s.Id,
            Hostname = s.Hostname,
            DisplayName = s.DisplayName,
            Description = s.Description,
            IsLocal = s.IsLocal
        }).ToList();
        _config.DfsRoots = _dfsRoots.Select(r => r.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ConfigService.Save(_config);
    }

    // ==================== SERVER SELECTION & REFRESH ====================

    private async void ServersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServersListBox.SelectedItem is ServerNode srv)
        {
            _currentServer = srv;
            SaveServersToConfig();
            await LoadServerDataAsync(srv);
        }
    }

    private async Task LoadServerDataAsync(ServerNode server)
    {
        HeaderServerTitle.Text = server.EffectiveTitle;
        HeaderServerHostBadge.Text = server.Hostname;
        HeaderServerSubInfo.Text = "Подключение к серверу и получение списка шар...";
        HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        ConnectionErrorBanner.Visibility = Visibility.Collapsed;

        server.Status = ServerConnectionStatus.Connecting;

        var overview = await _psService.TestAndGetServerOverviewAsync(server.Hostname);
        if (!overview.IsOnline)
        {
            server.Status = ServerConnectionStatus.Offline;
            server.LastError = overview.ErrorMessage;
            HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            HeaderServerSubInfo.Text = "Ошибка подключения по WinRM / PowerShell Remoting";
            ConnectionErrorText.Text = overview.ErrorMessage;
            ConnectionErrorBanner.Visibility = Visibility.Visible;

            _allServerShares.Clear();
            _allServerSessions.Clear();
            _allServerOpenFiles.Clear();
            ApplyShareFilters();
            ApplySessionFilters();
            ApplyOpenFileFilters();
            UpdateTabCounters();
            return;
        }

        server.Status = ServerConnectionStatus.Online;
        server.OsInfo = overview.OsCaption;
        server.DomainInfo = overview.Domain;
        HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));

        string adminTag = overview.IsAdmin ? "Права Администратора подтверждены" : "Только чтение (нет прав Администратора)";
        HeaderServerSubInfo.Text = $"{overview.OsCaption}  •  Домен: {overview.Domain}  •  {adminTag}";

        try
        {
            _allServerShares = await _psService.GetSharesAsync(server.Hostname);
            server.ShareCount = _allServerShares.Count;
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка чтения списка шар: {ex.Message}", isError: true);
            _allServerShares.Clear();
        }

        try
        {
            _allServerSessions = await _psService.GetSmbSessionsAsync(server.Hostname);
        }
        catch
        {
            _allServerSessions.Clear();
        }

        try
        {
            _allServerOpenFiles = await _psService.GetSmbOpenFilesAsync(server.Hostname);
        }
        catch
        {
            _allServerOpenFiles.Clear();
        }

        ApplyShareFilters();
        ApplySessionFilters();
        ApplyOpenFileFilters();
        UpdateTabCounters();
    }

    private void UpdateTabCounters()
    {
        bool showSys = ShowSystemSharesToggle.IsChecked == true;
        int visibleShareCount = _allServerShares.Count(s => showSys || !s.IsSystemOrAdmin);
        TabSharesText.Text = $"📁 Сетевые шары ({visibleShareCount})";
        if (TabDfsText != null)
            TabDfsText.Text = $"🌐 DFS Пространства ({_dfsRoots.Count})";
        TabSessionsText.Text = $"👥 Активные сессии ({_allServerSessions.Count})";
        TabFilesText.Text = $"📄 Открытые файлы ({_allServerOpenFiles.Count})";
    }

    private void ApplyShareFilters()
    {
        if (SharesListBox == null) return;

        bool showSystem = ShowSystemSharesToggle.IsChecked == true;
        string q = (ShareSearchBox?.Text ?? string.Empty).Trim();

        var filtered = _allServerShares.Where(s =>
        {
            if (!showSystem && s.IsSystemOrAdmin)
                return false;

            if (!string.IsNullOrEmpty(q))
            {
                bool match = s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                             || s.Path.Contains(q, StringComparison.OrdinalIgnoreCase)
                             || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (!match) return false;
            }
            return true;
        })
        .OrderBy(s => s.IsSystemOrAdmin)
        .ThenBy(s => s.Name)
        .ToList();

        string? prevSelectedName = _selectedShare?.Name;
        SharesListBox.ItemsSource = filtered;
        UpdateTabCounters();

        if (EmptySharesStatePanel != null)
        {
            if (filtered.Count == 0)
            {
                EmptySharesStatePanel.Visibility = Visibility.Visible;
                int sysCount = _allServerShares.Count(s => s.IsSystemOrAdmin);
                if (!showSystem && sysCount > 0 && string.IsNullOrEmpty(q))
                {
                    EmptySharesTitleText.Text = "Пользовательских шар пока нет";
                    EmptySharesSubText.Text = $"На этом сервере найдено системных шар: {sysCount} (C$, ADMIN$, IPC$). Включите переключатель «Системные шары» или создайте новую общую папку.";
                    ShowSystemSharesQuickBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    EmptySharesTitleText.Text = "Шары не найдены";
                    EmptySharesSubText.Text = "Попробуйте изменить поисковый запрос или создайте новую сетевую шару.";
                    ShowSystemSharesQuickBtn.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                EmptySharesStatePanel.Visibility = Visibility.Collapsed;
            }
        }

        if (prevSelectedName != null)
        {
            var match = filtered.FirstOrDefault(x => x.Name.Equals(prevSelectedName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                SharesListBox.SelectedItem = match;
                return;
            }
        }

        if (filtered.Count > 0)
        {
            SharesListBox.SelectedIndex = 0;
        }
        else
        {
            _selectedShare = null;
            InspectorEmptyState.Visibility = Visibility.Visible;
            InspectorContentScroll.Visibility = Visibility.Collapsed;
        }
    }

    private void EnableShowSystemShares_Click(object sender, RoutedEventArgs e)
    {
        ShowSystemSharesToggle.IsChecked = true;
    }

    private void ShareSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyShareFilters();
    }

    private void ShowSystemSharesToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        SaveServersToConfig();
        ApplyShareFilters();
    }

    private async void RefreshCurrentServer_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer != null)
        {
            await LoadServerDataAsync(_currentServer);
            ShowToast($"Данные сервера {_currentServer.EffectiveTitle} обновлены");
        }
    }

    private void OpenServerInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer == null) return;
        string host = string.IsNullOrWhiteSpace(_currentServer.Hostname) ? Environment.MachineName : _currentServer.Hostname;
        string unc = $@"\\{host}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = unc,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось открыть Проводник: {ex.Message}", isError: true);
        }
    }

    private void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch
        {
            ShowToast("Запуск от имени Администратора был отменен", isError: true);
        }
    }

    // ==================== MAIN NAVIGATION TABS ====================

    private void SwitchMainWorkspaceTab(int tabIndex)
    {
        SharesWorkspaceView.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionsWorkspaceView.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        FilesWorkspaceView.Visibility = tabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsWorkspaceView.Visibility = tabIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        DfsWorkspaceView.Visibility = tabIndex == 4 ? Visibility.Visible : Visibility.Collapsed;

        TabSharesButton.Style = (Style)FindResource(tabIndex == 0 ? "PrimaryButton" : "SecondaryButton");
        TabSessionsButton.Style = (Style)FindResource(tabIndex == 1 ? "PrimaryButton" : "SecondaryButton");
        TabFilesButton.Style = (Style)FindResource(tabIndex == 2 ? "PrimaryButton" : "SecondaryButton");
        TabSettingsButton.Style = (Style)FindResource(tabIndex == 3 ? "PrimaryButton" : "SecondaryButton");
        TabDfsButton.Style = (Style)FindResource(tabIndex == 4 ? "PrimaryButton" : "SecondaryButton");

        if (tabIndex == 4 && DfsRootsListBox.SelectedItem == null && _dfsRoots.Count > 0)
        {
            DfsRootsListBox.SelectedIndex = 0;
        }
    }

    private void SwitchTab_Shares_Click(object sender, RoutedEventArgs e) => SwitchMainWorkspaceTab(0);
    private void SwitchTab_Sessions_Click(object sender, RoutedEventArgs e) => SwitchMainWorkspaceTab(1);
    private void SwitchTab_Files_Click(object sender, RoutedEventArgs e) => SwitchMainWorkspaceTab(2);
    private void SwitchTab_Settings_Click(object sender, RoutedEventArgs e) => SwitchMainWorkspaceTab(3);
    private void SwitchTab_Dfs_Click(object sender, RoutedEventArgs e) => SwitchMainWorkspaceTab(4);

    // ==================== RIGHT INSPECTOR PANEL FOR SELECTED SHARE ====================

    private async void SharesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SharesListBox.SelectedItem is not SmbShareItem share || _currentServer == null)
        {
            _selectedShare = null;
            InspectorEmptyState.Visibility = Visibility.Visible;
            InspectorContentScroll.Visibility = Visibility.Collapsed;
            return;
        }

        _selectedShare = share;
        InspectorEmptyState.Visibility = Visibility.Collapsed;
        InspectorContentScroll.Visibility = Visibility.Visible;

        InspShareName.Text = share.Name;
        InspShareUnc.Text = share.UncPath;
        InspLocalPath.Text = string.IsNullOrWhiteSpace(share.Path) ? "(Системный ресурс / IPC)" : share.Path;
        InspAbeStatus.Text = share.IsAbeEnabled ? "Включен (AccessBased)" : "Отключен (Unrestricted)";
        InspAbeStatus.Foreground = share.IsAbeEnabled
            ? new SolidColorBrush(Color.FromRgb(52, 211, 153))
            : new SolidColorBrush(Color.FromRgb(148, 163, 184));
        InspCachingMode.Text = share.CachingMode;
        InspNtfsOwner.Text = "Загрузка...";

        // Load SMB Permissions
        try
        {
            var smbPerms = await _psService.GetSharePermissionsAsync(_currentServer.Hostname, share.Name);
            InspSmbPermissionsList.ItemsSource = smbPerms;
        }
        catch (Exception ex)
        {
            InspSmbPermissionsList.ItemsSource = new List<SmbPermissionItem>
            {
                new SmbPermissionItem { AccountName = $"Ошибка чтения: {ex.Message}", AccessRight = "-", AccessControlType = "Deny" }
            };
        }

        // Load NTFS ACL if share has a physical folder path
        if (!string.IsNullOrWhiteSpace(share.Path) && share.Path.Contains(':'))
        {
            try
            {
                InspNtfsStatusText.Visibility = Visibility.Collapsed;
                var acl = await _psService.GetNtfsAclAsync(_currentServer.Hostname, share.Path);
                InspNtfsOwner.Text = acl.Owner;
                InspInheritanceText.Text = acl.AreAccessRulesProtected ? "Наследование отключено" : "Наследование включено";
                InspNtfsPermissionsList.ItemsSource = acl.Rules;
            }
            catch (Exception ex)
            {
                InspNtfsOwner.Text = "Н/Д";
                InspNtfsPermissionsList.ItemsSource = null;
                InspNtfsStatusText.Text = $"Не удалось прочитать NTFS ACL: {ex.Message}";
                InspNtfsStatusText.Visibility = Visibility.Visible;
            }
        }
        else
        {
            InspNtfsOwner.Text = "Неприменимо";
            InspNtfsPermissionsList.ItemsSource = null;
            InspNtfsStatusText.Text = "Для системного ресурса без папки на диске права NTFS не применяются.";
            InspNtfsStatusText.Visibility = Visibility.Visible;
        }
    }

    // ==================== QUICK CARD ACTIONS ====================

    private void CopyUncPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SmbShareItem share)
        {
            Clipboard.SetText(share.UncPath);
            ShowToast($"Скопировано в буфер: {share.UncPath}");
        }
    }

    private void OpenShareInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SmbShareItem share)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = share.UncPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowToast($"Ошибка открытия шары: {ex.Message}", isError: true);
            }
        }
    }

    private async void EditShareCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SmbShareItem share)
        {
            SharesListBox.SelectedItem = share;
            await OpenShareEditorModalAsync(share, initialTab: 0);
        }
    }

    private async void OpenSelectedShareEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedShare != null)
            await OpenShareEditorModalAsync(_selectedShare, initialTab: 0);
    }

    private async void OpenSelectedShareSmbTab_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedShare != null)
            await OpenShareEditorModalAsync(_selectedShare, initialTab: 1);
    }

    private async void OpenSelectedShareNtfsTab_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedShare != null)
            await OpenShareEditorModalAsync(_selectedShare, initialTab: 2);
    }

    private async void DeleteShareCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SmbShareItem share || _currentServer == null)
            return;

        if (_config.ConfirmDangerousActions)
        {
            var res = MessageBox.Show(
                $"Вы действительно хотите прекратить общий доступ к шаре «{share.Name}» ({share.Path}) на сервере {_currentServer.EffectiveTitle}?\n\nСама папка и файлы на диске удалены НЕ будут.",
                "Подтверждение удаления шары — Folders",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes)
                return;
        }

        try
        {
            await _psService.DeleteShareAsync(_currentServer.Hostname, share.Name);
            ShowToast($"Шара «{share.Name}» успешно удалена");
            await LoadServerDataAsync(_currentServer);
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось удалить шару: {ex.Message}", isError: true);
        }
    }

    // ==================== MODAL 1: SHARE CREATE / EDIT & PERMISSIONS ====================

    private async void OpenCreateShareModal_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer == null) return;
        await OpenShareEditorModalAsync(null, initialTab: 0);
    }

    private async Task OpenShareEditorModalAsync(SmbShareItem? existingShare, int initialTab)
    {
        if (_currentServer == null) return;

        _isEditingExistingShare = existingShare != null;
        _editingShareItem = existingShare;
        _modalSmbPermissions.Clear();
        _modalNtfsAcl = null;

        if (existingShare == null)
        {
            // Create Mode
            ShareModalTitle.Text = $"Создание новой шары на {_currentServer.EffectiveTitle}";
            ShareModalSubtitle.Text = "Укажите имя шары, выберите или создайте папку на сервере и настройте права доступа";
            SaveShareModalBtnText.Text = "✓ Создать шару";

            EditShareNameBox.Text = "";
            EditShareNameBox.IsEnabled = true;
            EditSharePathBox.Text = string.IsNullOrWhiteSpace(_config.DefaultBaseSharePath) ? @"C:\Shares\" : _config.DefaultBaseSharePath;
            EditSharePathBox.IsEnabled = true;
            BrowseServerFolderBtn.IsEnabled = true;
            CreateFolderIfMissingCheck.Visibility = Visibility.Visible;
            CreateFolderIfMissingCheck.IsChecked = _config.DefaultCreateFolderIfMissing;

            EditShareDescBox.Text = "";
            EditShareAbeCheck.IsChecked = _config.DefaultAbeEnabled;
            SelectComboByTag(EditShareCachingCombo, "Manual");
            EditShareLimitBox.Text = "0";

            // Default initial SMB permission: Everyone Read or Administrators Full
            string adminsName = ResolveWellKnownSid("S-1-5-32-544", @"BUILTIN\Administrators");
            string everyoneName = ResolveWellKnownSid("S-1-1-0", "Everyone");

            _modalSmbPermissions.Add(new SmbPermissionItem
            {
                AccountName = adminsName,
                AccessControlType = "Allow",
                AccessRight = "Full"
            });
            _modalSmbPermissions.Add(new SmbPermissionItem
            {
                AccountName = everyoneName,
                AccessControlType = "Allow",
                AccessRight = "Change"
            });

            ShareModalStatusHint.Text = "При создании шары будут сразу применены выбранные права SMB.";
        }
        else
        {
            // Edit Mode
            ShareModalTitle.Text = $"Настройка шары «{existingShare.Name}»";
            ShareModalSubtitle.Text = $"Сервер: {_currentServer.EffectiveTitle} ({existingShare.UncPath})";
            SaveShareModalBtnText.Text = "✓ Сохранить настройки шары";

            EditShareNameBox.Text = existingShare.Name;
            EditShareNameBox.IsEnabled = false;
            EditSharePathBox.Text = existingShare.Path;
            EditSharePathBox.IsEnabled = false;
            BrowseServerFolderBtn.IsEnabled = false;
            CreateFolderIfMissingCheck.Visibility = Visibility.Collapsed;

            EditShareDescBox.Text = existingShare.Description;
            EditShareAbeCheck.IsChecked = existingShare.IsAbeEnabled;
            SelectComboByTag(EditShareCachingCombo, existingShare.CachingMode);
            EditShareLimitBox.Text = existingShare.ConcurrentUserLimit.ToString();

            ShareModalStatusHint.Text = "Изменения прав SMB и NTFS на вкладках 2 и 3 применяются на сервере сразу.";

            // Load current SMB permissions
            try
            {
                var perms = await _psService.GetSharePermissionsAsync(_currentServer.Hostname, existingShare.Name);
                foreach (var p in perms)
                    _modalSmbPermissions.Add(p);
            }
            catch (Exception ex)
            {
                ShowToast($"Ошибка загрузки прав SMB: {ex.Message}", isError: true);
            }

            // Load current NTFS permissions
            await LoadModalNtfsPermissionsAsync(existingShare.Path);
        }

        UpdateUncPreview();
        SwitchShareModalSubTab(initialTab);
        ShareEditorModalOverlay.Visibility = Visibility.Visible;
    }

    private async Task LoadModalNtfsPermissionsAsync(string folderPath)
    {
        if (_currentServer == null || string.IsNullOrWhiteSpace(folderPath) || !folderPath.Contains(':'))
        {
            ModalNtfsFolderText.Text = "Папка: (не задана или еще не создана)";
            ModalNtfsOwnerText.Text = "Владелец: —";
            ModalNtfsInheritanceStatus.Text = "Создайте шару или укажите существующую папку на сервере для управления NTFS ACL";
            ModalNtfsRulesList.ItemsSource = null;
            return;
        }

        ModalNtfsFolderText.Text = $"Папка на сервере: {folderPath}";
        try
        {
            _modalNtfsAcl = await _psService.GetNtfsAclAsync(_currentServer.Hostname, folderPath);
            ModalNtfsOwnerText.Text = $"Владелец: {_modalNtfsAcl.Owner}";
            if (_modalNtfsAcl.AreAccessRulesProtected)
            {
                ModalNtfsInheritanceStatus.Text = "Наследование прав от родительской папки: ОТКЛЮЧЕНО";
                ModalNtfsInheritanceStatus.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                ToggleNtfsInheritanceBtnText.Text = "Включить наследование";
            }
            else
            {
                ModalNtfsInheritanceStatus.Text = "Наследование прав от родительской папки: ВКЛЮЧЕНО";
                ModalNtfsInheritanceStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                ToggleNtfsInheritanceBtnText.Text = "Отключить наследование";
            }
            ModalNtfsRulesList.ItemsSource = _modalNtfsAcl.Rules;
        }
        catch (Exception ex)
        {
            ModalNtfsOwnerText.Text = $"Папка еще не создана или недоступна ({ex.Message})";
            ModalNtfsRulesList.ItemsSource = null;
        }
    }

    private void SwitchShareModalSubTab(int index)
    {
        ModalGeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModalSmbPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ModalNtfsPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        ModalTabGeneralBtn.Style = (Style)FindResource(index == 0 ? "PrimaryButton" : "SecondaryButton");
        ModalTabSmbBtn.Style = (Style)FindResource(index == 1 ? "PrimaryButton" : "SecondaryButton");
        ModalTabNtfsBtn.Style = (Style)FindResource(index == 2 ? "PrimaryButton" : "SecondaryButton");
    }

    private void ModalTabGeneral_Click(object sender, RoutedEventArgs e) => SwitchShareModalSubTab(0);
    private void ModalTabSmb_Click(object sender, RoutedEventArgs e) => SwitchShareModalSubTab(1);

    private async void ModalTabNtfs_Click(object sender, RoutedEventArgs e)
    {
        SwitchShareModalSubTab(2);
        string path = EditSharePathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await LoadModalNtfsPermissionsAsync(path);
        }
    }

    private void EditShareNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateUncPreview();
    }

    private void UpdateUncPreview()
    {
        if (EditShareUncPreview == null) return;
        string host = _currentServer?.Hostname ?? Environment.MachineName;
        string name = string.IsNullOrWhiteSpace(EditShareNameBox.Text) ? "<ИмяШары>" : EditShareNameBox.Text.Trim();
        EditShareUncPreview.Text = $@"\\{host}\{name}";
    }

    private void CloseShareEditorModal_Click(object sender, RoutedEventArgs e)
    {
        ShareEditorModalOverlay.Visibility = Visibility.Collapsed;
    }

    private async void SaveShareModal_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer == null) return;

        string shareName = EditShareNameBox.Text.Trim();
        string folderPath = EditSharePathBox.Text.Trim();
        string description = EditShareDescBox.Text.Trim();
        bool enableAbe = EditShareAbeCheck.IsChecked == true;
        string caching = GetSelectedComboTag(EditShareCachingCombo, "Manual");
        uint.TryParse(EditShareLimitBox.Text.Trim(), out uint userLimit);

        if (string.IsNullOrWhiteSpace(shareName))
        {
            ShowToast("Укажите имя сетевой шары", isError: true);
            SwitchShareModalSubTab(0);
            return;
        }

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            ShowToast("Укажите путь к папке на диске сервера", isError: true);
            SwitchShareModalSubTab(0);
            return;
        }

        SaveShareModalBtn.IsEnabled = false;
        try
        {
            if (!_isEditingExistingShare)
            {
                bool createDir = CreateFolderIfMissingCheck.IsChecked == true;
                await _psService.CreateShareAsync(
                    _currentServer.Hostname,
                    shareName,
                    folderPath,
                    description,
                    enableAbe,
                    caching,
                    userLimit,
                    createDir,
                    _modalSmbPermissions.ToList());

                ShowToast($"Шара «{shareName}» успешно создана на {_currentServer.EffectiveTitle}");
            }
            else
            {
                await _psService.UpdateShareSettingsAsync(
                    _currentServer.Hostname,
                    shareName,
                    description,
                    enableAbe,
                    caching,
                    userLimit);

                ShowToast($"Настройки шары «{shareName}» сохранены");
            }

            ShareEditorModalOverlay.Visibility = Visibility.Collapsed;
            await LoadServerDataAsync(_currentServer);

            // Select the newly created/updated share
            var created = _allServerShares.FirstOrDefault(x => x.Name.Equals(shareName, StringComparison.OrdinalIgnoreCase));
            if (created != null)
            {
                SharesListBox.SelectedItem = created;
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка сохранения шары: {ex.Message}", isError: true);
        }
        finally
        {
            SaveShareModalBtn.IsEnabled = true;
        }
    }

    // ==================== SMB SHARE PERMISSIONS ACTIONS ====================

    private async void AddSmbPermission_Click(object sender, RoutedEventArgs e)
    {
        string account = SmbNewAccountBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(account))
        {
            ShowToast("Введите имя пользователя/группы или выберите через поиск в AD", isError: true);
            return;
        }

        string right = GetSelectedComboTag(SmbNewRightCombo, "Change");
        string type = GetSelectedComboTag(SmbNewTypeCombo, "Allow");

        await ApplyOrStageSmbPermissionAsync(account, right, type);
        SmbNewAccountBox.Text = "";
    }

    private async void QuickAddEveryoneRead_Click(object sender, RoutedEventArgs e)
    {
        string everyone = ResolveWellKnownSid("S-1-1-0", "Everyone");
        await ApplyOrStageSmbPermissionAsync(everyone, "Read", "Allow");
    }

    private async void QuickAddEveryoneFull_Click(object sender, RoutedEventArgs e)
    {
        string everyone = ResolveWellKnownSid("S-1-1-0", "Everyone");
        await ApplyOrStageSmbPermissionAsync(everyone, "Full", "Allow");
    }

    private async void QuickAddAdminsFull_Click(object sender, RoutedEventArgs e)
    {
        string admins = ResolveWellKnownSid("S-1-5-32-544", @"BUILTIN\Administrators");
        await ApplyOrStageSmbPermissionAsync(admins, "Full", "Allow");
    }

    private async Task ApplyOrStageSmbPermissionAsync(string accountName, string accessRight, string accessControlType)
    {
        if (_currentServer == null) return;

        if (_isEditingExistingShare && _editingShareItem != null)
        {
            try
            {
                await _psService.SetSmbPermissionAsync(
                    _currentServer.Hostname,
                    _editingShareItem.Name,
                    accountName,
                    accessRight,
                    accessControlType);

                var updated = await _psService.GetSharePermissionsAsync(_currentServer.Hostname, _editingShareItem.Name);
                _modalSmbPermissions.Clear();
                foreach (var p in updated) _modalSmbPermissions.Add(p);
                InspSmbPermissionsList.ItemsSource = updated;

                ShowToast($"Права SMB для «{accountName}» обновлены");
            }
            catch (Exception ex)
            {
                ShowToast($"Ошибка выдачи прав SMB: {ex.Message}", isError: true);
            }
        }
        else
        {
            // Staged for creation
            var existing = _modalSmbPermissions.FirstOrDefault(x => x.AccountName.Equals(accountName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.AccessRight = accessRight;
                existing.AccessControlType = accessControlType;
            }
            else
            {
                _modalSmbPermissions.Add(new SmbPermissionItem
                {
                    AccountName = accountName,
                    AccessRight = accessRight,
                    AccessControlType = accessControlType
                });
            }
        }
    }

    private async void RemoveSmbPermission_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SmbPermissionItem perm || _currentServer == null)
            return;

        if (_isEditingExistingShare && _editingShareItem != null)
        {
            if (_modalSmbPermissions.Count <= 1)
            {
                ShowToast("У шары должно оставаться хотя бы одно правило доступа SMB", isError: true);
                return;
            }

            try
            {
                await _psService.RemoveSmbPermissionAsync(_currentServer.Hostname, _editingShareItem.Name, perm.AccountName);
                var updated = await _psService.GetSharePermissionsAsync(_currentServer.Hostname, _editingShareItem.Name);
                _modalSmbPermissions.Clear();
                foreach (var p in updated) _modalSmbPermissions.Add(p);
                InspSmbPermissionsList.ItemsSource = updated;
                ShowToast($"Право SMB для «{perm.AccountName}» удалено");
            }
            catch (Exception ex)
            {
                ShowToast($"Ошибка удаления права SMB: {ex.Message}", isError: true);
            }
        }
        else
        {
            _modalSmbPermissions.Remove(perm);
        }
    }

    // ==================== NTFS ACL PERMISSIONS ACTIONS ====================

    private async void ReloadModalNtfs_Click(object sender, RoutedEventArgs e)
    {
        await LoadModalNtfsPermissionsAsync(EditSharePathBox.Text.Trim());
        ShowToast("Список NTFS ACL обновлен");
    }

    private async void AddNtfsRule_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer == null) return;
        string path = EditSharePathBox.Text.Trim();
        string account = NtfsNewAccountBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(account))
        {
            ShowToast("Укажите пользователя или группу для правила NTFS", isError: true);
            return;
        }

        string rights = GetSelectedComboTag(NtfsNewRightsCombo, "Modify");
        string scope = GetSelectedComboTag(NtfsNewScopeCombo, "ThisFolderSubfoldersAndFiles");
        string type = GetSelectedComboTag(NtfsNewTypeCombo, "Allow");

        try
        {
            // Ensure folder exists first if creating a new share
            if (!_isEditingExistingShare)
            {
                string parent = Path.GetDirectoryName(path.TrimEnd('\\')) ?? @"C:\";
                string leaf = Path.GetFileName(path.TrimEnd('\\'));
                if (!string.IsNullOrWhiteSpace(leaf))
                {
                    await _psService.CreateRemoteFolderAsync(_currentServer.Hostname, parent, leaf);
                }
            }

            await _psService.AddOrUpdateNtfsRuleAsync(_currentServer.Hostname, path, account, rights, type, scope);
            NtfsNewAccountBox.Text = "";
            await LoadModalNtfsPermissionsAsync(path);
            if (_selectedShare != null && _selectedShare.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                InspNtfsPermissionsList.ItemsSource = _modalNtfsAcl?.Rules;
            }
            ShowToast($"Правило NTFS для «{account}» успешно применено");
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка применения NTFS ACL: {ex.Message}", isError: true);
        }
    }

    private async void RemoveNtfsRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not NtfsRuleItem rule || _currentServer == null)
            return;

        if (rule.IsInherited)
        {
            ShowToast("Унаследованное правило нельзя удалить напрямую — сначала отключите наследование папки", isError: true);
            return;
        }

        string path = EditSharePathBox.Text.Trim();
        try
        {
            await _psService.RemoveNtfsRuleAsync(_currentServer.Hostname, path, rule.IdentityReference, rule.AccessControlType);
            await LoadModalNtfsPermissionsAsync(path);
            if (_selectedShare != null && _selectedShare.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                InspNtfsPermissionsList.ItemsSource = _modalNtfsAcl?.Rules;
            }
            ShowToast($"Правило NTFS для «{rule.IdentityReference}» удалено");
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка удаления правила NTFS: {ex.Message}", isError: true);
        }
    }

    private async void ToggleNtfsInheritance_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer == null || _modalNtfsAcl == null) return;
        string path = EditSharePathBox.Text.Trim();

        try
        {
            if (!_modalNtfsAcl.AreAccessRulesProtected)
            {
                var res = MessageBox.Show(
                    "Отключить наследование прав NTFS от родительской папки?\n\nНажмите «Да», чтобы скопировать текущие унаследованные права в качестве явных прав на эту папку (рекомендуется).\nНажмите «Нет», чтобы удалить все унаследованные права.",
                    "Наследование прав NTFS",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.Cancel) return;
                bool preserve = res == MessageBoxResult.Yes;

                await _psService.SetNtfsInheritanceAsync(_currentServer.Hostname, path, disableInheritance: true, preserveInheritedRules: preserve);
                ShowToast("Наследование NTFS отключено");
            }
            else
            {
                await _psService.SetNtfsInheritanceAsync(_currentServer.Hostname, path, disableInheritance: false, preserveInheritedRules: true);
                ShowToast("Наследование NTFS включено");
            }

            await LoadModalNtfsPermissionsAsync(path);
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка изменения наследования NTFS: {ex.Message}", isError: true);
        }
    }

    // ==================== MODAL 2: REMOTE SERVER FOLDER BROWSER ====================

    private async void OpenRemoteFolderBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer == null) return;

        FolderBrowserModalOverlay.Visibility = Visibility.Visible;
        BrowserServerSubtitle.Text = $"Сервер: {_currentServer.EffectiveTitle} ({_currentServer.Hostname})";

        try
        {
            var disks = await _psService.GetRemoteDisksAsync(_currentServer.Hostname);
            RemoteDrivesItemsControl.ItemsSource = disks;

            string initialPath = EditSharePathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(initialPath) || !initialPath.Contains(':'))
            {
                initialPath = disks.FirstOrDefault()?.RootPath ?? @"C:\";
            }

            await NavigateRemoteFolderBrowserAsync(initialPath);
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось получить список дисков сервера: {ex.Message}", isError: true);
        }
    }

    private async Task NavigateRemoteFolderBrowserAsync(string path)
    {
        if (_currentServer == null) return;

        try
        {
            var subfolders = await _psService.GetRemoteSubfoldersAsync(_currentServer.Hostname, path);
            _currentRemoteBrowsePath = path;
            RemoteBrowserCurrentPathBox.Text = path;
            RemoteBrowserSelectedPreview.Text = $"Выбрано: {path}";
            RemoteFoldersListBox.ItemsSource = subfolders;
        }
        catch
        {
            // Fallback to drive root if path doesn't exist yet
            string root = Path.GetPathRoot(path) ?? @"C:\";
            try
            {
                var subfolders = await _psService.GetRemoteSubfoldersAsync(_currentServer.Hostname, root);
                _currentRemoteBrowsePath = root;
                RemoteBrowserCurrentPathBox.Text = root;
                RemoteBrowserSelectedPreview.Text = $"Выбрано: {root}";
                RemoteFoldersListBox.ItemsSource = subfolders;
            }
            catch (Exception ex)
            {
                ShowToast($"Ошибка открытия каталога: {ex.Message}", isError: true);
            }
        }
    }

    private async void SelectRemoteDrive_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RemoteDiskItem disk)
        {
            await NavigateRemoteFolderBrowserAsync(disk.RootPath);
        }
    }

    private async void RemoteFolderUp_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentRemoteBrowsePath)) return;
        string trimmed = _currentRemoteBrowsePath.TrimEnd('\\');
        int idx = trimmed.LastIndexOf('\\');
        if (idx >= 2)
        {
            string parent = trimmed.Substring(0, idx + 1);
            await NavigateRemoteFolderBrowserAsync(parent);
        }
        else if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            await NavigateRemoteFolderBrowserAsync(trimmed + @"\");
        }
    }

    private async void RemoteFolderGoToPath_Click(object sender, RoutedEventArgs e)
    {
        await NavigateRemoteFolderBrowserAsync(RemoteBrowserCurrentPathBox.Text.Trim());
    }

    private async void RemoteBrowserCurrentPathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await NavigateRemoteFolderBrowserAsync(RemoteBrowserCurrentPathBox.Text.Trim());
        }
    }

    private void RemoteFoldersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoteFoldersListBox.SelectedItem is RemoteFolderItem folder)
        {
            RemoteBrowserSelectedPreview.Text = $"Выбрано: {folder.FullPath}";
        }
    }

    private async void RemoteFoldersListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RemoteFoldersListBox.SelectedItem is RemoteFolderItem folder)
        {
            await NavigateRemoteFolderBrowserAsync(folder.FullPath);
        }
    }

    private async void EnterRemoteSubfolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RemoteFolderItem folder)
        {
            await NavigateRemoteFolderBrowserAsync(folder.FullPath);
        }
    }

    private async void CreateNewRemoteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer == null) return;
        string newName = NewRemoteFolderNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            ShowToast("Введите имя новой папки", isError: true);
            return;
        }

        try
        {
            string createdPath = await _psService.CreateRemoteFolderAsync(_currentServer.Hostname, _currentRemoteBrowsePath, newName);
            NewRemoteFolderNameBox.Text = "";
            ShowToast($"Папка «{newName}» создана на сервере");
            await NavigateRemoteFolderBrowserAsync(createdPath);
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось создать папку: {ex.Message}", isError: true);
        }
    }

    private void ConfirmRemoteFolderSelection_Click(object sender, RoutedEventArgs e)
    {
        string chosen = _currentRemoteBrowsePath;
        if (RemoteFoldersListBox.SelectedItem is RemoteFolderItem selectedSub)
        {
            chosen = selectedSub.FullPath;
        }

        EditSharePathBox.Text = chosen;
        if (string.IsNullOrWhiteSpace(EditShareNameBox.Text))
        {
            string leaf = Path.GetFileName(chosen.TrimEnd('\\'));
            if (!string.IsNullOrWhiteSpace(leaf))
            {
                EditShareNameBox.Text = leaf;
            }
        }
        FolderBrowserModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void CloseFolderBrowser_Click(object sender, RoutedEventArgs e)
    {
        FolderBrowserModalOverlay.Visibility = Visibility.Collapsed;
    }

    // ==================== MODAL 3: ACTIVE DIRECTORY & LOCAL PRINCIPAL PICKER ====================

    private async void OpenPrincipalPickerForSmb_Click(object sender, RoutedEventArgs e)
    {
        _principalTargetMode = "SMB";
        PrincipalSearchQueryBox.Text = SmbNewAccountBox.Text.Trim();
        PrincipalPickerModalOverlay.Visibility = Visibility.Visible;
        await RunPrincipalSearchAsync(PrincipalSearchQueryBox.Text);
    }

    private async void OpenPrincipalPickerForNtfs_Click(object sender, RoutedEventArgs e)
    {
        _principalTargetMode = "NTFS";
        PrincipalSearchQueryBox.Text = NtfsNewAccountBox.Text.Trim();
        PrincipalPickerModalOverlay.Visibility = Visibility.Visible;
        await RunPrincipalSearchAsync(PrincipalSearchQueryBox.Text);
    }

    private async void ExecutePrincipalSearch_Click(object sender, RoutedEventArgs e)
    {
        await RunPrincipalSearchAsync(PrincipalSearchQueryBox.Text.Trim());
    }

    private async void PrincipalSearchQueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await RunPrincipalSearchAsync(PrincipalSearchQueryBox.Text.Trim());
        }
    }

    private async Task RunPrincipalSearchAsync(string query)
    {
        if (_currentServer == null) return;
        PrincipalSearchStatusText.Text = "Поиск учетных записей и групп в Active Directory и на сервере...";
        try
        {
            var results = await _psService.SearchPrincipalsAsync(_currentServer.Hostname, query);
            PrincipalsResultListBox.ItemsSource = results;
            PrincipalSearchStatusText.Text = $"Найдено учетных записей и групп: {results.Count}";
        }
        catch (Exception ex)
        {
            PrincipalSearchStatusText.Text = $"Ошибка поиска: {ex.Message}";
        }
    }

    private void PrincipalsResultListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ConfirmPrincipalSelection();
    }

    private void ConfirmPrincipalSelection_Click(object sender, RoutedEventArgs e)
    {
        ConfirmPrincipalSelection();
    }

    private void ConfirmPrincipalSelection()
    {
        if (PrincipalsResultListBox.SelectedItem is not DirectoryPrincipalItem item)
        {
            ShowToast("Выберите пользователя или группу из списка", isError: true);
            return;
        }

        if (_principalTargetMode == "SMB")
        {
            SmbNewAccountBox.Text = item.AccountName;
        }
        else
        {
            NtfsNewAccountBox.Text = item.AccountName;
        }

        PrincipalPickerModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void ClosePrincipalPicker_Click(object sender, RoutedEventArgs e)
    {
        PrincipalPickerModalOverlay.Visibility = Visibility.Collapsed;
    }

    // ==================== SESSIONS & OPEN FILES MANAGEMENT ====================

    private void ApplySessionFilters()
    {
        if (SessionsItemsControl == null) return;
        string q = (SessionSearchBox?.Text ?? "").Trim();
        var filtered = _allServerSessions.Where(s =>
            string.IsNullOrEmpty(q)
            || s.ClientUserName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || s.ClientComputerName.Contains(q, StringComparison.OrdinalIgnoreCase)
        ).ToList();
        SessionsItemsControl.ItemsSource = filtered;
    }

    private void SessionSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySessionFilters();

    private async void RefreshSessions_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer == null) return;
        try
        {
            _allServerSessions = await _psService.GetSmbSessionsAsync(_currentServer.Hostname);
            ApplySessionFilters();
            UpdateTabCounters();
            ShowToast("Список активных SMB-сессий обновлен");
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка получения сессий: {ex.Message}", isError: true);
        }
    }

    private async void DisconnectSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SmbSessionItem sess || _currentServer == null)
            return;

        try
        {
            await _psService.CloseSmbSessionAsync(_currentServer.Hostname, sess.SessionId);
            ShowToast($"Сессия {sess.ClientUserName} ({sess.ClientComputerName}) сброшена");
            _allServerSessions = await _psService.GetSmbSessionsAsync(_currentServer.Hostname);
            ApplySessionFilters();
            UpdateTabCounters();
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка сброса сессии: {ex.Message}", isError: true);
        }
    }

    private void ApplyOpenFileFilters()
    {
        if (OpenFilesItemsControl == null) return;
        string q = (OpenFileSearchBox?.Text ?? "").Trim();
        var filtered = _allServerOpenFiles.Where(f =>
            string.IsNullOrEmpty(q)
            || f.Path.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.ClientUserName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.ClientComputerName.Contains(q, StringComparison.OrdinalIgnoreCase)
        ).ToList();
        OpenFilesItemsControl.ItemsSource = filtered;
    }

    private void OpenFileSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyOpenFileFilters();

    private async void RefreshOpenFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer == null) return;
        try
        {
            _allServerOpenFiles = await _psService.GetSmbOpenFilesAsync(_currentServer.Hostname);
            ApplyOpenFileFilters();
            UpdateTabCounters();
            ShowToast("Список открытых файлов обновлен");
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка получения открытых файлов: {ex.Message}", isError: true);
        }
    }

    private async void CloseOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SmbOpenFileItem file || _currentServer == null)
            return;

        try
        {
            await _psService.CloseSmbOpenFileAsync(_currentServer.Hostname, file.FileId);
            ShowToast($"Открытый файл принудительно закрыт (ID {file.FileId})");
            _allServerOpenFiles = await _psService.GetSmbOpenFilesAsync(_currentServer.Hostname);
            ApplyOpenFileFilters();
            UpdateTabCounters();
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось закрыть файл: {ex.Message}", isError: true);
        }
    }

    // ==================== MODAL 4: ADD / EDIT SERVER & AD DISCOVERY ====================

    private void OpenAddServerModal_Click(object sender, RoutedEventArgs e)
    {
        _isEditingServer = false;
        ManualServerPanel.Visibility = Visibility.Visible;
        AdDiscoveryPanel.Visibility = Visibility.Collapsed;
        ServerModalTitleText.Text = "Добавить сервер Windows";
        ServerHostnameInput.Text = "";
        ServerDisplayNameInput.Text = "";
        ServerDescriptionInput.Text = "";
        DeleteServerBtn.Visibility = Visibility.Collapsed;
        ServerModalOverlay.Visibility = Visibility.Visible;
    }

    private void EditCurrentServer_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer == null) return;
        _isEditingServer = true;
        ManualServerPanel.Visibility = Visibility.Visible;
        AdDiscoveryPanel.Visibility = Visibility.Collapsed;
        ServerModalTitleText.Text = $"Настройки сервера {_currentServer.EffectiveTitle}";
        ServerHostnameInput.Text = _currentServer.Hostname;
        ServerDisplayNameInput.Text = _currentServer.DisplayName;
        ServerDescriptionInput.Text = _currentServer.Description;
        DeleteServerBtn.Visibility = _servers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ServerModalOverlay.Visibility = Visibility.Visible;
    }

    private void SaveManualServer_Click(object sender, RoutedEventArgs e)
    {
        string host = ServerHostnameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            ShowToast("Введите имя хоста или IP-адрес сервера", isError: true);
            return;
        }

        string disp = ServerDisplayNameInput.Text.Trim();
        string desc = ServerDescriptionInput.Text.Trim();

        if (_isEditingServer && _currentServer != null)
        {
            _currentServer.Hostname = host;
            _currentServer.DisplayName = disp;
            _currentServer.Description = desc;
            SaveServersToConfig();
            ServerModalOverlay.Visibility = Visibility.Collapsed;
            _ = LoadServerDataAsync(_currentServer);
        }
        else
        {
            var newSrv = new ServerNode
            {
                Hostname = host,
                DisplayName = disp,
                Description = desc
            };
            _servers.Add(newSrv);
            SaveServersToConfig();
            ServerModalOverlay.Visibility = Visibility.Collapsed;
            ServersListBox.SelectedItem = newSrv;
        }
    }

    private void DeleteCurrentServer_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServer == null || _servers.Count <= 1) return;
        var toRemove = _currentServer;
        _servers.Remove(toRemove);
        SaveServersToConfig();
        ServerModalOverlay.Visibility = Visibility.Collapsed;
        ServersListBox.SelectedIndex = 0;
        ShowToast($"Сервер {toRemove.EffectiveTitle} удален из списка");
    }

    private async void OpenAdImportModal_Click(object sender, RoutedEventArgs e)
    {
        ManualServerPanel.Visibility = Visibility.Collapsed;
        AdDiscoveryPanel.Visibility = Visibility.Visible;
        ServerModalOverlay.Visibility = Visibility.Visible;
        await RunAdServerSearchAsync("");
    }

    private async void SearchAdServers_Click(object sender, RoutedEventArgs e)
    {
        await RunAdServerSearchAsync(AdServerFilterBox.Text.Trim());
    }

    private async Task RunAdServerSearchAsync(string filter)
    {
        AdDiscoveryStatusText.Text = "Поиск серверов в Active Directory...";
        try
        {
            var found = await _psService.DiscoverAdServersAsync(filter);
            AdDiscoveredServersList.ItemsSource = found;
            AdDiscoveryStatusText.Text = $"Найдено серверов в AD: {found.Count}";
        }
        catch (Exception ex)
        {
            AdDiscoveredServersList.ItemsSource = null;
            AdDiscoveryStatusText.Text = $"Ошибка: {ex.Message}";
        }
    }

    private void ImportSelectedAdServers_Click(object sender, RoutedEventArgs e)
    {
        if (AdDiscoveredServersList.ItemsSource is not IEnumerable<AdServerCandidate> candidates)
            return;

        int added = 0;
        ServerNode? lastAdded = null;
        foreach (var c in candidates.Where(x => x.IsSelected))
        {
            bool exists = _servers.Any(s =>
                s.Hostname.Equals(c.EffectiveHost, StringComparison.OrdinalIgnoreCase) ||
                s.Hostname.Equals(c.Name, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                lastAdded = new ServerNode
                {
                    Hostname = c.EffectiveHost,
                    DisplayName = c.Name,
                    Description = c.OperatingSystem
                };
                _servers.Add(lastAdded);
                added++;
            }
        }

        SaveServersToConfig();
        ServerModalOverlay.Visibility = Visibility.Collapsed;

        if (lastAdded != null)
        {
            ServersListBox.SelectedItem = lastAdded;
        }
        ShowToast($"Добавлено серверов из AD: {added}");
        _ = CheckAllServersStatusInBackgroundAsync();
    }

    private void CloseServerModal_Click(object sender, RoutedEventArgs e)
    {
        ServerModalOverlay.Visibility = Visibility.Collapsed;
    }

    // ==================== SETTINGS & DYNAMIC THEME ENGINE ====================

    private void PopulateSettingsUiFromConfig()
    {
        _isLoadingSettingsUi = true;
        try
        {
            SelectComboByTag(SettingsThemeCombo, _config.ThemeName);
            SettingsGlassToggle.IsChecked = _config.EnableGlassEffects;
            SettingsDefaultPathBox.Text = _config.DefaultBaseSharePath;
            SettingsDefaultAbeToggle.IsChecked = _config.DefaultAbeEnabled;
            SettingsDefaultCreateDirToggle.IsChecked = _config.DefaultCreateFolderIfMissing;
            SelectComboByTag(SettingsAutoRefreshCombo, _config.AutoRefreshIntervalSeconds.ToString());
            SelectComboByTag(SettingsTimeoutCombo, _config.WinRmTimeoutSeconds.ToString());
            SettingsConfirmDangerToggle.IsChecked = _config.ConfirmDangerousActions;
        }
        finally
        {
            _isLoadingSettingsUi = false;
        }
    }

    private void SettingsThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettingsUi || !IsLoaded) return;
        string theme = GetSelectedComboTag(SettingsThemeCombo, "CyberEmerald");
        _config.ThemeName = theme;
        ApplyThemeAndGlass(_config.ThemeName, _config.EnableGlassEffects);
        SaveServersToConfig();
        ShowToast($"Тема оформления изменена: {theme}");
    }

    private void SettingsGlassToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettingsUi || !IsLoaded) return;
        _config.EnableGlassEffects = SettingsGlassToggle.IsChecked == true;
        ApplyThemeAndGlass(_config.ThemeName, _config.EnableGlassEffects);
        SaveServersToConfig();
    }

    private void SettingsInputs_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettingsUi || !IsLoaded) return;
        _config.DefaultBaseSharePath = SettingsDefaultPathBox.Text.Trim();
        _config.DefaultAbeEnabled = SettingsDefaultAbeToggle.IsChecked == true;
        _config.DefaultCreateFolderIfMissing = SettingsDefaultCreateDirToggle.IsChecked == true;
        _config.ConfirmDangerousActions = SettingsConfirmDangerToggle.IsChecked == true;
        SaveServersToConfig();
    }

    private void SettingsAutoRefreshCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettingsUi || !IsLoaded) return;
        int.TryParse(GetSelectedComboTag(SettingsAutoRefreshCombo, "0"), out int sec);
        _config.AutoRefreshIntervalSeconds = sec;
        ConfigureAutoRefreshTimer(sec);
        SaveServersToConfig();
        ShowToast(sec > 0 ? $"Автообновление включено (каждые {sec} сек.)" : "Автообновление выключено");
    }

    private void SettingsTimeoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettingsUi || !IsLoaded) return;
        int.TryParse(GetSelectedComboTag(SettingsTimeoutCombo, "25"), out int sec);
        if (sec < 10) sec = 25;
        _config.WinRmTimeoutSeconds = sec;
        _psService.DefaultTimeoutMs = sec * 1000;
        SaveServersToConfig();
        ShowToast($"Таймаут команд WinRM установлен: {sec} сек.");
    }

    private void ConfigureAutoRefreshTimer(int intervalSeconds)
    {
        _autoRefreshTimer?.Stop();
        if (intervalSeconds <= 0)
            return;

        _autoRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(intervalSeconds)
        };
        _autoRefreshTimer.Tick += async (_, _) =>
        {
            if (_currentServer != null &&
                ShareEditorModalOverlay.Visibility != Visibility.Visible &&
                FolderBrowserModalOverlay.Visibility != Visibility.Visible &&
                PrincipalPickerModalOverlay.Visibility != Visibility.Visible &&
                ServerModalOverlay.Visibility != Visibility.Visible)
            {
                await LoadServerDataAsync(_currentServer);
            }
        };
        _autoRefreshTimer.Start();
    }

    private async void ResetWinRmPool_Click(object sender, RoutedEventArgs e)
    {
        await _psService.ResetSessionPoolAsync();
        ShowToast("Пул сессий WinRM / PowerShell успешно перезапущен");
        if (_currentServer != null)
        {
            await LoadServerDataAsync(_currentServer);
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = Path.GetDirectoryName(ConfigService.GetConfigFilePath())!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка открытия папки конфига: {ex.Message}", isError: true);
        }
    }

    private void ApplyThemeAndGlass(string themeName, bool enableGlass)
    {
        if (AmbientGlassLayer != null)
        {
            AmbientGlassLayer.Visibility = enableGlass ? Visibility.Visible : Visibility.Collapsed;
        }

        byte cardAlpha = enableGlass ? (byte)0xC4 : (byte)0xFF;
        byte sidebarAlpha = enableGlass ? (byte)0xC8 : (byte)0xFF;

        var res = Application.Current.Resources;

        switch (themeName)
        {
            case "NeonMatrix":
                res["BgAppBrush"] = new SolidColorBrush(Color.FromRgb(2, 4, 3));
                res["BgSidebarBrush"] = new SolidColorBrush(Color.FromArgb(sidebarAlpha, 4, 10, 6));
                res["BgHeaderBrush"] = new SolidColorBrush(Color.FromArgb(sidebarAlpha, 5, 12, 7));
                res["BgCardBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 7, 17, 10));
                res["BgCardHoverBrush"] = new SolidColorBrush(Color.FromArgb(0xE0, 11, 28, 16));
                res["BgCardSelectedBrush"] = new SolidColorBrush(Color.FromArgb(0xEE, 14, 40, 22));
                res["BgModalBrush"] = new SolidColorBrush(Color.FromArgb(0xF6, 5, 12, 8));
                res["BgInputBrush"] = new SolidColorBrush(Color.FromArgb(0xE0, 3, 7, 4));
                res["BorderSubtleBrush"] = new SolidColorBrush(Color.FromArgb(0x45, 0, 255, 102));
                res["BorderHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x99, 0, 255, 102));
                res["BorderFocusBrush"] = new SolidColorBrush(Color.FromRgb(0, 255, 102));
                res["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0, 230, 90));
                res["AccentBrightBrush"] = new SolidColorBrush(Color.FromRgb(57, 255, 20));
                res["AccentTextBrush"] = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                res["AccentSubtleBrush"] = new SolidColorBrush(Color.FromRgb(10, 36, 18));
                res["AmbientOrb1Brush"] = new SolidColorBrush(Color.FromArgb(0x30, 0, 255, 80));
                res["AmbientOrb2Brush"] = new SolidColorBrush(Color.FromArgb(0x25, 16, 185, 129));
                break;

            case "ObsidianMint":
                res["BgAppBrush"] = new SolidColorBrush(Color.FromRgb(10, 14, 13));
                res["BgSidebarBrush"] = new SolidColorBrush(Color.FromArgb(sidebarAlpha, 15, 21, 19));
                res["BgHeaderBrush"] = new SolidColorBrush(Color.FromArgb(sidebarAlpha, 16, 23, 21));
                res["BgCardBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 21, 30, 27));
                res["BgCardHoverBrush"] = new SolidColorBrush(Color.FromArgb(0xE0, 28, 40, 36));
                res["BgCardSelectedBrush"] = new SolidColorBrush(Color.FromArgb(0xEE, 26, 48, 41));
                res["BgModalBrush"] = new SolidColorBrush(Color.FromArgb(0xF6, 16, 23, 21));
                res["BgInputBrush"] = new SolidColorBrush(Color.FromArgb(0xE0, 11, 16, 15));
                res["BorderSubtleBrush"] = new SolidColorBrush(Color.FromArgb(0x38, 45, 212, 191));
                res["BorderHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x77, 45, 212, 191));
                res["BorderFocusBrush"] = new SolidColorBrush(Color.FromRgb(45, 212, 191));
                res["AccentBrush"] = new SolidColorBrush(Color.FromRgb(20, 184, 166));
                res["AccentBrightBrush"] = new SolidColorBrush(Color.FromRgb(45, 212, 191));
                res["AccentTextBrush"] = new SolidColorBrush(Color.FromRgb(94, 234, 212));
                res["AccentSubtleBrush"] = new SolidColorBrush(Color.FromRgb(19, 42, 39));
                res["AmbientOrb1Brush"] = new SolidColorBrush(Color.FromArgb(0x22, 45, 212, 191));
                res["AmbientOrb2Brush"] = new SolidColorBrush(Color.FromArgb(0x1E, 16, 185, 129));
                break;

            case "CyberBlue":
                res["BgAppBrush"] = new SolidColorBrush(Color.FromRgb(6, 10, 17));
                res["BgSidebarBrush"] = new SolidColorBrush(Color.FromArgb(sidebarAlpha, 10, 17, 28));
                res["BgHeaderBrush"] = new SolidColorBrush(Color.FromArgb(sidebarAlpha, 12, 20, 32));
                res["BgCardBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 15, 25, 40));
                res["BgCardHoverBrush"] = new SolidColorBrush(Color.FromArgb(0xE0, 22, 36, 56));
                res["BgCardSelectedBrush"] = new SolidColorBrush(Color.FromArgb(0xEE, 20, 44, 72));
                res["BgModalBrush"] = new SolidColorBrush(Color.FromArgb(0xF6, 12, 20, 32));
                res["BgInputBrush"] = new SolidColorBrush(Color.FromArgb(0xE0, 8, 13, 22));
                res["BorderSubtleBrush"] = new SolidColorBrush(Color.FromArgb(0x38, 56, 189, 248));
                res["BorderHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x77, 56, 189, 248));
                res["BorderFocusBrush"] = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                res["AccentBrush"] = new SolidColorBrush(Color.FromRgb(14, 165, 233));
                res["AccentBrightBrush"] = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                res["AccentTextBrush"] = new SolidColorBrush(Color.FromRgb(125, 211, 252));
                res["AccentSubtleBrush"] = new SolidColorBrush(Color.FromRgb(15, 38, 62));
                res["AmbientOrb1Brush"] = new SolidColorBrush(Color.FromArgb(0x24, 56, 189, 248));
                res["AmbientOrb2Brush"] = new SolidColorBrush(Color.FromArgb(0x1E, 16, 185, 129));
                break;

            default: // CyberEmerald (Crazy Monkeys Default)
                res["BgAppBrush"] = new SolidColorBrush(Color.FromRgb(5, 9, 7));
                res["BgSidebarBrush"] = new SolidColorBrush(Color.FromArgb(sidebarAlpha, 8, 17, 12));
                res["BgHeaderBrush"] = new SolidColorBrush(Color.FromArgb(sidebarAlpha, 9, 19, 14));
                res["BgCardBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 12, 24, 18));
                res["BgCardHoverBrush"] = new SolidColorBrush(Color.FromArgb(0xDC, 18, 38, 28));
                res["BgCardSelectedBrush"] = new SolidColorBrush(Color.FromArgb(0xE8, 20, 51, 35));
                res["BgModalBrush"] = new SolidColorBrush(Color.FromArgb(0xF2, 10, 20, 15));
                res["BgInputBrush"] = new SolidColorBrush(Color.FromArgb(0xD8, 6, 12, 9));
                res["BorderSubtleBrush"] = new SolidColorBrush(Color.FromArgb(0x38, 16, 185, 129));
                res["BorderHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x77, 16, 185, 129));
                res["BorderFocusBrush"] = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                res["AccentBrush"] = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                res["AccentBrightBrush"] = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                res["AccentTextBrush"] = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                res["AccentSubtleBrush"] = new SolidColorBrush(Color.FromRgb(18, 43, 32));
                res["AmbientOrb1Brush"] = new SolidColorBrush(Color.FromArgb(0x26, 0, 255, 102));
                res["AmbientOrb2Brush"] = new SolidColorBrush(Color.FromArgb(0x1F, 16, 185, 129));
                break;
        }
    }

    // ==================== DFS NAMESPACES MANAGEMENT ====================

    private async void DiscoverDfsRoots_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hostnames = _servers.Select(s => s.Hostname).ToList();
            var discovered = await DfsService.DiscoverNamespacesAsync(hostnames);
            int added = 0;
            foreach (var d in discovered)
            {
                if (!_dfsRoots.Any(x => x.RootPath.Equals(d.RootPath, StringComparison.OrdinalIgnoreCase)))
                {
                    _dfsRoots.Add(d);
                    added++;
                }
            }
            SaveServersToConfig();
            UpdateTabCounters();

            if (DfsRootsListBox.SelectedItem == null && _dfsRoots.Count > 0)
            {
                DfsRootsListBox.SelectedIndex = 0;
            }

            if (added > 0)
                ShowToast($"Найдено и добавлено пространств имен DFS: {added}");
            else if (_dfsRoots.Count > 0)
                ShowToast("Новых корней DFS не обнаружено (все доступные уже в списке)");
            else
                ShowToast("Пространства имен DFS не обнаружены. Вы можете подключить корень вручную или создать новый на сервере.");
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка автопоиска DFS: {ex.Message}", isError: true);
        }
    }

    private async void DfsRootsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DfsRootsListBox.SelectedItem is not DfsNamespaceRoot root)
        {
            _selectedDfsRoot = null;
            _currentDfsFolders.Clear();
            ApplyDfsFolderFilter();
            return;
        }

        _selectedDfsRoot = root;
        await LoadSelectedDfsRootFoldersAsync(root);
    }

    private async Task LoadSelectedDfsRootFoldersAsync(DfsNamespaceRoot root)
    {
        try
        {
            _currentDfsFolders = await DfsService.GetDfsFoldersAsync(root.RootPath);
            root.FolderCount = _currentDfsFolders.Count(f => !f.IsRootEntry);
            ApplyDfsFolderFilter();
        }
        catch (Exception ex)
        {
            _currentDfsFolders.Clear();
            ApplyDfsFolderFilter();
            ShowToast($"Ошибка чтения DFS '{root.RootPath}': {ex.Message}", isError: true);
        }
    }

    private void ApplyDfsFolderFilter()
    {
        if (DfsFoldersListBox == null) return;

        if (_selectedDfsRoot == null)
        {
            DfsFoldersListBox.ItemsSource = null;
            DfsEmptyStatePanel.Visibility = Visibility.Visible;
            DfsInspectorEmptyState.Visibility = Visibility.Visible;
            DfsInspectorContentScroll.Visibility = Visibility.Collapsed;
            return;
        }

        DfsEmptyStatePanel.Visibility = Visibility.Collapsed;
        string q = (DfsFolderSearchBox?.Text ?? "").Trim();

        var filtered = _currentDfsFolders.Where(f =>
        {
            if (string.IsNullOrEmpty(q)) return true;
            return f.EntryPath.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || f.RelativeFolderName.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || f.Comment.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || f.Targets.Any(t => t.TargetUncPath.Contains(q, StringComparison.OrdinalIgnoreCase));
        }).ToList();

        DfsFoldersListBox.ItemsSource = filtered;
        if (filtered.Count > 0)
        {
            DfsFoldersListBox.SelectedIndex = 0;
        }
        else
        {
            _selectedDfsFolder = null;
            DfsInspectorEmptyState.Visibility = Visibility.Visible;
            DfsInspectorContentScroll.Visibility = Visibility.Collapsed;
        }
    }

    private void DfsFolderSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyDfsFolderFilter();
    }

    private async void RefreshSelectedDfsRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDfsRoot != null)
        {
            await LoadSelectedDfsRootFoldersAsync(_selectedDfsRoot);
            ShowToast($"Папки DFS в {_selectedDfsRoot.RootPath} обновлены");
        }
    }

    private void DfsFoldersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DfsFoldersListBox.SelectedItem is not DfsFolderItem folder)
        {
            _selectedDfsFolder = null;
            DfsInspectorEmptyState.Visibility = Visibility.Visible;
            DfsInspectorContentScroll.Visibility = Visibility.Collapsed;
            return;
        }

        _selectedDfsFolder = folder;
        DfsInspectorEmptyState.Visibility = Visibility.Collapsed;
        DfsInspectorContentScroll.Visibility = Visibility.Visible;

        DfsInspFolderName.Text = folder.RelativeFolderName;
        DfsInspEntryPath.Text = folder.EntryPath;
        DfsInspTargetsList.ItemsSource = folder.Targets.ToList();
    }

    private void CopyDfsPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DfsFolderItem folder)
        {
            Clipboard.SetText(folder.EntryPath);
            ShowToast($"Путь DFS скопирован: {folder.EntryPath}");
        }
    }

    private void OpenDfsFolderInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DfsFolderItem folder)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = folder.EntryPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowToast($"Ошибка открытия Проводника: {ex.Message}", isError: true);
            }
        }
    }

    private void RemoveDfsRootFromList_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DfsNamespaceRoot root)
        {
            _dfsRoots.Remove(root);
            SaveServersToConfig();
            UpdateTabCounters();
            if (_dfsRoots.Count > 0 && DfsRootsListBox.SelectedItem == null)
                DfsRootsListBox.SelectedIndex = 0;
        }
    }

    // ---- Modal Mode B: Connect Existing DFS Root ----

    private void OpenAddDfsRootModal_Click(object sender, RoutedEventArgs e)
    {
        DfsFolderTargetModalPanel.Visibility = Visibility.Collapsed;
        DfsConnectRootModalPanel.Visibility = Visibility.Visible;
        DfsCreateNewRootModalPanel.Visibility = Visibility.Collapsed;

        string dom = Environment.GetEnvironmentVariable("USERDNSDOMAIN") ?? Environment.MachineName;
        DfsConnectRootInputBox.Text = $@"\\{dom}\";
        DfsModalOverlay.Visibility = Visibility.Visible;
    }

    private async void ConfirmConnectDfsRoot_Click(object sender, RoutedEventArgs e)
    {
        string raw = DfsConnectRootInputBox.Text.Trim();
        string norm = DfsService.NormalizeUncPath(raw);
        if (string.IsNullOrWhiteSpace(norm) || norm.Count(c => c == '\\') < 3)
        {
            ShowToast("Введите корректный UNC-путь корня DFS в формате \\\\домен_или_сервер\\ИмяКорня", isError: true);
            return;
        }

        try
        {
            var folders = await DfsService.GetDfsFoldersAsync(norm);
            var existing = _dfsRoots.FirstOrDefault(x => x.RootPath.Equals(norm, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new DfsNamespaceRoot
                {
                    RootPath = norm,
                    Flavor = "Connected",
                    Description = "Подключено вручную",
                    FolderCount = folders.Count(f => !f.IsRootEntry)
                };
                _dfsRoots.Add(existing);
                SaveServersToConfig();
                UpdateTabCounters();
            }

            DfsModalOverlay.Visibility = Visibility.Collapsed;
            SwitchMainWorkspaceTab(4);
            DfsRootsListBox.SelectedItem = existing;
            ShowToast($"Пространство имен DFS «{norm}» успешно подключено");
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось подключить '{norm}': {ex.Message}", isError: true);
        }
    }

    // ---- Modal Mode C: Create New DFS Root on Server ----

    private void OpenCreateNewDfsRootOnServerModal_Click(object sender, RoutedEventArgs e)
    {
        DfsFolderTargetModalPanel.Visibility = Visibility.Collapsed;
        DfsConnectRootModalPanel.Visibility = Visibility.Collapsed;
        DfsCreateNewRootModalPanel.Visibility = Visibility.Visible;

        DfsNewRootServerBox.Text = _currentServer?.Hostname ?? Environment.MachineName;
        DfsNewRootNameBox.Text = "Shares";
        DfsModalOverlay.Visibility = Visibility.Visible;
    }

    private async void ExecuteCreateNewDfsRoot_Click(object sender, RoutedEventArgs e)
    {
        string srv = DfsNewRootServerBox.Text.Trim();
        string rName = DfsNewRootNameBox.Text.Trim();
        string rType = GetSelectedComboTag(DfsNewRootTypeCombo, "DomainV2");
        bool installRole = DfsInstallFeatureCheck.IsChecked == true;

        if (string.IsNullOrWhiteSpace(srv) || string.IsNullOrWhiteSpace(rName))
        {
            ShowToast("Укажите имя сервера и название нового корня DFS", isError: true);
            return;
        }

        CreateNewDfsRootBtn.IsEnabled = false;
        try
        {
            ShowToast($"Развертывание корня DFS '{rName}' на сервере {srv}...");
            string createdRootPath = await DfsService.CreateDfsNamespaceRootOnServerAsync(_psService, srv, rName, rType, installRole);
            string norm = DfsService.NormalizeUncPath(createdRootPath);

            var existing = _dfsRoots.FirstOrDefault(x => x.RootPath.Equals(norm, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new DfsNamespaceRoot
                {
                    RootPath = norm,
                    Flavor = rType,
                    Description = $"Создано на {srv}"
                };
                _dfsRoots.Add(existing);
                SaveServersToConfig();
                UpdateTabCounters();
            }

            DfsModalOverlay.Visibility = Visibility.Collapsed;
            SwitchMainWorkspaceTab(4);
            DfsRootsListBox.SelectedItem = existing;
            ShowToast($"Корень DFS «{norm}» успешно создан!");
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка создания корня DFS: {ex.Message}", isError: true);
        }
        finally
        {
            CreateNewDfsRootBtn.IsEnabled = true;
        }
    }

    // ---- Modal Mode A: Create DFS Folder or Add Target / Publish SMB Share to DFS ----

    private void PopulateQuickShareComboForDfsModal()
    {
        string srvHost = _currentServer?.Hostname ?? Environment.MachineName;
        if (srvHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            srvHost = Environment.MachineName;

        var items = _allServerShares
            .Where(s => !s.IsSystemOrAdmin)
            .Select(s => new ComboBoxItem
            {
                Content = $@"\\{srvHost}\{s.Name} ({s.Path})",
                Tag = $"{srvHost}|{s.Name}"
            })
            .ToList();

        DfsModalQuickShareCombo.ItemsSource = items;
        DfsModalQuickShareCombo.SelectedIndex = -1;
    }

    private void DfsModalQuickShareCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DfsModalQuickShareCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            var parts = tag.Split('|');
            if (parts.Length == 2)
            {
                DfsModalTargetServerBox.Text = parts[0];
                DfsModalTargetShareBox.Text = parts[1];
                if (string.IsNullOrWhiteSpace(DfsModalFolderNameBox.Text) && DfsModalFolderNameBox.IsEnabled)
                {
                    DfsModalFolderNameBox.Text = parts[1].TrimEnd('$');
                }
            }
        }
    }

    private void OpenCreateDfsFolderModal_Click(object sender, RoutedEventArgs e)
    {
        OpenDfsFolderOrTargetModal(
            rootPath: _selectedDfsRoot?.RootPath ?? "",
            folderRelName: "",
            targetServer: _currentServer?.Hostname ?? Environment.MachineName,
            targetShare: "",
            comment: "",
            isAddingTargetOnly: false);
    }

    private void OpenAddTargetToDfsFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DfsFolderItem folder)
        {
            OpenDfsFolderOrTargetModal(
                rootPath: folder.RootPath,
                folderRelName: folder.IsRootEntry ? "" : folder.RelativeFolderName,
                targetServer: _currentServer?.Hostname ?? Environment.MachineName,
                targetShare: "",
                comment: folder.Comment,
                isAddingTargetOnly: true);
        }
    }

    private void OpenAddTargetForSelectedDfsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDfsFolder != null)
        {
            OpenDfsFolderOrTargetModal(
                rootPath: _selectedDfsFolder.RootPath,
                folderRelName: _selectedDfsFolder.IsRootEntry ? "" : _selectedDfsFolder.RelativeFolderName,
                targetServer: _currentServer?.Hostname ?? Environment.MachineName,
                targetShare: "",
                comment: _selectedDfsFolder.Comment,
                isAddingTargetOnly: true);
        }
    }

    private void PublishShareToDfs_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SmbShareItem share)
            return;

        string srv = string.IsNullOrWhiteSpace(share.ServerHostname) || share.ServerHostname == "localhost"
            ? Environment.MachineName
            : share.ServerHostname;

        string defaultRoot = _selectedDfsRoot?.RootPath
                             ?? _dfsRoots.FirstOrDefault()?.RootPath
                             ?? $@"\\{Environment.GetEnvironmentVariable("USERDNSDOMAIN") ?? srv}\Shares";

        OpenDfsFolderOrTargetModal(
            rootPath: defaultRoot,
            folderRelName: share.Name.TrimEnd('$'),
            targetServer: srv,
            targetShare: share.Name,
            comment: share.Description,
            isAddingTargetOnly: false);
    }

    private void OpenDfsFolderOrTargetModal(
        string rootPath,
        string folderRelName,
        string targetServer,
        string targetShare,
        string comment,
        bool isAddingTargetOnly)
    {
        _isAddingTargetToExistingDfsFolder = isAddingTargetOnly;

        DfsFolderTargetModalPanel.Visibility = Visibility.Visible;
        DfsConnectRootModalPanel.Visibility = Visibility.Collapsed;
        DfsCreateNewRootModalPanel.Visibility = Visibility.Collapsed;

        DfsFolderModalTitle.Text = isAddingTargetOnly
            ? "Добавить сервер-цель (Target) в папку DFS"
            : "Создать папку (ссылку) в пространстве имен DFS";

        DfsModalRootPathBox.Text = rootPath;
        DfsModalRootPathBox.IsEnabled = !isAddingTargetOnly;

        DfsModalFolderNameBox.Text = folderRelName;
        DfsModalFolderNameBox.IsEnabled = !isAddingTargetOnly;

        DfsModalTargetServerBox.Text = targetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? Environment.MachineName
            : targetServer;
        DfsModalTargetShareBox.Text = targetShare;
        DfsModalCommentBox.Text = comment;

        PopulateQuickShareComboForDfsModal();
        UpdateDfsModalPreview();
        DfsModalOverlay.Visibility = Visibility.Visible;
    }

    private void DfsModalPathInputs_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateDfsModalPreview();
    }

    private void UpdateDfsModalPreview()
    {
        if (DfsModalFullEntryPreview == null) return;
        string r = DfsService.NormalizeUncPath(DfsModalRootPathBox.Text);
        string f = (DfsModalFolderNameBox.Text ?? "").Trim().Trim('\\');
        DfsModalFullEntryPreview.Text = string.IsNullOrWhiteSpace(f) ? r : $@"{r}\{f}";
    }

    private async void SaveDfsFolderOrTarget_Click(object sender, RoutedEventArgs e)
    {
        string rootPath = DfsService.NormalizeUncPath(DfsModalRootPathBox.Text);
        string folderRel = DfsModalFolderNameBox.Text.Trim().Trim('\\');
        string targetServer = DfsModalTargetServerBox.Text.Trim();
        string targetShare = DfsModalTargetShareBox.Text.Trim();
        string comment = DfsModalCommentBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(rootPath) || rootPath.Count(c => c == '\\') < 3)
        {
            ShowToast("Укажите корректный корень DFS (например \\\\domain\\Root)", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(folderRel) && !_isAddingTargetToExistingDfsFolder)
        {
            ShowToast("Укажите имя папки в пространстве имен DFS", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(targetServer) || string.IsNullOrWhiteSpace(targetShare))
        {
            ShowToast("Укажите целевой сервер и имя шары на сервере", isError: true);
            return;
        }

        string fullEntryPath = string.IsNullOrWhiteSpace(folderRel) ? rootPath : $@"{rootPath}\{folderRel}";

        try
        {
            await DfsService.AddDfsFolderOrTargetAsync(
                fullEntryPath,
                targetServer,
                targetShare,
                comment,
                createNewLink: !_isAddingTargetToExistingDfsFolder);

            // Ensure root is in our list
            var rootNode = _dfsRoots.FirstOrDefault(x => x.RootPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase));
            if (rootNode == null)
            {
                rootNode = new DfsNamespaceRoot
                {
                    RootPath = rootPath,
                    Flavor = "Domain",
                    Description = "Добавлено при публикации"
                };
                _dfsRoots.Add(rootNode);
                SaveServersToConfig();
                UpdateTabCounters();
            }

            DfsModalOverlay.Visibility = Visibility.Collapsed;
            SwitchMainWorkspaceTab(4);
            DfsRootsListBox.SelectedItem = rootNode;
            await LoadSelectedDfsRootFoldersAsync(rootNode);

            var matchFolder = _currentDfsFolders.FirstOrDefault(x => x.EntryPath.Equals(fullEntryPath, StringComparison.OrdinalIgnoreCase));
            if (matchFolder != null)
            {
                DfsFoldersListBox.SelectedItem = matchFolder;
            }

            ShowToast($"Опубликовано в DFS: {fullEntryPath} -> \\\\{targetServer}\\{targetShare}");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, isError: true);
        }
    }

    private async void DeleteDfsFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DfsFolderItem folder || folder.IsRootEntry)
            return;

        if (_config.ConfirmDangerousActions)
        {
            var res = MessageBox.Show(
                $"Удалить папку «{folder.RelativeFolderName}» ({folder.EntryPath}) из пространства имен DFS?\n\nСами сетевые шары и файлы на конечных серверах удалены НЕ будут.",
                "Удаление папки DFS — Folders",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;
        }

        try
        {
            await DfsService.RemoveDfsFolderOrTargetAsync(folder.EntryPath);
            ShowToast($"Папка DFS «{folder.RelativeFolderName}» удалена");
            if (_selectedDfsRoot != null)
                await LoadSelectedDfsRootFoldersAsync(_selectedDfsRoot);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, isError: true);
        }
    }

    private async void ToggleDfsTargetState_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DfsTargetItem target || _selectedDfsFolder == null)
            return;

        try
        {
            bool newOnlineState = !target.IsOnline;
            await DfsService.SetDfsTargetStateAsync(_selectedDfsFolder.EntryPath, target.ServerName, target.ShareName, newOnlineState);
            ShowToast($"Цель {target.TargetUncPath} переведена в состояние: {(newOnlineState ? "Online" : "Offline")}");
            if (_selectedDfsRoot != null)
                await LoadSelectedDfsRootFoldersAsync(_selectedDfsRoot);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, isError: true);
        }
    }

    private async void RemoveDfsTarget_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DfsTargetItem target || _selectedDfsFolder == null)
            return;

        if (_config.ConfirmDangerousActions)
        {
            var res = MessageBox.Show(
                $"Удалить конечную цель «{target.TargetUncPath}» из папки DFS «{_selectedDfsFolder.EntryPath}»?",
                "Удаление цели DFS — Folders",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;
        }

        try
        {
            await DfsService.RemoveDfsFolderOrTargetAsync(_selectedDfsFolder.EntryPath, target.ServerName, target.ShareName);
            ShowToast($"Цель {target.TargetUncPath} удалена из папки DFS");
            if (_selectedDfsRoot != null)
                await LoadSelectedDfsRootFoldersAsync(_selectedDfsRoot);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, isError: true);
        }
    }

    private async void JumpToDfsTargetShare_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DfsTargetItem target)
            return;

        string srvName = target.ServerName.Trim().TrimStart('\\');
        var serverNode = _servers.FirstOrDefault(s =>
            s.Hostname.Equals(srvName, StringComparison.OrdinalIgnoreCase) ||
            (s.IsLocal && srvName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)));

        if (serverNode == null)
        {
            serverNode = new ServerNode
            {
                Hostname = srvName,
                DisplayName = srvName,
                Description = "Добавлен из цели DFS"
            };
            _servers.Add(serverNode);
            SaveServersToConfig();
        }

        SwitchMainWorkspaceTab(0);
        ServersListBox.SelectedItem = serverNode;
        await LoadServerDataAsync(serverNode);

        // If target share is hidden/admin, ensure it's found
        var shareItem = _allServerShares.FirstOrDefault(x => x.Name.Equals(target.ShareName, StringComparison.OrdinalIgnoreCase));
        if (shareItem != null)
        {
            if (shareItem.IsSystemOrAdmin && ShowSystemSharesToggle.IsChecked != true)
                ShowSystemSharesToggle.IsChecked = true;

            SharesListBox.SelectedItem = shareItem;
            await OpenShareEditorModalAsync(shareItem, initialTab: 1);
        }
        else
        {
            ShowToast($"Сервер {srvName} открыт, но шара '{target.ShareName}' не найдена в списке", isError: true);
        }
    }

    private void CloseDfsModal_Click(object sender, RoutedEventArgs e)
    {
        DfsModalOverlay.Visibility = Visibility.Collapsed;
    }

    // ==================== HELPERS & TOAST ====================

    private static string ResolveWellKnownSid(string sidString, string fallback)
    {
        try
        {
            var sid = new SecurityIdentifier(sidString);
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch
        {
            return fallback;
        }
    }

    private static string GetSelectedComboTag(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return tag;
        return fallback;
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private async void ShowToast(string message, bool isError = false)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        ToastMessageText.Text = message;
        if (isError)
        {
            ToastIconText.Text = "⚠";
            ToastIconText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            ToastBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        else
        {
            ToastIconText.Text = "✓";
            ToastIconText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            ToastBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246));
        }

        ToastBanner.Visibility = Visibility.Visible;

        try
        {
            await Task.Delay(4000, token);
            ToastBanner.Visibility = Visibility.Collapsed;
        }
        catch (TaskCanceledException)
        {
        }
    }
}
