# Third-Party Notices

## BASS audio libraries

Midora uses the BASS, BASSMIDI, and BASSWASAPI audio libraries supplied by Un4seen Developments.

These libraries are not open-source components of Midora and are not licensed under Midora's MIT source-code license. Their use and redistribution are governed by the terms supplied by Un4seen Developments:

- <https://www.un4seen.com/bass.html>

Midora's initial release is intended to be free, open-source, and non-commercial. Un4seen's published free-use conditions additionally depend on the actual distributing entity being non-commercial and on the product not generating money through sales, advertising, or similar means. Every distributor must verify the terms applicable to its own identity, platform, revenue model, and distribution method.

Commercial users, commercial forks, and any distributor whose eligibility is unclear must contact the rights holder or obtain an appropriate licence before distributing or using the BASS libraries commercially.

The Midora source repository does not contain BASS binary files. Official binaries used for a release must be supplied separately by the release operator and pass Midora's pinned version and SHA-256 verification.

## Fluent System Icons

Midora contains WPF Geometry conversions of selected vector paths from Microsoft's Fluent System Icons repository.

- Upstream: <https://github.com/microsoft/fluentui-system-icons>
- Revision: `0a92ff83f03fa5319edaf0e2b2a09e460b69091a`
- Copyright (c) 2020 Microsoft Corporation
- License: MIT

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Sora

Midora embeds the Light, Regular, SemiBold, and Bold static TrueType fonts from Sora for its general user interface.

- Upstream: <https://github.com/sora-xor/sora-font>
- Revision: `7f9a9c5d0ccd1c099cfac420aa27133df1c5fdc4`
- Copyright 2019 The Sora Project Authors
- License: SIL Open Font License, Version 1.1
- Bundled license: `assets/fonts/Sora/OFL.txt`

## JetBrains Mono

Midora embeds the Regular, SemiBold, and Bold static TrueType fonts from JetBrains Mono for code and other monospaced text.

- Upstream: <https://github.com/JetBrains/JetBrainsMono>
- Release: `v2.304`
- Revision: `cd5227bd1f61dff3bbd6c814ceaf7ffd95e947d9`
- Copyright 2020 The JetBrains Mono Project Authors
- License: SIL Open Font License, Version 1.1
- Bundled license: `assets/fonts/JetBrainsMono/OFL.txt`

## AvalonEdit

Midora uses AvalonEdit for the Batch Edit expression code editor.

- Upstream: <https://github.com/icsharpcode/AvalonEdit>
- Package: `AvalonEdit` `6.3.1.120`
- Revision: `862415d51eddc9eac93f462dbc522ffbf929cd52`
- Copyright 2000-2025 AlphaSierraPapa for the SharpDevelop Team
- License: MIT

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Microsoft.CodeAnalysis (Roslyn)

Midora uses the Roslyn C# compiler APIs for expression parsing, validation, completion, and the local Batch Edit tool.

- Upstream: <https://github.com/dotnet/roslyn>
- Package: `Microsoft.CodeAnalysis.CSharp` `5.3.0`
- Revision: `16f9bd284cd49604ac82998bfe778a8eb16d4347`
- Copyright (c) .NET Foundation and Contributors
- License: MIT
- Exact package notice distributed as `licenses/Roslyn-ThirdPartyNotices.rtf`

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Google.Protobuf

Midora uses the Google.Protobuf C# runtime for versioned Project source objects.

- Upstream: <https://github.com/protocolbuffers/protobuf>
- Package: `Google.Protobuf` `3.35.1`
- Revision: `35cd01f9fe9afbeea38cc7b979a3b6bfcde82c03`
- Copyright 2008 Google Inc. All rights reserved.
- License: BSD 3-Clause

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. Neither the name of Google Inc. nor the names of its contributors may be
   used to endorse or promote products derived from this software without
   specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.

## .NET 10 self-contained runtime

The self-contained `win-x64` Midora package contains Microsoft.NETCore.App,
Microsoft.WindowsDesktop.App, and framework assets resolved by the locked .NET
10 runtime packs. The local release script copies the exact license and
third-party-notice files from those resolved runtime-pack versions into
`licenses/dotnet/`; those files are part of the distributed notice set.

- Upstream: <https://github.com/dotnet/runtime> and <https://github.com/dotnet/wpf>
- License: MIT, with separately licensed third-party components enumerated in the bundled runtime notices
