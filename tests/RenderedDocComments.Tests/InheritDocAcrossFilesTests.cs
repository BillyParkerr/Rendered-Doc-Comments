using System;
using System.IO;
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
    /// Inherited docs record which files they came from (every step of the chain), so an edit in
    /// a base class's file can refresh the card in another file.
    /// </summary>
    public class InheritDocAcrossFilesTests
    {
        private static readonly string Dir = Path.Combine(Path.GetTempPath(), "RenderedDocCommentsTests");
        private static readonly string BaseFile = Path.Combine(Dir, "Base.cs");
        private static readonly string MiddleFile = Path.Combine(Dir, "Middle.cs");
        private static readonly string DerivedFile = Path.Combine(Dir, "Derived.cs");

        private const string Base = @"
namespace Shapes
{
    public abstract class Shape
    {
        /// <summary>The area.</summary>
        /// <returns>Square units.</returns>
        public abstract double Area();

        /// <summary>Scales the shape.</summary>
        /// <param name=""factor"">How much.</param>
        public virtual void Scale(double factor) { }
    }
}";

        private const string Middle = @"
namespace Shapes
{
    /// <summary>A circle.</summary>
    public class Circle : Shape
    {
        public override double Area() => 0;

        /// <inheritdoc path=""/summary""/>
        public override void Scale(double factor) { }
    }
}";

        private const string Derived = @"
namespace Shapes
{
    /// <summary>A big circle.</summary>
    public class BigCircle : Circle
    {
        /// <inheritdoc/>
        public override double Area() => 1;

        /// <inheritdoc/>
        public override void Scale(double factor) { }
    }
}";

        private static Document DerivedDocument()
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var project = workspace.AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), "Shapes", "Shapes", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose),
                metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }));

            Document Add(string file, string text) => workspace.AddDocument(DocumentInfo.Create(
                DocumentId.CreateNewId(projectId), Path.GetFileName(file),
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(text), VersionStamp.Create(), file)),
                filePath: file));

            Add(BaseFile, Base);
            Add(MiddleFile, Middle);
            return Add(DerivedFile, Derived);
        }

        private static async Task<InheritedDoc> ResolveAboveAsync(Document document, string declaration)
        {
            var text = await document.GetTextAsync();
            var line = text.Lines.First(l => l.ToString().Contains(declaration));
            var comment = text.Lines[line.LineNumber - 1];
            int start = comment.Start + comment.ToString().IndexOf("///", StringComparison.Ordinal);
            return await InheritDocResolver.ResolveAsync(document, TextSpan.FromBounds(start, comment.End), CancellationToken.None);
        }

        [Fact]
        public async Task Every_file_on_the_way_is_recorded()
        {
            // BigCircle.Area -> Circle.Area (undocumented, Middle.cs) -> Shape.Area (Base.cs).
            var doc = await ResolveAboveAsync(DerivedDocument(), "public override double Area() => 1;");

            Assert.Equal("M:Shapes.Shape.Area", doc.SourceId);
            Assert.Contains(BaseFile, doc.SourceFiles, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(MiddleFile, doc.SourceFiles, StringComparer.OrdinalIgnoreCase); // Documenting Circle.Area would change the card.
        }

        [Fact]
        public async Task A_base_members_own_path_is_followed()
        {
            // BigCircle.Scale -> Circle.Scale (<inheritdoc path="/summary"/>) -> Shape.Scale: summary only.
            var doc = await ResolveAboveAsync(DerivedDocument(), "public override void Scale(double factor) { }");
            var model = DocModel.Parse(doc.Xml);

            Assert.Equal("Scales the shape.", LenientXmlParser.InnerText(model.Summary).Trim());
            Assert.Empty(model.Params);
            Assert.Contains(MiddleFile, doc.SourceFiles, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(BaseFile, doc.SourceFiles, StringComparer.OrdinalIgnoreCase);
        }
    }
}
