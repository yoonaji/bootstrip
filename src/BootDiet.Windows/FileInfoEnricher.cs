using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using BootDiet.Core.Models;

namespace BootDiet.Windows;

/// <summary>실행 파일의 존재 여부, 버전 정보, 서명자를 채운다.</summary>
[SupportedOSPlatform("windows")]
public static class FileInfoEnricher
{
    public static StartupItem Enrich(StartupItem item)
    {
        var path = ResolvePath(item.ExecutablePath);
        if (path is null)
            return item;

        if (!File.Exists(path))
            return item with { ExecutablePath = path, FileExists = false };

        var version = FileVersionInfo.GetVersionInfo(path);
        return item with
        {
            ExecutablePath = path,
            FileExists = true,
            Company = NullIfEmpty(version.CompanyName) ?? item.Company,
            ProductName = NullIfEmpty(version.ProductName) ?? item.ProductName,
            FileVersion = NullIfEmpty(version.FileVersion),
            Signer = ReadSigner(path),
        };
    }

    /// <summary>"rundll32.exe"처럼 폴더 없이 적힌 경우 System32에서 찾는다.</summary>
    private static string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (Path.IsPathRooted(path))
            return path;
        var inSystem = Path.Combine(Environment.SystemDirectory, path);
        return File.Exists(inSystem) ? inSystem : path;
    }

    /// <summary>
    /// 서명 인증서의 CN만 읽는다. 서명이 유효한지(변조 여부)는 확인하지 않으며,
    /// 그 검증은 WinVerifyTrust로 나중에 추가한다.
    /// </summary>
    private static string? ReadSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
