using BootDiet.Core;

namespace BootDiet.Tests;

public class CommandLineParserTests
{
    private static string? Parse(string command) =>
        CommandLineParser.ExtractExecutablePath(command, s => s.Replace("%ProgramFiles%", @"C:\Program Files"));

    [Theory]
    [InlineData("\"C:\\Program Files\\LGHUB\\system_tray\\lghub_system_tray.exe\" --minimized", @"C:\Program Files\LGHUB\system_tray\lghub_system_tray.exe")]
    [InlineData(@"C:\Program Files (x86)\iniLINE\CrossEX\crossex\CrossEXService.exe", @"C:\Program Files (x86)\iniLINE\CrossEX\crossex\CrossEXService.exe")]
    [InlineData(@"C:\Users\user\AppData\Local\Programs\Notion\Notion.exe --open-at-login", @"C:\Users\user\AppData\Local\Programs\Notion\Notion.exe")]
    [InlineData(@"C:\Program Files\Teams Installer\Teams.exe --checkInstall --source=PROPLUS", @"C:\Program Files\Teams Installer\Teams.exe")]
    [InlineData(@"%ProgramFiles%\App\app.exe /tray", @"C:\Program Files\App\app.exe")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL", "rundll32.exe")]
    public void ExtractsExecutablePath(string command, string expected)
    {
        Assert.Equal(expected, Parse(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNullForEmpty(string? command)
    {
        Assert.Null(CommandLineParser.ExtractExecutablePath(command));
    }
}
