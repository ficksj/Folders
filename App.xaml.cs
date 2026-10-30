using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using ShareManager.Services;

namespace ShareManager;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            try
            {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ShareManager",
                    "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllText(logPath, e.Exception.ToString());
            }
            catch { }

            MessageBox.Show($"Произошла ошибка:\n{e.Exception.Message}", "Folders — by Crazy Monkeys", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            try
            {
                using var svc = new PowerShellRemoteService();
                var ov = await svc.TestAndGetServerOverviewAsync("localhost");
                sb.AppendLine($"OVERVIEW: Online={ov.IsOnline}, Computer={ov.ComputerName}, OS={ov.OsCaption}, Domain={ov.Domain}, Shares={ov.ShareCount}, Error={ov.ErrorMessage}");

                var shares = await svc.GetSharesAsync("localhost");
                sb.AppendLine($"SHARES ({shares.Count}):");
                foreach (var sh in shares)
                {
                    sb.AppendLine($"  - {sh.Name} | Path={sh.Path} | Special={sh.Special} | System={sh.IsSystemOrAdmin}");
                }

                var disks = await svc.GetRemoteDisksAsync("localhost");
                sb.AppendLine($"DISKS ({disks.Count}):");
                foreach (var d in disks)
                {
                    sb.AppendLine($"  - {d.DisplayTitle}");
                }

                var acl = await svc.GetNtfsAclAsync("localhost", @"C:\Windows");
                sb.AppendLine($"NTFS ACL C:\\Windows: Owner={acl.Owner}, Protected={acl.AreAccessRulesProtected}, Rules={acl.Rules.Count}");

                var principals = await svc.SearchPrincipalsAsync("localhost", "");
                sb.AppendLine($"PRINCIPALS ({principals.Count}):");
                foreach (var p in principals.Take(8))
                {
                    sb.AppendLine($"  - {p.AccountName} ({p.DisplayName}) [{p.TypeBadge} / {p.Source}]");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"SELF-TEST ERROR: {ex}");
            }

            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.txt"), sb.ToString(), Encoding.UTF8);
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }
}
