# Third-party notices

This file is generated from `docs/windows-launcher/about-content.v1.json`. Do not edit it directly.

STFC Mod Bridge is distributed under the repository license. The components below retain their own terms.

## Coverage and open review

Automated coverage classifies every resolved runtime-bearing NuGet package, including the managed-only SQLite provider closure, self-contained runtime-pack input, explicit project resource/content/embed/icon/manifest input, the locked Go toolchain, all 71 checksum-locked release-verifier modules, and the independent Profiles and offline TOML native source pins plus exact JSON/libarchive/liblzma/zlib/toml++ recipe and license inventory. licenses.v1.json is digest-bound to dependencies.v1.txt and CI rejects unclassified graph drift. Complete component-level notices for the self-contained .NET runtime and final artwork provenance remain review-pending under issue #30. This engineering inventory does not claim legal completeness.

## FluentIcons.Wpf and FluentIcons.Common

- Version: 2.1.333
- License: MIT License
- Source: https://github.com/davidxuang/FluentIcons
- Authoritative license information: https://github.com/davidxuang/FluentIcons/blob/master/LICENSE

```text
MIT License

Copyright (c) 2022 davidxuang

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Microsoft Fluent UI System Icons

- Version: upstream assets included by FluentIcons 2.1.333
- License: MIT License
- Source: https://github.com/microsoft/fluentui-system-icons
- Authoritative license information: https://github.com/microsoft/fluentui-system-icons/blob/main/LICENSE

```text
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## .NET 8 Windows Desktop Runtime

- Version: 8.0 (resolved at build time)
- License: .NET Library License on Windows; component notices also apply
- Source: https://github.com/dotnet/runtime
- Authoritative license information: https://github.com/dotnet/core/blob/main/license-information.md

```text
STFC Mod Bridge is published as a self-contained Windows application and therefore redistributes .NET runtime and Windows Desktop Runtime components. Microsoft documents Windows .NET product distributions under the .NET Library License and directs distributors to the applicable runtime third-party notices. The linked Microsoft license-information page and the notices shipped with the resolved .NET runtime are authoritative; this summary is not a replacement for those terms.
```

## Windows SDK for .NET targeting pack

- Version: 10.0.19041.56
- License: Microsoft Windows SDK license
- Source: https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.19041.56
- Authoritative license information: https://aka.ms/WinSDKLicenseURL

```text
Microsoft.Windows.SDK.NET.Ref supplies the Windows Runtime projections used for packaged update discovery. Its NuGet metadata requires acceptance of the Microsoft Windows SDK license; the linked Microsoft license is authoritative.
```

## Microsoft.Data.Sqlite.Core

- Version: 8.0.29
- License: MIT License
- Source: https://github.com/dotnet/efcore/tree/v8.0.29/src/Microsoft.Data.Sqlite.Core
- Authoritative license information: https://github.com/dotnet/efcore/blob/v8.0.29/LICENSE.txt

```text
Microsoft.Data.Sqlite.Core is part of the .NET Entity Framework Core repository and is licensed under the MIT License. The linked upstream license is authoritative.
```

## SQLitePCLRaw core and dynamic C-declaration provider

- Version: 2.1.11
- License: Apache License 2.0
- Source: https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.11
- Authoritative license information: https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.11/LICENSE.TXT

```text
SQLitePCLRaw.core and SQLitePCLRaw.provider.dynamic_cdecl are licensed under the Apache License, Version 2.0. The provider binds the Windows-serviced winsqlite3 module; this application does not redistribute native SQLite bytes.
```

## The Go Programming Language runtime

- Version: 1.26.6
- License: BSD 3-Clause License
- Source: https://go.dev/
- Authoritative license information: https://go.dev/LICENSE

```text
Copyright 2009 The Go Authors. All rights reserved. Redistribution and use in source and binary forms, with or without modification, are permitted subject to the conditions in the authoritative Go license. Neither the name of Google LLC nor the names of its contributors may be used to endorse or promote derived products without specific prior written permission. The software is provided without warranty; see the linked license for the complete terms.
```

## sigstore-go

- Version: 1.3.0
- License: Apache License 2.0
- Source: https://github.com/sigstore/sigstore-go
- Authoritative license information: https://github.com/sigstore/sigstore-go/blob/v1.3.0/LICENSE

```text
Copyright 2023 The Sigstore Authors. Licensed under the Apache License, Version 2.0. You may obtain a copy at https://www.apache.org/licenses/LICENSE-2.0. The software is distributed on an AS IS basis, without warranties or conditions of any kind. The linked upstream license contains the complete terms.
```

## STFC Mod Bridge Release Verifier module graph

- Version: 71 exact modules in dependencies.v1.txt
- License: Apache-2.0, MIT, BSD-2-Clause, BSD-3-Clause, and dual MIT/Apache-2.0 components
- Source: https://github.com/Guffawaffle/stfc-mod-bridge/blob/main/src/STFCModBridge.ReleaseVerifier/dependencies.v1.txt
- Authoritative license information: https://github.com/Guffawaffle/stfc-mod-bridge/blob/main/src/STFCModBridge.ReleaseVerifier/licenses.v1.json

```text
The closed release verifier compiles a checksum-locked 71-module Go graph. licenses.v1.json classifies every exact module under an SPDX expression and is cryptographically bound to dependencies.v1.txt; CI rejects graph, version, checksum, or classification drift. Each module retains its own copyright and authoritative license terms. This engineering inventory supports review and is not a legal-clearance claim.
```

## STFC Profiles shared native component

- Version: immutable source pin in dependencies/stfc-profiles-source-pin.json
- License: GNU General Public License v3.0
- Source: https://github.com/Guffawaffle/stfc-profiles
- Authoritative license information: https://github.com/Guffawaffle/stfc-profiles/blob/main/LICENSE

```text
STFC Profiles is distributed under the GNU General Public License, version 3. Its source provenance and notices are retained in the pinned repository. The complete GPLv3 license is included in this repository LICENSE, the ZIP/MSIX LICENSE.txt payload and with the native build evidence.
```

## JSON for Modern C++

- Version: 3.12.0
- License: MIT License
- Source: https://github.com/nlohmann/json/tree/v3.12.0
- Authoritative license information: https://github.com/nlohmann/json/blob/v3.12.0/LICENSE.MIT

```text
MIT License

Copyright (c) 2013-2025 Niels Lohmann

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

```

## libarchive

- Version: 3.8.9
- License: BSD-2-Clause AND BSD-3-Clause AND CC0-1.0
- Source: https://github.com/libarchive/libarchive/tree/v3.8.9
- Authoritative license information: https://github.com/libarchive/libarchive/blob/v3.8.9/COPYING

```text
The pinned static archive library includes its upstream BSD license notices and UC Regents compression-source notice below. CC0-1.0 is selected for its triple-licensed BLAKE2 code. Individual source headers remain authoritative.

The libarchive distribution as a whole is Copyright by Tim Kientzle
and is subject to the copyright notice reproduced at the bottom of
this file.

Each individual file in this distribution should have a clear
copyright/licensing statement at the beginning of the file.  If any do
not, please let me know and I will rectify it.  The following is
intended to summarize the copyright status of the individual files;
the actual statements in the files are controlling.

* Except as listed below, all C sources (including .c and .h files)
  and documentation files are subject to the copyright notice reproduced
  at the bottom of this file.

* The following source files are also subject in whole or in part to
  a 3-clause UC Regents copyright; please read the individual source
  files for details:
   libarchive/archive_read_support_filter_compress.c
   libarchive/archive_write_add_filter_compress.c
   libarchive/mtree.5

* The following source files are in the public domain:
   libarchive/archive_parse_date.c

* The following source files are triple-licensed with the ability to choose
  from CC0 1.0 Universal, OpenSSL or Apache 2.0 licenses:
   libarchive/archive_blake2.h
   libarchive/archive_blake2_impl.h
   libarchive/archive_blake2s_ref.c
   libarchive/archive_blake2sp_ref.c

* The build files---including Makefiles, configure scripts,
  and auxiliary scripts used as part of the compile process---have
  widely varying licensing terms.  Please check individual files before
  distributing them to see if those restrictions apply to you.

I intend for all new source code to use the license below and hope over
time to replace code with other licenses with new implementations that
do use the license below.  The varying licensing of the build scripts
seems to be an unavoidable mess.


Copyright (c) 2003-2018 <author(s)>
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer
   in this position and unchanged.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR(S) ``AS IS'' AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
IN NO EVENT SHALL THE AUTHOR(S) BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

Compression source attribution and license:

Copyright (c) 2003-2007 Tim Kientzle
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR(S) ``AS IS'' AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
IN NO EVENT SHALL THE AUTHOR(S) BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

This code borrows heavily from "compress" source code, which is
protected by the following copyright.  (Clause 3 dropped by request
of the Regents.)

Copyright (c) 1985, 1986, 1992, 1993
	The Regents of the University of California.  All rights reserved.

This code is derived from software contributed to Berkeley by
Diomidis Spinellis and James A. Woods, derived from original
work by Spencer Thomas and Joseph Orost.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.
4. Neither the name of the University nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE REGENTS AND CONTRIBUTORS ``AS IS'' AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED.  IN NO EVENT SHALL THE REGENTS OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
SUCH DAMAGE.


```

## XZ Utils liblzma

- Version: 5.8.3
- License: 0BSD (linked liblzma library)
- Source: https://github.com/tukaani-project/xz/tree/v5.8.3
- Authoritative license information: https://github.com/tukaani-project/xz/blob/v5.8.3/COPYING.0BSD

```text
Copyright (C) The XZ Utils authors and contributors. Only the static liblzma library is linked. The official XZ 5.8.3 COPYING identifies liblzma as 0BSD; the separate XZ command-line tools and scripts are not redistributed.

Permission to use, copy, modify, and/or distribute this
software for any purpose with or without fee is hereby granted.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL
WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL
THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR
CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT,
NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN
CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

```

## zlib

- Version: 1.3.2
- License: Zlib
- Source: https://github.com/madler/zlib/tree/v1.3.2
- Authoritative license information: https://github.com/madler/zlib/blob/v1.3.2/LICENSE

```text
Copyright notice:

 (C) 1995-2026 Jean-loup Gailly and Mark Adler

  This software is provided 'as-is', without any express or implied
  warranty.  In no event will the authors be held liable for any damages
  arising from the use of this software.

  Permission is granted to anyone to use this software for any purpose,
  including commercial applications, and to alter it and redistribute it
  freely, subject to the following restrictions:

  1. The origin of this software must not be misrepresented; you must not
     claim that you wrote the original software. If you use this software
     in a product, an acknowledgment in the product documentation would be
     appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be
     misrepresented as being the original software.
  3. This notice may not be removed or altered from any source distribution.

  Jean-loup Gailly        Mark Adler
  jloup@gzip.org          madler@alumni.caltech.edu

```

## STFC TOML shared native component

- Version: immutable source pin in dependencies/stfc-toml-source-pin.json
- License: GNU General Public License v3.0
- Source: https://github.com/Guffawaffle/stfc-mod
- Authoritative license information: https://github.com/Guffawaffle/stfc-mod/blob/play/LICENSE

```text
The offline STFC TOML editor is produced from the pinned shared/toml component of the STFC community mod source. Its exact source archive and dependency recipe are recorded with the build evidence. The complete GPLv3 license is included in this repository LICENSE, the ZIP/MSIX LICENSE.txt payload and with the native build evidence.
```

## toml++

- Version: 3.4.0
- License: MIT License
- Source: https://github.com/marzer/tomlplusplus/tree/v3.4.0
- Authoritative license information: https://github.com/marzer/tomlplusplus/blob/v3.4.0/LICENSE

```text
MIT License

Copyright (c) Mark Gillard <mark.gillard@outlook.com.au>

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Attribution review boundary

Attribution and non-endorsement copy is a factual compatibility statement, not a claim of legal clearance. Final wording and asset usage remain subject to the v1 release review tracked in issue #30.
