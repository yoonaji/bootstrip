using System.Buffers.Binary;
using System.Security.Cryptography.X509Certificates;
using BootDiet.Core.Models;

namespace BootDiet.Core;

/// <summary>
/// 직접 설치된 루트 인증서를 구성 요소로 바꾼다.
/// Windows 인증서 저장소 레지스트리(…\SystemCertificates\ROOT\Certificates\{지문}\Blob)를 읽는데,
/// Windows가 자동으로 받아 오는 공인 인증기관 목록(AuthRoot)은 여기 들어 있지 않아서
/// 프로그램이 몰래 넣은 루트 인증서만 골라낼 수 있다.
/// </summary>
public static class RootCertificateEntry
{
    public const string MachineRootKey = @"SOFTWARE\Microsoft\SystemCertificates\ROOT\Certificates";
    public const string UserRootKey = @"Software\Microsoft\SystemCertificates\Root\Certificates";

    private const int CertificatePropertyId = 0x20; // CERT_CERT_PROP_ID

    /// <summary>
    /// Blob 값에서 인증서(DER)를 꺼낸다. Blob은 [속성 ID(4) · 예약(4) · 길이(4) · 내용] 의 반복이다.
    /// </summary>
    public static byte[]? ExtractCertificate(ReadOnlySpan<byte> blob)
    {
        var offset = 0;
        while (offset + 12 <= blob.Length)
        {
            var id = BinaryPrimitives.ReadInt32LittleEndian(blob[offset..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(blob[(offset + 8)..]);
            offset += 12;
            if (length < 0 || offset + length > blob.Length)
                return null;
            if (id == CertificatePropertyId)
                return blob.Slice(offset, length).ToArray();
            offset += length;
        }
        return null;
    }

    public static Component ToComponent(X509Certificate2 cert, Scope scope)
    {
        var name = NullIfEmpty(cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false)) ?? cert.Subject;
        var key = scope == Scope.User ? $@"HKCU\{UserRootKey}" : $@"HKLM\{MachineRootKey}";
        return new Component
        {
            Name = name,
            Kind = ComponentKind.RootCertificate,
            Scope = scope,
            Location = $@"{key}\{cert.Thumbprint}",
            Command = cert.Subject,
            Enabled = true,
            Detail = $"{cert.NotAfter:yyyy-MM-dd}까지 유효 · 지문 {cert.Thumbprint[..8]}",
        };
    }

    public static Component? FromBlob(byte[] blob, Scope scope)
    {
        var der = ExtractCertificate(blob);
        if (der is null)
            return null;
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(der);
            return ToComponent(cert, scope);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
