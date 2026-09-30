using System.Runtime.CompilerServices;

// Lets the test project reach internal types without making them public.
// Kept separate from AssemblyInfo.cs because the versioning script rewrites that file.
[assembly: InternalsVisibleTo("ResourcePreview.Tests")]
