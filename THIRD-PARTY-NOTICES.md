# Third-party notices

This file is generated from `docs/windows-launcher/about-content.v1.json`. Do not edit it directly.

STFC Mod Bridge is distributed under the repository license. The components below retain their own terms.

## Coverage and open review

Automated coverage classifies every resolved runtime-bearing NuGet package, self-contained runtime-pack input, explicit project resource/content/embed/icon/manifest input, the locked Go toolchain, and the direct sigstore-go module. The verifier's 71-module compiled graph is checksum-locked in dependencies.v1.txt; complete transitive module license classification remains review-pending under issue #97, while complete component-level notices for the self-contained .NET runtime and final artwork provenance remain review-pending under issue #30. This inventory does not claim legal completeness.

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

## The Go Programming Language runtime

- Version: 1.26.5
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

## Attribution review boundary

Attribution and non-endorsement copy is a factual compatibility statement, not a claim of legal clearance. Final wording and asset usage remain subject to the v1 release review tracked in issue #30.
