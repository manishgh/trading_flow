# Tooling Choices

## Recommended Editor Setup

Use **Visual Studio** for C# debugging and solution/project management. It is the strongest choice for .NET once the project grows into multiple libraries, tests, and background workers.

Use **Codex** for implementation, refactoring, test generation, architecture documentation, and repetitive code changes.

Use **Cursor** only if you like its editor experience, but avoid having Cursor and Codex make overlapping edits at the same time. One active coding agent at a time keeps the project easier to reason about.

## Practical Workflow

1. Discuss or define the next milestone with Codex.
2. Let Codex make scoped changes.
3. Run tests/builds locally.
4. Open the project in Visual Studio for debugging and inspection.
5. Commit only when you are happy with the scope.

## Required Local Tools

- .NET SDK 8 or newer.
- Go 1.22 or newer.
- Git.
- Optional: Visual Studio 2022, JetBrains Rider, or Cursor.

Current machine check found the .NET runtime, but not the .NET SDK, and `go`/`git` were not available on PATH.

