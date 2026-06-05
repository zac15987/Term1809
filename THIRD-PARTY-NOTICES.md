# Third-Party Notices

## ConPTY Sample (Term1809/Terminal/Internals/)

The code under `Term1809/Terminal/Internals/` is a clean-room re-implementation
inspired by the Microsoft Windows Terminal ConPTY sample, originally published in the
[`microsoft/terminal`](https://github.com/microsoft/terminal) repository. The original
sample is licensed under the MIT License, Copyright (c) Microsoft Corporation.

This project's `Internals/` code was written independently, using the public ConPTY
Win32 API surface documented by Microsoft. No source files were copied or translated
from the original repository.

Original MIT License notice for the Microsoft Windows Terminal ConPTY sample:

```
MIT License

Copyright (c) Microsoft Corporation

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

## Third-Party Packages

The following NuGet packages are used by this project. They are referenced as package
dependencies and are not vendored into this repository. All are distributed under the
MIT License and are used unmodified.

### CI.Microsoft.Terminal.Wpf

- **License**: MIT License
- **Copyright**: Copyright (c) Microsoft Corporation
- **Description**: A community repackaging of Microsoft's Terminal.Wpf control, which
  provides the GPU-accelerated WPF terminal rendering surface backed by the Windows
  Terminal rendering engine.

### CI.Microsoft.Windows.Console.ConPTY

- **License**: MIT License
- **Copyright**: Copyright (c) Microsoft Corporation
- **Description**: A community repackaging of Microsoft's ConPTY / Windows Console API
  native binaries, providing the `conpty.dll` and associated headers used to create
  pseudo-console sessions on Windows.

### HandyControl

- **License**: MIT License
- **Copyright**: Copyright (c) HandyOrg / NetEase contributors
- **Description**: A WPF UI component library providing extended controls and themes.
  Used for the application shell UI (tabs, window chrome, styling).
  Repository: [https://github.com/HandyOrg/HandyControl](https://github.com/HandyOrg/HandyControl)
