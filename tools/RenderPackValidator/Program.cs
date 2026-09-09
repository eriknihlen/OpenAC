namespace AcDream.Tools.RenderPackValidator;

internal static class Program
{
    private static int Main(string[] args) =>
        RenderPackValidatorCommand.Run(args, Console.Out, Console.Error);
}
