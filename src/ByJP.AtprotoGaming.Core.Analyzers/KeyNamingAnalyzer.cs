using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ByJP.AtprotoGaming.Core.Analyzers
{
    /// <summary>
    /// Flags string-literal keys passed to <c>PlayUpdate</c>'s open-ended setters:
    /// a camelCase suggestion (BAG001) for keys that aren't camelCase, and an error
    /// (BAG002) for progress keys that have a dedicated helper (<c>outcome</c>,
    /// <c>route</c>).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class KeyNamingAnalyzer : DiagnosticAnalyzer
    {
        public const string CamelCaseId = "BAG001";
        public const string ReservedKeyId = "BAG002";

        private const string PlayUpdateType = "ByJP.AtprotoGaming.Core.PlayUpdate";

        private static readonly DiagnosticDescriptor CamelCaseRule = new DiagnosticDescriptor(
            CamelCaseId,
            "Record key should be camelCase",
            "atproto record keys are conventionally camelCase; \"{0}\" is not",
            "Naming",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "Open-ended progress/settings keys are stored as JSON field names, which atproto conventionally writes in camelCase.");

        private static readonly DiagnosticDescriptor ReservedKeyRule = new DiagnosticDescriptor(
            ReservedKeyId,
            "Use the dedicated helper for this progress key",
            "use {0} instead of {1}(\"{2}\")",
            "Usage",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "progress.outcome and progress.route are structured and have dedicated helpers.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(CamelCaseRule, ReservedKeyRule);

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

            var isProgress = method.Name == "SetProgress" || method.Name == "IncrementProgress";
            var isSetting = method.Name == "SetSetting";
            if (!isProgress && !isSetting) return;

            IArgumentOperation? nameArg = null;
            foreach (var arg in invocation.Arguments)
            {
                if (arg.Parameter?.Name == "name") { nameArg = arg; break; }
            }
            if (nameArg == null) return;

            var constant = nameArg.Value.ConstantValue;
            if (!constant.HasValue || !(constant.Value is string key) || key.Length == 0) return;

            var location = nameArg.Value.Syntax.GetLocation();

            if (isProgress && (key == "outcome" || key == "route"))
            {
                var helper = key == "outcome" ? "SetOutcome(...)" : "AddRouteStop(...)";
                context.ReportDiagnostic(Diagnostic.Create(ReservedKeyRule, location, helper, method.Name, key));
                return; // a reserved key is already an error; don't also nag about casing
            }

            if (!IsCamelCase(key))
                context.ReportDiagnostic(Diagnostic.Create(CamelCaseRule, location, key));
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
