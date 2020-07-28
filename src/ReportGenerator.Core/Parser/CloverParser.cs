using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Palmmedia.ReportGenerator.Core.Common;
using Palmmedia.ReportGenerator.Core.Logging;
using Palmmedia.ReportGenerator.Core.Parser.Analysis;
using Palmmedia.ReportGenerator.Core.Parser.Filtering;
using Palmmedia.ReportGenerator.Core.Properties;

namespace Palmmedia.ReportGenerator.Core.Parser
{
    /// <summary>
    /// Parser for XML reports generated by Clover.
    /// </summary>
    internal class CloverParser : ParserBase
    {
        /// <summary>
        /// The Logger.
        /// </summary>
        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(CloverParser));

        /// <summary>
        /// Initializes a new instance of the <see cref="CloverParser" /> class.
        /// </summary>
        /// <param name="assemblyFilter">The assembly filter.</param>
        /// <param name="classFilter">The class filter.</param>
        /// <param name="fileFilter">The file filter.</param>
        internal CloverParser(IFilter assemblyFilter, IFilter classFilter, IFilter fileFilter)
            : base(assemblyFilter, classFilter, fileFilter)
        {
        }

        /// <summary>
        /// Parses the given XML report.
        /// </summary>
        /// <param name="report">The XML report.</param>
        /// <returns>The parser result.</returns>
        public ParserResult Parse(XContainer report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var assemblies = new List<Assembly>();

            var modules = report.Descendants("package")
                .Where(p => p.Attribute("name") != null)
                .ToArray();

            if (modules.Length == 0)
            {
                modules = report.Descendants("project")
                    .Where(p => p.Attribute("name") != null)
                    .ToArray();
            }

            var assemblyNames = modules
                .Select(m => m.Attribute("name").Value)
                .Distinct()
                .Where(a => this.AssemblyFilter.IsElementIncludedInReport(a))
                .OrderBy(a => a)
                .ToArray();

            foreach (var assemblyName in assemblyNames)
            {
                assemblies.Add(this.ProcessAssembly(modules, assemblyName));
            }

            var result = new ParserResult(assemblies.OrderBy(a => a.Name).ToList(), true, this.ToString());

            return result;
        }

        /// <summary>
        /// Processes the given assembly.
        /// </summary>
        /// <param name="modules">The modules.</param>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <returns>The <see cref="Assembly"/>.</returns>
        private Assembly ProcessAssembly(XElement[] modules, string assemblyName)
        {
            Logger.DebugFormat(Resources.CurrentAssembly, assemblyName);

            var files = modules
                .Where(m => m.Attribute("name").Value.Equals(assemblyName))
                .Elements("file")
                .Where(f => this.FileFilter.IsElementIncludedInReport(f.Attribute("name").Value))
                .OrderBy(f => f.Attribute("name").Value)
                .ToArray();

            var assembly = new Assembly(assemblyName);

            Parallel.ForEach(files, file => this.ProcessFile(assembly, file));

            return assembly;
        }

        /// <summary>
        /// Processes the file.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <param name="fileElement">The file element.</param>
        private void ProcessFile(Assembly assembly, XElement fileElement)
        {
            string className = fileElement.Attribute("name").Value;

            int indexOfJava = className.LastIndexOf(".java");

            if (indexOfJava > 0)
            {
                className = className.Substring(0, indexOfJava);
            }

            if (!this.ClassFilter.IsElementIncludedInReport(className))
            {
                return;
            }

            var @class = new Class(className, assembly);

            var lines = fileElement.Elements("line")
                .ToArray();

            var linesOfFile = lines
                .Where(line => line.Attribute("type").Value == "stmt")
                .Select(line => new
                {
                    LineNumber = int.Parse(line.Attribute("num").Value, CultureInfo.InvariantCulture),
                    Visits = line.Attribute("count").Value.ParseLargeInteger()
                })
                .OrderBy(seqpnt => seqpnt.LineNumber)
                .ToArray();

            var branches = GetBranches(lines);

            int[] coverage = new int[] { };
            LineVisitStatus[] lineVisitStatus = new LineVisitStatus[] { };

            if (linesOfFile.Length > 0)
            {
                coverage = new int[linesOfFile[linesOfFile.LongLength - 1].LineNumber + 1];
                lineVisitStatus = new LineVisitStatus[linesOfFile[linesOfFile.LongLength - 1].LineNumber + 1];

                for (int i = 0; i < coverage.Length; i++)
                {
                    coverage[i] = -1;
                }

                foreach (var line in linesOfFile)
                {
                    coverage[line.LineNumber] = line.Visits;

                    bool partiallyCovered = false;

                    ICollection<Branch> branchesOfLine = null;
                    if (branches.TryGetValue(line.LineNumber, out branchesOfLine))
                    {
                        partiallyCovered = branchesOfLine.Any(b => b.BranchVisits == 0);
                    }

                    LineVisitStatus statusOfLine = line.Visits > 0 ? (partiallyCovered ? LineVisitStatus.PartiallyCovered : LineVisitStatus.Covered) : LineVisitStatus.NotCovered;
                    lineVisitStatus[line.LineNumber] = statusOfLine;
                }
            }

            var methodsOfFile = lines
                .Where(line => line.Attribute("type").Value == "method")
                .ToArray();

            var codeFile = new CodeFile(fileElement.Attribute("path").Value, coverage, lineVisitStatus, branches);

            SetCodeElements(codeFile, methodsOfFile, coverage.Length - 1);

            @class.AddFile(codeFile);

            assembly.AddClass(@class);
        }

        /// <summary>
        /// Gets the branches by line number.
        /// </summary>
        /// <param name="lines">The lines.</param>
        /// <returns>The branches by line number.</returns>
        private static Dictionary<int, ICollection<Branch>> GetBranches(IEnumerable<XElement> lines)
        {
            var result = new Dictionary<int, ICollection<Branch>>();

            foreach (var line in lines)
            {
                if (line.Attribute("type").Value != "cond")
                {
                    continue;
                }

                int lineNumber = int.Parse(line.Attribute("num").Value, CultureInfo.InvariantCulture);

                int negativeBrancheCovered = int.Parse(line.Attribute("falsecount").Value, CultureInfo.InvariantCulture);
                int positiveBrancheCovered = int.Parse(line.Attribute("truecount").Value, CultureInfo.InvariantCulture);

                if (result.ContainsKey(lineNumber))
                {
                    var branches = result[lineNumber];

                    Branch negativeBranch = branches.First();
                    Branch positiveBranch = branches.ElementAt(1);

                    negativeBranch.BranchVisits = Math.Max(negativeBrancheCovered > 0 ? 1 : 0, negativeBranch.BranchVisits);
                    positiveBranch.BranchVisits = Math.Max(positiveBrancheCovered > 0 ? 1 : 0, positiveBranch.BranchVisits);
                }
                else
                {
                    string identifier1 = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}_{1}",
                        lineNumber,
                        "0");

                    string identifier2 = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}_{1}",
                            lineNumber,
                            "1");

                    var branches = new HashSet<Branch>();
                    branches.Add(new Branch(negativeBrancheCovered > 0 ? 1 : 0, identifier1));
                    branches.Add(new Branch(positiveBrancheCovered > 0 ? 1 : 0, identifier2));

                    result.Add(lineNumber, branches);
                }
            }

            return result;
        }

        /// <summary>
        /// Extracts the methods/properties of the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="methodsOfFile">The methods of the file.</param>
        /// <param name="numberOrLines">The number of lines in the file.</param>
        private static void SetCodeElements(CodeFile codeFile, IEnumerable<XElement> methodsOfFile, int numberOrLines)
        {
            var codeElements = new List<CodeElementBase>();

            foreach (var method in methodsOfFile)
            {
                var signature = method.Attribute("signature");

                if (signature == null)
                {
                    continue;
                }

                string methodName = signature.Value;
                int lineNumber = int.Parse(method.Attribute("num").Value, CultureInfo.InvariantCulture);

                codeElements.Add(new CodeElementBase(methodName, lineNumber));

                var complexity = method.Attribute("complexity");

                if (complexity != null)
                {
                    var metrics = new List<Metric>()
                    {
                        new Metric(
                            ReportResources.CyclomaticComplexity,
                            ParserBase.CyclomaticComplexityUri,
                            MetricType.CodeQuality,
                            decimal.Parse(complexity.Value, CultureInfo.InvariantCulture),
                            MetricMergeOrder.LowerIsBetter)
                    };

                    var methodMetric = new MethodMetric(methodName, methodName, metrics)
                    {
                        Line = lineNumber
                    };

                    codeFile.AddMethodMetric(methodMetric);
                }
            }

            for (int i = 0; i < codeElements.Count; i++)
            {
                var codeElement = codeElements[i];

                int lastLine = numberOrLines;
                if (i < codeElements.Count - 1)
                {
                    lastLine = codeElements[i + 1].FirstLine - 1;
                }

                codeFile.AddCodeElement(new CodeElement(
                    codeElement.Name,
                    CodeElementType.Method,
                    codeElement.FirstLine,
                    lastLine,
                    codeFile.CoverageQuota(codeElement.FirstLine, lastLine)));
            }
        }
    }
}
