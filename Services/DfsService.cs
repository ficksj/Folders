using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ShareManager.Models;

namespace ShareManager.Services;

public static class DfsService
{
    private const int NERR_Success = 0;
    private const int ERROR_NO_MORE_ITEMS = 259;
    private const uint DFS_ADD_VOLUME = 1;
    private const uint DFS_STORAGE_STATE_OFFLINE = 1;
    private const uint DFS_STORAGE_STATE_ONLINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DFS_INFO_100
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Comment;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DFS_INFO_101
    {
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DFS_INFO_200
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string FtDfsName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DFS_INFO_300
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string DfsName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DFS_STORAGE_INFO
    {
        public uint State;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ServerName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ShareName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DFS_INFO_3
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string EntryPath;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Comment;
        public uint State;
        public uint NumberOfStorages;
        public IntPtr Storage;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetDfsEnum(
        string DfsName,
        uint Level,
        int PrefMaxLen,
        out IntPtr Buffer,
        out uint EntriesRead,
        ref uint ResumeHandle);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetDfsAdd(
        string DfsEntryPath,
        string ServerName,
        string ShareName,
        string? Comment,
        uint Flags);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetDfsRemove(
        string DfsEntryPath,
        string? ServerName,
        string? ShareName);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetDfsSetInfo(
        string DfsEntryPath,
        string? ServerName,
        string? ShareName,
        uint Level,
        IntPtr Buffer);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr Buffer);

    public static string NormalizeUncPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string p = path.Trim().Replace('/', '\\');
        while (p.StartsWith('\\'))
            p = p[1..];
        return @"\\" + p.TrimEnd('\\');
    }

    public static Task<List<DfsNamespaceRoot>> DiscoverNamespacesAsync(IEnumerable<string> serverHostnames)
    {
        return Task.Run(() =>
        {
            var found = new Dictionary<string, DfsNamespaceRoot>(StringComparer.OrdinalIgnoreCase);

            void TryAddRoot(string rawPath, string flavor, string desc)
            {
                string norm = NormalizeUncPath(rawPath);
                if (string.IsNullOrWhiteSpace(norm) || norm.Count(c => c == '\\') < 3)
                    return;

                if (!found.ContainsKey(norm))
                {
                    found[norm] = new DfsNamespaceRoot
                    {
                        RootPath = norm,
                        Flavor = flavor,
                        Description = desc
                    };
                }
            }

            // 1. Discover Domain-based DFS Namespaces via Level 200 on current AD domain
            string? dnsDomain = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
            string? netbiosDomain = Environment.UserDomainName;

            foreach (var dom in new[] { dnsDomain, netbiosDomain }.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                IntPtr buf = IntPtr.Zero;
                uint read = 0, resume = 0;
                try
                {
                    int res = NetDfsEnum(dom!, 200, -1, out buf, out read, ref resume);
                    if (res == NERR_Success && buf != IntPtr.Zero)
                    {
                        int sz = Marshal.SizeOf<DFS_INFO_200>();
                        for (int i = 0; i < read; i++)
                        {
                            IntPtr ptr = new IntPtr(buf.ToInt64() + i * sz);
                            var info = Marshal.PtrToStructure<DFS_INFO_200>(ptr);
                            if (!string.IsNullOrWhiteSpace(info.FtDfsName))
                            {
                                string host = !string.IsNullOrWhiteSpace(dnsDomain) ? dnsDomain : dom!;
                                TryAddRoot($@"\\{host}\{info.FtDfsName}", "Domain", $"Доменное пространство имен ({host})");
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    if (buf != IntPtr.Zero) NetApiBufferFree(buf);
                }
            }

            // 2. Discover Standalone / Server DFS Namespaces via Level 300 on known servers
            foreach (var srv in serverHostnames.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string cleanHost = srv.Trim().TrimStart('\\');
                if (cleanHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) || cleanHost == ".")
                    cleanHost = Environment.MachineName;

                foreach (var candidate in new[] { cleanHost, $@"\\{cleanHost}" })
                {
                    IntPtr buf = IntPtr.Zero;
                    uint read = 0, resume = 0;
                    try
                    {
                        int res = NetDfsEnum(candidate, 300, -1, out buf, out read, ref resume);
                        if (res == NERR_Success && buf != IntPtr.Zero)
                        {
                            int sz = Marshal.SizeOf<DFS_INFO_300>();
                            for (int i = 0; i < read; i++)
                            {
                                IntPtr ptr = new IntPtr(buf.ToInt64() + i * sz);
                                var info = Marshal.PtrToStructure<DFS_INFO_300>(ptr);
                                if (!string.IsNullOrWhiteSpace(info.DfsName))
                                {
                                    bool isDom = (info.Flags & 0x200) != 0; // DFS_VOLUME_FLAVOR_AD_BLOB
                                    TryAddRoot(info.DfsName, isDom ? "Domain" : "Standalone", $"Найдено на {cleanHost}");
                                }
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        if (buf != IntPtr.Zero) NetApiBufferFree(buf);
                    }
                }
            }

            return found.Values.OrderBy(x => x.RootPath).ToList();
        });
    }

    public static Task<List<DfsFolderItem>> GetDfsFoldersAsync(string dfsRootPath)
    {
        return Task.Run(() =>
        {
            string normRoot = NormalizeUncPath(dfsRootPath);
            if (string.IsNullOrWhiteSpace(normRoot))
                throw new ArgumentException("Укажите корректный UNC-путь пространства имен DFS (например \\\\domain\\Root).");

            IntPtr buf = IntPtr.Zero;
            uint read = 0, resume = 0;
            try
            {
                int res = NetDfsEnum(normRoot, 3, -1, out buf, out read, ref resume);
                if (res == ERROR_NO_MORE_ITEMS)
                    return new List<DfsFolderItem>();

                if (res != NERR_Success)
                    throw new InvalidOperationException($"Не удалось прочитать пространство имен DFS '{normRoot}': {GetWin32ErrorMessage(res)} (код {res})");

                var list = new List<DfsFolderItem>();
                if (buf != IntPtr.Zero && read > 0)
                {
                    int sz3 = Marshal.SizeOf<DFS_INFO_3>();
                    int szStorage = Marshal.SizeOf<DFS_STORAGE_INFO>();

                    for (int i = 0; i < read; i++)
                    {
                        IntPtr ptr = new IntPtr(buf.ToInt64() + i * sz3);
                        var info = Marshal.PtrToStructure<DFS_INFO_3>(ptr);

                        string entryPath = NormalizeUncPath(info.EntryPath);
                        bool isRoot = string.Equals(entryPath, normRoot, StringComparison.OrdinalIgnoreCase);

                        var targets = new List<DfsTargetItem>();
                        if (info.Storage != IntPtr.Zero && info.NumberOfStorages > 0)
                        {
                            for (int t = 0; t < info.NumberOfStorages; t++)
                            {
                                IntPtr sPtr = new IntPtr(info.Storage.ToInt64() + t * szStorage);
                                var st = Marshal.PtrToStructure<DFS_STORAGE_INFO>(sPtr);
                                targets.Add(new DfsTargetItem
                                {
                                    ServerName = st.ServerName ?? string.Empty,
                                    ShareName = st.ShareName ?? string.Empty,
                                    StateRaw = st.State
                                });
                            }
                        }

                        list.Add(new DfsFolderItem
                        {
                            EntryPath = entryPath,
                            RootPath = normRoot,
                            Comment = info.Comment ?? string.Empty,
                            StateRaw = info.State,
                            IsRootEntry = isRoot,
                            Targets = targets
                        });
                    }
                }

                return list
                    .OrderByDescending(x => x.IsRootEntry)
                    .ThenBy(x => x.RelativeFolderName)
                    .ToList();
            }
            finally
            {
                if (buf != IntPtr.Zero)
                    NetApiBufferFree(buf);
            }
        });
    }

    public static Task AddDfsFolderOrTargetAsync(
        string dfsEntryPath,
        string targetServer,
        string targetShare,
        string? comment,
        bool createNewLink)
    {
        return Task.Run(() =>
        {
            string normEntry = NormalizeUncPath(dfsEntryPath);
            string cleanServer = targetServer.Trim().TrimStart('\\');
            string cleanShare = targetShare.Trim().Trim('\\');

            uint flags = createNewLink ? DFS_ADD_VOLUME : 0u;
            int res = NetDfsAdd(normEntry, cleanServer, cleanShare, string.IsNullOrWhiteSpace(comment) ? null : comment, flags);

            // If folder already exists and user wanted to add target, retry with flags = 0
            if (res != NERR_Success && createNewLink)
            {
                int retryRes = NetDfsAdd(normEntry, cleanServer, cleanShare, string.IsNullOrWhiteSpace(comment) ? null : comment, 0u);
                if (retryRes == NERR_Success)
                    return;
            }

            if (res != NERR_Success)
            {
                throw new InvalidOperationException($"Ошибка добавления в DFS '{normEntry}' -> '\\\\{cleanServer}\\{cleanShare}': {GetWin32ErrorMessage(res)} (код {res})");
            }
        });
    }

    public static Task RemoveDfsFolderOrTargetAsync(string dfsEntryPath, string? targetServer = null, string? targetShare = null)
    {
        return Task.Run(() =>
        {
            string normEntry = NormalizeUncPath(dfsEntryPath);
            string? cleanServer = string.IsNullOrWhiteSpace(targetServer) ? null : targetServer.Trim().TrimStart('\\');
            string? cleanShare = string.IsNullOrWhiteSpace(targetShare) ? null : targetShare.Trim().Trim('\\');

            int res = NetDfsRemove(normEntry, cleanServer, cleanShare);
            if (res != NERR_Success)
            {
                throw new InvalidOperationException($"Ошибка удаления из DFS '{normEntry}': {GetWin32ErrorMessage(res)} (код {res})");
            }
        });
    }

    public static Task SetDfsTargetStateAsync(string dfsEntryPath, string targetServer, string targetShare, bool online)
    {
        return Task.Run(() =>
        {
            string normEntry = NormalizeUncPath(dfsEntryPath);
            string cleanServer = targetServer.Trim().TrimStart('\\');
            string cleanShare = targetShare.Trim().Trim('\\');

            var info = new DFS_INFO_101
            {
                State = online ? DFS_STORAGE_STATE_ONLINE : DFS_STORAGE_STATE_OFFLINE
            };

            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<DFS_INFO_101>());
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                int res = NetDfsSetInfo(normEntry, cleanServer, cleanShare, 101, ptr);
                if (res != NERR_Success)
                {
                    throw new InvalidOperationException($"Не удалось изменить состояние цели DFS: {GetWin32ErrorMessage(res)} (код {res})");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        });
    }

    public static async Task<string> CreateDfsNamespaceRootOnServerAsync(
        PowerShellRemoteService psService,
        string serverHostname,
        string rootName,
        string rootType, // "DomainV2" or "Standalone"
        bool installFeatureIfNeeded)
    {
        string srv = serverHostname.Replace("'", "''");
        string rName = rootName.Trim().Trim('\\').Replace("'", "''");
        string rType = string.Equals(rootType, "Standalone", StringComparison.OrdinalIgnoreCase) ? "Standalone" : "DomainV2";
        string installFlag = installFeatureIfNeeded ? "$true" : "$false";

        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    # Check if DFSN cmdlets are available
    if (-not (Get-Command New-DfsnRoot -ErrorAction SilentlyContinue)) {{
        if ({installFlag}) {{
            if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {{
                Install-WindowsFeature -Name FS-DFS-Namespace -IncludeManagementTools -ErrorAction Stop | Out-Null
                Import-Module DFSN -ErrorAction Stop
            }} else {{
                throw 'На целевой машине недоступна роль Windows Server FS-DFS-Namespace (это клиентская ОС или нет модуля ServerManager).'
            }}
        }} else {{
            throw 'Роль FS-DFS-Namespace (пространства имен DFS) не установлена на этом сервере. Включите галочку автоматической установки роли FS-DFS-Namespace.'
        }}
    }}

    $localDir = ""C:\DFSRoots\{rName}""
    if (-not (Test-Path -LiteralPath $localDir)) {{
        New-Item -Path $localDir -ItemType Directory -Force -ErrorAction Stop | Out-Null
    }}

    if (-not (Get-SmbShare -Name '{rName}' -ErrorAction SilentlyContinue)) {{
        $everyoneSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-1-0')
        $everyoneName = $everyoneSid.Translate([System.Security.Principal.NTAccount]).Value
        New-SmbShare -Name '{rName}' -Path $localDir -FullAccess $everyoneName -ErrorAction Stop | Out-Null
    }}

    $comp = $env:COMPUTERNAME
    $dom = $env:USERDNSDOMAIN
    $dfsPath = if ('{rType}' -eq 'DomainV2' -and $dom) {{ ""\\$dom\{rName}"" }} else {{ ""\\$comp\{rName}"" }}
    $targetPath = ""\\$comp\{rName}""

    New-DfsnRoot -Path $dfsPath -TargetPath $targetPath -Type '{rType}' -ErrorAction Stop | Out-Null
    [PSCustomObject]@{{ RootPath = $dfsPath }}
}}
";
        var data = await psService.ExecuteScriptAsync(script, timeoutMs: 90000);
        if (data.ValueKind == System.Text.Json.JsonValueKind.Object && data.TryGetProperty("RootPath", out var rp))
            return rp.GetString() ?? $@"\\{serverHostname}\{rootName}";
        return $@"\\{serverHostname}\{rootName}";
    }

    private static string GetWin32ErrorMessage(int code)
    {
        try
        {
            return new Win32Exception(code).Message;
        }
        catch
        {
            return $"Win32 Error {code}";
        }
    }
}
