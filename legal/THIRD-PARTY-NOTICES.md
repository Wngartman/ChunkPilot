# Third-party notices

This source-tree notice records project-specific attributions. Release packaging also generates
`THIRD-PARTY-NOTICES.txt` from the exact locked npm runtime graph and restored NuGet graph, including
the license files shipped by those packages. The validated SPDX SBOM is published beside each release.

ChunkPilot's runtime dependencies include CommunityToolkit.Mvvm, FluentIcons.Wpf, Microsoft.Data.Sqlite,
Microsoft.Extensions, Microsoft.Web.WebView2, SQLitePCLRaw, System.Security.Cryptography.ProtectedData,
SixLabors.ImageSharp, React, React DOM, Zustand, TanStack Virtual, Lucide React, Radix UI, and Inter.
Exact versions are pinned in `Directory.Packages.props` and `src/ChunkPilot.WebUi/package-lock.json`.

## FluentIcons.Wpf 2.1.333

ChunkPilot uses FluentIcons.Wpf, licensed under the MIT License. The package provides Fluent UI system icons for WPF and is distributed by its copyright holders. Source and license information are available from the package repository and NuGet metadata.

MIT License

Copyright (c) FluentIcons contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
