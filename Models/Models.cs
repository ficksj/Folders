using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ShareManager.Models;

public enum ServerConnectionStatus
{
    Unknown,
    Connecting,
    Online,
    Offline
}

public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public class ServerNode : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _hostname = string.Empty;
    private string _displayName = string.Empty;
    private string _description = string.Empty;
    private bool _isLocal;
    private ServerConnectionStatus _status = ServerConnectionStatus.Unknown;
    private string _osInfo = string.Empty;
    private string _domainInfo = string.Empty;
    private string _lastError = string.Empty;
    private int _shareCount;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Hostname
    {
        get => _hostname;
        set
        {
            if (SetProperty(ref _hostname, value))
                OnPropertyChanged(nameof(EffectiveTitle));
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
                OnPropertyChanged(nameof(EffectiveTitle));
        }
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public bool IsLocal
    {
        get => _isLocal;
        set => SetProperty(ref _isLocal, value);
    }

    public ServerConnectionStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string OsInfo
    {
        get => _osInfo;
        set => SetProperty(ref _osInfo, value);
    }

    public string DomainInfo
    {
        get => _domainInfo;
        set => SetProperty(ref _domainInfo, value);
    }

    public string LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    public int ShareCount
    {
        get => _shareCount;
        set => SetProperty(ref _shareCount, value);
    }

    public string EffectiveTitle => string.IsNullOrWhiteSpace(DisplayName) ? Hostname : DisplayName;

    public string StatusText => Status switch
    {
        ServerConnectionStatus.Online => "Подключен",
        ServerConnectionStatus.Connecting => "Подключение...",
        ServerConnectionStatus.Offline => "Ошибка связи",
        _ => "Не проверен"
    };
}

public class SmbShareItem : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ShareType { get; set; } = "FileSystemDirectory";
    public bool Special { get; set; }
    public string FolderEnumerationMode { get; set; } = "Unrestricted"; // AccessBased or Unrestricted
    public string CachingMode { get; set; } = "Manual";
    public uint ConcurrentUserLimit { get; set; }
    public uint CurrentUsers { get; set; }
    public string ServerHostname { get; set; } = string.Empty;

    public bool IsHidden => Name.EndsWith('$');
    public bool IsSystemOrAdmin => Special || Name.Equals("ADMIN$", StringComparison.OrdinalIgnoreCase)
        || Name.Equals("IPC$", StringComparison.OrdinalIgnoreCase)
        || Name.Equals("PRINT$", StringComparison.OrdinalIgnoreCase)
        || (Name.Length == 2 && char.IsLetter(Name[0]) && Name[1] == '$');

    public bool IsAbeEnabled => string.Equals(FolderEnumerationMode, "AccessBased", StringComparison.OrdinalIgnoreCase);

    public string AbeBadgeText => IsAbeEnabled ? "ABE Вкл" : "ABE Выкл";

    public string UncPath
    {
        get
        {
            var host = string.IsNullOrWhiteSpace(ServerHostname) || ServerHostname == "localhost"
                ? Environment.MachineName
                : ServerHostname;
            return $@"\\{host}\{Name}";
        }
    }
}

public class SmbPermissionItem : ObservableObject
{
    private string _accountName = string.Empty;
    private string _accessControlType = "Allow"; // Allow, Deny
    private string _accessRight = "Read"; // Read, Change, Full

    public string AccountName
    {
        get => _accountName;
        set => SetProperty(ref _accountName, value);
    }

    public string AccessControlType
    {
        get => _accessControlType;
        set
        {
            if (SetProperty(ref _accessControlType, value))
                OnPropertyChanged(nameof(AccessControlTypeDisplay));
        }
    }

    public string AccessRight
    {
        get => _accessRight;
        set
        {
            if (SetProperty(ref _accessRight, value))
                OnPropertyChanged(nameof(AccessRightDisplay));
        }
    }

    public string AccessControlTypeDisplay =>
        string.Equals(AccessControlType, "Deny", StringComparison.OrdinalIgnoreCase) ? "Запретить" : "Разрешить";

    public string AccessRightDisplay => AccessRight switch
    {
        "Full" => "Полный доступ",
        "Change" => "Изменение",
        "Read" => "Чтение",
        _ => AccessRight
    };
}

public class NtfsRuleItem : ObservableObject
{
    public string IdentityReference { get; set; } = string.Empty;
    public string AccessControlType { get; set; } = "Allow"; // Allow, Deny
    public string FileSystemRights { get; set; } = string.Empty;
    public bool IsInherited { get; set; }
    public string InheritanceFlags { get; set; } = string.Empty;
    public string PropagationFlags { get; set; } = string.Empty;

    public string AccessTypeDisplay =>
        string.Equals(AccessControlType, "Deny", StringComparison.OrdinalIgnoreCase) ? "Запретить" : "Разрешить";

    public string SourceDisplay => IsInherited ? "Унаследовано" : "Явное";

    public string RightsDisplay
    {
        get
        {
            var r = FileSystemRights ?? string.Empty;
            if (r.Contains("FullControl", StringComparison.OrdinalIgnoreCase) || r == "2032127")
                return "Полный доступ (FullControl)";
            if (r.Contains("Modify", StringComparison.OrdinalIgnoreCase))
                return "Изменение (Modify)";
            if (r.Contains("ReadAndExecute", StringComparison.OrdinalIgnoreCase))
                return "Чтение и выполнение";
            if (r.Contains("Write", StringComparison.OrdinalIgnoreCase) && r.Contains("Read", StringComparison.OrdinalIgnoreCase))
                return "Чтение и запись";
            if (r.Contains("Read", StringComparison.OrdinalIgnoreCase))
                return "Только чтение";
            if (r.Contains("Write", StringComparison.OrdinalIgnoreCase))
                return "Только запись";
            return r;
        }
    }

    public string ScopeDisplay
    {
        get
        {
            bool container = InheritanceFlags.Contains("ContainerInherit", StringComparison.OrdinalIgnoreCase);
            bool obj = InheritanceFlags.Contains("ObjectInherit", StringComparison.OrdinalIgnoreCase);
            bool inheritOnly = PropagationFlags.Contains("InheritOnly", StringComparison.OrdinalIgnoreCase);

            if (container && obj && !inheritOnly)
                return "Эта папка, подпапки и файлы";
            if (container && obj && inheritOnly)
                return "Только подпапки и файлы";
            if (container && !obj && !inheritOnly)
                return "Эта папка и подпапки";
            if (!container && obj && !inheritOnly)
                return "Эта папка и файлы";
            return "Только эта папка";
        }
    }
}

public class NtfsAclInfo
{
    public string Path { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public bool AreAccessRulesProtected { get; set; } // true = inheritance disabled
    public List<NtfsRuleItem> Rules { get; set; } = new();
}

public class SmbSessionItem
{
    public ulong SessionId { get; set; }
    public string ClientComputerName { get; set; } = string.Empty;
    public string ClientUserName { get; set; } = string.Empty;
    public uint NumOpens { get; set; }
    public uint SecondsExists { get; set; }
    public uint SecondsIdle { get; set; }
    public string Dialect { get; set; } = string.Empty;

    public string DurationDisplay => FormatSeconds(SecondsExists);
    public string IdleDisplay => FormatSeconds(SecondsIdle);

    private static string FormatSeconds(uint sec)
    {
        var ts = TimeSpan.FromSeconds(sec);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}ч {ts.Minutes}м";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}м {ts.Seconds}с";
        return $"{ts.Seconds}с";
    }
}

public class SmbOpenFileItem
{
    public ulong FileId { get; set; }
    public ulong SessionId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string ShareRelativePath { get; set; } = string.Empty;
    public string ClientComputerName { get; set; } = string.Empty;
    public string ClientUserName { get; set; } = string.Empty;
    public uint Locks { get; set; }
}

public class RemoteDiskItem
{
    public string Name { get; set; } = string.Empty; // e.g. "C:"
    public string RootPath { get; set; } = string.Empty; // e.g. "C:\"
    public string VolumeLabel { get; set; } = string.Empty;
    public double TotalGb { get; set; }
    public double FreeGb { get; set; }

    public double UsedPercent => TotalGb > 0 ? Math.Clamp((TotalGb - FreeGb) / TotalGb * 100.0, 0, 100) : 0;
    public string DisplayTitle => string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"{RootPath} ({FreeGb:F1} ГБ свободно из {TotalGb:F1} ГБ)"
        : $"{RootPath} [{VolumeLabel}] ({FreeGb:F1} ГБ своб. из {TotalGb:F1} ГБ)";
}

public class RemoteFolderItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public DateTime LastWriteTime { get; set; }
    public bool HasSubfolders { get; set; }
}

public class DirectoryPrincipalItem
{
    public string AccountName { get; set; } = string.Empty; // e.g. DOMAIN\ivanov or SERVER\Admin
    public string DisplayName { get; set; } = string.Empty;
    public string PrincipalType { get; set; } = "User"; // User, Group, WellKnown
    public string Source { get; set; } = "AD"; // AD, Local, BuiltIn
    public string Description { get; set; } = string.Empty;

    public string TypeBadge => PrincipalType switch
    {
        "Group" => "Группа",
        "WellKnown" => "Системная",
        _ => "Пользователь"
    };
}

public class AdServerCandidate : ObservableObject
{
    private bool _isSelected = true;

    public string Name { get; set; } = string.Empty;
    public string DnsHostName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string EffectiveHost => string.IsNullOrWhiteSpace(DnsHostName) ? Name : DnsHostName;
}

