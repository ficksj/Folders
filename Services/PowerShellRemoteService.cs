using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShareManager.Models;

namespace ShareManager.Services;

public class ServerOverviewResult
{
    public bool IsOnline { get; set; }
    public string ComputerName { get; set; } = string.Empty;
    public string OsCaption { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public int ShareCount { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

public class PowerShellRemoteService : IDisposable
{
    private Process? _psProcess;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public int DefaultTimeoutMs { get; set; } = 25000;

    public async Task ResetSessionPoolAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_psProcess != null && !_psProcess.HasExited)
            {
                try { _psProcess.Kill(); } catch { }
            }
            _psProcess?.Dispose();
            _psProcess = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureProcessAsync()
    {
        if (_psProcess != null && !_psProcess.HasExited)
            return;

        _psProcess?.Dispose();

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        _psProcess = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить powershell.exe");

        string initScript = @"
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$Global:RemoteSessions = @{}

function Global:Invoke-TargetServer {
    param(
        [string]$Server,
        [scriptblock]$ScriptBlock,
        [object[]]$ArgumentList = @()
    )
    $s = if ($null -eq $Server) { '' } else { $Server.Trim() }
    $localNames = @('', 'localhost', '127.0.0.1', '.', $env:COMPUTERNAME.ToLowerInvariant())
    if ($env:USERDNSDOMAIN) {
        $localNames += ""$($env:COMPUTERNAME).$($env:USERDNSDOMAIN)"".ToLowerInvariant()
    }
    if ($localNames -contains $s.ToLowerInvariant()) {
        return & $ScriptBlock @ArgumentList
    }

    $key = $s.ToLowerInvariant()
    $sess = $Global:RemoteSessions[$key]
    if ($null -eq $sess -or $sess.State -ne 'Opened') {
        if ($null -ne $sess) {
            Remove-PSSession -Session $sess -ErrorAction SilentlyContinue
        }
        $opt = New-PSSessionOption -OpenTimeout 8000 -OperationTimeout 25000 -CancelTimeout 5000
        $sess = New-PSSession -ComputerName $s -SessionOption $opt -ErrorAction Stop
        $Global:RemoteSessions[$key] = $sess
    }
    return Invoke-Command -Session $sess -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList -ErrorAction Stop
}
";
        string initB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(initScript));
        string singleLineInit = $". ([scriptblock]::Create([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{initB64}'))))";
        await _psProcess.StandardInput.WriteLineAsync(singleLineInit);
        await _psProcess.StandardInput.FlushAsync();
    }

    public Task<JsonElement> ExecuteScriptAsync(string script, int timeoutMs = 0)
    {
        int effectiveTimeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
        return Task.Run(async () =>
        {
            await _lock.WaitAsync();
            try
            {
                await EnsureProcessAsync();
                if (_psProcess == null)
                    throw new InvalidOperationException("PowerShell процесс недоступен.");

                string cmdId = Guid.NewGuid().ToString("N");
                string beginMarker = $"<<<BEGIN_{cmdId}>>>";
                string endMarker = $"<<<END_{cmdId}>>>";

                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
                string wrapper = $@"
$__err = $null
$__data = $null
try {{
    $__sb = [scriptblock]::Create([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{b64}')))
    $__data = & $__sb
}} catch {{
    $__err = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($__err)) {{ $__err = $_.ToString() }}
}}
if ($null -ne $__err) {{
    $__json = @{{ ok = $false; error = $__err }} | ConvertTo-Json -Depth 4 -Compress
}} else {{
    $__json = @{{ ok = $true; data = $__data }} | ConvertTo-Json -Depth 6 -Compress
}}
$__outB64 = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($__json))
Write-Output '{beginMarker}'
Write-Output $__outB64
Write-Output '{endMarker}'
";
                string wrapperB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(wrapper));
                string singleLineCmd = $". ([scriptblock]::Create([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{wrapperB64}'))))";

                await _psProcess.StandardInput.WriteLineAsync(singleLineCmd);
                await _psProcess.StandardInput.FlushAsync();

                var sb = new StringBuilder();
                bool capturing = false;

                using var cts = new CancellationTokenSource(effectiveTimeout);
                while (!cts.IsCancellationRequested)
                {
                    var readTask = _psProcess.StandardOutput.ReadLineAsync();
                    var completed = await Task.WhenAny(readTask, Task.Delay(effectiveTimeout, cts.Token));
                    if (completed != readTask)
                    {
                        try { _psProcess.Kill(); } catch { }
                        _psProcess = null;
                        throw new TimeoutException("Превышено время ожидания ответа от сервера (WinRM / PowerShell).");
                    }

                    string? line = await readTask;
                    if (line == null)
                    {
                        _psProcess = null;
                        throw new InvalidOperationException("Процесс PowerShell неожиданно завершился.");
                    }

                    if (line.Trim() == beginMarker)
                    {
                        capturing = true;
                        continue;
                    }

                    if (line.Trim() == endMarker)
                    {
                        break;
                    }

                    if (capturing)
                    {
                        sb.Append(line.Trim());
                    }
                }

                string b64Response = sb.ToString().Trim();
                if (string.IsNullOrWhiteSpace(b64Response))
                    throw new InvalidOperationException("Пустой ответ от PowerShell.");

                string jsonText = Encoding.UTF8.GetString(Convert.FromBase64String(b64Response));

                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement.Clone();

                if (root.TryGetProperty("ok", out var okProp) && !okProp.GetBoolean())
                {
                    string err = root.TryGetProperty("error", out var errProp) ? errProp.GetString() ?? "Ошибка выполнения" : "Ошибка выполнения";
                    throw new InvalidOperationException(err);
                }

                if (root.TryGetProperty("data", out var dataProp))
                    return dataProp;

                return default;
            }
            finally
            {
                _lock.Release();
            }
        });
    }

    private static List<JsonElement> EnumerateJsonElements(JsonElement data)
    {
        var list = new List<JsonElement>();
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    list.Add(item);
            }
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            list.Add(data);
        }
        return list;
    }

    private static string GetStr(JsonElement el, string prop, string fallback = "")
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p))
        {
            if (p.ValueKind == JsonValueKind.String)
                return p.GetString() ?? fallback;
            if (p.ValueKind != JsonValueKind.Null && p.ValueKind != JsonValueKind.Undefined)
                return p.ToString();
        }
        return fallback;
    }

    private static bool GetBool(JsonElement el, string prop, bool fallback = false)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p))
        {
            if (p.ValueKind == JsonValueKind.True) return true;
            if (p.ValueKind == JsonValueKind.False) return false;
            if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out bool b)) return b;
        }
        return fallback;
    }

    private static uint GetUInt(JsonElement el, string prop, uint fallback = 0)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt32(out uint v)) return v;
            if (p.ValueKind == JsonValueKind.String && uint.TryParse(p.GetString(), out uint sv)) return sv;
        }
        return fallback;
    }

    private static ulong GetULong(JsonElement el, string prop, ulong fallback = 0)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out ulong v)) return v;
            if (p.ValueKind == JsonValueKind.String && ulong.TryParse(p.GetString(), out ulong sv)) return sv;
        }
        return fallback;
    }

    private static double GetDouble(JsonElement el, string prop, double fallback = 0)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double v)) return v;
            if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), out double sv)) return sv;
        }
        return fallback;
    }

    private static string EscapePsSingleQuote(string input)
    {
        return (input ?? string.Empty).Replace("'", "''");
    }

    public async Task<ServerOverviewResult> TestAndGetServerOverviewAsync(string serverHostname)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction SilentlyContinue
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue
    $shares = @(Get-SmbShare -ErrorAction SilentlyContinue)
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    $isAdmin = $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    [PSCustomObject]@{{
        ComputerName = $env:COMPUTERNAME
        OsCaption    = if ($os) {{ $os.Caption }} else {{ 'Windows Server' }}
        Domain       = if ($cs) {{ $cs.Domain }} else {{ $env:USERDNSDOMAIN }}
        IsAdmin      = $isAdmin
        ShareCount   = $shares.Count
    }}
}}
";
        try
        {
            var data = await ExecuteScriptAsync(script, 15000);
            return new ServerOverviewResult
            {
                IsOnline = true,
                ComputerName = GetStr(data, "ComputerName", serverHostname),
                OsCaption = GetStr(data, "OsCaption", "Windows"),
                Domain = GetStr(data, "Domain", ""),
                IsAdmin = GetBool(data, "IsAdmin", false),
                ShareCount = (int)GetUInt(data, "ShareCount", 0)
            };
        }
        catch (Exception ex)
        {
            return new ServerOverviewResult
            {
                IsOnline = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<List<SmbShareItem>> GetSharesAsync(string serverHostname)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    $shares = Get-SmbShare -ErrorAction Stop
    foreach ($s in $shares) {{
        [PSCustomObject]@{{
            Name                  = [string]$s.Name
            Path                  = [string]$s.Path
            Description           = [string]$s.Description
            ShareType             = [string]$s.ShareType
            Special               = [bool]$s.Special
            FolderEnumerationMode = [string]$s.FolderEnumerationMode
            CachingMode           = [string]$s.CachingMode
            ConcurrentUserLimit   = [uint32]$s.ConcurrentUserLimit
            CurrentUsers          = [uint32]$s.CurrentUsers
        }}
    }}
}}
";
        var data = await ExecuteScriptAsync(script);
        var result = new List<SmbShareItem>();
        foreach (var el in EnumerateJsonElements(data))
        {
            result.Add(new SmbShareItem
            {
                Name = GetStr(el, "Name"),
                Path = GetStr(el, "Path"),
                Description = GetStr(el, "Description"),
                ShareType = GetStr(el, "ShareType", "FileSystemDirectory"),
                Special = GetBool(el, "Special"),
                FolderEnumerationMode = GetStr(el, "FolderEnumerationMode", "Unrestricted"),
                CachingMode = GetStr(el, "CachingMode", "Manual"),
                ConcurrentUserLimit = GetUInt(el, "ConcurrentUserLimit"),
                CurrentUsers = GetUInt(el, "CurrentUsers"),
                ServerHostname = serverHostname
            });
        }
        return result;
    }

    public async Task<List<SmbPermissionItem>> GetSharePermissionsAsync(string serverHostname, string shareName)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string share = EscapePsSingleQuote(shareName);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ArgumentList @('{share}') -ScriptBlock {{
    param($shName)
    $acc = Get-SmbShareAccess -Name $shName -ErrorAction Stop
    foreach ($a in $acc) {{
        [PSCustomObject]@{{
            AccountName       = [string]$a.AccountName
            AccessControlType = [string]$a.AccessControlType
            AccessRight       = [string]$a.AccessRight
        }}
    }}
}}
";
        var data = await ExecuteScriptAsync(script);
        var list = new List<SmbPermissionItem>();
        foreach (var el in EnumerateJsonElements(data))
        {
            list.Add(new SmbPermissionItem
            {
                AccountName = GetStr(el, "AccountName"),
                AccessControlType = GetStr(el, "AccessControlType", "Allow"),
                AccessRight = GetStr(el, "AccessRight", "Read")
            });
        }
        return list;
    }

    public async Task<NtfsAclInfo> GetNtfsAclAsync(string serverHostname, string folderPath)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string path = EscapePsSingleQuote(folderPath);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ArgumentList @('{path}') -ScriptBlock {{
    param($p)
    if ([string]::IsNullOrWhiteSpace($p) -or -not (Test-Path -LiteralPath $p)) {{
        throw ""Папка '$p' не найдена на сервере.""
    }}
    $acl = Get-Acl -LiteralPath $p -ErrorAction Stop
    $rules = foreach ($r in $acl.Access) {{
        [PSCustomObject]@{{
            IdentityReference = [string]$r.IdentityReference.Value
            AccessControlType = [string]$r.AccessControlType
            FileSystemRights  = [string]$r.FileSystemRights
            IsInherited       = [bool]$r.IsInherited
            InheritanceFlags  = [string]$r.InheritanceFlags
            PropagationFlags  = [string]$r.PropagationFlags
        }}
    }}
    [PSCustomObject]@{{
        Path                    = $p
        Owner                   = [string]$acl.Owner
        AreAccessRulesProtected = [bool]$acl.AreAccessRulesProtected
        Rules                   = @($rules)
    }}
}}
";
        var data = await ExecuteScriptAsync(script);
        var info = new NtfsAclInfo
        {
            Path = GetStr(data, "Path", folderPath),
            Owner = GetStr(data, "Owner"),
            AreAccessRulesProtected = GetBool(data, "AreAccessRulesProtected")
        };

        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("Rules", out var rulesProp))
        {
            foreach (var r in EnumerateJsonElements(rulesProp))
            {
                info.Rules.Add(new NtfsRuleItem
                {
                    IdentityReference = GetStr(r, "IdentityReference"),
                    AccessControlType = GetStr(r, "AccessControlType", "Allow"),
                    FileSystemRights = GetStr(r, "FileSystemRights"),
                    IsInherited = GetBool(r, "IsInherited"),
                    InheritanceFlags = GetStr(r, "InheritanceFlags"),
                    PropagationFlags = GetStr(r, "PropagationFlags")
                });
            }
        }
        return info;
    }

    public async Task CreateShareAsync(
        string serverHostname,
        string shareName,
        string folderPath,
        string description,
        bool enableAbe,
        string cachingMode,
        uint concurrentUserLimit,
        bool createFolderIfMissing,
        List<SmbPermissionItem> initialPermissions)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string name = EscapePsSingleQuote(shareName);
        string path = EscapePsSingleQuote(folderPath);
        string desc = EscapePsSingleQuote(description);
        string abe = enableAbe ? "AccessBased" : "Unrestricted";
        string cache = EscapePsSingleQuote(string.IsNullOrWhiteSpace(cachingMode) ? "Manual" : cachingMode);

        var permsBuilder = new StringBuilder();
        permsBuilder.AppendLine("$fullList = New-Object System.Collections.Generic.List[string]");
        permsBuilder.AppendLine("$changeList = New-Object System.Collections.Generic.List[string]");
        permsBuilder.AppendLine("$readList = New-Object System.Collections.Generic.List[string]");
        permsBuilder.AppendLine("$denyList = New-Object System.Collections.Generic.List[string]");

        foreach (var p in initialPermissions)
        {
            string acc = EscapePsSingleQuote(p.AccountName);
            if (string.IsNullOrWhiteSpace(acc)) continue;

            if (string.Equals(p.AccessControlType, "Deny", StringComparison.OrdinalIgnoreCase))
            {
                permsBuilder.AppendLine($"$denyList.Add('{acc}')");
            }
            else if (string.Equals(p.AccessRight, "Full", StringComparison.OrdinalIgnoreCase))
            {
                permsBuilder.AppendLine($"$fullList.Add('{acc}')");
            }
            else if (string.Equals(p.AccessRight, "Change", StringComparison.OrdinalIgnoreCase))
            {
                permsBuilder.AppendLine($"$changeList.Add('{acc}')");
            }
            else
            {
                permsBuilder.AppendLine($"$readList.Add('{acc}')");
            }
        }

        string createDirFlag = createFolderIfMissing ? "$true" : "$false";

        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    $p = '{path}'
    if (-not (Test-Path -LiteralPath $p)) {{
        if ({createDirFlag}) {{
            New-Item -Path $p -ItemType Directory -Force -ErrorAction Stop | Out-Null
        }} else {{
            throw ""Папка '$p' не существует на сервере.""
        }}
    }}

    {permsBuilder}

    $params = @{{
        Name                  = '{name}'
        Path                  = $p
        Description           = '{desc}'
        FolderEnumerationMode = '{abe}'
        CachingMode           = '{cache}'
        ErrorAction           = 'Stop'
    }}
    if ({concurrentUserLimit} -gt 0) {{
        $params['ConcurrentUserLimit'] = [uint32]{concurrentUserLimit}
    }}
    if ($fullList.Count -gt 0)   {{ $params['FullAccess']   = $fullList.ToArray() }}
    if ($changeList.Count -gt 0) {{ $params['ChangeAccess'] = $changeList.ToArray() }}
    if ($readList.Count -gt 0)   {{ $params['ReadAccess']   = $readList.ToArray() }}
    if ($denyList.Count -gt 0)   {{ $params['NoAccess']     = $denyList.ToArray() }}

    New-SmbShare @params | Out-Null
}}
";
        await ExecuteScriptAsync(script);
    }

    public async Task UpdateShareSettingsAsync(
        string serverHostname,
        string shareName,
        string description,
        bool enableAbe,
        string cachingMode,
        uint concurrentUserLimit)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string name = EscapePsSingleQuote(shareName);
        string desc = EscapePsSingleQuote(description);
        string abe = enableAbe ? "AccessBased" : "Unrestricted";
        string cache = EscapePsSingleQuote(string.IsNullOrWhiteSpace(cachingMode) ? "Manual" : cachingMode);

        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    Set-SmbShare -Name '{name}' -Description '{desc}' -FolderEnumerationMode '{abe}' -CachingMode '{cache}' -ConcurrentUserLimit ([uint32]{concurrentUserLimit}) -Force -ErrorAction Stop
}}
";
        await ExecuteScriptAsync(script);
    }

    public async Task DeleteShareAsync(string serverHostname, string shareName)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string name = EscapePsSingleQuote(shareName);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    Remove-SmbShare -Name '{name}' -Force -ErrorAction Stop
}}
";
        await ExecuteScriptAsync(script);
    }

    public async Task SetSmbPermissionAsync(
        string serverHostname,
        string shareName,
        string accountName,
        string accessRight,
        string accessControlType)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string name = EscapePsSingleQuote(shareName);
        string acc = EscapePsSingleQuote(accountName);
        string right = EscapePsSingleQuote(accessRight);
        bool isDeny = string.Equals(accessControlType, "Deny", StringComparison.OrdinalIgnoreCase);

        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    Unblock-SmbShareAccess -Name '{name}' -AccountName '{acc}' -Force -ErrorAction SilentlyContinue | Out-Null
    Revoke-SmbShareAccess -Name '{name}' -AccountName '{acc}' -Force -ErrorAction SilentlyContinue | Out-Null
    if (${(isDeny ? "true" : "false")}) {{
        Block-SmbShareAccess -Name '{name}' -AccountName '{acc}' -Force -ErrorAction Stop | Out-Null
    }} else {{
        Grant-SmbShareAccess -Name '{name}' -AccountName '{acc}' -AccessRight '{right}' -Force -ErrorAction Stop | Out-Null
    }}
}}
";
        await ExecuteScriptAsync(script);
    }

    public async Task RemoveSmbPermissionAsync(string serverHostname, string shareName, string accountName)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string name = EscapePsSingleQuote(shareName);
        string acc = EscapePsSingleQuote(accountName);

        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    Unblock-SmbShareAccess -Name '{name}' -AccountName '{acc}' -Force -ErrorAction SilentlyContinue | Out-Null
    Revoke-SmbShareAccess -Name '{name}' -AccountName '{acc}' -Force -ErrorAction SilentlyContinue | Out-Null
}}
";
        await ExecuteScriptAsync(script);
    }

    public async Task AddOrUpdateNtfsRuleAsync(
        string serverHostname,
        string folderPath,
        string identity,
        string rights,
        string accessControlType,
        string scopeMode)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string path = EscapePsSingleQuote(folderPath);
        string acc = EscapePsSingleQuote(identity);
        string r = EscapePsSingleQuote(rights);
        string type = string.Equals(accessControlType, "Deny", StringComparison.OrdinalIgnoreCase) ? "Deny" : "Allow";
        string scope = EscapePsSingleQuote(scopeMode);

        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    $acl = Get-Acl -LiteralPath '{path}' -ErrorAction Stop
    $inh = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    $prop = [System.Security.AccessControl.PropagationFlags]::None

    switch ('{scope}') {{
        'ThisFolderOnly' {{
            $inh = [System.Security.AccessControl.InheritanceFlags]::None
            $prop = [System.Security.AccessControl.PropagationFlags]::None
        }}
        'SubfoldersAndFilesOnly' {{
            $inh = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
            $prop = [System.Security.AccessControl.PropagationFlags]::InheritOnly
        }}
        'ThisFolderAndSubfolders' {{
            $inh = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit
            $prop = [System.Security.AccessControl.PropagationFlags]::None
        }}
        'ThisFolderAndFiles' {{
            $inh = [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
            $prop = [System.Security.AccessControl.PropagationFlags]::None
        }}
    }}

    $fsRights = [System.Security.AccessControl.FileSystemRights]'{r}'
    $acType = [System.Security.AccessControl.AccessControlType]'{type}'
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule('{acc}', $fsRights, $inh, $prop, $acType)
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath '{path}' -AclObject $acl -ErrorAction Stop
}}
";
        await ExecuteScriptAsync(script);
    }

    public async Task RemoveNtfsRuleAsync(
        string serverHostname,
        string folderPath,
        string identity,
        string accessControlType)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string path = EscapePsSingleQuote(folderPath);
        string acc = EscapePsSingleQuote(identity);
        string type = string.Equals(accessControlType, "Deny", StringComparison.OrdinalIgnoreCase) ? "Deny" : "Allow";

        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    $acl = Get-Acl -LiteralPath '{path}' -ErrorAction Stop
    $toRemove = @($acl.Access | Where-Object {{
        -not $_.IsInherited -and
        $_.IdentityReference.Value -ieq '{acc}' -and
        [string]$_.AccessControlType -ieq '{type}'
    }})
    foreach ($rule in $toRemove) {{
        $acl.RemoveAccessRuleSpecific($rule) | Out-Null
    }}
    Set-Acl -LiteralPath '{path}' -AclObject $acl -ErrorAction Stop
}}
";
        await ExecuteScriptAsync(script);
    }

    public async Task SetNtfsInheritanceAsync(
        string serverHostname,
        string folderPath,
        bool disableInheritance,
        bool preserveInheritedRules)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string path = EscapePsSingleQuote(folderPath);
        string isProtected = disableInheritance ? "$true" : "$false";
        string preserve = preserveInheritedRules ? "$true" : "$false";

        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    $acl = Get-Acl -LiteralPath '{path}' -ErrorAction Stop
    $acl.SetAccessRuleProtection({isProtected}, {preserve})
    Set-Acl -LiteralPath '{path}' -AclObject $acl -ErrorAction Stop
}}
";
        await ExecuteScriptAsync(script);
    }

    public async Task<List<RemoteDiskItem>> GetRemoteDisksAsync(string serverHostname)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    $disks = Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction Stop
    foreach ($d in $disks) {{
        [PSCustomObject]@{{
            Name        = [string]$d.DeviceID
            RootPath    = ""$($d.DeviceID)\""
            VolumeLabel = [string]$d.VolumeName
            TotalGb     = [math]::Round(($d.Size / 1GB), 2)
            FreeGb      = [math]::Round(($d.FreeSpace / 1GB), 2)
        }}
    }}
}}
";
        var data = await ExecuteScriptAsync(script);
        var list = new List<RemoteDiskItem>();
        foreach (var el in EnumerateJsonElements(data))
        {
            list.Add(new RemoteDiskItem
            {
                Name = GetStr(el, "Name"),
                RootPath = GetStr(el, "RootPath"),
                VolumeLabel = GetStr(el, "VolumeLabel"),
                TotalGb = GetDouble(el, "TotalGb"),
                FreeGb = GetDouble(el, "FreeGb")
            });
        }
        return list;
    }

    public async Task<List<RemoteFolderItem>> GetRemoteSubfoldersAsync(string serverHostname, string parentPath)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string path = EscapePsSingleQuote(parentPath);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ArgumentList @('{path}') -ScriptBlock {{
    param($p)
    $dirs = Get-ChildItem -LiteralPath $p -Directory -ErrorAction Stop | Sort-Object Name
    foreach ($d in $dirs) {{
        [PSCustomObject]@{{
            Name          = [string]$d.Name
            FullPath      = [string]$d.FullName
            LastWriteTime = $d.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
        }}
    }}
}}
";
        var data = await ExecuteScriptAsync(script);
        var list = new List<RemoteFolderItem>();
        foreach (var el in EnumerateJsonElements(data))
        {
            DateTime.TryParse(GetStr(el, "LastWriteTime"), out var dt);
            list.Add(new RemoteFolderItem
            {
                Name = GetStr(el, "Name"),
                FullPath = GetStr(el, "FullPath"),
                LastWriteTime = dt
            });
        }
        return list;
    }

    public async Task<string> CreateRemoteFolderAsync(string serverHostname, string parentPath, string newFolderName)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string parent = EscapePsSingleQuote(parentPath);
        string name = EscapePsSingleQuote(newFolderName);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ArgumentList @('{parent}', '{name}') -ScriptBlock {{
    param($p, $n)
    $item = New-Item -Path $p -Name $n -ItemType Directory -Force -ErrorAction Stop
    [PSCustomObject]@{{ FullPath = [string]$item.FullName }}
}}
";
        var data = await ExecuteScriptAsync(script);
        return GetStr(data, "FullPath", Path.Combine(parentPath, newFolderName));
    }

    public async Task<List<SmbSessionItem>> GetSmbSessionsAsync(string serverHostname)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    $sessions = @(Get-SmbSession -ErrorAction Stop)
    foreach ($s in $sessions) {{
        [PSCustomObject]@{{
            SessionId          = [uint64]$s.SessionId
            ClientComputerName = [string]$s.ClientComputerName
            ClientUserName     = [string]$s.ClientUserName
            NumOpens           = [uint32]$s.NumOpens
            SecondsExists      = [uint32]$s.SecondsExists
            SecondsIdle        = [uint32]$s.SecondsIdle
            Dialect            = [string]$s.Dialect
        }}
    }}
}}
";
        var data = await ExecuteScriptAsync(script);
        var list = new List<SmbSessionItem>();
        foreach (var el in EnumerateJsonElements(data))
        {
            list.Add(new SmbSessionItem
            {
                SessionId = GetULong(el, "SessionId"),
                ClientComputerName = GetStr(el, "ClientComputerName"),
                ClientUserName = GetStr(el, "ClientUserName"),
                NumOpens = GetUInt(el, "NumOpens"),
                SecondsExists = GetUInt(el, "SecondsExists"),
                SecondsIdle = GetUInt(el, "SecondsIdle"),
                Dialect = GetStr(el, "Dialect")
            });
        }
        return list;
    }

    public async Task CloseSmbSessionAsync(string serverHostname, ulong sessionId)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    Close-SmbSession -SessionId ([uint64]{sessionId}) -Force -ErrorAction Stop
}}
";
        await ExecuteScriptAsync(script);
    }

    public async Task<List<SmbOpenFileItem>> GetSmbOpenFilesAsync(string serverHostname)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    $files = @(Get-SmbOpenFile -ErrorAction Stop)
    foreach ($f in $files) {{
        [PSCustomObject]@{{
            FileId             = [uint64]$f.FileId
            SessionId          = [uint64]$f.SessionId
            Path               = [string]$f.Path
            ShareRelativePath  = [string]$f.ShareRelativePath
            ClientComputerName = [string]$f.ClientComputerName
            ClientUserName     = [string]$f.ClientUserName
            Locks              = [uint32]$f.Locks
        }}
    }}
}}
";
        var data = await ExecuteScriptAsync(script);
        var list = new List<SmbOpenFileItem>();
        foreach (var el in EnumerateJsonElements(data))
        {
            list.Add(new SmbOpenFileItem
            {
                FileId = GetULong(el, "FileId"),
                SessionId = GetULong(el, "SessionId"),
                Path = GetStr(el, "Path"),
                ShareRelativePath = GetStr(el, "ShareRelativePath"),
                ClientComputerName = GetStr(el, "ClientComputerName"),
                ClientUserName = GetStr(el, "ClientUserName"),
                Locks = GetUInt(el, "Locks")
            });
        }
        return list;
    }

    public async Task CloseSmbOpenFileAsync(string serverHostname, ulong fileId)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string script = $@"
Invoke-TargetServer -Server '{srv}' -ScriptBlock {{
    Close-SmbOpenFile -FileId ([uint64]{fileId}) -Force -ErrorAction Stop
}}
";
        await ExecuteScriptAsync(script);
    }

    public async Task<List<DirectoryPrincipalItem>> SearchPrincipalsAsync(string serverHostname, string searchQuery)
    {
        string srv = EscapePsSingleQuote(serverHostname);
        string q = EscapePsSingleQuote(searchQuery?.Trim() ?? "");

        string script = $@"
$results = New-Object System.Collections.Generic.List[object]
$q = '{q}'

# 1. Well-known principals (resolved via localized SID so they work on both RU and EN Windows)
$wellKnownSids = @(
    @{{ Sid = 'S-1-1-0';       Desc = 'Все пользователи (Everyone)' }},
    @{{ Sid = 'S-1-5-11';      Desc = 'Прошедшие проверку (Authenticated Users)' }},
    @{{ Sid = 'S-1-5-32-544';  Desc = 'Встроенная группа Администраторы' }},
    @{{ Sid = 'S-1-5-32-545';  Desc = 'Встроенная группа Пользователи' }},
    @{{ Sid = 'S-1-5-18';      Desc = 'Локальная система (SYSTEM)' }}
)
foreach ($wk in $wellKnownSids) {{
    try {{
        $sidObj = New-Object System.Security.Principal.SecurityIdentifier($wk.Sid)
        $ntAcc = $sidObj.Translate([System.Security.Principal.NTAccount]).Value
        if ([string]::IsNullOrWhiteSpace($q) -or $ntAcc -like ""*$q*"" -or $wk.Desc -like ""*$q*"") {{
            $results.Add([PSCustomObject]@{{
                AccountName   = $ntAcc
                DisplayName   = $wk.Desc
                PrincipalType = 'WellKnown'
                Source        = 'BuiltIn'
                Description   = $wk.Sid
            }})
        }}
    }} catch {{}}
}}

# 2. Active Directory search if joined to domain
if ($env:USERDNSDOMAIN) {{
    try {{
        $netbios = $env:USERDOMAIN
        $searcher = [adsisearcher]""""
        $searcher.PageSize = 40
        $searcher.SizeLimit = 40
        if ([string]::IsNullOrWhiteSpace($q)) {{
            $searcher.Filter = ""(|(objectCategory=group)(&(objectCategory=person)(objectClass=user)))""
        }} else {{
            $searcher.Filter = ""(&(|(objectCategory=group)(&(objectCategory=person)(objectClass=user)))(|(sAMAccountName=*$q*)(displayName=*$q*)(cn=*$q*)))""
        }}
        $searcher.PropertiesToLoad.AddRange(@('sAMAccountName', 'displayName', 'cn', 'objectClass', 'description'))
        foreach ($r in $searcher.FindAll()) {{
            $sam = [string]($r.Properties['samaccountname'] | Select-Object -First 1)
            if ([string]::IsNullOrWhiteSpace($sam)) {{ continue }}
            $disp = [string]($r.Properties['displayname'] | Select-Object -First 1)
            if ([string]::IsNullOrWhiteSpace($disp)) {{
                $disp = [string]($r.Properties['cn'] | Select-Object -First 1)
            }}
            $desc = [string]($r.Properties['description'] | Select-Object -First 1)
            $classes = @($r.Properties['objectclass'])
            $pType = if ($classes -contains 'group') {{ 'Group' }} else {{ 'User' }}
            $results.Add([PSCustomObject]@{{
                AccountName   = ""$netbios\$sam""
                DisplayName   = $disp
                PrincipalType = $pType
                Source        = 'Active Directory'
                Description   = $desc
            }})
        }}
    }} catch {{}}
}}

# 3. Local users & groups on target server
try {{
    $localItems = Invoke-TargetServer -Server '{srv}' -ArgumentList @($q) -ScriptBlock {{
        param($query)
        $loc = New-Object System.Collections.Generic.List[object]
        $comp = $env:COMPUTERNAME
        try {{
            $users = Get-LocalUser -ErrorAction SilentlyContinue
            foreach ($u in $users) {{
                if ([string]::IsNullOrWhiteSpace($query) -or $u.Name -like ""*$query*"" -or $u.FullName -like ""*$query*"") {{
                    $loc.Add([PSCustomObject]@{{
                        AccountName   = ""$comp\$($u.Name)""
                        DisplayName   = if ($u.FullName) {{ $u.FullName }} else {{ $u.Name }}
                        PrincipalType = 'User'
                        Source        = ""Локальный ($comp)""
                        Description   = [string]$u.Description
                    }})
                }}
            }}
            $groups = Get-LocalGroup -ErrorAction SilentlyContinue
            foreach ($g in $groups) {{
                if ([string]::IsNullOrWhiteSpace($query) -or $g.Name -like ""*$query*"" -or $g.Description -like ""*$query*"") {{
                    $loc.Add([PSCustomObject]@{{
                        AccountName   = ""$comp\$($g.Name)""
                        DisplayName   = $g.Name
                        PrincipalType = 'Group'
                        Source        = ""Локальная группа ($comp)""
                        Description   = [string]$g.Description
                    }})
                }}
            }}
        }} catch {{}}
        return $loc
    }}
    foreach ($li in @($localItems)) {{
        if ($null -ne $li -and $li.AccountName) {{
            $results.Add($li)
        }}
    }}
}} catch {{}}

$results | Select-Object -First 60
";
        var data = await ExecuteScriptAsync(script);
        var list = new List<DirectoryPrincipalItem>();
        foreach (var el in EnumerateJsonElements(data))
        {
            list.Add(new DirectoryPrincipalItem
            {
                AccountName = GetStr(el, "AccountName"),
                DisplayName = GetStr(el, "DisplayName"),
                PrincipalType = GetStr(el, "PrincipalType", "User"),
                Source = GetStr(el, "Source", "AD"),
                Description = GetStr(el, "Description")
            });
        }
        return list;
    }

    public async Task<List<AdServerCandidate>> DiscoverAdServersAsync(string query = "")
    {
        string q = EscapePsSingleQuote(query?.Trim() ?? "");
        string script = $@"
$list = New-Object System.Collections.Generic.List[object]
if (-not $env:USERDNSDOMAIN) {{
    throw 'Текущий компьютер не подключен к домену Active Directory.'
}}
$searcher = [adsisearcher]""""
$searcher.PageSize = 100
$searcher.SizeLimit = 100
$q = '{q}'
if ([string]::IsNullOrWhiteSpace($q)) {{
    $searcher.Filter = '(&(objectCategory=computer)(operatingSystem=*Server*))'
}} else {{
    $searcher.Filter = ""(&(objectCategory=computer)(|(name=*$q*)(dNSHostName=*$q*)(description=*$q*)))""
}}
$searcher.PropertiesToLoad.AddRange(@('name', 'dNSHostName', 'operatingSystem', 'description'))
foreach ($r in $searcher.FindAll()) {{
    $name = [string]($r.Properties['name'] | Select-Object -First 1)
    $dns  = [string]($r.Properties['dnshostname'] | Select-Object -First 1)
    $os   = [string]($r.Properties['operatingsystem'] | Select-Object -First 1)
    $desc = [string]($r.Properties['description'] | Select-Object -First 1)
    if ($name) {{
        $list.Add([PSCustomObject]@{{
            Name            = $name
            DnsHostName     = if ($dns) {{ $dns }} else {{ $name }}
            OperatingSystem = if ($os) {{ $os }} else {{ 'Windows Computer' }}
            Description     = $desc
        }})
    }}
}}
$list | Sort-Object Name
";
        var data = await ExecuteScriptAsync(script);
        var list = new List<AdServerCandidate>();
        foreach (var el in EnumerateJsonElements(data))
        {
            list.Add(new AdServerCandidate
            {
                Name = GetStr(el, "Name"),
                DnsHostName = GetStr(el, "DnsHostName"),
                OperatingSystem = GetStr(el, "OperatingSystem"),
                Description = GetStr(el, "Description"),
                IsSelected = true
            });
        }
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_psProcess != null && !_psProcess.HasExited)
            {
                _psProcess.Kill();
            }
        }
        catch { }
        _psProcess?.Dispose();
        _lock.Dispose();
    }
}
