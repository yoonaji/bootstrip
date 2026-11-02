using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BootDiet.Core;
using BootDiet.Core.Models;

namespace BootDiet.Tests;

public class CertificateAndFirewallTests
{
    private static X509Certificate2 SelfSigned(string subject, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notAfter.AddYears(-1), notAfter);
    }

    /// <summary>Windows 저장소 Blob 형식: [속성 ID · 예약(1) · 길이 · 내용] 반복.</summary>
    private static byte[] Blob(params (int Id, byte[] Data)[] properties)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (var (id, data) in properties)
        {
            w.Write(id);
            w.Write(1);
            w.Write(data.Length);
            w.Write(data);
        }
        return ms.ToArray();
    }

    [Fact]
    public void ReadsRootCertificateFromRegistryBlob()
    {
        using var cert = SelfSigned("CN=CrossEX RootCA2, O=iniLINE", new DateTimeOffset(2099, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var blob = Blob((0x03, new byte[20]), (0x20, cert.RawData)); // 앞에 다른 속성(해시)이 있어도 찾는다

        var c = RootCertificateEntry.FromBlob(blob, Scope.Machine)!;

        Assert.Equal("CrossEX RootCA2", c.Name);
        Assert.Equal(ComponentKind.RootCertificate, c.Kind);
        Assert.StartsWith("2099-12-31까지 유효", c.Detail);
        Assert.EndsWith(cert.Thumbprint, c.Location);
        Assert.Equal("kr-crossex", TestData.Classifier.Classify(c).RuleId);
    }

    [Fact]
    public void BrokenBlobIsIgnored()
    {
        Assert.Null(RootCertificateEntry.FromBlob(Blob((0x03, new byte[20])), Scope.Machine));
        Assert.Null(RootCertificateEntry.ExtractCertificate([0x20, 0, 0, 0, 1, 0, 0, 0, 0xFF, 0xFF, 0, 0]));
    }

    [Theory]
    [InlineData("INTEREZEN CA", "kr-ipinside")]
    [InlineData("ASTxRoot2", "kr-astx")]
    [InlineData("INCA G3", "kr-nprotect")]
    [InlineData("DigiCert Global Root G2", "win-public-root")]
    public void KnownRootCertificatesFollowTheirProduct(string name, string ruleId)
    {
        var c = new Component { Name = name, Kind = ComponentKind.RootCertificate, Scope = Scope.Machine, Command = $"CN={name}" };
        Assert.Equal(ruleId, TestData.Classifier.Classify(c).RuleId);
    }

    [Fact]
    public void MicrosoftRootsAreWindowsOwn()
    {
        var c = new Component { Name = "Microsoft Root Certificate Authority 2011", Kind = ComponentKind.RootCertificate,
            Command = "CN=Microsoft Root Certificate Authority 2011, O=Microsoft Corporation" };
        Assert.True(WindowsOwnFilter.IsWindowsOwn(c, @"C:\Windows"));
    }

    private static string Expand(string s) => s.Replace("%ProgramFiles% (x86)", @"C:\Program Files (x86)");

    [Fact]
    public void ParsesInboundAllowRuleOpenToPublicNetworks()
    {
        const string text = @"v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=6|LPort=10530|LPort=10531|App=%ProgramFiles% (x86)\SoftForum\XecureWeb\AnySign\dll\AnySign4PC.exe|Name=AnySign4PC|";

        var c = FirewallRuleEntry.Parse("{1234}", text, Expand)!;

        Assert.Equal("AnySign4PC", c.Name);
        Assert.Equal(ComponentKind.FirewallRule, c.Kind);
        Assert.Equal(@"C:\Program Files (x86)\SoftForum\XecureWeb\AnySign\dll\AnySign4PC.exe", c.ExecutablePath);
        Assert.True(c.Enabled);
        Assert.Equal("모든 네트워크 · TCP 10530,10531", c.Detail);
        Assert.Equal("kr-anysign", TestData.Classifier.Classify(c).RuleId);
    }

    [Theory]
    [InlineData(@"v2.31|Action=Allow|Active=TRUE|Dir=Out|App=C:\x\a.exe|Name=out|")]               // 나가는 연결
    [InlineData(@"v2.31|Action=Block|Active=TRUE|Dir=In|App=C:\x\a.exe|Name=block|")]             // 차단 규칙
    [InlineData(@"v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=6|LPort=445|App=System|Name=smb|")] // 시스템
    [InlineData(@"v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=6|LPort=80|Name=no-app|")]          // 프로그램 지정 없음
    public void SkipsRulesThatAreNotProgramInboundAllows(string text)
    {
        Assert.Null(FirewallRuleEntry.Parse("x", text));
    }

    [Fact]
    public void ProfileAndResourceNameAreReadable()
    {
        const string text = @"v2.31|Action=Allow|Active=FALSE|Dir=In|Protocol=17|Profile=Private|Profile=Domain|App=C:\Apps\Tool\tool.exe|Name=@{Some.Package/resource}|";

        var c = FirewallRuleEntry.Parse("x", text)!;

        Assert.Equal("tool", c.Name);
        Assert.False(c.Enabled);
        Assert.Equal("개인·도메인 · UDP 모든 포트", c.Detail);
    }

    [Fact]
    public void FirewallOnlyProductsAreDroppedUnlessSecurityModules()
    {
        Component Fw(string name, string path) => new() { Name = name, Kind = ComponentKind.FirewallRule, Scope = Scope.Machine, ExecutablePath = path };

        var products = TestData.Classifier.ClassifyProducts(
        [
            Fw("python", @"C:\Python312\python.exe"),
            Fw("AnySign4PC", @"C:\Program Files (x86)\SoftForum\XecureWeb\AnySign\dll\AnySign4PC.exe"),
            new Component { Name = "KakaoTalk", Kind = ComponentKind.RunKey, ExecutablePath = @"C:\Program Files\Kakao\KakaoTalk\KakaoTalk.exe" },
            Fw("KakaoTalk", @"C:\Program Files\Kakao\KakaoTalk\KakaoTalk.exe"),
        ]);

        var kept = WindowsOwnFilter.DropFirewallOnlyProducts(products);

        Assert.DoesNotContain(kept, p => p.Name == "python");
        Assert.Contains(kept, p => p.Verdict.RuleId == "kr-anysign");
        Assert.Equal(2, kept.Single(p => p.Verdict.RuleId == "app-messenger").Components.Count);
    }
}
