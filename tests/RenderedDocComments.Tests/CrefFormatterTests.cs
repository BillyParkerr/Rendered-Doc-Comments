using RenderedDocComments.Parsing;
using Xunit;

namespace RenderedDocComments.Tests
{
    /// <summary>How a cref is shown on a card before Roslyn resolves it.</summary>
    public class CrefFormatterTests
    {
        [Theory]
        // Documentation IDs, as Roslyn writes them into inherited docs.
        [InlineData("T:System.String", "String", "")]
        [InlineData("N:System.Collections", "Collections", "")]
        [InlineData("M:System.String.Format(System.String,System.Object)", "String", ".Format()")]
        [InlineData("P:System.String.Length", "String", ".Length")]
        [InlineData("F:System.Int32.MaxValue", "Int32", ".MaxValue")]
        [InlineData("E:System.AppDomain.UnhandledException", "AppDomain", ".UnhandledException")]
        [InlineData("M:Ns.Widget.#ctor", "Widget", ".ctor()")]
        [InlineData("T:System.Collections.Generic.List`1", "List", "")]
        [InlineData("M:System.Collections.Generic.List`1.Add(`0)", "List", ".Add()")]
        [InlineData("M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})", "Enumerable", ".Select()")]
        // Crefs as typed in source.
        [InlineData("List{T}", "List<T>", "")]
        [InlineData("Dictionary{TKey, TValue}.TryGetValue", "Dictionary<TKey, TValue>", ".TryGetValue")]
        [InlineData("Widget.Resize(int, int)", "Widget", ".Resize()")]
        [InlineData("Resize(int)", "", "Resize()")]
        [InlineData("count", "", "count")]
        [InlineData("Widget", "Widget", "")]
        [InlineData("  Widget  ", "Widget", "")]
        public void Splits_into_type_and_member(string cref, string type, string member)
        {
            var display = CrefFormatter.Format(cref);
            Assert.Equal(type, display.TypePart);
            Assert.Equal(member, display.MemberPart);
            Assert.Equal(type + member, display.ToString());
        }

        [Theory]
        // <exception> and <permission> crefs always name a type, so the dots are a namespace.
        [InlineData("System.Security.PermissionSet", "PermissionSet", "")]
        [InlineData("System.ArgumentNullException", "ArgumentNullException", "")]
        [InlineData("IO.IOException", "IOException", "")]
        [InlineData("T:System.IO.IOException", "IOException", "")]
        [InlineData("System.Collections.Generic.KeyNotFoundException", "KeyNotFoundException", "")]
        // Methods and members stay as they are.
        [InlineData("M:System.String.Format(System.String)", "String", ".Format()")]
        [InlineData("Widget.Resize(int)", "Widget", ".Resize()")]
        [InlineData("P:System.String.Length", "String", ".Length")]
        public void A_cref_known_to_name_a_type_shows_the_type(string cref, string type, string member)
        {
            var display = CrefFormatter.Format(cref, isType: true);
            Assert.Equal(type, display.TypePart);
            Assert.Equal(member, display.MemberPart);
        }

        [Fact]
        public void Without_knowing_it_is_a_type_a_dotted_name_reads_as_type_and_member()
        {
            Assert.Equal("Security.PermissionSet", CrefFormatter.Format("System.Security.PermissionSet").ToString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Empty_cref_shows_nothing(string cref)
        {
            Assert.Equal(string.Empty, CrefFormatter.Format(cref).ToString());
        }

        [Theory]
        [InlineData("T:")]
        [InlineData("M:(")]
        [InlineData("{{{")]
        [InlineData("}.}.")]
        [InlineData(".")]
        [InlineData("``")]
        public void Odd_crefs_do_not_throw(string cref)
        {
            Assert.Null(Record.Exception(() => CrefFormatter.Format(cref)));
        }
    }
}
