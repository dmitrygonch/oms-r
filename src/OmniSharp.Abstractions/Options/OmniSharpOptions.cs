using System;

namespace OmniSharp.Options
{
    public class OmniSharpOptions
    {
        public RoslynExtensionsOptions RoslynExtensionsOptions { get; } = new RoslynExtensionsOptions();

        public FormattingOptions FormattingOptions { get; } = new FormattingOptions();

        public FileOptions FileOptions { get; } = new FileOptions();
    }

    public static class HackOptions
    {
        public static bool Enabled => Environment.GetEnvironmentVariable("HACK_ENABLED") == "1";
    }
}
