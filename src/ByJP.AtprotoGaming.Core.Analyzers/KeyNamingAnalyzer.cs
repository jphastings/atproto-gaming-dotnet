using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ByJP.AtprotoGaming.Core.Analyzers
{
    /// <summary>
    /// Flags string-literal <c>name</c> keys passed to <c>PlayUpdate</c>'s keyed
    /// state setters (<c>SetMetric</c>, <c>UpdateMetric</c>, <c>SetSetting</c>) with a
    /// camelCase suggestion (BAG001) when they aren't camelCase — the key becomes a
    /// state entry's <c>id</c>, which atproto conventionally writes in camelCase.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class KeyNamingAnalyzer : DiagnosticAnalyzer
    {
        public const string CamelCaseId = "BAG001";

        private const string PlayUpdateType = "ByJP.AtprotoGaming.Core.PlayUpdate";

        private static readonly DiagnosticDescriptor CamelCaseRule = new DiagnosticDescriptor(
            CamelCaseId,
            "Record key should be camelCase",
            "atproto record keys are conventionally camelCase; \"{0}\" is not",
            "Naming",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "Metric and setting keys are stored as a state entry's id, which atproto conventionally writes in camelCase.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(CamelCaseRule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var method = invocation.TargetMethod;
            if (method.ContainingType?.ToDisplayString() != PlayUpdateType) return;

            if (method.Name != "SetMetric" && method.Name != "UpdateMetric" && method.Name != "SetSetting")
                return;

            IArgumentOperation? nameArg = null;
            foreach (var arg in invocation.Arguments)
            {
                if (arg.Parameter?.Name == "name") { nameArg = arg; break; }
            }
            if (nameArg == null) return;

            var constant = nameArg.Value.ConstantValue;
            if (!constant.HasValue || !(constant.Value is string key) || key.Length == 0) return;

            if (!IsCamelCase(key))
                context.ReportDiagnostic(Diagnostic.Create(CamelCaseRule, nameArg.Value.Syntax.GetLocation(), key));
        }

        /// <summary>True for a lowercase-first, letters-and-digits-only identifier (no separators).</summary>
        internal static bool IsCamelCase(string s)
        {
            if (s.Length == 0) return false;
            if (s[0] < 'a' || s[0] > 'z') return false;
            foreach (var c in s)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!ok) return false;
            }
            return true;
        }
    }
}
