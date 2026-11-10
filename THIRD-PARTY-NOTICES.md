# Third-Party Notices

Remote Client 는 아래 오픈소스 구성요소를 포함하거나 사용합니다.

## xterm.js 5.5.0 / @xterm/addon-fit 0.10.0 — MIT

`RemoteClient/wwwroot/xterm.js`, `xterm.css`, `addon-fit.js` 에 번들됨.

```
Copyright (c) 2017-2019, The xterm.js authors (https://github.com/xtermjs/xterm.js)
Copyright (c) 2014-2016, SourceLair Private Company (https://www.sourcelair.com)
Copyright (c) 2012-2013, Christopher Jeffrey (https://github.com/chjj/)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## SSH.NET 2026.0.0 — MIT

https://github.com/sshnet/SSH.NET

```
The MIT License (MIT)

Copyright (c) Renci, Oleg Kapeljushnik, Gert Driesen and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Microsoft.Web.WebView2 SDK 1.0.4314.35

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors 
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## 클라우드 SDK

| 패키지 | 라이선스 | 출처 |
|---|---|---|
| AWSSDK.Core / AWSSDK.EC2 / AWSSDK.SSO / AWSSDK.SSOOIDC | Apache-2.0 | https://github.com/aws/aws-sdk-net |
| Azure.Identity, Azure.Core | MIT | https://github.com/Azure/azure-sdk-for-net |
| Microsoft.Identity.Client (MSAL, Azure.Identity 의존성) | MIT | https://github.com/AzureAD/microsoft-authentication-library-for-dotnet |
| Google.Apis.Auth, Google.Apis.Core | Apache-2.0 | https://github.com/googleapis/google-api-dotnet-client |
| Newtonsoft.Json (Google.Apis 의존성) | MIT | https://github.com/JamesNK/Newtonsoft.Json |

Apache-2.0 전문: https://www.apache.org/licenses/LICENSE-2.0

Tencent Cloud API 는 SDK 없이 공개된 TC3-HMAC-SHA256 서명 규격으로 직접 호출합니다.

## 시스템 구성요소 (재배포하지 않음)

- Microsoft RDP Client Control (`mstscax.dll`) — Windows 기본 포함 구성요소를 호출만 함
- Microsoft Edge WebView2 Runtime — Windows 기본 포함
- Segoe Fluent Icons / Segoe MDL2 Assets — Windows 시스템 글꼴을 이름으로 참조만 함 (글꼴 파일 미포함)

## 터미널 색 테마

색상표 값은 아래 공개 색 구성을 참고했습니다.

| 테마 | 출처 | 라이선스 |
|---|---|---|
| Campbell | Windows Terminal (microsoft/terminal) | MIT |
| Dracula | draculatheme.com | MIT |
| One Dark | Atom One Dark | MIT |
| Nord | nordtheme.com (arcticicestudio) | MIT |
| Gruvbox Dark | morhetz/gruvbox | MIT |
| Monokai | Wimer Hazenberg 의 Monokai 색 구성 | 색상값 참고 |
| Tomorrow Night | chriskempson/tomorrow-theme | MIT |
| Solarized Dark / Light | Ethan Schoonover (altercation/solarized) | MIT |
| GitHub Light | GitHub Primer primitives | MIT |