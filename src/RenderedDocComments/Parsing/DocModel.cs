using System;
using System.Collections.Generic;
using System.Linq;

namespace RenderedDocComments.Parsing
{
    internal sealed class NamedSection
    {
        public NamedSection(string name, DocNode body)
        {
            Name = name;
            Body = body;
        }

        /// <summary>Parameter name, or the cref for exceptions.</summary>
        public string Name { get; }

        public DocNode Body { get; }
    }

    /// <summary>The sections of one doc comment, in the shape the renderer lays them out.</summary>
    internal sealed class DocModel
    {
        public DocNode Summary { get; private set; }
        public DocNode Remarks { get; private set; }
        public DocNode Returns { get; private set; }
        public DocNode Value { get; private set; }
        public DocNode Example { get; private set; }
        public DocNode InheritDoc { get; private set; }
        public List<NamedSection> TypeParams { get; } = new List<NamedSection>();
        public List<NamedSection> Params { get; } = new List<NamedSection>();
        public List<NamedSection> Exceptions { get; } = new List<NamedSection>();
        public List<DocNode> SeeAlso { get; } = new List<DocNode>();

        /// <summary>&lt;permission cref="..."&gt;: who may call it (by permission set).</summary>
        public List<NamedSection> Permissions { get; } = new List<NamedSection>();

        /// <summary>&lt;threadsafety static="..." instance="..."/&gt;.</summary>
        public DocNode ThreadSafety { get; private set; }

        /// <summary>&lt;include file="..." path="..."/&gt;: documentation kept in another file.</summary>
        public List<DocNode> Includes { get; } = new List<DocNode>();

        /// <summary>Other top-level tags (a team's own, like &lt;since&gt;), each shown as its own row, named by the tag.</summary>
        public List<NamedSection> Custom { get; } = new List<NamedSection>();

        public bool IsEmpty =>
            Summary == null && Remarks == null && Returns == null && Value == null && Example == null
            && InheritDoc == null && TypeParams.Count == 0 && Params.Count == 0 && Exceptions.Count == 0 && SeeAlso.Count == 0
            && Permissions.Count == 0 && ThreadSafety == null && Includes.Count == 0 && Custom.Count == 0;

        /// <summary>
        /// Formatting and block tags that make up text, so at the top level (outside any section)
        /// they're part of the summary rather than a section of their own.
        /// </summary>
        private static readonly HashSet<string> TextTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "c", "tt", "code", "pre", "b", "strong", "i", "em", "u", "s", "strike", "del", "sub", "sup", "br", "span",
            "see", "paramref", "typeparamref", "a", "para", "p", "list", "ul", "ol", "note",
            "h1", "h2", "h3", "h4", "h5", "h6",
        };

        /// <summary>Tags that aren't about this member: overload-group text, and old tooling hints.</summary>
        private static readonly HashSet<string> IgnoredTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "overloads", "filterpriority", "exclude", "preliminary",
        };

        public static DocModel Parse(string xml) => From(LenientXmlParser.Parse(xml));

        /// <summary>
        /// Fill in what this comment doesn't say from inherited documentation (for &lt;inheritdoc/&gt;).
        /// The comment's own sections always win; parameters, type parameters and exceptions are
        /// matched by name / cref.
        /// </summary>
        public void MergeInherited(DocModel inherited)
        {
            Summary ??= inherited.Summary;
            Remarks ??= inherited.Remarks;
            Returns ??= inherited.Returns;
            Value ??= inherited.Value;
            Example ??= inherited.Example;

            AddMissing(TypeParams, inherited.TypeParams, s => s.Name);
            AddMissing(Params, inherited.Params, s => s.Name);
            AddMissing(Exceptions, inherited.Exceptions, s => CrefFormatter.Format(s.Name).ToString());
            AddMissing(Permissions, inherited.Permissions, s => CrefFormatter.Format(s.Name).ToString());
            AddMissing(Custom, inherited.Custom, s => s.Name);
            ThreadSafety ??= inherited.ThreadSafety;

            var seeAlso = new HashSet<string>(SeeAlso.Select(SeeAlsoKey));
            SeeAlso.AddRange(inherited.SeeAlso.Where(s => seeAlso.Add(SeeAlsoKey(s))));
        }

        /// <summary>
        /// Replace each &lt;inheritdoc/&gt; inside a section (in the summary, a parameter...) with
        /// the part of the inherited docs it asks for: its path, or else the matching section's
        /// content. Returns how many there were.
        /// </summary>
        public int ExpandNestedInheritDoc(string inheritedXml)
        {
            int count = 0;

            void Walk(DocNode node, List<string> enclosing)
            {
                for (int i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    if (child.IsText)
                    {
                        continue;
                    }

                    if (child.Name == "inheritdoc")
                    {
                        string selected = InheritDocPath.Select(inheritedXml, child.Attr("path"), enclosing);
                        var replacement = selected == null ? new List<DocNode>() : LenientXmlParser.Parse(selected).Children;
                        node.Children.RemoveAt(i);
                        node.Children.InsertRange(i, replacement);
                        i += replacement.Count - 1;
                        count++;
                        continue;
                    }

                    enclosing.Add(InheritDocPath.Step(child.Name, child.Attr("name")));
                    Walk(child, enclosing);
                    enclosing.RemoveAt(enclosing.Count - 1);
                }
            }

            void Section(DocNode body, string step)
            {
                if (body != null) Walk(body, new List<string> { step });
            }

            Section(Summary, "summary");
            Section(Remarks, "remarks");
            Section(Returns, "returns");
            Section(Value, "value");
            Section(Example, "example");
            foreach (var p in TypeParams) Section(p.Body, InheritDocPath.Step("typeparam", p.Name));
            foreach (var p in Params) Section(p.Body, InheritDocPath.Step("param", p.Name));
            foreach (var e in Exceptions) Section(e.Body, "exception");
            foreach (var p in Permissions) Section(p.Body, "permission");
            foreach (var c in Custom) Section(c.Body, c.Name);
            return count;
        }

        private static void AddMissing(List<NamedSection> own, List<NamedSection> inherited, Func<NamedSection, string> key)
        {
            var present = new HashSet<string>(own.Select(key));
            own.AddRange(inherited.Where(s => present.Add(key(s))));
        }

        private static string SeeAlsoKey(DocNode node) =>
            node.Attr("cref") != null ? CrefFormatter.Format(node.Attr("cref")).ToString() : node.Attr("href") ?? string.Empty;

        public static DocModel From(DocNode root)
        {
            var model = new DocModel();
            DocNode loose = null;

            // Documentation from Roslyn (inherited docs) comes wrapped in <member name="...">.
            var elements = root.Children.Where(c => !c.IsText || !string.IsNullOrWhiteSpace(c.Text)).ToList();
            if (elements.Count == 1 && (elements[0].Name == "member" || elements[0].Name == "doc"))
            {
                root = elements[0];
            }

            foreach (var child in root.Children)
            {
                switch (child.Name)
                {
                    case null:
                        if (!string.IsNullOrWhiteSpace(child.Text)) (loose ??= new DocNode { Name = "#loose" }).Children.Add(child);
                        break;
                    case "summary": model.Summary = Merge(model.Summary, child); break;
                    case "remarks": model.Remarks = Merge(model.Remarks, child); break;
                    case "returns": model.Returns = Merge(model.Returns, child); break;
                    case "value": model.Value = Merge(model.Value, child); break;
                    case "example": model.Example = Merge(model.Example, child); break;
                    case "inheritdoc": model.InheritDoc = child; break;
                    case "param": model.Params.Add(new NamedSection(child.Attr("name") ?? "?", child)); break;
                    case "typeparam": model.TypeParams.Add(new NamedSection(child.Attr("name") ?? "?", child)); break;
                    case "exception": model.Exceptions.Add(new NamedSection(child.Attr("cref") ?? string.Empty, child)); break;
                    case "seealso": model.SeeAlso.Add(child); break;
                    case "permission": model.Permissions.Add(new NamedSection(child.Attr("cref") ?? string.Empty, child)); break;
                    case "threadsafety": model.ThreadSafety = child; break;
                    case "include": model.Includes.Add(child); break;
                    default:
                        if (IgnoredTags.Contains(child.Name))
                        {
                            break;
                        }

                        if (TextTags.Contains(child.Name))
                        {
                            // Content outside <summary>, e.g. "/// Does a <b>thing</b>." with no section tags.
                            (loose ??= new DocNode { Name = "#loose" }).Children.Add(child);
                        }
                        else
                        {
                            model.Custom.Add(new NamedSection(child.Name, child));
                        }

                        break;
                }
            }

            if (loose != null)
            {
                model.Summary = Merge(model.Summary, loose);
            }

            return model;
        }

        private static DocNode Merge(DocNode existing, DocNode added)
        {
            if (existing == null)
            {
                return added;
            }

            var merged = new DocNode { Name = existing.Name };
            merged.Children.AddRange(existing.Children);
            merged.Children.Add(new DocNode { Name = "para" });
            merged.Children.AddRange(added.Children);
            return merged;
        }
    }
}
