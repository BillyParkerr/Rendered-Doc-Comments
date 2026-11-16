using RenderedDocComments.Options;
using Xunit;

namespace RenderedDocComments.Tests
{
    /// <summary>Typed shortcuts for the quick documentation option, tidied into VS's form.</summary>
    public class ShortcutTextTests
    {
        [Theory]
        [InlineData("Ctrl+Shift+F1", "Ctrl+Shift+F1")]
        [InlineData("ctrl+shift+f1", "Ctrl+Shift+F1")]
        [InlineData(" ctrl + shift + f1 ", "Ctrl+Shift+F1")]
        [InlineData("Control+Q", "Ctrl+Q")]
        [InlineData("alt+q", "Alt+Q")]
        [InlineData("ctrl+k,ctrl+q", "Ctrl+K, Ctrl+Q")]
        [InlineData("Ctrl+K, Ctrl+Q", "Ctrl+K, Ctrl+Q")]
        [InlineData("ctrl+f12", "Ctrl+F12")]
        [InlineData("ctrl+space", "Ctrl+Space")]
        [InlineData("Ctrl++Q", "Ctrl+Q")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        [InlineData(null, "")]
        [InlineData(",", "")]
        public void Normalizes(string typed, string expected)
        {
            Assert.Equal(expected, ShortcutText.Normalize(typed));
        }
    }
}
