using BootDiet.Core;
using BootDiet.Core.Ai;
using BootDiet.Core.Models;

namespace BootDiet.Tests;

public class ConsentTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("bootdiet-consent-").FullName;

    [Fact]
    public void 처음에는_묻지_않은_상태고_동의와_거절이_파일에_남는다()
    {
        var dir = TempDir();
        var c = Consent.AiTransfer(dir);
        Assert.Null(c.State);
        Assert.False(c.IsGranted);

        c.Grant();
        Assert.True(Consent.AiTransfer(dir).IsGranted);

        c.Decline();
        Assert.False(Consent.AiTransfer(dir).State);

        c.Reset();
        Assert.Null(Consent.AiTransfer(dir).State);
    }

    [Fact]
    public void 고지_버전이_바뀌면_다시_묻는다()
    {
        var dir = TempDir();
        var c = Consent.Notice(dir);
        File.WriteAllText(c.Path, "agree v0 2026-11-01T00:00:00+09:00\n");
        Assert.Null(c.State);
    }

    [Fact]
    public void AI에_보내는_루트_인증서_정보에는_지문이_없다()
    {
        var cert = new Component
        {
            Kind = ComponentKind.RootCertificate,
            Name = "CrossEX Root CA",
            Detail = "2099-12-31까지 유효 · 지문 1A2B3C4D",
        };
        var o = ProductBrief.Component(cert, "c1");
        Assert.Equal("2099-12-31까지 유효", o["detail"]!.GetValue<string>());
    }
}
