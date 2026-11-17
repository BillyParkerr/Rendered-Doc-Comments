using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RenderedDocComments.Navigation;
using RenderedDocComments.Rendering;
using Xunit;

namespace RenderedDocComments.Tests
{
    /// <summary>
    /// What the quick documentation popup finds at a position, resolved with Roslyn against sample
    /// source and a compiled library with its own documentation file (standing in for .NET's).
    /// </summary>
    public class QuickDocResolverTests
    {
        private const string Library = @"
namespace Lib
{
    /// <summary>A library widget.</summary>
    public class Widget
    {
        /// <summary>Spins the widget.</summary>
        /// <param name=""turns"">How many times.</param>
        public void Spin(int turns) { }
    }
}";

        private const string Source = @"
using System;
using System.Collections.Generic;

namespace Shop
{
    /// <summary>A product in the shop.</summary>
    /// <typeparam name=""TId"">The key type.</typeparam>
    public class Product<TId>
    {
        /// <summary>Makes a product.</summary>
        /// <param name=""id"">Its key.</param>
        public Product(TId id) { Id = id; }

        /// <summary>The key.</summary>
        public TId Id { get; }

        /// <summary>The highest price, in pence.</summary>
        public const int MaxPrice = 100;

        /// <summary>Renames it.</summary>
        /// <param name=""name"">The new name.</param>
        /// <returns>True if it changed.</returns>
        public virtual bool Rename(string name) { var trimmed = name.Trim(); return trimmed.Length > 0; }
    }

    public class Gadget : Product<int>
    {
        public Gadget() : base(1) { }

        public override bool Rename(string name) => false;
    }

    /// <summary>Buys from the shop. See <see cref=""Product{TId}""/>.</summary>
    public static class Shopper
    {
        /// <summary>Buys things.</summary>
        public static void Buy(Lib.Widget widget)
        {
            var product = new Product<int>(5);
            product.Rename(""x"");
            string text = ""y"";
            Console.WriteLine(text);
            product.Weigh();
            widget.Spin(2);
        }

        /// <summary>Weighs a product.</summary>
        /// <param name=""product"">The product.</param>
        public static int Weigh(this Product<int> product) => 0;

        public static void Undocumented() { }
    }

    /// <summary>Money.</summary>
    public readonly struct Money { }

    /// <summary>A stack-only buffer.</summary>
    public ref struct Buffer { }

    /// <summary>A read-only stack-only buffer.</summary>
    public readonly ref struct ReadOnlyBuffer { }

    /// <summary>A plain struct.</summary>
    public struct Point { }

    /// <summary>Helpers.</summary>
    public static class Tools { }

    /// <summary>A base.</summary>
    public abstract class Shape { }
}
";

        private static readonly Lazy<Document> SharedDocument = new Lazy<Document>(CreateDocument);

        private static Document CreateDocument()
        {
            var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

            // A referenced library with a documentation file, as .NET's assemblies have.
            var library = CSharpCompilation.Create(
                "Lib",
                new[] { CSharpSyntaxTree.ParseText(Library, new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose)) },
                new[] { mscorlib },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var image = new MemoryStream();
            var docs = new MemoryStream();
            Assert.True(library.Emit(image, xmlDocumentationStream: docs).Success);
            var libraryReference = MetadataReference.CreateFromImage(image.ToArray(), documentation: XmlDocumentationProvider.CreateFromBytes(docs.ToArray()));

            var workspace = new AdhocWorkspace();
            var project = workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "Shop",
                "Shop",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose),
                metadataReferences: new[] { mscorlib, libraryReference }));
            return workspace.AddDocument(project.Id, "Shop.cs", SourceText.From(Source));
        }

        /// <summary>What the popup shows at the start of <paramref name="text"/>'s <paramref name="name"/> (or <paramref name="offset"/> into it).</summary>
        private static async Task<QuickDocTarget> AtAsync(string text, string name, int offset = 0)
        {
            int at = Source.IndexOf(text, StringComparison.Ordinal);
            Assert.True(at >= 0, "Not in the source: " + text);
            int position = at + text.IndexOf(name, StringComparison.Ordinal) + offset;
            return await QuickDocResolver.AtPositionAsync(SharedDocument.Value, position, CancellationToken.None);
        }

        private static string Signature(QuickDocContent content) => string.Concat(content.Signature.Select(p => p.Text));

        [Fact]
        public async Task Sample_source_compiles_cleanly()
        {
            var model = await SharedDocument.Value.GetSemanticModelAsync();
            var problems = model.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error || (d.Severity == DiagnosticSeverity.Warning && d.Id != "CS1591"));
            Assert.Empty(problems);
        }

        [Fact]
        public async Task A_method_call_shows_the_methods_docs_and_signature()
        {
            var target = await AtAsync("product.Rename(\"x\")", "Rename", 2);
            var content = target.Content;
            Assert.Contains("Renames it.", content.Xml);
            Assert.Contains("True if it changed.", content.Xml);
            Assert.Equal("virtual bool Product<int>.Rename(string name)", Signature(content));
            Assert.Equal("Shop", content.Container);
            Assert.Null(content.Assembly);
            Assert.Equal("Rename", target.Symbol.Name);
        }

        [Fact]
        public async Task The_name_just_before_the_cursor_counts()
        {
            var target = await AtAsync("product.Rename(\"x\")", "(\"x\")"); // Cursor right after "Rename".
            Assert.Equal("Rename", target.Symbol.Name);
        }

        [Fact]
        public async Task Away_from_any_name_finds_nothing()
        {
            Assert.Null(await AtAsync("product.Rename(\"x\");", ";"));
        }

        [Fact]
        public async Task The_symbols_own_name_goes_to_its_definition_and_types_link_to_their_docs()
        {
            var parts = (await AtAsync("product.Rename(\"x\")", "Rename")).Content.Signature;
            Assert.Equal("Rename", parts.Single(p => p.IsName).Text);
            Assert.Equal("T:Shop.Product`1", parts.Single(p => p.Text == "Product").LinkId);
            Assert.Equal(CodeTokenKind.Parameter, parts.Single(p => p.Text == "name").Kind);
            Assert.Equal(CodeTokenKind.MethodName, parts.Single(p => p.Text == "Rename").Kind);
        }

        [Fact]
        public async Task New_shows_the_constructor()
        {
            var content = (await AtAsync("new Product<int>(5)", "Product")).Content;
            Assert.Contains("Makes a product.", content.Xml);
            Assert.Contains("Product(int id)", Signature(content));
        }

        [Fact]
        public async Task Var_shows_the_type()
        {
            var content = (await AtAsync("var product", "var")).Content;
            Assert.Equal("class Product<int>", Signature(content));
            Assert.Contains("A product in the shop.", content.Xml);
        }

        [Fact]
        public async Task A_declarations_own_name_shows_its_docs()
        {
            var content = (await AtAsync("public virtual bool Rename", "Rename")).Content;
            Assert.Contains("Renames it.", content.Xml);
            Assert.Null(content.InheritedFrom);
        }

        [Fact]
        public async Task A_constant_shows_its_value()
        {
            Assert.Equal("const int Product<TId>.MaxPrice = 100", Signature((await AtAsync("MaxPrice = 100", "MaxPrice")).Content));
        }

        [Fact]
        public async Task An_undocumented_override_shows_the_overridden_docs()
        {
            var content = (await AtAsync("public override bool Rename", "Rename")).Content;
            Assert.Contains("Renames it.", content.Xml);
            Assert.Equal("M:Shop.Product`1.Rename(System.String)", content.InheritedFrom);
        }

        [Fact]
        public async Task An_undocumented_member_has_no_docs()
        {
            var content = (await AtAsync("public static void Undocumented", "Undocumented")).Content;
            Assert.Null(content.Xml);
            Assert.Equal("static void Shopper.Undocumented()", Signature(content));
        }

        [Fact]
        public async Task A_parameter_shows_its_description_from_the_method()
        {
            var content = (await AtAsync("name.Trim()", "name")).Content;
            Assert.Equal("parameter", content.Kind);
            Assert.Equal("string name", Signature(content));
            Assert.Equal("<summary>The new name.</summary>", content.Xml);
            Assert.Equal("Product<TId>.Rename", content.Container);
            Assert.Equal("M:Shop.Product`1.Rename(System.String)", content.ContainerId);
        }

        [Fact]
        public async Task A_type_parameter_shows_its_description_from_the_type()
        {
            var content = (await AtAsync("public TId Id", "TId")).Content;
            Assert.Equal("type parameter", content.Kind);
            Assert.Equal("<summary>The key type.</summary>", content.Xml);
        }

        [Fact]
        public async Task A_local_shows_its_type()
        {
            var content = (await AtAsync("var trimmed", "trimmed")).Content;
            Assert.Equal("local variable", content.Kind);
            Assert.Equal("string trimmed", Signature(content));
            Assert.Null(content.Xml);
        }

        [Theory]
        [InlineData("public readonly struct Money", "Money", "readonly struct Money")]
        [InlineData("public ref struct Buffer", "Buffer", "ref struct Buffer")]
        [InlineData("public readonly ref struct ReadOnlyBuffer", "ReadOnlyBuffer", "readonly ref struct ReadOnlyBuffer")]
        [InlineData("public struct Point", "Point", "struct Point")]
        [InlineData("public static class Tools", "Tools", "static class Tools")]
        [InlineData("public abstract class Shape", "Shape", "abstract class Shape")]
        [InlineData("public class Gadget", "Gadget", "class Gadget")]
        public async Task Type_modifiers_appear_once(string declaration, string name, string signature)
        {
            Assert.Equal(signature, Signature((await AtAsync(declaration, name)).Content));
        }

        [Fact]
        public async Task A_keyword_type_shows_the_framework_type()
        {
            var content = (await AtAsync("string text", "string")).Content;
            Assert.Equal("sealed class String", Signature(content));
            Assert.Equal("System", content.Container);
            Assert.NotNull(content.Assembly);
        }

        [Fact]
        public async Task An_extension_method_call_shows_the_methods_docs()
        {
            var content = (await AtAsync("product.Weigh()", "Weigh")).Content;
            Assert.Contains("Weighs a product.", content.Xml);
            Assert.Contains("this Product<int> product", Signature(content));
        }

        [Fact]
        public async Task A_library_member_shows_docs_from_its_documentation_file()
        {
            var content = (await AtAsync("widget.Spin(2)", "Spin")).Content;
            Assert.Contains("Spins the widget.", content.Xml);
            Assert.Equal("Lib", content.Assembly);
            Assert.Equal("Lib", content.Container);
        }

        [Fact]
        public async Task A_library_type_shows_docs_from_its_documentation_file()
        {
            var content = (await AtAsync("public static void Buy(Lib.Widget widget)", "Widget")).Content;
            Assert.Contains("A library widget.", content.Xml);
            Assert.Equal("class Widget", Signature(content));
        }

        [Fact]
        public async Task A_cref_in_a_doc_comment_shows_what_it_names()
        {
            var content = (await AtAsync("<see cref=\"Product{TId}\"/>", "Product")).Content;
            Assert.Contains("A product in the shop.", content.Xml);
        }

        [Fact]
        public async Task A_documentation_id_finds_its_symbol()
        {
            var compilation = await SharedDocument.Value.Project.GetCompilationAsync();
            var target = QuickDocResolver.ForId("T:Shop.Product`1", compilation, CancellationToken.None);
            Assert.Equal("Product<TId>", target.Content.Title);
            Assert.Contains("A product in the shop.", target.Content.Xml);

            Assert.Equal("Rename", QuickDocResolver.ForId("M:Shop.Product`1.Rename(System.String)", compilation, CancellationToken.None).Symbol.Name);
            Assert.Null(QuickDocResolver.ForId("!:Nowhere", compilation, CancellationToken.None));
            Assert.Null(QuickDocResolver.ForId(null, compilation, CancellationToken.None));
        }

        [Fact]
        public async Task Every_position_in_the_source_is_safe()
        {
            for (int position = 0; position <= Source.Length; position++)
            {
                var exception = await Record.ExceptionAsync(() => QuickDocResolver.AtPositionAsync(SharedDocument.Value, position, CancellationToken.None));
                Assert.True(exception == null, "Threw at " + position + ": " + exception);
            }
        }
    }
}
