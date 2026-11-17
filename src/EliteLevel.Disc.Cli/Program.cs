namespace EliteLevel.Disc.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => CliHost.RunAsync(args, Console.Out, Console.Error);
}
