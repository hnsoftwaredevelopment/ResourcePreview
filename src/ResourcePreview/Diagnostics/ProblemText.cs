using ResourcePreview.Localization;
using ResourcePreview.Resources;

namespace ResourcePreview.Diagnostics
{
    /// <summary>The text of a reference problem, shared by the Error List and the tooltip.</summary>
    internal static class ProblemText
    {
        public static string Describe(ResourceIndex index, ReferenceProblem problem)
        {
            string key = problem.Use.Key;
            switch (problem.Kind)
            {
                case ReferenceProblemKind.DefinedLater:
                    return Localizer.Format(Strings.DefinedLaterFormat, key, problem.Definition.LineNumber);

                case ReferenceProblemKind.NotLoaded:
                    return Localizer.Format(Strings.NotLoadedFormat, key, ReferenceChecker.Relative(index, problem.Definition.FilePath));

                default:
                    string text = Localizer.Format(problem.Use.IsStatic ? Strings.MissingStaticKeyFormat : Strings.MissingDynamicKeyFormat, key);
                    return problem.Suggestion == null ? text : text + " " + Localizer.Format(Strings.DidYouMeanFormat, problem.Suggestion);
            }
        }
    }
}
