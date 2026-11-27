# Third-Party Notices

Nubis 는 아래 오픈소스 구성요소를 포함하거나 사용합니다.

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

수정본(`2026.0.0.1-nubis`)을 사용함: SFTP 를 "sftp" 서브시스템 대신 지정한 명령(sudo sftp-server 등)으로 여는 옵션 추가.
변경 내용은 `third_party/SSH.NET/nubis-sftp-exec.patch`, 빌드는 `tools/build-sshnet.ps1`.
SSH.NET 의존성인 BouncyCastle.Cryptography 도 함께 배포됩니다 (아래 "그 밖의 의존성" 표).

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
| AWSSDK.EKS 4.0.108.3 (EKS 클러스터 조회) | Apache-2.0 | https://github.com/aws/aws-sdk-net |
| Azure.Identity, Azure.Core | MIT | https://github.com/Azure/azure-sdk-for-net |
| System.ClientModel (Azure.Core 의존성) | MIT | https://github.com/Azure/azure-sdk-for-net |
| Microsoft.Identity.Client, Microsoft.Identity.Client.Extensions.Msal (MSAL, Azure.Identity 의존성) | MIT | https://github.com/AzureAD/microsoft-authentication-library-for-dotnet |
| Microsoft.IdentityModel.Abstractions (MSAL 의존성) | MIT | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Google.Apis.Auth, Google.Apis, Google.Apis.Core | Apache-2.0 | https://github.com/googleapis/google-api-dotnet-client |
| Newtonsoft.Json (Google.Apis 의존성) | MIT | https://github.com/JamesNK/Newtonsoft.Json |

Apache-2.0 전문: https://www.apache.org/licenses/LICENSE-2.0

Tencent Cloud API 는 SDK 없이 공개된 TC3-HMAC-SHA256 서명 규격으로 직접 호출합니다.
OCI(Oracle Cloud) API 도 SDK 없이 공개된 HTTP Signature(rsa-sha256) 규격으로 직접 서명해 호출합니다.

## 호스트 가져오기

| 패키지 | 라이선스 | 출처 |
|---|---|---|
| YamlDotNet (Ansible YAML 인벤토리 읽기) | MIT | https://github.com/aaubry/YamlDotNet — Copyright (c) Antoine Aubry and contributors |
| System.DirectoryServices.Protocols (Active Directory · Entra Domain Services LDAP 조회) | MIT | https://github.com/dotnet/runtime — Copyright (c) .NET Foundation and Contributors |

## 그 밖의 의존성

| 패키지 | 라이선스 | 출처 |
|---|---|---|
| BouncyCastle.Cryptography (SSH.NET 의존성) | MIT | https://www.bouncycastle.org — Copyright (c) Legion of the Bouncy Castle Inc. |
| Microsoft.Extensions.* Abstractions · Options · Primitives, Microsoft.Bcl.AsyncInterfaces, System.Memory.Data, System.Management (SSH.NET · Azure · Google 라이브러리 의존성) | MIT | https://github.com/dotnet/runtime — Copyright (c) .NET Foundation and Contributors |
| .NET 런타임 · WPF · Windows Forms (자체 포함 배포에 함께 들어감) | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf, https://github.com/dotnet/winforms — Copyright (c) .NET Foundation and Contributors |

## 서버 OS 로고

호스트 카드 · 세션 탭 · 명령 팔레트의 서버 OS 로고(`RemoteClient/Core/BrandIcons.Os.cs`)는 Simple Icons 16.30.0 의 SVG 경로 데이터를 씁니다.

| 항목 | 라이선스 | 출처 |
|---|---|---|
| Simple Icons 경로 데이터 (Ubuntu · Debian · Red Hat · CentOS · Rocky Linux · AlmaLinux · Fedora · SUSE · openSUSE · Arch Linux · Alpine Linux · Linux(Tux)) | CC0-1.0 | https://github.com/simple-icons/simple-icons |

- 각 로고는 해당 상표권자의 상표이며, Nubis 는 서버의 OS 를 알아보는 용도로만 작게 표시합니다 (제휴·보증을 뜻하지 않음).
- Simple Icons 가 따로 밝힌 로고 라이선스: Debian Open Use Logo — CC-BY-SA-3.0 (Software in the Public Interest, Inc., https://www.debian.org/logos/), Rocky Linux — CC-BY-SA-4.0 (Rocky Enterprise Software Foundation). 그 밖의 로고는 각 상표 사용 지침을 따릅니다 (Fedora · AlmaLinux · Arch Linux · Ubuntu · SUSE · openSUSE).
- 어두운 테마에서 보이도록 일부 로고 색을 같은 브랜드의 밝은 색으로 바꿨습니다 (모양은 그대로).
- Windows 로고는 네 사각형을 직접 그렸고, Amazon Linux 는 전용 로고 없이 Tux 를 주황으로 칠해 표시합니다.

## 시스템 구성요소 (재배포하지 않음)

- Microsoft RDP Client Control (`mstscax.dll`) — Windows 기본 포함 구성요소를 호출만 함
- Microsoft Edge WebView2 Runtime — Windows 기본 포함
- Segoe Fluent Icons / Segoe MDL2 Assets — Windows 시스템 글꼴을 이름으로 참조만 함 (글꼴 파일 미포함)
- kubectl — 사용자가 설치한 것을 호출만 함 (Kubernetes 페이지·클러스터 탭·Claude 의 `run_kubectl`)
- 클라우드 클러스터 인증 도구 — 사용자가 설치한 것을 kubeconfig 의 exec 플러그인으로 호출만 함: AWS CLI (`aws eks get-token`), gke-gcloud-auth-plugin (GKE), kubelogin (AKS · Microsoft Entra 인증 클러스터), OCI CLI (`oci ce cluster generate-token`)

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