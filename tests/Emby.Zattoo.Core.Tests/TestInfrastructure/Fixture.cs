namespace Emby.Zattoo.Core.Tests.TestInfrastructure;

internal static class Fixture
{
    public static string Read(string fileName)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
    }
}
