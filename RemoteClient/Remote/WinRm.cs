using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Nubis;

/// <summary>
/// WinRM(WS-Management) 원격 명령 실행. 5986(HTTPS) 우선, 실패하면 5985(HTTP + NTLM 메시지 암호화)로 재시도.
/// PowerShell Invoke-Command 와 달리 로컬 TrustedHosts 설정이 필요 없다.
/// </summary>
public static class WinRm
{
    public const int HttpsPort = 5986;
    public const int HttpPort = 5985;
    private const int MaxOutput = 8000;

    private static readonly XNamespace S = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Rsp = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell";
    private static readonly XNamespace Wsman = "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd";
    private const string ShellUri = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd";
    private const string DoneState = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Done";

    /// <summary>여러 Windows 서버에 병렬 실행</summary>
    public static async Task<List<ExecResult>> RunAsync(IEnumerable<HostEntry> hosts, string command, bool powershell, int timeoutSec, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(10);
        var tasks = hosts.Select(async h =>
        {
            await gate.WaitAsync(ct);
            try { return await RunOneAsync(h, command, powershell, timeoutSec, ct); }
            finally { gate.Release(); }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    public static async Task<ExecResult> RunOneAsync(HostEntry h, string command, bool powershell, int timeoutSec, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(h.Username) || string.IsNullOrEmpty(h.Password))
            return new ExecResult(h, false, null, "", "저장된 계정 정보가 없습니다 (호스트 또는 클라우드 프로필 설정에 계정 저장 필요)", sw.ElapsedMilliseconds);

        var cred = ToCredential(h.Username, h.Password);
        var errors = new List<string>();
        // 시도 순서: 사설 IP 5986 → 사설 5985 → 공인 IP 5986 → 공인 5985
        foreach (var addr in h.AddressCandidates())
        foreach (var https in new[] { true, false })
        {
            var where = $"{addr}:{(https ? HttpsPort : HttpPort)}";
            Transport? t = null;
            var shellCreated = false;
            try
            {
                t = https ? new HttpsTransport(addr, cred) : new HttpEncryptedTransport(addr, cred);
                await t.ConnectAsync(ct);
                var (code, stdout, stderr) = await ExecuteAsync(t, command, powershell, timeoutSec, () => shellCreated = true, ct);
                var note = errors.Count > 0 ? $"[{where} 로 실행 — 앞선 시도 실패: {string.Join(" / ", errors)}]\n" : "";
                return new ExecResult(h, code == 0, code, Trim(stdout), Trim(note + stderr).Trim(), sw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (StopRetrying(ex, shellCreated, ct))
            {
                // 다른 포트·주소로 넘어가지 않는 오류:
                // - 인증서 지문 불일치: 5985(HTTP+NTLM)로 넘어가면 중간자가 NTLM 교환을 받게 된다
                // - 인증 실패: 로그온 실패가 쌓여 계정 잠금 정책에 걸리는 것 방지
                // - WS-Man 오류(Access denied 등)·셸 생성 이후 오류: 재시도해도 같거나 중복 실행 위험
                // - 사용자 취소
                var msg = ct.IsCancellationRequested ? "취소되었습니다." : Describe(ex);
                var note = errors.Count > 0 ? $" [앞선 시도 실패: {string.Join(" / ", errors)}]" : "";
                return new ExecResult(h, false, null, "", $"{where}: {msg}{note}", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                // 연결·TLS·셸 생성 실패 → 다음 포트, 그다음 공인 IP 로
                errors.Add($"{where} {Describe(ex)}");
            }
            finally
            {
                t?.Dispose();
            }
        }
        return new ExecResult(h, false, null, "", "WinRM 연결 실패 — " + string.Join(" / ", errors), sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// 이 오류로 다른 포트·주소 재시도를 멈춰야 하는지.
    /// 연결·TLS 실패(인증서 불일치 제외)만 다음 후보로 넘어간다.
    /// </summary>
    internal static bool StopRetrying(Exception ex, bool shellCreated, CancellationToken ct) =>
        shellCreated || ct.IsCancellationRequested || ex is WinRmFatalException;   // Cert·Auth·Fault 는 모두 WinRmFatalException

    /// <summary>URL 에 넣을 호스트: IPv6 리터럴은 [ ] 로 감싼다 (영역 ID "%12" 는 .NET Uri 가 그대로 받아들임)</summary>
    internal static string UrlHost(string host) =>
        !host.Contains(':') || host.StartsWith('[') ? host : "[" + host + "]";

    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        if (ex is TaskCanceledException or OperationCanceledException) return "응답 없음(시간 초과)";
        return ex is WinRmFatalException ? ex.Message : CloudProviders_FirstLine(inner.Message);
    }

    private static string CloudProviders_FirstLine(string s) => (s ?? "").Split('\n')[0].Trim();

    internal static NetworkCredential ToCredential(string user, string password)
    {
        var slash = user.IndexOf('\\');
        if (slash > 0) return new NetworkCredential(user[(slash + 1)..], password, user[..slash]);
        var at = user.IndexOf('@');
        if (at > 0) return new NetworkCredential(user[..at], password, user[(at + 1)..]);
        return new NetworkCredential(user, password, "");
    }

    private static string Trim(string s) =>
        SshExec.ClipOutput(s, MaxOutput);

    // ------------------------------------------------------------------
    // WS-Management 셸: Create → Command → Receive(반복) → Signal → Delete
    // ------------------------------------------------------------------
    private static async Task<(int code, string stdout, string stderr)> ExecuteAsync(
        Transport t, string command, bool powershell, int timeoutSec, Action onShellCreated, CancellationToken ct)
    {
        var (exe, arguments, skipCmdShell) = BuildCommand(command, powershell);

        // 1) 셸 생성
        var created = await t.SendAsync(Envelope(t.Url, "http://schemas.xmlsoap.org/ws/2004/09/transfer/Create", null,
            "<w:OptionSet><w:Option Name=\"WINRS_NOPROFILE\">TRUE</w:Option><w:Option Name=\"WINRS_CODEPAGE\">65001</w:Option></w:OptionSet>",
            "<rsp:Shell><rsp:InputStreams>stdin</rsp:InputStreams><rsp:OutputStreams>stdout stderr</rsp:OutputStreams></rsp:Shell>"), ct);
        var shellId = created.Descendants(Rsp + "ShellId").FirstOrDefault()?.Value
                      ?? created.Descendants(Wsman + "Selector").FirstOrDefault(e => (string?)e.Attribute("Name") == "ShellId")?.Value
                      ?? throw new WinRmFatalException("셸 생성 응답에 ShellId 가 없습니다.");
        onShellCreated();
        try
        {
            // 2) 명령 실행
            var cmdResp = await t.SendAsync(Envelope(t.Url, "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command", shellId,
                "<w:OptionSet><w:Option Name=\"WINRS_CONSOLEMODE_STDIN\">TRUE</w:Option>" +
                $"<w:Option Name=\"WINRS_SKIP_CMD_SHELL\">{(skipCmdShell ? "TRUE" : "FALSE")}</w:Option></w:OptionSet>",
                $"<rsp:CommandLine><rsp:Command>{Xml(exe)}</rsp:Command>" +
                (arguments != null ? $"<rsp:Arguments>{Xml(arguments)}</rsp:Arguments>" : "") +
                "</rsp:CommandLine>"), ct);
            var commandId = cmdResp.Descendants(Rsp + "CommandId").FirstOrDefault()?.Value
                            ?? throw new WinRmFatalException("명령 실행 응답에 CommandId 가 없습니다.");

            // 3) 출력 수신 (Done 이 올 때까지)
            var stdout = new List<byte>();
            var stderr = new List<byte>();
            int? exitCode = null;
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            while (exitCode == null)
            {
                if (DateTime.UtcNow > deadline)
                    throw new WinRmFatalException($"{timeoutSec}초 안에 끝나지 않아 중단했습니다.\n{Encoding.UTF8.GetString(stdout.ToArray())}");
                XDocument recv;
                try
                {
                    recv = await t.SendAsync(Envelope(t.Url, "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive", shellId, null,
                        $"<rsp:Receive><rsp:DesiredStream CommandId=\"{commandId}\">stdout stderr</rsp:DesiredStream></rsp:Receive>"), ct);
                }
                catch (WinRmFaultException f) when (f.IsOperationTimeout) { continue; }   // 아직 출력 없음

                foreach (var s in recv.Descendants(Rsp + "Stream"))
                {
                    if (string.IsNullOrEmpty(s.Value)) continue;
                    var bytes = Convert.FromBase64String(s.Value);
                    ((string?)s.Attribute("Name") == "stderr" ? stderr : stdout).AddRange(bytes);
                }
                var state = recv.Descendants(Rsp + "CommandState").FirstOrDefault();
                if (state != null && (string?)state.Attribute("State") == DoneState)
                    exitCode = int.TryParse(state.Element(Rsp + "ExitCode")?.Value, out var c) ? c : -1;   // 종료 코드 없음 = 알 수 없음(실패 취급)
            }

            return (exitCode.Value, Encoding.UTF8.GetString(stdout.ToArray()), CleanClixml(Encoding.UTF8.GetString(stderr.ToArray())));
        }
        finally
        {
            // 4) 셸 삭제 (실패해도 무시 — 서버가 유휴 시간 후 정리)
            try
            {
                await t.SendAsync(Envelope(t.Url, "http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete", shellId, null, ""), CancellationToken.None);
            }
            catch { }
        }
    }

    /// <summary>CreateProcess 명령줄 최대 길이(32767)에서 실행 파일 이름 등 여유를 뺀 값</summary>
    internal const int MaxProcessCommandLine = 32000;
    /// <summary>cmd.exe 명령줄 최대 길이</summary>
    internal const int MaxCmdCommandLine = 8191;

    /// <summary>
    /// 실행할 명령 (실행 파일, 인수, WINRS_SKIP_CMD_SHELL).
    /// PowerShell 은 cmd.exe 를 거치지 않고 powershell.exe 를 바로 실행한다 — cmd.exe 를 거치면 명령줄이 8191 자로 제한되는데,
    /// -EncodedCommand(UTF-16 base64)는 스크립트 길이의 약 2.7배라 3000 자 정도의 스크립트도 넘친다.
    /// 일반 명령(cmd 모드)은 기존대로 cmd.exe 로 실행(파이프·리디렉션·내장 명령 사용).
    /// 너무 길면 서버에 보내기 전에 WinRmFatalException.
    /// </summary>
    internal static (string Exe, string? Arguments, bool SkipCmdShell) BuildCommand(string command, bool powershell)
    {
        if (!powershell)
        {
            if (command.Length > MaxCmdCommandLine)
                throw new WinRmFatalException($"명령이 너무 깁니다 ({command.Length}자, cmd.exe 한도 {MaxCmdCommandLine}자). PowerShell 모드를 쓰거나 명령을 나누세요.");
            return (command, null, false);
        }
        var script = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;\n" + command;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";
        if (args.Length > MaxProcessCommandLine)
            throw new WinRmFatalException($"스크립트가 너무 깁니다 (인코딩 후 {args.Length}자, 한도 약 {MaxProcessCommandLine}자). 스크립트를 나누거나 서버에 파일로 올려 실행하세요.");
        return ("powershell.exe", args, true);
    }

    private static string Xml(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    internal static string Envelope(string url, string action, string? shellId, string? options, string body) =>
        "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" " +
        "xmlns:a=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
        "xmlns:w=\"http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd\" " +
        "xmlns:p=\"http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd\" " +
        "xmlns:rsp=\"http://schemas.microsoft.com/wbem/wsman/1/windows/shell\">" +
        "<s:Header>" +
        $"<a:To>{url}</a:To>" +
        "<a:ReplyTo><a:Address s:mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address></a:ReplyTo>" +
        $"<w:ResourceURI s:mustUnderstand=\"true\">{ShellUri}</w:ResourceURI>" +
        $"<a:Action s:mustUnderstand=\"true\">{action}</a:Action>" +
        "<w:MaxEnvelopeSize s:mustUnderstand=\"true\">512000</w:MaxEnvelopeSize>" +
        $"<a:MessageID>uuid:{Guid.NewGuid().ToString().ToUpperInvariant()}</a:MessageID>" +
        "<w:Locale xml:lang=\"en-US\" s:mustUnderstand=\"false\"/>" +
        "<p:DataLocale xml:lang=\"en-US\" s:mustUnderstand=\"false\"/>" +
        "<w:OperationTimeout>PT20S</w:OperationTimeout>" +
        (shellId != null ? $"<w:SelectorSet><w:Selector Name=\"ShellId\">{shellId}</w:Selector></w:SelectorSet>" : "") +
        (options ?? "") +
        "</s:Header>" +
        $"<s:Body>{body}</s:Body></s:Envelope>";

    /// <summary>PowerShell 이 stderr 로 내보내는 CLIXML 을 사람이 읽을 수 있는 텍스트로</summary>
    internal static string CleanClixml(string s)
    {
        if (!s.StartsWith("#< CLIXML")) return s;
        var lines = Regex.Matches(s, "<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value.Replace("_x000D__x000A_", "\n").Replace("_x000A_", "\n"))
            .Select(WebUtility.HtmlDecode);
        var text = string.Concat(lines).Trim();
        return text.Length > 0 ? text : "";
    }

    // ------------------------------------------------------------------
    // 전송 계층
    // ------------------------------------------------------------------
    private abstract class Transport : IDisposable
    {
        public abstract string Url { get; }
        public abstract Task ConnectAsync(CancellationToken ct);
        protected abstract Task<(HttpStatusCode code, string body)> PostAsync(string soap, CancellationToken ct);

        public async Task<XDocument> SendAsync(string soap, CancellationToken ct)
        {
            var (code, body) = await PostAsync(soap, ct);
            if (code == HttpStatusCode.Unauthorized) throw new WinRmAuthException("인증 실패 (401) — 계정/비밀번호 또는 WinRM 인증 설정 확인");
            XDocument doc;
            try { doc = XDocument.Parse(body); }
            // WinRM 이 아닌 응답(다른 서비스·프록시 오류 페이지 등)은 연결 실패처럼 다음 포트로 넘어갈 수 있게 Fatal 이 아닌 예외로
            catch { throw new InvalidOperationException($"HTTP {(int)code}: 응답을 해석할 수 없습니다. {CloudProviders_FirstLine(body)}"); }
            var fault = doc.Descendants(S + "Fault").FirstOrDefault();
            if (fault != null) throw new WinRmFaultException(fault);
            return doc;
        }

        public abstract void Dispose();
    }

    /// <summary>5986: TLS 가 암호화를 맡으므로 HttpClient 의 NTLM/Negotiate 인증을 그대로 사용</summary>
    private sealed class HttpsTransport : Transport
    {
        private readonly HttpClient _http;
        private readonly string _host;
        /// <summary>저장된 지문과 다른 인증서를 받았으면 (저장된 지문, 받은 지문)</summary>
        private (string Known, string Got)? _certMismatch;
        public override string Url { get; }

        public HttpsTransport(string host, NetworkCredential cred)
        {
            _host = host;
            Url = $"https://{UrlHost(host)}:{HttpsPort}/wsman";
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                Credentials = new CredentialCache { { new Uri(Url), "Negotiate", cred }, { new Uri(Url), "NTLM", cred } },
                PreAuthenticate = true,
                MaxConnectionsPerServer = 1,
                // WinRM HTTPS 리스너는 자체 서명 인증서가 일반적이라 CA 검증 대신 SSH 처럼 지문을 고정한다:
                // 처음 본 인증서는 저장(accept-new), 이후 바뀌면 거부 (중간자의 NTLM 릴레이 방지)
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                        errors == SslPolicyErrors.None || (cert != null && PinCertificate(host, cert)),
                },
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        }

        private bool PinCertificate(string host, System.Security.Cryptography.X509Certificates.X509Certificate cert)
        {
            var id = $"winrm:{host}:{HttpsPort}";
            var fp = "x509 SHA256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData()));
            var known = KnownHostStore.Get(id);
            if (known == null) { KnownHostStore.Set(id, fp); return true; }
            if (known == fp) return true;
            // 바뀌었으면(또는 사용자가 거부한 키면) 거부 → PostAsync 에서 WinRmCertException 으로 바꿔 5985 로 넘어가지 않게 한다
            _certMismatch = (known, fp);
            return false;
        }

        public override async Task ConnectAsync(CancellationToken ct)
        {
            // 연결/TLS/인증 확인: 빈 Identify 요청 대신 셸 생성에서 확인되므로 여기서는 TCP 만 빠르게 확인
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(_host, HttpsPort, cts.Token);
        }

        protected override async Task<(HttpStatusCode, string)> PostAsync(string soap, CancellationToken ct)
        {
            using var content = new StringContent(soap, Encoding.UTF8);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/soap+xml;charset=UTF-8");
            HttpResponseMessage resp;
            try { resp = await _http.PostAsync(Url, content, ct); }
            catch (HttpRequestException) when (_certMismatch is { } m)
            {
                throw new WinRmCertException(
                    $"WinRM HTTPS 인증서가 저장된 것과 다릅니다 — 중간자 공격일 수 있어 연결을 중단했습니다 (HTTP 5985 로도 넘어가지 않음). " +
                    $"저장됨 {m.Known} / 받은 값 {m.Got}. 서버 인증서를 새로 만든 것이 확실하면 설정의 known_hosts 에서 'winrm:{_host}:{HttpsPort}' 항목을 지운 뒤 다시 시도하세요.");
            }
            using (resp)
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }

        public override void Dispose() => _http.Dispose();
    }

    /// <summary>
    /// 5985: HTTP 이므로 WinRM 이 요구하는 메시지 암호화를 직접 수행.
    /// NTLM 핸드셰이크 후 각 SOAP 메시지를 NegotiateAuthentication.Wrap 으로 암호화해 multipart/encrypted 로 전송.
    /// NTLM 은 연결 단위 인증이라 HTTP 연결 하나를 계속 재사용한다.
    /// </summary>
    private sealed class HttpEncryptedTransport : Transport
    {
        private const string Boundary = "Encrypted Boundary";
        private const string Protocol = "application/HTTP-SPNEGO-session-encrypted";
        private readonly HttpClient _http;
        private readonly NegotiateAuthentication _auth;
        public override string Url { get; }

        public HttpEncryptedTransport(string host, NetworkCredential cred)
        {
            Url = $"http://{UrlHost(host)}:{HttpPort}/wsman";
            _http = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                MaxConnectionsPerServer = 1,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                UseCookies = false,
            }) { Timeout = TimeSpan.FromSeconds(60) };
            _auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
            {
                Package = "NTLM",
                Credential = cred,
                TargetName = $"HTTP/{host}",
                RequiredProtectionLevel = ProtectionLevel.EncryptAndSign,
            });
        }

        public override async Task ConnectAsync(CancellationToken ct)
        {
            // 1) NEGOTIATE → 401 + CHALLENGE
            var blob = _auth.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out var status);
            if (blob == null || status is not (NegotiateAuthenticationStatusCode.ContinueNeeded or NegotiateAuthenticationStatusCode.Completed))
                throw new AuthenticationException($"NTLM 초기화 실패: {status}");
            using var r1 = await PostRawAsync(null, "Negotiate " + Convert.ToBase64String(blob), ct);
            var challengeHeader = r1.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme is "Negotiate" or "NTLM")?.Parameter;
            if (r1.StatusCode != HttpStatusCode.Unauthorized || string.IsNullOrEmpty(challengeHeader))
                throw new AuthenticationException($"NTLM 챌린지를 받지 못했습니다 (HTTP {(int)r1.StatusCode})");

            // 2) AUTHENTICATE (빈 본문) → 인증 완료
            blob = _auth.GetOutgoingBlob(Convert.FromBase64String(challengeHeader), out status);
            if (blob == null || status != NegotiateAuthenticationStatusCode.Completed)
                throw new AuthenticationException($"NTLM 인증 생성 실패: {status}");
            using var r2 = await PostRawAsync(null, "Negotiate " + Convert.ToBase64String(blob), ct);
            if (r2.StatusCode == HttpStatusCode.Unauthorized)
                throw new WinRmAuthException("인증 실패 (401) — 계정/비밀번호 확인");
        }

        private async Task<HttpResponseMessage> PostRawAsync(HttpContent? content, string? authorization, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = content ?? new ByteArrayContent(Array.Empty<byte>()) };
            if (authorization != null) req.Headers.TryAddWithoutValidation("Authorization", authorization);
            if (content == null) req.Content.Headers.ContentLength = 0;
            return await _http.SendAsync(req, ct);
        }

        protected override async Task<(HttpStatusCode, string)> PostAsync(string soap, CancellationToken ct)
        {
            var plain = Encoding.UTF8.GetBytes(soap);
            var wrapped = new ArrayBufferWriter<byte>();
            if (_auth.Wrap(plain, wrapped, true, out var encrypted) != NegotiateAuthenticationStatusCode.Completed || !encrypted)
                throw new WinRmFatalException("메시지 암호화 실패");

            // NTLM Wrap 결과 = 서명(16바이트) + 암호문. WinRM 형식은 [서명 길이(LE 4바이트)][서명][암호문]
            const int sigLen = 16;
            var body = new List<byte>();
            body.AddRange(Encoding.ASCII.GetBytes(
                $"--{Boundary}\r\n\tContent-Type: {Protocol}\r\n\tOriginalContent: type=application/soap+xml;charset=UTF-8;Length={plain.Length}\r\n" +
                $"--{Boundary}\r\n\tContent-Type: application/octet-stream\r\n"));
            var len = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, sigLen);
            body.AddRange(len);
            body.AddRange(wrapped.WrittenSpan.ToArray());
            body.AddRange(Encoding.ASCII.GetBytes($"--{Boundary}--\r\n"));

            var content = new ByteArrayContent(body.ToArray());
            content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/encrypted;protocol=\"{Protocol}\";boundary=\"{Boundary}\"");
            using var resp = await PostRawAsync(content, null, ct);
            var raw = await resp.Content.ReadAsByteArrayAsync(ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) return (resp.StatusCode, "");
            return (resp.StatusCode, Decrypt(raw));
        }

        private string Decrypt(byte[] raw)
        {
            // 암호화되지 않은 응답(오류 페이지 등)은 그대로
            var marker = Encoding.ASCII.GetBytes("application/octet-stream\r\n");
            var idx = IndexOf(raw, marker);
            if (idx < 0) return Encoding.UTF8.GetString(raw);
            var start = idx + marker.Length;
            var end = IndexOf(raw, Encoding.ASCII.GetBytes($"--{Boundary}--"), start);
            if (end < 0) end = raw.Length;
            var payload = raw.AsSpan(start, end - start);
            // [서명 길이(4)][서명][암호문] → NTLM Unwrap 입력은 서명+암호문
            var token = payload[4..];
            var plain = new ArrayBufferWriter<byte>();
            if (_auth.Unwrap(token, plain, out _) != NegotiateAuthenticationStatusCode.Completed)
                throw new WinRmFatalException("응답 복호화 실패");
            return Encoding.UTF8.GetString(plain.WrittenSpan);
        }

        private static int IndexOf(byte[] hay, byte[] needle, int from = 0)
        {
            for (var i = from; i <= hay.Length - needle.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length && ok; j++) ok = hay[i + j] == needle[j];
                if (ok) return i;
            }
            return -1;
        }

        public override void Dispose()
        {
            _auth.Dispose();
            _http.Dispose();
        }
    }
}

/// <summary>인증 이후 단계의 오류 — 다른 포트로 재시도해도 의미가 없음</summary>
public class WinRmFatalException(string message) : Exception(message);

/// <summary>인증 실패 (401) — 계정 잠금 방지를 위해 다른 포트로 재시도하지 않음</summary>
public sealed class WinRmAuthException(string message) : WinRmFatalException(message);

/// <summary>HTTPS 인증서 지문이 저장된 것과 다름 — 중간자에게 NTLM 을 넘기지 않도록 5985·다른 주소로 재시도하지 않음</summary>
public sealed class WinRmCertException(string message) : WinRmFatalException(message);

public sealed class WinRmFaultException : WinRmFatalException
{
    public bool IsOperationTimeout { get; }

    public WinRmFaultException(XElement fault) : base(Describe(fault))
    {
        // w:TimedOut / 2150858793 = 수신 대기 시간 초과 (출력이 아직 없음) — 다시 Receive 하면 됨
        IsOperationTimeout = fault.ToString().Contains("TimedOut") || fault.ToString().Contains("2150858793");
    }

    private static string Describe(XElement fault)
    {
        var text = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value
                   ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value
                   ?? fault.Value;
        return "WinRM 오류: " + text.Trim();
    }
}
