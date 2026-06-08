using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class AnalyzerTests
{
    // Wrap a statement body in a method that takes a PlayUpdate (its ctor is
    // internal, so we can't construct one in the snippet) and run the analyzer.
    private static Task VerifyAsync(string body, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<KeyNamingAnalyzer, DefaultVerifier>
        {
            TestCode = "using ByJP.AtprotoGaming.Core;\nclass C { void M(PlayUpdate tx) {\n" + body + "\n} }",
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.TestState.AdditionalReferences.Add(
            MetadataReference.CreateFromFile(typeof(PlayUpdate).Assembly.Location));
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Fact]
    public Task CamelCaseKeyIsClean() =>
        VerifyAsync("tx.SetProgress(\"hpMax\", 1);");

    [Fact]
    public Task NonCamelCaseProgressKeyIsFlagged() =>
        VerifyAsync("tx.SetProgress({|BAG001:\"HpMax\"|}, 1);");

    [Fact]
    public Task NonCamelCaseSettingKeyIsFlagged() =>
        VerifyAsync("tx.SetSetting({|BAG001:\"Difficulty\"|}, 1);");

    [Fact]
    public Task ReservedProgressKeyIsAnError() =>
        VerifyAsync("tx.SetProgress({|BAG002:\"outcome\"|}, 1);");

    [Fact]
    public Task ReservedKeyOnIncrementIsAnError() =>
        VerifyAsync("tx.IncrementProgress({|BAG002:\"route\"|}, 1);");

    [Fact]
    public Task NonLiteralKeyIsIgnored() =>
        VerifyAsync("string k = \"HpMax\"; tx.SetProgress(k, 1);");

    [Theory]
    [InlineData("hpMax", true)]
    [InlineData("score", true)]
    [InlineData("x2", true)]
    [InlineData("HpMax", false)]
    [InlineData("hp_max", false)]
    [InlineData("hp.max", false)]
    [InlineData("", false)]
    public void IsCamelCaseClassifiesKeys(string value, bool expected) =>
        Assert.Equal(expected, KeyNamingAnalyzer.IsCamelCase(value));
}
