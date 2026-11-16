using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace RenderedDocComments.Parsing
{
    /// <summary>
    /// Picks the part of inherited documentation that an &lt;inheritdoc/&gt; asks for, the way the
    /// C# tools do:
    /// <list type="bullet">
    /// <item><description><c>path</c> is an XPath over the inherited docs; one starting with "/" is
    /// relative to their root element (&lt;member&gt; or &lt;doc&gt;).</description></item>
    /// <item><description>Without a path, a top-level &lt;inheritdoc/&gt; takes everything (except
    /// &lt;overloads&gt;), and one inside a section takes the content of the matching section,
    /// e.g. the inherited summary's content for one inside &lt;summary&gt;, or parameter
    /// <c>x</c>'s for one inside &lt;param name="x"&gt;.</description></item>
    /// </list>
    /// </summary>
    internal static class InheritDocPath
    {
        private const string Everything = "/*/node()[not(self::overloads)]";

        /// <param name="inheritedXml">The inherited docs' content (without a root element).</param>
        /// <param name="path">The &lt;inheritdoc&gt;'s path attribute, or null.</param>
        /// <param name="enclosing">
        /// For an &lt;inheritdoc/&gt; inside a section: the elements around it, outermost first
        /// (e.g. "summary", or "param[@name='x']"). Empty or null for a top-level one.
        /// </param>
        /// <returns>The selected XML, or null when the path is invalid.</returns>
        public static string Select(string inheritedXml, string path, IReadOnlyList<string> enclosing = null)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse("<member>" + inheritedXml + "</member>", LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return null;
            }

            string xpath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                xpath = path.Trim();
                if (xpath.StartsWith("/", StringComparison.Ordinal))
                {
                    xpath = "/*" + xpath; // Account for the root <member>/<doc>.
                }
            }
            else if (enclosing == null || enclosing.Count == 0)
            {
                xpath = Everything;
            }
            else
            {
                xpath = "/*/" + string.Join("/", enclosing) + "/node()[not(self::overloads)]";
            }

            object result;
            try
            {
                result = document.XPathEvaluate(xpath);
            }
            catch (XPathException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null; // E.g. an undefined XPath function or variable.
            }

            if (!(result is IEnumerable items) || result is string)
            {
                return Escape(Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture));
            }

            var text = new StringBuilder();
            foreach (var item in items)
            {
                switch (item)
                {
                    case XNode node:
                        text.Append(node.ToString(SaveOptions.DisableFormatting));
                        break;
                    case XAttribute attribute:
                        text.Append(Escape(attribute.Value));
                        break;
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// The step that selects an element like this one in the inherited docs: its name, plus
        /// the parameter name for &lt;param&gt; and &lt;typeparam&gt; so the right one is picked.
        /// </summary>
        public static string Step(string elementName, string nameAttribute)
        {
            if ((elementName == "param" || elementName == "typeparam") && !string.IsNullOrEmpty(nameAttribute) && !nameAttribute.Contains("'"))
            {
                return elementName + "[@name='" + nameAttribute + "']";
            }

            return elementName;
        }

        private static string Escape(string text) =>
            text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
