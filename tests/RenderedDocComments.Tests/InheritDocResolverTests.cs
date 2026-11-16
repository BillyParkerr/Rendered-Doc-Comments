using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RenderedDocComments.Navigation;
using RenderedDocComments.Parsing;
using Xunit;

namespace RenderedDocComments.Tests
{
    /// <summary>
    /// What &lt;inheritdoc/&gt; shows, resolved with Roslyn the way the editor does it, against
    /// documentation declared in the same source (no framework XML files needed).
    /// </summary>
    public class InheritDocResolverTests
    {
        private const string Source = @"
using System;

namespace Shapes
{
    /// <summary>A shape.</summary>
    /// <remarks>Base remarks.</remarks>
    public abstract class Shape
    {
        /// <summary>Makes a shape.</summary>
        /// <param name=""name"">The shape's name.</param>
        protected Shape(string name) { }

        /// <summary>The area.</summary>
        /// <returns>Square units.</returns>
        public abstract double Area();

        /// <summary>Scales the shape.</summary>
        /// <param name=""factor"">How much.</param>
        /// <param name=""around"">The fixed point.</param>
        /// <exception cref=""ArgumentOutOfRangeException"">Factor is negative.</exception>
        public virtual void Scale(double factor, int around) { }

        /// <summary>The name.</summary>
        public virtual string Name => ""shape"";
    }

    /// <summary>Something drawable.</summary>
    public interface IDrawable
    {
        /// <summary>Draws it.</summary>
        /// <param name=""canvas"">Where.</param>
        void Draw(object canvas);
    }

    /// <summary>Converts things.</summary>
    public static class Converter
    {
        /// <summary>Converts a value.</summary>
        /// <typeparam name=""T"">The value type.</typeparam>
        /// <param name=""value"">The value.</param>
        /// <param name=""culture"">Unused here.</param>
        public static string Convert<T>(T value, string culture) => null;
    }

    /// <inheritdoc/>
    public class Circle : Shape, IDrawable
    {
        /// <inheritdoc/>
        public Circle(string name) : base(name) { }

        /// <inheritdoc/>
        public override double Area() => 0;

        /// <inheritdoc/>
        /// <remarks>Own remarks win.</remarks>
        public override void Scale(double factor, int around) { }

        /// <inheritdoc/>
        public void Draw(object canvas) { }

        /// <inheritdoc/>
        public override string Name => ""circle"";

        /// <inheritdoc cref=""Converter.Convert{T}(T, string)""/>
        public string Describe<T>(T value) => null;

        /// <inheritdoc cref=""DoesNotExist""/>
        public void Broken() { }

        /// <inheritdoc/>
        public void NothingToInherit() { }
    }

    /// <summary>An undocumented override in the middle of the chain.</summary>
    public class BigCircle : Circle
    {
        public BigCircle() : base(""big"") { }

        public override double Area() => 1;
    }

    /// <summary>Guarded things.</summary>
    public class Guarded
    {
        /// <summary>Opens it.</summary>
        /// <permission cref=""System.Security.PermissionSet"">Anyone.</permission>
        public virtual void Open() { }
    }

    /// <summary>More guarded.</summary>
    public class MoreGuarded : Guarded
    {
        /// <inheritdoc/>
        /// <permission cref=""System.Security.SecurityException"">Admins.</permission>
        public override void Open() { }
    }

    /// <summary>Most guarded.</summary>
    public class MostGuarded : MoreGuarded
    {
        /// <inheritdoc/>
        public sealed override void Open() { }
    }

    /// <inheritdoc/>
    public class HugeCircle : BigCircle
    {
        /// <inheritdoc/>
        public override double Area() => 2;

        /// <inheritdoc/>
        public override string Name => ""huge"";
    }
}
";

        private static readonly Lazy<Document> SharedDocument = new Lazy<Document>(CreateDocument);

        private static Document CreateDocument()
        {
            var workspace = new AdhocWorkspace();
            var project = workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "Shapes",
                "Shapes",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose),
                metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }));
            return workspace.AddDocument(project.Id, "Shapes.cs", SourceText.From(Source));
        }

        /// <summary>Resolve the doc comment directly above the line containing the declaration text.</summary>
        private static async Task<InheritedDoc> ResolveAboveAsync(string declaration)
        {
            var document = SharedDocument.Value;
            var lines = (await document.GetTextAsync()).Lines;
            int declarationLine = lines.First(l => l.ToString().Contains(declaration)).LineNumber;
            int first = declarationLine, last = declarationLine - 1;
            while (first - 1 >= 0 && lines[first - 1].ToString().TrimStart().StartsWith("///", StringComparison.Ordinal)) first--;
            Assert.True(first <= last, "No doc comment above " + declaration);

            int start = lines[first].Start + lines[first].ToString().IndexOf("///", StringComparison.Ordinal);
            var span = TextSpan.FromBounds(start, lines[last].End);
            return await InheritDocResolver.ResolveAsync(document, span, CancellationToken.None);
        }

        private static DocModel Model(InheritedDoc doc)
        {
            Assert.True(doc.Found, "Nothing inherited.");
            return DocModel.Parse(doc.Xml);
        }

        private static string Text(DocNode node) => node == null ? null : LenientXmlParser.InnerText(node).Trim();

        [Fact]
        public async Task Sample_source_compiles_cleanly()
        {
            var model = await SharedDocument.Value.GetSemanticModelAsync();
            var problems = model.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error
                || (d.Severity == DiagnosticSeverity.Warning && d.Id != "CS1574" && d.Id != "CS1591"));
            // Allowed: CS1574 for the deliberately broken cref, CS1591 for the deliberately undocumented members.
            Assert.Empty(problems);
        }

        [Fact]
        public async Task Override_inherits_from_the_overridden_member()
        {
            var doc = await ResolveAboveAsync("public override double Area() => 0;");
            Assert.Equal("M:Shapes.Shape.Area", doc.SourceId);
            Assert.Equal("The area.", Text(Model(doc).Summary));
            Assert.Equal("Square units.", Text(Model(doc).Returns));
        }

        [Fact]
        public async Task Undocumented_overrides_in_between_are_skipped()
        {
            // HugeCircle.Area -> BigCircle.Area (no docs) -> Circle.Area (<inheritdoc/>) -> Shape.Area.
            var doc = await ResolveAboveAsync("public override double Area() => 2;");
            Assert.Equal("M:Shapes.Circle.Area", doc.SourceId);
            Assert.Equal("The area.", Text(Model(doc).Summary));
        }

        [Fact]
        public async Task A_chain_of_inheritdoc_is_followed()
        {
            // HugeCircle.Name -> Circle.Name (<inheritdoc/>) -> Shape.Name.
            var doc = await ResolveAboveAsync("public override string Name => \"huge\";");
            Assert.Equal("P:Shapes.Circle.Name", doc.SourceId);
            Assert.Equal("The name.", Text(Model(doc).Summary));
        }

        [Fact]
        public async Task Interface_implementation_inherits_from_the_interface()
        {
            var doc = await ResolveAboveAsync("public void Draw(object canvas)");
            Assert.Equal("M:Shapes.IDrawable.Draw(System.Object)", doc.SourceId);
            Assert.Equal("Where.", Text(Model(doc).Params.Single().Body));
        }

        [Fact]
        public async Task Class_inherits_from_its_base_class()
        {
            var doc = await ResolveAboveAsync("public class Circle : Shape, IDrawable");
            Assert.Equal("T:Shapes.Shape", doc.SourceId);
            Assert.Equal("A shape.", Text(Model(doc).Summary));
            Assert.Equal("Base remarks.", Text(Model(doc).Remarks));
        }

        [Fact]
        public async Task Class_chain_reaches_a_documented_ancestor()
        {
            var doc = await ResolveAboveAsync("public class HugeCircle : BigCircle");
            Assert.Equal("T:Shapes.BigCircle", doc.SourceId);
            Assert.Equal("An undocumented override in the middle of the chain.", Text(Model(doc).Summary));
        }

        [Fact]
        public async Task Constructor_inherits_from_the_matching_base_constructor()
        {
            var doc = await ResolveAboveAsync("public Circle(string name) : base(name)");
            Assert.Equal("Makes a shape.", Text(Model(doc).Summary));
            Assert.Equal("name", Model(doc).Params.Single().Name);
        }

        [Fact]
        public async Task Inherited_sections_include_everything_the_source_has()
        {
            // The comment's own <remarks> is merged on top by the card, not by the resolver.
            var model = Model(await ResolveAboveAsync("public override void Scale(double factor, int around)"));
            Assert.Equal("Scales the shape.", Text(model.Summary));
            Assert.Equal(new[] { "factor", "around" }, model.Params.Select(p => p.Name));
            Assert.Equal("T:System.ArgumentOutOfRangeException", model.Exceptions.Single().Name);
        }

        /// <summary>
        /// A cref to a generic method binds to a constructed copy of it; resolving that used to
        /// crash. Parameters the member doesn't have are dropped from what it inherits.
        /// </summary>
        [Fact]
        public async Task Cref_to_a_generic_method_keeps_only_matching_parameters()
        {
            var doc = await ResolveAboveAsync("public string Describe<T>(T value)");
            Assert.Equal("M:Shapes.Converter.Convert``1(``0,System.String)", doc.SourceId);

            var model = Model(doc);
            Assert.Equal("Converts a value.", Text(model.Summary));
            Assert.Equal(new[] { "value" }, model.Params.Select(p => p.Name));
            Assert.Equal(new[] { "T" }, model.TypeParams.Select(p => p.Name));
        }

        [Fact]
        public async Task A_chain_keeps_every_permission_by_cref()
        {
            // MostGuarded.Open -> MoreGuarded.Open (own permission + <inheritdoc/>) -> Guarded.Open.
            var model = Model(await ResolveAboveAsync("public sealed override void Open()"));
            Assert.Equal("Opens it.", Text(model.Summary));
            Assert.Equal(
                new[] { "T:System.Security.SecurityException", "T:System.Security.PermissionSet" },
                model.Permissions.Select(p => p.Name));
        }

        [Fact]
        public async Task Unresolvable_cref_finds_nothing()
        {
            Assert.False((await ResolveAboveAsync("public void Broken()")).Found);
        }

        [Fact]
        public async Task Member_with_nothing_to_inherit_finds_nothing()
        {
            Assert.False((await ResolveAboveAsync("public void NothingToInherit()")).Found);
        }

        [Fact]
        public async Task Span_outside_any_comment_finds_nothing()
        {
            var doc = await InheritDocResolver.ResolveAsync(SharedDocument.Value, new TextSpan(0, 1), CancellationToken.None);
            Assert.False(doc.Found);
        }
    }
}
