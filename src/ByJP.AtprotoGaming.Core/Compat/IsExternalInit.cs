// netstandard2.0 doesn't ship the IsExternalInit modreq the compiler emits for
// `init` accessors and positional records. Providing it ourselves lets the rest
// of the package use modern syntax while still targeting ns2.0.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
