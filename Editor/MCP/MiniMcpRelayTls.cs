using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace MiniMCP.Editor
{
    internal static class MiniMcpRelayTls
    {
        private const string CertificateDirectoryName = "MiniMCP";
        private const string CertificateFileName = "relay-loopback-v2.pfx";
        private const string PasswordFileName = "relay-loopback-v2-password.txt";
        private const string FriendlyName = "MiniMCP Loopback Relay v2";
        private const string DefaultPassword = "mini-unity-mcp-localhost";

        public static bool TryEnsureTrustedCertificate(out string pfxPath, out string password, out string error)
        {
            pfxPath = string.Empty;
            password = string.Empty;
            error = string.Empty;

            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                string certDirectory = Path.Combine(projectRoot, "Library", CertificateDirectoryName);

                Directory.CreateDirectory(certDirectory);

                pfxPath = Path.Combine(certDirectory, CertificateFileName);
                string passwordPath = Path.Combine(certDirectory, PasswordFileName);
                password = LoadOrCreatePassword(passwordPath);

                if (File.Exists(pfxPath))
                {
                    return true;
                }

                return TryCreateCertificateWithPowerShell(pfxPath, password, out error);
            }
            catch (Exception ex)
            {
                pfxPath = string.Empty;
                password = string.Empty;
                error = ex.Message;
                return false;
            }
        }

        private static string LoadOrCreatePassword(string passwordPath)
        {
            if (File.Exists(passwordPath))
            {
                string existing = File.ReadAllText(passwordPath).Trim();
                if (!string.IsNullOrEmpty(existing))
                {
                    return existing;
                }
            }

            File.WriteAllText(passwordPath, DefaultPassword);
            return DefaultPassword;
        }

        private static bool TryCreateCertificateWithPowerShell(string pfxPath, string password, out string error)
        {
            error = string.Empty;

            string escapedPfxPath = EscapePowerShellSingleQuotedString(pfxPath);
            string escapedPassword = EscapePowerShellSingleQuotedString(password);
            string escapedFriendlyName = EscapePowerShellSingleQuotedString(FriendlyName);

            string script = string.Join("; ", new[]
            {
                "$ErrorActionPreference = 'Stop'",
                "$pfxPath = '" + escapedPfxPath + "'",
                "$password = ConvertTo-SecureString '" + escapedPassword + "' -AsPlainText -Force",
                "$friendlyName = '" + escapedFriendlyName + "'",
                "$existing = Get-ChildItem Cert:\\CurrentUser\\My | Where-Object { $_.FriendlyName -eq $friendlyName -and $_.NotAfter -gt (Get-Date).AddDays(7) } | Sort-Object NotAfter -Descending | Select-Object -First 1",
                "if (-not $existing) { $existing = New-SelfSignedCertificate -Subject 'CN=localhost' -FriendlyName $friendlyName -CertStoreLocation 'Cert:\\CurrentUser\\My' -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(5) -TextExtension @('2.5.29.17={text}DNS=localhost&IPAddress=127.0.0.1&IPAddress=::1','2.5.29.37={text}1.3.6.1.5.5.7.3.1') }",
                "$rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root','CurrentUser')",
                "$rootStore.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)",
                "$alreadyTrusted = $rootStore.Certificates | Where-Object { $_.Thumbprint -eq $existing.Thumbprint } | Select-Object -First 1",
                "if (-not $alreadyTrusted) { $rootStore.Add($existing) }",
                "$rootStore.Close()",
                "Export-PfxCertificate -Cert $existing -FilePath $pfxPath -Password $password -Force | Out-Null"
            });

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();

                string stdOut = process.StandardOutput.ReadToEnd();
                string stdErr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && File.Exists(pfxPath))
                {
                    return true;
                }

                error = string.IsNullOrWhiteSpace(stdErr) ? stdOut.Trim() : stdErr.Trim();
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "PowerShell certificate provisioning failed.";
                }

                return false;
            }
        }

        private static string EscapePowerShellSingleQuotedString(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }
    }
}